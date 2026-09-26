using System.Text;
using System.Text.Json.Nodes;
using Xunit;
namespace MoenotesAssets.Tests;

public sealed class ModelSiteTests
{
    [Fact]
    public void WritesJsonLikePython()
    {
        var doc = new PyObject
        {
            ["b"] = 1L, ["a"] = 1.0, ["f"] = (double)0.3f, ["inf"] = double.PositiveInfinity, ["ninf"] = double.NegativeInfinity, ["neg0"] = -0.0,
            ["s"] = "é\"\\\n\u0001", ["l"] = new List<object?> { true, null, 1e-5, 123456789012345.0 }, ["o"] = new PyObject(),
        };
        Assert.Equal("{\"b\":1,\"a\":1.0,\"f\":0.30000001192092896,\"inf\":1e999,\"ninf\":-1e999,\"neg0\":-0.0,\"s\":\"é\\\"\\\\\\n\\u0001\",\"l\":[true,null,1e-05,123456789012345.0],\"o\":{}}",
            PyJson.Dumps(doc));
        Assert.Contains("/x[1]", Assert.Throws<InvalidDataException>(() => PyJson.Dumps(new PyObject { ["x"] = new List<object?> { 1.0, double.NaN } })).Message);
        var parsed = UnityTree.Obj(PyJson.Parse(PyJson.Minified(doc)));
        Assert.Equal(PyJson.Dumps(doc), PyJson.Dumps(parsed));
        Assert.IsType<long>(parsed["b"]); Assert.IsType<double>(parsed["a"]); Assert.Equal(double.PositiveInfinity, parsed["inf"]);
        Assert.Equal(new[] { "b", "a", "f", "inf", "ninf", "neg0", "s", "l", "o" }, parsed.Keys);
    }

