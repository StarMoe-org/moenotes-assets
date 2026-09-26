using System.Text.Json.Nodes;
using System.Threading.Channels;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public sealed record ModelSiteRequest(string[]? Models = null, bool Force = false, string? Snapshot = null, string? Region = null, string? Locale = null);

// The Live2D model site task (docs/MODEL_SITE.md): every Live2D model of the catalog built from its bundle closure and the
// APK data (the APK's script classes and Cubism mask materials) into the site the chart site serves at /chart-site/:
// models.json, models/<id>.json and the shared assets/. A built model records a hash of its inputs, so a later build only
// builds new models and those whose bundles or APK changed.
public sealed partial class AssetService
{
    private readonly Channel<string> modelRequests = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private int pendingModelRequests;
    private Task modelRunner = Task.CompletedTask;
    private readonly SemaphoreSlim apkGate = new(1);
    private readonly Dictionary<(string, long, DateTime), string> apkHashes = [];
    public TaskInfo? LastAutomaticModelSite { get; private set; }
    private bool ModelSiteConfigured => Config.Apk.Length > 0 || Config.Playfetch.Length > 0;
    public string ApkRoot => Path.Combine(Config.DataDir, "apk");
    public string ApkDataRoot => Path.Combine(Config.DataDir, "apk-data");

