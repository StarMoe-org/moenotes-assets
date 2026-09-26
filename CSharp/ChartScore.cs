using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MoenotesAssets;

// Runtime notes of a shipped chart (Live/MusicScore), the `score/<file>.notes.json` of an ournotes-player chart.
// A port of nnnotes score.convert (MIT, see THIRD_PARTY_NOTICES.md), itself a re-implementation of the game's
// converter (SsRootDeserializer -> SsTickConverter -> SsMusicScoreConverter -> MusicScoreNoteCreator). Every
// (float) cast mirrors a float32 step of the game; doubles stay doubles. Sorts are stable, as in the reference.
public static class ChartScore
{
    public const int LaneCount = 24;
    const int MaxLines = 20;
    public static readonly string[] Difficulties = ["easy", "normal", "hard", "expert"];
    static readonly string[] NoteTypes = ["tap", "flick", "trace", "long", "guide", "node"];
    static readonly string[] FlickDirs = ["up", "left", "right", "down"];
    static readonly string[] Eases = ["linear", "in", "out"];
    static readonly string[] Fades = ["none", "in", "out"];
    static readonly Dictionary<int, string> OpNames = new()
    {
        [0] = "None",
        [1] = "Normal",
        [20] = "SlideBegin",
        [21] = "SlideConnection",
        [22] = "SlideEnd",
        [40] = "Flick",
        [41] = "SlideBeginFlick",
        [42] = "SlideEndFlick",
        [60] = "Trace",
        [61] = "SlideBeginTrace",
        [62] = "SlideEndTrace",
        [63] = "SlideConnectionTrace",
        [80] = "HiddenSlideBegin",
        [82] = "HiddenSlideEnd",
        [100] = "GuideBegin",
        [101] = "GuideBeginNormal",
        [102] = "GuideBeginFlick",
        [103] = "GuideEnd",
        [104] = "GuideBeginTrace",
        [105] = "GuideEndTrace",
        [120] = "Combo",
        [121] = "ComboSkip",
        [122] = "Hidden",
        [123] = "InvalidHidden",
    };
    static readonly string[] LineEaseNames = ["Linear", "EaseIn", "EaseOut"];
    static readonly string[] DirectionNames = ["Normal", "Left", "Right"];
    static readonly Dictionary<int, string> JudgementTypeNames = new()
    {
        [0] = "None",
        [1] = "Normal",
        [2] = "EasyNormal",
        [5] = "Flick",
        [10] = "SlideBegin",
        [11] = "SlideEnd",
        [12] = "SlideEndFlick",
        [15] = "SlideBeginEasy",
        [21] = "Trace",
        [22] = "SlideEndTrace",
    };
    static readonly string[] OffsetTypeNames = ["Default", "Slide", "SlideBegin", "SlideEnd", "Flick", "Trace", "SlideMin", "SlideMax", "EasyDefault", "EasySlideBegin"];
    static readonly HashSet<int> SlideBeginClass = [20, 41, 61, 80, 104];
    static readonly HashSet<int> SlideEndClass = [22, 42, 62, 82, 105];
    static readonly HashSet<int> Mergeable = [20, 22, 41, 42, 61, 62, 80, 82, 100, 101, 102, 103, 104, 105];

    // ------------------------------------------------------------------ numeric helpers
    /// System.Math.Round(double) as the game's inlined IL2CPP code does it (ties to even; negatives truncate x - 0.5).
    public static double NetRound(double x)
    {
        var ip = Math.Truncate(x); var frac = x - ip;
        if (x >= 0.0)
        {
            if (frac == 0.5) return ((long)ip & 1) != 0 ? ip + 1.0 : ip;
            return Math.Floor(x + 0.5);
        }
        if (frac == -0.5) return ((long)ip & 1) != 0 ? ip - 1.0 : ip;
        return (double)(long)(x - 0.5);
    }
    static int NetRoundInt(double x) { var v = NetRound(x); return double.IsFinite(v) ? checked((int)v) : int.MinValue; }
    static bool IsJudgementNote(int op) => op is not (0 or 80 or 82 or 100 or 103 or 121 or 122 or 123);
    static bool IsFlickType(int op) => op is 40 or 41 or 42 or 102;
    static int JudgementType(int op, bool crit) => op switch
    {
        0 or 21 => op,
        1 => crit ? 2 : 1,
        20 => crit ? 15 : 10,
        22 => 11,
        40 or 41 or 42 or 102 => 5,
        60 or 61 or 63 or 104 or 105 or 120 => 21,
        62 => 22,
        80 or 82 or 100 or 101 or 103 or 121 or 122 => 1,
        _ => throw new InvalidDataException($"ConvertJudgementType: operateType {op}"),
    };
    /// UnityEngine.Mathf.Approximately, without flush-to-zero.
    static bool Approximately(float a, float b)
    {
        var m = Math.Max(Math.Abs(a), Math.Abs(b));
        var tol = Math.Max((float)(m * 1e-6f), (float)(float.Epsilon * 8f));
        return Math.Abs((float)(b - a)) < tol;
    }
    static float Ease(float t, int kind) => kind switch
    {
        0 => t,
        2 => (float)(t * t),
        1 => (float)((float)(2f - t) * t),
        _ => throw new InvalidDataException($"noteLineEaseType {kind}"),
    };
    static int ConvertEase(int e) => e == 1 ? 2 : e == 2 ? 1 : 0;
    static int FlickDirection(SsNote n) => n.Dir == 1 ? 1 : n.Dir == 2 ? 2 : 0;
    static int MirrorDirection(int d, bool mirror) => !mirror ? d : d == 1 ? 2 : d == 2 ? 1 : 0;
    static float ApplyMirror(float lane, float width, bool mirror) =>
        mirror ? (float)((float)(24f - (float)(lane + 0f)) - width) : (float)(lane + 0f);
    static (int, int, int) OverlapKey(int t, float pos, float size) => (t, NetRoundInt(pos), NetRoundInt(size));

