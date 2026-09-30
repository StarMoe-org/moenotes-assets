using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class ExportUnchangedTests
{
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogIdentityDoesNotVerifyAnotherDownloadSource(bool differentRegion)
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create(); var downloads = 0; var missingBundle = false;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/{root}/asset/Android/{file}", (string root, string file) =>
        {
            if (file.EndsWith(".hash")) return Results.Text("hash");
            if (file.EndsWith(".bin")) return Results.Bytes(fixture.Catalog);
            Interlocked.Increment(ref downloads);
            return missingBundle ? Results.NotFound() : Results.Bytes(fixture.Bundle);
        });
        await cdn.StartAsync(); var address = Address(cdn);
        await using var service = new AssetService(new Config
        {
            DataDir = dir.Path,
            Region = "tw",
            Locale = "en",
            AllowLoopbackHttp = true,
            Downloads = 1,
            Workers = 1,
            Regions = [new("tw", address + "/first", "en", ["en"]), new("kr", address + "/first", "en", ["en"])]
        });
        var first = await service.Wait(service.StartRefresh(region: "tw", catalogVersion: "1.0.0.1").Id);
        Assert.Equal("succeeded", first.State);
        var firstExport = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: first.Snapshot)).Id); Assert.Equal("succeeded", firstExport.State);
        var firstManifest = service.Store.Get<Manifest>("export", Assert.Single(firstExport.Results).ExportId!)!;
        var firstFile = service.Store.Get<FileRecord>("file", Assert.Single(firstManifest.Files).Id)!;

        // Neither a new region at the same URL nor a new root may inherit input verification.
        missingBundle = true;
        var region = differentRegion ? "kr" : "tw";
        var second = await service.Wait(service.StartRefresh(region: region, catalogVersion: "1.0.0.2", cdnRoots: address + (differentRegion ? "/first" : "/second")).Id);
        Assert.Equal("succeeded", second.State);
        var export = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: second.Snapshot)).Id);
        Assert.Equal("failed", export.State); Assert.Contains("CDN HTTP 404", Assert.Single(export.Results).Error!);
        Assert.Equal(2, downloads); Assert.Equal(0, export.Unchanged); Assert.Equal(0, export.Reused);
        var bundle = Assert.Single(service.Store.Bundles(second.Snapshot!, null, 0, 100).Bundles);
        Assert.Null(bundle.DownloadSha256); Assert.Null(bundle.PlainSha256);
        Assert.Single(service.Store.All<Manifest>("export"));
        Assert.Equal(Fixture.Body, File.ReadAllBytes(service.FilePath(firstFile)));
        Assert.Equal(Path.Combine(dir.Path, "exports", firstManifest.Id), Assert.Single(Directory.EnumerateDirectories(Path.Combine(dir.Path, "exports"))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, "tmp"))); Assert.Equal(0, service.Budget.Used);
        await cdn.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameSizeCorruptOutputDoesNotSucceedFromCache(bool newCatalog)
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", (string file) => file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(file.EndsWith(".bin") ? fixture.Catalog : fixture.Bundle));
        await cdn.StartAsync();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = Address(cdn), Region = "tw", Locale = "en", AllowLoopbackHttp = true, Downloads = 1, Workers = 1 });
        var refresh = await service.Wait(service.StartRefresh(catalogVersion: "1.0.0.1").Id); Assert.Equal("succeeded", refresh.State);
        var first = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: refresh.Snapshot)).Id); Assert.Equal("succeeded", first.State);
        var manifest = service.Store.Get<Manifest>("export", Assert.Single(first.Results).ExportId!)!;
        var record = service.Store.Get<FileRecord>("file", Assert.Single(manifest.Files).Id)!;
        var corrupt = (byte[])Fixture.Body.Clone(); corrupt[0] ^= 1;
        File.WriteAllBytes(service.FilePath(record), corrupt);
        if (newCatalog) { refresh = await service.Wait(service.StartRefresh(catalogVersion: "1.0.0.2").Id); Assert.Equal("succeeded", refresh.State); }

        var export = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: refresh.Snapshot)).Id);
        Assert.Equal("failed", export.State); Assert.Contains("hash mismatch", Assert.Single(export.Results).Error!);
        Assert.Equal(0, export.Unchanged); Assert.Single(service.Store.All<Manifest>("export"));
        Assert.Equal(corrupt, File.ReadAllBytes(service.FilePath(record)));
        Assert.Equal(Path.Combine(dir.Path, "exports", manifest.Id), Assert.Single(Directory.EnumerateDirectories(Path.Combine(dir.Path, "exports"))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, "tmp"))); Assert.Equal(0, service.Budget.Used);
        await cdn.StopAsync();
    }

    [Fact]
    public async Task MatchingPlaintextAcrossRegionsReusesConversionOnlyAfterDownload()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create(); var downloads = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", (string file) =>
        {
            if (file.EndsWith(".hash")) return Results.Text("hash");
            if (file.EndsWith(".bin")) return Results.Bytes(fixture.Catalog);
            Interlocked.Increment(ref downloads); return Results.Bytes(fixture.Bundle);
        });
        await cdn.StartAsync(); var address = Address(cdn);
        await using var service = new AssetService(new Config
        {
            DataDir = dir.Path,
            Region = "tw",
            Locale = "en",
            AllowLoopbackHttp = true,
            Downloads = 1,
            Workers = 1,
            Regions = [new("tw", address, "en", ["en"]), new("kr", address, "en", ["en"])]
        });
        foreach (var region in new[] { "tw", "kr" })
        {
            var refresh = await service.Wait(service.StartRefresh(region: region).Id); Assert.Equal("succeeded", refresh.State);
            var export = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: refresh.Snapshot)).Id); Assert.Equal("succeeded", export.State);
            Assert.Equal(region == "kr" ? 1 : 0, export.Reused);
            var bundle = Assert.Single(service.Store.Bundles(refresh.Snapshot!, null, 0, 100).Bundles);
            Assert.Equal(Crypto.Sha256(fixture.Bundle), bundle.DownloadSha256);
            Assert.Equal(Crypto.Sha256(Fixture.Bundle()), bundle.PlainSha256);
        }
        Assert.Equal(2, downloads);
        Assert.Equal(new[] { "kr", "tw" }, service.Store.All<Manifest>("export").Select(m => m.Region).Order(StringComparer.Ordinal).ToArray());
        Assert.Single(service.Blobs.Files());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, "tmp"))); Assert.Equal(0, service.Budget.Used);
        await cdn.StopAsync();
    }

    [Fact]
    public async Task NewCatalogVersionOnlyExportsChangedBundles()
    {
        using var dir = new TempDirectory();
        var original = Fixture.Create(); var changed = Fixture.Create("{\"fixture\":null}"u8.ToArray());
        var current = original; var downloads = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", (string file) =>
        {
            if (file.EndsWith(".hash")) return Results.Text("hash");
            if (file.EndsWith(".bin")) return Results.Bytes(file.Contains(".3_") ? current.Catalog : original.Catalog);
            Interlocked.Increment(ref downloads); return Results.Bytes(current.Bundle);
        });
        await cdn.StartAsync();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = Address(cdn), Region = "tw", Locale = "en", AllowLoopbackHttp = true });
        async Task<(string Snapshot, TaskInfo Export)> Release(string version)
        {
            var refresh = await service.Wait(service.StartRefresh(catalogVersion: version).Id); Assert.Equal("succeeded", refresh.State);
            var export = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: refresh.Snapshot)).Id); Assert.Equal("succeeded", export.State);
            return (refresh.Snapshot!, export);
        }

        var (first, firstExport) = await Release("1.0.0.1");
        Assert.Equal(1, downloads);
        var served = Assert.Single(firstExport.Results).ExportId!;

        // Same bundles under a new catalog version: nothing is downloaded or published, the earlier export keeps serving.
        var (second, secondExport) = await Release("1.0.0.2");
        Assert.NotEqual(first, second);
        Assert.Equal(1, downloads); Assert.Equal(1, secondExport.Unchanged);
        var item = Assert.Single(secondExport.Results); Assert.True(item.Unchanged); Assert.Equal(served, item.ExportId);
        Assert.Single(service.Store.All<Manifest>("export"));
        Assert.Equal(served, service.ResolvePath(null, "en", Fixture.Key)!.Manifest.Id);

        // A changed bundle converts again and becomes what the path serves.
        current = changed;
        var (_, thirdExport) = await Release("1.0.0.3");
        Assert.Equal(2, downloads); Assert.Equal(0, thirdExport.Unchanged);
        var republished = Assert.Single(thirdExport.Results).ExportId!;
        Assert.NotEqual(served, republished);
        Assert.Equal(republished, service.ResolvePath(null, "en", Fixture.Key)!.Manifest.Id);
        await cdn.StopAsync();
    }
}
