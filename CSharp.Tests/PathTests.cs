using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class PathTests
{
    private static PublishedFile File(string id, string name, string label, string sha) => new(id, name, label, "audio/mp4", 1, sha, null);

    [Fact]
    public void NamesUseLabelAndExtensionAndRejectConflictingContent()
    {
        var manifest = new Manifest("e", "s", "Cri/Sound/Se", "p", [], [
            File("a", "00000.m4a", "Luck", "one"), File("b", "00001.m4a", "Luck", "two"),
            File("c", "00002.png", "Same", "x"), File("d", "00003.png", "Same", "x"),
            File("e", "00004.m4a", "a b", "y"), File("f", "00005.m4a", "sub/dir", "z")]);
        var files = AssetService.PathFiles(manifest);
        Assert.Null(files["Luck.m4a"]); Assert.Equal("c", files["Same.png"]!.Id); Assert.False(files.ContainsKey("sub/dir.m4a"));
        var listing = AssetService.Listing("ja", manifest);
        Assert.Equal([null, null, "/ja/Cri/Sound/Se/Same.png", "/ja/Cri/Sound/Se/Same.png", "/ja/Cri/Sound/Se/a%20b.m4a", null], listing.Files.Select(f => f.Path));
        Assert.All(listing.Files, f => Assert.StartsWith("/files/", f.File));
    }
}