    // ------------------------------------------------------------------ SsRoot (ReadNote / ReadEvents)
    sealed class SsNote
    {
        public int Type, T, Dir, EaseL, EaseR, Alpha;
        public float Pos, Size = 6f;
        public bool PosAuto, Crit, Visible = true;
        public List<SsNote>? Node;
        public int[] Src = [];
    }
    sealed record SsEvents(List<(int T, float Bpm)> Bpm, List<(int T, int Num, int Den)> Sig, List<int> Skill, List<(int A, int B)> Fever, List<(int T, int[] Timing)> Call);

    static bool Present(JsonElement o, string key, out JsonElement value) =>
        o.TryGetProperty(key, out value) && value.ValueKind != JsonValueKind.Null;
    static JsonElement Get(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) ? v : throw new InvalidDataException($"ss missing {key}");
    /// (int)JToken: floats round half to even.
    static int ToInt(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Number when IsIntegral(v) => checked((int)v.GetInt64()),
        JsonValueKind.Number => NetRoundInt(v.GetDouble()),
        JsonValueKind.String => int.Parse(v.GetString()!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("ss int value"),
    };
    static bool IsIntegral(JsonElement v) { var raw = v.GetRawText(); return raw.IndexOfAny(['.', 'e', 'E']) < 0 && v.TryGetInt64(out _); }
    static float ToF32(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => (float)v.GetDouble(),
        JsonValueKind.String => (float)double.Parse(v.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.True => 1f,
        JsonValueKind.False => 0f,
        _ => throw new InvalidDataException("ss float value"),
    };
    static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        _ => v.EnumerateObject().Any(),
    };
    static int Enum(string[] table, JsonElement v, string what)
    {
        var index = v.ValueKind == JsonValueKind.String ? Array.IndexOf(table, v.GetString()) : -1;
        return index >= 0 ? index : throw new InvalidDataException($"ss {what} value '{(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())}'");
    }

    static SsNote ReadNote(JsonElement n, int[] src)
    {
        Config.Require(n.ValueKind == JsonValueKind.Object, "ss note");
        var o = new SsNote { Src = src };
        if (n.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) o.Type = Enum(NoteTypes, type, "type");
        if (Present(n, "t", out var t)) o.T = ToInt(t);
        if (Present(n, "pos", out var pos))
        {
            if (pos.ValueKind == JsonValueKind.String && pos.GetString() == "auto") o.PosAuto = true;
            else o.Pos = ToF32(pos);
        }
        if (Present(n, "size", out var size)) o.Size = ToF32(size);
        if (Present(n, "crit", out var crit)) o.Crit = Truthy(crit);
        if (n.TryGetProperty("dir", out var dir) && dir.ValueKind == JsonValueKind.String) o.Dir = Enum(FlickDirs, dir, "dir");
        if (Present(n, "ease", out var ease))
        {
            if (ease.ValueKind == JsonValueKind.Array) { o.EaseL = Enum(Eases, ease[0], "ease"); o.EaseR = Enum(Eases, ease[1], "ease"); }
            else o.EaseL = o.EaseR = Enum(Eases, ease, "ease");
        }
        if (Present(n, "visible", out var visible)) o.Visible = Truthy(visible);
        if (n.TryGetProperty("alpha", out var alpha) && alpha.ValueKind == JsonValueKind.String) o.Alpha = Math.Max(0, Array.IndexOf(Fades, alpha.GetString()));
        if (n.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Array)
            o.Node = node.EnumerateArray().Select((c, i) => ReadNote(c, [.. src, i])).ToList();
        return o;
    }

    static IEnumerable<JsonElement> Items(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && Truthy(v) ? v.EnumerateArray() : [];

    static SsEvents ReadEvents(JsonElement ev) => new(
        Items(ev, "bpm").Select(b => (ToInt(Get(b, "t")), ToF32(Get(b, "bpm")))).ToList(),
        Items(ev, "sig").Select(s => (ToInt(Get(s, "t")), ToInt(Get(s, "sig")[0]), ToInt(Get(s, "sig")[1]))).ToList(),
        Items(ev, "skill").Select(ToInt).ToList(),
        Items(ev, "fever").Select(f => (ToInt(f[0]), ToInt(f[1]))).ToList(),
        Items(ev, "call").Select(c => (ToInt(Get(c, "t")), Get(c, "timing").EnumerateArray().Select(ToInt).ToArray())).ToList());

    /// MusicScoreLoader.Load: gunzip when the bytes start 1F 8B.
    public static byte[] Decode(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B) return data;
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress); using var output = new MemoryStream();
        input.CopyTo(output); return output.ToArray();
    }

    // ------------------------------------------------------------------ SsTickConverter
    sealed record Pos(int Bar, int Rhythm, int Unit, float Progress, int Ms)
    {
        public float Key => (float)(Progress + (float)Bar);
        public bool Before(int bar, float progress) => Bar < bar || (Bar == bar && Progress < progress);
        // the Python dataclass repr, as the reference writes it into the conversion log
        public override string ToString() => $"Pos(bar={Bar}, rhythm={Rhythm}, unit={Unit}, progress=np.float32({PyFloat.Repr(Progress)}), ms={Ms})";
    }

