using AssetsTools.NET;
using MoenotesAssets;
using Xunit;

namespace MoenotesAssets.Tests;

public class BundleContentsTests
{
    [Fact]
    public void VersionThreeDatabaseAddsContentTablesWithoutReindexingCatalog()
    {
        using var dir = new TempDirectory();
        var bytes = Fixture.Bundle();
        var catalog = Catalog.Parse(Fixture.Catalog(bytes.Length, ~BinaryTools.Crc32(Fixture.Serialized())));
        var snapshot = new Snapshot("old-snapshot", Crypto.Identity("old-content"), "tw", "en", "main", "https://cdn.invalid", "", 1);
        using (var store = new Store(dir.Path))
        {
            store.IndexSnapshot(snapshot, catalog);
            store.Put("setting", "browser_index_version", 3);
            store.Execute("DROP TABLE bundle_directories;DROP TABLE bundle_contents;DROP TABLE bundle_scan_failures;DROP TABLE bundle_scans;");
        }
        using (var store = new Store(dir.Path))
        {
            Assert.Equal(4, store.Get<int>("setting", "browser_index_version"));
            Assert.Equal(Fixture.Key, Assert.Single(store.Assets(snapshot.Id, null, null, null, 0, 10).Assets).Key);
            Assert.Single(store.PendingBundleScans());
            Assert.Equal(0, store.ScanStatus(snapshot.Id).Scanned);
        }
    }

    [Theory]
    [InlineData(AssetBundleCompressionType.LZ4)]
    [InlineData(AssetBundleCompressionType.LZMA)]
    public async Task ScannerReadsCompressedUnityFsContainers(AssetBundleCompressionType compression)
    {
        using var dir = new TempDirectory();
        var bundle = new AssetBundleFile(); bundle.Read(new AssetsFileReader(new MemoryStream(Fixture.Bundle())));
        var path = Path.Combine(dir.Path, "packed.bundle");
        using (var writer = new AssetsFileWriter(File.Create(path))) bundle.Pack(writer, compression);
        bundle.Close();
        var catalog = Catalog.Parse(Fixture.Catalog((int)new FileInfo(path).Length, ~BinaryTools.Crc32(Fixture.Serialized())));
        var location = Assert.Single(catalog.Closure(Fixture.Key));
        var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid", DataDir = dir.Path }, location, [new(location, path)], Path.Combine(dir.Path, "out"));
        Assert.Contains(await Processes.ScanBundle(job, dir.Path, CancellationToken.None), item => item is { Path: Fixture.Internal, Kind: "asset" });
    }

    [Fact]
    public async Task ScannerIndexesActualContainerPathsAndKeepsThemAcrossVersionUpgrade()
    {
        using var dir = new TempDirectory();
        var bytes = Fixture.Bundle();
        var catalog = Catalog.Parse(Fixture.Catalog(bytes.Length, ~BinaryTools.Crc32(Fixture.Serialized())));
        var location = Assert.Single(catalog.Closure(Fixture.Key));
        var path = Path.Combine(dir.Path, "fixture.bundle"); File.WriteAllBytes(path, bytes);
        var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid", DataDir = dir.Path }, location, [new(location, path)], Path.Combine(dir.Path, "out"));
        var scanned = await Processes.ScanBundle(job, dir.Path, CancellationToken.None);
        Assert.Contains(scanned, item => item is { Path: Fixture.Internal, Kind: "asset" });
        Assert.Contains(scanned, item => item is { Path: "CAB-fixture", Kind: "unity_file" });

        var snapshot = new Snapshot("scan-snapshot", Crypto.Identity("scan-content"), "tw", "en", "main", "https://cdn.invalid", "", 1);
        var bundleId = BundleIdentity.Id(location);
        using (var store = new Store(dir.Path))
        {
            store.IndexSnapshot(snapshot, catalog);
            Assert.Empty(store.Contents(snapshot.Id, null, null, 0, 100).Contents);
            store.RecordBundleScanFailure(bundleId, "temporary failure");
            Assert.Equal(1, store.ScanStatus(snapshot.Id).Failed);
            store.SaveBundleScan(bundleId, "plain-sha", scanned);
            var file = Assert.Single(store.Contents(snapshot.Id, null, null, 0, 100).Contents);
            Assert.Equal(Fixture.Internal, file.Path); Assert.Equal("7", file.PathId);
            Assert.Equal(2, store.Contents(snapshot.Id, null, bundleId, 0, 100, true).Total);
            Assert.Equal(1, store.ScanStatus(snapshot.Id).Scanned);
            Assert.Equal(0, store.ScanStatus(snapshot.Id).Failed);
            Assert.Equal("Assets", Assert.Single(store.Browse(snapshot.Id, "", null, 0, 100).Folders).Name);
            Assert.Equal(Fixture.Internal, Assert.Single(store.Browse(snapshot.Id, "Assets", null, 0, 100).Files).Path);
            Assert.Equal(Fixture.Internal, Assert.Single(store.Browse(snapshot.Id, "", "fixture", 0, 100).Files).Path);
            store.Put("setting", "browser_index_version", 3);
        }
        using (var store = new Store(dir.Path))
        {
            Assert.Equal(4, store.Get<int>("setting", "browser_index_version"));
            Assert.Empty(store.PendingBundleScans());
            Assert.Equal(Fixture.Internal, Assert.Single(store.Contents(snapshot.Id, null, null, 0, 100).Contents).Path);
        }
    }
}
