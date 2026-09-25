using System.Security.Cryptography;
using System.Text;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class PublicTreeTests
{
    [Fact]
    public async Task NewerSnapshotsOwnPathsAndUnsafeKeysStayOffDisk()
    {
        using var dir = new TempDirectory();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = "https://example.com" });
        Snapshot Snapshot(string id, long created, string region = "hk")
        {
            var snapshot = new Snapshot(id, "content", region, "zh-Hant", "main", "https://example.com", "hash", created);
            service.Store.Put("snapshot", id, snapshot); return snapshot;
        }
        PublishedFile File(string id, string label, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var blob = service.Blobs.PathFor(sha); Directory.CreateDirectory(Path.GetDirectoryName(blob)!); System.IO.File.WriteAllBytes(blob, bytes);
            return new(id, "00000.m4a", label, "audio/mp4", bytes.Length, sha, null);
        }
        Manifest Export(string id, Snapshot snapshot, string key, params PublishedFile[] files) => new(id, snapshot.Id, key, Worker.Profile, [], files);
        string Tree(params string[] parts) => Path.Combine([service.PublicRoot, "zh-Hant", .. parts]);

        var (older, newer, newest) = (Snapshot("old", 100), Snapshot("new", 200), Snapshot("newest", 300));
        Assert.True(service.MaterializePaths(Export("e-new", newer, "Cri/Sound/Song", File("f-new", "Song", "new"))));
        Assert.True(service.MaterializePaths(Export("e-old", older, "Cri/Sound/Song", File("f-old", "Song", "old"))));
        Assert.Equal("new", System.IO.File.ReadAllText(Tree("Cri", "Sound", "Song", "Song.m4a")));
        // A newer export replaces the directory's names: renamed files leave no stale link behind.
        service.MaterializePaths(Export("e-newest", newest, "Cri/Sound/Song", File("f-renamed", "Renamed", "renamed")));
        Assert.False(System.IO.File.Exists(Tree("Cri", "Sound", "Song", "Song.m4a")));
        Assert.Equal("renamed", System.IO.File.ReadAllText(Tree("Cri", "Sound", "Song", "Renamed.m4a")));
        // Movie labels are their asset key; the file is named after the key's last segment.
        service.MaterializePaths(Export("e-movie", newest, "Cri/Video/adv/clip/clip", File("f-movie", "Cri/Video/adv/clip/clip", "movie")));
        Assert.Equal("movie", System.IO.File.ReadAllText(Tree("Cri", "Video", "adv", "clip", "clip", "clip.m4a")));
        // Unsafe keys, unsafe labels and other regions are not materialized.
        service.MaterializePaths(Export("e-escape", newest, "../escape", File("f-escape", "x", "x")));
        service.MaterializePaths(Export("e-label", newest, "Cri/Sound/Label", File("f-label", "../x", "x")));
        service.MaterializePaths(Export("e-region", Snapshot("other", 400, "tw"), "Cri/Sound/Region", File("f-region", "Region", "x")));
        Assert.False(Directory.Exists(Path.Combine(service.PublicRoot, "escape")));
        Assert.DoesNotContain(Directory.EnumerateFiles(Tree("Cri", "Sound"), "*", SearchOption.AllDirectories), p => !p.Contains("Song"));
        // Temporary links are dot-prefixed and never left behind; owners are recorded in .export.json.
        Assert.DoesNotContain(Directory.EnumerateFiles(service.PublicRoot, "*", SearchOption.AllDirectories), p => p.EndsWith(".tmp"));
        Assert.Contains("\"newest\"", System.IO.File.ReadAllText(Tree("Cri", "Sound", "Song", ".export.json")));
    }
}
