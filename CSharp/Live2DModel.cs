using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using static MoenotesAssets.Config;
using static MoenotesAssets.UnityTree;
namespace MoenotesAssets;

/// <summary>
/// One Live2D model of the catalog (Character/Live2D/&lt;group&gt;/&lt;name&gt;/model/&lt;name&gt;) as the files the
/// ournotes-player Live2D viewer reads, the way nnnotes `web --live2d` builds them (live2d.export_runtime +
/// webmodel.export_model), from the model's bundle closure and the APK data (ApkData.cs):
///
///   model.json                     index: format, name, key, moc3, prefab, drawable textures, canvas, shaders,
///                                  resources (the Cubism mask materials)
///   &lt;name&gt;.moc3                    CubismMoc._bytes
///   &lt;name&gt;.prefab.json             the prefab as a node list: every GameObject with every component, the fade
///                                  motion list, the expression list and every AnimationClip inlined; plus canvas
///   textures/*.png                 the atlas pages the drawables draw with
///   shaders/shaders.json, ...      the Live2D shaders' parsed forms and the GLES3 programs the materials select
///
/// JSON is written as nnnotes writes it (PyJson), so equal inputs give the same files; PNGs are this service's
/// encodings of the same pixels. Runs in a worker process (mode "live2d").
/// </summary>
public static class Live2DModel
{
    public const int Format = 1;                          // model.json "format"
    public const string Index = "model.json";
    public const string MaskKeyword = "CUBISM_MASK_ON";  // a drawable material's keyword: the drawable is masked
    public const string SummaryFile = "summary.json";   // written next to the files; not part of the model
    static readonly string[] StubAssets = ["CubismMoc"];  // the moc is written as <name>.moc3; the prefab names it only

    public static Artifact[] Build(WorkerJob job)
    {
        Require(job.ApkData != null, "Live2D build without APK data");
        var apk = ApkData.Load(job.ApkData!);
        var manager = Worker.Manager(job.Config);
        try
        {
            var files = Worker.LoadBundles(manager, job);
            var ex = new Live2DExporter(manager, files, apk, job.Config);
            return ex.Model(job.Target.Key, job.Target.Internal, job.Output);
        }
        finally { manager.UnloadAll(true); }
    }

    /// <summary>CanvasInfo of a moc3 (section table at 0x40; entry 1 is the canvas info).</summary>
    public static PyObject Canvas(byte[] moc)
    {
        Require(moc.Length > 0x48 && moc.AsSpan(0, 4).SequenceEqual("MOC3"u8), "not a moc3");
        var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(moc.AsSpan(0x44));
        Require(offset >= 0 && offset + 20 <= moc.Length, "moc3 canvas info outside the file");
        double F(int i) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(moc.AsSpan(offset + 4 * i));
        return new() { ["pixelsPerUnit"] = F(0), ["originX"] = F(1), ["originY"] = F(2), ["width"] = F(3), ["height"] = F(4), ["mocVersion"] = (long)moc[4] };
    }

    /// <summary>A material as nnnotes exports it (Exporter.material); `reference` resolves its PPtrs.</summary>
    public static PyObject Material(PyObject tt, Func<object?, object?> reference)
    {
        var sp = Obj(tt["m_SavedProperties"]);
        PyObject Pairs(object? list)
        {
            var output = new PyObject();
            foreach (var item in list as List<object?> ?? []) { var pair = List(item); output[Str(pair[0])] = pair[1]; }
            return output;
        }
        var textures = new PyObject();
        foreach (var item in List(sp["m_TexEnvs"]))
        {
            var pair = List(item); var env = Obj(pair[1]);
            textures[Str(pair[0])] = new PyObject { ["texture"] = reference(env["m_Texture"]), ["scale"] = env["m_Scale"], ["offset"] = env["m_Offset"] };
        }
        return new PyObject
        {
            ["material"] = tt["m_Name"],
            ["shader"] = reference(tt["m_Shader"]),
            ["keywords"] = tt.Get("m_ValidKeywords") ?? new List<object?>(),
            ["invalidKeywords"] = tt.Get("m_InvalidKeywords") ?? new List<object?>(),
            ["renderQueue"] = tt.Get("m_CustomRenderQueue"),
            ["tags"] = Pairs(tt.Get("stringTagMap")),
            ["disabledPasses"] = tt.Get("disabledShaderPasses") ?? new List<object?>(),
            ["textures"] = textures,
            ["ints"] = Pairs(sp.Get("m_Ints")),
            ["floats"] = Pairs(sp.Get("m_Floats")),
            ["colors"] = Pairs(sp.Get("m_Colors")),
        };
    }

    /// <summary>The names of the shaders a document references (its {"shader": name} objects).</summary>
    public static void ShaderNames(object? doc, ISet<string> output)
    {
        var stack = new Stack<object?>(); stack.Push(doc);
        while (stack.Count > 0)
            switch (stack.Pop())
            {
                case PyObject o:
                    if (o.Count == 1 && o.TryGetValue("shader", out var s) && s is string name) output.Add(name);
                    foreach (var (_, v) in o) stack.Push(v);
                    break;
                case List<object?> l: foreach (var v in l) stack.Push(v); break;
            }
    }

