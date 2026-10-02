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
        // Manifests live only in SQLite; content-addressed exports leave nothing under exports/.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, "exports")));
        await cdn.StopAsync();
    }
}
