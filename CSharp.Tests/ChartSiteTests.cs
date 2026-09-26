using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace MoenotesAssets.Tests;

public sealed class ChartSiteTests
{
    static byte[] Command(ushort code, params byte[] payload) => [(byte)(code >> 8), (byte)code, (byte)payload.Length, .. payload];
    static byte[] Be(float value) { var b = BitConverter.GetBytes(value); Array.Reverse(b); return b; }

    /// A live cue sheet: one cue, a sequence of one track that notes on one synth holding memory waveform 3.
    public static byte[] SyntheticAcb() => CriFixture.Utf(("AcbVolume", 0.75f),
        ("CueTable", CriFixture.Utf(("CueId", 100), ("ReferenceType", 3), ("ReferenceIndex", 0), ("Length", 1000))),
        ("CueNameTable", CriFixture.Utf(("CueIndex", 0), ("CueName", "bgm_song"))),
        ("SequenceTable", CriFixture.Utf(("Type", 0), ("CommandIndex", 0), ("TrackIndex", new byte[] { 0, 0 }))),
        ("SeqCommandTable", CriFixture.Utf(("Command", (byte[])[.. Command(65, 0, 0, 0, 7), .. Command(146, Be(0.5f)), .. Command(999, 0xab, 0xcd)]))),
        ("TrackTable", CriFixture.Utf(("CommandIndex", 0), ("EventIndex", 0))),
        ("TrackCommandTable", CriFixture.Utf(("Command", (byte[])[.. Command(111, 0, 0, 0x13, 0x88), .. Command(146, Be(0.8f))]))),
        ("TrackEventTable", CriFixture.Utf(("Command", (byte[])[.. Command(2000, 0, 2, 0, 0), .. Command(0)]))),
        ("SynthTable", CriFixture.Utf(("Type", 0), ("CommandIndex", 0), ("ReferenceItems", new byte[] { 0, 1, 0, 0 }))),
        ("SynthCommandTable", CriFixture.Utf(("Command", Command(146, Be(0.5f))))),
        ("WaveformTable", CriFixture.Utf(("MemoryAwbId", 3), ("NumSamples", 48000), ("LoopFlag", 0), ("Streaming", 0))),
        ("AcfReferenceTable", CriFixture.Utf(("Type", 3), ("Id", 7), ("Name", "Music"))),
        ("StringValueTable", CriFixture.Utf(("StringValue", "bus_a"))));

    [Fact]
    public void ReadsLiveCueSheets()
    {
        var cue = Assert.Single(AcbCues.Parse(SyntheticAcb())).Value;
        Assert.Equal(("bgm_song", 100L, 1000L), (cue.Name, cue.CueId, cue.LengthMs));
        Assert.Equal(new[] { "Music" }, cue.Categories);
        Assert.Equal((0.5, 0.75), (cue.Volume, cue.AcbVolume));
        Assert.Equal("abcd", cue.SequenceOther["999"]);
        var layer = Assert.Single(cue.Layers);
        Assert.Equal((3L, 48000L, 0L), (layer.AwbId, layer.Samples, layer.LoopFlag));
        Assert.Equal((double)0.8f * 0.5f, layer.Volume);
        Assert.Equal(0.5, layer.BusSends["bus_a"]);
        var streamed = CriFixture.Utf(("CueTable", CriFixture.Utf(("CueId", 1), ("ReferenceType", 1), ("ReferenceIndex", 0))), ("CueNameTable", CriFixture.Utf(("CueIndex", 0), ("CueName", "x"))));
        Assert.Contains("only sequences", Assert.Throws<InvalidDataException>(() => AcbCues.Parse(streamed)).Message);
    }