    /// <summary>
    /// The text a large JSON object is stored as per top-level key (nnnotes split_json): [(key, value text)] cut from
    /// its minified text, or null when it stays whole (up to 512 KiB, not an object, a key outside [A-Za-z0-9_$.-]).
    /// The parts rejoin to exactly the text.
    /// </summary>
    public static List<(string Key, byte[] Text)>? Split(byte[] text)
    {
        const int minimum = 512 * 1024;
        if (text.Length <= minimum || text.Length == 0 || text[0] != (byte)'{') return null;
        var parts = new List<(string, byte[])>(); var at = 1;
        while (at < text.Length && text[at] != (byte)'}')
        {
            Require(text[at] == (byte)'"', "Split JSON: key expected");
            var end = SkipString(text, at);
            var key = Encoding.UTF8.GetString(text, at + 1, end - at - 2);
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '.' or '-')) return null;
            Require(text[end] == (byte)':', "Split JSON: colon expected");
            var start = end + 1; var stop = SkipValue(text, start);
            parts.Add((key, text[start..stop]));
            at = stop;
            if (text[at] == (byte)',') at++;
        }
        Require(at == text.Length - 1, "Split JSON: trailing data");
        return parts.Count < 2 ? null : parts;
    }
    static int SkipString(byte[] t, int at)
    {
        for (var i = at + 1; i < t.Length; i++)
        {
            if (t[i] == (byte)'\\') i++;
            else if (t[i] == (byte)'"') return i + 1;
        }
        throw new InvalidDataException("Split JSON: unterminated string");
    }
    static int SkipValue(byte[] t, int at)
    {
        var depth = 0;
        for (var i = at; i < t.Length; i++)
        {
            var c = t[i];
            if (c == (byte)'"') { i = SkipString(t, i) - 1; if (depth == 0) return i + 1; continue; }
            if (c is (byte)'{' or (byte)'[') depth++;
            else if (c is (byte)'}' or (byte)']') { if (depth == 0) return i; if (--depth == 0) return i + 1; }
            else if (c == (byte)',' && depth == 0) return i;
        }
        throw new InvalidDataException("Split JSON: unterminated value");
    }
}

/// <summary>The GameObject / Transform hierarchy of the loaded files (nnnotes unity.SceneGraph, without world matrices).</summary>
sealed class SceneGraph
{
    public readonly Dictionary<long, PyObject> Go = [], Tf = [];
    public readonly Dictionary<long, long> TfOfGo = [];
    readonly Dictionary<long, string> paths = [];
    Dictionary<string, List<long>>? gosByPath;
    public void Add(long pathId, bool gameObject, PyObject tree)
    {
        var table = gameObject ? Go : Tf;
        Require(table.TryAdd(pathId, tree), $"{(gameObject ? "GameObject" : "Transform")} path_id {pathId} appears in two files");
        if (!gameObject) TfOfGo[Long(Obj(tree["m_GameObject"])["m_PathID"])] = pathId;
    }
    public string Path(long tf)
    {
        if (paths.TryGetValue(tf, out var p)) return p;
        var parts = new List<string>();
        for (var t = tf; Tf.TryGetValue(t, out var node); t = Long(Obj(node["m_Father"])["m_PathID"]))
            parts.Add(Go.TryGetValue(Long(Obj(node["m_GameObject"])["m_PathID"]), out var go) ? Str(go["m_Name"]) : "?");
        parts.Reverse();
        return paths[tf] = string.Join('/', parts);
    }
    public List<long> GosAtPath(string path)
    {
        if (gosByPath == null)
        {
            gosByPath = [];
            foreach (var (go, tf) in TfOfGo) (gosByPath.TryGetValue(Path(tf), out var l) ? l : gosByPath[Path(tf)] = []).Add(go);
        }
        return gosByPath.TryGetValue(path, out var list) ? list : [];
    }
}

/// <summary>The part of nnnotes' export.Exporter a Live2D model uses ("generic" clips, meshes as name + vertex count).</summary>
sealed class Live2DExporter
{
    readonly record struct UObj(AssetsFileInstance File, AssetFileInfo Info)
    {
        public (string, long) Key => (File.name, Info.PathId);
        public string Type => ((AssetClassID)Info.TypeId).ToString();
    }
    const string DefaultResources = "Library/unity default resources";
    static readonly string[] Header = ["m_GameObject", "m_Script"];
    static readonly Dictionary<long, string> ClassId = new()
    {
        [1] = "GameObject", [4] = "Transform", [23] = "MeshRenderer", [95] = "Animator", [198] = "ParticleSystem",
        [199] = "ParticleSystemRenderer", [212] = "SpriteRenderer", [224] = "RectTransform", [225] = "CanvasGroup",
        [114] = "MonoBehaviour", [137] = "SkinnedMeshRenderer", [120] = "LineRenderer", [96] = "TrailRenderer",
        [331] = "SpriteMask", [222] = "CanvasRenderer",
    };
    static readonly Dictionary<long, (string, int)> TransformAttr = new()
    {
        [1] = ("m_LocalPosition", 3), [2] = ("m_LocalRotation", 4), [3] = ("m_LocalScale", 3), [4] = ("localEulerAnglesRaw", 3),
    };
    static readonly string[] EngineProps = ["m_IsActive", "m_Enabled", "m_Color.r", "m_Color.g", "m_Color.b", "m_Color.a",
        "m_Size.x", "m_Size.y", "m_FlipX", "m_FlipY", "m_SortingOrder", "m_Sprite", "m_Alpha",
        "m_SizeDelta.x", "m_SizeDelta.y", "m_AnchoredPosition.x", "m_AnchoredPosition.y",
        "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z"];
    static readonly string[] RectProps = ["m_AnchoredPosition.x", "m_AnchoredPosition.y", "m_SizeDelta.x", "m_SizeDelta.y",
        "m_AnchorMin.x", "m_AnchorMin.y", "m_AnchorMax.x", "m_AnchorMax.y", "m_Pivot.x", "m_Pivot.y"];
    static readonly string[] MaterialProps = ["_Color", "_TintColor", "_MainTex_ST", "_BaseColor", "_EmissionColor", "_Alpha", "_Cutoff",
        "_Intensity", "_Offset", "_Scroll", "_ScrollX", "_ScrollY", "_Power", "_Value", "_Glow", "_GlowColor", "_AddColor",
        "_MultiplyColor", "_Progress", "_Fade", "_Brightness", "_Dissolve", "_MainTex", "_Speed", "_Rotation"];

