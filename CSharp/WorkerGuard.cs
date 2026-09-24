using System.Diagnostics;
namespace MoenotesAssets;
// A sampled process/disk guard complements per-format allocation checks. Container quotas
// remain the hard OS boundary; native decoder and FFmpeg allocations are not managed heap.
public sealed class WorkerGuard : IDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task monitor;
    public WorkerGuard(WorkerJob job)
    {
        monitor = Task.Run(async () =>
        {
            using var self = Process.GetCurrentProcess();
            while (!stopped.IsCancellationRequested)
            {
                try
                {
                    self.Refresh();
                    var stage = Path.GetDirectoryName(job.Output)!;
                    long bytes = 0, outputs = 0;
                    if (Directory.Exists(stage))
                        foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                        {
                            var size = new FileInfo(file).Length; bytes += size;
                            if (file.StartsWith(job.Output + Path.DirectorySeparatorChar, StringComparison.Ordinal)) outputs += size;
                        }
                    var parentExited = false;
                    if (job.ParentPid != 0)
                    {
                        try { using var parent = Process.GetProcessById(job.ParentPid); parentExited = parent.HasExited; }
                        catch (ArgumentException) { parentExited = true; }
                    }
                    if (parentExited || self.WorkingSet64 > job.Config.WorkerMemoryBytes || outputs > job.Config.OutputBytes || bytes > job.Config.OutputBytes + 2 * job.Config.ExpandedBytes)
                    {
                        Console.Error.WriteLine("Worker memory/disk limit exceeded or parent exited");
                        self.Kill(true); return;
                    }
                    await Task.Delay(200, stopped.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* Publication/deletion can race a sample. */ }
            }
        });
    }
    public void Dispose() { stopped.Cancel(); monitor.GetAwaiter().GetResult(); stopped.Dispose(); }
}
