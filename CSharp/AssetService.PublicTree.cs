using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MoenotesAssets;

// Published files are also materialized as a static tree, public/{locale}/{key}/{label}{extension}, of hard links to
// their content-addressed blobs. The static file middleware serves it without touching SQLite; identical files in
// several languages share one blob.
public sealed partial class AssetService
{
    private const string TreeVersionSetting = "public_tree_version";
    private const int TreeVersion = 1;
    /// <summary>Which snapshot's export owns a key's directory, and the names it linked (stored as .export.json there).</summary>
    public sealed record PathState(string Snapshot, long Created, string[] Names);
    private readonly object treeGate = new();
    private readonly ConcurrentDictionary<string, Snapshot> snapshotsById = new(StringComparer.Ordinal);
    private int copyFallbackLogged;
    public string PublicRoot => Path.Combine(Config.DataDir, "public");
    /// <summary>False until existing exports are backfilled; until then the path fallback resolves misses from SQLite.</summary>
    public bool PublicTreeReady { get; private set; }

    public bool IsPathLocale(string locale)
    {
        var region = Config.ForRegion();
        return (region.Locales.Length == 0 ? [region.Locale] : region.Locales).Contains(locale, StringComparer.Ordinal);
    }

    /// <summary>
    /// Links a manifest's addressable files into the tree unless a newer snapshot already owns the key. Each link is
    /// created under a dot-prefixed temporary name (never served) and renamed into place atomically.
    /// </summary>
    public bool MaterializePaths(Manifest manifest)
    {
        try
        {
            if (!snapshotsById.TryGetValue(manifest.Snapshot, out var snapshot) && (snapshot = Store.Get<Snapshot>("snapshot", manifest.Snapshot)) != null)
                snapshotsById[manifest.Snapshot] = snapshot;
            var region = Config.ForRegion();
            if (snapshot == null || snapshot.Region != region.Region || snapshot.BiliVersion != region.BiliVersion || !IsPathLocale(snapshot.Locale)) return true;
            var segments = manifest.Key.Split('/');
            if (!segments.All(SafeSegment)) return true;
            var directory = Path.Combine([PublicRoot, snapshot.Locale, .. segments]);
            var files = PathFiles(manifest).Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
            lock (treeGate)
            {
                // The owner is recorded beside the files, in a dot file the static provider never serves, so the
                // tree describes itself and publication adds no SQLite commit.
                var statePath = Path.Combine(directory, ".export.json");
                var state = File.Exists(statePath) ? Json.Read<PathState>(File.ReadAllText(statePath)) : null;
                if (state != null && state.Created > snapshot.Created) return true;
                if (state == null && files.Count == 0) return true;
                Directory.CreateDirectory(directory);
                foreach (var (name, file) in files) Link(Path.Combine(directory, name), Blobs.PathFor(file.Sha256));
                foreach (var stale in (state?.Names ?? []).Except(files.Keys, StringComparer.Ordinal)) File.Delete(Path.Combine(directory, stale));
                var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temp, Json.Write(new PathState(snapshot.Id, snapshot.Created, files.Keys.Order(StringComparer.Ordinal).ToArray())));
                File.Move(temp, statePath, true);
            }
            return true;
        }
        catch (Exception error)
        {
            // Serving still works through /files/{id}; the next start backfills the tree again.
            Console.Error.WriteLine($"[paths] materialize {manifest.Key} failed: {error.Message}");
            Store.Put("setting", TreeVersionSetting, 0);
            return false;
        }
    }

    /// <summary>Materializes every published export once per tree version, in pages, without blocking startup.</summary>
    public Task BackfillPublicTree()
    {
        if (Store.Get<int>("setting", TreeVersionSetting) == TreeVersion) { PublicTreeReady = true; return Task.CompletedTask; }
        return Task.Run(() =>
        {
            var clock = Stopwatch.StartNew(); var count = 0; var failed = 0; string after = "";
            try
            {
                for (; ; )
                {
                    var page = Store.ExportPage(after, 500);
                    if (page.Length == 0) break;
                    foreach (var manifest in page) { if (!MaterializePaths(manifest)) failed++; count++; }
                    after = page[^1].Id;
                    if (shutdown.IsCancellationRequested) return;
                }
                if (failed == 0) Store.Put("setting", TreeVersionSetting, TreeVersion);
                Console.Error.WriteLine($"[paths] backfilled {count} exports in {clock.Elapsed.TotalSeconds:F1}s ({failed} failed)");
            }
            catch (Exception error) { Console.Error.WriteLine($"[paths] backfill stopped after {count} exports: {error.Message}"); }
            finally { PublicTreeReady = true; }
        });
    }

    private void Link(string path, string blob)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            if (!HardLink.TryCreate(temp, blob))
            {
                if (Interlocked.Exchange(ref copyFallbackLogged, 1) == 0)
                    Console.Error.WriteLine($"[paths] hard links unavailable (error {Marshal.GetLastPInvokeError()}); copying files instead");
                File.Copy(blob, temp);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>A key segment or file name that is safe on disk and visible to the static file provider.</summary>
    public static bool SafeSegment(string segment) =>
        segment.Length is > 0 and <= 255 && segment[0] != '.' && segment.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !segment.Any(char.IsControl);

    private static class HardLink
    {
        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int LinkUnix(string existing, string link);
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LinkWindows(string link, string existing, IntPtr security);
        public static bool TryCreate(string link, string existing)
        {
            try { return OperatingSystem.IsWindows() ? LinkWindows(link, existing, IntPtr.Zero) : LinkUnix(existing, link) == 0; }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}
