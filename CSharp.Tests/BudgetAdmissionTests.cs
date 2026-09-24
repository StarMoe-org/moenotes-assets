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

public class BudgetAdmissionTests
{
    [Fact]
    public async Task TenDownloadsOneWorkerWaitForBudgetInsteadOfFailing()
    {
        using var dir = new TempDirectory(); var fixture = Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build();
        cdn.MapGet("/asset/Android/fixture.bundle", async () => { entered.TrySetResult(); await release.Task; return Results.Bytes(fixture.Bundle); });
        await cdn.StartAsync(); var url = cdn.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var config = new Config
        {
            DataDir = dir.Path,
            CdnRoot = url,
            AllowLoopbackHttp = true,
            Downloads = 10,
            Workers = 1,
            InputBytes = 1 << 20,
            OutputBytes = 1 << 20,
            ExpandedBytes = 1 << 20,
            TempBytes = 4 << 20,
            MemoryThreshold = 0
        };
        await using var service = new AssetService(config);
        var digest = Crypto.Sha256(fixture.Catalog); File.WriteAllBytes(Path.Combine(dir.Path, "catalogs", digest + ".bin"), fixture.Catalog);
        var tasks = new List<TaskInfo>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var snapshot = new Snapshot("snapshot" + i, digest, "tw", "locale" + i, "main", url, "", AssetService.Now);
                service.Store.IndexSnapshot(snapshot, Catalog.Parse(fixture.Catalog));
                tasks.Add(service.StartExport(new(Keys: [Fixture.Key], Snapshot: snapshot.Id)));
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(service.Budget.Used, 3L << 20, config.TempBytes);
            var cancelled = tasks[^1]; service.Cancel(cancelled.Id);
            var cancellationResult = await service.Wait(cancelled.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cancelled", cancellationResult.State); Assert.Empty(cancellationResult.Results);
            release.TrySetResult();
            var results = await Task.WhenAll(tasks.Take(9).Select(t => service.Wait(t.Id))).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(results, t => { Assert.Equal("succeeded", t.State); Assert.Null(Assert.Single(t.Results).Error); });
            Assert.Equal(0, service.Budget.Used);
        }
        finally { release.TrySetResult(); await cdn.StopAsync(); }
    }
    [Fact]
    public async Task WaitingReservationIsCancellableAndOversizedTaskFailsImmediately()
    {
        var budget = new Budget(10); using var held = budget.Reserve(10);
        using var cancellation = new CancellationTokenSource();
        var wait = budget.ReserveAsync(1, cancellation.Token).AsTask(); Assert.False(wait.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await budget.ReserveAsync(11, CancellationToken.None));
        held.Dispose(); using var next = await budget.ReserveAsync(10, CancellationToken.None); Assert.Equal(10, budget.Used);
    }
}
