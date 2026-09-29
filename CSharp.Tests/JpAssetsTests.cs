using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class JpAssetsTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    [Fact]
    public void SelectsLiveAndRejectsUnmatchedOrUnsafePaths()
    {
        var raw = $$"""{"version":"fallback","Android":"{{Hash}}","live":[{"minClientVersion":"2.0","version":"new","Android":"{{Hash}}"},{"minClientVersion":"1.0","version":"first","Android":"{{Hash}}"},{"minClientVersion":"1.0.0","version":"second","Android":"{{Hash}}"}]}""";
        Assert.Equal(("first", Hash), AssetService.SelectJpAsset(raw, "1.0.4"));
        Assert.Throws<InvalidDataException>(() => AssetService.SelectJpAsset(raw, "0.1"));
        Assert.Throws<InvalidDataException>(() => AssetService.SelectJpAsset("{\"version\":\"fallback\",\"Android\":\"" + Hash + "\",\"live\":{}}", "1.0"));
        var config = new Config { CdnRoot = "https://static.bang-dream-on.jp" };
        var root = config.CdnRoot + "/asset/1.0/Android/" + Hash;
        var source = new JpAssetSource("jp", "Android", "1.0", Hash, root + "/catalog_main.bin", root, config.JpApiOrigin, "1.0.4");
        source.Validate(config);
        foreach (var name in new[] { "../secret", "%2e%2e/a", "/other", "a?x=1", "a\\b", "a#b", "" })
            Assert.Throws<InvalidDataException>(() => source.AssetUri(JpAssetSource.Placeholder + name));
        Assert.Throws<InvalidDataException>(() => (source with { ApiRoot = "https://untrusted.example" }).Validate(config));
        Assert.Throws<InvalidDataException>(() => (source with { BundleRoot = "https://untrusted.example" }).Validate(config));
    }

    [Fact]
    public async Task AnonymousVersionGzipPlaceholderExportRotationHashUpdateAndRestart()
    {
        using var dir = new TempDirectory();
        var fixture = Fixture.Create();
        var catalog = Fixture.Catalog(fixture.Bundle.Length, ~BinaryTools.Crc32(Fixture.Serialized()), internalId: JpAssetSource.Placeholder + "fixture.bundle");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(catalog);
        var hash = Hash; var password = "synthetic-cdn-first"; var versionCalls = 0; var cdnCalls = 0; var rejects = 0; var rejectedStatus = 401;
        string cdn = "", api = ""; bool publishedPaths = true, staleMetadata = false, wrongOrigin = false;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        JpAssetSource Source() { var root = cdn + "/asset/1.0.0.100/Android/" + hash; return new("jp", "Android", "1.0.0.100", hash, root + "/catalog_main.bin", root, api, "1.0.4"); }
        server.MapGet("/current_version.json", () =>
        {
            var entry = new JsonObject
            {
                ["version"] = "1.0.0.100/" + Hash,
                ["resource_version"] = "1.0.0.100",
                ["resource_hash"] = hash,
                ["resource_version_source"] = "x-asset-version",
                ["client_version"] = "1.0.4",
                ["upstream"] = new JsonObject { ["api_root"] = api, ["cdn_root"] = cdn }
            };
            if (publishedPaths) entry["assets"] = JsonNode.Parse(Json.Write(Source()));
            return Results.Text(new JsonObject { ["regions"] = new JsonObject { ["jp"] = entry } }.ToJsonString(), "application/json");
        });
        server.MapGet("/asset/{version}/Android/{hash}/{file}", (HttpContext context, string file) =>
        {
            Interlocked.Increment(ref cdnCalls);
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("sirius:" + password));
            if (context.Request.Headers.Authorization != expected) { Interlocked.Increment(ref rejects); return Results.StatusCode(rejectedStatus); }
            Assert.Equal("OurNotes/1.0.4", context.Request.Headers.UserAgent.ToString());
            Assert.DoesNotContain(".hash", file);
            return Results.Bytes(file == "catalog_main.bin" ? compressed.ToArray() : fixture.Bundle);
        });
        await server.StartAsync(); cdn = Address(server);
        var apiBuilder = WebApplication.CreateBuilder(); apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        await using var rpc = apiBuilder.Build();
        rpc.MapPost("/app.masterdata.MasterdataService/Version", async (HttpContext context) =>
        {
            Interlocked.Increment(ref versionCalls);
            Assert.Equal("HTTP/2", context.Request.Protocol);
            Assert.Empty(context.Request.Headers.Authorization.ToString());
            Assert.Equal("1.0.4", context.Request.Headers["x-client-version"].ToString());
            Assert.Equal("android", context.Request.Headers["x-platform"].ToString());
            using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body); Assert.Equal(new byte[5], body.ToArray());
            context.Response.ContentType = "application/grpc";
            context.Response.Headers["x-sirius-env"] = wrongOrigin ? "https://untrusted.example" : cdn;
            context.Response.Headers["x-sirius-cred"] = password;
            var advertisedHash = staleMetadata ? new string('c', 32) : hash;
            context.Response.Headers["x-asset-version"] = $$"""{"live":[{"minClientVersion":"1.0.4","version":"1.0.0.100","Android":"{{advertisedHash}}"}]}""";
            context.Response.DeclareTrailer("grpc-status");
            await context.Response.Body.WriteAsync(new byte[] { 0, 0, 0, 0, 3, 10, 1, 118 });
            context.Response.AppendTrailer("grpc-status", "0");
        });
        await rpc.StartAsync(); api = Address(rpc);
        var config = new Config
        {
            DataDir = dir.Path,
            Region = "jp",
            Locale = "ja",
            Locales = ["ja"],
            CdnRoot = cdn,
            VersionUrl = cdn + "/current_version.json",
            AllowLoopbackHttp = true,
            JpApiOrigin = api,
            JpCdnOrigin = cdn
        };
        string firstSnapshot;
        await using (var service = new AssetService(config))
        {
            var check = await service.CheckVersions(); Assert.Equal("queued", check.Regions[0].Action);
            var release = await service.WaitRelease(check.Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("succeeded", release.State); Assert.Equal(1, Assert.Single(release.Locales).Exported);
            firstSnapshot = release.Locales[0].Snapshot!;
            Assert.Equal(1, versionCalls); Assert.Equal(2, cdnCalls);
            Assert.Equal(1, service.ListCatalogs()[0].RemoteBundles);
            Assert.Equal("current", (await service.CheckVersions()).Regions[0].Action);
            password = "synthetic-cdn-second";
            Assert.Equal("succeeded", (await service.Wait(service.StartRefresh().Id)).State);
            Assert.Equal(2, versionCalls); Assert.Equal(1, rejects);
            // A hash-only revision must get a different release, snapshot and public directory.
            hash = new string('b', 32); publishedPaths = false; // compatibility with the preceding metadata service
            var second = await service.CheckVersions();
            Assert.NotEqual(check.Regions[0].Release, second.Regions[0].Release);
            // A new publication obtains a fresh anonymous observation immediately.
            var updated = await service.WaitRelease(second.Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("succeeded", updated.State);
        }
        await using (var service = new AssetService(config))
        {
            var check = await service.CheckVersions(true);
            var release = await service.WaitRelease(check.Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("succeeded", release.State); Assert.NotEqual(firstSnapshot, release.Locales[0].Snapshot);
            Assert.True(File.Exists(Path.Combine(dir.Path, "versions", "jp", "1.0.0.100-" + Hash, "release.json")));
            Assert.True(File.Exists(Path.Combine(dir.Path, "versions", "jp", "1.0.0.100-" + hash, "release.json")));
            Assert.Equal("succeeded", (await service.Wait(service.StartRefresh().Id)).State);
            // 429 is not retried or treated as a password rotation.
            password = "synthetic-cdn-third"; rejectedStatus = 429; var calls = versionCalls;
            Assert.Equal("failed", (await service.Wait(service.StartRefresh().Id)).State);
            Assert.Equal(calls, versionCalls);
            // The next 401 obtains a new observation, but cannot move a fixed catalog to another generation.
            rejectedStatus = 401; staleMetadata = true; var before = cdnCalls;
            var stale = await service.Wait(service.StartRefresh().Id);
            Assert.Equal("failed", stale.State); Assert.Contains("metadata changed", stale.Error);
            Assert.Equal(before + 1, cdnCalls); Assert.Equal(calls + 1, versionCalls);
        }
        wrongOrigin = true; staleMetadata = false;
        await using (var service = new AssetService(config))
        {
            var before = cdnCalls;
            var failed = await service.Wait(service.StartRefresh().Id);
            Assert.Equal("failed", failed.State); Assert.Contains("Unapproved JP CDN", failed.Error);
            Assert.Equal(before, cdnCalls);
        }
        foreach (var path in Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var secret in new[] { "synthetic-cdn-first", "synthetic-cdn-second", "synthetic-cdn-third" })
            {
                Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0);
                var encoded = Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("sirius:" + secret)));
                Assert.False(bytes.AsSpan().IndexOf(encoded) >= 0);
            }
        }
        await rpc.StopAsync(); await server.StopAsync();
    }
}