    readonly AssetsManager manager;
    readonly List<AssetsFileInstance> files;
    readonly Dictionary<string, AssetsFileInstance> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ApkData apk;
    readonly Config config;
    readonly SceneGraph graph = new();
    readonly Dictionary<(string, long), PyObject> trees = [];
    readonly Dictionary<(string, long), (string Namespace, string Class)?> classes = [];
    readonly Dictionary<(string, long), UObj> objects = [];
    readonly Dictionary<long, string> goFile = [];
    readonly Dictionary<(string, long), string> componentRef = [];
    readonly Dictionary<(string, long), object?> done = [];
    readonly Dictionary<(string, long), PyObject> textures = [];
    readonly Dictionary<string, UObj> textureFiles = new(StringComparer.Ordinal);   // texture path -> Texture2D
    readonly Dictionary<string, UObj> Shaders = new(StringComparer.Ordinal);  // registered (name -> Shader)
    readonly Dictionary<string, UObj> shaderByName = new(StringComparer.Ordinal);    // every Shader of the closure
    readonly HashSet<string> materialProps = new(StringComparer.Ordinal);
    readonly Dictionary<(string, long), List<string>> namesOf = [];
    readonly Dictionary<string, HashSet<string>> namesByClass = new(StringComparer.Ordinal);
    readonly Dictionary<(string, long), Dictionary<string, object?>> clipRecs = [];
    Dictionary<uint, HashSet<string>>? relTable;
    readonly Dictionary<string, List<long>> gosAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, uint> crcs = new(StringComparer.Ordinal);

    public Live2DExporter(AssetsManager manager, List<AssetsFileInstance> files, ApkData apk, Config config)
    {
        this.manager = manager; this.files = files; this.apk = apk; this.config = config;
        foreach (var file in files) byName[file.name] = file;
        foreach (var file in files)
        {
            manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
            foreach (var info in file.file.AssetInfos)
            {
                var o = new UObj(file, info);
                objects[o.Key] = o;
                switch ((AssetClassID)info.TypeId)
                {
                    case AssetClassID.GameObject: graph.Add(info.PathId, true, Tree(o)); goFile[info.PathId] = file.name; break;
                    case AssetClassID.Transform or AssetClassID.RectTransform: graph.Add(info.PathId, false, Tree(o)); break;
                    case AssetClassID.Shader:
                        var name = manager.GetBaseField(file, info)["m_ParsedForm"]["m_Name"].AsString;
                        shaderByName.TryAdd(name, o); Shaders.TryAdd(name, o); break;
                }
            }
        }
        // Components living on GameObjects are exported where they sit in the hierarchy; references to them elsewhere
        // become {component, gameObject}.
        foreach (var (goPid, go) in graph.Go)
        {
            var path = graph.TfOfGo.TryGetValue(goPid, out var tf) ? graph.Path(tf) : Str(go["m_Name"]);
            foreach (var c in List(go["m_Component"])) componentRef[(goFile[goPid], Long(UnityTree.Obj(UnityTree.Obj(c)["component"])["m_PathID"]))] = path;
        }
    }

    // ------------------------------------------------------------------ objects
    PyObject Tree(UObj o)
    {
        if (!trees.TryGetValue(o.Key, out var tree)) trees[o.Key] = tree = UnityTree.Obj(UnityTree.Read(manager.GetBaseField(o.File, o.Info)));
        return tree;
    }

    /// <summary>The object a PPtr read from `owner` points at; null for a null PPtr.</summary>
    UObj? Deref(UObj owner, object? pptr)
    {
        var p = UnityTree.Obj(pptr); var fileId = (int)Long(p["m_FileID"]); var pathId = Long(p["m_PathID"]);
        if (pathId == 0) return null;
        var file = owner.File;
        if (fileId != 0)
        {
            var externals = owner.File.file.Metadata.Externals;
            Require(fileId > 0 && fileId <= externals.Count, "Invalid external reference");
            var external = externals[fileId - 1].PathName;
            Require(external != DefaultResources, "reference into unity default resources needs player data");
            var name = external.Replace('\\', '/').Split('/')[^1];
            Require(byName.TryGetValue(name, out file!), $"Reference into {name}, which is not in the model's bundles");
        }
        var info = file.file.GetAssetInfo(pathId);
        Require(info != null, $"Null or missing object reference {file.name}:{pathId}");
        return new UObj(file, info!);
    }

