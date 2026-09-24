using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace MoenotesAssets;

public static class BinaryTools
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => { uint c = (uint)i; for (int j = 0; j < 8; j++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0); return c; }).ToArray();
    public static uint Crc32(ReadOnlySpan<byte> bytes, uint state = 0xffffffff)
    { foreach (var b in bytes) state = CrcTable[(state ^ b) & 255] ^ (state >> 8); return state; }
    public static byte[] ReadLimited(Stream stream, long limit)
    {
        using var result = new MemoryStream(); var buffer = new byte[65536]; int n;
        while ((n = stream.Read(buffer)) > 0) { Config.Require(result.Length + n <= limit, "Expansion limit"); result.Write(buffer, 0, n); }
        return result.ToArray();
    }
    public static byte[] DecodeText(byte[] raw, long limit)
    {
        Config.Require(raw.Length <= limit, "Text limit");
        if (!raw.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b })) return raw;
        using var gzip = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        return ReadLimited(gzip, limit);
    }
    public static void Png(string path, byte[] rgba, int width, int height)
    {
        Config.Require(rgba.Length == checked(width * height * 4), "Invalid image buffer");
        using var file = System.IO.File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, true))
            for (var y = 0; y < height; y++) { zlib.WriteByte(0); zlib.Write(rgba, y * width * 4, width * 4); }
        Chunk("IDAT", data.ToArray()); Chunk("IEND", []);
        void Chunk(string type, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); file.Write(number);
            var label = Encoding.ASCII.GetBytes(type); file.Write(label); file.Write(bytes);
            BinaryPrimitives.WriteUInt32BigEndian(number, ~Crc32(bytes, Crc32(label))); file.Write(number);
        }
    }
}
