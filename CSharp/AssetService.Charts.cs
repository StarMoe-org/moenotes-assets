using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public sealed record ChartSiteRequest(int[]? Music = null, bool Force = false, string? Snapshot = null, string? Region = null, string? Locale = null);

// The chart site task: take the static base (a package downloaded from chart_base_url, or the chart_base directory),
// then compose every chart of MasterLiveMusic the site lacks from the song's own exports (Live/MusicScore chart,
// Cri/Sound BGM and its ACB, Image/Jacket) and a template of the same stage band. Served read-only at /chart-site/.
public sealed partial class AssetService
{
    private readonly SemaphoreSlim chartGate = new(1);
    public string ChartSiteRoot => Path.Combine(Config.DataDir, "chart-site");
    public string ChartBaseRoot => Path.Combine(Config.DataDir, "chart-base");
    static readonly string[] MasterTables = ["MasterLiveMusic", "MasterLiveMusicScore", "MasterSound", "MasterSoundCueSheet", "MasterCharacter", "MasterBand", "MasterText"];

    public TaskInfo StartChartSite(ChartSiteRequest request)
    {
        Require(Config.ChartBaseUrl.Length > 0 || (Config.ChartBase.Length > 0 && Directory.Exists(Config.ChartBase)), "chart_base_url or chart_base is not configured (or chart_base is missing)");
        Require(Config.MasterRoot.Length > 0, "master_root is not configured");
        var (snapshot, catalog) = GetSnapshot(request.Snapshot, request.Region, request.Locale);
        return Start("chart_site", snapshot.Id, 0, async (task, token) =>
        {
            await chartGate.WaitAsync(token);
            try
            {
                var site = new ChartSite(ChartSiteRoot, await EnsureChartBase(token), HardLink.TryCreate);
                var imported = site.ImportBase();
                Console.Error.WriteLine($"[chart-site] base {site.BaseDir} ({(site.IsPackage ? "static package" : "nnnotes site")}), imported {imported.Length} charts");
                var master = await LoadMaster(token);
                var charts = ChartMaster.Charts(master).Where(c => request.Music == null || request.Music.Contains(c.MusicId))
                    .Where(c => site.NeedsBuild(ChartSite.ChartId(c.MusicId, c.Difficulty), request.Force)).ToList();
                task = task with { Total = charts.Count, Updated = Now }; Store.Put("task", task.Id, task);
                var results = new List<ItemResult>();
                foreach (var song in charts.GroupBy(c => c.MusicId))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var inputs = await SongInputs(snapshot, catalog, master, song.ToList(), token);
                        foreach (var input in inputs)
                        {
                            var id = ChartSite.ChartId(input.MusicId, input.Difficulty);
                            try { site.Build(input); results.Add(new(id, id, null)); }
                            catch (Exception e) when (e is not OperationCanceledException) { results.Add(new(id, null, e.Message)); }
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        results.AddRange(song.Select(c => new ItemResult(ChartSite.ChartId(c.MusicId, c.Difficulty), null, e.Message)));
                    }
                    Console.Error.WriteLine($"[chart-site] music {song.Key}: {results.Count}/{charts.Count} charts, {results.Count(r => r.Error != null)} failed");
                    task = task with { Completed = results.Count, Results = [.. results], Updated = Now }; Store.Put("task", task.Id, task);
                }
                var index = site.WriteIndex();
                Console.Error.WriteLine($"[chart-site] index {index.ToJsonString()}");
                var failed = results.Count(r => r.Error != null);
                return task with { Completed = results.Count, Results = [.. results], State = failed == 0 ? "succeeded" : failed < results.Count ? "partial" : "failed" };
            }
            finally { chartGate.Release(); }
        });
    }

    /// <summary>
    /// The static base directory: with chart_base_url, the package downloaded once, checked against chart_base_sha256
    /// and extracted to data_dir/chart-base/&lt;sha256&gt; (other versions there are removed); else chart_base.
    /// </summary>
    public async Task<string> EnsureChartBase(CancellationToken token)
    {
        if (Config.ChartBaseUrl.Length == 0)
        {
            Require(Config.ChartBase.Length > 0 && Directory.Exists(Config.ChartBase), "chart_base is not configured or missing");
            return Config.ChartBase;
        }
        var digest = Config.ChartBaseSha256; var directory = Path.Combine(ChartBaseRoot, digest);
        if (File.Exists(Path.Combine(directory, "base.json"))) return directory;
        Directory.CreateDirectory(ChartBaseRoot);
        var staging = Path.Combine(ChartBaseRoot, $".{Guid.NewGuid():N}");
        var zip = Path.Combine(Config.DataDir, "tmp", $"chart-base-{Guid.NewGuid():N}.zip");
        try
        {
            Console.Error.WriteLine($"[chart-site] downloading the static base {Config.ChartBaseUrl}");
            var actual = await DownloadFile(Config.ChartBaseUri(), zip, Config.InputBytes, token);
            Require(actual == digest, $"Static base sha256 {actual} does not match chart_base_sha256");
            ChartSite.ExtractBase(zip, staging, Config.ExpandedBytes);
            if (Directory.Exists(directory)) RemoveTree(directory);
            Directory.Move(staging, directory);
            foreach (var other in Directory.GetDirectories(ChartBaseRoot).Where(d => Path.GetFileName(d) != digest)) RemoveTree(other);
            Console.Error.WriteLine($"[chart-site] static base {digest} installed");
            return directory;
        }
        finally { RemoveTree(zip); RemoveTree(staging); }
    }

    /// <summary>A file by HTTPS (redirects followed, no downgrade), size-limited; returns its sha256.</summary>
    private static async Task<string> DownloadFile(Uri uri, string path, long limit, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Require(response.IsSuccessStatusCode, $"Static base download HTTP {(int)response.StatusCode}");
        Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= limit, "Static base size limit");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16]; long received = 0; int n;
        while ((n = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            received += n; Require(received <= limit, "Static base size limit");
            hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<Dictionary<string, JsonArray>> LoadMaster(CancellationToken token)
    {
        var root = Config.MasterUri(); var tables = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var name in MasterTables)
        {
            var bytes = await Fetch(new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + name + ".json"), 256 << 20, token);
            var node = JsonNode.Parse(bytes);
            tables[name] = (node is JsonObject o ? o["_allData"] : node) as JsonArray ?? throw new InvalidDataException($"{name}: no rows");
        }
        return tables;
    }

    /// <summary>What each chart of one song needs, from the song's exports in the snapshot.</summary>
    private async Task<List<ChartSite.ChartInput>> SongInputs(Snapshot snapshot, Catalog catalog, Dictionary<string, JsonArray> master, List<ChartMaster.Chart> charts, CancellationToken token)
    {
        var song = ChartMaster.Song(master, charts[0].MusicId, snapshot.Locale);
        var bgm = await ExportKey(snapshot, catalog, $"Cri/Sound/{song.Sheet}", token);
        var acb = AcbCues.Parse(await ReadCueSheet(snapshot, catalog, $"Cri/Sound/{song.Sheet}", token));
        var cue = acb.TryGetValue(song.Cue, out var c) ? c : throw new InvalidDataException($"{song.Sheet}: cue {song.Cue} missing");
        var layers = await BgmLayers(bgm, acb, cue, token);
        // The key holds the jacket Texture2D (named like the asset) and its Sprite; the stage samples the texture.
        var pngs = (await ExportKey(snapshot, catalog, $"Image/Jacket/{song.Jacket}", token)).Files.Where(f => f.MediaType == "image/png").ToArray();
        var jacket = pngs.FirstOrDefault(f => f.Label == song.Jacket) ?? pngs.FirstOrDefault()
            ?? throw new InvalidDataException($"Image/Jacket/{song.Jacket}: no PNG");
        var sprite = pngs.FirstOrDefault(f => f.Label != jacket.Label)?.Label ?? jacket.Label;
        var png = await ReadBlob(jacket, token);
        var (width, height) = PngSize(png);
        var layout = catalog.Target($"Cri/Sound/{song.Sheet}").ResourceType == Worker.SplitAcbType ? ChartSite.SplitAcbLayout : ChartSite.EmbeddedAcbLayout;
        var inputs = new List<ChartSite.ChartInput>();
        foreach (var chart in charts)
        {
            var key = $"Live/MusicScore/{chart.ScoreFile}";
            var score = (await ExportKey(snapshot, catalog, key, token)).Files.FirstOrDefault(f => f.MediaType == "application/json")
                ?? throw new InvalidDataException($"{key}: no JSON");
            var source = new JsonObject
            {
                ["key"] = key,
                ["musicId"] = chart.MusicId,
                ["difficulty"] = chart.Difficulty,
                ["masterLiveMusicScoreId"] = chart.ScoreId,
                ["fullComboCount"] = chart.FullComboCount,
            };
            var facts = new JsonObject
            {
                ["title"] = song.Title,
                ["bands"] = new JsonArray([.. song.BandNames.Select(n => (JsonNode?)n)]),
                ["bandIds"] = song.Music["_bandIDs"]?.DeepClone(),
                ["stageBand"] = song.Band,
                ["level"] = chart.Level,
                ["displayLevel"] = chart.DisplayLevel,
                ["fullComboCount"] = chart.FullComboCount,
                ["sortOrder"] = song.Music["_sortOrder"]?.DeepClone(),
            };
            inputs.Add(new(chart.MusicId, chart.Difficulty, song.Band, await ReadBlob(score, token), source, facts, song.Music, song.Sheet, song.Cue,
                song.SoundRow, cue, layers, song.Jacket, png, width, height, sprite, layout));
        }
        return inputs;
    }

    /// <summary>The cue's waveforms among the sheet's m4a exports (files follow the AWB ids the sheet's cues reference, ascending).</summary>
    private async Task<ChartSite.BgmLayer[]> BgmLayers(Manifest export, Dictionary<string, AcbCues.Cue> acb, AcbCues.Cue cue, CancellationToken token)
    {
        var audio = export.Files.Where(f => f.MediaType == "audio/mp4").ToArray();
        var ids = acb.Values.SelectMany(c => c.Layers).Select(l => l.AwbId).Distinct().Order().ToList();
        Require(audio.Length == ids.Count, $"{export.Key}: {audio.Length} audio files for {ids.Count} waveforms");
        var layers = new List<ChartSite.BgmLayer>();
        foreach (var layer in cue.Layers)
        {
            var file = audio[ids.IndexOf(layer.AwbId)];
            var data = await ReadBlob(file, token);
            var (samples, rate, channels) = await DecodedLength(data, token);
            layers.Add(new(data, samples, rate, channels, ChartSite.Mp4Priming(data)));
        }
        return [.. layers];
    }

    /// <summary>Sample frames, rate and channels of an m4a as a browser decodes it (edit list applied).</summary>
    private async Task<(long, int, int)> DecodedLength(byte[] m4a, CancellationToken token)
    {
        var directory = Path.Combine(Config.DataDir, "tmp", "chart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "in.m4a"); var output = Path.Combine(directory, "out.wav");
            await File.WriteAllBytesAsync(input, m4a, token);
            await Processes.Run(Config.Ffmpeg, ["-nostdin", "-hide_banner", "-v", "error", "-y", "-i", input, "-c:a", "pcm_s16le", "-f", "wav", output], token);
            using var wav = File.OpenRead(output); using var reader = new BinaryReader(wav);
            Require(new string(reader.ReadChars(4)) == "RIFF", "Invalid WAV"); reader.ReadUInt32(); Require(new string(reader.ReadChars(4)) == "WAVE", "Invalid WAV");
            int channels = 0, rate = 0, align = 0;
            while (wav.Position + 8 <= wav.Length)
            {
                var id = new string(reader.ReadChars(4)); var size = reader.ReadUInt32();
                if (id == "fmt ") { reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = (int)reader.ReadUInt32(); reader.ReadUInt32(); align = reader.ReadUInt16(); wav.Position += size - 14; }
                else if (id == "data") { Require(align > 0, "WAV format missing"); return (Math.Min(size, wav.Length - wav.Position) / align, rate, channels); }
                else wav.Position += size + (size & 1);
            }
            throw new InvalidDataException("WAV data missing");
        }
        finally { RemoveTree(directory); }
    }

    private async Task<byte[]> ReadBlob(PublishedFile file, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(Blobs.PathFor(file.Sha256), token);
        Require(bytes.Length == file.Bytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.Sha256, $"{file.Label}: blob mismatch");
        return bytes;
    }

    static (int, int) PngSize(byte[] png)
    {
        Require(png.Length >= 24 && png.AsSpan(12, 4).SequenceEqual("IHDR"u8), "Invalid PNG");
        return ((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }

    /// <summary>The export of one key in the snapshot (an existing one, or a new one through the shared export work).</summary>
    private async Task<Manifest> ExportKey(Snapshot snapshot, Catalog catalog, string key, CancellationToken token)
    {
        var skip = ExportSelection.SkipReason(catalog, key);
        Require(skip == null, $"{key}: {skip}");
        var id = Crypto.Identity(snapshot.Id, key, Worker.ProfileFor(catalog.Target(key)));
        var manifest = Store.Get<Manifest>("export", id);
        if (manifest != null) return manifest;
        using var lease = await exportWork.Join(id, t => ExportOne(snapshot, catalog, key, id, t), token);
        return lease.Value;
    }

    /// <summary>The ACB bytes of a cue sheet key, from a worker in "acb" mode (nothing is published).</summary>
    private async Task<byte[]> ReadCueSheet(Snapshot snapshot, Catalog catalog, string key, CancellationToken token)
    {
        var target = catalog.Target(key); var locations = ExportSelection.Dependencies(catalog, key);
        var raw = locations.Where(l => l.Provider == Catalog.Cri).ToArray();
        Require(raw.Length <= 1, "Ambiguous CRI dependencies");
        if (raw.Length == 1) locations = raw;
        var inputBytes = locations.Sum(l => l.Options!.Size);
        Require(inputBytes <= Config.ExpandedBytes, "Dependency set budget");
        using var reservation = await Budget.ReserveAsync(inputBytes + Config.InputBytes + Config.ExpandedBytes, token);
        var leases = new List<SharedWork<Download>.Lease>(); var stage = Path.Combine(Config.DataDir, "tmp", "acb-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var location in locations) leases.Add(await downloadWork.Join(snapshot.Id + ":" + location.Id, ct => DownloadOne(snapshot, location, ct), token));
            Directory.CreateDirectory(stage); var output = Path.Combine(stage, "out");
            Artifact[] files;
            await workers.WaitAsync(token);
            try { files = await Processes.Worker(new(Config.ForSnapshot(snapshot), target, leases.Select(l => l.Value.Input).ToArray(), output, Mode: "acb"), stage, token); }
            finally { workers.Release(); }
            Require(files.Length == 1 && files[0].Name == Path.GetFileName(files[0].Name), "Invalid cue sheet output");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, files[0].Name), token);
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == files[0].Sha256, "Cue sheet hash mismatch");
            return bytes;
        }
        finally
        {
            try { RemoveTree(stage); }
            finally { foreach (var lease in leases) await lease.DisposeAsync(); }
        }
    }
}