    /// <summary>(namespace, class) of a MonoBehaviour's or a clip binding's script PPtr (the APK's shared_monoscripts).</summary>
    (string Namespace, string Class)? Script(UObj owner, object? pptr)
    {
        var p = UnityTree.Obj(pptr); var fileId = (int)Long(p["m_FileID"]); var pathId = Long(p["m_PathID"]);
        if (pathId == 0) return null;
        if (fileId != 0)
        {
            var name = owner.File.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/').Split('/')[^1];
            if (name.Equals(apk.ScriptFile, StringComparison.OrdinalIgnoreCase))
            {
                Require(apk.Scripts.TryGetValue(pathId, out var s), $"MonoScript {pathId} is not in the APK's {apk.ScriptFile}");
                return (s.Namespace, s.Class);
            }
        }
        var script = Deref(owner, pptr)!.Value;
        var tree = Tree(script);
        return (Str(tree["m_Namespace"]), Str(tree["m_ClassName"]));
    }

    string ClassOf(UObj o)
    {
        if (!classes.TryGetValue(o.Key, out var c))
            classes[o.Key] = c = Script(o, UnityTree.Read(manager.GetBaseField(o.File, o.Info)["m_Script"]));
        return c?.Class ?? throw new InvalidDataException($"MonoBehaviour {o.File.name}:{o.Info.PathId} has no script");
    }
    string? MonoClass(UObj o)
    {
        try { return ClassOf(o); } catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    // ------------------------------------------------------------------ values
    object? Value(UObj owner, object? v)
    {
        if (IsPPtr(v)) return Ref(owner, v);
        switch (v)
        {
            case PyObject o:
                var output = new PyObject();
                foreach (var (key, item) in o) output[key] = Value(owner, item);
                return output;
            case List<object?> list:
                if (list.All(x => x is null or bool or long or ulong or double or string)) return new List<object?>(list);
                return list.Select(x => Value(owner, x)).ToList();
            case byte[] bytes: return Convert.ToHexStringLower(bytes);
            default: return v;
        }
    }

    object? Ref(UObj owner, object? pptr)
    {
        if (Deref(owner, pptr) is not { } o) return null;
        switch (o.Type)
        {
            case "Texture2D": return Texture(o);
            case "Sprite": throw new NotSupportedException($"Sprite reference in a Live2D model ({o.File.name}:{o.Info.PathId})");
            case "Material": return Material(o);
            case "Shader": return new PyObject { ["shader"] = Shader(o) };
            case "Mesh":
                var mesh = manager.GetBaseField(o.File, o.Info);
                return new PyObject { ["mesh"] = mesh["m_Name"].AsString, ["vertexCount"] = (long)mesh["m_VertexData"]["m_VertexCount"].AsUInt };
            case "AnimationClip": return Clip(o);
            case "GameObject": return new PyObject { ["gameObject"] = graph.Path(graph.TfOfGo[o.Info.PathId]) };
            case "Transform" or "RectTransform": return new PyObject { ["transform"] = graph.Path(o.Info.PathId) };
        }
        if (componentRef.TryGetValue(o.Key, out var path))
        {
            var desc = new PyObject { ["component"] = o.Type, ["gameObject"] = path };
            if (o.Type == "MonoBehaviour") desc["class"] = ClassOf(o);
            return desc;
        }
        if (o.Type == "MonoBehaviour") return Scriptable(o);
        if (o.Type == "MonoScript")
        {
            var ms = Tree(o);
            return new PyObject { ["script"] = $"{Str(ms["m_Namespace"])}.{Str(ms["m_ClassName"])}".TrimStart('.') };
        }
        return new PyObject { ["object"] = o.Type, ["name"] = Tree(o).Get("m_Name") };
    }

    string Shader(UObj o)
    {
        var name = manager.GetBaseField(o.File, o.Info)["m_ParsedForm"]["m_Name"].AsString;
        Shaders.TryAdd(name, o);
        return name;
    }

    PyObject Texture(UObj o)
    {
        if (textures.TryGetValue(o.Key, out var descriptor)) return descriptor;
        var tt = Tree(o);
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{o.File.name}:{o.Info.PathId}")))[..8];
        var file = $"textures/{SafeFile(Str(tt["m_Name"]))}-{hash}.png";
        textureFiles[file] = o;
        return textures[o.Key] = new PyObject
        {
            ["texture"] = file, ["name"] = tt["m_Name"], ["width"] = tt["m_Width"], ["height"] = tt["m_Height"],
            ["format"] = tt["m_TextureFormat"], ["mipCount"] = tt.Get("m_MipCount", 1L), ["colorSpace"] = tt.Get("m_ColorSpace"),
            ["settings"] = tt.Get("m_TextureSettings"),
        };
    }
    static string SafeFile(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_.-]+", "_");

    PyObject Material(UObj o)
    {
        var m = Live2DModel.Material(Tree(o), p => Ref(o, p));
        materialProps.UnionWith(UnityTree.Obj(m["floats"]).Keys);
        materialProps.UnionWith(UnityTree.Obj(m["colors"]).Keys);
        return m;
    }

    PyObject Scriptable(UObj o)
    {
        if (done.TryGetValue(o.Key, out var existing)) return (PyObject)existing!;
        var cls = ClassOf(o);
        if (StubAsset(cls)) return new PyObject { ["asset"] = cls, ["name"] = manager.GetBaseField(o.File, o.Info)["m_Name"].AsString };
        var tt = Tree(o);
        var reference = new PyObject { ["asset"] = cls, ["name"] = tt.Get("m_Name", "") };
        done[o.Key] = reference;
        var output = new PyObject { ["asset"] = cls, ["name"] = tt.Get("m_Name", "") };
        foreach (var (key, value) in tt) if (!Header.Contains(key)) output[key] = Value(o, value);
        return output;
    }
    static bool StubAsset(string cls) => cls == "CubismMoc";

    PyObject Component(UObj o)
    {
        var tt = Tree(o);
        var key = o.Type == "MonoBehaviour" ? "MonoBehaviour:" + MonoClass(o) : o.Info.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (namesByClass.TryGetValue(key, out var set) ? set : namesByClass[key] = new(StringComparer.Ordinal)).UnionWith(Names(o));
        var output = new PyObject { ["type"] = o.Type };
        if (o.Type == "MonoBehaviour") output["class"] = ClassOf(o);
        foreach (var (k, v) in tt) if (!Header.Contains(k)) output[k] = Value(o, v);
        return output;
    }

    List<string> Names(UObj o)
    {
        if (!namesOf.TryGetValue(o.Key, out var names)) namesOf[o.Key] = names = FlatNames(Tree(o));
        return names;
    }

    // ------------------------------------------------------------------ hierarchy
    List<object?> Hierarchy(long rootTf)
    {
        var byPid = new Dictionary<long, List<UObj>>();
        foreach (var o in objects.Values) (byPid.TryGetValue(o.Info.PathId, out var l) ? l : byPid[o.Info.PathId] = []).Add(o);
        var nodes = new List<object?>();
        void Walk(long tf)
        {
            var t = graph.Tf[tf]; var go = graph.Go[Long(UnityTree.Obj(t["m_GameObject"])["m_PathID"])];
            var components = new List<object?>();
            foreach (var c in List(go["m_Component"]))
            {
                var pid = Long(UnityTree.Obj(UnityTree.Obj(c)["component"])["m_PathID"]);
                var candidates = (byPid.TryGetValue(pid, out var l) ? l : []).Where(x => x.Type != "GameObject").ToList();
                Require(candidates.Count == 1, $"{graph.Path(tf)}: component {pid} resolves to {candidates.Count}");
                var co = candidates[0];
                if (co.Type is "Transform" or "RectTransform") continue;
                components.Add(Component(co));
            }
            var node = new PyObject
            {
                ["path"] = graph.Path(tf), ["name"] = go["m_Name"], ["active"] = Truthy(go["m_IsActive"]), ["layer"] = go["m_Layer"],
                ["tag"] = go.Get("m_Tag"), ["localPosition"] = t["m_LocalPosition"], ["localRotation"] = t["m_LocalRotation"],
                ["localScale"] = t["m_LocalScale"], ["components"] = components,
            };
            if (t.ContainsKey("m_AnchorMin"))
            {
                var rect = new PyObject();
                foreach (var k in new[] { "m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta", "m_Pivot" }) rect[k] = t[k];
                node["rect"] = rect;
            }
            nodes.Add(node);
            foreach (var ch in List(t["m_Children"])) Walk(Long(UnityTree.Obj(ch)["m_PathID"]));
        }
        Walk(rootTf);
        return nodes;
    }

    // ------------------------------------------------------------------ clips
    uint Crc(string s)
    {
        if (!crcs.TryGetValue(s, out var c)) crcs[s] = c = ~BinaryTools.Crc32(Encoding.UTF8.GetBytes(s), 0xffffffff);
        return c;
    }

    /// <summary>crc32 -> transform paths relative to any of their ancestors ("" for crc 0).</summary>
    Dictionary<uint, HashSet<string>> RelTable()
    {
        if (relTable != null) return relTable;
        relTable = new() { [0] = [""] };
        foreach (var tf in graph.Tf.Keys)
        {
            var parts = graph.Path(tf).Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var s = string.Join('/', parts[i..]);
                (relTable.TryGetValue(Crc(s), out var set) ? set : relTable[Crc(s)] = new(StringComparer.Ordinal)).Add(s);
            }
        }
        return relTable;
    }

