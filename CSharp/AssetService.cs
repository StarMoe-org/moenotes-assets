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
            foreach (var name in new[] { "tmp", "exports", "catalogs", "blobs" })
            {
                var path = Path.Combine(Config.DataDir, name);
                Require(!Directory.Exists(path) || !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "Symlink storage directory");
                Directory.CreateDirectory(path);
            }
            Store = new Store(Config.DataDir);
            Blobs = new BlobStore(Config.DataDir);
            Blobs.Recover(Store.All<FileRecord>("file").Where(f => f.BlobSha256 != null).Select(f => f.BlobSha256!).ToHashSet(StringComparer.Ordinal));
            foreach (var scope in Store.All<Snapshot>("snapshot").GroupBy(s => Store.ScopeSetting(s.Region, s.Locale, s.BiliVersion)))
            {
                var preferred = Store.Get<string>("setting", scope.Key) ?? scope.OrderBy(s => s.Created).Last().Id;
                foreach (var retained in scope)
                    if (!Store.HasCatalogIndex(retained.Id)) Store.IndexSnapshot(retained, Store.HasCatalogContent(retained.ContentSha256) ? null : Catalog.Parse(File.ReadAllBytes(CatalogPath(retained))), retained.Id == preferred);
            }
            foreach (var task in Store.All<TaskInfo>("task"))
                if (task.State is "queued" or "running") Store.Put("task", task.Id, task with { State = "failed", Error = "Interrupted by service restart; resubmit failed keys", Updated = Now });
            foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(Config.DataDir, "tmp"))) RemoveTree(path);
            foreach (var path in Directory.EnumerateDirectories(Path.Combine(Config.DataDir, "exports")))
                if (Store.Get<Manifest>("export", Path.GetFileName(path)) == null) RemoveTree(path);
            http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
            downloads = new(config.Downloads); workers = new(config.Workers); videos = new(config.Videos); queue = new(config.QueueLimit);
            Budget = new(config.TempBytes);
            downloadWork = new(d => RemoveTree(d.Directory));
            InitializeBatches();
        }
        catch { Store?.Dispose(); instance.Dispose(); throw; }
    }
    public static void RemoveTree(string path)
    {
        if (!Path.Exists(path)) return;
        if (Directory.Exists(path)) Directory.Delete(path, !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        else File.Delete(path);
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
    public TaskInfo StartRefresh(string? region = null, string? locale = null, string? version = null) => Start("catalog_refresh", null, 1, async (task, token) =>
    {
        await refresh.WaitAsync(token);
        try
        {
            var selected = Config.ForRegion(region, locale, version);
            var hash = new UTF8Encoding(false, true).GetString(await Fetch(selected.CatalogUri("hash"), 65536, token)).Trim();
            Require(hash.Length <= 128, "Invalid catalog hash");
            var bytes = await Fetch(selected.CatalogUri("bin"), 32 << 20, token);

            var digest = Crypto.Sha256(bytes);
            var id = Crypto.Identity(selected.Region, selected.Locale, selected.BiliVersion, selected.CdnRoot, digest);
            var snapshot = new Snapshot(id, digest, selected.Region, selected.Locale, selected.BiliVersion, selected.CdnRoot, hash, Now);
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
    private async Task<byte[]> Fetch(Uri uri, int limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Config.DownloadTimeoutSecs));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
            var results = new List<ItemResult>();
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
                            if (manifest == null)
                            {
                                using var lease = await exportWork.Join(id, t => ExportOne(snapshot, catalog, key, id, t), ct);
                                manifest = lease.Value;
                            }
                            item = new(key, manifest.Id, null, Reused: manifest.ReusedFrom != null);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (UnsupportedInputException e) { item = new(key, null, null, e.Message); }
                    catch (Exception e) { item = new(key, null, e.Message); }
                    lock (results)
                    {
                        results.Add(item);
                        if ((results.Count % 20 == 0 && progress.Elapsed >= TimeSpan.FromSeconds(1)) || progress.Elapsed >= TimeSpan.FromSeconds(10))
                        {
                            task = task with { Completed = results.Count, Skipped = results.Count(r => r.SkipReason != null), Reused = results.Count(r => r.Reused), Results = results.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray(), Updated = Now };
                            Store.Put("task", task.Id, task);
                            Console.Error.WriteLine($"[task {task.Id}] export region={snapshot.Region} locale={snapshot.Locale} progress={results.Count}/{keys.Length} succeeded={results.Count(r => r.ExportId != null)} skipped={task.Skipped} reused={task.Reused} failed={results.Count(r => r.Error != null)}");
                            progress.Restart();
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            var successes = results.Count(r => r.ExportId != null);
            var skipped = results.Count(r => r.SkipReason != null);
            return task with { Completed = results.Count, Skipped = skipped, Reused = results.Count(r => r.Reused), Results = results.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray(), State = token.IsCancellationRequested ? "cancelled" : successes + skipped == keys.Length ? "succeeded" : successes > 0 ? "partial" : "failed" };
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
            var uri = (Config with { CdnRoot = snapshot.CdnRoot }).AssetUri(location.Internal);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(Config.DownloadTimeoutSecs));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
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
        var leases = new List<SharedWork<Download>.Lease>(); string? stage = null; var moved = false;
        IDisposable? reservation = null;
        var destination = Path.Combine(Config.DataDir, "exports", id);
        try
        {
            var existing = Store.Get<Manifest>("export", id); if (existing != null) return existing;
            var target = catalog.Target(key); var profile = Worker.ProfileFor(target); var locations = ExportSelection.Dependencies(catalog, key);
            if (target.Provider == Catalog.Cri || target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal))
            {
                var raw = locations.Where(l => l.Provider == Catalog.Cri).ToArray();
                Require(raw.Length <= 1, "Ambiguous CRI dependencies");
                if (raw.Length == 1) locations = raw; // Otherwise the CRI bytes are embedded in the Unity asset.
            }
            Require(locations.Sum(l => l.Options!.Size) <= Config.ExpandedBytes, "Dependency set budget");
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
            if (previous != null && previous.Files.All(f => File.Exists(Blobs.PathFor(f.Sha256)) && new FileInfo(Blobs.PathFor(f.Sha256)).Length == f.Bytes))
            {
                Require(previous.Files.Sum(f => f.Bytes) <= Config.OutputBytes, "Reused output size budget");
                var copied = previous.Files.Select(f => f with { Id = Crypto.Identity(id, f.Name), Label = f.MediaType == "video/mp4" ? target.Key : f.Label }).ToArray();
                var reused = new Manifest(id, snapshot.Id, key, profile, leases.Select(l => new Source(l.Value.Input.Location, l.Value.Hash, l.Value.PlainHash)).ToArray(), copied, snapshot.Region, previous.Id);
                stage = Path.Combine(Config.DataDir, "tmp", "reuse-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
                await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), Json.Write(reused), token);
                token.ThrowIfCancellationRequested(); Directory.Move(stage, destination); moved = true;
                Store.Publish(reused, true); moved = false; return reused;
            }
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
            foreach (var file in files) Blobs.Publish(Path.Combine(output, file.Name), file.Sha256, file.Bytes);
            await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), Json.Write(manifest), token);
            token.ThrowIfCancellationRequested(); Directory.Move(output, destination); moved = true;
            Store.Publish(manifest, true); moved = false;
            Store.Put("conversion", conversionId, manifest.Id); return manifest;
        }
        finally
        {
            try { if (moved) RemoveTree(destination); if (stage != null) RemoveTree(stage); }
            finally
            {
                try { foreach (var lease in leases) await lease.DisposeAsync(); }
                finally { reservation?.Dispose(); if (conversionHeld) conversionGate!.Release(); publication.Release(); }
            }
        }
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
    public FileRecord? LookupFile(string id) => Store.Get<FileRecord>("file", id);
    public Manifest? Manifest(string id) => Store.Get<Manifest>("export", id);
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); await batchRunner; await Task.WhenAll(running.Values.ToArray());
        // Shared producers may still be unwinding after their last waiter cancelled.
        await exportWork.Drain(); await downloadWork.Drain();
        http.Dispose(); Store.Dispose(); instance.Dispose(); shutdown.Dispose();
    }
}
