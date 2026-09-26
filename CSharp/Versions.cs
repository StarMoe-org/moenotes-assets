using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

// Version tracking: each tracked region's resource version comes from the metadata service (version_url, a
// current_version.json). A new resource version, or new CDN roots for it, is a release: one batch refreshes every
// language from the entry's cdnRoot and exports it. When the batch ends the release is finalized and data_dir/versions,
// served at /versions/, is rewritten: current_version.json (latest completed release per region), index.json (all
// releases), and per release {region}/{resource_version}/release.json and diff/{locale}.json against the previous one.
public sealed record VersionEntry(string ResourceVersion, string CdnRoot, string? ClientVersion, string? MasterVersion, string? VerifiedAt);
public sealed record VersionDiffSummary(string From, string FromSnapshot, int Added, int Removed, int Changed, int Unchanged, int Failed);
public sealed record ReleaseLocale(string Locale, string State, string? Snapshot = null, string? CatalogSha256 = null, string? ExportTask = null,
    int Total = 0, int Exported = 0, int Skipped = 0, int Failed = 0, int Reused = 0, VersionDiffSummary? Diff = null);
// State is queued until the batch ends, then succeeded, partial, failed or cancelled.
public sealed record Release(string Id, long Sequence, string Region, string MetadataRegion, string ResourceVersion, string CdnRoot,
    string? ClientVersion, string? MasterVersion, string? VerifiedAt, string BatchId, string State, long Detected, ReleaseLocale[] Locales,
    long? Completed = null, string? Previous = null);
// Action: queued, pending (already queued), current, cancelled, untracked, missing, invalid or error (see Error).
public sealed record VersionCheckRegion(string Region, string? MetadataRegion, string Action, string? ResourceVersion = null,
    string? Release = null, string? Batch = null, string? Error = null);
public sealed record VersionCheck(string Url, long Checked, VersionCheckRegion[] Regions);

public sealed record DiffEntry(string Key, string[]? Files = null, string? Error = null);
public sealed record VersionDiff(int Unchanged, DiffEntry[] Added, DiffEntry[] Changed, DiffEntry[] Removed, DiffEntry[] Failed)
{
    /// <summary>
    /// Published keys of two releases (key → export ID) compared by content: a key changed when the label, media type
    /// or sha256 of its files did. Files are the path names (AssetService.PathNamesById) that are new or changed. Keys
    /// that failed now are listed as failed rather than removed.
    /// </summary>
    public static VersionDiff Compute(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
        IReadOnlyDictionary<string, Manifest> manifests, IReadOnlyCollection<ItemResult> failed)
    {
        static string Line(PublishedFile file) => file.Label + "\n" + file.MediaType + "\n" + file.Sha256;
        HashSet<string> Lines(string id) => manifests.TryGetValue(id, out var manifest) ? manifest.Files.Select(Line).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
        string[] Names(string id, Func<PublishedFile, bool> include)
        {
            if (!manifests.TryGetValue(id, out var manifest)) return [];
            var names = AssetService.PathNamesById(manifest);
            return manifest.Files.Where(include).Select(f => names.GetValueOrDefault(f.Id)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        List<DiffEntry> added = [], changed = []; var unchanged = 0;
        foreach (var (key, id) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(key, out var old)) { added.Add(new(key, Names(id, _ => true))); continue; }
            if (old == id) { unchanged++; continue; }
            var previous = Lines(old);
            if (manifests.ContainsKey(old) && manifests.ContainsKey(id) && previous.SetEquals(Lines(id))) unchanged++;
            else changed.Add(new(key, Names(id, f => !previous.Contains(Line(f)))));
        }
        var failedKeys = failed.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var removed = before.Keys.Where(k => !after.ContainsKey(k) && !failedKeys.Contains(k)).Order(StringComparer.Ordinal).Select(k => new DiffEntry(k));
        return new(unchanged, [.. added], [.. changed], [.. removed], [.. failed.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new DiffEntry(r.Key, Error: r.Error))]);
    }
}

public sealed partial class AssetService
{
    private readonly object releaseGate = new();
    private Task versionPolling = Task.CompletedTask;
    private static readonly JsonSerializerOptions VersionJson = new(Json.Options) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string VersionsRoot => Path.Combine(Config.DataDir, "versions");
    public Release? GetRelease(string id) => Store.Get<Release>("release", id);
    private Release[] Releases() => Store.All<Release>("release").OrderBy(r => r.Sequence).ToArray();
    /// <summary>The CDN roots of the region's latest detected release, which refreshes follow instead of cdn_root.</summary>
    private string? ReleaseCdnRoot(string region) => Config.MetadataRegionFor(region) == null ? null : Releases().LastOrDefault(r => r.Region == region)?.CdnRoot;