    List<long> GosAt(string rel)
    {
        if (!gosAt.TryGetValue(rel, out var list))
            gosAt[rel] = list = graph.TfOfGo.Where(p => rel.Length == 0 || graph.Path(p.Value) == rel || graph.Path(p.Value).EndsWith("/" + rel, StringComparison.Ordinal)).Select(p => p.Key).ToList();
        return list;
    }

    HashSet<string> BoundNames(Dictionary<string, object?> r)
    {
        var tid = (long)r["typeID"]!; var names = new HashSet<string>(StringComparer.Ordinal);
        var script = ((string, string)?)r["script"];
        foreach (var goPid in GosAt((string)r["path"]!))
        {
            var go = graph.Go[goPid];
            if (tid == 1) { r["component"] = "GameObject"; names.UnionWith(FlatNames(go)); }
            foreach (var c in List(go["m_Component"]))
            {
                if (!objects.TryGetValue((goFile[goPid], Long(UnityTree.Obj(UnityTree.Obj(c)["component"])["m_PathID"])), out var co)) continue;
                if (tid == 114)
                {
                    if (script != null && co.Type == "MonoBehaviour" && MonoClass(co) == script.Value.Item2) names.UnionWith(Names(co));
                }
                else if (co.Info.TypeId == tid) { r["component"] = co.Type; names.UnionWith(Names(co)); }
            }
        }
        return names;
    }