    sealed class TickConverter
    {
        public readonly List<(int Tick, int Bar, int Tpm, int Num, int Den)> Sig = [];
        public readonly List<(int Tick, int Ms, float Bpm)> Bpm = [];
        static int Quotient(int a, int b) => b != 0 ? (int)Math.Truncate((double)a / b) : 0;
        public TickConverter(List<(int T, int Num, int Den)> sigs, List<(int T, float Bpm)> bpms)
        {
            var sorted = sigs.OrderBy(s => s.T).ToList();
            if (sorted.Count == 0 || sorted[0].T > 0) Sig.Add((0, 0, 1920, 4, 4));
            int bar = 0, prevT = 0, tpm = 1920;
            foreach (var (t, num, den) in sorted)
            {
                var q = tpm != 0 ? (int)Math.Truncate((double)(t - prevT) / tpm) : 0;
                if (t - prevT != 0 && prevT <= t) bar += q;
                tpm = den != 0 ? (int)Math.Truncate(num * 1920.0 / den) : 0;
                Sig.Add((t, bar, tpm, num, den)); prevT = t;
            }
            var bpmSorted = bpms.OrderBy(b => b.T).ToList();
            if (bpmSorted.Count == 0 || bpmSorted[0].T > 0) Bpm.Add((0, 0, 120f));
            double acc = 0; int prev = 0; var prevBpm = 120f;
            foreach (var (t, bpm) in bpmSorted)
            {
                if (t - prev != 0 && prev <= t) acc += ((double)(t - prev) * 60000.0) / (double)(float)(prevBpm * 480f);
                Bpm.Add((t, NetRoundInt(acc), bpm)); prev = t; prevBpm = bpm;
            }
        }
        static T Find<T>(List<T> segs, int tick, Func<T, int> start)
        {
            for (int i = segs.Count - 1; i >= 0; i--) if (start(segs[i]) <= tick) return segs[i];
            return segs[0];
        }
        public int TimeMs(int tick)
        {
            var (t0, ms0, bpm) = Find(Bpm, tick, s => s.Tick);
            return (int)Math.Floor(((double)(tick - t0) * 60000.0) / (double)(float)(bpm * 480f) + ms0);
        }
        public (int Bar, int Rhythm, int Unit, float Progress) BarPosition(int tick)
        {
            var (t0, bar0, tpm, _, _) = Find(Sig, tick, s => s.Tick);
            var q = Quotient(tick - t0, tpm); var r = (tick - t0) - q * tpm;
            return (bar0 + q, r, tpm, (float)((float)r / (float)tpm));
        }
        public int BarHeadTick(int bar)
        {
            var seg = Sig[0];
            for (int i = Sig.Count - 1; i >= 0; i--) if (Sig[i].Bar <= bar) { seg = Sig[i]; break; }
            return seg.Tick + (bar - seg.Bar) * seg.Tpm;
        }
        public Pos Position(int tick) { var bp = BarPosition(tick); return new(bp.Bar, bp.Rhythm, bp.Unit, bp.Progress, TimeMs(tick)); }
    }

    /// MusicScoreUtility.GetTimeMsFromBar (float32, floor).
    static int TimeMsFromBar(int bar, float progress, List<(float Value, Pos P)> bpmEvents, List<(float Value, Pos P)> barEvents)
    {
        var @ref = new Pos(0, 0, 0, 0f, 0); var beats = 4f;
        foreach (var (value, p) in barEvents) if (p.Before(bar, progress)) { beats = value; if (p.Ms > @ref.Ms) @ref = p; }
        var bpm = 160f;
        foreach (var (value, p) in bpmEvents) if (p.Before(bar, progress)) { bpm = value; if (p.Ms > @ref.Ms) @ref = p; }
        var spb = (float)((float)(beats * 60f) / bpm);
        var x = (float)((float)(spb * (float)(bar - @ref.Bar)) + (float)(spb * (float)(progress - @ref.Progress)));
        x = (float)(x * 1000f);
        return @ref.Ms + (int)Math.Floor((double)x);
    }
    static float BarRhythm(int bar, List<(float Value, Pos P)> barEvents)
    {
        var v = 4f;
        foreach (var (value, p) in barEvents) { if (bar < p.Bar) return v; v = value; }
        return v;
    }
    static float BpmAtMs(int ms, List<(float Value, Pos P)> bpmEvents)
    {
        var v = 160f;
        foreach (var (value, p) in bpmEvents) if (p.Ms <= ms) v = value;
        return v;
    }
    static bool SamePosition(Pos a, Pos b) => a.Bar == b.Bar && Approximately(a.Progress, b.Progress);

    // ------------------------------------------------------------------ NoteInfoData
    sealed class NoteInfo
    {
        public int Bar, Rhythm, Unit, Lane, Width, Op, Direction, Ease, EaseR, Tick, Alpha;
        public float Progress, LaneF, WidthF;
        public bool SlideAlong, Crit, Visible;
        public List<int> Lines = [];
        public int[] Src = [];
        public float Key => (float)(Progress + (float)Bar);
    }

    sealed class LineIndexAssigner
    {
        readonly List<(int Index, int End)> inUse = [];
        public int Acquire(int startTick)
        {
            for (int i = inUse.Count - 1; i >= 0; i--) if (inUse[i].End < startTick) inUse.RemoveAt(i);
            var used = inUse.Select(x => x.Index).ToHashSet();
            for (int i = 0; i < MaxLines; i++) if (!used.Contains(i)) return i;
            throw new InvalidDataException($"ss lineIndex {MaxLines} startTick {startTick}");
        }
        public void Register(int index, int endTick) => inUse.Add((index, endTick));
    }

    static NoteInfo SingleInfo(SsNote n, TickConverter tc, bool mirror)
    {
        var bp = tc.BarPosition(n.T); var laneF = ApplyMirror(n.Pos, n.Size, mirror);
        var (op, d) = n.Type == 1 ? (40, FlickDirection(n)) : n.Type == 2 ? (60, 0) : (1, 0);
        return new()
        {
            Bar = bp.Bar,
            Rhythm = bp.Rhythm,
            Unit = bp.Unit,
            Progress = bp.Progress,
            Lane = NetRoundInt(laneF),
            LaneF = laneF,
            Width = Math.Max(1, NetRoundInt(n.Size)),
            WidthF = n.Size,
            Op = op,
            Crit = n.Crit,
            Direction = MirrorDirection(d, mirror),
            Src = n.Src,
            Tick = n.T,
            Visible = n.Visible,
            Alpha = n.Alpha,
        };
    }

