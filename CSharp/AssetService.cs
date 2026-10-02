using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public sealed partial class AssetService : IAsyncDisposable
{
    public Config Config { get; }
    public Store Store { get; }
    public Budget Budget { get; }
    public BlobStore Blobs { get; }
    private readonly FileStream instance;
    private readonly HttpClient http;
    private readonly SemaphoreSlim downloads, workers, videos, queue, refresh = new(1);
    private readonly SharedWork<Download> downloadWork;
    private readonly SharedWork<Manifest> exportWork = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<string, Task> running = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, WeakReference<SemaphoreSlim>> publicationGates = new();
    private readonly string classDataIdentity;
    // Held from a publication's first move into blobs/ until its SQLite commit; see SweepStorage.
    private readonly object storageGate = new();
    private Task storageSweep = Task.CompletedTask;
    private sealed record Download(WorkerInput Input, string Hash, string PlainHash, string Directory);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public AssetService(Config config)
    {
        config.Validate(); Config = config with { DataDir = Path.GetFullPath(config.DataDir) };
        classDataIdentity = Config.ClassData.Length == 0 ? "embedded" : Crypto.Sha256(File.ReadAllBytes(Config.ClassData));
        Directory.CreateDirectory(Config.DataDir);
        Require(!System.IO.File.Exists(Path.Combine(Config.DataDir, "index.sqlite")), "Legacy Rust storage detected. Configure a new data_dir; automatic import is not supported.");
        instance = new FileStream(Path.Combine(Config.DataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var name in new[] { "tmp", "exports", "catalogs", "blobs", "public", "regions", "versions" })
            {
                var path = Path.Combine(Config.DataDir, name);
                Require(!Directory.Exists(path) || !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "Symlink storage directory");
                Directory.CreateDirectory(path);
            }
            // Startup is serial and precedes listening; log phases so slow volumes show where time goes. Orphaned blobs
            // and export directories are never served, so SweepStorage removes them beside the listener instead.
            var clock = System.Diagnostics.Stopwatch.StartNew(); var phases = new List<string>();
            void Phase(string name) { phases.Add($"{name}={clock.Elapsed.TotalSeconds:F1}s"); clock.Restart(); }
            Store = new Store(Config.DataDir); Phase("store");
            Blobs = new BlobStore(Config.DataDir);
            foreach (var scope in Store.All<Snapshot>("snapshot").GroupBy(s => Store.ScopeSetting(s.Region, s.Locale, s.BiliVersion)))
            {
                var preferred = Store.Get<string>("setting", scope.Key) ?? scope.OrderBy(s => s.Created).Last().Id;
                foreach (var retained in scope)
                    if (!Store.HasCatalogIndex(retained.Id)) Store.IndexSnapshot(retained, Store.HasCatalogContent(retained.ContentSha256) ? null : Catalog.Parse(File.ReadAllBytes(CatalogPath(retained))), retained.Id == preferred);
            }
            Phase("catalogs");
            foreach (var task in Store.UnfinishedTasks())
                Store.Put("task", task.Id, task with { State = "failed", Error = "Interrupted by service restart; resubmit failed keys", Updated = Now });
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(Config.DataDir, "tmp"))) RemoveTree(path);
            Phase("tmp");
            Console.Error.WriteLine($"[startup] storage recovery {string.Join(' ', phases)}");
            http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
            downloads = new(config.Downloads); workers = new(config.Workers); videos = new(config.Videos); queue = new(config.QueueLimit);
            Budget = new(config.TempBytes);
            downloadWork = new(d => RemoveTree(d.Directory));
            InitializeBatches();
            chartRunner = Task.Run(RunAutomaticChartSite);
            modelRunner = Task.Run(RunAutomaticModelSite);
        }
        catch { Store?.Dispose(); instance.Dispose(); throw; }
    }
    public static void RemoveTree(string path)
    {
        if (!Path.Exists(path)) return;
        if (Directory.Exists(path)) Directory.Delete(path, !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        else File.Delete(path);
    }
    /// <summary>
    /// Removes blobs and exports/{id} directories that an interrupted publication left without their SQLite commit.
    /// They are never served, so this runs beside the listener: listing a large blob tree on a network volume takes
    /// about a minute. Candidates are rechecked under storageGate, so a concurrent publication is never removed.
    /// </summary>
    public Task SweepStorage() => storageSweep = Task.Run(() =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            MaintainStorage();
            clock.Restart();
            var blobs = Sweep(Store.ReferencedBlobs, Blobs.Files(), File.Delete);
            var exports = Sweep(() => Store.Ids("export"), Directory.EnumerateDirectories(Path.Combine(Config.DataDir, "exports")), RemoveTree);
            Console.Error.WriteLine($"[startup] storage sweep removed {blobs} orphan blobs and {exports} unpublished exports in {clock.Elapsed.TotalSeconds:F1}s");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Console.Error.WriteLine($"[startup] storage sweep stopped: {error.Message}"); }
    });
    private int Sweep(Func<HashSet<string>> committed, IEnumerable<string> paths, Action<string> remove)
    {
        var known = committed(); var candidates = new List<string>();
        foreach (var path in paths)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            if (!known.Contains(Path.GetFileName(path))) candidates.Add(path);
        }
        if (candidates.Count == 0) return 0;
        lock (storageGate)
        {
            // Anything published since the first read committed before this lock was taken.
            known = committed();
            var orphans = candidates.Where(p => !known.Contains(Path.GetFileName(p))).ToArray();
            foreach (var path in orphans) remove(path);
            return orphans.Length;
        }
    }
    public async Task CheckMedia(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var encoders = await Processes.Run(Config.Ffmpeg, ["-hide_banner", "-encoders"], timeout.Token);
        Require(encoders.Contains("libx264") && encoders.Split('\n').Any(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == "aac"), "FFmpeg AAC and libx264 encoders required");
        await Processes.Run(Config.Ffprobe, ["-version"], timeout.Token);
    }
    public Snapshot ResolveSnapshot(string? id = null, string? region = null, string? locale = null)
    {
        if (id == null)
        {
            var selected = Config.ForRegion(region, locale);
            id = Store.CurrentSnapshot(selected.Region, selected.Locale, selected.BiliVersion);
        }
        var snapshot = id == null ? null : Store.Get<Snapshot>("snapshot", id);
        if (snapshot == null) throw new ApiException(404, "Catalog snapshot not found; refresh first");
        Require((region == null || region == snapshot.Region) && (locale == null || locale == snapshot.Locale), "Snapshot does not match region/locale");
        return snapshot;
    }
    private string CatalogPath(Snapshot snapshot)
    {
        var path = Path.Combine(Config.DataDir, "catalogs", snapshot.ContentSha256 + ".bin");
        return File.Exists(path) ? path : Path.Combine(Config.DataDir, "catalogs", snapshot.Id + ".bin");
    }
    public (Snapshot Snapshot, Catalog Catalog) GetSnapshot(string? id = null, string? region = null, string? locale = null)
    {
        var snapshot = ResolveSnapshot(id, region, locale);
        return (snapshot, Catalog.Parse(File.ReadAllBytes(CatalogPath(snapshot))));
    }
    public CatalogStats[] ListCatalogs(string? region = null, string? locale = null) => Store.Catalogs(region, locale);
    public object ListAssets(string? snapshot, string? prefix, string? type, int offset, int limit, string? region = null, string? locale = null, string? bundle = null)
        => Store.Assets(ResolveSnapshot(snapshot, region, locale).Id, prefix, type, bundle, offset, limit);
    public BundlePage ListBundles(string? snapshot, string? region, string? locale, string? prefix, int offset, int limit)
        => Store.Bundles(ResolveSnapshot(snapshot, region, locale).Id, prefix, offset, limit);
    public string FilePath(FileRecord record) => record.BlobSha256 != null ? Blobs.PathFor(record.BlobSha256) : Path.Combine(Config.DataDir, "exports", record.ExportId, record.File.Name);
    public TaskInfo? GetTask(string id) => Store.Get<TaskInfo>("task", id);
    public TaskInfo Cancel(string id)
    {
        var task = GetTask(id) ?? throw new ApiException(404, "Task not found");
        if (cancellations.TryGetValue(id, out var token)) { try { token.Cancel(); } catch (ObjectDisposedException) { } }
        return task;
    }
    private TaskInfo Start(string kind, string? snapshot, int total, Func<TaskInfo, CancellationToken, Task<TaskInfo>> operation)
    {
        if (shutdown.IsCancellationRequested) throw new ApiException(503, "Service shutting down");
        if (!queue.Wait(0)) throw new ApiException(429, "Task queue full");
        var task = new TaskInfo(Guid.NewGuid().ToString("N"), kind, "queued", snapshot, total, 0, [], null, Now, Now);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        try { Store.Put("task", task.Id, task); cancellations[task.Id] = cancellation; }
        catch { cancellation.Dispose(); queue.Release(); throw; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running[task.Id] = completion.Task;
        Console.Error.WriteLine($"[task {task.Id}] queued kind={kind} total={total}");
        _ = Task.Run(async () =>
        {
            var result = task with { State = "running", Updated = Now };
            try { Store.Put("task", task.Id, result); Console.Error.WriteLine($"[task {task.Id}] running kind={kind}"); result = await operation(result, cancellation.Token); }
            catch (Exception e) { result = result with { State = cancellation.IsCancellationRequested ? "cancelled" : "failed", Error = e.Message }; }
            finally
            {
                try { Store.Put("task", task.Id, result with { Updated = Now }); }
                catch (Exception e) { Console.Error.WriteLine($"Task persistence failed: {e.Message}"); }
                Console.Error.WriteLine($"[task {task.Id}] {result.State} kind={kind} progress={result.Completed}/{result.Total} skipped={result.Skipped} failed={result.Results.Count(r => r.Error != null)}");
                cancellations.TryRemove(task.Id, out _); cancellation.Dispose(); queue.Release();
                running.TryRemove(task.Id, out _); completion.SetResult();
            }
        });
        return task;
    }
    public async Task<TaskInfo> Wait(string id, CancellationToken token = default)
    {
        if (running.TryGetValue(id, out var work)) await work.WaitAsync(token);
        return GetTask(id) ?? throw new ApiException(404, "Task not found");
    }
    /// <summary>
    /// Refreshes one catalog from <paramref name="cdnRoots"/> ("a|b": mirrors tried in order), else from the region's
    /// latest detected release (Versions.cs), else from the configured cdn_root. The snapshot records the root that answered.
    /// </summary>
    public TaskInfo StartRefresh(string? region = null, string? locale = null, string? version = null, string? cdnRoots = null, JpAssetSource? assets = null, string? catalogVersion = null) => Start("catalog_refresh", null, 1, async (task, token) =>
    {
        await refresh.WaitAsync(token);
        try
        {
            var selected = Config.ForRegion(region, locale, version);
            assets ??= ReleaseAssets(selected.Region);
            // Keep the configured browsing scope (usually main), while pinning the
            // actual catalog filename to the release being downloaded.
            catalogVersion ??= assets == null && version == null ? ReleaseCatalogVersion(selected.Region) : null;
            Require(assets != null || Config.MetadataRegionFor(selected.Region) != "jp" && selected.Region != "jp", "Discover JP assets with a version check before refreshing");
            if (assets != null) { assets.Validate(Config); Require(selected.Locale is "" or "ja", "JP requires locale ja or empty"); }
            var roots = (cdnRoots ?? ReleaseCdnRoot(selected.Region) ?? selected.CdnRoot).Split('|');
            string hash; byte[] bytes;
            for (var i = 0; ; i++)
            {
                selected = selected with { CdnRoot = roots[i] };
                try
                {
                    if (assets != null)
                    {
                        hash = assets.Hash;
                        selected = selected with { CdnRoot = Config.JpCdnOrigin.TrimEnd('/') };
                        bytes = await Fetch(new Uri(assets.CatalogUrl), 32 << 20, token, assets);
                    }
                    else
                    {
                        hash = new UTF8Encoding(false, true).GetString(await Fetch(selected.CatalogUri("hash", catalogVersion), 65536, token)).Trim();
                        Require(hash.Length <= 128, "Invalid catalog hash");
                        bytes = await Fetch(selected.CatalogUri("bin", catalogVersion), 32 << 20, token);
                    }
                    break;
                }
                catch (Exception e) when (i + 1 < roots.Length && !token.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[refresh] {selected.Region}/{selected.Locale} {roots[i]} failed ({e.Message}); trying {roots[i + 1]}");
                }
            }

            var digest = Crypto.Sha256(bytes);
            var id = Crypto.Identity(selected.Region, selected.Locale, selected.BiliVersion, selected.CdnRoot, digest);
            if (assets != null) id = Crypto.Identity(id, assets.Version, assets.Hash, assets.BundleRoot);
            else if (catalogVersion != null) id = Crypto.Identity(id, catalogVersion);
            var snapshot = new Snapshot(id, digest, selected.Region, selected.Locale, selected.BiliVersion, selected.CdnRoot, hash, Now, assets, catalogVersion);
            var target = Path.Combine(Config.DataDir, "catalogs", digest + ".bin");
            token.ThrowIfCancellationRequested();
            if (!File.Exists(target))
            {
                var temporary = target + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, token); File.Move(temporary, target, true);
            }
            Store.IndexSnapshot(snapshot, Store.HasCatalogContent(digest) ? null : Catalog.Parse(bytes));
            ContinueAutomaticBundleScan();
            return task with { State = "succeeded", Snapshot = id, Completed = 1 };
        }
        finally { refresh.Release(); }
    });
    private async Task<byte[]> Fetch(Uri uri, int limit, CancellationToken token, JpAssetSource? assets = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Config.DownloadTimeoutSecs));
        using var response = await SendAsset(uri, assets, true, timeout.Token);
        Require(response.StatusCode == HttpStatusCode.OK, $"CDN HTTP {(int)response.StatusCode}");
        Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= limit, "Response size limit");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(); var buffer = new byte[65536]; int n;
        while ((n = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            Require(output.Length + n <= limit, "Response size limit"); output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
    public TaskInfo StartExport(ExportRequest request)
    {
        Require((request.Keys is { Length: > 0 }) != (request.Prefix != null), "Provide either nonempty keys or prefix");
        var (snapshot, catalog) = GetSnapshot(request.Snapshot, request.Region, request.Locale);
        var keys = (request.Prefix != null ? catalog.Keys.Keys.Where(k => k.StartsWith(request.Prefix, StringComparison.Ordinal)).Take(Config.MaxKeys + 1) : request.Keys!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Require(keys.Length > 0 && keys.Length <= Config.MaxKeys && keys.All(k => k != null && k.Length <= 4096), "Empty or excessive selection");
        if (request.Prefix != null) keys = ExportSelection.UniqueKeys(catalog, keys);
        return Start("export", snapshot.Id, keys.Length, async (task, token) =>
        {
            var fallbacks = FallbackSnapshots(snapshot);
            // Skipped items are only counted: listing every unsupported object made progress documents megabytes.
            var results = new List<ItemResult>(); int completed = 0, skipped = 0, reused = 0, unchanged = 0, succeeded = 0;
            var progress = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = Config.Downloads, CancellationToken = token }, async (key, ct) =>
                {
                    ItemResult item;
                    try
                    {
                        var skip = ExportSelection.SkipReason(catalog, key);
                        if (skip != null) item = new(key, null, null, skip);
                        else
                        {
                            var id = Crypto.Identity(snapshot.Id, key, Worker.ProfileFor(catalog.Target(key)));
                            var manifest = Store.Get<Manifest>("export", id);
                            if (manifest == null && ServedUnchanged(snapshot, catalog, key, fallbacks) is { } served) item = new(key, served.Id, null, Unchanged: true);
                            else
                            {
                                if (manifest == null)
                                {
                                    using var lease = await exportWork.Join(id, t => ExportOne(snapshot, catalog, key, id, t), ct);
                                    manifest = lease.Value;
                                }
                                item = new(key, manifest.Id, null, Reused: manifest.ReusedFrom != null);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (UnsupportedInputException e) { item = new(key, null, null, e.Message); }
                    catch (Exception e) { item = new(key, null, e.Message); }
                    lock (results)
                    {
                        completed++;
                        if (item.SkipReason != null) skipped++; else results.Add(item);
                        if (item.ExportId != null) succeeded++;
                        if (item.Reused) reused++;
                        if (item.Unchanged) unchanged++;
                        if ((completed % 20 == 0 && progress.Elapsed >= TimeSpan.FromSeconds(1)) || progress.Elapsed >= TimeSpan.FromSeconds(10))
                        {
                            // Checkpoints carry counters and failures only; the full per-key results (megabytes for a whole
                            // catalog) are written once when the task ends, where release diffs read them.
                            task = task with { Completed = completed, Skipped = skipped, Reused = reused, Unchanged = unchanged, Results = results.Where(r => r.Error != null).Take(100).ToArray(), Updated = Now };
                            Store.Put("task", task.Id, task);
                            Console.Error.WriteLine($"[task {task.Id}] export region={snapshot.Region} locale={snapshot.Locale} progress={completed}/{keys.Length} succeeded={succeeded} skipped={skipped} unchanged={unchanged} reused={reused} failed={results.Count - succeeded}");
                            progress.Restart();
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            Console.Error.WriteLine($"[task {task.Id}] export region={snapshot.Region} locale={snapshot.Locale} done keys={keys.Length} unchanged={unchanged} skipped={skipped} published={succeeded - unchanged} reused={reused} failed={results.Count - succeeded}");
            return task with { Completed = completed, Skipped = skipped, Reused = reused, Unchanged = unchanged, Results = results.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray(), State = token.IsCancellationRequested ? "cancelled" : succeeded + skipped == keys.Length ? "succeeded" : succeeded > 0 ? "partial" : "failed" };
        });
    }
    private async Task<Download> DownloadOne(Snapshot snapshot, Location location, CancellationToken token)
    {
        await downloads.WaitAsync(token);
        string? directory = null;
        try
        {
            var options = location.Options ?? throw new InvalidDataException("Missing bundle options");
            Require(options.Size > 0 && options.Size <= Config.InputBytes, "Input size budget");
            directory = Path.Combine(Config.DataDir, "tmp", "download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "payload");
            var uri = snapshot.Assets?.AssetUri(location.Internal) ?? (Config with { CdnRoot = snapshot.CdnRoot }).AssetUri(location.Internal);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Config.DownloadTimeoutSecs));
            using var response = await SendAsset(uri, snapshot.Assets, false, timeout.Token);
            Require(response.StatusCode == HttpStatusCode.OK, $"CDN HTTP {(int)response.StatusCode}");
            Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == options.Size, "Content-Length mismatch");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long received = 0; int n;
            var name = location.Internal.Split('/').Last();
            var encrypted = location.Provider == Catalog.Crypt && !Crypto.Builtin(name);
            while ((n = received == 0 ? await source.ReadAtLeastAsync(buffer, (int)Math.Min(8, options.Size), false, timeout.Token) : await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                Require(received + n <= options.Size, "Download exceeds catalog size");
                hash.AppendData(buffer, 0, n);
                if (received == 0 && buffer.AsSpan(0, n).StartsWith("UnityFS\0"u8)) encrypted = false;
                if (encrypted) Crypto.Decrypt(buffer.AsSpan(0, n), name, received);
                plainHash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token); received += n;
            }
            Require(received == options.Size, "Truncated download");
            await output.FlushAsync(timeout.Token);
            var rawDigest = Convert.ToHexStringLower(hash.GetHashAndReset()); var plainDigest = Convert.ToHexStringLower(plainHash.GetHashAndReset());
            Store.Observe(snapshot.Id, location, rawDigest, plainDigest);
            return new(new(location, path), rawDigest, plainDigest, directory);
        }
        catch { if (directory != null) RemoveTree(directory); throw; }
        finally { downloads.Release(); }
    }
    private async Task<Manifest> ExportOne(Snapshot snapshot, Catalog catalog, string key, string id, CancellationToken token)
    {
        // A cancelled producer must finish cleanup before a retry can publish the same identity.
        SemaphoreSlim publication;
        lock (publicationGates)
        {
            foreach (var expired in publicationGates.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToArray()) publicationGates.Remove(expired);
            if (!publicationGates.TryGetValue(id, out var reference) || !reference.TryGetTarget(out publication!))
            {
                publication = new(1); publicationGates[id] = new(publication);
            }
        }
        await publication.WaitAsync(token);
        SemaphoreSlim? conversionGate = null; var conversionHeld = false;
        var leases = new List<SharedWork<Download>.Lease>(); string? stage = null;
        IDisposable? reservation = null;
        try
        {
            var existing = Store.Get<Manifest>("export", id); if (existing != null) return existing;
            var (target, profile, locations) = ExportInputs(catalog, key);
            Require(locations.Sum(l => l.Options!.Size) <= Config.ExpandedBytes, "Dependency set budget");
            // Manifests live only in SQLite: the exports/{id}/manifest.json copies were never read.
            Manifest Reuse(Manifest previous, Source[] sources)
            {
                Require(previous.Files.Sum(f => f.Bytes) <= Config.OutputBytes, "Reused output size budget");
                var copied = previous.Files.Select(f => f with { Id = Crypto.Identity(id, f.Name), Label = f.MediaType == "video/mp4" ? target.Key : f.Label }).ToArray();
                var reused = new Manifest(id, snapshot.Id, key, profile, sources, copied, snapshot.Region, previous.Id);
                token.ThrowIfCancellationRequested();
                lock (storageGate) Store.Publish(reused, true);
                MaterializePaths(reused); return reused;
            }
            // A new catalog version usually keeps most bundles byte-identical. When every
            // dependency has the same catalog identity as an earlier export, reuse it without downloading.
            if (FindUnchangedExport(snapshot, key, target, profile, locations) is { } unchanged)
            {
                foreach (var source in unchanged.Sources) Store.Observe(snapshot.Id, source.Location, source.DownloadSha256, source.PlainSha256!);
                return Reuse(unchanged.Previous, unchanged.Sources);
            }
            // Admit the complete dependency set and workspace atomically. Waiting
            // while holding partial downloads could otherwise deadlock the budget.
            reservation = await Budget.ReserveAsync(locations.Sum(l => l.Options!.Size) + Config.OutputBytes + Config.ExpandedBytes * 2, token);
            foreach (var location in locations)
                leases.Add(await downloadWork.Join(snapshot.Id + ":" + location.Id, ct => DownloadOne(snapshot, location, ct), token));
            var selectedConfig = Config.ForSnapshot(snapshot);
            var cri = locations.Length == 1 && locations[0].Provider == Catalog.Cri;
            var conversionId = Crypto.Identity(profile, cri ? "cri" : target.Internal, cri ? "cri" : target.ResourceType,
                selectedConfig.CriKey.ToString(System.Globalization.CultureInfo.InvariantCulture), classDataIdentity,
                string.Join(',', leases.Select(l => l.Value.PlainHash).Order(StringComparer.Ordinal)));
            lock (publicationGates)
            {
                var gateKey = "conversion:" + conversionId;
                if (!publicationGates.TryGetValue(gateKey, out var reference) || !reference.TryGetTarget(out conversionGate))
                { conversionGate = new(1); publicationGates[gateKey] = new(conversionGate); }
            }
            await conversionGate.WaitAsync(token); conversionHeld = true;
            var previousId = Store.Get<string>("conversion", conversionId);
            var previous = previousId == null ? null : Store.Get<Manifest>("export", previousId);
            if (previous != null && BlobsPresent(previous))
                return Reuse(previous, leases.Select(l => new Source(l.Value.Input.Location, l.Value.Hash, l.Value.PlainHash)).ToArray());
            stage = Path.Combine(Config.DataDir, "tmp", "job-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            var output = Path.Combine(stage, "out");
            await workers.WaitAsync(token);
            Artifact[] files;
            try
            {
                var video = false;
                foreach (var lease in leases.Where(l => l.Value.Input.Location.Provider == Catalog.Cri))
                {
                    using var raw = System.IO.File.OpenRead(lease.Value.Input.Path); var magic = new byte[4]; raw.ReadExactly(magic);
                    video |= magic.AsSpan().SequenceEqual("CRID"u8);
                }
                if (video) await videos.WaitAsync(token);
                try { files = await Processes.Worker(new(Config.ForSnapshot(snapshot), target, leases.Select(l => l.Value.Input).ToArray(), output), stage, token); }
                finally { if (video) videos.Release(); }
            }
            finally { workers.Release(); }
            long total = 0;
            foreach (var file in files)
            {
                Require(file.Name == Path.GetFileName(file.Name) && !file.Name.Contains('\\') && !file.Name.Contains('/') && file.Name is not "." and not "..", "Invalid output name");
                var path = Path.Combine(output, file.Name);
                Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "Symlink output");
                total += new FileInfo(path).Length;
                Require(total <= Config.OutputBytes && new FileInfo(path).Length == file.Bytes, "Invalid output size");
                await using var stream = File.OpenRead(path);
                Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) == file.Sha256, "Output hash mismatch");
            }
            var published = files.Select(f => new PublishedFile(Crypto.Identity(id, f.Name), f.Name, f.Label, f.MediaType, f.Bytes, f.Sha256, f.Metadata)).ToArray();
            var manifest = new Manifest(id, snapshot.Id, key, profile, leases.Select(l => new Source(l.Value.Input.Location, l.Value.Hash, l.Value.PlainHash)).ToArray(), published, snapshot.Region);
            token.ThrowIfCancellationRequested();
            lock (storageGate)
            {
                foreach (var file in files) Blobs.Publish(Path.Combine(output, file.Name), file.Sha256, file.Bytes);
                Store.Publish(manifest, true);
            }
            MaterializePaths(manifest);
            Store.Put("conversion", conversionId, manifest.Id); return manifest;
        }
        finally
        {
            try { if (stage != null) RemoveTree(stage); }
            finally
            {
                try { foreach (var lease in leases) await lease.DisposeAsync(); }
                finally { reservation?.Dispose(); if (conversionHeld) conversionGate!.Release(); publication.Release(); }
            }
        }
    }
    private bool BlobsPresent(Manifest manifest) =>
        manifest.Files.All(f => File.Exists(Blobs.PathFor(f.Sha256)) && new FileInfo(Blobs.PathFor(f.Sha256)).Length == f.Bytes);
    /// <summary>
    /// Finds an earlier export of the same key whose dependencies have identical catalog identities
    /// (internal path, provider, hash, CRC, size), so their bytes and plain hashes are known without a download.
    /// The conversion identity is recomputed for this snapshot's config, so a CRI key or class data change still converts.
    /// </summary>
    private (Manifest Previous, Source[] Sources)? FindUnchangedExport(Snapshot snapshot, string key, Location target, string profile, Location[] locations)
    {
        if (Wanted(locations) is not { } wanted) return null;
        foreach (var candidate in Store.ExportsForKey(key))
        {
            if (candidate.Snapshot == snapshot.Id || MatchSources(candidate, profile, wanted) is not { } sources) continue;
            var previousId = Store.Get<string>("conversion", ConversionId(snapshot, target, profile, locations, sources));
            var previous = previousId == null ? null : Store.Get<Manifest>("export", previousId);
            if (previous != null && BlobsPresent(previous)) return (previous, sources);
        }
        return null;
    }
    /// <summary>The export target, worker profile and downloaded dependency set of a key.</summary>
    private static (Location Target, string Profile, Location[] Locations) ExportInputs(Catalog catalog, string key)
    {
        var target = catalog.Target(key); var locations = ExportSelection.Dependencies(catalog, key);
        if (target.Provider == Catalog.Cri || target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal))
        {
            var raw = locations.Where(l => l.Provider == Catalog.Cri).ToArray();
            Require(raw.Length <= 1, "Ambiguous CRI dependencies");
            if (raw.Length == 1) locations = raw; // Otherwise the CRI bytes are embedded in the Unity asset.
        }
        return (target, Worker.ProfileFor(target), locations);
    }
    /// <summary>Dependencies by bundle identity, or null when one lacks the catalog hash/CRC that makes the identity content-bound.</summary>
    private static Dictionary<string, Location>? Wanted(Location[] locations)
    {
        if (locations.Length == 0 || locations.Any(l => l.Options == null || BundleIdentity.Candidate(l) == null)) return null;
        var wanted = new Dictionary<string, Location>(StringComparer.Ordinal);
        foreach (var location in locations) if (!wanted.TryAdd(BundleIdentity.Id(location), location)) return null;
        return wanted;
    }
    /// <summary>The manifest's sources relocated onto <paramref name="wanted"/>, when both name exactly the same bundles.</summary>
    private static Source[]? MatchSources(Manifest manifest, string profile, Dictionary<string, Location> wanted)
    {
        if (manifest.Profile != profile || manifest.Sources.Length != wanted.Count) return null;
        var sources = new Source[manifest.Sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var source = manifest.Sources[i];
            if (source.PlainSha256 == null || source.Location.Options == null || !wanted.TryGetValue(BundleIdentity.Id(source.Location), out var current)) return null;
            sources[i] = source with { Location = current };
        }
        return sources;
    }
    private string ConversionId(Snapshot snapshot, Location target, string profile, Location[] locations, IEnumerable<Source> sources)
    {
        var cri = locations.Length == 1 && locations[0].Provider == Catalog.Cri;
        return Crypto.Identity(profile, cri ? "cri" : target.Internal, cri ? "cri" : target.ResourceType,
            Config.ForSnapshot(snapshot).CriKey.ToString(System.Globalization.CultureInfo.InvariantCulture), classDataIdentity,
            string.Join(',', sources.Select(s => s.PlainSha256!).Order(StringComparer.Ordinal)));
    }
    /// <summary>
    /// Older snapshots of the scope that path routes fall back to after <paramref name="snapshot"/>, newest first.
    /// Empty unless the snapshot is its scope's current one: only then is "what an earlier snapshot serves" what the routes serve.
    /// </summary>
    private string[] FallbackSnapshots(Snapshot snapshot)
    {
        if (Store.CurrentSnapshot(snapshot.Region, snapshot.Locale, snapshot.BiliVersion) != snapshot.Id) return [];
        return Store.ScopeSnapshots(snapshot.Region, snapshot.Locale, snapshot.BiliVersion).Where(s => s != snapshot.Id).ToArray();
    }
    /// <summary>
    /// The export that path routes already serve for <paramref name="key"/> from an earlier snapshot of the scope,
    /// when its dependencies are byte-identical to this catalog's and it was converted under the current config.
    /// Such a key needs no new manifest: the routes fall back to it and the version diff sees an unchanged export ID.
    /// </summary>
    private Manifest? ServedUnchanged(Snapshot snapshot, Catalog catalog, string key, string[] fallbacks)
    {
        if (fallbacks.Length == 0) return null;
        var (target, profile, locations) = ExportInputs(catalog, key);
        if (Wanted(locations) is not { } wanted) return null;
        // The first match is what ResolvePath returns; an older identical export behind a changed one is not served.
        var served = Store.FirstExport(fallbacks.SelectMany(s => new[] { Crypto.Identity(s, key, Worker.Profile), Crypto.Identity(s, key, Worker.MovieProfile) }).ToArray());
        if (served == null || MatchSources(served, profile, wanted) is not { } sources) return null;
        // A CRI key or class data change maps the same inputs to another conversion, so the key converts again.
        var canonical = Store.Get<string>("conversion", ConversionId(snapshot, target, profile, locations, sources));
        return canonical != null && (canonical == served.Id || canonical == served.ReusedFrom) ? served : null;
    }
    public TaskInfo StartVerify(VerifyRequest request)
    {
        var snapshot = ResolveSnapshot(request.Snapshot, request.Region, request.Locale);
        Require(request.Ids is { Length: > 0 } && request.Ids.Length <= Math.Min(Config.MaxKeys, 1000), "Select 1 to 1000 bundle IDs");
        var ids = request.Ids.Distinct(StringComparer.Ordinal).ToArray();
        var locations = ids.Select(id => Store.BundleLocation(snapshot.Id, id)).ToArray();
        return Start("bundle_verify", snapshot.Id, ids.Length, async (task, token) =>
        {
            var results = new List<ItemResult>();
            foreach (var location in locations)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    using var reservation = await Budget.ReserveAsync(location.Options!.Size, token);
                    await using var lease = await downloadWork.Join(snapshot.Id + ":" + location.Id, ct => DownloadOne(snapshot, location, ct), token);
                    results.Add(new(BundleIdentity.Id(location), null, null));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) { results.Add(new(BundleIdentity.Id(location), null, e.Message)); }
                task = task with { Completed = results.Count, Results = results.ToArray(), Updated = Now };
                if (results.Count % 20 == 0) Store.Put("task", task.Id, task);
            }
            var success = results.Count(r => r.Error == null);
            return task with { State = token.IsCancellationRequested ? "cancelled" : success == ids.Length ? "succeeded" : success > 0 ? "partial" : "failed" };
        });
    }
    public Manifest? Manifest(string id) => Store.Find<Manifest>("export", id);
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); await versionPolling; await batchRunner; await chartRunner; await modelRunner; await storageSweep; await Task.WhenAll(running.Values.ToArray());
        // Shared producers may still be unwinding after their last waiter cancelled.
        await exportWork.Drain(); await downloadWork.Drain();
        http.Dispose(); pathCache.Dispose(); Store.Dispose(); instance.Dispose(); shutdown.Dispose();
    }
}