    HashSet<string> ClassNames(Dictionary<string, object?> r)
    {
        var tid = (long)r["typeID"]!; var script = ((string, string)?)r["script"];
        var names = new HashSet<string>(EngineProps.Concat(RectProps), StringComparer.Ordinal);
        foreach (var p in MaterialProps.Concat(materialProps).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            names.Add($"material.{p}");
            foreach (var c in "rgbaxyzw") names.Add($"material.{p}.{c}");
        }
        var key = tid == 114 && script != null ? "MonoBehaviour:" + script.Value.Item2 : tid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (namesByClass.TryGetValue(key, out var own)) names.UnionWith(own);
        return names;
    }

    void Resolve(Dictionary<string, object?> r)
    {
        var crc = (uint)r["pathCrc"]!;
        if (r["path"] == null)
        {
            var cands = RelTable().TryGetValue(crc, out var set) ? set : [];
            if (cands.Count == 1) r["path"] = cands.First();
            else r["pathCandidates"] = cands.Count > 1 ? cands.Order(StringComparer.Ordinal).Cast<object?>().ToList() : null;
        }
        Require((long?)r["customType"] != 22, "Renderer material clip bindings are not supported in Live2D models");
        if (r["attribute"] == null)
        {
            var a = (uint)r["attributeCrc"]!;
            var hits = r["path"] != null ? BoundNames(r).Where(x => Crc(x) == a).ToList() : [];
            if (hits.Count != 1) hits = ClassNames(r).Where(x => Crc(x) == a).ToList();
            if (hits.Count == 1) r["attribute"] = hits[0];
        }
    }

    Dictionary<string, object?> BindingRecord(UObj owner, PyObject b)
    {
        var tid = Long(b["typeID"]);
        var r = new Dictionary<string, object?>
        {
            ["pathCrc"] = (uint)Long(b["path"]), ["typeID"] = tid, ["attributeCrc"] = (uint)Long(b["attribute"]),
            ["pptr"] = Truthy(b["isPPtrCurve"]), ["int"] = Truthy(b["isIntCurve"]), ["customType"] = b.Get("customType") is { } ct ? Long(ct) : null,
            ["serializeReference"] = Truthy(b.Get("isSerializeReferenceCurve")), ["path"] = null, ["pathCandidates"] = null,
            ["attribute"] = null, ["curveCount"] = 1, ["transformAttr"] = false, ["component"] = null, ["script"] = null,
        };
        if (tid == 4 && TransformAttr.TryGetValue(Long(b["attribute"]), out var ta) && !(bool)r["pptr"]!)
        {
            r["attribute"] = ta.Item1; r["curveCount"] = ta.Item2; r["transformAttr"] = true;
        }
        if (tid == 114) r["script"] = Script(owner, b["script"]);
        Resolve(r);
        return r;
    }

    object? Clip(UObj o)
    {
        if (done.TryGetValue(o.Key, out var existing)) return existing;
        var tt = Tree(o);
        Require(!Truthy(tt["m_Legacy"]) && !Truthy(tt["m_Compressed"]), $"clip {Str(tt["m_Name"])}: legacy/compressed");
        var mc = UnityTree.Obj(tt["m_MuscleClip"]);
        var data = UnityTree.Obj(UnityTree.Obj(mc["m_Clip"])["data"]);
        var cbc = UnityTree.Obj(tt["m_ClipBindingConstant"]);
        var streamed = UnityTree.Obj(data["m_StreamedClip"]); var dense = UnityTree.Obj(data["m_DenseClip"]);
        var constant = List(UnityTree.Obj(data["m_ConstantClip"])["data"]);
        var pptrMapping = cbc.Get("pptrCurveMapping") as List<object?> ?? [];
        var bindings = List(cbc["genericBindings"]).Select(b => BindingRecord(o, UnityTree.Obj(b))).ToList();
        var name = Str(tt["m_Name"]);
        var floats = bindings.Where(r => !(bool)r["pptr"]!).Sum(r => (int)r["curveCount"]!);
        var total = Long(streamed["curveCount"]) + Long(dense["m_CurveCount"]) + constant.Count;
        Require(total == floats, $"clip {name}: curve count != binding curve count");
        var reference = new PyObject { ["clip"] = name };
        done[o.Key] = reference;
        var events = List(tt["m_Events"]).Select(e =>
        {
            var ev = UnityTree.Obj(e); var output = new PyObject();
            foreach (var k in new[] { "time", "functionName", "data", "floatParameter", "intParameter", "messageOptions" }) output[k] = ev[k];
            return (object?)output;
        }).ToList();
        var s = new PyObject { ["curveCount"] = streamed["curveCount"], ["frames"] = StreamedFrames(List(streamed["data"])) };
        if (Truthy(streamed.Get("discreteCurveCount"))) s["discreteCurveCount"] = streamed["discreteCurveCount"];
        var clip = new PyObject
        {
            ["clip"] = name, ["sampleRate"] = tt["m_SampleRate"], ["wrapMode"] = tt["m_WrapMode"], ["startTime"] = mc["m_StartTime"],
            ["stopTime"] = mc["m_StopTime"], ["loopTime"] = Truthy(mc["m_LoopTime"]), ["cycleOffset"] = mc["m_CycleOffset"], ["events"] = events,
            ["bindings"] = bindings.Select(BindingGeneric).ToList(), ["streamed"] = s,
            ["dense"] = new PyObject
            {
                ["curveCount"] = dense["m_CurveCount"], ["frameCount"] = dense["m_FrameCount"], ["sampleRate"] = dense["m_SampleRate"],
                ["beginTime"] = dense["m_BeginTime"], ["samples"] = new List<object?>(List(dense["m_SampleArray"])),
            },
            ["constant"] = new List<object?>(constant),
        };
        if (pptrMapping.Count > 0) clip["pptrCurveMapping"] = pptrMapping.Select(p => Ref(o, p)).ToList();
        return clip;
    }