    public TaskInfo StartModelSite(ModelSiteRequest request)
    {
        Require(ModelSiteConfigured, "apk or playfetch is not configured");
        var (snapshot, catalog) = GetSnapshot(request.Snapshot, request.Region, request.Locale);
        return Start("model_site", snapshot.Id, 0, async (task, token) =>
        {
            // The chart site and the model site share one directory, its assets and its index.
            await chartGate.WaitAsync(token);
            try
            {
                var apk = await EnsureApkData(token);
                Dictionary<string, JsonObject>? names = null;
                if (Config.MasterRoot.Length > 0)
                {
                    try { names = ModelSite.Names(await LoadMaster(token, ModelSite.MasterTables), snapshot.Locale); }
                    catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"[model-site] model names left out: {e.Message}"); }
                }
                var all = ModelSite.Models(catalog.Keys.Keys.Where(k => k.StartsWith(ModelSite.Live2DPrefix, StringComparison.Ordinal)));
                var unknown = request.Models?.Where(m => !all.ContainsKey(m) && !all.ContainsValue(m)).ToArray() ?? [];
                Require(unknown.Length == 0, $"Not a Live2D model of the catalog: {string.Join(", ", unknown)}");
                var selected = request.Models == null ? all : new SortedDictionary<string, string>(all.Where(p => request.Models.Contains(p.Key) || request.Models.Contains(p.Value)).ToDictionary(), StringComparer.Ordinal);
                var root = ChartSiteRoot; var results = new List<ItemResult>(); var plan = new List<(string Id, string Key, string Inputs)>();
                var renamed = 0;
                foreach (var (id, key) in selected)
                {
                    try
                    {
                        var inputs = ModelSite.Inputs(apk.Sha256, catalog.Target(key), ModelBundles(catalog, key));
                        if (ModelSite.NeedsBuild(root, id, request.Force, inputs)) plan.Add((id, key, inputs));
                        else if (names != null && ModelSite.RefreshNames(root, id, names.GetValueOrDefault(key))) renamed++;
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { results.Add(new(id, null, e.Message)); }
                }
                Console.Error.WriteLine($"[model-site] {selected.Count} models: {plan.Count} to build, {selected.Count - plan.Count - results.Count} current ({renamed} renamed), {results.Count} failed to plan; APK {apk.Sha256[..12]}");
                var gate = new object();
                task = task with { Total = results.Count + plan.Count, Completed = results.Count, Results = [.. results], Updated = Now }; Store.Put("task", task.Id, task);
                await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = Config.Workers, CancellationToken = token }, async (item, ct) =>
                {
                    ItemResult result;
                    try { await BuildModel(snapshot, catalog, item.Id, item.Key, item.Inputs, apk.Directory, names?.GetValueOrDefault(item.Key), ct); result = new(item.Id, item.Id, null); }
                    catch (Exception e) when (e is not OperationCanceledException) { result = new(item.Id, null, e.Message); Console.Error.WriteLine($"[model-site] {item.Id}: {e.Message}"); }
                    lock (gate)
                    {
                        results.Add(result);
                        task = task with { Completed = results.Count, Results = [.. results], Updated = Now }; Store.Put("task", task.Id, task);
                        if (results.Count % 20 == 0) Console.Error.WriteLine($"[model-site] {results.Count}/{task.Total} models");
                    }
                });
                var index = ChartSite.WriteIndex(root);
                Console.Error.WriteLine($"[model-site] index {index.ToJsonString()}");
                var failed = results.Count(r => r.Error != null);
                return task with { Completed = results.Count, Results = [.. results], State = failed == 0 ? "succeeded" : failed < results.Count ? "partial" : "failed" };
            }
            finally { chartGate.Release(); }
        });
    }

    /// <summary>
    /// The CDN bundles of a model's closure. Its only APK-local dependency is shared_monoscripts, whose script classes
    /// come from the APK data instead.
    /// </summary>
    private static Location[] ModelBundles(Catalog catalog, string key)
    {
        var locations = catalog.Closure(key);
        var local = locations.Where(l => !Uri.TryCreate(l.Internal, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")).ToArray();
        Require(local.All(l => l.Internal.EndsWith("/shared_monoscripts.bundle", StringComparison.Ordinal)),
            $"Unsupported local dependency: {local.FirstOrDefault(l => !l.Internal.EndsWith("/shared_monoscripts.bundle", StringComparison.Ordinal))?.Internal}");
        return [.. locations.Except(local)];
    }

    private async Task BuildModel(Snapshot snapshot, Catalog catalog, string id, string key, string inputs, string apkData, JsonObject? names, CancellationToken token)
    {
        var target = catalog.Target(key); var locations = ModelBundles(catalog, key);
        var inputBytes = locations.Sum(l => l.Options!.Size);
        Require(inputBytes <= Config.ExpandedBytes, "Dependency set budget");
        using var reservation = await Budget.ReserveAsync(inputBytes + Config.InputBytes + Config.ExpandedBytes, token);
        var leases = new List<SharedWork<Download>.Lease>(); var stage = Path.Combine(Config.DataDir, "tmp", "model-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var location in locations) leases.Add(await downloadWork.Join(snapshot.Id + ":" + location.Id, ct => DownloadOne(snapshot, location, ct), token));
            Directory.CreateDirectory(stage); var output = Path.Combine(stage, "out");
            Artifact[] files;
            await workers.WaitAsync(token);
            try { files = await Processes.Worker(new(Config.ForSnapshot(snapshot), target, leases.Select(l => l.Value.Input).ToArray(), output, Mode: "live2d", ApkData: apkData), stage, token); }
            finally { workers.Release(); }
            ModelSite.Ingest(ChartSiteRoot, id, key, output, files.Select(f => f.Name), names, inputs);
        }
        finally
        {
            try { RemoveTree(stage); }
            finally { foreach (var lease in leases) await lease.DisposeAsync(); }
        }
    }

    /// <summary>
    /// The APK data directory (data_dir/apk-data/&lt;sha256&gt;), extracted from the configured `apk`, or else from the
    /// newest base.apk that playfetch pulls into data_dir/apk/ (a failed pull falls back to the APK pulled before).
    /// </summary>
    public async Task<(string Directory, string Sha256)> EnsureApkData(CancellationToken token)
    {
        await apkGate.WaitAsync(token);
        try
        {
            var apk = Config.Apk.Length > 0 ? Config.Apk : await PullApk(token);
            Require(File.Exists(apk), $"APK {apk} not found");
            var info = new FileInfo(apk); var cacheKey = (info.FullName, info.Length, info.LastWriteTimeUtc);
            if (!apkHashes.TryGetValue(cacheKey, out var sha256)) apkHashes[cacheKey] = sha256 = await Task.Run(() => ApkData.FileSha256(apk), token);
            var directory = Path.Combine(ApkDataRoot, sha256);
            if (!File.Exists(Path.Combine(directory, ApkData.Manifest)))
            {
                Directory.CreateDirectory(ApkDataRoot);
                await Task.Run(() => ApkData.Extract(apk, directory, Config), token);
                foreach (var other in Directory.GetDirectories(ApkDataRoot).Where(d => Path.GetFileName(d) != sha256)) RemoveTree(other);
                Console.Error.WriteLine($"[model-site] APK data {sha256[..12]} extracted from {Path.GetFileName(apk)}");
            }
            return (directory, sha256);
        }
        finally { apkGate.Release(); }
    }

    private async Task<string> PullApk(CancellationToken token)
    {
        var root = Path.Combine(ApkRoot, Config.ApkPackage);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(60));
            await Processes.Run(Config.Playfetch, ["pull", Config.ApkPackage, "-out-root", ApkRoot, "-mode", "split", .. Config.PlayfetchArgs], timeout.Token, 16 << 20);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (LatestApk(root) is { } previous) { Console.Error.WriteLine($"[model-site] playfetch failed ({e.Message[..Math.Min(300, e.Message.Length)]}); using {previous}"); return previous; }
            throw new InvalidDataException($"playfetch pull {Config.ApkPackage} failed: {e.Message}");
        }
        var latest = LatestApk(root) ?? throw new InvalidDataException($"playfetch pulled no base.apk into {root}");
        // Older versions are not needed again.
        foreach (var directory in Directory.GetDirectories(root).Where(d => Path.GetFullPath(d) != Path.GetDirectoryName(latest))) RemoveTree(directory);
        return latest;
    }

    /// <summary>base.apk of the highest versionCode directory under a playfetch package root that has a manifest.</summary>
    private static string? LatestApk(string root) => Directory.Exists(root)
        ? Directory.GetDirectories(root).Select(d => (Dir: Path.GetFullPath(d), Code: long.TryParse(Path.GetFileName(d), out var c) ? c : -1))
            .Where(d => d.Code >= 0 && File.Exists(Path.Combine(d.Dir, "manifest.json")) && File.Exists(Path.Combine(d.Dir, "base.apk")))
            .OrderByDescending(d => d.Code).Select(d => Path.Combine(d.Dir, "base.apk")).FirstOrDefault()
        : null;

    /// <summary>Requests the automatic builds that follow a release or a master data change: the chart site and the model site.</summary>
    private void RequestSites(string reason)
    {
        RequestChartSite(reason);
        RequestModelSite(reason);
    }

    private void RequestModelSite(string reason)
    {
        if (!ModelSiteConfigured || shutdown.IsCancellationRequested) return;
        Interlocked.Increment(ref pendingModelRequests); modelRequests.Writer.TryWrite(reason);
    }

    /// <summary>
    /// The server's first model build (serve): a new deployment builds the models its site lacks, and a restart after a
    /// missed release catches up. Models whose inputs are unchanged are only compared, not downloaded.
    /// </summary>
    public void EnableAutomaticModelSite() => RequestModelSite("service start");

    private async Task RunAutomaticModelSite()
    {
        try
        {
            while (await modelRequests.Reader.WaitToReadAsync(shutdown.Token))
            {
                var reasons = new List<string>();
                while (modelRequests.Reader.TryRead(out var reason)) reasons.Add(reason);
                try
                {
                    var task = StartModelSite(new());
                    Console.Error.WriteLine($"[model-site] automatic build {task.Id} after {string.Join(", ", reasons)}");
                    LastAutomaticModelSite = await Wait(task.Id, shutdown.Token);
                }
                catch (Exception e) when (!shutdown.IsCancellationRequested) { Console.Error.WriteLine($"[model-site] automatic build after {string.Join(", ", reasons)} failed to start: {e.Message}"); }
                finally { Interlocked.Add(ref pendingModelRequests, -reasons.Count); }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    /// <summary>Waits until every requested automatic model build has run; returns the last one (null if none ran).</summary>
    public async Task<TaskInfo?> WaitAutomaticModelSite()
    {
        while (Volatile.Read(ref pendingModelRequests) > 0) await Task.Delay(200);
        return LastAutomaticModelSite;
    }
}
