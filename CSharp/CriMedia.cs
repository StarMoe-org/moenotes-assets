using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using VGAudio.Codecs.CriAdx;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Adx;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;
using VGAudio.Utilities;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public static class CriMedia
{
    // The pinned decoder exposes no public strict frame validator. Bind its validator once; fail closed if it changes.
    private static readonly Func<CriHcaFrame, BitReader, bool> UnpackFrame = typeof(CriHcaEncryption).Assembly.GetType("VGAudio.Codecs.CriHca.CriHcaPacking", true)!.GetMethod("UnpackFrame")!.CreateDelegate<Func<CriHcaFrame, BitReader, bool>>();
    public static async Task Export(string path, Worker.Output output, CancellationToken token)
    {
        using var file = File.OpenRead(path); var magic = new byte[4]; file.ReadExactly(magic);
        if (magic.AsSpan().SequenceEqual("@UTF"u8)) await Acb(path, output, token);
        else if (magic.AsSpan().SequenceEqual("CRID"u8)) await Movie(path, output, token);
        else throw new InvalidDataException("Unsupported CRI container");
    }
    public static int DecodeHca(byte[] bytes, ushort subkey, Config config, string path)
    {
        var reader = new HcaReader(); var meta = reader.ReadMetadata(new MemoryStream(bytes)); var hca = meta.Hca;
        Require(hca.ChannelCount is 1 or 2 && hca.FrameSize > 0 && hca.FrameCount > 0 &&
            (long)hca.HeaderSize + (long)hca.FrameSize * hca.FrameCount <= bytes.Length &&
            (long)hca.FrameCount * 1024 * hca.ChannelCount * 2 <= config.ExpandedBytes, "HCA size/channel budget");
        var effective = subkey == 0 ? config.CriKey : unchecked(config.CriKey * (((ulong)subkey << 16) | ((ulong)(ushort)~subkey + 2)));
        var key = hca.EncryptionType switch { 0 => new CriHcaKey(CriHcaKey.Type.Type0), 1 => new CriHcaKey(CriHcaKey.Type.Type1), 56 => new CriHcaKey(effective), _ => throw new InvalidDataException("Unsupported HCA cipher") };
        if (meta.Version == 0x300) { Hca3.Decode(bytes, meta, key, path); return hca.ChannelCount; }
        var frame = new CriHcaFrame(hca); var checksum = new Crc16(0x8005);
        for (var index = 0; index < hca.FrameCount; index++)
        {
            var data = bytes.AsSpan(hca.HeaderSize + index * hca.FrameSize, hca.FrameSize).ToArray();
            Require(checksum.Compute(data, data.Length - 2) == BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(data.Length - 2)), "HCA CRC mismatch");
            CriHcaEncryption.CryptFrame(hca, data, key, true);
            Require(UnpackFrame(frame, new BitReader(data)), "HCA key or block validation failed");
        }
        reader.EncryptionKey = key;
        var audio = reader.Read(bytes);
        using var wav = File.Create(path); new WaveWriter().WriteToStream(audio, wav);
        return hca.ChannelCount;
    }
    // FFmpeg's ADX demuxer rejects the short final read left by the standard end frame of
    // most mono and some stereo streams, and its decoder drifts from CRI's scale + 1 and
    // truncated coefficients. VGAudio matches CRI's arithmetic and reads only declared frames.
    public static int DecodeAdx(byte[] bytes, Config config, string path)
    {
        var meta = new AdxReader().ReadMetadata(new MemoryStream(bytes));
        Require(meta.HeaderSize >= 6 && meta.HeaderSize + 4 <= bytes.Length && bytes.AsSpan(meta.HeaderSize - 2, 6).SequenceEqual("(c)CRI"u8), "Invalid ADX header");
        Require(meta.EncodingType == CriAdxType.Linear && meta.FrameSize == 18 && meta.BitDepth == 4 && meta.Revision == 0, "Unsupported ADX format");
        var frames = ((long)meta.SampleCount + meta.SamplesPerFrame - 1) / meta.SamplesPerFrame;
        Require(meta.ChannelCount is 1 or 2 && meta.SampleCount > 0 &&
            meta.HeaderSize + 4L + frames * meta.FrameSize * meta.ChannelCount <= bytes.Length &&
            (long)meta.SampleCount * meta.ChannelCount * 2 <= config.ExpandedBytes, "ADX size/channel budget");
        var audio = new AdxReader().Read(bytes);
        using var wav = File.Create(path); new WaveWriter().WriteToStream(audio, wav);
        return meta.ChannelCount;
    }
    private sealed record Cue(long Id, string Name);
    private static async Task Acb(string path, Worker.Output output, CancellationToken token)
    {
        var config = output.Job.Config; using var file = File.OpenRead(path);
        var root = CriTables.Parse(BinaryTools.ReadLimited(file, config.InputBytes)); Require(root.Length == 1, "ACB header rows");
        var header = root[0];
        Dictionary<string, object?>[] Table(string name, bool optional = false) => optional && (!header.TryGetValue(name, out var b) || b is not byte[] { Length: > 0 }) ? [] : CriTables.Parse(header.Bytes(name));
        var cues = Table("CueTable"); var names = Table("CueNameTable"); var waves = Table("WaveformTable");
        var synths = Table("SynthTable", true); var sequences = Table("SequenceTable", true); var tracks = Table("TrackTable", true);
        var events = Table(header.ContainsKey("TrackEventTable") ? "TrackEventTable" : "CommandTable", true);
        var references = new SortedDictionary<int, List<Cue>>();
        for (var cueIndex = 0; cueIndex < cues.Length; cueIndex++)
        {
            var cue = cues[cueIndex]; var named = names.FirstOrDefault(n => n.Number("CueIndex", -1) == cueIndex);
            var label = named?.String("CueName", $"cue-{cueIndex}") ?? $"cue-{cueIndex}";
            var visited = new HashSet<(int, int)>();
            Visit((int)cue.Number("ReferenceType"), (int)cue.Number("ReferenceIndex"));
            void Visit(int type, int index)
            {
                Require(visited.Count < 10000, "ACB reference limit"); if (!visited.Add((type, index))) return;
                Dictionary<string, object?> Row(Dictionary<string, object?>[] table) { Require(index >= 0 && index < table.Length, "ACB reference missing"); return table[index]; }
                switch (type)
                {
                    case 0: return;
                    case 1:
                        var wave = Row(waves); Require(wave.Number("Streaming") == 0, "External AWB is unsupported");
                        var id = (int)wave.Number("MemoryAwbId", wave.Number("Id", -1)); Require(id >= 0, "Missing AWB waveform ID");
                        if (!references.TryGetValue(id, out var list)) references[id] = list = [];
                        list.Add(new(cue.Number("CueId", cueIndex), label)); break;
                    case 2:
                        var refs = Row(synths).Bytes("ReferenceItems"); Require(refs.Length % 4 == 0, "Invalid synth references");
                        for (var at = 0; at < refs.Length; at += 4) Visit(BinaryPrimitives.ReadUInt16BigEndian(refs.AsSpan(at)), BinaryPrimitives.ReadUInt16BigEndian(refs.AsSpan(at + 2)));
                        break;
                    case 3:
                    case 8:
                        var sequence = Row(sequences); var indices = sequence.Bytes("TrackIndex"); var count = sequence.Number("NumTracks"); Require(count >= 0 && count * 2 <= indices.Length, "Sequence tracks truncated");
                        for (var i = 0; i < count; i++)
                        {
                            var trackIndex = BinaryPrimitives.ReadUInt16BigEndian(indices.AsSpan(i * 2)); Require(trackIndex < tracks.Length, "Track missing");
                            var eventIndex = tracks[trackIndex].Number("EventIndex", -1); if (eventIndex == 65535) continue;
                            Require(eventIndex >= 0 && eventIndex < events.Length, "Track event missing");
                            var command = events[eventIndex].Bytes("Command"); var r = new CriTables.Cursor(command);
                            while (r.Position < command.Length)
                            {
                                var code = r.U16(); var length = r.Byte(); var args = r.Take(length);
                                if (code == 0) break;
                                if (code == 0x7d0) { Require(args.Length >= 4, "Invalid cue command"); Visit(BinaryPrimitives.ReadUInt16BigEndian(args), BinaryPrimitives.ReadUInt16BigEndian(args[2..])); }
                            }
                        }
                        break;
                    default: throw new InvalidDataException($"Unsupported cue reference {type}");
                }
            }
        }
        Require(references.Count is > 0 and <= 10000, "No supported embedded waveforms");
        var awb = header.Bytes("AwbFile"); Require(awb.Length >= 16 && awb.AsSpan(0, 4).SequenceEqual("AFS2"u8), "Missing embedded AWB");
        var countAwb = BinaryPrimitives.ReadUInt32LittleEndian(awb.AsSpan(8)); var align = BinaryPrimitives.ReadUInt16LittleEndian(awb.AsSpan(12)); var subkey = BinaryPrimitives.ReadUInt16LittleEndian(awb.AsSpan(14));
        int idSize = awb[6], offsetSize = awb[5], position = 16;
        Require(countAwb <= 10000 && align > 0 && idSize is 2 or 4 && offsetSize is 2 or 4, "AWB header limits");
        uint Number(int size) { Require(position + size <= awb.Length, "Truncated AWB table"); var value = size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(awb.AsSpan(position)) : BinaryPrimitives.ReadUInt32LittleEndian(awb.AsSpan(position)); position += size; return value; }
        var ids = Enumerable.Range(0, (int)countAwb).Select(_ => Number(idSize)).ToArray(); var offsets = Enumerable.Range(0, (int)countAwb + 1).Select(_ => Number(offsetSize)).ToArray();
        Require(ids.Distinct().Count() == ids.Length, "Duplicate AWB IDs");
        long expanded = 0;
        foreach (var (id, labels) in references)
        {
            var index = Array.IndexOf(ids, (uint)id); Require(index >= 0, "Referenced waveform missing");
            var start = ((long)offsets[index] + align - 1) / align * align;
            var end = index + 1 < countAwb ? ((long)offsets[index + 1] + align - 1) / align * align : offsets[index + 1];
            Require(start >= position && end > start && end <= awb.Length, "AWB waveform range"); expanded += end - start; Require(expanded <= config.ExpandedBytes, "Waveform budget");
            var wav = Path.Combine(Path.GetDirectoryName(output.Job.Output)!, $"wave-{id}.wav");
            var channels = DecodeHca(awb.AsSpan((int)start, (int)(end - start)).ToArray(), subkey, config, wav);
            var original = await Probe(config, wav, token); var destination = output.PathFor("m4a");
            await Ffmpeg(config, ["-i", wav, "-map_metadata", "-1", "-c:a", "aac", "-b:a", channels == 1 ? "96k" : "192k", "-movflags", "+faststart", destination], token);
            var probe = await Validate(config, destination, [original], null, false, token);
            output.Add(destination, labels[0].Name, "audio/mp4", new { cues = labels.Distinct().ToArray(), probe }); File.Delete(wav);
        }
    }
    public static async Task<JsonObject> Probe(Config config, string path, CancellationToken token, bool packets = false)
    {
        var text = await Processes.Run(config.Ffprobe, ["-v", "error", packets ? "-count_packets" : "-count_frames", "-show_streams", "-show_format", "-of", "json", path], token);
        var result = JsonNode.Parse(text)!.AsObject(); result["format"]?.AsObject().Remove("filename"); return result;
    }
    private static Task<string> Ffmpeg(Config config, IEnumerable<string> args, CancellationToken token) =>
        Processes.Run(config.Ffmpeg, new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-y", "-threads", config.FfmpegThreads.ToString(CultureInfo.InvariantCulture) }.Concat(args), token);
    private static double FrameRate(JsonNode stream) { var parts = ((string?)stream["r_frame_rate"] ?? "0/1").Split('/'); return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture); }
    // A copied video stream holds the source packets, which were fully decoded while probing the source.
    private static async Task<JsonObject> Validate(Config config, string path, JsonObject[] originals, string? videoCodec, bool copied, CancellationToken token)
    {
        Require(new FileInfo(path).Length <= config.OutputBytes, "Output media budget");
        var probe = await Probe(config, path, token, copied); var streams = probe["streams"]!.AsArray();
        Require(streams.Count == originals.Length && streams.Count(s => (string?)s?["codec_type"] == "video") == (videoCodec != null ? 1 : 0), "Output track count mismatch");
        foreach (var original in originals)
        {
            var sourceStreams = original["streams"]!.AsArray(); Require(sourceStreams.Count == 1, "Ambiguous source tracks"); var src = sourceStreams[0]!;
            var kind = (string?)src["codec_type"]; var dst = streams.SingleOrDefault(s => (string?)s?["codec_type"] == kind); Require(dst != null, "Output track missing");
            Require((string?)dst!["codec_name"] == (kind == "video" ? videoCodec : "aac"), "Output codec mismatch");
            double? Duration(JsonNode s, JsonNode p) => double.TryParse((string?)(s["duration"] ?? p["format"]?["duration"]), CultureInfo.InvariantCulture, out var duration) && double.IsFinite(duration) ? duration : null;
            var sourceDuration = Duration(src, original); var outputDuration = Duration(dst, probe);
            if (sourceDuration is { } duration) Require(outputDuration != null && Math.Abs(outputDuration.Value - duration) <= Math.Max(0.2, duration * 0.02), "Output media truncated");
            if (kind == "video")
            {
                Require(long.TryParse((string?)src["nb_read_frames"], out var frames) && frames > 0 && long.TryParse((string?)dst[copied ? "nb_read_packets" : "nb_read_frames"], out var outputFrames) && frames == outputFrames, "Video frame count mismatch");
                Require(double.IsFinite(FrameRate(src)) && Math.Abs(FrameRate(src) - FrameRate(dst)) < 0.001, "Video frame rate changed");
            }
            else Require(sourceDuration != null, "Missing audio duration");
            if (kind == "audio") Require((int?)dst["channels"] == (int?)src["channels"] && (string?)dst["sample_rate"] == (string?)src["sample_rate"], "Audio format changed");
            else Require((int?)dst["width"] == (copied ? (int)src["width"]! : ((int)src["width"]! + 1) / 2 * 2) && (int?)dst["height"] == (copied ? (int)src["height"]! : ((int)src["height"]! + 1) / 2 * 2), "Video dimensions changed");
        }
        if (!copied) await Ffmpeg(config, ["-i", path, "-map", "0", "-f", "null", "-"], token);
        else if (originals.Length > 1) await Ffmpeg(config, ["-i", path, "-map", "0:a", "-f", "null", "-"], token);
        return probe;
    }
    private static async Task Movie(string path, Worker.Output output, CancellationToken token)
    {
        var config = output.Job.Config; var stage = Path.GetDirectoryName(output.Job.Output)!;
        var demuxed = Usm.Demux(path, stage, config);
        var video = demuxed.Video; var audio = demuxed.Audio;
        var original = await Probe(config, video, token); var originals = new List<JsonObject> { original };
        var args = new List<string>();
        var source = original["streams"]![0]!;
        // Browsers play VP9 directly, so copying it avoids a lossy CPU-bound H.264 encode.
        var copy = (string?)source["codec_name"] == "vp9";
        if (demuxed.Frames is { } frames)
            Require(long.TryParse((string?)source["nb_read_frames"], out var decoded) && decoded == frames, "USM source frame count mismatch");
        if (demuxed.RateNumerator is { } numerator && demuxed.RateDenominator is { } denominator)
        {
            var rate = $"{numerator}/{denominator}";
            Require(long.TryParse((string?)source["nb_read_frames"], out var count) && count > 0, "Missing source frame count");
            // FFmpeg keeps container timestamps when copying and ignores -r, so only copy
            // when the IVF timing already matches the USM header; otherwise re-encode.
            copy &= Math.Abs(FrameRate(source) - (double)numerator / denominator) < 0.001;
            // Raw MPEG duration/r_frame_rate can be bitrate/field-rate estimates.
            // The USM stream header is authoritative for presentation timing.
            source["r_frame_rate"] = rate;
            source["duration"] = (count * (double)denominator / numerator).ToString("R", CultureInfo.InvariantCulture);
            if (!copy) args.AddRange(["-r", rate]);
        }
        args.AddRange(["-i", video]);
        if (audio != null)
        {
            // Demux yields only HCA or ADX; both are decoded here instead of by FFmpeg.
            var wav = audio + ".wav"; var encoded = File.ReadAllBytes(audio);
            if (audio.EndsWith(".adx", StringComparison.Ordinal)) DecodeAdx(encoded, config, wav); else DecodeHca(encoded, 0, config, wav);
            audio = wav;
            var ap = await Probe(config, audio, token); var channels = (int?)ap["streams"]?[0]?["channels"]; Require(channels is 1 or 2, "Unsupported channels");
            originals.Add(ap); args.AddRange(["-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", channels == 1 ? "96k" : "192k"]);
        }
        else args.AddRange(["-map", "0:v:0"]);
        var destination = output.PathFor("mp4");
        args.AddRange(["-map_metadata", "-1"]);
        args.AddRange(copy ? ["-c:v", "copy"] : ["-c:v", "libx264", "-threads", config.FfmpegThreads.ToString(CultureInfo.InvariantCulture), "-crf", "20", "-preset", "medium", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p"]);
        args.AddRange(["-movflags", "+faststart", destination]);
        await Ffmpeg(config, args, token); var probe = await Validate(config, destination, originals.ToArray(), copy ? "vp9" : "h264", copy, token);
        output.Add(destination, output.Job.Target.Key, "video/mp4", probe);
    }
}
