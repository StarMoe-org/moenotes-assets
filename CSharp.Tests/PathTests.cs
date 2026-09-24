using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class PathTests
{
    private static PublishedFile File(string id, string name, string label, string sha) => new(id, name, label, "audio/mp4", 1, sha, null);

    [Fact]
    public void FirstFileOwnsTheLabelAndLaterDifferentContentGetsASequenceAlias()
    {
        var manifest = new Manifest("e", "s", "Cri/Sound/Se", "p", [], [
            File("a", "00000.m4a", "Luck", "one"), File("b", "00001.m4a", "Luck", "two"),
            File("c", "00002.png", "Same", "x"), File("d", "00003.png", "Same", "x"),
            File("e", "00004.m4a", "a b", "y"), File("f", "00005.m4a", "sub/dir", "z")]);
        var files = AssetService.PathFiles(manifest);
        Assert.Equal(["Luck.m4a", "Luck__00001.m4a", "Same.png", "a b.m4a"], files.Keys);
        Assert.Equal(["a", "b", "c", "e"], files.Values.Select(f => f.Id));
        var listing = AssetService.Listing("ja", manifest);
        Assert.Equal(["/ja/Cri/Sound/Se/Luck.m4a", "/ja/Cri/Sound/Se/Luck__00001.m4a", "/ja/Cri/Sound/Se/Same.png", "/ja/Cri/Sound/Se/Same.png", "/ja/Cri/Sound/Se/a%20b.m4a", null], listing.Files.Select(f => f.Path));
        Assert.All(listing.Files, f => Assert.StartsWith("/files/", f.File));
    }
}
