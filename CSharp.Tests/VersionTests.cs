using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoenotesAssets;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;
namespace MoenotesAssets.Tests;

public class VersionTests
{
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    [Fact]
    public async Task NewResourceVersionUnpacksFromItsCdnRootAndPublishesVersionFiles()
    {
        using var dir = new TempDirectory();
        var fixtures = new Dictionary<string, (byte[] Catalog, byte[] Bundle)> { ["v1"] = Fixture.Create(), ["v2"] = Fixture.Create("{\"fixture\":2026}"u8.ToArray()) };
        var requests = new ConcurrentQueue<string>(); string version = "1.0.0.1", roots = ""; var broken = true;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/current_version.json", () => Results.Text(new JsonObject
        {
            ["schema_version"] = 1,
            ["regions"] = new JsonObject
            {
                ["hk-tw-mo"] = new JsonObject { ["version"] = "master-" + version, ["resource_version"] = version, ["client_version"] = "1.0.1", ["server"] = new JsonObject { ["cdnRoot"] = roots } },
                ["jp"] = new JsonObject { ["resource_version"] = "9" },
            },
        }.ToJsonString(), "application/json"));
        server.MapGet("/{root}/asset/Android/{file}", (string root, string file) =>
        {
            requests.Enqueue(root + "/" + file);
            if (!fixtures.TryGetValue(root, out var fixture) || broken) return Results.StatusCode(503);
            return file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(file.EndsWith(".bin") ? fixture.Catalog : fixture.Bundle);
        });
        await server.StartAsync(); var address = Address(server);
        var config = new Config
        {
            DataDir = dir.Path,
            AllowLoopbackHttp = true,
            Region = "tw",
            Locale = "en",
            VersionUrl = address + "/current_version.json",
            Regions = [new("tw", address + "/stale", "en", ["en"], MetadataRegion: "hk-tw-mo"), new("kr", address + "/stale", "en", ["en"]), new("jp", address + "/stale", "en", ["en"])]
        };
        await using var service = new AssetService(config);
        await using var api = Api.Build(service, "http://127.0.0.1:0", "test-key"); await api.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(Address(api)) };
        try
        {
            // While the CDN is down the release fails, is not current, and is retried by the next check.
            roots = $"{address}/down|{address}/v1";
            var check = await service.CheckVersions();
            Assert.Equal(new[] { "queued", "missing", "invalid" }, check.Regions.Select(r => r.Action));
            var failed = await service.WaitRelease(check.Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(("failed", "failed"), (failed.State, Assert.Single(failed.Locales).State));
            Assert.Null(JsonNode.Parse(await http.GetStringAsync("/versions/current_version.json"))!["regions"]!["tw"]);
            Assert.Equal("failed", (string?)JsonNode.Parse(await http.GetStringAsync("/versions/index.json"))!["regions"]!["tw"]![0]!["state"]);
            // The first mirror is down; the second answers and becomes the snapshot's root. cdn_root is never used.
            broken = false; check = await service.CheckVersions();
            Assert.Equal(("queued", check.Regions[0].Release), (check.Regions[0].Action, failed.Id));
            var first = await service.WaitRelease(check.Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("succeeded", first.State);
            var en = Assert.Single(first.Locales);
            Assert.Equal((1, 1), (en.Total, en.Exported)); Assert.Null(en.Diff); Assert.Null(first.Previous);
            Assert.Equal(address + "/v1", service.Store.RequireSnapshot(en.Snapshot!).CdnRoot);
            Assert.Equal("current", (await service.CheckVersions()).Regions[0].Action);
            // Manual refreshes follow the release's roots too.
            Assert.Equal(en.Snapshot, (await service.Wait(service.StartRefresh().Id)).Snapshot);
            Assert.DoesNotContain(requests, r => r.StartsWith("stale/", StringComparison.Ordinal));
            var current = JsonNode.Parse(await http.GetStringAsync("/versions/current_version.json"))!;
            Assert.Equal("1.0.0.1", (string?)current["regions"]!["tw"]!["resource_version"]);
            Assert.Equal("master-1.0.0.1", (string?)current["regions"]!["tw"]!["master_version"]);
            Assert.Equal(en.Snapshot, (string?)current["regions"]!["tw"]!["locales"]!["en"]!["snapshot"]);

            // A new version with other content is unpacked from its own root and diffed against the first release.
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/versions/check", null)).StatusCode);
            (version, roots) = ("1.0.0.2", address + "/v2");
            http.DefaultRequestHeaders.Authorization = new("Bearer", "test-key");
            var response = await http.PostAsync("/versions/check", null); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var queued = Json.Read<VersionCheck>(await response.Content.ReadAsStringAsync()).Regions[0];
            Assert.Equal(("queued", "1.0.0.2"), (queued.Action, queued.ResourceVersion));
            var second = await service.WaitRelease(queued.Release!).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(("succeeded", "1.0.0.1"), (second.State, second.Previous));
            var diff = Assert.Single(second.Locales).Diff!;
            Assert.Equal(("1.0.0.1", 0, 1, 0, 0), (diff.From, diff.Added, diff.Changed, diff.Removed, diff.Unchanged));
            var document = JsonNode.Parse(await http.GetStringAsync("/versions/tw/1.0.0.2/diff/en.json"))!;
            var changed = Assert.Single(document["changed"]!.AsArray())!;
            Assert.Equal(Fixture.Key, (string?)changed["key"]);
            Assert.Equal("fixture.json", (string?)Assert.Single(changed["files"]!.AsArray()));
            Assert.Equal("1.0.0.1", (string?)document["from"]!["resource_version"]);
            var index = JsonNode.Parse(await http.GetStringAsync("/versions/index.json"))!;
            Assert.Equal(new[] { "1.0.0.2", "1.0.0.1" }, index["regions"]!["tw"]!.AsArray().Select(r => (string?)r!["resource_version"]));
            Assert.Equal("succeeded", (string?)JsonNode.Parse(await http.GetStringAsync("/versions/tw/1.0.0.1/release.json"))!["state"]);
            var cached = await http.GetAsync("/versions/current_version.json"); Assert.Equal(TimeSpan.FromSeconds(60), cached.Headers.CacheControl!.MaxAge);
            Assert.Equal("1.0.0.2", (string?)JsonNode.Parse(await cached.Content.ReadAsStringAsync())!["regions"]!["tw"]!["resource_version"]);
            // Asset paths follow the new release.
            Assert.Equal("{\"fixture\":2026}", await http.GetStringAsync($"/en/{Fixture.Key}/fixture.json"));
        }
        finally { await api.StopAsync(); await server.StopAsync(); }
    }

    [Fact]
    public void DiffComparesPublishedFilesByContent()
    {
        static Manifest M(string id, params (string Label, string Sha)[] files) =>
            new(id, "s", "k", "p", [], files.Select((f, i) => new PublishedFile(id + i, $"{i:D5}.png", f.Label, "image/png", 1, f.Sha, null)).ToArray());
        var manifests = new[] { M("a1", ("a", "1")), M("a2", ("a", "1")), M("b1", ("b", "1")), M("b2", ("b", "1"), ("b2", "3")), M("n2", ("n", "1")) }.ToDictionary(m => m.Id);
        var before = new Dictionary<string, string> { ["same"] = "s", ["a"] = "a1", ["b"] = "b1", ["gone"] = "g1", ["broken"] = "x1" };
        var after = new Dictionary<string, string> { ["same"] = "s", ["a"] = "a2", ["b"] = "b2", ["new"] = "n2" };
        var diff = VersionDiff.Compute(before, after, manifests, [new ItemResult("broken", null, "decoder error")]);
        Assert.Equal(2, diff.Unchanged); // the same export, and a new export with identical files
        var added = Assert.Single(diff.Added); Assert.Equal("new", added.Key); Assert.Equal(["n.png"], added.Files!);
        var changed = Assert.Single(diff.Changed); Assert.Equal("b", changed.Key); Assert.Equal(["b2.png"], changed.Files!);
        Assert.Equal("gone", Assert.Single(diff.Removed).Key);
        var failed = Assert.Single(diff.Failed); Assert.Equal(("broken", "decoder error"), (failed.Key, failed.Error));
    }
}
