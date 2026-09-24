using System.Buffers.Binary;
using System.Reflection;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;
using VGAudio.Formats.Pcm16;
using VGAudio.Utilities;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

// HCA 3.0 resolution/noise reconstruction follows cridecoder (MIT).
// VGAudio provides the shared spectral codebooks and IMDCT; its v2 frame parser
// clamps resolution to 1 and cannot decode v3 noise-coded bands correctly.
public static class Hca3
{
    private static readonly Type Packing = typeof(CriHcaFrame).Assembly.GetType("VGAudio.Codecs.CriHca.CriHcaPacking", true)!;
    private static readonly Type Tables = typeof(CriHcaFrame).Assembly.GetType("VGAudio.Codecs.CriHca.CriHcaTables", true)!;
    private static readonly Func<CriHcaChannel, BitReader, bool> ReadScales = Packing.GetMethod("ReadScaleFactors", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<CriHcaChannel, BitReader, bool>>();
    private static readonly Action<CriHcaFrame, BitReader> ReadSpectra = Packing.GetMethod("ReadSpectralCoefficients", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Action<CriHcaFrame, BitReader>>();
    private static readonly double[] Scaling = (double[])Tables.GetProperty("DequantizerScalingTable")!.GetValue(null)!;
    private static readonly double[] Steps = (double[])Tables.GetProperty("QuantizerStepSize")!.GetValue(null)!;
    private static readonly double[] Conversion = (double[])Tables.GetProperty("ScaleConversionTable")!.GetValue(null)!;
    private static readonly int[] Resolution = [14, 14, 14, 14, 14, 14, 13, 13, 13, 13, 13, 13, 12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 7, 6, 6, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    public static void Decode(byte[] bytes, HcaStructure meta, CriHcaKey key, string path)
    {
        var h = meta.Hca;
        Require(h.HfrGroupCount == 0 && h.StereoBandCount == 0 && meta.Reserved1 == 0 && !h.UseAthCurve,
            "Unsupported HCA 3.0 HFR/joint-stereo mode");
        Require(h.MinResolution is >= 0 and <= 15 && h.MaxResolution >= h.MinResolution && h.MaxResolution <= 15 && h.BaseBandCount is > 0 and <= 128 && h.SampleCount > 0,
            "Invalid HCA 3.0 coding parameters");
        var pcm = Enumerable.Range(0, h.ChannelCount).Select(_ => new short[h.SampleCount]).ToArray();
        var frame = new CriHcaFrame(h); var crc = new Crc16(0x8005); uint random = 1;
        for (var index = 0; index < h.FrameCount; index++)
        {
            var data = bytes.AsSpan(h.HeaderSize + index * h.FrameSize, h.FrameSize).ToArray();
            Require(crc.Compute(data, data.Length - 2) == BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(data.Length - 2)), "HCA CRC mismatch");
            CriHcaEncryption.CryptFrame(h, data, key, true);
            var reader = new BitReader(data);
            Require(reader.ReadInt(16) == 65535, "HCA frame sync mismatch");
            var noise = reader.ReadInt(9); var boundary = reader.ReadInt(7);
            var packedNoise = unchecked(((uint)noise << 8) - (uint)boundary);
            var noiseBands = new List<int>[h.ChannelCount]; var validBands = new List<int>[h.ChannelCount];
            for (var c = 0; c < h.ChannelCount; c++)
            {
                var channel = frame.Channels[c];
                Require(ReadScales(channel, reader), "HCA key or scale-factor validation failed");
                noiseBands[c] = []; validBands[c] = [];
                for (var band = 0; band < channel.CodedScaleFactorCount; band++)
                {
                    var scale = channel.ScaleFactors[band]; var resolution = 0;
                    if (scale > 0)
                    {
                        var curve = (long)(unchecked(packedNoise + (uint)band) >> 8) + 1 - (5 * scale >> 1);
                        resolution = Math.Clamp(curve < 0 ? 15 : curve >= Resolution.Length ? 0 : Resolution[(int)curve], h.MinResolution, h.MaxResolution);
                        if (resolution == 0) noiseBands[c].Add(band); else validBands[c].Add(band);
                    }
                    channel.Resolution[band] = resolution;
                    channel.Gain[band] = Scaling[scale] * Steps[resolution];
                }
                validBands[c].Reverse();
            }
            ReadSpectra(frame, reader);
            Require(reader.Position <= data.Length * 8 - 16, "HCA key or spectral block validation failed");
            // Unused spectral padding must be zero, including any partial byte.
            for (var bit = reader.Position; bit < data.Length * 8 - 16; bit++)
                Require((data[bit >> 3] & (1 << (7 - (bit & 7)))) == 0, "HCA key or padding validation failed");
            for (var sf = 0; sf < 8; sf++)
                for (var c = 0; c < h.ChannelCount; c++)
                {
                    var channel = frame.Channels[c]; var spectra = channel.Spectra[sf];
                    for (var band = 0; band < channel.CodedScaleFactorCount; band++) spectra[band] = channel.QuantizedSpectra[sf][band] * channel.Gain[band];
                    if (validBands[c].Count > 0)
                        foreach (var band in noiseBands[c])
                        {
                            random = unchecked(random * 0x343fd + 0x269ec3);
                            var source = validBands[c][(int)((random & 32767) * validBands[c].Count >> 15)];
                            var scale = Math.Max(0, channel.ScaleFactors[band] - channel.ScaleFactors[source] + 63);
                            spectra[band] = Conversion[scale] * spectra[source];
                        }
                    channel.Mdct.RunImdct(spectra, channel.PcmFloat[sf]);
                    for (var sample = 0; sample < 128; sample++)
                    {
                        var destination = index * 1024 + sf * 128 + sample - h.InsertedSamples;
                        if (destination >= 0 && destination < h.SampleCount)
                            pcm[c][destination] = (short)Math.Clamp((int)(channel.PcmFloat[sf][sample] * 32768), short.MinValue, short.MaxValue);
                    }
                }
        }
        using var wav = File.Create(path);
        new WaveWriter().WriteToStream(new Pcm16FormatBuilder(pcm, h.SampleRate).Build(), wav);
    }
}
