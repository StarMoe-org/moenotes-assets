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
using System.Net.Http.Json;
using Xunit;
namespace MoenotesAssets.Tests;

public class BatchQueueTests
{
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task<BatchInfo> Finish(AssetService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var batch = service.GetBatch(id);
            if (batch.State is not ("queued" or "running")) return batch;
            await Task.Delay(20, timeout.Token);
        }
    }
    [Fact]
    public async Task AllLanguagesExportInOrderAndQueuedCancellationNeverRuns()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", async (string file) =>
        {
            requests.Enqueue(file);
            if (file == "catalog_main_en.hash" && !entered.Task.IsCompleted) { entered.SetResult(); await release.Task; }
            return file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(file.EndsWith(".bin") ? fixture.Catalog : fixture.Bundle);
        });
        await cdn.StartAsync();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = Address(cdn), Region = "tw", Locale = "en", Locales = ["en", "ja", "ko"], QueueLimit = 3, AllowLoopbackHttp = true });
        await using var api = Api.Build(service, "http://127.0.0.1:0", "test-key"); await api.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(Address(api)) }; http.DefaultRequestHeaders.Authorization = new("Bearer", "test-key");
        try
        {
            var response = await http.PostAsJsonAsync("/tasks/batches", new { region = "tw" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var first = Json.Read<BatchInfo>(await response.Content.ReadAsStringAsync());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = service.StartBatch(new(Locales: ["en"], Export: false));
            var cancelled = service.StartBatch(new(Locales: ["ja"], Export: false));
            Assert.Equal(429, Assert.Throws<ApiException>(() => service.StartBatch(new())).Status);
            Assert.Equal("queued", service.GetBatch(second.Id).State);
            var cancel = await http.PostAsync($"/tasks/batches/{cancelled.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal("cancelled", service.GetBatch(cancelled.Id).State);
            release.SetResult();
            var done = await Finish(service, first.Id); Assert.Equal("succeeded", done.State); Assert.Equal(6, done.Steps.Length);
            Assert.All(done.Steps, s => Assert.Equal("succeeded", s.State));
            Assert.Equal("succeeded", (await Finish(service, second.Id)).State);
            Assert.Equal(new[] { "catalog_main_en.hash", "catalog_main_en.bin", "fixture.bundle", "catalog_main_ja.hash", "catalog_main_ja.bin", "fixture.bundle", "catalog_main_ko.hash", "catalog_main_ko.bin", "fixture.bundle", "catalog_main_en.hash", "catalog_main_en.bin" }, requests.ToArray());
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/tasks/batches")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/tasks/batches/{first.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/tasks/batches/missing")).StatusCode);
            Assert.Equal(3, service.Store.All<Manifest>("export").Length);
        }
        finally { release.TrySetResult(); await api.StopAsync(); await cdn.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBatchResumesAndCancelledBatchStaysCancelled(bool cancelActive)
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>(); var interrupt = true;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", async (string file, HttpContext context) =>
        {
            requests.Enqueue(file);
            if (file == "catalog_main_ja.hash" && interrupt)
            {
                blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            return file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(fixture.Catalog);
        });
        await cdn.StartAsync();
        var config = new Config { DataDir = dir.Path, CdnRoot = Address(cdn), Locale = "en", Locales = ["en", "ja"], AllowLoopbackHttp = true };
        string id;
        await using (var service = new AssetService(config))
        {
            id = service.StartBatch(new(Export: false)).Id;
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("succeeded", service.GetBatch(id).Steps[0].State);
            if (cancelActive)
            {
                service.CancelBatch(id);
                interrupt = false;
                var next = service.StartBatch(new(Locales: ["ja"], Export: false));
                Assert.Equal("succeeded", (await Finish(service, next.Id)).State);
            }
        }
        interrupt = false;
        await using (var service = new AssetService(config))
        {
            Assert.Equal(cancelActive ? "cancelled" : "succeeded", (await Finish(service, id)).State);
            Assert.Equal(1, requests.Count(r => r == "catalog_main_en.hash"));
            Assert.Equal(2, requests.Count(r => r == "catalog_main_ja.hash"));
        }
        await cdn.StopAsync();
    }

    [Fact]
    public async Task FailedLanguageSkipsExportAndContinuesNextLanguage()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/{file}", (string file) => file.Contains("_en") ? Results.NotFound() : file.EndsWith(".hash") ? Results.Text("hash") : Results.Bytes(file.EndsWith(".bin") ? fixture.Catalog : fixture.Bundle));
        await cdn.StartAsync();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = Address(cdn), Locale = "en", Locales = ["en", "ja"], AllowLoopbackHttp = true });
        var batch = await Finish(service, service.StartBatch(new()).Id);
        Assert.Equal("partial", batch.State);
        Assert.Equal(new[] { "failed", "skipped", "succeeded", "succeeded" }, batch.Steps.Select(s => s.State));
        Assert.Contains("404", batch.Steps[0].Error!);
        await cdn.StopAsync();
    }
}
