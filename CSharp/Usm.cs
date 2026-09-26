using System.Buffers.Binary;
using System.Text;
using static MoenotesAssets.Config;
namespace MoenotesAssets;
// Mask derivation follows the CRI USM interoperability implementation in cridecoder (MIT).
public static class Usm
{
    public sealed record Streams(string Video, string? Audio, long? Frames, long? RateNumerator, long? RateDenominator);
    public static Streams Demux(string path, string stage, Config config)
    {
        var mask = VideoMask(config.CriKey); var audioMask = mask.Select((b, i) => (i & 1) == 0 ? (byte)~b : "URUC"u8[(i >> 1) & 3]).ToArray();
        using var input = File.OpenRead(path); var header = new byte[32];
        var masked = Masked(input, mask, config);
        var videoPath = Path.Combine(stage, "video.m2v"); var audioPath = Path.Combine(stage, "audio.hca");
        using var video = File.Create(videoPath); using var audio = File.Create(audioPath);
        var channels = new HashSet<(string, byte)>(); var ended = new HashSet<(string, byte)>();
        int? audioCodec = null, videoCodec = null; long total = 0;
        long? frames = null, rateNumerator = null, rateDenominator = null;
        while (input.Position < input.Length)
        {
            var start = input.Position; input.ReadExactly(header); var type = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)); var offset = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
            var padding = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10)); var channel = header[12]; var kind = header[15] & 3;
            Require(size >= 24 && start + 8 + size <= input.Length && offset >= 24 && offset + padding <= size, "Invalid USM chunk range");
            if (type == "@ALP") throw new UnsupportedInputException("Unsupported USM alpha video (@ALP)");
            Require(type is "CRID" or "@SFV" or "@SFA" or "@CUE", "Unsupported USM stream");
            var length = size - offset - padding; Require(length <= Math.Min(config.ExpandedBytes, 256L << 20), "USM chunk budget");
            input.Position = start + 8 + offset; var bytes = new byte[length]; input.ReadExactly(bytes);
            if (type is "@SFV" or "@SFA")
            {
                channels.Add((type, channel)); Require(channels.Count(c => c.Item1 == type) == 1, "Multiple USM tracks");
                if (kind == 1 && bytes.AsSpan().StartsWith("@UTF"u8))
                {
                    var rows = CriTables.Parse(bytes); Require(rows.Length > 0, "Empty USM stream header");
                    if (type == "@SFA") { if (rows[0].ContainsKey("audio_codec")) audioCodec = (int)rows[0].Number("audio_codec"); }
                    else
                    {
                        videoCodec = (int)rows[0].Number("mpeg_codec", -1);
                        if (rows[0].ContainsKey("total_frames")) { frames = rows[0].Number("total_frames"); Require(frames > 0, "Invalid USM frame count"); }
                        if (rows[0].ContainsKey("framerate_n") || rows[0].ContainsKey("framerate_d"))
                        {
                            rateNumerator = rows[0].Number("framerate_n"); rateDenominator = rows[0].Number("framerate_d");
                            Require(rateNumerator > 0 && rateDenominator > 0 && (double)rateNumerator / rateDenominator <= 240, "Invalid USM frame rate");
                        }
                    }
                }
                if (kind == 2 && bytes.AsSpan().StartsWith("#CONTENTS END"u8)) ended.Add((type, channel));
                if (kind == 0)
                {
                    Require(!ended.Contains((type, channel)), "USM data after stream end");
                    total += bytes.Length; Require(total <= config.ExpandedBytes, "USM expansion budget");
                    if (type == "@SFV") { if (masked) UnmaskVideo(bytes, mask); video.Write(bytes); }
                    else
                    {
                        // Some encoders omit audio_codec; the stream's first bytes then identify ADX (0x8000) or HCA
                        // (its magic, with or without the high-bit mask).
                        audioCodec ??= bytes is [0x80, 0x00, ..] ? 2
                            : bytes is [var h, var c, var a, 0, ..] && (h & 0x7f) == 'H' && (c & 0x7f) == 'C' && (a & 0x7f) == 'A' ? 4 : (int?)null;
                        Require(audioCodec is 2 or 4, "Unsupported USM audio codec");
                        if (audioCodec == 2 && masked) for (int i = 0x140; i < bytes.Length; i++) bytes[i] ^= audioMask[(i - 0x140) & 31];
                        audio.Write(bytes);
                    }
                }
            }
            input.Position = start + 8 + size;
        }
        Require(video.Length > 0 && channels.SetEquals(ended), "USM stream missing end marker");
        Require(!channels.Any(c => c.Item1 == "@SFA") || audio.Length > 0, "USM audio missing");
        video.Flush(); audio.Flush(); video.Dispose(); audio.Dispose();
        // Windows cannot rename a file that is still open.
        var magic = new byte[4];
        using (var check = File.OpenRead(videoPath)) check.ReadExactly(magic);
        if (magic.AsSpan().SequenceEqual("DKIF"u8)) { var renamed = Path.Combine(stage, "video.ivf"); File.Move(videoPath, renamed); videoPath = renamed; }
        else Require(videoCodec is null or 1 or 0, "Unsupported USM video codec");
        if (new FileInfo(audioPath).Length == 0) return new(videoPath, null, frames, rateNumerator, rateDenominator);
        if (audioCodec == 2) { var renamed = Path.Combine(stage, "audio.adx"); File.Move(audioPath, renamed); audioPath = renamed; }
        return new(videoPath, audioPath, frames, rateNumerator, rateDenominator);
    }
    /// <summary>
    /// Whether the streams are masked. Some encoders write plain MPEG, which unmasking would destroy, so the first
    /// maskable MPEG chunk decides: past its always-plain first 0x40 bytes, plain MPEG has start codes (one per slice)
    /// and masked bytes practically none. VP9 (IVF) and files without a deciding chunk count as masked.
    /// </summary>
    private static bool Masked(FileStream input, byte[] mask, Config config)
    {
        var header = new byte[32]; var video = false;
        try
        {
            while (input.Position + header.Length <= input.Length)
            {
                var start = input.Position; input.ReadExactly(header);
                var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)); var offset = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
                var padding = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10));
                // Demux reports invalid ranges.
                if (size < 24 || start + 8 + size > input.Length || offset < 24 || offset + padding > size || size - offset - padding > Math.Min(config.ExpandedBytes, 256L << 20)) return true;
                if (header.AsSpan(0, 4).SequenceEqual("@SFV"u8) && (header[15] & 3) == 0)
                {
                    var bytes = new byte[size - offset - padding]; input.Position = start + 8 + offset; input.ReadExactly(bytes);
                    if (!video && bytes.AsSpan().StartsWith("DKIF"u8)) return true;
                    video = true;
                    if (bytes.Length >= 0x240)
                    {
                        var unmasked = (byte[])bytes.Clone(); UnmaskVideo(unmasked, mask);
                        return StartCodes(unmasked.AsSpan(0x40)) >= StartCodes(bytes.AsSpan(0x40));
                    }
                }
                input.Position = start + 8 + size;
            }
            return true;
        }
        finally { input.Position = 0; }
    }
    private static int StartCodes(ReadOnlySpan<byte> data)
    {
        var count = 0;
        for (int at; (at = data.IndexOf("\0\0\u0001"u8)) >= 0; data = data[(at + 3)..]) count++;
        return count;
    }
    public static void UnmaskVideo(Span<byte> bytes, byte[] mask)
    {
        if (bytes.Length < 0x240) return;
        var state = mask.Select(b => (byte)~b).ToArray();
        for (int i = 0x140; i < bytes.Length; i++) { var lane = (i - 0x140) & 31; bytes[i] ^= state[lane]; state[lane] = (byte)(bytes[i] ^ ~mask[lane]); }
        state = (byte[])mask.Clone();
        for (int i = 0; i < 256; i++) { state[i & 31] ^= bytes[0x140 + i]; bytes[0x40 + i] ^= state[i & 31]; }
    }
    public static byte[] VideoMask(ulong key)
    {
        unchecked
        {
            var t = new byte[32]; var a = (uint)key; var b = (uint)(key >> 32);
            t[0] = (byte)a; t[1] = (byte)(a >> 8); t[2] = (byte)(a >> 16); t[3] = (byte)((a >> 24) - 0x34);
            t[4] = (byte)((b & 15) + 0xf9); t[5] = (byte)((b >> 8) ^ 0x13); t[6] = (byte)((b >> 16) + 0x61);
            t[7] = (byte)~t[0]; t[8] = (byte)(t[2] + t[1]); t[9] = (byte)(t[1] - t[7]); t[10] = (byte)~t[2]; t[11] = (byte)~t[1];
            t[12] = (byte)(t[11] + t[9]); t[13] = (byte)(t[8] - t[3]); t[14] = (byte)~t[13]; t[15] = (byte)(t[10] - t[11]);
            t[16] = (byte)(t[8] - t[15]); t[17] = (byte)(t[16] ^ t[7]); t[18] = (byte)~t[15]; t[19] = (byte)(t[3] ^ 0x10);
            t[20] = (byte)(t[4] - 0x32); t[21] = (byte)(t[5] + 0xed); t[22] = (byte)(t[6] ^ 0xf3); t[23] = (byte)(t[19] - t[15]);
            t[24] = (byte)(t[21] + t[7]); t[25] = (byte)(0x21 - t[19]); t[26] = (byte)(t[20] ^ t[23]); t[27] = (byte)(t[22] * 2);
            t[28] = (byte)(t[23] + 0x44); t[29] = (byte)(t[3] + t[4]); t[30] = (byte)(t[5] - t[22]); t[31] = (byte)(t[29] ^ t[19]); return t;
        }
    }
}