    static int LongNodeOp(List<SsNote> nodes, int i)
    {
        var n = nodes[i];
        if (i == 0) return !n.Visible ? 80 : n.Type == 1 ? 41 : n.Type == 2 ? 61 : 20;
        if (i == nodes.Count - 1) return !n.Visible ? 82 : n.Type == 1 ? 42 : n.Type == 2 ? 62 : 22;
        if (n.PosAuto) return 21;
        if (!n.Visible) return 122;
        return n.Type == 2 ? 63 : 21;
    }

    static int GuideNodeOp(List<SsNote> nodes, int i, Dictionary<(int, int, int), SsNote>? guideMap)
    {
        var n = nodes[i];
        if (i == 0)
        {
            if (guideMap is not null && !n.PosAuto && guideMap.TryGetValue(OverlapKey(n.T, n.Pos, n.Size), out var s) && s.Type < 3)
                return new[] { 101, 102, 104 }[s.Type];
            return 100;
        }
        if (i == nodes.Count - 1) return !n.Visible ? 103 : n.Type == 2 ? 105 : 103;
        if (n.PosAuto) return 63;
        return !n.Visible ? 122 : 63;
    }

    static void LineInfos(List<SsNote> chains, bool isLong, TickConverter tc, bool mirror, List<NoteInfo> output,
        Dictionary<(int, int, int), SsNote>? guideMap, LineIndexAssigner assigner)
    {
        foreach (var chain in chains)
        {
            var nodes = chain.Node!; var count = nodes.Count;
            var lineIndex = assigner.Acquire(nodes[0].T); var endTick = nodes[^1].T;
            for (int i = 0; i < count; i++)
            {
                var nd = nodes[i];
                var op = isLong ? LongNodeOp(nodes, i) : GuideNodeOp(nodes, i, guideMap);
                float pos, size;
                if (!nd.PosAuto) (pos, size) = (nd.Pos, nd.Size);
                else if (i == 0 || i >= count - 1) (pos, size) = (nodes[0].Pos, nodes[0].Size);
                else
                {
                    var j = i;
                    while (true)                          // nearest non-auto node in [1, i-1], else node 0
                    {
                        if (j < 2) { j = 0; break; }
                        j--;
                        if (!nodes[j].PosAuto) break;
                    }
                    var k = i + 1;
                    while (k < count - 1 && nodes[k].PosAuto) k++;
                    var a = nodes[j]; var b = nodes[k]; var span = b.T - a.T;
                    var prog = span < 1 ? 0f : (float)((float)(nd.T - a.T) / (float)span);
                    var el = Ease(prog, ConvertEase(a.EaseL)); var er = Ease(prog, ConvertEase(a.EaseR));
                    pos = (float)(a.Pos + (float)(el * (float)(b.Pos - a.Pos)));
                    var aRight = (float)(a.Pos + a.Size);
                    var right = (float)(aRight + (float)(er * (float)((float)(b.Pos + b.Size) - aRight)));
                    size = (float)(right - pos);
                }
                var bp = tc.BarPosition(nd.T); var laneF = ApplyMirror(pos, size, mirror);
                var dirSource = nd;
                if (guideMap is not null && i == 0 && !isLong && !nd.PosAuto && guideMap.TryGetValue(OverlapKey(nd.T, nd.Pos, nd.Size), out var single))
                    dirSource = single;
                var d = dirSource.Type == 1 ? FlickDirection(dirSource) : 0;
                output.Add(new()
                {
                    Bar = bp.Bar,
                    Rhythm = bp.Rhythm,
                    Unit = bp.Unit,
                    Progress = bp.Progress,
                    Lane = NetRoundInt(laneF),
                    LaneF = laneF,
                    Width = Math.Max(1, NetRoundInt(size)),
                    WidthF = size,
                    Op = op,
                    SlideAlong = nd.PosAuto,
                    Crit = nd.Crit,
                    Direction = MirrorDirection(d, mirror),
                    Ease = ConvertEase(nd.EaseL),
                    EaseR = ConvertEase(nd.EaseR),
                    Lines = [lineIndex],
                    Src = nd.Src,
                    Tick = nd.T,
                    Visible = nd.Visible,
                    Alpha = nd.Alpha,
                });
            }
            assigner.Register(lineIndex, endTick);
        }
    }

    static List<NoteInfo> BuildNoteInfos(List<SsNote> notes, TickConverter tc, bool mirror)
    {
        var guideStarts = new HashSet<(int, int, int)>();
        foreach (var n in notes)
            if (n.Type == 4 && n.Node is { Count: > 0 } && !n.Node[0].PosAuto) guideStarts.Add(OverlapKey(n.Node[0].T, n.Node[0].Pos, n.Node[0].Size));
        var guideMap = new Dictionary<(int, int, int), SsNote>(); var merged = new HashSet<int>();
        for (int i = 0; i < notes.Count; i++)
        {
            if (notes[i].Type >= 3) continue;
            var key = OverlapKey(notes[i].T, notes[i].Pos, notes[i].Size);
            if (guideStarts.Contains(key) && guideMap.TryAdd(key, notes[i])) merged.Add(i);
        }
        var output = new List<NoteInfo>();
        for (int i = 0; i < notes.Count; i++) if (!merged.Contains(i) && notes[i].Type < 3) output.Add(SingleInfo(notes[i], tc, mirror));
        List<SsNote> Chains(int type) => notes.Select((n, i) => (n, i)).Where(x => x.n.Type == type && x.n.Node is { Count: > 1 })
            .OrderBy(x => x.n.Node![0].T).ThenBy(x => x.i).Select(x => x.n).ToList();
        var assigner = new LineIndexAssigner();
        LineInfos(Chains(3), true, tc, mirror, output, null, assigner);
        LineInfos(Chains(4), false, tc, mirror, output, guideMap, assigner);
        return output;
    }