    /// With MOENOTES_NNNOTES=<nnnotes checkout>/src and Python on PATH: the cue data equals nnnotes liveaudio.acb_cues.
    [Fact]
    public async Task ReadsCueSheetsLikeTheReference()
    {
        var source = Environment.GetEnvironmentVariable("MOENOTES_NNNOTES");
        if (string.IsNullOrEmpty(source)) return;
        using var dir = new TempDirectory(); var path = Path.Combine(dir.Path, "sheet.acb");
        await File.WriteAllBytesAsync(path, SyntheticAcb());
        var script = "import json,sys; sys.path.insert(0, sys.argv[1]); from nnnotes import liveaudio; print(json.dumps(liveaudio.acb_cues(open(sys.argv[2],'rb').read())))";
        var reference = JsonNode.Parse(await Processes.Run("python", ["-c", script, source, path], CancellationToken.None))!["bgm_song"]!;
        var cue = AcbCues.Parse(SyntheticAcb())["bgm_song"];
        Assert.Equal(((long)reference["cueId"]!, (long)reference["lengthMs"]!, (double)reference["volume"]!, (double)reference["acbVolume"]!), (cue.CueId, cue.LengthMs, cue.Volume, cue.AcbVolume));
        Assert.Equal(reference["categories"]!.AsArray().Select(c => (string)c!).ToArray(), cue.Categories);
        Assert.Equal((string)reference["sequenceOther"]!["999"]!, cue.SequenceOther["999"]);
        var layer = reference["layers"]![0]!;
        Assert.Equal(((long)layer["awbId"]!, (long)layer["samples"]!, (long)layer["loopFlag"]!, (double)layer["volume"]!, (double)layer["busSends"]!["bus_a"]!),
            (cue.Layers[0].AwbId, cue.Layers[0].Samples, cue.Layers[0].LoopFlag, cue.Layers[0].Volume, cue.Layers[0].BusSends["bus_a"]));
    }

