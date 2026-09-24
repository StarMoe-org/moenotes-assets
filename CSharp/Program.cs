using MoenotesAssets;
const string usage = "Usage: moenotes-assets serve CONFIG.toml | refresh CONFIG.toml | list CONFIG.toml [PREFIX] | export CONFIG.toml KEY... | export CONFIG.toml --prefix PREFIX | --version";
try
{
    if (args.Length == 1 && args[0] is "--help" or "-h") { Console.WriteLine(usage); return 0; }
    if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("moenotes-assets 0.2.0-csharp (.NET 10)"); return 0; }
    if (args.Length == 2 && args[0] == "worker")
    {
        WorkerResult result;
        try { var job = Json.Read<WorkerJob>(await File.ReadAllTextAsync(args[1])); job.Config.Validate(); using var guard = new WorkerGuard(job); result = new(await Worker.Run(job), null); }
        catch (Exception e) { result = new([], e.Message); }
        await File.WriteAllTextAsync(args[1] + ".result.json", Json.Write(result)); return 0;
    }
    if (args.Length < 2 || args[0] is not ("serve" or "refresh" or "list" or "export"))
    {
        Console.Error.WriteLine(usage); return 2;
    }
    if ((args[0] is "serve" or "refresh" && args.Length != 2) || (args[0] == "list" && args.Length > 3) ||
        (args[0] == "export" && (args.Length < 3 || (args[2] == "--prefix" && args.Length != 4))))
    { Console.Error.WriteLine(usage); return 2; }
    var config = Config.Load(args[1]); await using var service = new AssetService(config);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (args[0] == "serve")
    {
        await service.CheckMedia(cancellation.Token); var app = Api.Build(service); await app.RunAsync(cancellation.Token); return 0;
    }
    if (args[0] == "list") { Console.WriteLine(Json.Write(service.ListAssets(null, args.ElementAtOrDefault(2), null, 0, 1000))); return 0; }
    TaskInfo task;
    if (args[0] == "refresh") task = service.StartRefresh();
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
