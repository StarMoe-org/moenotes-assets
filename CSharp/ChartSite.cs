using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

// An ournotes-player chart site (the layout of nnnotes `web`, SITE_FORMAT 2):
//   charts.json                          chart index
//   charts/<musicId>_<difficulty>.json   chart manifest: logical path -> {asset, size} | {size, parts: [[key, asset, size]]}
//   assets/<sha256>.<ext>                content-addressed files shared by every chart
// The static base (stage, note skins, effects, shaders, sound effects; everything that does not depend on the song)
// comes from an nnnotes build, as one of:
//   a static package (PackBase): base.json, templates/<id>.json (nnnotes manifests), assets/ without the songs' own
//     files (BGM, notes, jacket); every chart is composed here;
//   an nnnotes site (charts/, assets/): its charts are published as they are (ImportBase), the others composed here.
// A chart is composed from a template of the same stage band: its static files, plus the song's own score, BGM, jacket
// and sound definition. Template files are read in the base directory and linked into the site as charts use them.
public sealed partial class ChartSite(string root, string baseDir, Func<string, string, bool> link)
{
    public const int Format = 2;
    public const string Builder = "moenotes-assets";
    // Part of every built chart's inputs (AssetService.ChartInputs): bump it when composition changes to rebuild them all.
    public const int BuildVersion = 1;
    const int SplitMinBytes = 512 * 1024;
    [GeneratedRegex("^[A-Za-z0-9_$.\\-]+$")] private static partial Regex SplitKey();
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 1024 };
    static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };
    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 1024 };
    readonly Dictionary<int, Template> templates = [];

    public const int PackageFormat = 1;
    public string Root { get; } = Path.GetFullPath(root);
    public string BaseDir { get; } = Path.GetFullPath(baseDir);
    string AssetsDir => Path.Combine(Root, "assets");
    string ChartsDir => Path.Combine(Root, "charts");
    /// <summary>A static package (templates only) rather than a whole nnnotes site.</summary>
    public bool IsPackage => File.Exists(Path.Combine(BaseDir, "base.json"));
    string TemplatesDir => Path.Combine(BaseDir, IsPackage ? "templates" : "charts");

    /// <summary>An m4a waveform: <paramref name="Samples"/> as decoded (edit list applied), which may include AAC end padding.</summary>
    public sealed record BgmLayer(byte[] Data, long Samples, int SampleRate, int Channels, long EncoderDelay);
    // How a cue sheet is stored, in nnnotes' words (live.json audio.source, live-audio.json decoded.<sheet>.layout).
    public const string SplitAcbLayout = "SplitAcbData (chunks joined, XOR-masked)";
    public const string EmbeddedAcbLayout = "CriSerializedBytesAssetImpl (ACB bytes inside the bundle)";
    /// <summary>
    /// One chart to compose. <paramref name="Facts"/> lacks `notes` and `durationMs`, which follow from the score and BGM.
    /// <paramref name="Inputs"/>, a hash of what the chart is built from, is recorded in its manifest (see NeedsBuild).
    /// </summary>
    public sealed record ChartInput(int MusicId, string Difficulty, int Band, byte[] Score, JsonObject ScoreSource, JsonObject Facts,
        JsonObject LiveMusic, string Sheet, string Cue, JsonObject SoundRow, AcbCues.Cue CueInfo, BgmLayer[] Layers,
        string Jacket, byte[] JacketPng, int JacketWidth, int JacketHeight, string JacketSprite, string SheetLayout, string? Inputs = null);
    sealed record Template(string Id, JsonObject Manifest, Dictionary<string, JsonObject> Static, Dictionary<string, JsonNode> Shaders);

    public static string ChartId(int musicId, string difficulty) => $"{musicId}_{difficulty}";
    public bool Has(string id) => File.Exists(ManifestPath(id));
    /// <summary>
    /// Whether to compose a chart: one the site lacks, or a built one whose recorded inputs differ from
    /// <paramref name="inputs"/> (built before inputs were recorded, or from another score, BGM, jacket or master rows);
    /// with <paramref name="force"/> every chart not taken from an nnnotes site. With a static package, charts an earlier
    /// nnnotes site published are composed again.
    /// </summary>
    public bool NeedsBuild(string id, bool force, string? inputs = null) => IsPackage ? force || !Has(id) || IsBase(id) || Stale(id, inputs)
        : force ? !IsBase(id) : !Has(id) || (!IsBase(id) && Stale(id, inputs));
    bool Stale(string id, string? inputs) => inputs != null && (string?)Parse(File.ReadAllBytes(ManifestPath(id)))["inputs"] != inputs;
    string ManifestPath(string id) => Path.Combine(ChartsDir, id + ".json");

    // ------------------------------------------------------------------ store
    public JsonObject Put(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(AssetsDir);
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var name = $"{Convert.ToHexStringLower(SHA256.HashData(data))}.{(extension.Length > 0 ? extension : "bin")}";
        var target = Path.Combine(AssetsDir, name);
        if (!File.Exists(target) || new FileInfo(target).Length != data.Length)
        {
            var temporary = Path.Combine(AssetsDir, $".{Guid.NewGuid():N}.part");
            File.WriteAllBytes(temporary, data.ToArray()); File.Move(temporary, target, true);
        }
        return new() { ["asset"] = $"assets/{name}", ["size"] = data.Length };
    }

    /// <summary>A JSON file, stored per top-level key when it is a large object (the player rejoins the parts).</summary>
    public JsonObject PutJson(string path, JsonNode node)
    {
        var data = Minify(node);
        if (data.Length <= SplitMinBytes || node is not JsonObject obj || obj.Count < 2 || !obj.All(p => SplitKey().IsMatch(p.Key))) return Put(path, data);
        var parts = obj.Select(p => (p.Key, Text: Minify(p.Value))).ToArray();
        Require(Join(parts).AsSpan().SequenceEqual(data), "Split JSON does not rejoin");
        return new()
        {
            ["size"] = data.Length,
            ["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)new JsonArray(p.Key, (string)Put($"{path}#{p.Key}.json", p.Text)["asset"]!, p.Text.Length))]),
        };
    }

    public static byte[] Minify(JsonNode? node) => JsonSerializer.SerializeToUtf8Bytes(node, Compact);
    static byte[] Join(IEnumerable<(string Key, byte[] Text)> parts)
    {
        using var output = new MemoryStream(); output.WriteByte((byte)'{'); var first = true;
        foreach (var (key, text) in parts)
        {
            if (!first) output.WriteByte((byte)',');
            first = false; output.Write(JsonSerializer.SerializeToUtf8Bytes(key)); output.WriteByte((byte)':'); output.Write(text);
        }
        output.WriteByte((byte)'}'); return output.ToArray();
    }
    static string AssetPath(string directory, string asset)
    {
        Require(asset.StartsWith("assets/", StringComparison.Ordinal) && SafeName(asset[7..]), "Invalid asset path");
        return Path.Combine(directory, asset);
    }
    byte[] ReadAsset(string asset) => File.ReadAllBytes(AssetPath(BaseDir, asset));
    /// <summary>The text of a template's file (a split JSON file rejoined), read in the base directory.</summary>
    byte[] Read(JsonObject entry) => entry["parts"] is JsonArray parts
        ? Join(parts.Select(p => ((string)p![0]!, ReadAsset((string)p[1]!))))
        : ReadAsset((string)entry["asset"]!);
    /// <summary>Makes a base asset part of the site (a hard link when possible).</summary>
    void LinkAsset(string asset)
    {
        var source = AssetPath(BaseDir, asset); var target = AssetPath(Root, asset);
        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) return;
        Require(File.Exists(source), $"Static base lacks {asset}");
        Directory.CreateDirectory(AssetsDir);
        var temporary = Path.Combine(AssetsDir, $".{Guid.NewGuid():N}.part");
        try { if (!link(temporary, source)) File.Copy(source, temporary); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static JsonNode Parse(byte[] data) => JsonNode.Parse(data, NodeOptions, DocumentOptions) ?? throw new InvalidDataException("Null JSON document");
    static bool SafeName(string name) => name.Length is > 0 and <= 255 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    /// <summary>An integer of a parsed or constructed node (JsonValue of long, int or an integral JSON number).</summary>
    public static long Integer(JsonNode? node) => node is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<int>(out var i) && (l = i) == i))
        ? l : throw new InvalidDataException($"Expected an integer, got {node?.ToJsonString() ?? "null"}");
    static IEnumerable<string> EntryAssets(JsonObject entry) => entry["parts"] is JsonArray parts ? parts.Select(p => (string)p![1]!) : [(string)entry["asset"]!];

    // ------------------------------------------------------------------ base
    /// <summary>
    /// An nnnotes site's charts published as they are (their assets linked); base charts replace built ones. A static
    /// package publishes nothing: its templates only serve composition.
    /// </summary>
    public string[] ImportBase()
    {
        Require(Directory.Exists(TemplatesDir) && Directory.Exists(Path.Combine(BaseDir, "assets")), "Chart base is neither a static package (base.json, templates/, assets/) nor an nnnotes site (charts/, assets/)");
        templates.Clear();
        if (IsPackage)
        {
            var info = Parse(File.ReadAllBytes(Path.Combine(BaseDir, "base.json")));
            Require((int?)info["format"] == PackageFormat && (int?)info["siteFormat"] == Format, "Unsupported static package");
            return [];
        }
        Directory.CreateDirectory(ChartsDir);
        var imported = new List<string>();
        foreach (var (id, manifest, bytes) in BaseManifests())
        {
            foreach (var asset in manifest["files"]!.AsObject().SelectMany(f => EntryAssets(f.Value!.AsObject()))) LinkAsset(asset);
            WriteAtomic(ManifestPath(id), bytes); imported.Add(id);
        }
        return [.. imported];
    }

    IEnumerable<(string Id, JsonObject Manifest, byte[] Bytes)> BaseManifests()
    {
        foreach (var path in Directory.GetFiles(TemplatesDir, "*.json").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path); var manifest = Parse(bytes).AsObject(); var id = Path.GetFileNameWithoutExtension(path);
            Require((int?)manifest["format"] == Format && manifest["files"] is JsonObject && id == ChartId((int)manifest["musicId"]!, (string)manifest["difficulty"]!), $"Unsupported base manifest {id}");
            yield return (id, manifest, bytes);
        }
    }
    public bool IsBase(string id) => Has(id) && Parse(File.ReadAllBytes(ManifestPath(id)))["builder"] is null;

    /// <summary>The paths of a chart that belong to its song: they are never taken from another chart.</summary>
    HashSet<string> SongPaths(JsonObject files)
    {
        var paths = files.Select(f => f.Key).Where(p => p == "live.json" || p == "audio/live-audio.json" || p == "livescene/scene.json"
            || p.EndsWith("/shaders.json", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        paths.UnionWith(SongData(files, Read));
        return paths;
    }
    /// <summary>A chart's song data: notes, BGM and jacket, which a static package leaves out.</summary>
    static HashSet<string> SongData(JsonObject files, Func<JsonObject, byte[]> read)
    {
        var paths = files.Select(f => f.Key).Where(p => p.StartsWith("score/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (files["audio/live-audio.json"] is JsonObject audio)
        {
            var la = Parse(read(audio));
            foreach (var layer in la["sounds"]![la["music"]!["soundId"]!.ToString()]!["layers"]!.AsArray()) paths.Add((string)layer!["file"]!);
        }
        if (files["livescene/scene.json"] is JsonObject scene && JacketTexture(scene, read) is { } jacket) paths.Add("livescene/" + jacket);
        return paths;
    }
    static string? JacketTexture(JsonObject sceneEntry, Func<JsonObject, byte[]> read)
    {
        JsonNode? sprites;
        if (sceneEntry["parts"] is JsonArray parts)
        {
            var part = parts.FirstOrDefault(p => (string)p![0]! == "sprites");
            sprites = part is null ? null : Parse(read(new JsonObject { ["asset"] = (string)part[1]! }));
        }
        else sprites = Parse(read(sceneEntry))["sprites"];
        return (string?)sprites?["jacket"]?["texture"]?["texture"];
    }

    /// <summary>The base chart a song of <paramref name="band"/> starts from, with the static files of every base chart of that band.</summary>
    Template TemplateFor(int band)
    {
        if (templates.TryGetValue(band, out var cached)) return cached;
        var bases = BaseManifests().Select(m => (m.Id, m.Manifest)).ToList();
        Require(bases.Count > 0, "The static base has no charts");
        var sameBand = bases.Where(m => (int?)m.Manifest["chart"]?["stageBand"] == band).ToList();
        var candidates = sameBand.Count > 0 ? sameBand : bases;
        var ordered = candidates.OrderByDescending(m => m.Manifest["files"]!.AsObject().Count).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var files = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var shaders = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        foreach (var (_, manifest) in ordered)
        {
            var entries = manifest["files"]!.AsObject(); var song = SongPaths(entries);
            foreach (var (path, entry) in entries)
            {
                if (path.EndsWith("/shaders.json", StringComparison.Ordinal)) { (shaders.TryGetValue(path, out var list) ? list : shaders[path] = []).Add(Parse(Read(entry!.AsObject()))); continue; }
                if (!song.Contains(path)) files.TryAdd(path, entry!.AsObject());
            }
        }
        var merged = shaders.ToDictionary(s => s.Key, s => MergeShaders(s.Value), StringComparer.Ordinal);
        return templates[band] = new(ordered[0].Id, ordered[0].Manifest, files, merged);
    }

    /// <summary>shaders.json indexes of several charts (each filtered to what its chart reads) as one: shaders by name, variants by file.</summary>
    static JsonNode MergeShaders(List<JsonNode> indexes)
    {
        var output = new JsonArray(); var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var index in indexes)
            foreach (var record in index.AsArray().Select(r => r!.AsObject()))
            {
                var name = (string)record["name"]!;
                if (!byName.TryGetValue(name, out var existing)) { var copy = record.DeepClone().AsObject(); byName[name] = copy; output.Add(copy); continue; }
                var variants = existing["variants"]!.AsArray(); var known = variants.Select(v => (string)v!["file"]!).ToHashSet(StringComparer.Ordinal);
                foreach (var variant in record["variants"]!.AsArray()) if (known.Add((string)variant!["file"]!)) variants.Add(variant.DeepClone());
            }
        return output;
    }

    // ------------------------------------------------------------------ compose
    public JsonObject Build(ChartInput input)
    {
        var template = TemplateFor(input.Band);
        var baseFiles = template.Manifest["files"]!.AsObject();
        var files = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (path, value) in template.Static)
        {
            foreach (var asset in EntryAssets(value)) LinkAsset(asset);
            files[path] = value.DeepClone();
        }
        foreach (var (path, index) in template.Shaders) files[path] = PutJson(path, index);

        // score: the converted runtime notes, as nnnotes score.extract writes them
        var notes = ChartScore.Convert(input.Score);
        notes["source"] = input.ScoreSource.DeepClone();
        var scoreFile = ((string?)input.ScoreSource["key"] ?? "").Split('/')[^1];
        Require(SafeName(scoreFile), "Invalid chart file name");
        var notesPath = $"score/{scoreFile}.notes.json";
        files[notesPath] = PutJson(notesPath, notes);

        // sounds: the template's sound definitions with the song's BGM in place of the template's
        var la = Parse(Read(baseFiles["audio/live-audio.json"]!.AsObject())).AsObject();
        var sounds = la["sounds"]!.AsObject();
        var oldId = la["music"]!["soundId"]!.ToString();
        var oldSheet = (string?)sounds[oldId]?["sheet"];
        sounds.Remove(oldId);
        var soundId = Integer(input.SoundRow["_id"]);
        var entry = SoundEntry(input);
        sounds[soundId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry;
        la["musicId"] = input.MusicId; la["music"]!["soundId"] = soundId;
        if (la["decoded"] is JsonObject decoded)
        {
            if (oldSheet != null) decoded.Remove(oldSheet);
            decoded[input.Sheet] = new JsonObject { ["layout"] = input.SheetLayout, ["cues"] = 1, ["streams"] = input.Layers.Length };
        }
        files["audio/live-audio.json"] = PutJson("audio/live-audio.json", la);
        var layerFiles = entry["layers"]!.AsArray().Select(l => (string)l!["file"]!).ToArray();
        for (var i = 0; i < input.Layers.Length; i++) files[layerFiles[i]] = Put(layerFiles[i], input.Layers[i].Data);

        // scene: the template's scene with the song's jacket (only the changed parts are stored again)
        var jacketTexture = $"textures/{input.Jacket}-{Convert.ToHexStringLower(SHA256.HashData(input.JacketPng))[..8]}.png";
        Require(SafeName(jacketTexture[9..]), "Invalid jacket name");
        files["livescene/" + jacketTexture] = Put(jacketTexture, input.JacketPng);
        files["livescene/scene.json"] = SceneEntry(baseFiles["livescene/scene.json"]!.AsObject(), input, jacketTexture);

        // index
        var live = Parse(Read(baseFiles["live.json"]!.AsObject())).AsObject();
        live["musicId"] = input.MusicId; live["difficulty"] = input.Difficulty;
        live["chart"] = $"score/{scoreFile}.json"; live["notes"] = notesPath;
        live["audio"] = new JsonObject
        {
            ["cueSheet"] = input.Sheet,
            ["cue"] = input.Cue,
            ["file"] = layerFiles[0],
            ["source"] = input.SheetLayout,
            ["cues"] = new JsonObject { [input.Cue] = layerFiles[0] },
        };
        files["live.json"] = PutJson("live.json", live);

        var facts = input.Facts.DeepClone().AsObject();
        facts["notes"] = notes["judgementNoteCount"]!.DeepClone();
        facts["durationMs"] = Math.Min(input.CueInfo.Layers[0].Samples, input.Layers[0].Samples) * 1000 / input.Layers[0].SampleRate;
        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["musicId"] = input.MusicId,
            ["difficulty"] = input.Difficulty,
            ["audio"] = true,
            ["audioFormat"] = "aac",
            ["flows"] = new JsonArray("direct"),
            ["quality"] = template.Manifest["quality"]?.DeepClone() ?? 1,
            ["chart"] = facts,
            ["builder"] = Builder,
            ["template"] = template.Id,
            ["files"] = new JsonObject([.. files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))]),
        };
        if (input.Inputs != null) manifest["inputs"] = input.Inputs;
        WriteAtomic(ManifestPath(ChartId(input.MusicId, input.Difficulty)), Dump(manifest));
        return manifest;
    }

    static JsonObject SoundEntry(ChartInput input)
    {
        var cue = input.CueInfo;
        Require(cue.Layers.Length == input.Layers.Length && cue.Layers.Length > 0, $"{input.Sheet}/{input.Cue}: {cue.Layers.Length} ACB layers, {input.Layers.Length} audio files");
        var layers = new JsonArray();
        for (var i = 0; i < cue.Layers.Length; i++)
        {
            var (l, audio) = (cue.Layers[i], input.Layers[i]);
            // The waveform's length is the ACB's. An AAC decode may run up to one frame past it (end padding); the HCA
            // decode can also end up to a frame short of it (a final partial frame of a few samples). The player keeps
            // `samples` frames after the encoder delay only when the decode holds samples + delay, so `samples` must not
            // exceed the file's: the shorter of the two.
            Require(audio.Samples >= l.Samples - 1024 && audio.Samples <= l.Samples + 2048, $"{input.Sheet}/{input.Cue}: {audio.Samples} decoded samples, ACB {l.Samples}");
            var stem = cue.Layers.Length == 1 ? input.Cue : $"{input.Cue}_{i + 1}";
            Require(SafeName(stem) && SafeName(input.Sheet), "Invalid cue file name");
            layers.Add(new JsonObject
            {
                ["file"] = $"audio/{input.Sheet}/{stem}.m4a",
                ["stream"] = i + 1,
                ["awbId"] = l.AwbId,
                ["sampleRate"] = audio.SampleRate,
                ["channels"] = audio.Channels,
                ["samples"] = Math.Min(l.Samples, audio.Samples),
                ["loopStart"] = null,
                ["loopEnd"] = null,
                ["loopFlag"] = l.LoopFlag,
                ["volume"] = l.Volume,
                ["busSends"] = Map(l.BusSends),
                ["other"] = new JsonObject { ["track"] = Map(l.TrackOther), ["synth"] = Map(l.SynthOther) },
                ["encoderDelay"] = audio.EncoderDelay,
            });
        }
        return new()
        {
            ["row"] = input.SoundRow.DeepClone(),
            ["sheet"] = input.Sheet,
            ["cue"] = input.Cue,
            ["category"] = input.SoundRow["_category"]?.DeepClone(),
            ["categories"] = new JsonArray([.. cue.Categories.Select(c => (JsonNode?)c)]),
            ["volume"] = cue.Volume * cue.AcbVolume,
            ["busSends"] = Map(cue.BusSends),
            ["lengthMs"] = cue.LengthMs,
            ["other"] = Map(cue.SequenceOther),
            ["layers"] = layers,
        };
    }
    static JsonObject Map<T>(Dictionary<string, T> values) => new([.. values.Select(v => KeyValuePair.Create(v.Key, JsonValue.Create(v.Value) as JsonNode))]);

    /// <summary>The template's scene.json with the song's jacket, music row and slice; other parts keep their stored assets.</summary>
    JsonObject SceneEntry(JsonObject entry, ChartInput input, string jacketTexture)
    {
        JsonNode Patch(string key, JsonNode value)
        {
            switch (key)
            {
                case "sprites":
                    var jacket = value["jacket"]!.AsObject(); var texture = jacket["texture"]!.AsObject();
                    jacket["key"] = $"Image/Jacket/{input.Jacket}"; jacket["sprite"] = input.JacketSprite;
                    texture["texture"] = jacketTexture; texture["name"] = input.Jacket; texture["width"] = input.JacketWidth; texture["height"] = input.JacketHeight;
                    texture["mipCount"] = 1; break;
                case "slice":
                    value["musicId"] = input.MusicId; value["band"] = input.Band;
                    if (value["bandChoice"] is JsonObject choice) choice["band"] = input.Band;
                    break;
                case "master":
                    value["liveMusic"] = input.LiveMusic.DeepClone(); break;
            }
            return value;
        }
        string[] changed = ["sprites", "slice", "master"];
        if (entry["parts"] is not JsonArray parts)
        {
            var whole = Parse(Read(entry)).AsObject();
            foreach (var key in changed) if (whole[key] is { } value) Patch(key, value);
            return PutJson("livescene/scene.json", whole);
        }
        var result = new JsonArray(); long size = 1;
        foreach (var part in parts)
        {
            var key = (string)part![0]!; var asset = (string)part[1]!; var length = (long)part[2]!;
            if (changed.Contains(key))
            {
                var text = Minify(Patch(key, Parse(ReadAsset(asset))));
                asset = (string)Put($"livescene/scene.json#{key}.json", text)["asset"]!; length = text.Length;
            }
            else LinkAsset(asset);
            result.Add(new JsonArray(key, asset, length));
            size += JsonSerializer.SerializeToUtf8Bytes(key).Length + 1 + length + 1;
        }
        return new() { ["size"] = size, ["parts"] = result };
    }

    // ------------------------------------------------------------------ index
    /// <summary>charts.json and models.json from every manifest; assets no chart or model manifest references are removed.</summary>
    public JsonObject WriteIndex() => WriteIndex(Root);
    /// <summary>The site indexes of `root` (charts and Live2D models share its assets); unreferenced assets are removed.</summary>
    public static JsonObject WriteIndex(string root)
    {
        root = Path.GetFullPath(root);
        var assetsDir = Path.Combine(root, "assets"); var chartsDir = Path.Combine(root, "charts");
        Directory.CreateDirectory(assetsDir);
        var charts = new List<JsonObject>(); var used = new HashSet<string>(StringComparer.Ordinal);
        var manifests = Directory.Exists(chartsDir)
            ? Directory.GetFiles(chartsDir, "*.json").Order(StringComparer.Ordinal).Select(p => (Path.GetFileNameWithoutExtension(p), Parse(File.ReadAllBytes(p)).AsObject()))
            : [];
        foreach (var (id, manifest) in manifests)
        {
            var files = manifest["files"]!.AsObject();
            foreach (var (_, entry) in files) used.UnionWith(EntryAssets(entry!.AsObject()));
            var chart = new JsonObject
            {
                ["id"] = id,
                ["manifest"] = $"charts/{id}.json",
                ["musicId"] = manifest["musicId"]!.DeepClone(),
                ["difficulty"] = manifest["difficulty"]!.DeepClone(),
                ["audio"] = manifest["audio"]?.DeepClone(),
                ["audioFormat"] = manifest["audioFormat"]?.DeepClone(),
                ["flows"] = manifest["flows"]?.DeepClone(),
                ["bytes"] = files.Sum(f => (long)f.Value!["size"]!),
            };
            foreach (var (key, value) in manifest["chart"]?.AsObject() ?? new JsonObject()) chart[key] = value?.DeepClone();
            charts.Add(chart);
        }
        var order = ChartScore.Difficulties.ToList();
        charts = [.. charts.OrderBy(c => (int)c["musicId"]!).ThenBy(c => order.IndexOf((string)c["difficulty"]!))];
        var index = new JsonObject { ["format"] = Format, ["charts"] = new JsonArray([.. charts]) };
        WriteAtomic(Path.Combine(root, "charts.json"), Dump(index));
        var (models, modelAssets) = ModelSite.WriteIndex(root);
        used.UnionWith(modelAssets);
        var removed = 0;
        foreach (var file in Directory.GetFiles(assetsDir))
            if (!used.Contains("assets/" + Path.GetFileName(file))) { File.Delete(file); removed++; }
        return new() { ["charts"] = charts.Count, ["models"] = models, ["assets"] = used.Count, ["removedAssets"] = removed };
    }

    static byte[] Dump(JsonNode node) => [.. JsonSerializer.SerializeToUtf8Bytes(Sorted(node), Indented), (byte)'\n'];
    static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
    static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.part");
        File.WriteAllBytes(temporary, data); File.Move(temporary, path, true);
    }

    // ------------------------------------------------------------------ static package
    static readonly DateTimeOffset PackageTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>
    /// An nnnotes site as a static package (deterministic zip): base.json, the site's manifests as templates/, and every
    /// asset they reference except the songs' own data (notes, BGM, jacket), which each deployment exports itself.
    /// </summary>
    public static JsonObject PackBase(string siteDir, string zipPath, string source)
    {
        siteDir = Path.GetFullPath(siteDir);
        byte[] ReadSite(JsonObject entry) => entry["parts"] is JsonArray parts
            ? Join(parts.Select(p => ((string)p![0]!, File.ReadAllBytes(AssetPath(siteDir, (string)p[1]!)))))
            : File.ReadAllBytes(AssetPath(siteDir, (string)entry["asset"]!));
        var manifests = Directory.GetFiles(Path.Combine(siteDir, "charts"), "*.json").Order(StringComparer.Ordinal).ToArray();
        Require(manifests.Length > 0, "No charts in the nnnotes site");
        var kept = new SortedSet<string>(StringComparer.Ordinal); var bands = new SortedSet<int>();
        foreach (var path in manifests)
        {
            var manifest = Parse(File.ReadAllBytes(path)).AsObject(); var files = manifest["files"]!.AsObject();
            Require((int?)manifest["format"] == Format, $"Unsupported manifest {Path.GetFileName(path)}");
            if (manifest["chart"]?["stageBand"] is JsonValue band) bands.Add((int)Integer(band));
            var data = SongData(files, ReadSite);
            foreach (var (p, entry) in files) if (!data.Contains(p)) kept.UnionWith(EntryAssets(entry!.AsObject()));
        }
        var bytes = kept.Sum(a => new FileInfo(AssetPath(siteDir, a)).Length);
        var info = new JsonObject
        {
            ["format"] = PackageFormat,
            ["siteFormat"] = Format,
            ["source"] = source,
            ["templates"] = manifests.Length,
            ["stageBands"] = new JsonArray([.. bands.Select(b => (JsonNode?)b)]),
            ["assets"] = kept.Count,
            ["assetBytes"] = bytes,
        };
        var readme = $"""
            ournotes-player static base (package format {PackageFormat}, site format {Format})

              base.json              package facts
              templates/<id>.json    chart manifests of the nnnotes build, used as templates (their notes, BGM and
                                     jacket are not included)
              assets/<sha256>.<ext>  every other file they reference: stages, note skins, effects, shaders, sound effects

            moenotes-assets composes every chart from a template of the song's stage band and the song's own exports
            (chart_base_url + chart_base_sha256; docs/CHART_SITE.md).

            Source: {source}
            Game resources belong to their respective rights holders.

            """;
        var temporary = zipPath + ".part";
        using (var zip = new ZipArchive(File.Create(temporary), ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data, bool compress)
            {
                var entry = zip.CreateEntry(name, compress ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression);
                entry.LastWriteTime = PackageTime;
                using var stream = entry.Open(); stream.Write(data);
            }
            Add("base.json", Dump(info), true);
            Add("README.txt", Encoding.UTF8.GetBytes(readme.Replace("\r\n", "\n")), true);
            foreach (var path in manifests) Add("templates/" + Path.GetFileName(path), File.ReadAllBytes(path), true);
            foreach (var asset in kept) Add(asset, File.ReadAllBytes(AssetPath(siteDir, asset)), asset.EndsWith(".json", StringComparison.Ordinal) || asset.EndsWith(".glsl", StringComparison.Ordinal));
        }
        File.Move(temporary, zipPath, true);
        return info;
    }

    /// <summary>Extracts a static package into <paramref name="directory"/>; entry names and asset hashes are checked.</summary>
    public static void ExtractBase(string zipPath, string directory, long limit)
    {
        using var zip = ZipFile.OpenRead(zipPath); long total = 0;
        Directory.CreateDirectory(directory);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var parts = entry.FullName.Split('/');
            Require(parts.Length == 1 ? parts[0] is "base.json" or "README.txt" : parts.Length == 2 && parts[0] is "templates" or "assets" && SafeName(parts[1]),
                $"Unexpected entry in the static package: {entry.FullName}");
            total += entry.Length; Require(total <= limit, "Static package expansion budget");
            var target = parts.Length == 1 ? Path.Combine(directory, parts[0]) : Path.Combine(directory, parts[0], parts[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
            Require(new FileInfo(target).Length == entry.Length, $"{entry.FullName}: truncated");
            if (parts[0] == "assets")
            {
                using var stream = File.OpenRead(target);
                Require(parts[1].StartsWith(Convert.ToHexStringLower(SHA256.HashData(stream)) + ".", StringComparison.Ordinal), $"{entry.FullName}: content does not match its name");
            }
        }
        var info = Parse(File.ReadAllBytes(Path.Combine(directory, "base.json")));
        Require((int?)info["format"] == PackageFormat && (int?)info["siteFormat"] == Format, "Unsupported static package");
    }

    // ------------------------------------------------------------------ audio
    /// <summary>Encoder delay of an MP4 audio track: media_time of the first edit list entry (moov/trak/edts/elst).</summary>
    public static long Mp4Priming(ReadOnlySpan<byte> data)
    {
        static (int Start, int End)? Find(ReadOnlySpan<byte> data, int offset, int end, string[] path)
        {
            while (offset + 8 <= end)
            {
                long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[offset..]); var header = 8;
                var kind = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
                if (size == 1) { size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]); header = 16; }
                else if (size == 0) size = end - offset;
                if (size < header || offset + size > end) return null;
                if (kind == path[0]) return path.Length == 1 ? (offset + header, (int)(offset + size)) : Find(data, offset + header, (int)(offset + size), path[1..]);
                offset += (int)size;
            }
            return null;
        }
        var box = Find(data, 0, data.Length, ["moov", "trak", "edts", "elst"]);
        if (box is not { } b || b.End - b.Start < 8) return 0;
        var version = data[b.Start]; var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[(b.Start + 4)..]);
        if (count < 1) return 0;
        return version == 1 ? Math.Max(0, System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data[(b.Start + 16)..]))
            : Math.Max(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[(b.Start + 12)..]));
    }
}
