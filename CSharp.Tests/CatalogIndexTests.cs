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
}
