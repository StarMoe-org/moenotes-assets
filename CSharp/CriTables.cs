using System.Buffers.Binary;
using System.Text;
using static MoenotesAssets.Config;
namespace MoenotesAssets;
// CRI @UTF offsets are relative to the byte following table_size.
public static class CriTables
{
    public static Dictionary<string, object?>[] Parse(byte[] bytes)
    {
        var r = new Cursor(bytes); Require(r.Text(4) == "@UTF", "Invalid UTF table");
        var size = checked((int)r.U32() + 8); Require(size <= bytes.Length && size >= 32, "UTF size");
        var version = r.U16(); var rowsAt = r.U16() + 8; var stringsAt = checked((int)r.U32() + 8); var dataAt = checked((int)r.U32() + 8);
        _ = r.U32(); var columns = r.U16(); var rowSize = r.U16(); var count = r.U32();
        Require(version <= 1 && columns is > 0 and <= 1024 && count <= 100000 && (long)count * columns <= 1000000 &&
            rowsAt >= 32 && rowsAt <= stringsAt && stringsAt <= dataAt && dataAt <= size && (long)rowsAt + (long)count * rowSize <= stringsAt, "UTF limits");
        string Str(uint offset)
        {
            var at = checked(stringsAt + (int)offset); Require(at >= stringsAt && at < dataAt, "UTF string offset");
            var end = Array.IndexOf(bytes, (byte)0, at, dataAt - at); Require(end >= at && end - at <= 65536, "UTF string terminator");
            return Encoding.UTF8.GetString(bytes, at, end - at);
        }
        object? Value(int type, Cursor input)
        {
            switch (type)
            {
                case 0: return (long)input.Byte();
                case 1: return (long)(sbyte)input.Byte();
                case 2: return (long)input.U16();
                case 3: return (long)(short)input.U16();
                case 4: return (long)input.U32();
                case 5: return (long)(int)input.U32();
                case 6: case 7: return (long)input.U64();
                case 8: return BitConverter.Int32BitsToSingle((int)input.U32());
                case 9: return BitConverter.Int64BitsToDouble((long)input.U64());
                case 10: return Str(input.U32());
                case 11:
                    var at = checked(dataAt + (int)input.U32()); var length = checked((int)input.U32());
                    Require(at >= dataAt && (long)at + length <= size, "UTF blob range");
                    return bytes.AsSpan(at, length).ToArray();
                default: throw new InvalidDataException("Unsupported UTF value type");
            }
        }
        var schema = new List<(string Name, int Type, int Storage, object? Value)>();
        for (int i = 0; i < columns; i++)
        {
            var flag = r.Byte(); var name = Str(r.U32()); var storage = flag & 0xf0; var type = flag & 15;
            Require(storage is 0x10 or 0x30 or 0x50 or 0x70, "Unsupported UTF storage");
            schema.Add((name, type, storage, storage is 0x30 or 0x70 ? Value(type, r) : null));
        }
        Require(r.Position <= rowsAt, "UTF schema overlaps rows");
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 0; i < count; i++)
        {
            r.Position = rowsAt + i * rowSize; var row = new Dictionary<string, object?>();
            foreach (var column in schema)
                Require(row.TryAdd(column.Name, column.Storage == 0x50 ? Value(column.Type, r) : column.Storage == 0x10 ? column.Type == 11 ? Array.Empty<byte>() : column.Type == 10 ? "" : 0L : column.Value), "Duplicate UTF column");
            Require(r.Position <= rowsAt + (i + 1) * rowSize, "UTF row overflow"); rows.Add(row);
        }
        return rows.ToArray();
    }
    public static long Number(this Dictionary<string, object?> row, string key, long fallback = 0) => row.TryGetValue(key, out var value) && value is long n ? n : fallback;
    public static byte[] Bytes(this Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is byte[] bytes ? bytes : throw new InvalidDataException($"Missing {key}");
    public static string String(this Dictionary<string, object?> row, string key, string fallback = "") => row.TryGetValue(key, out var value) && value is string text ? text : fallback;
    public sealed class Cursor(byte[] data)
    {
        public int Position { get; set; }
        public ReadOnlySpan<byte> Take(int n) { Require(n >= 0 && Position >= 0 && (long)Position + n <= data.Length, "Truncated CRI data"); var b = data.AsSpan(Position, n); Position += n; return b; }
        public byte Byte() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        public string Text(int n) => Encoding.ASCII.GetString(Take(n));
    }
}
