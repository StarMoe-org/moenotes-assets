using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class CatalogIndexTests
{
    private static Location Bundle(uint id, string name, string hash, long bytes = 100) => new(id, name, "https://dummy.net/asset/Android/" + name + "_" + hash + ".bundle", Catalog.Crypt, "Bundle", [], new(hash, name + "_internal", 42, bytes));
    private static Catalog Graph(params Location[] bundles)
    {
        var catalog = new Catalog(); foreach (var b in bundles) { catalog.Locations.Add(b.Id, b); var a = new Location(b.Id + 1000, "Assets/" + b.Key, "Assets/" + b.Key + ".bytes", "asset-provider", "UnityEngine.TextAsset", [b.Id], null); catalog.Locations.Add(a.Id, a); catalog.Keys.Add(a.Key, [a.Id]); }
        return catalog;
    }
    private static Snapshot Snapshot(string id, string region = "tw", string locale = "en", string? content = null, long created = 1) => new(id, content ?? Crypto.Identity(id), region, locale, "main", "https://cdn.invalid", "hint", created);
    [Fact]
    public void BundleDiffSeparatesChangesAndHashEvidence()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path);
        var a = Bundle(1, "same", new string('a', 32)); var b = Bundle(2, "modified", new string('b', 32)); var removed = Bundle(3, "removed", new string('c', 32));
        var changed = Bundle(2, "modified", new string('d', 32)); var added = Bundle(4, "added", new string('e', 32));
        var before = Snapshot("before"); var after = Snapshot("after", created: 2); store.IndexSnapshot(before, Graph(a, b, removed)); store.IndexSnapshot(after, Graph(a, changed, added));
        var diff = store.Diff(before.Id, after.Id, null, 0, 100, true); Assert.Equal("version", diff.Kind); Assert.Equal(1, diff.Summary["added"]); Assert.Equal(1, diff.Summary["removed"]); Assert.Equal(1, diff.Summary["changed"]); Assert.Equal(1, diff.Summary["unchanged"]);
        Assert.Equal("modified.bundle", diff.Entries.Single(e => e.Change == "changed").Key);
        store.Observe(before.Id, a, "raw-a", "plain-a"); store.Observe(after.Id, a, "raw-b", "plain-b");
        diff = store.Diff(before.Id, after.Id, null, 0, 100, true); Assert.Equal(2, diff.Summary["changed"]); Assert.Equal("verified_plain_sha256", diff.Entries.Single(e => e.Key == "same.bundle").Evidence);
        Assert.Equal("conflicting_plain_sha256", Assert.Single(store.Equivalents(before.Id, BundleIdentity.Id(a))).Evidence);
    }
    [Fact]
    public void RegionLocalePointersAndCatalogContentAreIndependent()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path); var graph = Graph(Bundle(1, "shared", new string('a', 32))); var content = Crypto.Identity("identical");
        var tw = Snapshot("tw-en", "tw", "en", content); var kr = Snapshot("kr-en", "kr", "en", content); var ja = Snapshot("tw-ja", "tw", "ja", content);
        store.IndexSnapshot(tw, graph); store.IndexSnapshot(kr, null); store.IndexSnapshot(ja, null);
        Assert.Equal(tw.Id, store.CurrentSnapshot("tw", "en", "main")); Assert.Equal(kr.Id, store.CurrentSnapshot("kr", "en", "main")); Assert.Equal(ja.Id, store.CurrentSnapshot("tw", "ja", "main"));
        Assert.Equal(3, store.Catalogs().Length); Assert.Equal("region", store.Diff(tw.Id, kr.Id, null, 0, 100).Kind); Assert.Equal("locale", store.Diff(tw.Id, ja.Id, null, 0, 100).Kind);
        var stats = System.Text.Json.JsonSerializer.SerializeToElement(store.StorageStats(0), Json.Options); Assert.Equal(1, stats.GetProperty("unique_catalogs").GetInt32()); Assert.Equal(1, stats.GetProperty("indexed_bundle_rows").GetInt32());
        Assert.Equal(2, store.Equivalents(tw.Id, BundleIdentity.Id(graph.Locations[1])).Length);
    }
    [Fact]
    public void AssetsCanBeBrowsedWithoutCatalogBinaryAndResolveTransitiveCycles()
    {
        using var dir = new TempDirectory(); awaitUsingNotNeeded();
        void awaitUsingNotNeeded()
        {
            using var store = new Store(dir.Path); var b = Bundle(1, "dependency", new string('a', 32)); var graph = Graph(b);
            graph.Locations[2] = new(2, "wrapper", "wrapper", "asset-provider", "wrapper", [1, 3], null);
            graph.Locations[3] = new(3, "logical", "Assets/example.bytes", "asset-provider", "UnityEngine.TextAsset", [2], null); graph.Keys["path/logical"] = [3];
            var snapshot = Snapshot("snapshot"); store.IndexSnapshot(snapshot, graph);
            var page = store.Assets(snapshot.Id, "path/", null, BundleIdentity.Id(b), 0, 100); Assert.Equal("path/logical", Assert.Single(page.Assets).Key); Assert.False(File.Exists(Path.Combine(dir.Path, "catalogs", snapshot.Id + ".bin")));
        }
    }
    [Fact]
    public void AmbiguousAndUnknownHashesAreNotClaimedIdentical()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path); var one = Bundle(1, "collision", new string('a', 32)); var two = Bundle(2, "collision", new string('b', 32)); var unknown = Bundle(3, "unknown", new string('0', 32));
        var graph = new Catalog { Locations = new() { [1] = one, [2] = two, [3] = unknown } }; var first = Snapshot("first"); var second = Snapshot("second", "kr"); store.IndexSnapshot(first, graph); store.IndexSnapshot(second, graph);
        var diff = store.Diff(first.Id, second.Id, null, 0, 100, true); Assert.Equal(1, diff.Summary["ambiguous"]); Assert.Equal(1, diff.Summary["unknown"]);
        Assert.Equal(3, store.Bundles(first.Id, null, 0, 100).Total);
    }
    [Fact]
    public void BlobDedupKeepsBothRegionManifests()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path); var blobs = new BlobStore(dir.Path); var bytes = "same exported bytes"u8.ToArray(); var sha = Crypto.Sha256(bytes);
        foreach (var region in new[] { "tw", "kr" })
        {
            var source = Path.Combine(dir.Path, region + ".tmp"); File.WriteAllBytes(source, bytes); blobs.Publish(source, sha, bytes.Length);
            var file = new PublishedFile(region + "-file", "00000.txt", "label", "text/plain", bytes.Length, sha, null);
            store.Publish(new(region + "-export", region + "-snapshot", "logical", Worker.Profile, [], [file], region), true);
        }
        Assert.Single(Directory.EnumerateFiles(blobs.Root, "*", SearchOption.AllDirectories)); Assert.Equal(2, store.All<Manifest>("export").Length);
        Assert.Equal(sha, store.Get<FileRecord>("file", "tw-file")!.BlobSha256); Assert.Equal(sha, store.Get<FileRecord>("file", "kr-file")!.BlobSha256);
        var stats = System.Text.Json.JsonSerializer.SerializeToElement(store.StorageStats(0), Json.Options); Assert.Equal(bytes.Length, stats.GetProperty("deduplicated_output_bytes").GetInt64());
    }
    [Fact]
    public async Task StorageSweepRemovesOnlyUncommittedPublications()
    {
        using var dir = new TempDirectory();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = "https://example.com" });
        string Blob(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text); var sha = Crypto.Sha256(bytes);
            var source = Path.Combine(dir.Path, sha + ".tmp"); File.WriteAllBytes(source, bytes); service.Blobs.Publish(source, sha, bytes.Length); return sha;
        }
        // Legacy export directories hold their files beside an unread manifest.json copy.
        string ExportDirectory(string id, bool files = true)
        {
            var path = Path.Combine(dir.Path, "exports", id); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "manifest.json"), "{}");
            if (files) File.WriteAllText(Path.Combine(path, "00000.txt"), "legacy"); return path;
        }
        var kept = Blob("committed"); var orphan = Blob("interrupted");
        var published = ExportDirectory("published"); var unpublished = ExportDirectory("unpublished"); var copyOnly = ExportDirectory("copy-only", files: false);
        service.Store.Publish(new("published", "snapshot", "key", Worker.Profile, [], [new("file", "00000.txt", "label", "text/plain", 9, kept, null)]), true);
        // Recovery runs after construction, beside the listener, instead of blocking startup.
        Assert.True(File.Exists(service.Blobs.PathFor(orphan)));
        await service.SweepStorage();
        Assert.True(File.Exists(service.Blobs.PathFor(kept))); Assert.False(File.Exists(service.Blobs.PathFor(orphan)));
        Assert.True(File.Exists(Path.Combine(published, "00000.txt"))); Assert.False(File.Exists(Path.Combine(published, "manifest.json")));
        Assert.False(Directory.Exists(unpublished)); Assert.False(Directory.Exists(copyOnly));
    }
    [Fact]
    public void PublishedFileRecordsOmitManifestMetadata()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path); var sha = new string('a', 64);
        var metadata = new Dictionary<string, object> { ["width"] = 4 };
        store.Publish(new("new", "snapshot", "key", Worker.Profile, [], [new("new-file", "00000.png", "label", "image/png", 9, sha, metadata)]), true);
        Assert.Null(store.Get<FileRecord>("file", "new-file")!.File.Metadata);
        Assert.NotNull(store.Get<Manifest>("export", "new")!.Files[0].Metadata);
        // Records written before the change are slimmed once, in batches smaller than the table.
        for (var i = 0; i < 3; i++) store.Put("file", "old-" + i, new FileRecord("old", new("old-" + i, "00000.png", "label", "image/png", 9, sha, metadata), sha));
        Assert.Equal(3, store.SlimFileRecords(CancellationToken.None, batch: 2));
        var old = store.Get<FileRecord>("file", "old-1")!; Assert.Null(old.File.Metadata); Assert.Equal(sha, old.BlobSha256); Assert.Equal("image/png", old.File.MediaType);
        store.Put("file", "later", new FileRecord("old", new("later", "00000.png", "label", "image/png", 9, sha, metadata), sha));
        Assert.Equal(0, store.SlimFileRecords(CancellationToken.None));
    }
    [Fact]
    public void PruneTasksKeepsUnfinishedRecentAndRetained()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path);
        void Task(string id, string state, long updated) => store.Put("task", id, new TaskInfo(id, "export", state, null, 1, 1, [], null, 1, updated));
        Task("old", "succeeded", 10); Task("retained", "succeeded", 10); Task("running", "running", 10); Task("recent", "failed", 100);
        Assert.Equal(1, store.PruneTasks(50, ["retained"]));
        Assert.Equal(["recent", "retained", "running"], store.All<TaskInfo>("task").Select(t => t.Id).Order().ToArray());
    }
    [Fact]
    public void CompactReclaimsDeletedPages()
    {
        using var dir = new TempDirectory(); using var store = new Store(dir.Path);
        for (var i = 0; i < 200; i++) store.Put("task", "t" + i, new string('x', 20000));
        Assert.NotNull(store.Find<string>("task", "t0")); // leaves an idle pooled reader open, as a serving process has
        store.Execute("DELETE FROM records WHERE kind='task'");
        var (before, after) = store.Compact();
        Assert.True(after < before / 4, $"{before} -> {after}");
        Assert.Null(store.Find<string>("task", "t0"));
    }
}
