using System.Security.Cryptography;
namespace MoenotesAssets;

public sealed class BlobStore(string root)
{
    public string Root { get; } = Path.Combine(root, "blobs");
    public string PathFor(string sha)
    {
        Config.Require(sha.Length == 64 && sha.All(char.IsAsciiHexDigit), "Invalid blob digest");
        return Path.Combine(Root, sha[..2], sha);
    }
    public void Publish(string source, string sha, long bytes)
    {
        var target = PathFor(sha); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try { File.Move(source, target); }
        catch (IOException) when (File.Exists(target))
        {
            Config.Require(new FileInfo(target).Length == bytes, "Stored content size mismatch");
            Config.Require(Matches(sha, bytes), "Stored content hash mismatch"); File.Delete(source);
        }
    }
    public void PublishBatch(IEnumerable<(string Source, string Sha, long Bytes)> files)
    {
        var pending = files.ToArray();
        // Reject known integrity failures before moving any output. Other I/O failures
        // can still leave complete unindexed blobs for the existing storage sweep.
        foreach (var file in pending)
        {
            var target = PathFor(file.Sha);
            if (!File.Exists(target)) continue;
            Config.Require(new FileInfo(target).Length == file.Bytes, "Stored content size mismatch");
            Config.Require(Matches(file.Sha, file.Bytes), "Stored content hash mismatch");
        }
        foreach (var file in pending) Publish(file.Source, file.Sha, file.Bytes);
    }
    public bool Matches(string sha, long bytes) => Matches(PathFor(sha), sha, bytes);
    public static bool Matches(string path, string sha, long bytes)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length == bytes && Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    /// <summary>Every stored blob file, named by its digest.</summary>
    public IEnumerable<string> Files()
    {
        Directory.CreateDirectory(Root);
        foreach (var bucket in Directory.EnumerateDirectories(Root))
        {
            Config.Require(!File.GetAttributes(bucket).HasFlag(FileAttributes.ReparsePoint), "Symlink blob directory");
            foreach (var path in Directory.EnumerateFiles(bucket)) yield return path;
        }
    }
}
