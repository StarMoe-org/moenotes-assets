using System.Threading.Channels;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public sealed record BatchRequest(string? Region = null, string[]? Locales = null, bool Export = true, string Prefix = "");
public sealed record BatchStep(string Locale, string Phase, string State = "queued", string? TaskId = null, string? Snapshot = null, string? Error = null);
// CdnRoot and Release are set for a release detected by version tracking (Versions.cs): refreshes use its roots.
public sealed record BatchInfo(string Id, long Sequence, string Region, string Version, string Prefix, string State,
    BatchStep[] Steps, long Created, long Updated, string? CdnRoot = null, string? Release = null);

public sealed partial class AssetService
{
    private readonly object batchGate = new();
    private readonly Channel<string> batchChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, CancellationTokenSource> batchCancellations = new();
    private Task batchRunner = Task.CompletedTask;
    private long batchSequence;

    private void InitializeBatches()
    {
        var retained = Store.All<BatchInfo>("batch").OrderBy(b => b.Sequence).ToArray();
        batchSequence = retained.Select(b => b.Sequence).DefaultIfEmpty().Max();
        foreach (var batch in retained.Where(b => b.State is "queued" or "running"))
        {
            // Completed child tasks remain authoritative even if the parent checkpoint was interrupted.
            var recovered = batch with { State = "queued", Updated = Now };
            Store.Put("batch", batch.Id, recovered);
            batchChannel.Writer.TryWrite(batch.Id);
            Console.Error.WriteLine($"[batch {batch.Id}] queued position_sequence={batch.Sequence} region={batch.Region} steps={batch.Steps.Length}");
        }
        batchRunner = Task.Run(ProcessBatches);
    }

