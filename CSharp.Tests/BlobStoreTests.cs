using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class BlobStoreTests
{
    [Fact]
    public void BatchPublicationRejectsExistingCorruptionBeforeAnyMoves()
    {
        using var dir = new TempDirectory(); var blobs = new BlobStore(dir.Path);
        var stored = "stored output"u8.ToArray(); var storedSha = Crypto.Sha256(stored);
        var second = Path.Combine(dir.Path, "second.tmp"); File.WriteAllBytes(second, stored); blobs.Publish(second, storedSha, stored.Length);
        var corrupt = (byte[])stored.Clone(); corrupt[0] ^= 1; File.WriteAllBytes(blobs.PathFor(storedSha), corrupt);
        File.WriteAllBytes(second, stored);
        var fresh = "new output"u8.ToArray(); var freshSha = Crypto.Sha256(fresh);
        var first = Path.Combine(dir.Path, "first.tmp"); File.WriteAllBytes(first, fresh);

        Assert.Throws<InvalidDataException>(() => blobs.PublishBatch([(first, freshSha, fresh.Length), (second, storedSha, stored.Length)]));
        Assert.False(File.Exists(blobs.PathFor(freshSha)));
        Assert.Equal(fresh, File.ReadAllBytes(first)); Assert.Equal(stored, File.ReadAllBytes(second));
        Assert.Equal(corrupt, File.ReadAllBytes(blobs.PathFor(storedSha)));
    }

    [Fact]
    public void SameSizeCorruptionIsNotDeduplicated()
    {
        using var dir = new TempDirectory(); var blobs = new BlobStore(dir.Path);
        var bytes = "verified output"u8.ToArray(); var sha = Crypto.Sha256(bytes);
        var source = Path.Combine(dir.Path, "output.tmp"); File.WriteAllBytes(source, bytes); blobs.Publish(source, sha, bytes.Length);
        var corrupt = (byte[])bytes.Clone(); corrupt[0] ^= 1; File.WriteAllBytes(blobs.PathFor(sha), corrupt);
        File.WriteAllBytes(source, bytes);

        var error = Assert.Throws<InvalidDataException>(() => blobs.Publish(source, sha, bytes.Length));
        Assert.Contains("hash mismatch", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(corrupt, File.ReadAllBytes(blobs.PathFor(sha)));
    }
}