/// <summary>The master rows a chart needs (decoded MasterData tables, `_allData` rows).</summary>
public static class ChartMaster
{
    // Levels keep their master values (display levels such as 20.5 are not integers).
    public sealed record Chart(int MusicId, string Difficulty, long ScoreId, string ScoreFile, JsonNode? Level, JsonNode? DisplayLevel, long FullComboCount);
    public sealed record SongRows(JsonObject Music, int Band, string Sheet, string Cue, JsonObject SoundRow, string Jacket, string? Title, string?[] BandNames);

    static Dictionary<long, JsonObject> ById(Dictionary<string, JsonArray> master, string table) =>
        master[table].Select(r => r!.AsObject()).ToDictionary(r => ChartSite.Integer(r["_id"]));

    /// <summary>Every MasterLiveMusic x difficulty with a MasterLiveMusicScore row, in music id order (nnnotes web.all_pairs).</summary>
    public static List<Chart> Charts(Dictionary<string, JsonArray> master)
    {
        var scores = ById(master, "MasterLiveMusicScore"); var output = new List<Chart>();
        foreach (var music in master["MasterLiveMusic"].Select(r => r!.AsObject()).OrderBy(r => ChartSite.Integer(r["_id"])))
            foreach (var difficulty in ChartScore.Difficulties)
                if (music[$"_{difficulty}ID"] is JsonValue scoreId && scoreId.GetValueKind() == System.Text.Json.JsonValueKind.Number && scores.TryGetValue(ChartSite.Integer(scoreId), out var score))
                    output.Add(new((int)ChartSite.Integer(music["_id"]), difficulty, ChartSite.Integer(score["_id"]), (string)score["_musicScoreTextFileName"]!,
                        score["_musicScoreLevel"]?.DeepClone(), score["_musicScoreDisplayLevel"]?.DeepClone(), ChartSite.Integer(score["_fullComboCount"])));
        return output;
    }

