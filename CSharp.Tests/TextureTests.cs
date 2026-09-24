using AssetsTools.NET;
using AssetsTools.NET.Extra;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class TextureTests
{
    [Fact]
    public async Task RgbaTextureToPngHasCorrectDimensionsAndOrientation()
    {
        using var dir = new TempDirectory(); var manager = new AssetsManager();
        using var package = typeof(Worker).Assembly.GetManifestResourceStream("MoenotesAssets.Resources.classdata.tpk")!;
        manager.LoadClassPackage(package); manager.LoadClassDatabaseFromPackage("6000.3.12f1");
        var source = manager.LoadAssetsFile(new MemoryStream(Fixture.Serialized()), "fixture", false);
        var field = manager.CreateValueBaseField(source, (int)AssetClassID.Texture2D);
        field["m_Name"].AsString = "checker"; field["m_Width"].AsInt = 2; field["m_Height"].AsInt = 2; field["m_CompleteImageSize"].AsInt = 16;
        field["m_TextureFormat"].AsInt = 4; field["m_MipCount"].AsInt = 1; field["m_ImageCount"].AsInt = 1; field["m_TextureDimension"].AsInt = 2;
        field["image data"].AsByteArray = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        var serialized = Fixture.Serialized(field.WriteToByteArray(), 28); manager.UnloadAll(true);
        var path = Path.Combine(dir.Path, "texture.bundle"); var bundle = Fixture.Bundle(serialized); File.WriteAllBytes(path, bundle);
        var catalog = Catalog.Parse(Fixture.Catalog(bundle.Length, ~BinaryTools.Crc32(serialized))); var location = catalog.Closure(Fixture.Key)[0];
        var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid" }, catalog.Target(Fixture.Key), [new(location, path)], Path.Combine(dir.Path, "out"));
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("image/png", result[0].MediaType);
        var png = File.ReadAllBytes(Path.Combine(job.Output, result[0].Name)); Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))); Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        using var raw = new MemoryStream();
        for (int at = 8; at < png.Length;) { var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at)); if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) raw.Write(png, at + 8, length); at += 12 + length; }
        raw.Position = 0; using var zlib = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionMode.Decompress); var pixels = BinaryTools.ReadLimited(zlib, 100);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 255, 0, 255, 0, 0, 255, 0, 255, 0, 255 }, pixels);
    }
}
