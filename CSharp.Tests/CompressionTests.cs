using AssetsTools.NET;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class CompressionTests
{
    [Theory]
    [InlineData(AssetBundleCompressionType.LZ4)]
    [InlineData(AssetBundleCompressionType.LZMA)]
    public async Task CompressedUnityFsExportsAndVerifiesCrc(AssetBundleCompressionType compression)
    {
        using var dir = new TempDirectory(); var bundle = new AssetBundleFile();
        bundle.Read(new AssetsFileReader(new MemoryStream(Fixture.Bundle())));
        var path = Path.Combine(dir.Path, "packed.bundle"); using (var writer = new AssetsFileWriter(File.Create(path))) bundle.Pack(writer, compression); bundle.Close();
        var catalog = Catalog.Parse(Fixture.Catalog((int)new FileInfo(path).Length, ~BinaryTools.Crc32(Fixture.Serialized())));
        var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid" }, catalog.Target(Fixture.Key), [new(catalog.Closure(Fixture.Key)[0], path)], Path.Combine(dir.Path, "out"));
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Equal(Fixture.Body, File.ReadAllBytes(Path.Combine(job.Output, result[0].Name)));
    }
    [Fact]
    public void LegacyDataIsRejectedBeforeCleanup()
    {
        using var dir = new TempDirectory(); File.WriteAllText(Path.Combine(dir.Path, "index.sqlite"), "legacy"); var exports = Path.Combine(dir.Path, "exports"); Directory.CreateDirectory(exports); var retained = Path.Combine(exports, "keep.txt"); File.WriteAllText(retained, "keep");
        Assert.Throws<InvalidDataException>(() => new AssetService(new Config { CdnRoot = "https://cdn.invalid", DataDir = dir.Path })); Assert.Equal("keep", File.ReadAllText(retained));
    }
}