    public BatchInfo StartBatch(BatchRequest request, string? cdnRoot = null, string? release = null)
    {
        var scope = Config.ForRegion(request.Region);
        var languages = request.Locales ?? (scope.Locales.Length > 0 ? scope.Locales : [scope.Locale]);
        Require(languages is { Length: > 0 and <= 64 } && languages.All(l => l != null), "Select 1 to 64 locales");
        Require(request.Prefix != null && request.Prefix.Length <= 4096, "Invalid prefix");
        languages = languages.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var language in languages) _ = Config.ForRegion(scope.Region, language);
        lock (batchGate)
        {
            if (shutdown.IsCancellationRequested) throw new ApiException(503, "Service shutting down");
            if (Store.All<BatchInfo>("batch").Count(b => b.State is "queued" or "running") >= Config.QueueLimit)
                throw new ApiException(429, "Batch queue full");
            var steps = languages.SelectMany(l => request.Export
                ? new[] { new BatchStep(l, "refresh"), new BatchStep(l, "export") }
                : new[] { new BatchStep(l, "refresh") }).ToArray();
            var batch = new BatchInfo(Guid.NewGuid().ToString("N"), ++batchSequence, scope.Region, scope.BiliVersion,
                request.Prefix!, "queued", steps, Now, Now, cdnRoot, release);
            Store.Put("batch", batch.Id, batch);
            Console.Error.WriteLine($"[batch {batch.Id}] queued sequence={batch.Sequence} region={batch.Region} steps={steps.Length}");
            batchChannel.Writer.TryWrite(batch.Id);
            return batch;
        }
    }

    public BatchInfo GetBatch(string id) => Store.Get<BatchInfo>("batch", id) ?? throw new ApiException(404, "Batch not found");
    public object ListBatches(int offset = 0, int limit = 100)
    {
        Require(offset >= 0 && limit is >= 1 and <= 1000, "Invalid pagination");
        var all = Store.All<BatchInfo>("batch").OrderBy(b => b.Sequence).ToArray();
        return new
        {
            total = all.Length,
            offset,
            limit,
            batches = all.Skip(offset).Take(limit).Select(b => new
            {
                b.Id,
                b.Sequence,
                b.Region,
                b.State,
                total = b.Steps.Length,
                completed = b.Steps.Count(s => s.State is not ("queued" or "running")),
                b.Created,
                b.Updated,
                active_task = b.Steps.FirstOrDefault(s => s.State == "running")?.TaskId
            }).ToArray()
        };
    }

    public BatchInfo CancelBatch(string id)
    {
        lock (batchGate)
        {
            var batch = GetBatch(id);
            if (batch.State is not ("queued" or "running")) return batch;
            batch = batch with
            {
                State = "cancelled",
                Updated = Now,
                Steps = batch.Steps.Select(s => s.State is "queued" or "running" ? s with { State = "cancelled" } : s).ToArray()
            };
            Store.Put("batch", id, batch);
            if (batchCancellations.TryGetValue(id, out var cancellation)) cancellation.Cancel();
            Console.Error.WriteLine($"[batch {id}] cancelled");
            return batch;
        }
    }

    private async Task ProcessBatches()
    {
        try
        {
            RecoverReleases();
            await foreach (var id in batchChannel.Reader.ReadAllAsync(shutdown.Token))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                lock (batchGate)
                {
                    var batch = GetBatch(id);
                    if (batch.State != "queued") continue;
                    batchCancellations[id] = cancellation;
                    Store.Put("batch", id, batch with { State = "running", Updated = Now });
                    Console.Error.WriteLine($"[batch {id}] running region={batch.Region} steps={batch.Steps.Length}");
                }
                try { await RunBatch(id, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception e)
                {
                    lock (batchGate)
                    {
                        var batch = GetBatch(id);
                        if (batch.State == "running") Store.Put("batch", id, batch with
                        {
                            State = "failed",
                            Updated = Now,
                            Steps = batch.Steps.Select(s => s.State is "queued" or "running" ? s with { State = "failed", Error = e.Message } : s).ToArray()
                        });
                    }
                }
                finally { lock (batchGate) batchCancellations.Remove(id); }
                FinalizeRelease(id);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task RunBatch(string id, CancellationToken token)
    {
        var initial = GetBatch(id);
        for (var index = 0; index < initial.Steps.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var batch = GetBatch(id);
            var step = batch.Steps[index];
            if (step.State is not ("queued" or "running")) continue;
            if (step.Phase == "export")
            {
                var previous = batch.Steps[index - 1];
                if (previous.State != "succeeded")
                {
                    SaveStep(id, index, step with { State = "skipped", Error = "Catalog refresh did not succeed" });
                    continue;
                }
                step = step with { Snapshot = previous.Snapshot };
            }
            TaskInfo? child = step.TaskId == null ? null : GetTask(step.TaskId);
            // A child interrupted by shutdown is retried against the retained refresh snapshot.
            if (child?.State is not ("succeeded" or "partial") && step.State == "running") child = null;
            try
            {
                while (child == null)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        lock (batchGate)
                        {
                            token.ThrowIfCancellationRequested();
                            child = step.Phase == "refresh" ? StartRefresh(batch.Region, step.Locale, batch.Version, batch.CdnRoot)
                                : StartExport(new(Prefix: batch.Prefix, Snapshot: step.Snapshot));
                            step = step with { State = "running", TaskId = child.Id };
                            SaveStep(id, index, step);
                        }
                    }
                    catch (ApiException e) when (e.Status == 429) { await Task.Delay(250, token); }
                }
                TaskInfo result;
                try { result = await Wait(child.Id, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Cancel(child.Id);
                    await Wait(child.Id); // Drain before starting the next queued batch.
                    throw;
                }
                token.ThrowIfCancellationRequested();
                var failed = result.Results.Count(r => r.Error != null);
                SaveStep(id, index, step with
                {
                    State = result.State,
                    Snapshot = result.Snapshot,
                    Error = result.Error ?? (failed > 0 ? $"{failed} resources failed; see child task results" : null)
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { SaveStep(id, index, step with { State = "failed", Error = e.Message }); }
        }
        lock (batchGate)
        {
            token.ThrowIfCancellationRequested();
            var batch = GetBatch(id);
            var state = batch.Steps.All(s => s.State == "succeeded") ? "succeeded"
                : batch.Steps.Any(s => s.State is "succeeded" or "partial") ? "partial" : "failed";
            Store.Put("batch", id, batch with { State = state, Updated = Now });
            Console.Error.WriteLine($"[batch {id}] {state} steps={batch.Steps.Length} failed={batch.Steps.Count(s => s.State == "failed")} skipped={batch.Steps.Count(s => s.State == "skipped")}");
        }
    }

    private void SaveStep(string id, int index, BatchStep step)
    {
        lock (batchGate)
        {
            var batch = GetBatch(id);
            if (batch.State == "cancelled") return;
            var steps = batch.Steps.ToArray(); steps[index] = step;
            Store.Put("batch", id, batch with { Steps = steps, Updated = Now });
            Console.Error.WriteLine($"[batch {id}] step={index + 1}/{steps.Length} region={batch.Region} locale={step.Locale} phase={step.Phase} state={step.State} task={step.TaskId}");
        }
    }
}