    /// <summary>Checks version_url now and every version_poll_secs while serving; a failed check is logged and retried on the next tick.</summary>
    public void EnableVersionPolling()
    {
        if (Config.VersionUrl.Length == 0 || Config.VersionPollSecs == 0) return;
        versionPolling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Config.VersionPollSecs));
            try
            {
                do
                {
                    try { await CheckVersions(false, shutdown.Token); }
                    catch (Exception e) when (!shutdown.IsCancellationRequested) { Console.Error.WriteLine($"[versions] check failed: {e.Message}"); }
                }
                while (await timer.WaitForNextTickAsync(shutdown.Token));
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        });
    }

    /// <summary>
    /// Reads the version document and queues a release for every tracked region whose resource version or CDN roots
    /// differ from its latest release, or whose latest release failed (with <paramref name="force"/>: unless pending).
    /// </summary>
    public async Task<VersionCheck> CheckVersions(bool force = false, CancellationToken token = default)
    {
        Require(Config.VersionUrl.Length > 0, "version_url is not configured");
        var document = JsonNode.Parse(await Fetch(Config.VersionUri(), 4 << 20, token));
        var entries = document?["regions"] as JsonObject ?? throw new InvalidDataException("Version document has no regions object");
        var results = new List<VersionCheckRegion>();
        lock (releaseGate)
        {
            foreach (var region in Config.Regions.Length == 0 ? [Config.Region] : Config.Regions.Select(r => r.Id))
            {
                var name = Config.MetadataRegionFor(region);
                if (name == null) { results.Add(new(region, null, "untracked")); continue; }
                if (entries[name] is not JsonObject node) { results.Add(new(region, name, "missing", Error: $"No entry {name}")); continue; }
                VersionEntry entry;
                try { entry = ParseEntry(node); }
                catch (InvalidDataException e) { results.Add(new(region, name, "invalid", Error: e.Message)); continue; }
                var latest = Releases().LastOrDefault(r => r.Region == region);
                if (latest != null && latest.ResourceVersion == entry.ResourceVersion && latest.CdnRoot == entry.CdnRoot && (latest.State == "queued" || (!force && latest.State != "failed")))
                {
                    var action = latest.State switch { "queued" => "pending", "cancelled" => "cancelled", _ => "current" };
                    results.Add(new(region, name, action, entry.ResourceVersion, latest.Id, latest.BatchId)); continue;
                }
                // Held under releaseGate until the release is stored, so a batch that ends at once still finds it.
                var id = region + ":" + entry.ResourceVersion;
                BatchInfo batch;
                try { batch = StartBatch(new(region), entry.CdnRoot, id); }
                catch (ApiException e) { results.Add(new(region, name, "error", entry.ResourceVersion, Error: e.Message)); continue; } // queue full: next check
                Store.Put("release", id, new Release(id, batch.Sequence, region, name, entry.ResourceVersion, entry.CdnRoot, entry.ClientVersion,
                    entry.MasterVersion, entry.VerifiedAt, batch.Id, "queued", Now, []));
                Console.Error.WriteLine($"[versions] {region} resource_version {entry.ResourceVersion} ({name}, was {latest?.ResourceVersion ?? "none"}) queued batch {batch.Id} from {entry.CdnRoot}");
                results.Add(new(region, name, "queued", entry.ResourceVersion, id, batch.Id));
            }
            if (results.Any(r => r.Action == "queued")) WriteVersionFiles();
        }
        return new(Config.VersionUrl, Now, [.. results]);
    }

    /// <summary>A region entry: its resource version names directories, and each '|'-separated cdnRoot is checked like cdn_root.</summary>
    private VersionEntry ParseEntry(JsonObject entry)
    {
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 2048 ? text : null;
        var version = Text(entry["resource_version"]) ?? throw new InvalidDataException("resource_version missing");
        Require(version.Length <= 64 && char.IsAsciiLetterOrDigit(version[0]) && !version.Contains("..") && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'), "Invalid resource_version");
        var roots = (Text((entry["server"] as JsonObject)?["cdnRoot"]) ?? throw new InvalidDataException("server.cdnRoot missing")).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Require(roots.Length is > 0 and <= 4, "Invalid server.cdnRoot");
        foreach (var root in roots) _ = (Config with { CdnRoot = root }).Root();
        return new(version, string.Join('|', roots), Text(entry["client_version"]), Text(entry["version"]), Text(entry["verified_at"]));
    }

    /// <summary>Waits until the release's batch has ended and the release is finalized.</summary>
    public async Task<Release> WaitRelease(string id)
    {
        while (true)
        {
            var release = GetRelease(id) ?? throw new ApiException(404, "Release not found");
            if (release.State != "queued") return release;
            await Task.Delay(500);
        }
    }

    /// <summary>Finalizes releases whose batch ended before a restart, then rewrites the version files. Runs on the batch consumer.</summary>
    private void RecoverReleases()
    {
        foreach (var release in Store.All<Release>("release").Where(r => r.State == "queued")) FinalizeRelease(release.BatchId);
        try { lock (releaseGate) WriteVersionFiles(); }
        catch (Exception e) { Console.Error.WriteLine($"[versions] writing version files failed: {e.Message}"); }
    }

    /// <summary>
    /// After a release's batch ends: records each language's snapshot and export counts, diffs its published files against
    /// the latest earlier release that has the language, and rewrites the version files. Other batches are ignored.
    /// Runs on the batch consumer, so the next release is diffed against a finalized one.
    /// </summary>
    private void FinalizeRelease(string batchId)
    {
        try
        {
            if (shutdown.IsCancellationRequested) return;
            var batch = Store.Get<BatchInfo>("batch", batchId);
            Release? release;
            lock (releaseGate) release = batch?.Release == null ? null : GetRelease(batch.Release);
            if (batch == null || release == null || release.BatchId != batchId || release.State != "queued" || batch.State is "queued" or "running") return;
            var history = Releases().Where(r => r.Region == release.Region && r.Id != release.Id && r.Sequence < release.Sequence && r.Completed != null).Reverse().ToArray();
            var locales = new List<ReleaseLocale>();
            foreach (var steps in batch.Steps.GroupBy(s => s.Locale))
            {
                var refreshed = steps.First(s => s.Phase == "refresh"); var exported = steps.FirstOrDefault(s => s.Phase == "export");
                var task = exported?.TaskId == null ? null : GetTask(exported.TaskId);
                var snapshot = refreshed.State == "succeeded" ? refreshed.Snapshot : null;
                var locale = new ReleaseLocale(steps.Key, refreshed.State != "succeeded" ? refreshed.State : exported?.State ?? "succeeded", snapshot,
                    snapshot == null ? null : Store.Get<Snapshot>("snapshot", snapshot)?.ContentSha256, task?.Id, task?.Total ?? 0,
                    task?.Results.Count(r => r.ExportId != null) ?? 0, task?.Skipped ?? 0, task?.Results.Count(r => r.Error != null) ?? 0, task?.Reused ?? 0);
                if (task != null && snapshot != null) locale = locale with { Diff = WriteDiff(release, locale, task, history) };
                locales.Add(locale);
            }
            var state = locales.All(l => l.State == "succeeded") ? "succeeded" : locales.Any(l => l.State is "succeeded" or "partial") ? "partial"
                : batch.State == "cancelled" ? "cancelled" : "failed";
            lock (releaseGate)
            {
                if (GetRelease(release.Id) is not { State: "queued" } live || live.BatchId != batchId) return;
                release = release with { State = state, Completed = Now, Locales = [.. locales], Previous = history.FirstOrDefault(r => r.State is "succeeded" or "partial")?.ResourceVersion };
                Store.Put("release", release.Id, release);
                WriteVersionFiles();
            }
            Console.Error.WriteLine($"[versions] {release.Region} resource_version {release.ResourceVersion} {state}: " + string.Join(' ', locales.Select(l =>
                $"{l.Locale}={l.State}" + (l.Diff is { } d ? $"(+{d.Added} ~{d.Changed} -{d.Removed} !{d.Failed} vs {d.From})" : ""))));
        }
        catch (Exception e) { Console.Error.WriteLine($"[versions] finalizing batch {batchId} failed: {e.Message}"); }
    }

    /// <summary>Writes one language's diff/{locale}.json against the latest earlier release with its export, or returns null for a first release.</summary>
    private VersionDiffSummary? WriteDiff(Release release, ReleaseLocale locale, TaskInfo task, Release[] history)
    {
        var (from, previous) = history.Select(r => (r, r.Locales.FirstOrDefault(l => l.Locale == locale.Locale && l.ExportTask != null && l.State is "succeeded" or "partial")))
            .FirstOrDefault(p => p.Item2 != null);
        if (from == null || previous == null || GetTask(previous.ExportTask!) is not { } previousTask) return null;
        static Dictionary<string, string> Published(TaskInfo t) => t.Results.Where(r => r.ExportId != null).ToDictionary(r => r.Key, r => r.ExportId!, StringComparer.Ordinal);
        var before = Published(previousTask); var after = Published(task);
        // Equal export IDs (an unchanged catalog keeps its snapshot) are unchanged without reading their manifests.
        var manifests = Store.Exports(after.Where(p => before.GetValueOrDefault(p.Key) != p.Value)
            .SelectMany(p => before.TryGetValue(p.Key, out var old) ? new[] { p.Value, old } : new[] { p.Value }));
        var diff = VersionDiff.Compute(before, after, manifests, task.Results.Where(r => r.Error != null).ToArray());
        WriteStatic(Path.Combine(VersionsRoot, release.Region, release.ResourceVersion, "diff", locale.Locale + ".json"), new
        {
            schema_version = 1,
            region = release.Region,
            locale = locale.Locale,
            from = new { resource_version = from.ResourceVersion, snapshot = previous.Snapshot },
            to = new { resource_version = release.ResourceVersion, snapshot = locale.Snapshot },
            summary = new { added = diff.Added.Length, changed = diff.Changed.Length, removed = diff.Removed.Length, unchanged = diff.Unchanged, failed = diff.Failed.Length },
            diff.Added,
            diff.Changed,
            diff.Removed,
            diff.Failed,
        });
        return new(from.ResourceVersion, previous.Snapshot!, diff.Added.Length, diff.Removed.Length, diff.Changed.Length, diff.Unchanged, diff.Failed.Length);
    }

    /// <summary>Rewrites current_version.json, index.json and each completed release's release.json from the release records.</summary>
    private void WriteVersionFiles()
    {
        var releases = Releases(); if (releases.Length == 0) return;
        foreach (var release in releases.Where(r => r.Completed != null)) WriteStatic(Path.Combine(VersionsRoot, release.Region, release.ResourceVersion, "release.json"), View(release));
        var updated = Time(Now);
        WriteStatic(Path.Combine(VersionsRoot, "current_version.json"), new
        {
            schema_version = 1,
            updated_at = updated,
            regions = releases.Where(r => r.State is "succeeded" or "partial").GroupBy(r => r.Region).ToDictionary(g => g.Key, g => View(g.Last()), StringComparer.Ordinal),
            pending = releases.Where(r => r.State == "queued").GroupBy(r => r.Region).ToDictionary(g => g.Key, g => g.Select(r => new
            {
                r.ResourceVersion,
                state = Store.Get<BatchInfo>("batch", r.BatchId)?.State ?? "queued",
                detected_at = Time(r.Detected),
            }).ToArray(), StringComparer.Ordinal),
        });
        WriteStatic(Path.Combine(VersionsRoot, "index.json"), new
        {
            schema_version = 1,
            updated_at = updated,
            regions = releases.GroupBy(r => r.Region).ToDictionary(g => g.Key, g => g.Reverse().Select(View).ToArray(), StringComparer.Ordinal),
        });
    }

    private static object View(Release r) => new
    {
        r.ResourceVersion,
        r.ClientVersion,
        r.MasterVersion,
        r.MetadataRegion,
        r.CdnRoot,
        r.State,
        detected_at = Time(r.Detected),
        completed_at = r.Completed is { } completed ? Time(completed) : null,
        verified_at = r.VerifiedAt,
        r.Previous,
        release = $"/versions/{r.Region}/{r.ResourceVersion}/release.json",
        locales = r.Locales.ToDictionary(l => l.Locale, l => new
        {
            l.State,
            l.Snapshot,
            l.CatalogSha256,
            l.Total,
            l.Exported,
            l.Skipped,
            l.Failed,
            l.Reused,
            diff = l.Diff is not { } d ? null : new { d.From, d.FromSnapshot, d.Added, d.Changed, d.Removed, d.Unchanged, d.Failed, url = $"/versions/{r.Region}/{r.ResourceVersion}/diff/{l.Locale}.json" },
        }, StringComparer.Ordinal),
    };

    private static string Time(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Replaces a version file atomically; the dot-prefixed temporary file is never served.</summary>
    private static void WriteStatic(string path, object value)
    {
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, VersionJson)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