    /// <summary>A song's rows. The stage band is that of the first vocal character (nnnotes live.resolve_band default).</summary>
    public static SongRows Song(Dictionary<string, JsonArray> master, int musicId, string locale)
    {
        var music = ById(master, "MasterLiveMusic").GetValueOrDefault(musicId) ?? throw new InvalidDataException($"MasterLiveMusic {musicId} missing");
        var vocals = music["_vocalCharacterIDs"]?.AsArray() ?? [];
        Require(vocals.Count > 0, $"music {musicId}: no vocal character, no stage band");
        var character = ById(master, "MasterCharacter").GetValueOrDefault(ChartSite.Integer(vocals[0])) ?? throw new InvalidDataException($"MasterCharacter {vocals[0]} missing");
        var sound = ById(master, "MasterSound").GetValueOrDefault(ChartSite.Integer(music["_musicSoundID"])) ?? throw new InvalidDataException($"MasterSound {music["_musicSoundID"]} missing");
        Require(!(bool)(sound["_isRandomPitch"] ?? false), $"sound {sound["_id"]}: random pitch");
        var sheet = ById(master, "MasterSoundCueSheet").GetValueOrDefault(ChartSite.Integer(sound["_soundCueSheetID"])) ?? throw new InvalidDataException($"MasterSoundCueSheet {sound["_soundCueSheetID"]} missing");
        var column = locale switch { "zh-Hans" => "_simplifiedChinese", "ja" => "_japanese", "en" => "_english", "ko" => "_korean", _ => "_traditionalChinese" };
        var texts = master["MasterText"].Select(r => r!.AsObject()).GroupBy(r => (string)r["_id"]!).ToDictionary(g => g.Key, g => g.First());
        string? Text(JsonNode? id) => id is null || !texts.TryGetValue((string)id!, out var row) ? null : (string?)row[column];
        var bands = ById(master, "MasterBand");
        var names = (music["_bandIDs"]?.AsArray() ?? []).Select(b => bands.TryGetValue(ChartSite.Integer(b), out var band) ? Text(band["_nameTextID"]) : null).ToArray();
        return new(music, (int)ChartSite.Integer(character["_bandID"]), (string)sheet["_cueSheetName"]!, (string)sound["_cueName"]!, sound,
            (string)music["_jacketAssetName"]!, Text(music["_titleTextID"]), names);
    }
}
