using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class HttpTests
{
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    [Fact]
    public async Task ExportDownloadRangeReuseAndRestart()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create(); var downloads = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); await using var cdn = builder.Build();
        cdn.MapGet("/prod/asset/Android/catalog_main_zh-Hant.hash", () => "hint");
        cdn.MapGet("/prod/asset/Android/catalog_main_zh-Hant.bin", () => Results.Bytes(fixture.Catalog));
        cdn.MapGet("/prod/asset/Android/fixture.bundle", () => { Interlocked.Increment(ref downloads); return Results.Bytes(fixture.Bundle); });
        await cdn.StartAsync(); var config = new Config { DataDir = dir.Path, CdnRoot = Address(cdn) + "/prod", AllowLoopbackHttp = true };
        string exportId, fileId;
        await using (var service = new AssetService(config))
        {
            await using var app = Api.Build(service, "http://127.0.0.1:0", apiKey: "integration-test-key"); await app.StartAsync(); using var http = new HttpClient { BaseAddress = new Uri(Address(app)) }; http.DefaultRequestHeaders.Authorization = new("Bearer", "integration-test-key");
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/files/unknown")).StatusCode); Assert.Equal(0, downloads);
            var refresh = await http.PostAsync("/catalog/refresh", null); Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
            var task = Json.Read<TaskInfo>(await refresh.Content.ReadAsStringAsync()); var refreshed = await service.Wait(task.Id); Assert.Equal("succeeded", refreshed.State);
            var assets = await http.GetStringAsync("/assets"); Assert.Contains(Fixture.Key, assets);
            var pathUrl = $"/zh-Hant/{Fixture.Key}/fixture.json";
            // The miss is cached and publicly cacheable; publishing must still make the path visible at once.
            var miss = await http.GetAsync(pathUrl); Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode); Assert.Equal(TimeSpan.FromSeconds(60), miss.Headers.CacheControl!.MaxAge);
            var first = service.StartExport(new(Keys: [Fixture.Key])); var second = service.StartExport(new(Keys: [Fixture.Key]));
            var results = await Task.WhenAll(service.Wait(first.Id), service.Wait(second.Id));
            foreach (var result in results) Assert.True(result.State == "succeeded", Json.Write(result));
            exportId = results[0].Results[0].ExportId!; Assert.Equal(exportId, results[1].Results[0].ExportId); Assert.Equal(1, downloads);
            var manifest = service.Manifest(exportId)!; fileId = manifest.Files[0].Id; var response = await http.GetAsync("/files/" + fileId); Assert.Equal(Fixture.Body, await response.Content.ReadAsByteArrayAsync());
            using var range = new HttpRequestMessage(HttpMethod.Get, "/files/" + fileId); range.Headers.Range = new(1, 4); var partial = await http.SendAsync(range); Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode); Assert.Equal(Fixture.Body[1..5], await partial.Content.ReadAsByteArrayAsync());
            using var conditional = new HttpRequestMessage(HttpMethod.Get, "/files/" + fileId); conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!); Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);
            using var head = new HttpRequestMessage(HttpMethod.Head, "/files/" + fileId); var headResponse = await http.SendAsync(head); Assert.Equal(Fixture.Body.Length, headResponse.Content.Headers.ContentLength); Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());
            // Publication links the file into the static tree; the path is served from disk.
            var treeFile = Path.Combine(service.PublicRoot, "zh-Hant", "Live", "MusicScore", "test", "fixture.json");
            Assert.Equal(Fixture.Body, File.ReadAllBytes(treeFile));
            var byPath = await http.GetAsync(pathUrl); Assert.Equal(Fixture.Body, await byPath.Content.ReadAsByteArrayAsync());
            Assert.Equal(TimeSpan.FromSeconds(600), byPath.Headers.CacheControl!.MaxAge); Assert.Equal("application/json", byPath.Content.Headers.ContentType!.MediaType);
            using var pathConditional = new HttpRequestMessage(HttpMethod.Get, pathUrl); pathConditional.Headers.IfNoneMatch.Add(byPath.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(pathConditional)).StatusCode);
            using var pathHead = new HttpRequestMessage(HttpMethod.Head, pathUrl); Assert.Equal(Fixture.Body.Length, (await http.SendAsync(pathHead)).Content.Headers.ContentLength);
            // A lost tree is rebuilt by the backfill, after which misses no longer consult SQLite.
            Directory.Delete(service.PublicRoot, true); Directory.CreateDirectory(service.PublicRoot);
            await service.BackfillPublicTree(); Assert.True(service.PublicTreeReady); Assert.Equal(Fixture.Body, File.ReadAllBytes(treeFile));
            Assert.Equal(Fixture.Body, await http.GetByteArrayAsync(pathUrl));
            var listing = Json.Read<AssetService.PathListing>(await http.GetStringAsync($"/zh-Hant/{Fixture.Key}/"));
            Assert.Equal(pathUrl, Assert.Single(listing.Files).Path); Assert.Equal("/files/" + fileId, listing.Files[0].File);
            foreach (var missing in new[] { $"/ja/{Fixture.Key}/fixture.json", $"/zh-Hant/{Fixture.Key}/other.json", "/zh-Hant/Live/MusicScore/", "/favicon.ico", "/zh-Hant/" })
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(missing)).StatusCode);
            var reused = await service.Wait(service.StartExport(new(Keys: [Fixture.Key])).Id); Assert.Equal("succeeded", reused.State); Assert.Equal(1, downloads);
            var mixed = await service.Wait(service.StartExport(new(Keys: [Fixture.Key, "missing"])).Id); Assert.Equal("partial", mixed.State);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/exports", new { prefix = "", keys = new[] { Fixture.Key } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/exports", new { typo = true })).StatusCode);
            service.Store.Put("task", "interrupted", new TaskInfo("interrupted", "export", "running", refreshed.Snapshot, 1, 0, [], null, 0, 0));
            await app.StopAsync();
        }
        await using (var restarted = new AssetService(config))
        {
            Assert.Equal("failed", restarted.GetTask("interrupted")!.State); Assert.NotNull(restarted.Manifest(exportId)); Assert.NotNull(restarted.LookupFile(fileId));
            Assert.Equal("succeeded", (await restarted.Wait(restarted.StartExport(new(Keys: [Fixture.Key])).Id)).State); Assert.Equal(1, downloads);
            Assert.Throws<IOException>(() => new AssetService(config));
        }
        var cliConfig = Path.Combine(dir.Path, "cli.toml");
        await File.WriteAllTextAsync(cliConfig, $"cdn_root = '{config.CdnRoot}'\ndata_dir = '{dir.Path}'\nallow_loopback_http = true\n");
        var assembly = typeof(AssetService).Assembly.Location;
        Assert.Equal("succeeded", Json.Read<TaskInfo>(await Processes.Run("dotnet", [assembly, "refresh", cliConfig], CancellationToken.None)).State);
        Assert.Contains(Fixture.Key, await Processes.Run("dotnet", [assembly, "list", cliConfig, "Live/"], CancellationToken.None));
        Assert.Equal(exportId, Json.Read<TaskInfo>(await Processes.Run("dotnet", [assembly, "export", cliConfig, Fixture.Key], CancellationToken.None)).Results[0].ExportId);
        Assert.Equal(1, downloads);
        await cdn.StopAsync();
    }
    [Fact]
    public async Task RegionPathsServeEachRegionBesideDefaultLocalePaths()
    {
        using var dir = new TempDirectory();
        var fixtures = new Dictionary<string, (byte[] Catalog, byte[] Bundle)> { ["tw"] = Fixture.Create(), ["en"] = Fixture.Create("{\"fixture\":2026}"u8.ToArray()) };
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); await using var cdn = builder.Build();
        cdn.MapGet("/{root}/asset/Android/{file}", (string root, string file) => file.EndsWith(".hash") ? Results.Text("hash")
            : Results.Bytes(file.EndsWith(".bin") ? fixtures[root].Catalog : fixtures[root].Bundle));
        await cdn.StartAsync(); var address = Address(cdn);
        // The en server's ID equals a locale: /en/{key} is the default region's en, /en/en/{key} the en server's.
        var config = new Config
        {
            DataDir = dir.Path,
            AllowLoopbackHttp = true,
            Region = "tw",
            Regions = [new("tw", address + "/tw", "en", ["zh-Hant", "en"]), new("en", address + "/en", "en", ["en"])]
        };
        await using var service = new AssetService(config);
        await using var app = Api.Build(service, "http://127.0.0.1:0", apiKey: "integration-test-key"); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(Address(app)) };
        foreach (var region in new[] { "tw", "en" })
        {
            Assert.Equal("succeeded", (await service.Wait(service.StartRefresh(region, "en").Id)).State);
            Assert.Equal("succeeded", (await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Region: region, Locale: "en")).Id)).State);
        }
        var (tw, en) = (Fixture.Body, "{\"fixture\":2026}"u8.ToArray());
        foreach (var (url, body) in new[] { ($"/en/{Fixture.Key}/fixture.json", tw), ($"/tw/en/{Fixture.Key}/fixture.json", tw), ($"/en/en/{Fixture.Key}/fixture.json", en) })
        {
            var response = await http.GetAsync(url); Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(TimeSpan.FromSeconds(600), response.Headers.CacheControl!.MaxAge);
        }
        Assert.Equal(en, File.ReadAllBytes(Path.Combine(service.RegionsRoot, "en", "en", "Live", "MusicScore", "test", "fixture.json")));
        Assert.False(Directory.Exists(Path.Combine(service.PublicRoot, "zh-Hant")));
        var regional = await http.GetStringAsync($"/en/en/{Fixture.Key}/");
        var listing = Json.Read<AssetService.PathListing>(regional);
        Assert.Equal(("en", $"/en/en/{Fixture.Key}/fixture.json"), (listing.Region, Assert.Single(listing.Files).Path));
        Assert.Equal(service.Store.CurrentSnapshot("en", "en", "main"), listing.Snapshot);
        // /{locale}/ listings keep their original fields.
        var plain = await http.GetStringAsync($"/en/{Fixture.Key}/");
        Assert.DoesNotContain("\"region\"", plain);
        Assert.Equal($"/en/{Fixture.Key}/fixture.json", Assert.Single(Json.Read<AssetService.PathListing>(plain).Files).Path);
        foreach (var missing in new[] { $"/en/zh-Hant/{Fixture.Key}/fixture.json", $"/kr/en/{Fixture.Key}/fixture.json", $"/tw/ja/{Fixture.Key}/fixture.json", "/tw/en/", "/en/en/", $"/zh-Hant/{Fixture.Key}/fixture.json" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(missing)).StatusCode);
        // Before the backfill, a lost region tree is answered from SQLite; the backfill then relinks it.
        Directory.Delete(service.RegionsRoot, true); Directory.CreateDirectory(service.RegionsRoot);
        Assert.Equal(en, await http.GetByteArrayAsync($"/en/en/{Fixture.Key}/fixture.json"));
        await service.BackfillPublicTree();
        Assert.Equal(en, File.ReadAllBytes(Path.Combine(service.RegionsRoot, "en", "en", "Live", "MusicScore", "test", "fixture.json")));
        Assert.Equal(tw, File.ReadAllBytes(Path.Combine(service.RegionsRoot, "tw", "en", "Live", "MusicScore", "test", "fixture.json")));
        await app.StopAsync(); await cdn.StopAsync();
    }
    [Fact]
    public async Task QueueLimitAndCancellation()
    {
        using var dir = new TempDirectory(); var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/catalog_main_zh-Hant.hash", async (Microsoft.AspNetCore.Http.HttpContext c) => { await Task.Delay(TimeSpan.FromSeconds(30), c.RequestAborted); return "hash"; }); await cdn.StartAsync();
        await using var service = new AssetService(new Config { CdnRoot = Address(cdn), AllowLoopbackHttp = true, DataDir = dir.Path, QueueLimit = 1 });
        var task = service.StartRefresh(); Assert.Equal(429, Assert.Throws<ApiException>(() => service.StartRefresh()).Status); service.Cancel(task.Id);
        Assert.Equal("cancelled", (await service.Wait(task.Id).WaitAsync(TimeSpan.FromSeconds(5))).State); Assert.Equal(0, service.Budget.Used); await cdn.StopAsync();
    }
}
