using System.Buffers.Binary;
using System.Text;
namespace MoenotesAssets.Tests;

internal static class CriFixture
{
    public static byte[] Utf(params (string Name, object Value)[] columns)
    {
        using var strings = new MemoryStream(); uint Str(string text) { var at = (uint)strings.Position; strings.Write(Encoding.UTF8.GetBytes(text)); strings.WriteByte(0); return at; }
        var tableName = Str("fixture"); using var schema = new MemoryStream(); using var row = new MemoryStream(); using var data = new MemoryStream();
        foreach (var (name, value) in columns)
        {
            var type = value switch { string => 10, byte[] => 11, _ => 4 }; schema.WriteByte((byte)(0x50 | type)); Fixture.Be(schema, Str(name));
            if (value is string text) Fixture.Be(row, Str(text));
            else if (value is byte[] bytes) { Fixture.Be(row, (uint)data.Position); Fixture.Be(row, (uint)bytes.Length); data.Write(bytes); }
            else Fixture.Be(row, Convert.ToUInt32(value));
        }
        using var output = new MemoryStream(); output.Write("@UTF"u8); Fixture.Be(output, (uint)(24 + schema.Length + row.Length + strings.Length + data.Length));
        Fixture.Be(output, (ushort)0); Fixture.Be(output, (ushort)(24 + schema.Length)); Fixture.Be(output, (uint)(24 + schema.Length + row.Length)); Fixture.Be(output, (uint)(24 + schema.Length + row.Length + strings.Length));
        Fixture.Be(output, tableName); Fixture.Be(output, (ushort)columns.Length); Fixture.Be(output, (ushort)row.Length); Fixture.Be(output, 1u);
        output.Write(schema.ToArray()); output.Write(row.ToArray()); output.Write(strings.ToArray()); output.Write(data.ToArray()); return output.ToArray();
    }
    public static byte[] Acb(byte[] hca, bool streaming = false)
    {
        var awb = new byte[32 + hca.Length]; "AFS2"u8.CopyTo(awb); awb[4] = 1; awb[5] = 4; awb[6] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(awb.AsSpan(8), 1); BinaryPrimitives.WriteUInt16LittleEndian(awb.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(awb.AsSpan(18), 32); BinaryPrimitives.WriteUInt32LittleEndian(awb.AsSpan(22), (uint)awb.Length); hca.CopyTo(awb, 32);
        return Utf(("AwbFile", awb), ("CueTable", Utf(("CueId", 100), ("ReferenceType", 3), ("ReferenceIndex", 0))),
            ("CueNameTable", Utf(("CueIndex", 0), ("CueName", "synthetic-tone"))), ("SequenceTable", Utf(("NumTracks", 1), ("TrackIndex", new byte[2]))),
            ("TrackTable", Utf(("EventIndex", 0))), ("TrackEventTable", Utf(("Command", new byte[] { 7, 0xd0, 4, 0, 2, 0, 0, 0, 0, 0 }))),
            ("SynthTable", Utf(("ReferenceItems", new byte[] { 0, 1, 0, 0 }))), ("WaveformTable", Utf(("MemoryAwbId", 0), ("Streaming", streaming ? 1 : 0), ("EncodeType", 2))));
    }
    public static byte[] Usm(byte[] video, ulong key, int frames = 0, int rate = 25)
    {
        using var stream = new MemoryStream(); Chunk("CRID", 1, Utf(("name", "synthetic"))); Chunk("@SFV", 1, frames == 0 ? Utf(("mpeg_codec", 1)) : Utf(("mpeg_codec", 1), ("total_frames", frames), ("framerate_n", rate), ("framerate_d", 1)));
        var bytes = (byte[])video.Clone(); var mask = MoenotesAssets.Usm.VideoMask(key);
        if (bytes.Length >= 0x240)
        {
            var state = (byte[])mask.Clone(); for (int i = 0; i < 256; i++) { state[i & 31] ^= bytes[0x140 + i]; bytes[0x40 + i] ^= state[i & 31]; }
            state = mask.Select(b => (byte)~b).ToArray();
            for (int i = 0x140; i < bytes.Length; i++) { int lane = (i - 0x140) & 31; var plain = bytes[i]; bytes[i] ^= state[lane]; state[lane] = (byte)(plain ^ ~mask[lane]); }
        }
        Chunk("@SFV", 0, bytes); Chunk("@SFV", 2, "#CONTENTS END"u8.ToArray()); return stream.ToArray();
        void Chunk(string type, int kind, byte[] payload)
        {
            var header = new byte[32]; Encoding.ASCII.GetBytes(type).CopyTo(header, 0); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)(24 + payload.Length));
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 24); header[15] = (byte)kind; stream.Write(header); stream.Write(payload);
        }
    }
}
