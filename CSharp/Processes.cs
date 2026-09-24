using System.Diagnostics;
using System.Reflection;
namespace MoenotesAssets;

public static class Processes
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken token, long outputLimit = 1 << 20, WorkerJob? limits = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start process");
        using var guard = limits == null ? null : new WorkerGuard(limits, process);
        process.StandardInput.Close();
        using var registration = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = Read(process.StandardOutput, outputLimit, process);
        var stderr = Read(process.StandardError, outputLimit, process);
        await process.WaitForExitAsync(CancellationToken.None);
        var results = await Task.WhenAll(stdout, stderr);
        token.ThrowIfCancellationRequested();
        Config.Require(guard?.Failure == null, guard?.Failure ?? "Worker limit exceeded");
        Config.Require(process.ExitCode == 0, $"{Path.GetFileName(executable)} failed: {results[1][..Math.Min(2000, results[1].Length)]}");
        return results[0];
    }
    private static async Task<string> Read(StreamReader reader, long limit, Process process)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer)) > 0)
        {
            if (result.Length + length > limit)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new InvalidDataException("Process output limit");
            }
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
    public static async Task<Artifact[]> Worker(WorkerJob job, string stage, CancellationToken token)
    {
        var request = Path.Combine(stage, "job.json");
        await File.WriteAllTextAsync(request, Json.Write(job with { ParentPid = Environment.ProcessId }), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(job.Config.WorkerTimeoutSecs));
        var executable = Environment.ProcessPath!;
        var arguments = new List<string>();
        // When hosted by tests, always run the service assembly rather than testhost.
        var assembly = typeof(Processes).Assembly.Location;
        if (!Path.GetFileNameWithoutExtension(executable).Equals("MoenotesAssets", StringComparison.OrdinalIgnoreCase))
        {
            executable = "dotnet"; arguments.Add(assembly);
        }
        arguments.AddRange(["worker", request]);
        await Run(executable, arguments, timeout.Token, limits: job);
        var result = Json.Read<WorkerResult>(await File.ReadAllTextAsync(request + ".result.json", token));
        if (result.Error != null) throw new InvalidDataException(result.Error);
        Config.Require(result.Files.Length > 0, "Worker produced no files");
        return result.Files;
    }
}