    [Fact]
    public void ReadsTheAacEditList()
    {
        static byte[] Box(string kind, byte[] body) { var b = new byte[8 + body.Length]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length); Encoding.ASCII.GetBytes(kind).CopyTo(b, 4); body.CopyTo(b, 8); return b; }
        var elst = new byte[20]; elst[7] = 1; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(elst.AsSpan(12), 1024);
        var mp4 = (byte[])[.. Box("ftyp", new byte[8]), .. Box("moov", [.. Box("mvhd", new byte[4]), .. Box("trak", [.. Box("tkhd", new byte[4]), .. Box("edts", Box("elst", elst))])])];
        Assert.Equal(1024, ChartSite.Mp4Priming(mp4));
        Assert.Equal(0, ChartSite.Mp4Priming(Box("moov", Box("trak", new byte[4]))));
    }

    /// Two nnnotes-style base charts of band 1 with a split scene: a song of band 1 takes the first as its template,
    /// the static files of both, their merged shader indexes, and its own score, BGM, sound entry and jacket.
    [Fact]
    public void ComposesChartsFromTheStaticBase()
    {
        using var dir = new TempDirectory();
        var baseDir = Path.Combine(dir.Path, "base");
        WriteBase(baseDir);
        var site = new ChartSite(Path.Combine(dir.Path, "site"), baseDir, (_, _) => false);
        Assert.False(site.IsPackage);
        Assert.Equal(new[] { "100001_expert", "100002_easy" }, site.ImportBase());
        Assert.True(site.IsBase("100001_expert"));
        Assert.False(site.NeedsBuild("100001_expert", true));

        var png = SamplePng;
        var manifest = site.Build(SampleInput());
        var files = manifest["files"]!.AsObject();

        Assert.Equal(new[] { "audio/BGM_song/bgm_song.m4a", "audio/SE/tap.flac", "audio/live-audio.json", "live.json", "livenotes/notes.json",
                      "livenotes/shaders/a.glsl", "livenotes/shaders/b.glsl", "livenotes/shaders/shaders.json", "livescene/scene.json",
                      "livescene/textures/bg.png", "livescene/textures/extra.png", $"livescene/textures/jkt_009-{Hash(png)[..8]}.png", "score/0009_02.notes.json" },
                     files.Select(f => f.Key).ToArray());
        Assert.Equal(("moenotes-assets", "100002_easy", 2), ((string)manifest["builder"]!, (string)manifest["template"]!, (int)manifest["chart"]!["notes"]!));
        Assert.Equal(1000, (long)manifest["chart"]!["durationMs"]!);

        var la = Json(site, files["audio/live-audio.json"]!);
        Assert.Equal(new[] { "2001", "5009" }, la["sounds"]!.AsObject().Select(s => s.Key).Order().ToArray());
        Assert.Equal(5009, (long)la["music"]!["soundId"]!);
        var layer = la["sounds"]!["5009"]!["layers"]![0]!;
        Assert.Equal(("audio/BGM_song/bgm_song.m4a", 48000L, 1024L, 3L), ((string)layer["file"]!, (long)layer["samples"]!, (long)layer["encoderDelay"]!, (long)layer["awbId"]!));
        Assert.Equal(new[] { "Music" }, la["sounds"]!["5009"]!["categories"]!.AsArray().Select(c => (string)c!).ToArray());
        Assert.Equal(new[] { "SE", "BGM_song" }, la["decoded"]!.AsObject().Select(d => d.Key).ToArray());

        var scene = files["livescene/scene.json"]!.AsObject(); var parts = scene["parts"]!.AsArray();
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(baseDir, "charts", "100002_easy.json")))!["files"]!["livescene/scene.json"]!["parts"]!.AsArray();
        Assert.Equal((string)template[0]![1]!, (string)parts[0]![1]!);          // the shared stage part keeps its asset
        var rebuilt = Json(site, scene);
        Assert.Equal((long)scene["size"]!, Encoding.UTF8.GetByteCount(RawJoin(site, scene)));
        Assert.Equal($"textures/jkt_009-{Hash(png)[..8]}.png", (string)rebuilt["sprites"]!["jacket"]!["texture"]!["texture"]!);
        Assert.Equal("stage", (string)rebuilt["sprites"]!["lightweightBackground"]!["texture"]!["texture"]!);
        Assert.Equal(100009, (int)rebuilt["slice"]!["musicId"]!);
        Assert.Equal("jkt_009_0", (string)rebuilt["sprites"]!["jacket"]!["sprite"]!);

        var shaders = Json(site, files["livenotes/shaders/shaders.json"]!).AsArray();
        Assert.Equal(new[] { "b.glsl", "a.glsl" }, shaders[0]!["variants"]!.AsArray().Select(v => (string)v!["file"]!).ToArray());
        var live = Json(site, files["live.json"]!);
        Assert.Equal(("score/0009_02.notes.json", "livescene/scene.json"), ((string)live["notes"]!, (string)live["scene"]!));
        Assert.Equal(2, Json(site, files["score/0009_02.notes.json"]!)["judgementNoteCount"]!.GetValue<int>());

        File.WriteAllText(Path.Combine(site.Root, "assets", "stray.json"), "{}");
        var index = site.WriteIndex();
        Assert.Equal((3, 1), ((int)index["charts"]!, (int)index["removedAssets"]!));
        var charts = JsonNode.Parse(File.ReadAllText(Path.Combine(site.Root, "charts.json")))!["charts"]!.AsArray();
        Assert.Equal(new[] { "100001_expert", "100002_easy", "100009_hard" }, charts.Select(c => (string)c!["id"]!).ToArray());
        Assert.False(site.IsBase("100009_hard"));
    }

    static string Hash(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
    static readonly byte[] SamplePng = [0x89, .. "PNG\r\n\u001a\n"u8, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 1, 0, 0, 0, 1, 0];
    static ChartSite.ChartInput SampleInput() => new(100009, "hard", 1,
        Encoding.UTF8.GetBytes("""{"score":{"events":{"bpm":[{"t":0,"bpm":120}]},"notes":[{"t":1920,"pos":0,"size":6},{"t":3840,"pos":6,"size":6}]}}"""),
        new() { ["key"] = "Live/MusicScore/0009/0009_02", ["musicId"] = 100009, ["difficulty"] = "hard" },
        new() { ["title"] = "Song", ["stageBand"] = 1, ["level"] = 20 }, new() { ["_id"] = 100009 },
        "BGM_song", "bgm_song", new() { ["_id"] = 5009, ["_category"] = 1 }, AcbCues.Parse(SyntheticAcb())["bgm_song"],
        [new("m4a-bytes"u8.ToArray(), 48004, 48000, 2, 1024)], "jkt_009", SamplePng, 256, 256, "jkt_009_0", ChartSite.SplitAcbLayout);
    static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// The static package: the site's manifests as templates and every asset but the songs' notes, BGM and jacket; a
    /// deterministic zip whose entries and asset hashes are checked on extraction, and from which charts compose.
    [Fact]
    public void PacksTheBaseAndComposesFromThePackage()
    {
        using var dir = new TempDirectory();
        var nnnotes = Path.Combine(dir.Path, "nnnotes"); WriteBase(nnnotes);
        var zip = Path.Combine(dir.Path, "base.zip");
        var info = ChartSite.PackBase(nnnotes, zip, "synthetic");
        Assert.Equal((2, "synthetic"), ((int)info["templates"]!, (string)info["source"]!));
        string Asset(string path) => (string)JsonNode.Parse(File.ReadAllText(Path.Combine(nnnotes, "charts", "100001_expert.json")))!["files"]![path]!["asset"]!;
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(new[] { "base.json", "README.txt", "templates/100001_expert.json", "templates/100002_easy.json" }, names.Take(4).ToArray());
            foreach (var song in new[] { "audio/BGM_100001/bgm_100001.m4a", "score/x.notes.json", "livescene/textures/jkt_001.png" }) Assert.DoesNotContain(Asset(song), names);
            foreach (var kept in new[] { "audio/SE/tap.flac", "audio/live-audio.json", "live.json", "livenotes/notes.json", "livescene/textures/bg.png" }) Assert.Contains(Asset(kept), names);
        }
        var again = Path.Combine(dir.Path, "again.zip"); ChartSite.PackBase(nnnotes, again, "synthetic");
        Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(again));

        var package = Path.Combine(dir.Path, "package"); ChartSite.ExtractBase(zip, package, 1 << 30);
        var site = new ChartSite(Path.Combine(dir.Path, "site"), package, (_, _) => false);
        Assert.True(site.IsPackage);
        Assert.Empty(site.ImportBase());
        Assert.True(site.NeedsBuild("100001_expert", false));
        var manifest = site.Build(SampleInput() with { Layers = [new("m4a-bytes"u8.ToArray(), 47990, 48000, 2, 1024)] });
        foreach (var (_, entry) in manifest["files"]!.AsObject())
            foreach (var asset in entry!["parts"] is JsonArray parts ? parts.Select(p => (string)p![1]!) : [(string)entry["asset"]!])
                Assert.True(File.Exists(Path.Combine(site.Root, asset)), asset);
        // a decode shorter than the ACB's waveform declares its own length
        Assert.Equal(47990, (long)Json(site, manifest["files"]!["audio/live-audio.json"]!)["sounds"]!["5009"]!["layers"]![0]!["samples"]!);
        Assert.Equal(999, (long)manifest["chart"]!["durationMs"]!);
        Assert.Equal(2, Json(site, manifest["files"]!["score/0009_02.notes.json"]!)["judgementNoteCount"]!.GetValue<int>());
        Assert.Equal(1, (int)site.WriteIndex()["charts"]!);
        Assert.False(site.NeedsBuild("100009_hard", false));
        Assert.True(site.NeedsBuild("100009_hard", true));

        void Bad(string name, string text)
        {
            var path = Path.Combine(dir.Path, "bad.zip"); File.Delete(path);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (entryName, data) in new[] { ("base.json", "{\"format\":1,\"siteFormat\":2}"), (name, text) })
                    using (var stream = archive.CreateEntry(entryName).Open()) stream.Write(Encoding.UTF8.GetBytes(data));
            Assert.Throws<InvalidDataException>(() => ChartSite.ExtractBase(path, Path.Combine(dir.Path, "bad-" + Guid.NewGuid().ToString("N")), 1 << 30));
        }
        Bad($"assets/{new string('0', 64)}.json", "{}");
        Bad("../escape.json", "{}");
        Bad("templates/sub/x.json", "{}");
    }

    /// chart_base_url: downloaded once (redirects followed), checked against chart_base_sha256, kept in data_dir.
    [Fact]
    public async Task DownloadsTheStaticBaseOnce()
    {
        using var dir = new TempDirectory();
        var nnnotes = Path.Combine(dir.Path, "nnnotes"); WriteBase(nnnotes);
        var zip = Path.Combine(dir.Path, "base.zip"); ChartSite.PackBase(nnnotes, zip, "synthetic");
        var digest = Hash(File.ReadAllBytes(zip)); var requests = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var host = builder.Build();
        host.MapGet("/releases/base.zip", () => Results.Redirect("/objects/base.zip"));
        host.MapGet("/objects/base.zip", () => { Interlocked.Increment(ref requests); return Results.Bytes(File.ReadAllBytes(zip), "application/zip"); });
        await host.StartAsync();
        var config = new Config
        {
            DataDir = Path.Combine(dir.Path, "data"),
            CdnRoot = "https://cdn.example.invalid/prod",
            AllowLoopbackHttp = true,
            ChartBaseUrl = Address(host) + "/releases/base.zip",
            ChartBaseSha256 = digest,
        };
        await using (var service = new AssetService(config))
        {
            var installed = await service.EnsureChartBase(CancellationToken.None);
            Assert.Equal(Path.Combine(service.ChartBaseRoot, digest), installed);
            Assert.True(File.Exists(Path.Combine(installed, "templates", "100001_expert.json")));
            Assert.Equal(installed, await service.EnsureChartBase(CancellationToken.None));
            Assert.Equal(1, requests);
        }
        await using (var service = new AssetService(config with { ChartBaseSha256 = new string('a', 64) }))
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureChartBase(CancellationToken.None));
            Assert.Contains("does not match", error.Message);
            Assert.True(Directory.Exists(Path.Combine(service.ChartBaseRoot, digest)));
        }
        Assert.Throws<InvalidDataException>(() => (config with { ChartBaseSha256 = "" }).Validate());
        Assert.Throws<InvalidDataException>(() => (config with { AllowLoopbackHttp = false }).Validate());
    }

    /// The site is served read-only under /chart-site/: immutable assets, short-lived manifests, CORS, dot files hidden.
    [Fact]
    public async Task ServesTheSite()
    {
        using var dir = new TempDirectory();
        await using var service = new AssetService(new Config { DataDir = dir.Path, CdnRoot = "https://cdn.example.invalid/prod", CorsOrigins = ["*"] });
        Directory.CreateDirectory(Path.Combine(service.ChartSiteRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(service.ChartSiteRoot, "charts.json"), "{\"format\":2,\"charts\":[]}");
        await File.WriteAllTextAsync(Path.Combine(service.ChartSiteRoot, "assets", "ab.glsl"), "void main(){}");
        await File.WriteAllTextAsync(Path.Combine(service.ChartSiteRoot, "assets", ".x.part"), "partial");
        await using var app = Api.Build(service, "http://127.0.0.1:0", apiKey: "key"); await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/chart-site/assets/ab.glsl"); request.Headers.Add("Origin", "https://example.org");
        var asset = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, asset.StatusCode);
        Assert.True(asset.Headers.CacheControl!.Public && asset.Headers.CacheControl.MaxAge == TimeSpan.FromDays(365) && asset.Headers.CacheControl.Extensions.Any(e => e.Name == "immutable"));
        Assert.Equal("text/plain", asset.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", asset.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var index = await http.GetAsync("/chart-site/charts.json");
        Assert.Equal(TimeSpan.FromSeconds(60), index.Headers.CacheControl!.MaxAge);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/chart-site/assets/.x.part")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.PostAsync("/chart-site/build", null)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", "key");
        var build = await http.PostAsync("/chart-site/build", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, build.StatusCode);
        Assert.Contains("chart_base", await build.Content.ReadAsStringAsync());
    }
    static string RawJoin(ChartSite site, JsonObject entry) => "{" + string.Join(",", entry["parts"]!.AsArray().Select(p =>
        $"\"{(string)p![0]!}\":" + File.ReadAllText(Path.Combine(site.Root, (string)p[1]!)))) + "}";
    static JsonNode Json(ChartSite site, JsonNode entry) => JsonNode.Parse(entry["parts"] is null
        ? File.ReadAllText(Path.Combine(site.Root, (string)entry["asset"]!)) : RawJoin(site, entry.AsObject()))!;

    /// Writes an nnnotes-style site: content-addressed assets, split scene.json, filtered shader indexes.
    static void WriteBase(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "assets")); Directory.CreateDirectory(Path.Combine(root, "charts"));
        JsonObject Put(string path, byte[] data)
        {
            var name = $"{Hash(data)}{Path.GetExtension(path).ToLowerInvariant()}"; File.WriteAllBytes(Path.Combine(root, "assets", name), data);
            return new() { ["asset"] = "assets/" + name, ["size"] = data.Length };
        }
        JsonObject PutJson(string path, JsonNode node) => Put(path, Encoding.UTF8.GetBytes(node.ToJsonString()));
        JsonObject Split(string path, JsonObject node)
        {
            var parts = new JsonArray(); var texts = new List<string>();
            foreach (var (key, value) in node) { var text = value!.ToJsonString(); texts.Add($"\"{key}\":{text}"); parts.Add(new JsonArray(key, (string)Put($"{path}#{key}.json", Encoding.UTF8.GetBytes(text))["asset"]!, text.Length)); }
            return new() { ["size"] = Encoding.UTF8.GetByteCount("{" + string.Join(",", texts) + "}"), ["parts"] = parts };
        }
        void Chart(int musicId, string difficulty, string jacket, string[] variants, bool extra)
        {
            var bgm = $"audio/BGM_{musicId}/bgm_{musicId}.m4a";
            var files = new JsonObject
            {
                ["live.json"] = PutJson("live.json", new JsonObject { ["musicId"] = musicId, ["difficulty"] = difficulty, ["notes"] = "score/x.notes.json", ["scene"] = "livescene/scene.json", ["noteAssets"] = "livenotes/notes.json", ["liveAudio"] = "audio/live-audio.json" }),
                ["score/x.notes.json"] = PutJson("score/x.notes.json", new JsonObject { ["notes"] = new JsonArray() }),
                ["audio/live-audio.json"] = PutJson("audio/live-audio.json", new JsonObject
                {
                    ["musicId"] = musicId,
                    ["sounds"] = new JsonObject
                    {
                        ["1001"] = new JsonObject { ["sheet"] = $"BGM_{musicId}", ["layers"] = new JsonArray(new JsonObject { ["file"] = bgm }) },
                        ["2001"] = new JsonObject { ["sheet"] = "SE", ["layers"] = new JsonArray(new JsonObject { ["file"] = "audio/SE/tap.flac" }) },
                    },
                    ["music"] = new JsonObject { ["soundId"] = 1001 },
                    ["decoded"] = new JsonObject { [$"BGM_{musicId}"] = new JsonObject(), ["SE"] = new JsonObject() },
                }),
                [bgm] = Put(bgm, Encoding.UTF8.GetBytes($"bgm {musicId}")),
                ["audio/SE/tap.flac"] = Put("tap.flac", "tap"u8.ToArray()),
                ["livescene/scene.json"] = Split("livescene/scene.json", new JsonObject
                {
                    ["scene"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["path"] = new string('n', 600_000) }) },
                    ["sprites"] = new JsonObject
                    {
                        ["lightweightBackground"] = new JsonObject { ["texture"] = new JsonObject { ["texture"] = "stage" } },
                        ["jacket"] = new JsonObject { ["key"] = $"Image/Jacket/{jacket}", ["texture"] = new JsonObject { ["texture"] = $"textures/{jacket}.png", ["width"] = 512, ["height"] = 512, ["mipCount"] = 1 } },
                    },
                    ["slice"] = new JsonObject { ["musicId"] = musicId, ["band"] = 1 },
                    ["master"] = new JsonObject { ["liveMusic"] = new JsonObject { ["_id"] = musicId } },
                }),
                [$"livescene/textures/{jacket}.png"] = Put("j.png", Encoding.UTF8.GetBytes(jacket)),
                ["livescene/textures/bg.png"] = Put("bg.png", "bg"u8.ToArray()),
                ["livenotes/notes.json"] = PutJson("notes.json", new JsonObject { ["settings"] = new JsonObject() }),
                ["livenotes/shaders/shaders.json"] = PutJson("shaders.json", new JsonArray(new JsonObject { ["name"] = "Note", ["parsed"] = "Note.json", ["variants"] = new JsonArray([.. variants.Select(v => (JsonNode)new JsonObject { ["file"] = v })]) })),
            };
            foreach (var v in variants) files[$"livenotes/shaders/{v}"] = Put(v, Encoding.UTF8.GetBytes(v));
            if (extra) files["livescene/textures/extra.png"] = Put("extra.png", "extra"u8.ToArray());
            var manifest = new JsonObject { ["format"] = 2, ["musicId"] = musicId, ["difficulty"] = difficulty, ["quality"] = 1, ["chart"] = new JsonObject { ["stageBand"] = 1 }, ["files"] = files };
            File.WriteAllText(Path.Combine(root, "charts", $"{musicId}_{difficulty}.json"), manifest.ToJsonString());
        }
        Chart(100001, "expert", "jkt_001", ["a.glsl"], false);
        Chart(100002, "easy", "jkt_002", ["b.glsl"], true);
    }
}
