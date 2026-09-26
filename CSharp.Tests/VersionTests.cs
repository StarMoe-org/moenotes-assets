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
    public async Task ReleasesAndMasterChangesRebuildTheSites()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        string master = "m1", cdn = ""; var masterReads = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/current_version.json", () => Results.Text(new JsonObject
        {
            ["regions"] = new JsonObject { ["tw"] = new JsonObject { ["version"] = master, ["resource_version"] = "1.0.0.1", ["server"] = new JsonObject { ["cdnRoot"] = cdn } } },
        }.ToJsonString(), "application/json"));
        server.MapGet("/master/{table}", (string table) =>
        {
            if (table == "MasterLiveMusic.json") Interlocked.Increment(ref masterReads);
            return Results.Text("{\"_allData\":[]}", "application/json");
        });
        server.MapGet("/v1/asset/Android/{file}", (string file) => file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(file.EndsWith(".bin") ? fixture.Catalog : fixture.Bundle));
        await server.StartAsync(); var address = Address(server); cdn = address + "/v1";
        var chartBase = Path.Combine(dir.Path, "base");
        Directory.CreateDirectory(Path.Combine(chartBase, "templates")); Directory.CreateDirectory(Path.Combine(chartBase, "assets"));
        File.WriteAllText(Path.Combine(chartBase, "base.json"), "{\"format\":1,\"siteFormat\":2}");
        await using var service = new AssetService(new Config
        {
            DataDir = Path.Combine(dir.Path, "data"),
            AllowLoopbackHttp = true,
            Region = "tw",
            Locale = "en",
            Locales = ["en"],
            CdnRoot = address + "/stale",
            VersionUrl = address + "/current_version.json",
            MasterRoot = address + "/master",
            ChartBase = chartBase,
            Apk = Path.Combine(dir.Path, "missing.apk"),
        });
        var release = await service.WaitRelease((await service.CheckVersions()).Regions[0].Release!).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("succeeded", release.State);
        // The completed release builds the chart site from its snapshot.
        var first = await service.WaitAutomaticChartSite().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(("chart_site", "succeeded", release.Locales[0].Snapshot), (first!.Kind, first.State, first.Snapshot));
        Assert.True(File.Exists(Path.Combine(service.ChartSiteRoot, "charts.json")));
        Assert.Equal(1, masterReads);
        // The model site follows the same release (here it cannot read its APK).
        var model = await service.WaitAutomaticModelSite().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(("model_site", "failed"), (model!.Kind, model.State));
        Assert.Contains("missing.apk", model.Error);
        // New master data at the same resource version: recorded on the release and the site is rebuilt, nothing re-unpacked.
        master = "m2";
        var changed = Assert.Single((await service.CheckVersions()).Regions);
        Assert.Equal(("master", release.Id), (changed.Action, changed.Release));
        var second = await service.WaitAutomaticChartSite().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEqual(first.Id, second!.Id);
        Assert.NotEqual(model.Id, (await service.WaitAutomaticModelSite().WaitAsync(TimeSpan.FromSeconds(60)))!.Id);
        Assert.Equal(("m2", release.BatchId), (service.GetRelease(release.Id)!.MasterVersion, service.GetRelease(release.Id)!.BatchId));
        Assert.Equal("current", Assert.Single((await service.CheckVersions()).Regions).Action);
        Assert.Equal(2, masterReads);
        await server.StopAsync();
    }

    [Fact]
    public void PlainHttpVersionUrlNeedsItsOwnOptIn()
    {
        var cluster = new Config { CdnRoot = "https://cdn.example.invalid/prod", VersionUrl = "http://metadata.moenotes.svc.cluster.local/current_version.json" };
        Assert.Contains("allow_insecure_version_url", Assert.Throws<InvalidDataException>(cluster.Validate).Message);
        (cluster with { AllowInsecureVersionUrl = true }).Validate();
        // The flag covers only the document's URL, not the CDN roots it names.
        Assert.Throws<InvalidDataException>(() => (cluster with { AllowInsecureVersionUrl = true, CdnRoot = "http://cdn.example.invalid/prod" }).Validate());
        Assert.Throws<InvalidDataException>(() => (cluster with { AllowInsecureVersionUrl = true, VersionPollSecs = 30 }).Validate());
        (cluster with { AllowInsecureVersionUrl = true, VersionPollSecs = 60 }).Validate();
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
