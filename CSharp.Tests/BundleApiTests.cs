using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class BundleApiTests
{
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("*")]
    public async Task MultiRegionRefreshVerificationDiffAndApiOnly(string allowedOrigin)
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create(); var count = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); await using var cdn = builder.Build();
        cdn.MapGet("/{region}/asset/Android/{file}", (string region, string file) => file.EndsWith(".hash") ? Results.Text("hint") : file.EndsWith(".bin") ? Results.Bytes(fixture.Catalog) : Download());
        IResult Download() { Interlocked.Increment(ref count); return Results.Bytes(fixture.Bundle); }
        await cdn.StartAsync(); var root = Address(cdn);
        var config = new Config { DataDir = dir.Path, Region = "tw", Locale = "en", AllowLoopbackHttp = true, CorsOrigins = [allowedOrigin], Regions = [new("tw", root + "/tw", "en", ["en", "ja"]), new("kr", root + "/kr", "en", ["en"])] };
        var service = new AssetService(config); await using var app = Api.Build(service, "http://127.0.0.1:0", apiKey: "integration-test-key"); await app.StartAsync(); using var http = new HttpClient { BaseAddress = new Uri(Address(app)) }; http.DefaultRequestHeaders.Authorization = new("Bearer", "integration-test-key");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/")).StatusCode);
        async Task<string> Refresh(string region, string locale)
        {
            var response = await http.PostAsync($"/catalog/refresh?region={region}&locale={locale}", null); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var task = Json.Read<TaskInfo>(await response.Content.ReadAsStringAsync()); var result = await service.Wait(task.Id); Assert.Equal("succeeded", result.State); return result.Snapshot!;
        }
        var tw = await Refresh("tw", "en"); var ja = await Refresh("tw", "ja"); var kr = await Refresh("kr", "en"); Assert.NotEqual(tw, kr);
        var page = Json.Read<BundlePage>(await http.GetStringAsync("/bundles?region=tw&locale=en")); var bundle = Assert.Single(page.Bundles); Assert.Equal(tw, page.Snapshot); Assert.Equal(0, count);
        var assets = Json.Read<AssetPage>(await http.GetStringAsync($"/bundles/{bundle.Id}/assets?snapshot={tw}")); Assert.Equal(Fixture.Key, Assert.Single(assets.Assets).Key);
        var diff = Json.Read<DiffPage>(await http.GetStringAsync($"/diffs?from={tw}&to={kr}&include_unchanged=true")); Assert.Equal("region", diff.Kind); Assert.Equal(1, diff.Summary["unchanged"]);
        var verify = await http.PostAsJsonAsync("/bundles/verify", new VerifyRequest([bundle.Id], Snapshot: tw), Json.Options); Assert.Equal(HttpStatusCode.Accepted, verify.StatusCode);
        var task = Json.Read<TaskInfo>(await verify.Content.ReadAsStringAsync()); Assert.Equal("succeeded", (await service.Wait(task.Id)).State); Assert.Equal(1, count);
        var detail = Json.Read<BundleEntry>(await http.GetStringAsync($"/bundles/{bundle.Id}?snapshot={tw}")); Assert.NotNull(detail.PlainSha256); Assert.NotEqual(detail.PlainSha256, detail.DownloadSha256);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/exports"); preflight.Headers.Add("Origin", "http://localhost:3000"); preflight.Headers.Add("Access-Control-Request-Method", "POST"); var cors = await http.SendAsync(preflight); Assert.Contains(allowedOrigin, cors.Headers.GetValues("Access-Control-Allow-Origin"));
        await app.StopAsync(); await service.DisposeAsync(); Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir.Path, "tmp"))); await cdn.StopAsync();
    }
}