    /// BuildNoteInfoDictionary + TryMergeSlideEndpoint: bar key -> lane -> infos, both in first-insertion order.
    static Dictionary<float, List<(int Lane, List<NoteInfo> Infos)>> InfoDictionary(List<NoteInfo> infos, List<string> log)
    {
        var d = new Dictionary<float, List<(int Lane, List<NoteInfo> Infos)>>();
        foreach (var n in infos)
        {
            if (!d.TryGetValue(n.Key, out var lanes)) d[n.Key] = lanes = [];
            var index = lanes.FindIndex(x => x.Lane == n.Lane);
            if (index < 0) { lanes.Add((n.Lane, [])); index = lanes.Count - 1; }
            var list = lanes[index].Infos;
            if (Mergeable.Contains(n.Op) && n.Lines.Count > 0)
            {
                var hit = list.FirstOrDefault(e => e.Op == n.Op && e.Width == n.Width && e.Crit == n.Crit && e.Direction == n.Direction && e.Ease == n.Ease && e.EaseR == n.EaseR);
                if (hit is not null) { hit.Lines.AddRange(n.Lines); continue; }
            }
            if (IsJudgementNote(n.Op) && list.Count(e => IsJudgementNote(e.Op)) > 2)
                log.Add($"3 bar {n.Bar} barProgress {PyFloat.Repr(n.Progress)} lane {n.Lane} op {OpNames[n.Op]}");
            list.Add(n);
        }
        return d;
    }
    static int Priority(int op) => op is 20 or 41 or 61 or 80 or 100 or 101 or 102 or 104 ? 8 : op == 122 ? 9 : 10;

    // ------------------------------------------------------------------ MusicScoreNoteCreator
    sealed class Note
    {
        public int Id, Op, LaneStart, LaneEnd, Direction, Ease, EaseR, OffsetType, PairId;
        public required Pos Pos;
        public float LaneStartF, LaneEndF, Width;
        public bool SlideAlong, Crit;
        public List<int> LineIds = [];
        public int? Fever;
        public NoteInfo? Info;
        public readonly List<Note> ViewNotes = [], ComboNotes = [], BeginNotes = [], EndCombo = [];
        public Note? BeginRef, EndRef;
    }

    sealed class Creator(int startId, List<(float Value, Pos P)> bpmEvents, List<(float Value, Pos P)> barEvents, List<(int Index, Pos Start, Pos End)> fevers)
    {
        int curId = startId, curLine = startId, curGuideLine = startId;
        Note? pairTmp;
        readonly Dictionary<int, Note> beginByLine = [];
        readonly int[] lineArr = Enumerable.Repeat(-1, MaxLines).ToArray(), guideArr = Enumerable.Repeat(-1, MaxLines).ToArray();
        public readonly List<string> Log = [];

        Note Make(NoteInfo info, Pos pos, int op, List<int> lineIds, int offset) => new()
        {
            Id = curId,
            Pos = pos,
            Op = op,
            LaneStart = info.Lane,
            LaneEnd = info.Lane + info.Width - 1,
            LaneStartF = info.LaneF,
            LaneEndF = (float)((float)(info.LaneF + info.WidthF) - 1f),
            Width = info.WidthF,
            SlideAlong = info.SlideAlong,
            Crit = info.Crit,
            Direction = info.Direction,
            LineIds = lineIds,
            Ease = info.Ease,
            EaseR = info.EaseR,
            OffsetType = offset,
            Info = info,
        };

        public Note? Create(NoteInfo info)
        {
            var ms = TimeMsFromBar(info.Bar, info.Progress, bpmEvents, barEvents);
            var pos = new Pos(info.Bar, info.Rhythm, info.Unit, info.Progress, ms);
            curId++;
            var op = info.Op; var crit = info.Crit; Note note;
            switch (op)
            {
                case 1: note = Make(info, pos, op, [], crit ? 8 : 0); break;
                case 20 or 41 or 61 or 80:
                    {
                        var ids = new List<int>();
                        foreach (var li in info.Lines) { curLine++; ids.Add(curLine); lineArr[li] = curLine; }
                        note = Make(info, pos, op, ids, op switch { 20 => crit ? 9 : 2, 41 => 4, _ => 5 });
                        foreach (var lid in ids) beginByLine[lid] = note;
                        break;
                    }
                case 21 or 63:
                    {
                        var lid = (op == 21 ? lineArr : guideArr)[info.Lines[0]];
                        note = Make(info, pos, op, [lid], op == 21 ? 1 : 5);
                        if (beginByLine.TryGetValue(lid, out var b)) b.ViewNotes.Add(note);
                        break;
                    }
                case 22 or 42 or 62 or 82:
                    {
                        var ids = new List<int>();
                        foreach (var li in info.Lines) { ids.Add(lineArr[li]); lineArr[li] = -1; }
                        note = Make(info, pos, op, ids, op switch { 22 => 3, 42 => 4, _ => 5 });
                        EndLine(note);
                        break;
                    }
                case 40: note = Make(info, pos, op, [], 4); break;
                case 60: note = Make(info, pos, op, [], 5); break;
                case 100 or 101 or 102 or 104:
                    {
                        var ids = new List<int>();
                        foreach (var li in info.Lines) { var lid = curGuideLine + 10001; curGuideLine++; ids.Add(lid); guideArr[li] = lid; }
                        note = Make(info, pos, op, ids, op == 104 ? 5 : 0);
                        foreach (var lid in ids) beginByLine[lid] = note;
                        break;
                    }
                case 103 or 105:
                    {
                        var ids = new List<int>();
                        foreach (var li in info.Lines) { ids.Add(guideArr[li]); guideArr[li] = -1; }
                        note = Make(info, pos, op, ids, op == 105 ? 5 : 0);
                        EndLine(note);
                        break;
                    }
                case 122:
                    {
                        var lid = lineArr[info.Lines[0]];
                        if (lid < 1)
                        {
                            lid = guideArr[info.Lines[0]];
                            if (lid < 1) { Log.Add($"noteId {curId} pos {pos}"); return null; }
                        }
                        note = Make(info, pos, op, [lid], 0);
                        if (beginByLine.TryGetValue(lid, out var b)) b.ViewNotes.Add(note);
                        break;
                    }
                default: Log.Add($"noteId {curId} type {(OpNames.TryGetValue(op, out var name) ? name : op.ToString(CultureInfo.InvariantCulture))}"); return null;
            }
            if (op is 1 or 20 or 22 or 40 or 41 or 42)            // IsPairNoteType -> TrySetPairNoteId
            {
                if (pairTmp is not null && SamePosition(pairTmp.Pos, note.Pos)) { note.PairId = pairTmp.Id; pairTmp.PairId = note.Id; }
                pairTmp = note;
            }
            note.Fever = FeverOf(note.Pos.Ms);
            return note;
        }

