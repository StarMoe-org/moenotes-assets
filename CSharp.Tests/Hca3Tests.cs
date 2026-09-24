using System.Buffers.Binary;
using System.Reflection;
using MoenotesAssets;
using VGAudio.Codecs.CriHca;
using VGAudio.Utilities;
using Xunit;
namespace MoenotesAssets.Tests;

public class Hca3Tests
{
    public static byte[] Fixture(ulong key, ushort subkey = 0)
    {
        var bytes = new byte[96 + 3 * 64];
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), (ushort)value);
        void U32(int at, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)value);
        "HCA\0"u8.CopyTo(bytes); U16(4, 0x300); U16(6, 96);
        "fmt\0"u8.CopyTo(bytes.AsSpan(8)); U32(12, (1 << 24) | 48000); U32(16, 3); U16(20, 128); U16(22, 128);
        "comp"u8.CopyTo(bytes.AsSpan(24)); U16(28, 64); bytes[30] = 0; bytes[31] = 15; bytes[32] = 1; bytes[33] = 1; bytes[34] = 2; bytes[35] = 2;
        "ciph"u8.CopyTo(bytes.AsSpan(40)); U16(44, 56); "pad\0"u8.CopyTo(bytes.AsSpan(46));
        var crc = new Crc16(0x8005); U16(94, crc.Compute(bytes, 94));
        var info = new HcaInfo { ChannelCount = 1, FrameSize = 64, BaseBandCount = 2, TotalBandCount = 2, TrackCount = 1, MinResolution = 0, MaxResolution = 15 };
        var frame = new CriHcaFrame(info) { AcceptableNoiseLevel = 200, EvaluationBoundary = 0 };
        var channel = frame.Channels[0]; channel.ScaleFactorDeltaBits = 6;
        channel.ScaleFactors[0] = 63; channel.ScaleFactors[1] = 50;
        channel.Resolution[0] = 6; channel.Resolution[1] = 0;
        for (int sf = 0; sf < 8; sf++) channel.QuantizedSpectra[sf][0] = sf % 2 == 0 ? 1 : -1;
        var pack = typeof(CriHcaFrame).Assembly.GetType("VGAudio.Codecs.CriHca.CriHcaPacking", true)!.GetMethod("PackFrame", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Action<CriHcaFrame, Crc16, byte[]>>();
        var effective = subkey == 0 ? key : unchecked(key * (((ulong)subkey << 16) | ((ulong)(ushort)~subkey + 2)));
        for (int i = 0; i < 3; i++)
        {
            var data = new byte[64]; pack(frame, crc, data); CriHcaEncryption.CryptFrame(info, data, new CriHcaKey(effective), false); data.CopyTo(bytes, 96 + i * 64);
        }
        return bytes;
    }
    [Fact]
    public void NoiseCodedV3DecodesAndRejectsWrongKeysAndDamagedFrames()
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://example.com" };
        var bytes = Fixture(config.CriKey, 1234); var path = Path.Combine(dir.Path, "decoded.wav");
        Assert.Equal(1, CriMedia.DecodeHca(bytes, 1234, config, path));
        var probeBytes = File.ReadAllBytes(path);
        Assert.True(probeBytes.Length > 2816 * 2);
        // Independent vgmstream r2117 decode of this synthetic noise-coded stream.
        short[] expected = [-4986, -5057, -5128, -5201, -5274, -5348, -5421, -5495, -5568, -5642, -5715, -5788, -5860, -5932, -6003, -6073, -6143, -6211, -6278, -6345, -6409, -6473, -6535, -6595, -6653, -6709, -6763, -6814, -6864, -6910, -6953, -6994];
        var dataOffset = 12;
        while (!probeBytes.AsSpan(dataOffset, 4).SequenceEqual("data"u8))
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(probeBytes.AsSpan(dataOffset + 4));
            dataOffset += 8 + length + (length & 1);
        }
        Assert.Equal(2816 * 2, BinaryPrimitives.ReadInt32LittleEndian(probeBytes.AsSpan(dataOffset + 4)));
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(probeBytes.AsSpan(dataOffset + 8 + i * 2)) - expected[i]), 0, 1);
        Assert.ThrowsAny<Exception>(() => CriMedia.DecodeHca(bytes, 1234, config with { CriKey = 123 }, path));
        bytes[^5] ^= 1;
        Assert.Throws<InvalidDataException>(() => CriMedia.DecodeHca(bytes, 1234, config, path));
    }
}
