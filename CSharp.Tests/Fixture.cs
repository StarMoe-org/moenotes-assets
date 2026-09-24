using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MoenotesAssets;
namespace MoenotesAssets.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moenotes-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
internal static class Fixture
{
    public const string Key = "Live/MusicScore/test", Internal = "Assets/fixture.bytes";
    public static readonly byte[] Body = "{\"fixture\":true}"u8.ToArray();
    public static (byte[] Catalog, byte[] Bundle) Create()
    {
        var plain = Bundle(); var crc = ~BinaryTools.Crc32(Serialized());
        var catalog = Catalog(plain.Length, crc); Crypto.Decrypt(plain, "fixture.bundle", 0); return (catalog, plain);
    }
    public static byte[] Serialized(byte[]? objectData = null, int classId = 49)
    {
        using var gz = new MemoryStream(); using (var zip = new GZipStream(gz, CompressionLevel.Optimal, true)) zip.Write(Body);
        using var text = new MemoryStream(); using var tw = new BinaryWriter(text); String(tw, "fixture"); tw.Write((int)gz.Length); tw.Write(gz.ToArray());
        if (objectData != null) { text.SetLength(0); text.Write(objectData); }
        using var bundle = new MemoryStream(); using var bw = new BinaryWriter(bundle);
        String(bw, "fixture"); bw.Write(0); bw.Write(1); String(bw, Internal); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(7L); bw.Write(new byte[20]); bw.Write(0); String(bw, "fixture"); bw.Write(0); bw.Write((byte)0);
        using var meta = new MemoryStream(); using var mw = new BinaryWriter(meta); mw.Write(Encoding.ASCII.GetBytes("6000.3.12f1\0")); mw.Write(13); mw.Write((byte)0); mw.Write(2);
        foreach (var cls in new[] { classId, 142 }) { mw.Write(cls); mw.Write((byte)0); mw.Write((short)-1); mw.Write(new byte[16]); }
        mw.Write(2); while ((48 + meta.Length) % 4 != 0) mw.Write((byte)0);
        mw.Write(7L); mw.Write(0L); mw.Write((uint)text.Length); mw.Write(0); mw.Write(1L); mw.Write(text.Length); mw.Write((uint)bundle.Length); mw.Write(1);
        mw.Write(0); mw.Write(0); mw.Write(0); mw.Write((byte)0);
        var offset = (48 + (int)meta.Length + 15) / 16 * 16; var result = new byte[offset + text.Length + bundle.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 22); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), (uint)meta.Length);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(24), (ulong)result.Length); BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(32), (ulong)offset);
        meta.ToArray().CopyTo(result, 48); text.ToArray().CopyTo(result, offset); bundle.ToArray().CopyTo(result, offset + (int)text.Length); return result;
    }
    private static void String(BinaryWriter writer, string text)
    {
        var b = Encoding.UTF8.GetBytes(text); writer.Write(b.Length); writer.Write(b); while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }
    public static byte[] Bundle(byte[]? data = null)
    {
        var payload = data ?? Serialized(); using var info = new MemoryStream();
        info.Write(new byte[16]); Be(info, 1u); Be(info, (uint)payload.Length); Be(info, (uint)payload.Length); Be(info, (ushort)0); Be(info, 1u); Be(info, 0UL); Be(info, (ulong)payload.Length); Be(info, 4u); info.Write("CAB-fixture\0"u8);
        using var output = new MemoryStream(); output.Write("UnityFS\0"u8); Be(output, 8u); output.Write("5.x.x\0"u8); output.Write("6000.3.12f1\0"u8); var at = (int)output.Position; Be(output, 0UL); Be(output, (uint)info.Length); Be(output, (uint)info.Length); Be(output, 0u); while (output.Position % 16 != 0) output.WriteByte(0); output.Write(info.ToArray()); output.Write(payload);
        var bytes = output.ToArray(); BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), (ulong)bytes.Length); return bytes;
    }
    public static void Be(Stream stream, ulong n) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, n); stream.Write(b); }
    public static void Be(Stream stream, uint n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); stream.Write(b); }
    public static void Be(Stream stream, ushort n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, n); stream.Write(b); }
    public static byte[] Catalog(int size, uint crc)
    {
        var b = new Bin(); var hash = b.Put(Enumerable.Repeat((byte)1, 16).ToArray()); var bn = b.String("fixture"); var common = b.Words(0, 0); var opt = b.Words(hash, bn, crc, (uint)size, common); var ot = b.Type("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions"); var extra = b.Words(ot, opt);
        var bk = b.String("fixture.bundle"); var bi = b.String("https://dummy.net/asset/Android/fixture.bundle"); var bp = b.String(MoenotesAssets.Catalog.Crypt); var bt = b.Type("UnityEngine.ResourceManagement.ResourceProviders.IAssetBundleResource"); var bundle = b.Words(bk, bi, bp, uint.MaxValue, 0, extra, bt);
        var key = b.String(Key); var inner = b.String(Internal); var provider = b.String("UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider"); var deps = b.Array(bundle); var type = b.Type("UnityEngine.TextAsset"); var loc = b.Words(key, inner, provider, deps, 0, uint.MaxValue, type);
        var kt = b.Type("System.String"); var ks = b.String(Key); var kv = b.Words(ks, 0); var k = b.Words(kt, kv); var ls = b.Array(loc); var keys = b.Array(k, ls); var bytes = b.Bytes(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x0de38942); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), keys); return bytes;
    }
    private sealed class Bin
    {
        private readonly MemoryStream stream = new(); public Bin() => stream.Write(new byte[32]);
        public uint Put(byte[] bytes) { var at = (uint)stream.Position; stream.Write(bytes); return at; }
        public uint Words(params uint[] values) => Put(values.SelectMany(BitConverter.GetBytes).ToArray());
        public uint String(string s) { var bytes = Encoding.UTF8.GetBytes(s); Words((uint)bytes.Length); return Put(bytes); }
        public uint Array(params uint[] values) { Words((uint)values.Length * 4); return Words(values); }
        public uint Type(string s) { var a = String("Test"); var b = String(s); return Words(a, b); }
        public byte[] Bytes() => stream.ToArray();
    }
}