        int? FeverOf(int ms)
        {
            foreach (var (index, start, end) in fevers) if (start.Ms <= ms && ms <= end.Ms) return index;
            return null;
        }

        /// TrySetSlideNoteId, then SlideEndNote.AddBeginNote.
        void EndLine(Note end)
        {
            var begins = new List<Note>(); var ok = true;
            foreach (var lid in end.LineIds)
            {
                if (!beginByLine.TryGetValue(lid, out var begin)) { ok = false; break; }
                begins.Add(begin);
                var notes = begin.ViewNotes.Where(x => x.LineIds.Contains(lid)).OrderBy(x => x.Pos.Bar).ThenBy(x => (double)x.Pos.Progress).ToList();
                notes.Add(end);
                var cur = notes.First(x => !x.SlideAlong);
                var startPos = begin.Pos; var endPos = end.Pos;
                var mids = notes.Where(x => !ReferenceEquals(x, end) && IsJudgementNote(x.Op)).Select(x => x.Pos).ToList();
                var beats = BeatsBetween(startPos, endPos, mids, bpmEvents, barEvents).ToList();
                var prev = begin; var walk = 0;
                for (int k = 0; k < beats.Count; k++)
                {
                    var beat = beats[k];
                    if (notes.Any(x => IsJudgementNote(x.Op) && SamePosition(x.Pos, beat))) continue;
                    var window = 15000.0 / (double)BpmAtMs(beat.Ms, bpmEvents);
                    var skip = mids.Any(mp => mp.Ms - window <= beat.Ms && beat.Ms < mp.Ms);
                    if (k == 0 && window > beat.Ms - startPos.Ms) skip = true;
                    if (k == beats.Count - 1 && window > endPos.Ms - beat.Ms) skip = true;
                    while (cur.Pos.Key < beat.Key)                // walk to the enclosing segment
                    {
                        var next = notes[walk]; walk++;
                        if (next.Id != cur.Id && !next.SlideAlong) (prev, cur) = (cur, next);
                        if (walk >= notes.Count) break;
                    }
                    var (ls, le) = LanePosition(beat, prev, cur);
                    // SlideComboNote..ctor: LaneStart/EndIndex = floor, Width = end - start + 1
                    var combo = new Note
                    {
                        Id = lid + k * 10000 + 10000,
                        Pos = beat,
                        Op = skip ? 121 : 120,
                        LaneStart = (int)Math.Floor((double)ls),
                        LaneEnd = (int)Math.Floor((double)le),
                        LaneStartF = ls,
                        LaneEndF = le,
                        Width = (float)((float)(le - ls) + 1f),
                        LineIds = [lid],
                        OffsetType = 1,
                        BeginRef = begin,
                        EndRef = end,
                    };
                    combo.Fever = FeverOf(beat.Ms);
                    if (SlideBeginClass.Contains(begin.Op)) begin.ComboNotes.Add(combo);   // GuideBeginNote.AddCombo is empty
                }
                beginByLine.Remove(lid);
            }
            if (!ok) return;
            foreach (var b in begins)
            {
                end.BeginNotes.Add(b);
                if (SlideEndClass.Contains(end.Op) && SlideBeginClass.Contains(b.Op)) end.EndCombo.AddRange(b.ComboNotes);
            }
        }
    }

    /// GetBeatsBetweenNotes: eighth notes of every [boundary, next boundary) segment.
    static IEnumerable<Pos> BeatsBetween(Pos start, Pos end, List<Pos> mids, List<(float, Pos)> bpmEvents, List<(float, Pos)> barEvents)
    {
        var bounds = new List<Pos> { start }; bounds.AddRange(mids); bounds.Add(end);
        for (int s = 0; s < bounds.Count - 1; s++) foreach (var p in Eighths(bounds[s], bounds[s + 1], bpmEvents, barEvents)) yield return p;
    }

    /// GetRelativeEighthNotePositions (double accumulator, restarts at the segment start).
    static IEnumerable<Pos> Eighths(Pos segStart, Pos segEnd, List<(float, Pos)> bpmEvents, List<(float, Pos)> barEvents)
    {
        var bar = segStart.Bar; double prog = segStart.Progress;
        var r = BarRhythm(bar, barEvents); var step = 1.0 / ((double)r + r);
        while (true)
        {
            prog += step;
            if (prog >= 1.0) { bar++; prog -= 1.0; r = BarRhythm(bar, barEvents); step = 1.0 / ((double)r + r); }
            var fp = (float)prog;
            var ms = TimeMsFromBar(bar, fp, bpmEvents, barEvents);
            if (segEnd.Ms <= ms) yield break;
            var r2 = BarRhythm(bar, barEvents); var unit = (int)(float)(r2 + r2);
            yield return new(bar, NetRoundInt((float)(fp * (float)unit)), unit, fp, ms);
        }
    }

