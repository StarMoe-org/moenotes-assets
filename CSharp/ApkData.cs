using System.IO.Compression;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

/// <summary>
/// What Live2D models need from the game's APK (base.apk), which the CDN does not serve:
///
///   - the script classes of the APK's shared_monoscripts bundle: every MonoBehaviour of a model bundle names its
///     script there (m_Script), and the model's components are identified by that class;
///   - the Cubism mask materials (Resources "Live2D/Cubism/Materials/Mask" and "MaskCulling" of the boot data
///     data.unity3d) and their shader "Live2D Cubism/Mask", as nnnotes exports them for model.json `resources`.
///
/// Extracted once per APK into data_dir/apk-data/&lt;sha256 of base.apk&gt;/: apk.json (the facts above) and
/// shaders/ (the parsed form and GLES3 programs of the mask shader, as ShaderDump writes them).
/// </summary>
public sealed class ApkData
{
    public const int Format = 1;
    public const string Manifest = "apk.json";
    const string MonoScripts = "assets/aa/Android/shared_monoscripts.bundle";
    const string BootData = "assets/bin/Data/data.unity3d";
    // model.json resources: name -> Resources.Load path (nnnotes advscene.RESOURCES)
    static readonly (string Name, string Path)[] ResourcePaths = [("cubismMask", "Live2D/Cubism/Materials/Mask"), ("cubismMaskCulling", "Live2D/Cubism/Materials/MaskCulling")];

    public required string Sha256 { get; init; }
    public required string UnityVersion { get; init; }
    public required string ScriptFile { get; init; }
    public required Dictionary<long, (string Assembly, string Namespace, string Class)> Scripts { get; init; }
    public required PyObject Resources { get; init; }
    public required Dictionary<string, ShaderDump.Dump> Shaders { get; init; }

    public static ApkData Load(string directory)
    {
        var doc = UnityTree.Obj(PyJson.Parse(File.ReadAllBytes(Path.Combine(directory, Manifest))));
        Require(UnityTree.Long(doc["format"]) == Format, "Unsupported APK data format");
        var apk = UnityTree.Obj(doc["apk"]); var scripts = UnityTree.Obj(doc["scripts"]);
        var table = new Dictionary<long, (string, string, string)>();
        foreach (var row in UnityTree.List(scripts["classes"]))
        {
            var r = UnityTree.List(row);
            table[UnityTree.Long(r[0])] = (UnityTree.Str(r[1]), UnityTree.Str(r[2]), UnityTree.Str(r[3]));
        }
        var shaders = new Dictionary<string, ShaderDump.Dump>(StringComparer.Ordinal);
        foreach (var item in UnityTree.List(doc["shaders"]))
        {
            var record = UnityTree.Obj(item); var name = UnityTree.Str(record["name"]);
            var parsed = UnityTree.Obj(PyJson.Parse(File.ReadAllBytes(Path.Combine(directory, "shaders", UnityTree.Str(record["parsed"])))));
            var variants = UnityTree.List(record["variants"]).Select(v =>
            {
                var r = UnityTree.Obj(v); var file = UnityTree.Str(r["file"]);
                Require(!file.Contains("..") && !Path.IsPathRooted(file), "Unsafe shader file in the APK data");
                return new ShaderDump.Variant(file, UnityTree.Long(r["subShader"]), UnityTree.Long(r["pass"]), UnityTree.Str(r["stage"]),
                    UnityTree.List(r["keywords"]).Select(UnityTree.Str).ToList(), File.ReadAllBytes(Path.Combine(directory, "shaders", file)));
            }).ToList();
            shaders[name] = new(name, UnityTree.Str(record["source"]), parsed, variants);
        }
        return new()
        {
            Sha256 = UnityTree.Str(apk["sha256"]), UnityVersion = UnityTree.Str(apk["unityVersion"]), ScriptFile = UnityTree.Str(scripts["file"]),
            Scripts = table, Resources = UnityTree.Obj(doc["resources"]), Shaders = shaders,
        };
    }

