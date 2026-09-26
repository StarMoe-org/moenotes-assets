using System.Buffers.Binary;
using System.Text.RegularExpressions;
using AssetsTools.NET;
namespace MoenotesAssets;

/// <summary>
/// A Shader object as nnnotes' shader.py dumps it (Unity 2021.2+ / 6000.x blob layout), GLES3 only: the parsed form's
/// summary (the `&lt;name&gt;.json` a player reads for properties and render state) and every GLES3 program of the
/// player sub-programs, numbered as shader.py numbers them. Per platform the compressed blob is an LZ4 block holding
/// a table of (offset, length, segment) entries; a code entry is version, program type, 4 ints, keyword strings
/// (aligned), then the program bytes (GLSL ES text with both stages under #ifdef VERTEX / #ifdef FRAGMENT).
/// </summary>
public static partial class ShaderDump
{
    public const string Platform = "gles3";
    public const string ProgramType = "GLES3";
    const long Gles3Platform = 9;
    static readonly string[] Stages = ["progVertex", "progFragment", "progGeometry", "progHull", "progDomain"];
    // UnityPy ShaderGpuProgramType: 4 = kShaderGpuProgramGLES3 (the only type of ShaderCompilerPlatform 9 kept here)
    const long Gles3Type = 4;

    public sealed record Variant(string File, long SubShader, long Pass, string Stage, List<string> Keywords, byte[] Code)
    {
        public PyObject Record() => new()
        {
            ["file"] = File, ["platform"] = Platform, ["subShader"] = SubShader, ["pass"] = Pass, ["stage"] = Stage,
            ["type"] = ProgramType, ["keywords"] = Keywords.Cast<object?>().ToList(),
        };
    }
    /// <summary>One dumped shader: its name, the file name stem, the parsed summary and the GLES3 variants.</summary>
    public sealed record Dump(string Name, string Source, PyObject Parsed, List<Variant> Variants)
    {
        public string Safe => SafeName(Name);
        public string ParsedFile => $"{Safe}.json";
    }

    public static string SafeName(string name) => Unsafe().Replace(name, "_");
    [GeneratedRegex("[^0-9A-Za-z._-]")] private static partial Regex Unsafe();

    /// <summary>The dump of a Shader object's base field (source: the serialized file's name, as shader.py records it).</summary>
    public static Dump Read(AssetTypeValueField shader, string source)
    {
        var parsed = UnityTree.Obj(UnityTree.Read(shader["m_ParsedForm"]));
        var name = parsed.Get("m_Name") as string;
        Config.Require(!string.IsNullOrEmpty(name), "Shader without a parsed-form name");
        var blobs = PlatformBlobs(shader);
        var names = parsed.Get("m_KeywordNames") is List<object?> k ? k.Select(UnityTree.Str).ToList() : [];
        var variants = new List<Variant>(); var safe = SafeName(name!);
        var subShaders = UnityTree.List(parsed["m_SubShaders"]);
        for (var si = 0; si < subShaders.Count; si++)
        {
            var passes = UnityTree.List(UnityTree.Obj(subShaders[si])["m_Passes"]);
            for (var pi = 0; pi < passes.Count; pi++)
            {
                var pass = UnityTree.Obj(passes[pi]);
                foreach (var stage in Stages)
                {
                    var program = pass.Get(stage) as PyObject;
                    var n = 0;
                    foreach (var group in program?.Get("m_PlayerSubPrograms") as List<object?> ?? [])
                        foreach (var item in UnityTree.List(group))
                        {
                            var sub = UnityTree.Obj(item);
                            if (UnityTree.Long(sub["m_GpuProgramType"]) == Gles3Type && blobs.TryGetValue(Gles3Platform, out var table))
                            {
                                var index = (int)UnityTree.Long(sub["m_BlobIndex"]);
                                Config.Require(index >= 0 && index < table.Count, $"Shader {name}: blob index {index} outside the table");
                                var keywords = (sub.Get("m_KeywordIndices") as List<object?> ?? []).Select(UnityTree.Long)
                                    .Where(i => i < names.Count).Select(i => names[(int)i]).ToList();
                                var stageName = stage[4..].ToLowerInvariant();
                                variants.Add(new($"{safe}/{Platform}/s{si}p{pi}_{stageName}_{n}.glsl", si, pi, stageName, keywords, Code(table[index])));
                            }
                            n++;
                        }
                }
            }
        }
        return new(name!, source, Summary(parsed), variants);
    }

    /// <summary>shader.py _parsed_summary.</summary>
    static PyObject Summary(PyObject parsed)
    {
        var subShaders = new List<object?>();
        foreach (var item in UnityTree.List(parsed["m_SubShaders"]))
        {
            var ss = UnityTree.Obj(item);
            var passes = new List<object?>();
            foreach (var p in UnityTree.List(ss["m_Passes"]))
            {
                var pass = UnityTree.Obj(p);
                var state = pass.Get("m_State") as PyObject;
                var stateName = state?.Get("m_Name");
                passes.Add(new PyObject { ["name"] = UnityTree.Truthy(stateName) ? stateName : pass.Get("m_Name"), ["tags"] = pass.Get("m_Tags"), ["state"] = pass.Get("m_State") });
            }
            subShaders.Add(new PyObject { ["tags"] = ss.Get("m_Tags"), ["lod"] = ss.Get("m_LOD"), ["passes"] = passes });
        }
        return new PyObject
        {
            ["name"] = parsed.Get("m_Name"),
            ["properties"] = (parsed.Get("m_PropInfo") as PyObject)?.Get("m_Props") ?? new List<object?>(),
            ["keywords"] = parsed.Get("m_KeywordNames") ?? new List<object?>(),
            ["subShaders"] = subShaders,
            ["fallback"] = parsed.Get("m_FallbackName"),
        };
    }