    /// MusicScoreUtility.GetLanePosition (one ease, a.LineEaseType, for both edges).
    static (float, float) LanePosition(Pos pos, Note a, Note b)
    {
        var span = (float)(b.Pos.Key - a.Pos.Key);
        var e = span <= 0f ? Ease(1f, a.Ease) : Ease((float)((float)(pos.Key - a.Pos.Key) / span), a.Ease);
        e = Math.Min(Math.Max(e, 0f), 1f);
        return ((float)(a.LaneStartF + (float)(e * (float)(b.LaneStartF - a.LaneStartF))), (float)(a.LaneEndF + (float)(e * (float)(b.LaneEndF - a.LaneEndF))));
    }

    // ------------------------------------------------------------------ convert
    /// SsMusicScoreConverter.Load: the runtime score as the nnnotes `.notes.json` document (without `source`).
    public static JsonObject Convert(byte[] data, bool mirror = false, int startNoteId = 0)
    {
        using var document = JsonDocument.Parse(Decode(data), new JsonDocumentOptions { MaxDepth = 256 });
        var root = document.RootElement;
        var score = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("score", out var s) ? s : default;
        Config.Require(score.ValueKind == JsonValueKind.Object, "ss score");
        var ev = score.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Object ? ReadEvents(events) : new([], [], [], [], []);
        var notes = Items(score, "notes").Select((n, i) => ReadNote(n, [i])).ToList();
        var tc = new TickConverter(ev.Sig, ev.Bpm);
        var infos = BuildNoteInfos(notes, tc, mirror);

        var bpmEvents = ev.Bpm.OrderBy(b => b.T).Select(b => (b.Bpm, tc.Position(b.T))).ToList();
        var barEvents = ev.Sig.OrderBy(s => s.T).Select(s => ((float)((float)((float)s.Num * 4f) / (float)s.Den), tc.Position(s.T))).ToList();
        var fevers = ev.Fever.OrderBy(f => f.A).Select((f, i) => (i, tc.Position(f.A), tc.Position(f.B))).ToList();
        var skills = ev.Skill.Select((t, i) => (i, tc.Position(t))).ToList();
        var calls = ev.Call.OrderBy(c => c.T).Select(c => (tc.Position(c.T),
            c.Timing.Select((v, i) => (v, i)).Where(x => x.v == 1).Select(x => (double)(float)((float)(x.i + 1.0) / (float)c.Timing.Length)).ToList())).ToList();
        var maxBar = infos.Count > 0 ? infos.Max(n => n.Bar) : -1;
        var barLineMs = Enumerable.Range(0, maxBar + 1).Select(b => tc.TimeMs(tc.BarHeadTick(b))).ToList();

        var log = new List<string>();
        var dictionary = InfoDictionary(infos, log);
        var creator = new Creator(startNoteId, bpmEvents, barEvents, fevers);
        var byKey = new Dictionary<float, List<Note>>();
        List<Note> Bucket(float key) { if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = []; return list; }
        foreach (var key in dictionary.Keys.Order())
        {
            foreach (var info in dictionary[key].SelectMany(x => x.Infos).OrderBy(x => Priority(x.Op)).ToList())
            {
                var note = creator.Create(info);
                if (note is null) continue;
                Bucket(note.Pos.Key).Add(note);
                if (SlideEndClass.Contains(note.Op))
                    foreach (var combo in note.EndCombo) { var list = Bucket(combo.Pos.Key); if (!list.Contains(combo)) list.Add(combo); }
            }
        }
        log.AddRange(creator.Log);
        var ordered = byKey.Keys.Order().SelectMany(k => byKey[k]).ToList();
        return ToJson(ordered, tc, bpmEvents, barEvents, fevers, skills, calls, barLineMs, log, mirror, startNoteId);
    }

    static JsonObject PosJson(Pos p) => new() { ["bar"] = p.Bar, ["rhythm"] = p.Rhythm, ["rhythmicUnit"] = p.Unit, ["barProgress"] = (double)p.Progress, ["timeMs"] = p.Ms };
    static JsonObject With(JsonObject head, JsonObject tail) { foreach (var (k, v) in tail.ToList()) { tail.Remove(k); head[k] = v; } return head; }
    static JsonArray Ints(IEnumerable<int> values) => new([.. values.Select(v => (JsonNode?)v)]);

