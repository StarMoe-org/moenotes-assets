using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MoenotesAssets;
using System.Net;
using Xunit;

namespace MoenotesAssets.Tests;

public class AuthTests
{
    [Theory]
    [InlineData("")]
    [InlineData("test-secret")]
    public async Task AdministrativeRoutesRequireKeyWhileBrowsingAndPreflightRemainPublic(string key)
    {
        using var dir = new TempDirectory();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = "https://example.com/prod", CorsOrigins = ["*"] });
        await using var app = Api.Build(service, "http://127.0.0.1:0", apiKey: key);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var rejected = key.Length == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized;
        foreach (var path in new[] { "/catalog/refresh", "/exports", "/bundles/verify", "/tasks/unknown/cancel", "/tasks/batches", "/tasks/batches/unknown/cancel" })
        {
            using var response = await http.PostAsync(path, null);
            Assert.Equal(rejected, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        foreach (var path in new[] { "/tasks/unknown", "/TASKS/unknown/", "/storage", "/STORAGE/", "/tasks/batches", "/tasks/batches/unknown" })
            Assert.Equal(rejected, (await http.GetAsync(path)).StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Head, "/tasks/unknown"))
            Assert.Equal(rejected, (await http.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/regions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/catalogs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/files/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/exports/unknown")).StatusCode);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/exports");
        preflight.Headers.Add("Origin", "https://frontend.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var cors = await http.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, cors.StatusCode);
        Assert.Contains("*", cors.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Contains("authorization", string.Join(",", cors.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant());
        http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-key");
        Assert.Equal(rejected, (await http.GetAsync("/storage")).StatusCode);
        http.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(rejected, (await http.GetAsync("/storage?api_key=test-secret")).StatusCode);
        if (key.Length > 0)
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", key);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/storage")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/tasks/unknown")).StatusCode);
            using var duplicate = new HttpRequestMessage(HttpMethod.Get, "/storage");
            duplicate.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + key, "Bearer " + key });
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(duplicate)).StatusCode);
        }
        await app.StopAsync();
    }
}
