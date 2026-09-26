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
            Config.Require(new FileInfo(target).Length == bytes, "Stored content size mismatch"); File.Delete(source);
        }
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