    /// <summary>Extracts the APK data of `apkPath` into `directory` (created; replaced when it exists).</summary>
    public static void Extract(string apkPath, string directory, Config config)
    {
        var sha256 = FileSha256(apkPath);
        var staging = directory + $".{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(staging);
        var manager = Worker.Manager(config);
        try
        {
            using (var zip = ZipFile.OpenRead(apkPath))
            {
                foreach (var (entry, target) in new[] { (MonoScripts, "monoscripts.bundle"), (BootData, "data.unity3d") })
                {
                    var e = zip.GetEntry(entry) ?? throw new InvalidDataException($"{Path.GetFileName(apkPath)} has no {entry}");
                    Require(e.Length <= config.InputBytes, $"{entry}: size budget");
                    e.ExtractToFile(Path.Combine(staging, target));
                }
            }
            // script classes
            var scriptsBundle = manager.LoadBundleFile(Path.Combine(staging, "monoscripts.bundle"), true);
            var classes = new List<object?>(); string? scriptFile = null; string? unity = null;
            for (var i = 0; i < scriptsBundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if (!scriptsBundle.file.BlockAndDirInfo.DirectoryInfos[i].IsSerialized) continue;
                var file = manager.LoadAssetsFileFromBundle(scriptsBundle, i, false);
                manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
                var scripts = file.file.GetAssetsOfType(AssetClassID.MonoScript);
                if (scripts.Count == 0) continue;
                Require(scriptFile == null, "More than one serialized file with MonoScripts in shared_monoscripts.bundle");
                scriptFile = file.name; unity = file.file.Metadata.UnityVersion;
                foreach (var info in scripts.OrderBy(s => s.PathId))
                {
                    var f = manager.GetBaseField(file, info);
                    classes.Add(new List<object?> { info.PathId, f["m_AssemblyName"].AsString, f["m_Namespace"].AsString, f["m_ClassName"].AsString });
                }
            }
            Require(scriptFile != null, "shared_monoscripts.bundle holds no MonoScript");
            // Cubism mask materials and their shader
            var boot = manager.LoadBundleFile(Path.Combine(staging, "data.unity3d"), true);
            var ggm = manager.LoadAssetsFileFromBundle(boot, "globalgamemanagers", true) ?? throw new InvalidDataException("data.unity3d has no globalgamemanagers");
            manager.LoadClassDatabaseFromPackage(ggm.file.Metadata.UnityVersion);
            var container = new List<(string Path, AssetTypeValueField Pointer)>();
            foreach (var info in ggm.file.GetAssetsOfType(AssetClassID.ResourceManager))
                foreach (var pair in manager.GetBaseField(ggm, info)["m_Container"]["Array"].Children) container.Add((pair["first"].AsString, pair["second"]));
            var resources = new PyObject(); var shaders = new Dictionary<string, ShaderDump.Dump>(StringComparer.Ordinal);
            foreach (var (name, path) in ResourcePaths)
            {
                var hits = container.Where(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
                Require(hits.Count == 1, $"Resources path {path}: {hits.Count} entries");
                var material = manager.GetExtAsset(ggm, hits[0].Pointer);
                Require(material.info?.TypeId == (int)AssetClassID.Material, $"Resources path {path}: not a Material");
                var tree = UnityTree.Obj(UnityTree.Read(material.baseField));
                resources[name] = Live2DModel.Material(tree, pptr =>
                {
                    var p = UnityTree.Obj(pptr);
                    if (UnityTree.Long(p["m_PathID"]) == 0) return null;
                    var target = manager.GetExtAsset(material.file, (int)UnityTree.Long(p["m_FileID"]), UnityTree.Long(p["m_PathID"]));
                    Require(target.info?.TypeId == (int)AssetClassID.Shader, $"Resources path {path}: references a {(AssetClassID?)target.info?.TypeId} (only its shader is supported)");
                    var dump = ShaderDump.Read(target.baseField, target.file.name);
                    shaders.TryAdd(dump.Name, dump);
                    return new PyObject { ["shader"] = dump.Name };
                });
            }
            // the shader files and apk.json
            var records = new List<object?>();
            foreach (var dump in shaders.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                Directory.CreateDirectory(Path.Combine(staging, "shaders"));
                File.WriteAllBytes(Path.Combine(staging, "shaders", dump.ParsedFile), PyJson.Minified(dump.Parsed));
                foreach (var v in dump.Variants)
                {
                    var target = Path.Combine(staging, "shaders", v.File.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, v.Code);
                }
                records.Add(new PyObject { ["name"] = dump.Name, ["source"] = dump.Source, ["parsed"] = dump.ParsedFile, ["variants"] = dump.Variants.Select(v => (object?)v.Record()).ToList() });
            }
            var doc = new PyObject
            {
                ["format"] = (long)Format,
                ["apk"] = new PyObject { ["sha256"] = sha256, ["bytes"] = new FileInfo(apkPath).Length, ["unityVersion"] = unity },
                ["scripts"] = new PyObject { ["file"] = scriptFile, ["classes"] = classes },
                ["resources"] = resources,
                ["shaders"] = records,
            };
            manager.UnloadAll(true);
            File.Delete(Path.Combine(staging, "monoscripts.bundle")); File.Delete(Path.Combine(staging, "data.unity3d"));
            File.WriteAllBytes(Path.Combine(staging, Manifest), PyJson.Minified(doc));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Move(staging, directory);
        }
        finally
        {
            manager.UnloadAll(true);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