    [Fact]
    public void SplitsLargeObjectsByTopLevelKey()
    {
        Assert.Null(Live2DModel.Split(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}")));
        var big = new string('x', 600 * 1024);
        var doc = new PyObject { ["key"] = "k", ["nodes"] = new List<object?> { new PyObject { ["s"] = big + "\",}]{" }, 1.5 }, ["canvas"] = new PyObject { ["w"] = 1.0 } };
        var text = PyJson.Minified(doc);
        var parts = Live2DModel.Split(text)!;
        Assert.Equal(new[] { "key", "nodes", "canvas" }, parts.Select(p => p.Key));
        Assert.Equal(PyJson.Dumps(doc["nodes"]), Encoding.UTF8.GetString(parts[1].Text));
        var joined = "{" + string.Join(",", parts.Select(p => $"\"{p.Key}\":{Encoding.UTF8.GetString(p.Text)}")) + "}";
        Assert.Equal(Encoding.UTF8.GetString(text), joined);
        Assert.Null(Live2DModel.Split(PyJson.Minified(new PyObject { ["bad key"] = big, ["b"] = 1L })));
        Assert.Null(Live2DModel.Split(PyJson.Minified(new List<object?> { big, big })));
    }

    [Fact]
    public void ReadsTheMocCanvas()
    {
        var moc = new byte[0x80]; "MOC3"u8.CopyTo(moc); moc[4] = 5;
        BitConverter.GetBytes(0x60u).CopyTo(moc, 0x44);
        foreach (var (i, v) in new[] { (0, 6000f), (1, 3000f), (2, 4500f), (3, 6000f), (4, 9000f) }) BitConverter.GetBytes(v).CopyTo(moc, 0x60 + 4 * i);
        Assert.Equal("{\"pixelsPerUnit\":6000.0,\"originX\":3000.0,\"originY\":4500.0,\"width\":6000.0,\"height\":9000.0,\"mocVersion\":5}", PyJson.Dumps(Live2DModel.Canvas(moc)));
        Assert.Throws<InvalidDataException>(() => Live2DModel.Canvas(new byte[0x80]));
    }

    [Fact]
    public void ListsModelKeys()
    {
        var models = ModelSite.Models([
            "Character/Live2D/003_adv/adv_live2d_rana_003_casual_spring_01/model/adv_live2d_rana_003_casual_spring_01",
            "Character/Live2D/003_adv/adv_live2d_rana_003_casual_spring_01/common/motions/x",
            "Character/Live2D/mortis/live2d_mortis_01/model/live2d_mortis_01",
            "Character/Live2D/a/b/model/c",
        ]);
        Assert.Equal(new[] { "adv_live2d_rana_003_casual_spring_01", "live2d_mortis_01" }, models.Keys);
        Assert.Equal("003_adv", ModelSite.Group(models["adv_live2d_rana_003_casual_spring_01"]));
        Assert.Throws<InvalidDataException>(() => ModelSite.Models(["Character/Live2D/a/m/model/m", "Character/Live2D/b/m/model/m"]));
        Assert.Throws<InvalidDataException>(() => ModelSite.Models(["Character/Live2D/a/Big/model/Big"]));
    }

    [Fact]
    public void NamesModelsFromMasterData()
    {
        var master = new Dictionary<string, JsonArray>
        {
            ["MasterCharacterCostume"] = [
                new JsonObject { ["_id"] = 1, ["_characterID"] = 3, ["_live2dPath"] = "003_adv/a/model/a" },
                new JsonObject { ["_id"] = 2, ["_characterID"] = 3, ["_live2dPath"] = "003_adv/a/model/a" },
                new JsonObject { ["_id"] = 3, ["_characterID"] = 3, ["_live2dPath"] = "shared/s/model/s" },
                new JsonObject { ["_id"] = 4, ["_characterID"] = 4, ["_live2dPath"] = "shared/s/model/s" },
                new JsonObject { ["_id"] = 5, ["_characterID"] = 5, ["_live2dPath"] = "005/n/model/n" },
                new JsonObject { ["_id"] = 6, ["_characterID"] = 3, ["_live2dPath"] = "" },
            ],
            ["MasterCharacter"] = [new JsonObject { ["_id"] = 3, ["_nameTextID"] = "chara_3" }, new JsonObject { ["_id"] = 4, ["_nameTextID"] = "chara_4" }, new JsonObject { ["_id"] = 5, ["_nameTextID"] = "none" }],
            ["MasterText"] = [new JsonObject { ["_id"] = "chara_3", ["_japanese"] = "要 楽奈", ["_traditionalChinese"] = "要樂奈", ["_english"] = "" }],
        };
        var names = ModelSite.Names(master, "zh-Hant");
        var entry = Assert.Single(names);
        Assert.Equal(ModelSite.Live2DPrefix + "003_adv/a/model/a", entry.Key);
        Assert.Equal("{\"character\":3,\"names\":{\"ja\":\"要 楽奈\",\"zh-Hant\":\"要樂奈\"},\"label\":\"要樂奈\"}", entry.Value.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.False(ModelSite.Names(master, "ko").Single().Value.ContainsKey("label"));
    }

    [Fact]
    public void SelectsTheShaderProgramsOfTheMaterials()
    {
        ShaderDump.Variant V(int n, params string[] keywords) => new($"S/gles3/s0p0_vertex_{n}.glsl", 0, 0, "vertex", [.. keywords], []);
        var dumps = new Dictionary<string, ShaderDump.Dump>
        {
            ["S"] = new("S", "CAB-x", new PyObject(), [V(0), V(1, "CUBISM_MASK_ON"), V(2, "CUBISM_INVERT_ON"), V(3, "CUBISM_INVERT_ON", "CUBISM_MASK_ON")]),
        };
        PyObject M(params string[] keywords) => new() { ["shader"] = new PyObject { ["shader"] = "S" }, ["keywords"] = keywords.Cast<object?>().ToList() };
        // keywords no program uses (a global keyword such as _ADDITIONAL_LIGHTS) are ignored
        Assert.Equal(new[] { "S.json", "S/gles3/s0p0_vertex_0.glsl", "S/gles3/s0p0_vertex_3.glsl" },
            ShaderDump.Files(dumps, [M(), M("CUBISM_MASK_ON", "CUBISM_INVERT_ON"), M("_OTHER")]));
        Assert.Throws<InvalidDataException>(() => ShaderDump.Files(dumps, [new PyObject { ["shader"] = new PyObject { ["shader"] = "T" }, ["keywords"] = new List<object?>() }]));
    }

    [Fact]
    public void PublishesModelsNextToCharts()
    {
        using var dir = new TempDirectory(); var root = Path.Combine(dir.Path, "site"); var work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(Path.Combine(work, "textures"));
        var prefab = PyJson.Minified(new PyObject { ["key"] = "k", ["nodes"] = new List<object?> { new string('n', 600 * 1024) }, ["canvas"] = new PyObject() });
        File.WriteAllBytes(Path.Combine(work, "m.prefab.json"), prefab);
        File.WriteAllBytes(Path.Combine(work, "model.json"), "{\"format\":1}"u8.ToArray());
        File.WriteAllBytes(Path.Combine(work, "textures", "t.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(work, Live2DModel.SummaryFile), PyJson.Minified(new PyObject
        {
            ["canvas"] = new PyObject { ["width"] = 6000.0 }, ["textures"] = new List<object?> { "textures/t.png" }, ["nodes"] = 3L,
        }));
        var key = "Character/Live2D/003_adv/m/model/m";
        var names = new JsonObject { ["character"] = 3, ["names"] = new JsonObject { ["ja"] = "x" }, ["label"] = "x" };
        var result = ModelSite.Ingest(root, "m", key, work, ["model.json", "m.prefab.json", "textures/t.png"], names, "inputs-1");
        Assert.Equal(3, (int)result["files"]!);
        Directory.CreateDirectory(Path.Combine(root, "assets")); File.WriteAllBytes(Path.Combine(root, "assets", "0000.json"), [0]);
        var index = ChartSite.WriteIndex(root);
        Assert.Equal((0, 1, 1), ((int)index["charts"]!, (int)index["models"]!, (int)index["removedAssets"]!));   // the orphan only
        var models = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "models.json")))!["models"]!.AsArray();
        var item = Assert.Single(models)!;
        Assert.Equal(("m", "models/m.json", "003_adv", 3, "x", 1), ((string)item["id"]!, (string)item["manifest"]!, (string)item["group"]!, (int)item["nodes"]!, (string)item["label"]!, (int)item["textures"]!));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "models", "m.json")))!;
        var parts = manifest["files"]!["m.prefab.json"]!["parts"]!.AsArray();
        Assert.Equal(new[] { "key", "nodes", "canvas" }, parts.Select(p => (string)p![0]!));
        Assert.All(parts, p => Assert.True(File.Exists(Path.Combine(root, (string)p![1]!))));
        Assert.Equal("6000.0", manifest["model"]!["canvas"]!["width"]!.ToJsonString());
        Assert.False(ModelSite.NeedsBuild(root, "m", false, "inputs-1"));
        Assert.True(ModelSite.NeedsBuild(root, "m", false, "inputs-2"));
        Assert.True(ModelSite.NeedsBuild(root, "m", true, "inputs-1"));
        Assert.False(ModelSite.RefreshNames(root, "m", names));
        Assert.True(ModelSite.RefreshNames(root, "m", null));
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "models", "m.json")))!["model"]!["label"]);
    }

    /// With MOENOTES_MODEL_ORACLE=<an nnnotes `web --live2d` site> and MOENOTES_MODEL_SITE=<a site built here>: every model of
    /// the oracle stores the same files, and the same assets apart from PNG encodings.
    [Fact]
    public void MatchesTheReferenceSite()
    {
        var oracle = Environment.GetEnvironmentVariable("MOENOTES_MODEL_ORACLE"); var site = Environment.GetEnvironmentVariable("MOENOTES_MODEL_SITE");
        if (string.IsNullOrEmpty(oracle) || string.IsNullOrEmpty(site)) return;
        var failures = new List<string>(); var count = 0;
        foreach (var path in Directory.GetFiles(Path.Combine(oracle, "models"), "*.json").Order(StringComparer.Ordinal))
        {
            count++;
            var expected = JsonNode.Parse(File.ReadAllText(path))!["files"]!.AsObject();
            var ours = Path.Combine(site, "models", Path.GetFileName(path));
            if (!File.Exists(ours)) { failures.Add($"{Path.GetFileName(path)}: missing"); continue; }
            var actual = JsonNode.Parse(File.ReadAllText(ours))!["files"]!.AsObject();
            if (!expected.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(actual.Select(p => p.Key).Order(StringComparer.Ordinal))) { failures.Add($"{Path.GetFileName(path)}: other files"); continue; }
            foreach (var (file, entry) in expected)
                if (!file.EndsWith(".png", StringComparison.Ordinal) && entry!.ToJsonString() != actual[file]!.ToJsonString()) failures.Add($"{Path.GetFileName(path)}: {file}");
        }
        Assert.True(count > 0, "no models in the oracle site");
        Assert.True(failures.Count == 0, $"{failures.Count} differences in {count} models:\n" + string.Join("\n", failures.Take(20)));
    }
}