    static JsonObject ToJson(List<Note> notes, TickConverter tc, List<(float Value, Pos P)> bpmEvents, List<(float Value, Pos P)> barEvents,
        List<(int Index, Pos Start, Pos End)> fevers, List<(int, Pos)> skills, List<(Pos, List<double>)> calls, List<int> barLineMs,
        List<string> log, bool mirror, int startId)
    {
        var outNotes = new JsonArray(); var lines = new Dictionary<int, JsonObject>();
        foreach (var n in notes)
        {
            var rec = new JsonObject
            {
                ["id"] = n.Id,
                ["op"] = n.Op,
                ["opName"] = OpNames[n.Op],
                ["timeMs"] = n.Pos.Ms,
                ["bar"] = n.Pos.Bar,
                ["rhythm"] = n.Pos.Rhythm,
                ["rhythmicUnit"] = n.Pos.Unit,
                ["barProgress"] = (double)n.Pos.Progress,
                ["laneStart"] = n.LaneStart,
                ["laneEnd"] = n.LaneEnd,
                ["laneStartFloat"] = (double)n.LaneStartF,
                ["laneEndFloat"] = (double)n.LaneEndF,
                ["width"] = (double)n.Width,
                ["critical"] = n.Crit,
                ["direction"] = DirectionNames[n.Direction],
                ["slideAlong"] = n.SlideAlong,
                ["lineIds"] = Ints(n.LineIds),
                ["lineEase"] = LineEaseNames[n.Ease],
                ["lineEaseR"] = LineEaseNames[n.EaseR],
                ["pairNoteId"] = n.PairId,
                ["fever"] = n.Fever,
                ["judgement"] = IsJudgementNote(n.Op),
                ["flick"] = IsFlickType(n.Op),
                ["judgementType"] = n.Op != 123 ? JudgementTypeNames[JudgementType(n.Op, n.Crit)] : null,
                ["judgementAreaOffset"] = OffsetTypeNames[n.OffsetType],
            };
            if (n.Info is { } info)
            {
                rec["tick"] = info.Tick; rec["laneIndexFloat"] = (double)info.LaneF; rec["widthFloat"] = (double)info.WidthF;
                rec["laneIndex"] = info.Lane; rec["laneWidth"] = info.Width; rec["lineIndices"] = Ints(info.Lines);
                rec["visible"] = info.Visible; rec["alpha"] = Fades[info.Alpha]; rec["src"] = Ints(info.Src);
            }
            else { rec["lineBeginId"] = n.BeginRef!.Id; rec["lineEndId"] = n.EndRef!.Id; }
            outNotes.Add(rec);
            foreach (var lid in n.LineIds)
            {
                if (!lines.TryGetValue(lid, out var line))
                    lines[lid] = line = new() { ["lineId"] = lid, ["type"] = lid > startId + 10000 ? "guide" : "long", ["noteIds"] = new JsonArray(), ["comboIds"] = new JsonArray() };
                ((JsonArray)line[n.Info is null ? "comboIds" : "noteIds"]!).Add(n.Id);
            }
        }
        var counts = new JsonObject();
        foreach (var group in notes.GroupBy(n => n.Op).OrderBy(g => g.Key)) counts[OpNames[group.Key]] = group.Count();
        return new()
        {
            ["format"] = "nnnotes.live-score/1",
            ["mirror"] = mirror,
            ["startNoteId"] = startId,
            ["laneCount"] = LaneCount,
            ["judgementNoteCount"] = notes.Count(n => IsJudgementNote(n.Op)),
            ["counts"] = counts,
            ["notes"] = outNotes,
            ["lines"] = new JsonArray([.. lines.Values.OrderBy(l => (int)l["lineId"]!)]),
            ["bpmChanges"] = new JsonArray([.. bpmEvents.Select(b => With(new() { ["bpm"] = (double)b.Value }, PosJson(b.P)))]),
            ["barChanges"] = new JsonArray([.. barEvents.Select(b => With(new() { ["beatsPerBar"] = (double)b.Value }, PosJson(b.P)))]),
            ["tickSegments"] = new JsonObject
            {
                ["bpm"] = new JsonArray([.. tc.Bpm.Select(s => new JsonObject { ["startTick"] = s.Tick, ["startTimeMs"] = s.Ms, ["bpm"] = (double)s.Bpm })]),
                ["sig"] = new JsonArray([.. tc.Sig.Select(s => new JsonObject { ["startTick"] = s.Tick, ["startBar"] = s.Bar, ["ticksPerMeasure"] = s.Tpm, ["numerator"] = s.Num, ["denominator"] = s.Den })]),
            },
            ["barLineTimeMs"] = Ints(barLineMs),
            ["fever"] = new JsonArray([.. fevers.Select(f => new JsonObject { ["index"] = f.Index, ["start"] = PosJson(f.Start), ["end"] = PosJson(f.End) })]),
            ["skill"] = new JsonArray([.. skills.Select(s => With(new() { ["index"] = s.Item1 }, PosJson(s.Item2)))]),
            ["call"] = new JsonArray([.. calls.Select(c => With(PosJson(c.Item1), new() { ["rhythms"] = new JsonArray([.. c.Item2.Select(r => (JsonNode?)r)]) }))]),
            ["lastNoteTimeMs"] = notes.Count > 0 ? notes.Max(n => n.Pos.Ms) : 0,
            ["log"] = new JsonArray([.. log.Select(l => (JsonNode?)l)]),
        };
    }
}

/// Python's repr of a float (shortest round-trip digits; fixed notation for 1e-4 <= |x| < 1e16).
public static class PyFloat
{
    public static string Repr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        return Format(value.ToString("R", CultureInfo.InvariantCulture));
    }
    /// numpy's repr digits of a float32 scalar (shortest float32 round-trip), in Python's layout.
    public static string Repr(float value)
    {
        if (float.IsNaN(value)) return "nan";
        if (float.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return float.IsNegative(value) ? "-0.0" : "0.0";
        return Format(value.ToString("R", CultureInfo.InvariantCulture));
    }
    static string Format(string shortest)
    {
        var negative = shortest[0] == '-'; if (negative) shortest = shortest[1..];
        var exponentAt = shortest.IndexOfAny(['E', 'e']);
        var mantissa = exponentAt >= 0 ? shortest[..exponentAt] : shortest;
        var exponent = exponentAt >= 0 ? int.Parse(shortest[(exponentAt + 1)..], CultureInfo.InvariantCulture) : 0;
        var point = mantissa.IndexOf('.');
        var digits = (point >= 0 ? mantissa.Remove(point, 1) : mantissa).TrimStart('0');
        var leadingZeros = (point >= 0 ? mantissa.Remove(point, 1) : mantissa).Length - digits.Length;
        var decimalExponent = (point >= 0 ? point : mantissa.Length) - leadingZeros + exponent - 1;
        digits = digits.TrimEnd('0'); if (digits.Length == 0) digits = "0";
        var sb = new StringBuilder(negative ? "-" : "");
        if (decimalExponent < -4 || decimalExponent >= 16)
        {
            sb.Append(digits[0]); if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(decimalExponent < 0 ? '-' : '+').Append(Math.Abs(decimalExponent).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decimalExponent < 0) sb.Append("0.").Append('0', -decimalExponent - 1).Append(digits);
        else if (digits.Length <= decimalExponent + 1) sb.Append(digits).Append('0', decimalExponent + 1 - digits.Length).Append(".0");
        else sb.Append(digits, 0, decimalExponent + 1).Append('.').Append(digits, decimalExponent + 1, digits.Length - decimalExponent - 1);
        return sb.ToString();
    }
}
