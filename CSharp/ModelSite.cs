using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

/// <summary>
/// The Live2D part of the ournotes-player site (nnnotes webmodel.py), next to the charts and sharing their assets:
///
///   models.json             model index: id, manifest path, key, listing facts, sizes
///   models/&lt;id&gt;.json       model manifest: every file the Live2D viewer reads -> {asset, size}, or {size, parts}
///                           for a large JSON object (as a chart manifest)
///   assets/&lt;sha256&gt;.&lt;ext&gt;  content-addressed files shared with the charts
///
/// A model is a catalog key Character/Live2D/&lt;group&gt;/&lt;name&gt;/model/&lt;name&gt;; its id is &lt;name&gt;. Its files come
/// from a worker (Live2DModel.cs). A built manifest records `builder` and `inputs` (a hash of what it was built from),
/// so a later build rebuilds only the models whose bundles, APK data or build version changed.
/// </summary>
public static partial class ModelSite
{
    public const int Format = 2;
    public const string ModelsDir = "models";
    public const string IndexFile = "models.json";
    public const string Live2DPrefix = "Character/Live2D/";
    // Part of every model's inputs: bump it when the model export changes to rebuild them all.
    public const int BuildVersion = 1;
    static readonly string[] NameFields = ["character", "names", "label"];
    // MasterText columns by language code (nnnotes languages.LANGUAGES order)
    static readonly (string Code, string Column)[] Languages = [("ja", "_japanese"), ("en", "_english"), ("zh-Hant", "_traditionalChinese"), ("zh-Hans", "_simplifiedChinese"), ("ko", "_korean")];
    public static readonly string[] MasterTables = ["MasterCharacterCostume", "MasterCharacter", "MasterText"];
    [GeneratedRegex(@"^Character/Live2D/(?<group>[^/]+)/(?<name>[^/]+)/model/\k<name>$")] private static partial Regex ModelKey();
    [GeneratedRegex("^[a-z0-9_]+$")] private static partial Regex ModelId();
    static readonly JsonSerializerOptions Indented = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, MaxDepth = 256 };

    /// <summary>The id of a model key (its &lt;name&gt;), or null when the key is not a Live2D model key.</summary>
    public static string? Id(string key)
    {
        var m = ModelKey().Match(key);
        return m.Success ? m.Groups["name"].Value : null;
    }
    public static string Group(string key) => ModelKey().Match(key).Groups["group"].Value;

    /// <summary>{id: key} of the model keys among `keys`, in id order. Ids must be unique and URL-safe.</summary>
    public static SortedDictionary<string, string> Models(IEnumerable<string> keys)
    {
        var output = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (Id(key) is not { } id) continue;
            Require(ModelId().IsMatch(id), $"{key}: model id {id} is not [a-z0-9_]");
            Require(!output.TryGetValue(id, out var other) || other == key, $"model id {id}: keys {other} and {key}");
            output[id] = key;
        }
        return output;
    }

    /// <summary>
    /// {model key: {character, names, label}} of the models the master data maps to one character: MasterCharacterCostume
    /// maps Live2DPrefix + _live2dPath to _characterID; names are the MasterText of the character's _nameTextID in every
    /// language that has one; label the name in `language`. A key two characters share, or a character without a name,
    /// is left out (nnnotes webmodel.model_names).
    /// </summary>
    public static Dictionary<string, JsonObject> Names(Dictionary<string, JsonArray> master, string language)
    {
        var charactersOf = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var row in master["MasterCharacterCostume"].Select(r => r!.AsObject()))
            if (row["_live2dPath"] is JsonValue p && p.TryGetValue<string>(out var path) && path.Length > 0 && row["_characterID"] is JsonValue c)
                (charactersOf.TryGetValue(Live2DPrefix + path, out var set) ? set : charactersOf[Live2DPrefix + path] = []).Add(ChartSite.Integer(c));
        var characters = master["MasterCharacter"].Select(r => r!.AsObject()).GroupBy(r => ChartSite.Integer(r["_id"])).ToDictionary(g => g.Key, g => g.First());
        var texts = master["MasterText"].Select(r => r!.AsObject()).Where(r => r["_id"] is JsonValue).GroupBy(r => r["_id"]!.ToJsonString()).ToDictionary(g => g.Key, g => g.First());
        var output = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, ids) in charactersOf.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (ids.Count != 1) continue;
            var id = ids.First();
            if (!characters.TryGetValue(id, out var character) || character["_nameTextID"] is not { } textId || !texts.TryGetValue(textId.ToJsonString(), out var row)) continue;
            var names = new JsonObject();
            foreach (var (code, column) in Languages)
                if (row[column] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) names[code] = text;
            if (names.Count == 0) continue;
            var entry = new JsonObject { ["character"] = id, ["names"] = names };
            if (names[language] is { } label) entry["label"] = label.DeepClone();
            output[key] = entry;
        }
        return output;
    }

    static string ManifestPath(string root, string id) => Path.Combine(root, ModelsDir, id + ".json");
    static JsonObject? Manifest(string root, string id) => File.Exists(ManifestPath(root, id)) ? JsonNode.Parse(File.ReadAllBytes(ManifestPath(root, id)))!.AsObject() : null;

    /// <summary>Whether to build a model: one the site lacks, or one whose recorded inputs differ; with force every model.</summary>
    public static bool NeedsBuild(string root, string id, bool force, string inputs)
    {
        if (force) return true;
        var manifest = Manifest(root, id);
        return manifest == null || (string?)manifest["inputs"] != inputs;
    }

    /// <summary>
    /// A hash of what a model is built from: the build version, the APK data, the key and internal id and the bundles of
    /// its closure (their catalog file names carry their content hash; with size and CRC).
    /// </summary>
    public static string Inputs(string apkSha256, Location target, IEnumerable<Location> bundles)
    {
        var parts = new List<string> { $"build {BuildVersion}", $"apk {apkSha256}", $"key {target.Key}", $"internal {target.Internal}" };
        parts.AddRange(bundles.Select(b => $"bundle {b.Internal.Split('/')[^1]} {b.Options?.Size} {b.Options?.Crc}").Order(StringComparer.Ordinal));
        return Crypto.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
    }

    /// <summary>
    /// One built model's files (the worker's output directory) into the site's assets, and its manifest. JSON files larger
    /// than 512 KiB are stored per top-level key, cut from their text (nnnotes split_json).
    /// </summary>
    public static JsonObject Ingest(string root, string id, string key, string directory, IEnumerable<string> files, JsonObject? names, string inputs)
    {
        var summary = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, Live2DModel.SummaryFile)))!.AsObject();
        var entries = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            Require(path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Split('/').Any(p => p is "" or "." or ".."), $"Unsafe model file {path}");
            var data = File.ReadAllBytes(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)));
            var parts = path.EndsWith(".json", StringComparison.Ordinal) ? Live2DModel.Split(data) : null;
            entries[path] = parts == null ? Put(root, path, data) : new JsonObject
            {
                ["size"] = data.Length,
                ["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)new JsonArray(p.Key, (string)Put(root, $"{path}#{p.Key}.json", p.Text)["asset"]!, p.Text.Length))]),
            };
        }
        var model = new JsonObject
        {
            ["group"] = Group(key), ["canvas"] = summary["canvas"]!.DeepClone(), ["textures"] = summary["textures"]!.AsArray().Count, ["nodes"] = summary["nodes"]!.DeepClone(),
        };
        SetNames(model, names);
        var manifestFiles = new JsonObject();
        foreach (var (path, entry) in entries) manifestFiles[path] = entry;
        var manifest = new JsonObject
        {
            ["format"] = Format, ["id"] = id, ["key"] = key, ["model"] = model, ["files"] = manifestFiles,
            ["builder"] = ChartSite.Builder, ["inputs"] = inputs,
        };
        WriteAtomic(ManifestPath(root, id), Dump(manifest));
        return new JsonObject { ["id"] = id, ["files"] = entries.Count, ["bytes"] = entries.Values.Sum(e => ChartSite.Integer(e["size"])) };
    }

    static void SetNames(JsonObject model, JsonObject? names)
    {
        foreach (var field in NameFields) model.Remove(field);
        if (names == null) return;
        foreach (var field in NameFields) if (names[field] is { } v) model[field] = v.DeepClone();
    }

    /// <summary>Sets a model manifest's name fields to those of `names` (null: none); true when it changed.</summary>
    public static bool RefreshNames(string root, string id, JsonObject? names)
    {
        var manifest = Manifest(root, id);
        if (manifest?["model"] is not JsonObject model) return false;
        var before = Sorted(model)!.ToJsonString();
        SetNames(model, names);
        if (Sorted(model)!.ToJsonString() == before) return false;
        WriteAtomic(ManifestPath(root, id), Dump(manifest));
        return true;
    }

    /// <summary>models.json from every model manifest (none: no models.json); returns the count and the assets they use.</summary>
    public static (int Count, HashSet<string> Assets) WriteIndex(string root)
    {
        var directory = Path.Combine(root, ModelsDir); var used = new HashSet<string>(StringComparer.Ordinal);
        var index = Path.Combine(root, IndexFile);
        if (!Directory.Exists(directory)) { if (File.Exists(index)) File.Delete(index); return (0, used); }
        var models = new JsonArray();
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); var files = manifest["files"]!.AsObject();
            foreach (var (_, entry) in files)
                if (entry!["parts"] is JsonArray parts) used.UnionWith(parts.Select(p => (string)p![1]!));
                else used.Add((string)entry["asset"]!);
            var item = new JsonObject
            {
                ["id"] = Path.GetFileNameWithoutExtension(path), ["manifest"] = $"{ModelsDir}/{Path.GetFileName(path)}", ["key"] = manifest["key"]?.DeepClone(),
                ["files"] = files.Count, ["bytes"] = files.Sum(f => ChartSite.Integer(f.Value!["size"])),
            };
            foreach (var (k, v) in manifest["model"]?.AsObject() ?? []) item[k] = v?.DeepClone();
            models.Add(item);
        }
        WriteAtomic(index, Dump(new JsonObject { ["format"] = Format, ["models"] = models }));
        return (models.Count, used);
    }

    /// <summary>Model ids with a manifest in the site.</summary>
    public static string[] Ids(string root) => Directory.Exists(Path.Combine(root, ModelsDir))
        ? [.. Directory.GetFiles(Path.Combine(root, ModelsDir), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!]
        : [];
    public static void Remove(string root, string id) { if (File.Exists(ManifestPath(root, id))) File.Delete(ManifestPath(root, id)); }

    static JsonObject Put(string root, string path, ReadOnlySpan<byte> data)
    {
        var assets = Path.Combine(root, "assets"); Directory.CreateDirectory(assets);
        var extension = Path.GetExtension(path.Split('#')[^1]).TrimStart('.').ToLowerInvariant();
        var name = $"{Convert.ToHexStringLower(SHA256.HashData(data))}.{(extension.Length > 0 ? extension : "bin")}";
        var target = Path.Combine(assets, name);
        if (!File.Exists(target) || new FileInfo(target).Length != data.Length)
        {
            var temporary = Path.Combine(assets, $".{Guid.NewGuid():N}.part");
            File.WriteAllBytes(temporary, data.ToArray()); File.Move(temporary, target, true);
        }
        return new() { ["asset"] = $"assets/{name}", ["size"] = data.Length };
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
}
