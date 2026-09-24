namespace MoenotesAssets;

public sealed partial class AssetService
{
    private readonly object scanGate = new();
    private string? activeScanId;
    private bool automaticBundleScan;
    private bool scanFollowupQueued;

    public void EnableAutomaticBundleScan()
    {
        automaticBundleScan = true;
        if (Store.PendingBundleScans().Length > 0) _ = StartBundleScan();
    }

    private void ContinueAutomaticBundleScan()
    {
        if (shutdown.IsCancellationRequested || !automaticBundleScan || Store.PendingBundleScans().Length == 0) return;
        lock (scanGate)
        {
            if (activeScanId != null && GetTask(activeScanId) is { State: "queued" or "running" })
            {
                if (scanFollowupQueued) return;
                scanFollowupQueued = true;
                var previous = running.TryGetValue(activeScanId, out var work) ? work : Task.CompletedTask;
                _ = Task.Run(async () =>
                {
                    try { await previous; }
                    finally
                    {
                        lock (scanGate) scanFollowupQueued = false;
                        ContinueAutomaticBundleScan();
                    }
                });
                return;
            }
        }
        try { _ = StartBundleScan(); }
        catch (ApiException error) when (error.Status is 429 or 503) { Console.Error.WriteLine("Bundle scan will resume on next service start: " + error.Message); }
    }

    public TaskInfo StartBundleScan()
    {
        lock (scanGate)
        {
            if (activeScanId != null && GetTask(activeScanId) is { State: "queued" or "running" } current) return current;
            var initial = Store.PendingBundleScans();
            var started = Start("bundle_scan", null, initial.Length, async (task, token) =>
            {
                var attempted = new HashSet<string>(StringComparer.Ordinal);
                var errors = new List<ItemResult>();
                var succeeded = 0;
                while (!token.IsCancellationRequested)
                {
                    var pending = Store.PendingBundleScans().Where(t => !attempted.Contains(t.BundleId)).ToArray();
                    if (pending.Length == 0) break;
                    task = task with { Total = attempted.Count + pending.Length };
                    foreach (var target in pending)
                    {
                        if (token.IsCancellationRequested) break;
                        attempted.Add(target.BundleId);
                        var stage = Path.Combine(Config.DataDir, "tmp", "scan-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            var snapshot = ResolveSnapshot(target.Snapshot);
                            var location = Store.BundleLocation(snapshot.Id, target.BundleId);
                            using var reservation = await Budget.ReserveAsync(location.Options!.Size + Config.ExpandedBytes, token);
                            await using var lease = await downloadWork.Join(snapshot.Id + ":" + location.Id, ct => DownloadOne(snapshot, location, ct), token);
                            Directory.CreateDirectory(stage);
                            await workers.WaitAsync(token);
                            BundleContentItem[] contents;
                            try
                            {
                                contents = await Processes.ScanBundle(new(Config.ForSnapshot(snapshot), location, [lease.Value.Input], Path.Combine(stage, "out")), stage, token);
                            }
                            finally { workers.Release(); }
                            Store.SaveBundleScan(target.BundleId, lease.Value.PlainHash, contents);
                            succeeded++;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch (Exception error)
                        {
                            Store.RecordBundleScanFailure(target.BundleId, error.Message);
                            if (errors.Count < 100) errors.Add(new(target.BundleId, null, error.Message));
                            Console.Error.WriteLine($"[task {task.Id}] scan failed bundle={target.BundleId}: {error.Message}");
                        }
                        finally { RemoveTree(stage); }
                        task = task with { Completed = attempted.Count, Results = errors.ToArray(), Updated = Now };
                        if (attempted.Count % 20 == 0)
                        {
                            Store.Put("task", task.Id, task);
                            Console.Error.WriteLine($"[task {task.Id}] scan progress={attempted.Count}/{task.Total} succeeded={succeeded} failed={attempted.Count - succeeded}");
                        }
                    }
                }
                return task with { State = token.IsCancellationRequested ? "cancelled" : succeeded == attempted.Count ? "succeeded" : succeeded > 0 ? "partial" : "failed", Updated = Now };
            });
            activeScanId = started.Id;
            return started;
        }
    }
}
