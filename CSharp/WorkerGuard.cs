using System.Diagnostics;
namespace MoenotesAssets;
// A sampled process/disk guard complements per-format allocation checks. Container quotas
// remain the hard OS boundary; native decoder and FFmpeg allocations are not managed heap.
public sealed class WorkerGuard : IDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task monitor;
    public string? Failure { get; private set; }
    public WorkerGuard(WorkerJob job, Process? worker = null)
    {
        monitor = Task.Run(async () =>
        {
            using var ownHandle = worker == null ? Process.GetCurrentProcess() : null;
            var self = worker ?? ownHandle!;
            while (!stopped.IsCancellationRequested)
            {
                try
                {
                    self.Refresh();
                    if (self.HasExited) return;
                    var stage = Path.GetDirectoryName(job.Output)!;
                    long bytes = 0, outputs = 0;
                    if (worker != null && Directory.Exists(stage))
                        foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                        {
                            var size = new FileInfo(file).Length; bytes += size;
                            if (file.StartsWith(job.Output + Path.DirectorySeparatorChar, StringComparison.Ordinal)) outputs += size;
                        }
                    var parentExited = false;
                    if (worker == null && job.ParentPid != 0)
                    {
                        try { using var parent = Process.GetProcessById(job.ParentPid); parentExited = parent.HasExited; }
                        catch (ArgumentException) { parentExited = true; }
                    }
                    if (parentExited || (worker != null && (self.WorkingSet64 > job.Config.WorkerMemoryBytes || outputs > job.Config.OutputBytes || bytes > job.Config.OutputBytes + 2 * job.Config.ExpandedBytes)))
                    {
                        Failure = "Worker memory/disk limit exceeded or parent exited";
                        Console.Error.WriteLine(Failure);
                        self.Kill(worker != null); return; // Only the parent may terminate the entire worker tree.
                    }
                    await Task.Delay(200, stopped.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* Publication/deletion can race a sample. */ }
                catch (InvalidOperationException) { return; } // Worker exited during a sample.
            }
        });
    }
    public void Dispose() { stopped.Cancel(); monitor.GetAwaiter().GetResult(); stopped.Dispose(); }
}
