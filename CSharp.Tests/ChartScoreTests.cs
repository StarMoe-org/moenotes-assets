using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace MoenotesAssets.Tests;

public sealed class ChartScoreTests
{
    // A chart that exercises every construct the shipped charts use: taps, crit taps, flicks (left/right), traces,
    // long chains with in/out/linear and split eases, auto and hidden nodes, invisible ends, guides merged with
    // singles, simultaneous notes, BPM and meter changes, fever, skill and call events.
    const string Synthetic = """
    {"meta":{"version":100},"score":{
      "events":{"bpm":[{"t":0,"bpm":180.0},{"t":7680,"bpm":150.5}],"sig":[{"t":0,"sig":[4,4]},{"t":11520,"sig":[3,4]}],
        "skill":[1920,5760],"fever":[[3840,9600]],"call":[{"t":0,"timing":[0,1,0,1]},{"t":1920,"timing":[1,1,1]}]},
      "notes":[
        {"t":1920,"pos":0,"size":6},{"t":1920,"pos":18,"size":6,"crit":true},
        {"type":"flick","t":2400,"pos":6,"size":4,"dir":"left"},{"type":"flick","t":2880,"pos":12,"size":4,"dir":"right"},
        {"type":"trace","t":3360,"pos":9.5,"size":3.5},
        {"type":"long","node":[{"t":3840,"pos":2,"size":6,"ease":"out"},{"t":4320,"pos":"auto","size":6},
          {"t":4800,"pos":8,"size":8,"ease":["out","in"],"visible":false},{"t":5280,"pos":12,"size":6,"ease":"in"},
          {"type":"flick","t":5760,"pos":16,"size":6,"dir":"right"}]},
        {"type":"long","node":[{"t":4080,"pos":14,"size":4,"crit":true},{"t":4560,"pos":"auto","size":4},
          {"type":"trace","t":5040,"pos":10,"size":4},{"t":6000,"pos":4,"size":6,"visible":false}]},
        {"type":"long","node":[{"type":"trace","t":6720,"pos":0,"size":8,"visible":false},{"type":"trace","t":7680,"pos":6,"size":8}]},
        {"t":8640,"pos":3,"size":6},
        {"type":"guide","node":[{"t":8640,"pos":3,"size":6,"ease":"in"},{"t":9120,"pos":"auto"},{"t":9600,"pos":15,"size":6}]},
        {"type":"flick","t":10080,"pos":9,"size":6},
        {"type":"guide","node":[{"t":10080,"pos":9,"size":6},{"type":"trace","t":11040,"pos":1,"size":6}]},
        {"type":"long","node":[{"t":11520,"pos":5,"size":6.5,"ease":"linear"},{"t":12480,"pos":11,"size":6.5},
          {"t":13440,"pos":11,"size":6.5}]},
        {"t":12480,"pos":0,"size":4},{"type":"long","node":[{"t":12480,"pos":20,"size":4},{"t":12960,"pos":20,"size":4}]},
        {"type":"node","t":13000,"pos":0,"size":1},{"type":"long","node":[{"t":14000,"pos":0,"size":1}]}
      ]}}
    """;

    [Fact]
    public void ConvertsTheSyntheticChartLikeTheReference()
    {
        var actual = ChartScore.Convert(System.Text.Encoding.UTF8.GetBytes(Synthetic));
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "synthetic.notes.json")))!;
        AssertSame(expected, actual, "$");
    }

    [Fact]
    public void ReadsGzipAndRejectsCharts()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(System.Text.Encoding.UTF8.GetBytes(Synthetic));
        Assert.Equal(ChartScore.Convert(System.Text.Encoding.UTF8.GetBytes(Synthetic)).ToJsonString(), ChartScore.Convert(buffer.ToArray()).ToJsonString());
        Assert.Throws<InvalidDataException>(() => ChartScore.Convert("{\"meta\":{}}"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => ChartScore.Convert("{\"score\":{\"notes\":[{\"type\":\"slide\"}]}}"u8.ToArray()));
    }

    [Theory]
    [InlineData(0.5, "0.5"), InlineData(6.0, "6.0"), InlineData(1e-5, "1e-05"), InlineData(0.0001, "0.0001"), InlineData(1e16, "1e+16"),
     InlineData(123456789012345.0, "123456789012345.0"), InlineData(0.30000001192092896, "0.30000001192092896"), InlineData(-2.5e-7, "-2.5e-07")]
    public void FormatsFloatsLikePython(double value, string expected) => Assert.Equal(expected, PyFloat.Repr(value));

    /// With MOENOTES_CHART_ORACLE=<dir> (raw/<name>.json charts, expected/<name>.json from nnnotes score.convert):
    /// every chart converts to the reference document.
    [Fact]
    public void MatchesTheReferenceOnRealCharts()
    {
        var root = Environment.GetEnvironmentVariable("MOENOTES_CHART_ORACLE");
        if (string.IsNullOrEmpty(root)) return;
        var failures = new List<string>(); var count = 0;
        foreach (var raw in Directory.GetFiles(Path.Combine(root, "raw"), "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(raw); count++;
            try { AssertSame(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "expected", name)))!, ChartScore.Convert(File.ReadAllBytes(raw)), "$"); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        }
        Assert.True(count > 0, "no charts in the oracle directory");
        Assert.True(failures.Count == 0, $"{failures.Count}/{count} charts differ:\n" + string.Join("\n", failures.Take(20)));
    }

    static void AssertSame(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null) { if (expected is not null || actual is not null) Fail(path, expected, actual); return; }
        switch (expected)
        {
            case JsonObject e:
                if (actual is not JsonObject a) { Fail(path, expected, actual); return; }
                var ek = e.Select(p => p.Key).ToList(); var ak = a.Select(p => p.Key).ToList();
                if (!ek.SequenceEqual(ak)) throw new Xunit.Sdk.XunitException($"{path}: keys [{string.Join(",", ek)}] != [{string.Join(",", ak)}]");
                foreach (var k in ek) AssertSame(e[k], a[k], $"{path}.{k}");
                return;
            case JsonArray e:
                if (actual is not JsonArray list || list.Count != e.Count) { Fail(path, $"array[{e.Count}]", actual is JsonArray x ? $"array[{x.Count}]" : actual); return; }
                for (int i = 0; i < e.Count; i++) AssertSame(e[i], list[i], $"{path}[{i}]");
                return;
        }
        var ev = expected.AsValue(); var av = actual as JsonValue;
        if (av is null) { Fail(path, expected, actual); return; }
        var ekind = ev.GetValueKind(); var akind = av.GetValueKind();
        if (ekind == JsonValueKind.Number && akind == JsonValueKind.Number)
        {
            if (ev.GetValue<double>().Equals(ToDouble(av))) return;
            Fail(path, expected, actual); return;
        }
        if (ekind != akind || expected.ToJsonString() != actual.ToJsonString()) Fail(path, expected, actual);
    }
    static double ToDouble(JsonValue v) => v.TryGetValue<double>(out var d) ? d : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
    static void Fail(string path, object? expected, object? actual) =>
        throw new Xunit.Sdk.XunitException($"{path}: expected {(expected as JsonNode)?.ToJsonString() ?? expected} got {(actual as JsonNode)?.ToJsonString() ?? actual}");
}
