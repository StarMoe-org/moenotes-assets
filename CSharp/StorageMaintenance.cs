using System.Globalization;
namespace MoenotesAssets;

public sealed partial class Store
{
    private const string SlimFilesSetting = "file_records_slim";

    /// <summary>
    /// Drops the metadata copied into file records before Publish stopped storing it (it stays in the manifest).
    /// Runs in short batches on the writer so exports keep publishing meanwhile; returns the rewritten row count.
    /// </summary>
    public long SlimFileRecords(CancellationToken token, int batch = 5000)
    {
        if (Get<int>("setting", SlimFilesSetting) >= 1) return 0;
        long rewritten = 0; var after = "";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                using var bounds = Command("SELECT MAX(id),COUNT(*) FROM (SELECT id FROM records WHERE kind='file' AND id>$after ORDER BY id LIMIT $limit)", [("$after", after), ("$limit", batch)]);
                using var reader = bounds.ExecuteReader(); reader.Read();
                if (reader.GetInt64(1) == 0) break;
                var last = reader.GetString(0); reader.Close();
                using var update = Command("UPDATE records SET body=json_remove(body,'$.file.metadata') WHERE kind='file' AND id>$after AND id<=$last AND json_extract(body,'$.file.metadata') IS NOT NULL",
                    [("$after", after), ("$last", last)]);
                rewritten += update.ExecuteNonQuery(); after = last;
            }
        }
        Put("setting", SlimFilesSetting, 1);
        Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        return rewritten;
    }

    /// <summary>Deletes finished tasks last updated before <paramref name="before"/>, except <paramref name="keep"/>.</summary>
    public long PruneTasks(long before, IEnumerable<string> keep)
    {
        lock (gate)
        {
            using var command = Command("""
                DELETE FROM records WHERE kind='task' AND json_extract(body,'$.state') NOT IN ('queued','running')
                  AND json_extract(body,'$.updated') < $before AND id NOT IN (SELECT value FROM json_each($keep))
                """, [("$before", before), ("$keep", Json.Write(keep.ToArray()))]);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Rewrites the database without free pages and truncates the WAL. Writers wait for the whole run.</summary>
    public (long Before, long After) Compact()
    {
        lock (gate)
        {
            var before = Scalar("PRAGMA page_count") * Scalar("PRAGMA page_size");
            Execute("VACUUM"); Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            return (before, Scalar("PRAGMA page_count") * Scalar("PRAGMA page_size"));
        }
    }
}

public sealed partial class AssetService
{
    /// <summary>Finished tasks are kept this long; release diffs keep the export tasks they still compare against.</summary>
    public static readonly TimeSpan TaskRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Export tasks a later step still reads: the latest diffable export of every release locale (WriteDiff compares the
    /// next release against it) and the children of unfinished batches (FinalizeRelease reads them).
    /// </summary>
    private HashSet<string> RetainedTasks()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in Releases().Where(r => r.Completed != null).GroupBy(r => r.Region))
            foreach (var locale in region.SelectMany(r => r.Locales.Select(l => (r.Sequence, Locale: l))).Where(p => p.Locale.ExportTask != null && p.Locale.State is "succeeded" or "partial").GroupBy(p => p.Locale.Locale))
                keep.Add(locale.MaxBy(p => p.Sequence).Locale.ExportTask!);
        foreach (var batch in Store.All<BatchInfo>("batch").Where(b => b.State is "queued" or "running"))
            foreach (var step in batch.Steps) if (step.TaskId != null) keep.Add(step.TaskId);
        return keep;
    }

    /// <summary>
    /// Removes the exports/{id}/manifest.json copies earlier versions wrote beside SQLite, and the directories they leave
    /// empty. Directories that still hold files belong to legacy, non-content-addressed records and are kept.
    /// </summary>
    private int RemoveManifestCopies()
    {
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Config.DataDir, "exports")))
        {
            shutdown.Token.ThrowIfCancellationRequested();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
            var copy = Path.Combine(directory, "manifest.json");
            if (File.Exists(copy)) { File.Delete(copy); removed++; }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        return removed;
    }

    /// <summary>Storage housekeeping after the orphan sweep: manifest copies, slim file records and old tasks.</summary>
    private void MaintainStorage()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var copies = RemoveManifestCopies();
        var slimmed = Store.SlimFileRecords(shutdown.Token);
        var tasks = Store.PruneTasks(Now - (long)TaskRetention.TotalSeconds, RetainedTasks());
        Console.Error.WriteLine($"[startup] storage maintenance removed {copies} manifest copies, slimmed {slimmed} file records, pruned {tasks} tasks in {clock.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>Administrative: waits for startup maintenance, then compacts SQLite (see Store.Compact).</summary>
    public TaskInfo StartCompact() => Start("storage_compact", null, 1, async (task, token) =>
    {
        await storageSweep.WaitAsync(token);
        var (before, after) = Store.Compact();
        Console.Error.WriteLine($"[task {task.Id}] storage compact sqlite {before.ToString(CultureInfo.InvariantCulture)} -> {after.ToString(CultureInfo.InvariantCulture)} bytes");
        return task with { State = "succeeded", Completed = 1, Updated = Now };
    });
}
