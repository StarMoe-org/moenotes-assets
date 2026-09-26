using System.Buffers.Binary;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

// Cue data of an ACB as the live's sound definitions (audio/live-audio.json `sounds`) need it: a port of nnnotes
// liveaudio.acb_cues (MIT, see THIRD_PARTY_NOTICES.md). Only the shape of the live sheets is accepted: a cue is a
// sequence of tracks, each track notes on one synth, each synth holds one memory-AWB waveform.
public static class AcbCues
{
    public sealed record Layer(long AwbId, long Samples, long LoopFlag, double Volume, Dictionary<string, double> BusSends,
        Dictionary<string, string> TrackOther, Dictionary<string, string> SynthOther);
    public sealed record Cue(string Name, long CueId, long LengthMs, string[] Categories, double Volume, Dictionary<string, double> BusSends,
        Dictionary<string, string> SequenceOther, double AcbVolume, Layer[] Layers);
    sealed record Params(List<string> Categories, double Volume, Dictionary<string, double> BusSends, Dictionary<string, string> Other);

    public static Dictionary<string, Cue> Parse(byte[] acb)
    {
        var top = CriTables.Parse(acb); Require(top.Length >= 1, "ACB header rows");
        var header = top[0];
        var tables = new Dictionary<string, Dictionary<string, object?>[]>(StringComparer.Ordinal);
        foreach (var (name, value) in header)
            if (value is byte[] { Length: >= 4 } bytes && bytes.AsSpan(0, 4).SequenceEqual("@UTF"u8)) tables[name] = CriTables.Parse(bytes);
        Dictionary<string, object?>[] Table(string name) => tables.TryGetValue(name, out var t) ? t : throw new InvalidDataException($"ACB table {name} missing");
        var categories = (tables.GetValueOrDefault("AcfReferenceTable") ?? []).Where(r => r.Number("Type") == 3).ToDictionary(r => r.Number("Id"), r => r.String("Name"));
        var buses = (tables.GetValueOrDefault("StringValueTable") ?? []).Select(r => r.String("StringValue")).ToArray();
        var names = Table("CueNameTable").ToDictionary(r => r.Number("CueIndex"), r => r.String("CueName"));
        List<(int Code, byte[] Value)> Commands(string table, long index) => index == 65535 ? [] : Split(Table(table)[checked((int)index)].Bytes("Command"));
        Params Values(List<(int Code, byte[] Value)> commands)
        {
            var result = new Params([], 1.0, [], []);
            var volume = 1.0;
            foreach (var (code, value) in commands)
            {
                switch (code)
                {
                    case 65:
                        for (var i = 0; i + 4 <= value.Length; i += 4)
                        {
                            var id = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(i));
                            result.Categories.Add(categories.TryGetValue(id, out var category) ? category : throw new InvalidDataException($"ACB category {id} missing"));
                        }
                        break;
                    case 146: Require(value.Length >= 4, "ACB volume command"); volume *= BinaryPrimitives.ReadSingleBigEndian(value); break;
                    case 111:
                        Require(value.Length >= 4, "ACB bus send command");
                        var bus = BinaryPrimitives.ReadUInt16BigEndian(value); Require(bus < buses.Length, "ACB bus missing");
                        result.BusSends[buses[bus]] = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2)) / 10000.0; break;
                    case 0 or 2000: break;
                    default: result.Other[code.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToHexStringLower(value); break;
                }
            }
            return result with { Volume = volume };
        }
        var output = new Dictionary<string, Cue>(StringComparer.Ordinal);
        var cues = Table("CueTable");
        for (var ci = 0; ci < cues.Length; ci++)
        {
            var c = cues[ci];
            var name = names.TryGetValue(ci, out var n) ? n : throw new InvalidDataException($"ACB cue {ci} has no name");
            Require(c.Number("ReferenceType") == 3, $"{name}: cue reference type {c.Number("ReferenceType")} (only sequences handled)");
            var sequence = Table("SequenceTable")[checked((int)c.Number("ReferenceIndex"))];
            Require(sequence.Number("Type") == 0, $"{name}: sequence type {sequence.Number("Type")} (only polyphonic handled)");
            var sp = Values(Commands("SeqCommandTable", sequence.Number("CommandIndex")));
            var layers = new List<Layer>();
            var trackIndices = sequence.Bytes("TrackIndex");
            for (var at = 0; at + 2 <= trackIndices.Length; at += 2)
            {
                var ti = BinaryPrimitives.ReadUInt16BigEndian(trackIndices.AsSpan(at));
                var track = Table("TrackTable")[ti];
                var tp = Values(Commands("TrackCommandTable", track.Number("CommandIndex")));
                var ons = Commands("TrackEventTable", track.Number("EventIndex")).Where(x => x.Code == 2000).Select(x => x.Value).ToArray();
                Require(ons.Length == 1, $"{name}: track {ti} has {ons.Length} note-ons");
                Require(ons[0].Length >= 4, $"{name}: track {ti} note-on");
                var kind = BinaryPrimitives.ReadUInt16BigEndian(ons[0]); var si = BinaryPrimitives.ReadUInt16BigEndian(ons[0].AsSpan(2));
                Require(kind == 2, $"{name}: track {ti} note-on kind {kind}");
                var synth = Table("SynthTable")[si];
                var yp = Values(Commands("SynthCommandTable", synth.Number("CommandIndex")));
                var items = synth.Bytes("ReferenceItems");
                Require(synth.Number("Type") == 0 && items.Length == 4 && BinaryPrimitives.ReadUInt16BigEndian(items) == 1, $"{name}: synth {si} type {synth.Number("Type")} is not one waveform");
                var wave = Table("WaveformTable")[BinaryPrimitives.ReadUInt16BigEndian(items.AsSpan(2))];
                Require(wave.Number("Streaming") == 0, $"{name}: streamed waveform (memory AWB expected)");
                var sends = new Dictionary<string, double>(tp.BusSends, StringComparer.Ordinal);
                foreach (var (k, v) in yp.BusSends) sends[k] = v;
                layers.Add(new(wave.Number("MemoryAwbId"), wave.Number("NumSamples"), wave.Number("LoopFlag"), tp.Volume * yp.Volume, sends, tp.Other, yp.Other));
            }
            output[name] = new(name, c.Number("CueId"), c.Number("Length"), [.. sp.Categories], sp.Volume, sp.BusSends, sp.Other,
                header.GetValueOrDefault("AcbVolume") switch { float f => f, double d => d, _ => 1.0 }, [.. layers]);
        }
        return output;
    }

    /// An ACB command list: repeated (u16 code, u8 size, payload).
    static List<(int, byte[])> Split(byte[] b)
    {
        var output = new List<(int, byte[])>(); var p = 0;
        while (p + 3 <= b.Length)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); var n = b[p + 2];
            output.Add((code, b.AsSpan(p + 3, Math.Min(n, b.Length - p - 3)).ToArray())); p += 3 + n;
        }
        return output;
    }
}