    static object? BindingGeneric(Dictionary<string, object?> r)
    {
        Require(!(bool)r["serializeReference"]!, "SerializeReference clip bindings are not supported");
        var tid = (long)r["typeID"]!; var script = ((string, string)?)r["script"];
        var d = new PyObject
        {
            ["path"] = r["path"], ["typeID"] = tid,
            ["class"] = tid == 114 && script != null ? script.Value.Item2 : ClassId.TryGetValue(tid, out var c) ? c : $"classID {tid}",
            ["attribute"] = r["attribute"],
        };
        if ((int)r["curveCount"]! != 1) d["curves"] = (long)(int)r["curveCount"]!;
        if ((bool)r["pptr"]!) d["pptr"] = true;
        if ((bool)r["int"]!) d["int"] = true;
        if (r["path"] == null) d["pathCrc"] = (long)(uint)r["pathCrc"]!;
        if (r["attribute"] == null) d["attributeCrc"] = (long)(uint)r["attributeCrc"]!;
        return d;
    }

    /// <summary>Mecanim StreamedClip words -> [[time, [[curve, c0, c1, c2, c3], ...]], ...] without the closing +inf frame.</summary>
    static List<object?> StreamedFrames(List<object?> words)
    {
        var frames = new List<object?>();
        if (words.Count == 0) return frames;
        var buffer = new byte[words.Count * 4];
        for (var i = 0; i < words.Count; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4 * i), (uint)Long(words[i]));
        var at = 0; double lastTime = 0; var lastCount = 0;
        while (at < buffer.Length)
        {
            Require(at + 8 <= buffer.Length, "streamed clip frame header truncated");
            var time = (double)System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(at));
            var n = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at + 4)); at += 8;
            Require(n >= 0 && at + 20L * n <= buffer.Length, "streamed clip keys truncated");
            var keys = new List<object?>(n);
            for (var k = 0; k < n; k++, at += 20)
            {
                double F(int j) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(at + 4 + 4 * j));
                keys.Add(new List<object?> { (long)System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at)), F(0), F(1), F(2), F(3) });
            }
            frames.Add(new List<object?> { time, keys });
            lastTime = time; lastCount = n;
        }
        Require(double.IsPositiveInfinity(lastTime) && lastCount == 0, $"streamed clip does not close with an empty +inf frame: {lastTime}");
        frames.RemoveAt(frames.Count - 1);
        return frames;
    }

    // ------------------------------------------------------------------ the model
    public Artifact[] Model(string key, string internalId, string outDir)
    {
        var name = key.Split('/')[^1];
        Directory.CreateDirectory(outDir);
        var root = ContainerGameObject(internalId);
        var mocs = objects.Values.Where(o => o.Type == "MonoBehaviour" && MonoClass(o) == "CubismMoc").ToList();
        Require(mocs.Count == 1, $"{name}: {mocs.Count} CubismMoc");
        var mocField = manager.GetBaseField(mocs[0].File, mocs[0].Info)["_bytes"];
        var moc = Bytes(mocField);
        var canvas = Live2DModel.Canvas(moc);
        var prefab = new PyObject { ["key"] = key, ["nodes"] = Hierarchy(graph.TfOfGo[root.Info.PathId]), ["canvas"] = canvas };
        // atlas pages, in name order (their descriptors; the files of the pages the drawables use are written below)
        foreach (var page in objects.Values.Where(o => o.Type == "Texture2D").OrderBy(o => Str(Tree(o)["m_Name"]), StringComparer.Ordinal)) Texture(page);

        var nodes = List(prefab["nodes"]).Select(n => UnityTree.Obj(n)).ToList();
        var components = nodes.SelectMany(n => List(n["components"]).Select(c => UnityTree.Obj(c))).ToList();
        var drawableTextures = components.Where(c => c.Get("class") as string == "CubismRenderer" && Truthy(c.Get("_mainTexture")))
            .Select(c => Str(UnityTree.Obj(c["_mainTexture"])["texture"])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var materials = components.Where(c => c.Get("type") as string == "MeshRenderer" && c.Get("m_Materials") is List<object?> { Count: > 0 } m && m[0] != null)
            .Select(c => UnityTree.Obj(List(c["m_Materials"])[0])).ToList();

        var resources = apk.Resources;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Live2DModel.ShaderNames(prefab, names); Live2DModel.ShaderNames(resources, names);
        var dumps = new Dictionary<string, ShaderDump.Dump>(StringComparer.Ordinal);
        foreach (var shader in names)
        {
            if (Shaders.TryGetValue(shader, out var o)) dumps[shader] = ShaderDump.Read(manager.GetBaseField(o.File, o.Info), o.File.name);
            else if (apk.Shaders.TryGetValue(shader, out var dump)) dumps[shader] = dump;
            else throw new InvalidDataException($"{key}: shaders neither in the model's bundles nor in the APK data: {shader}");
        }
        var doc = new PyObject
        {
            ["format"] = (long)Live2DModel.Format, ["name"] = name, ["key"] = key, ["moc3"] = $"{name}.moc3", ["prefab"] = $"{name}.prefab.json",
            ["textures"] = drawableTextures.Cast<object?>().ToList(), ["canvas"] = canvas, ["shaders"] = "shaders/shaders.json", ["resources"] = resources,
        };
        if (materials.Any(m => List(m["keywords"]).Contains(Live2DModel.MaskKeyword))) materials.AddRange(resources.Select(p => UnityTree.Obj(p.Value)));
        var shaderFiles = ShaderDump.Files(dumps, materials);

        var written = new List<Artifact>();
        void Put(string path, byte[] data, string mime)
        {
            var full = Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            written.Add(new(path, path, mime, data.LongLength, Convert.ToHexStringLower(SHA256.HashData(data)), null));
        }
        Put(Live2DModel.Index, PyJson.Minified(doc), "application/json");
        Put($"{name}.moc3", moc, "application/octet-stream");
        Put($"{name}.prefab.json", PyJson.Minified(prefab), "application/json");
        foreach (var file in drawableTextures)
        {
            var (pixels, width, height) = Worker.Texture(new AssetExternal { file = textureFiles[file].File, info = textureFiles[file].Info, baseField = manager.GetBaseField(textureFiles[file].File, textureFiles[file].Info) }, config);
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < height; y++) Buffer.BlockCopy(pixels, y * width * 4, flipped, (height - 1 - y) * width * 4, width * 4);
            var temporary = Path.Combine(outDir, $".{Guid.NewGuid():N}.png");
            BinaryTools.Png(temporary, flipped, width, height);
            Put(file, File.ReadAllBytes(temporary), "image/png"); File.Delete(temporary);
        }
        var index = new List<object?>();
        foreach (var dump in dumps.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (!shaderFiles.Contains(dump.ParsedFile)) continue;
            var variants = dump.Variants.Where(v => shaderFiles.Contains(v.File)).ToList();
            index.Add(new PyObject { ["name"] = dump.Name, ["source"] = dump.Source, ["parsed"] = dump.ParsedFile, ["variants"] = variants.Select(v => (object?)v.Record()).ToList() });
            Put($"shaders/{dump.ParsedFile}", PyJson.Minified(dump.Parsed), "application/json");
            foreach (var v in variants) Put($"shaders/{v.File}", ShaderDump.Text(v.Code), "text/plain; charset=utf-8");
        }
        Put("shaders/shaders.json", PyJson.Minified(index), "application/json");
        var summary = new PyObject
        {
            ["name"] = name, ["key"] = key, ["canvas"] = canvas, ["textures"] = drawableTextures.Cast<object?>().ToList(), ["nodes"] = (long)nodes.Count,
            ["shaders"] = names.Cast<object?>().ToList(),
        };
        File.WriteAllBytes(Path.Combine(outDir, Live2DModel.SummaryFile), PyJson.Minified(summary));
        return [.. written.OrderBy(a => a.Name, StringComparer.Ordinal)];
    }

    static byte[] Bytes(AssetTypeValueField field)
    {
        var array = field.TemplateField.Type == "TypelessData" ? field : field["Array"];
        return array.Value?.ValueType == AssetValueType.ByteArray ? array.AsByteArray : [.. array.Children.Select(c => (byte)c.AsUInt)];
    }

    UObj ContainerGameObject(string internalId)
    {
        foreach (var o in objects.Values.Where(o => o.Type == "AssetBundle"))
            foreach (var item in List(Tree(o)["m_Container"]))
            {
                var pair = List(item);
                if (!Str(pair[0]).Equals(internalId, StringComparison.OrdinalIgnoreCase)) continue;
                var asset = Deref(o, UnityTree.Obj(pair[1])["asset"]);
                Require(asset is { Type: "GameObject" }, $"{internalId}: container asset is {asset?.Type ?? "null"}");
                return asset!.Value;
            }
        throw new InvalidDataException($"{internalId} not in any AssetBundle container");
    }
}
