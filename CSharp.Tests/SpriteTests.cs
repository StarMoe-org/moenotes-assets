using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class SpriteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SpriteAndAtlasExport(bool atlasTarget, bool tight)
    {
        using var dir = new TempDirectory(); var manager = new AssetsManager();
        using var package = typeof(Worker).Assembly.GetManifestResourceStream("MoenotesAssets.Resources.classdata.tpk")!;
        manager.LoadClassPackage(package); manager.LoadClassDatabaseFromPackage("6000.3.12f1");
        var source = manager.LoadAssetsFile(new MemoryStream(Fixture.Serialized()), "fixture", false);
        AssetTypeValueField Make(int type) => manager.CreateValueBaseField(source, type);
        var texture = Make(28); texture["m_Name"].AsString = "texture"; texture["m_Width"].AsInt = 2; texture["m_Height"].AsInt = 2; texture["m_CompleteImageSize"].AsInt = 16;
        texture["m_TextureFormat"].AsInt = 4; texture["m_MipCount"].AsInt = 1; texture["m_ImageCount"].AsInt = 1; texture["m_TextureDimension"].AsInt = 2;
        texture["image data"].AsByteArray = Enumerable.Repeat((byte)255, 16).ToArray();
        var sprite = Make(213); sprite["m_Name"].AsString = "sprite"; sprite["m_Rect"]["width"].AsFloat = 2; sprite["m_Rect"]["height"].AsFloat = 2; sprite["m_PixelsToUnits"].AsFloat = 1;
        var data = sprite["m_RD"]; data["texture"]["m_PathID"].AsLong = 7; data["textureRect"]["width"].AsFloat = 2; data["textureRect"]["height"].AsFloat = 2;
        data["settingsRaw"].AsUInt = tight ? 0u : 2u; data["downscaleMultiplier"].AsFloat = 1;
        if (tight)
        {
            var vertexData = data["m_VertexData"]; vertexData["m_VertexCount"].AsUInt = 3;
            var channelArray = vertexData["m_Channels"]["Array"]; var channel = ValueBuilder.DefaultValueFieldFromArrayTemplate(channelArray); channel["dimension"].AsByte = 3; channelArray.Children.Add(channel);
            var vertices = new[] { 0f, 0f, 0f, 2f, 0f, 0f, 0f, 2f, 0f }.SelectMany(BitConverter.GetBytes).ToArray(); vertexData["m_DataSize"].AsByteArray = vertices;
            data["m_IndexBuffer"]["Array"].AsByteArray = [0, 0, 1, 0, 2, 0]; var submeshes = data["m_SubMeshes"]["Array"]; var submesh = ValueBuilder.DefaultValueFieldFromArrayTemplate(submeshes); submesh["indexCount"].AsUInt = 3; submesh["vertexCount"].AsUInt = 3; submeshes.Children.Add(submesh);
        }
        var atlas = Make(687078895); atlas["m_Name"].AsString = "atlas";
        if (atlasTarget)
        {
            sprite["m_SpriteAtlas"]["m_PathID"].AsLong = 9;
            var map = atlas["m_RenderDataMap"]["Array"]; var entry = ValueBuilder.DefaultValueFieldFromArrayTemplate(map);
            entry["second"]["texture"]["m_PathID"].AsLong = 7; entry["second"]["textureRect"]["width"].AsFloat = 2; entry["second"]["textureRect"]["height"].AsFloat = 2;
            entry["second"]["settingsRaw"].AsUInt = 2; entry["second"]["downscaleMultiplier"].AsFloat = 1; map.Children.Add(entry);
        }
        var packed = atlas["m_PackedSprites"]["Array"]; var ptr = ValueBuilder.DefaultValueFieldFromArrayTemplate(packed); ptr["m_PathID"].AsLong = 8; packed.Children.Add(ptr);
        var bundle = Make(142); bundle["m_Name"].AsString = "fixture"; var container = bundle["m_Container"]["Array"]; var pair = ValueBuilder.DefaultValueFieldFromArrayTemplate(container);
        pair["first"].AsString = Fixture.Internal; pair["second"]["asset"]["m_PathID"].AsLong = atlasTarget ? 9 : 8; container.Children.Add(pair);
        var serialized = Serialize([(7, 28, texture.WriteToByteArray()), (8, 213, sprite.WriteToByteArray()), (9, 687078895, atlas.WriteToByteArray()), (1, 142, bundle.WriteToByteArray())]); manager.UnloadAll(true);
        var bytes = Fixture.Bundle(serialized); var path = Path.Combine(dir.Path, "sprite.bundle"); File.WriteAllBytes(path, bytes); var cat = Catalog.Parse(Fixture.Catalog(bytes.Length, ~BinaryTools.Crc32(serialized)));
        var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid" }, cat.Target(Fixture.Key), [new(cat.Closure(Fixture.Key)[0], path)], Path.Combine(dir.Path, "out"));
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("image/png", result[0].MediaType); Assert.Equal("sprite", result[0].Label);
        var png = File.ReadAllBytes(Path.Combine(job.Output, result[0].Name)); using var raw = new MemoryStream();
        for (int at = 8; at < png.Length;) { var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at)); if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) raw.Write(png, at + 8, length); at += 12 + length; }
        raw.Position = 0; using var zlib = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionMode.Decompress); var pixels = BinaryTools.ReadLimited(zlib, 100);
        Assert.Equal(tight ? new byte[] { 255, 0, 255, 255 } : new byte[] { 255, 255, 255, 255 }, new byte[] { pixels[4], pixels[8], pixels[13], pixels[17] });
    }
    private static byte[] Serialize((long Id, int Class, byte[] Data)[] objects)
    {
        using var data = new MemoryStream(); var offsets = new List<long>(); foreach (var item in objects) { while (data.Length % 8 != 0) data.WriteByte(0); offsets.Add(data.Position); data.Write(item.Data); }
        using var meta = new MemoryStream(); using var w = new BinaryWriter(meta); w.Write(Encoding.ASCII.GetBytes("6000.3.12f1\0")); w.Write(13); w.Write((byte)0); w.Write(objects.Length);
        foreach (var item in objects) { w.Write(item.Class); w.Write((byte)0); w.Write((short)-1); w.Write(new byte[16]); }
        w.Write(objects.Length); while ((48 + meta.Length) % 4 != 0) w.Write((byte)0);
        for (int i = 0; i < objects.Length; i++) { w.Write(objects[i].Id); w.Write(offsets[i]); w.Write((uint)objects[i].Data.Length); w.Write(i); }
        w.Write(0); w.Write(0); w.Write(0); w.Write((byte)0);
        var start = (48 + (int)meta.Length + 15) / 16 * 16; var bytes = new byte[start + data.Length]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 22); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)meta.Length); BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), (ulong)bytes.Length); BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), (ulong)start); meta.ToArray().CopyTo(bytes, 48); data.ToArray().CopyTo(bytes, start); return bytes;
    }
}