    /// <summary>Per platform the blob entries (shader.py platform_blobs; a segmented entry uses its first segment).</summary>
    static Dictionary<long, List<byte[]>> PlatformBlobs(AssetTypeValueField shader)
    {
        var blob = shader["compressedBlob"]["Array"].AsByteArray ?? [];
        static long Entry(AssetTypeValueField array, int i)
        {
            var item = array["Array"].Children[i];
            return item.Children.Count > 0 && item.Children[0].TemplateField.IsArray ? item["Array"].Children[0].AsLong : item.AsLong;
        }
        var platforms = shader["platforms"]["Array"].Children;
        var offsets = shader["offsets"]; var compressed = shader["compressedLengths"]; var decompressed = shader["decompressedLengths"];
        var count = Math.Min(platforms.Count, Math.Min(compressed["Array"].Children.Count, decompressed["Array"].Children.Count));
        var output = new Dictionary<long, List<byte[]>>();
        for (var i = 0; i < count; i++)
        {
            var platform = platforms[i].AsLong;
            if (platform != Gles3Platform) continue;
            var offset = Entry(offsets, i); var length = Entry(compressed, i); var size = Entry(decompressed, i);
            Config.Require(offset >= 0 && length >= 0 && offset + length <= blob.Length && size is >= 0 and <= 1 << 30, "Shader blob entry outside the blob");
            var raw = LZ4ps.LZ4Codec.Decode64(blob, (int)offset, (int)length, (int)size);
            output[platform] = Table(raw);
        }
        return output;
    }

    static List<byte[]> Table(byte[] raw)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(raw);
        Config.Require(n >= 0 && 4 + 12L * n <= raw.Length, "Shader blob table truncated");
        var output = new List<byte[]>(n);
        for (var i = 0; i < n; i++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 + 12 * i)); var length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8 + 12 * i));
            Config.Require(offset >= 0 && length >= 0 && (long)offset + length <= raw.Length, "Shader blob entry truncated");
            output.Add(raw.AsSpan(offset, length).ToArray());
        }
        return output;
    }

    /// <summary>The program bytes of a code entry.</summary>
    static byte[] Code(byte[] entry)
    {
        var at = 4 + 4 + 16;
        var keywords = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at)); at += 4;
        Config.Require(keywords is >= 0 and <= 100000, "Shader code entry keyword count");
        for (var i = 0; i < keywords; i++)
        {
            var n = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at)); at += 4 + n;
            at = (at + 3) & ~3;
        }
        var size = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(at));
        Config.Require(size >= 0 && at + 4L + size <= entry.Length, "Shader code entry truncated");
        return entry.AsSpan(at + 4, size).ToArray();
    }

    /// <summary>
    /// The files of the shaders a model reads: per (shader, keyword set) of its materials the parsed file and the GLES3
    /// program of subshader 0 pass 0 whose keywords are exactly the material's keywords among those the shader's
    /// programs there use (nnnotes webmodel.shader_files).
    /// </summary>
    public static SortedSet<string> Files(IReadOnlyDictionary<string, Dump> dumps, IEnumerable<PyObject> materials)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var sets = new HashSet<(string, string)>();
        foreach (var material in materials)
        {
            var shader = UnityTree.Str(UnityTree.Obj(material["shader"])["shader"]);
            var keywords = UnityTree.List(material["keywords"]).Select(UnityTree.Str).ToList();
            if (!sets.Add((shader, string.Join('\n', keywords)))) continue;
            Config.Require(dumps.TryGetValue(shader, out var dump), $"shader {shader}: not in the shader index");
            var variants = dump!.Variants.Where(v => v.SubShader == 0 && v.Pass == 0).ToList();
            var used = variants.SelectMany(v => v.Keywords).ToHashSet(StringComparer.Ordinal);
            var want = keywords.Where(used.Contains).ToHashSet(StringComparer.Ordinal);
            var hits = variants.Where(v => v.Keywords.ToHashSet(StringComparer.Ordinal).SetEquals(want)).ToList();
            Config.Require(hits.Count == 1, $"shader {shader}: {hits.Count} GLES3 programs with the keywords [{string.Join(", ", want.Order(StringComparer.Ordinal))}]");
            files.Add(dump.ParsedFile); files.Add(hits[0].File);
        }
        return files;
    }

    /// <summary>GLSL as nnnotes stores it: read as text (universal newlines), UTF-8.</summary>
    public static byte[] Text(byte[] code)
    {
        if (!code.AsSpan().Contains((byte)'\r')) return code;
        var text = System.Text.Encoding.UTF8.GetString(code).Replace("\r\n", "\n").Replace('\r', '\n');
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
