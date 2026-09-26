using MoenotesAssets;
const string usage = "Usage: moenotes-assets serve CONFIG.toml | scan CONFIG.toml | refresh CONFIG.toml | update CONFIG.toml [--force] | list CONFIG.toml [PREFIX] | export CONFIG.toml KEY... | export CONFIG.toml --prefix PREFIX | chart-site CONFIG.toml [--force] [MUSIC_ID...] | model-site CONFIG.toml [--force] [MODEL_ID...] | chart-base NNNOTES_SITE OUT.zip SOURCE | apk-data BASE.APK OUT_DIR | --version";
try
{
    if (args.Length == 1 && args[0] is "--help" or "-h") { Console.WriteLine(usage); return 0; }
    if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("moenotes-assets 0.2.0-csharp (.NET 10)"); return 0; }
    if (args.Length == 4 && args[0] == "chart-base")
    {
        // Packs an nnnotes `web` site as a static base package (docs/CHART_SITE.md); prints its facts and sha256.
        var info = ChartSite.PackBase(args[1], args[2], args[3]);
        await using var zip = File.OpenRead(args[2]);
        info["sha256"] = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(zip));
        info["bytes"] = zip.Length;
        Console.WriteLine(info.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); return 0;
    }
    if (args.Length == 3 && args[0] == "apk-data")
    {
        // Extracts what Live2D models need from a base.apk into a directory (docs/MODEL_SITE.md).
        ApkData.Extract(args[1], args[2], new Config());
        var data = ApkData.Load(args[2]);
        Console.WriteLine(Json.Write(new { data.Sha256, data.UnityVersion, data.ScriptFile, scripts = data.Scripts.Count, resources = data.Resources.Keys, shaders = data.Shaders.Keys })); return 0;
    }
    if (args.Length >= 6 && args[0] == "live2d-model")
    {
        // Builds one model's files from local bundles: APK_DATA OUT KEY INTERNAL_ID BUNDLE... (a debugging aid).
        var job = new WorkerJob(new Config(), new Location(0, args[3], args[4], Catalog.Crypt, "UnityEngine.GameObject", [], null),
            [.. args[5..].Select((p, i) => new WorkerInput(new Location((uint)i + 1, Path.GetFileName(p), p, Catalog.Crypt, "", [], null), Path.GetFullPath(p)))],
            Path.GetFullPath(args[2]), Mode: "live2d", ApkData: Path.GetFullPath(args[1]));
        var files = Live2DModel.Build(job);
        Console.WriteLine(Json.Write(new { files = files.Length, bytes = files.Sum(f => f.Bytes) })); return 0;
    }
    if (args.Length == 2 && args[0] == "worker")
    {
        WorkerResult result;
        try { var job = Json.Read<WorkerJob>(await File.ReadAllTextAsync(args[1])); job.Config.Validate(); using var guard = new WorkerGuard(job); result = new(await Worker.Run(job), null); }
        catch (UnsupportedInputException e) { result = new([], e.Message, true); }
        catch (Exception e) { result = new([], e.Message); }
        await File.WriteAllTextAsync(args[1] + ".result.json", Json.Write(result)); return 0;
    }
    if (args.Length == 2 && args[0] == "scan-worker")
    {
        BundleScanResult result;
        try { var job = Json.Read<WorkerJob>(await File.ReadAllTextAsync(args[1])); job.Config.Validate(); using var guard = new WorkerGuard(job); result = new(BundleScanner.Scan(job), null); }
        catch (Exception e) { result = new([], e.Message); }
        await File.WriteAllTextAsync(args[1] + ".result.json", Json.Write(result)); return 0;
    }
    if (args.Length < 2 || args[0] is not ("serve" or "scan" or "refresh" or "update" or "list" or "export" or "chart-site" or "model-site"))
    {
        Console.Error.WriteLine(usage); return 2;
    }
    if ((args[0] is "serve" or "scan" or "refresh" && args.Length != 2) || (args[0] == "list" && args.Length > 3) ||
        (args[0] == "update" && !(args.Length == 2 || (args.Length == 3 && args[2] == "--force"))) ||
        (args[0] == "export" && (args.Length < 3 || (args[2] == "--prefix" && args.Length != 4))))
    { Console.Error.WriteLine(usage); return 2; }
    var config = Config.Load(args[1]); await using var service = new AssetService(config);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (args[0] == "serve")
    {
        await service.CheckMedia(cancellation.Token); var app = Api.Build(service); service.EnableAutomaticBundleScan(); service.EnableVersionPolling(); service.EnableAutomaticModelSite();
        _ = service.BackfillPublicTree(); // background; path misses fall back to SQLite until it finishes
        _ = service.SweepStorage(); // background; orphaned blobs are never served
        await app.RunAsync(cancellation.Token); return 0;
    }
    if (args[0] == "list") { Console.WriteLine(Json.Write(service.ListAssets(null, args.ElementAtOrDefault(2), null, 0, 1000))); return 0; }
    _ = service.SweepStorage();
    if (args[0] == "update")
    {
        // Checks version_url once and waits for every queued release (refresh + export of all languages) to finish.
        await service.CheckMedia(cancellation.Token);
        var check = await service.CheckVersions(args.Length == 3, cancellation.Token);
        var waiting = check.Regions.Where(r => r.Action is "queued" or "pending").ToArray();
        using var stop = cancellation.Token.Register(() => { foreach (var r in waiting) service.CancelBatch(r.Batch!); });
        var releases = new List<Release>();
        foreach (var r in waiting) releases.Add(await service.WaitRelease(r.Release!));
        var chartSite = await service.WaitAutomaticChartSite(); // requested by a completed release or a master change
        var modelSite = await service.WaitAutomaticModelSite(); // requested with the chart site
        Console.WriteLine(Json.Write(new { check, releases, chart_site = chartSite, model_site = modelSite }));
        return releases.Any(r => r.State == "cancelled") ? 130
            : releases.All(r => r.State == "succeeded") && check.Regions.All(r => r.Error == null) && chartSite?.State is null or "succeeded" && modelSite?.State is null or "succeeded" ? 0 : 1;
    }
    TaskInfo task;
    if (args[0] == "refresh") task = service.StartRefresh();
    else if (args[0] == "scan") task = service.StartBundleScan();
    else if (args[0] == "chart-site")
    {
        var options = args[2..]; var force = options.Contains("--force");
        var music = options.Where(a => a != "--force").Select(a => int.TryParse(a, out var id) && id > 0 ? id : throw new InvalidDataException($"Invalid music id {a}")).ToArray();
        await service.CheckMedia(cancellation.Token);
        task = service.StartChartSite(new(music.Length > 0 ? music : null, force));
    }
    else if (args[0] == "model-site")
    {
        var options = args[2..];
        task = service.StartModelSite(new(options.Any(a => a != "--force") ? [.. options.Where(a => a != "--force")] : null, options.Contains("--force")));
    }
    else
    {
        if (args.Length < 3) throw new InvalidDataException("Specify one or more keys or --prefix PREFIX");
        await service.CheckMedia(cancellation.Token);
        task = service.StartExport(args[2] == "--prefix" && args.Length == 4 ? new(Prefix: args[3]) : new(Keys: args[2..]));
    }
    using var registration = cancellation.Token.Register(() => service.Cancel(task.Id));
    var final = await service.Wait(task.Id); Console.WriteLine(Json.Write(final));
    return final.State == "succeeded" ? 0 : final.State == "cancelled" ? 130 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled"); return 130; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
