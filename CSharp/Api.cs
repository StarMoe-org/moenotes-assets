using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
namespace MoenotesAssets;

public static class Api
{
    public static WebApplication Build(AssetService service, string? url = null, string? apiKey = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable("MOENOTES_API_KEY");
        var keyHash = string.IsNullOrWhiteSpace(apiKey) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(Api).Assembly.GetName().Name });
        builder.WebHost.UseUrls(url ?? $"http://{service.Config.Listen}");
        // Per-request framework logs (request start/finish, endpoint, file result) are four lines per file download.
        // Keep warnings and errors; service events and failed requests are logged separately.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 << 20);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
            options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        });
        if (service.Config.CorsOrigins.Length > 0) builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (service.Config.CorsOrigins.Contains("*")) policy.AllowAnyOrigin();
            else policy.WithOrigins(service.Config.CorsOrigins);
            policy.WithMethods("GET", "HEAD", "POST").WithHeaders("Authorization", "Content-Type", "Range", "If-None-Match", "If-Range").WithExposedHeaders("ETag", "Content-Range", "Accept-Ranges", "Content-Length", "Location");
        }));
        var app = builder.Build();
        if (service.Config.CorsOrigins.Length > 0) app.UseCors();
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var protectedRequest = (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
                || request.Path.StartsWithSegments("/tasks", StringComparison.OrdinalIgnoreCase)
                || request.Path.StartsWithSegments("/storage", StringComparison.OrdinalIgnoreCase);
            if (protectedRequest)
            {
                context.Response.Headers.CacheControl = "no-store";
                if (keyHash == null)
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new { error = "Administrative API disabled: MOENOTES_API_KEY is not configured" });
                    return;
                }
                var headers = request.Headers.Authorization;
                var header = headers.Count == 1 ? headers[0] : null;
                var valid = header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    && CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])));
                if (!valid)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
                    return;
                }
            }
            await next(context);
        });
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception exception) when (!context.Response.HasStarted)
            {
                var status = exception switch
                {
                    ApiException e => e.Status,
                    BadHttpRequestException e => e.StatusCode,
                    InvalidDataException or JsonException or ArgumentException => 400,
                    _ => 500
                };
                context.Response.StatusCode = status;
                if (status == 500) app.Logger.LogError(exception, "Request failed");
                await context.Response.WriteAsJsonAsync(new { error = status == 500 ? "Internal service error" : exception.Message });
            }
        });
        // Path routes: public/{locale}/{key}/{label}{ext} is a tree of hard links written at publication
        // (AssetService.MaterializePaths), served as static files without SQLite. Paths follow the newest published
        // export, so they get a short cache; /files/{id} stays immutable.
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".txt"] = contentTypes.Mappings[".sus"] = "text/plain; charset=utf-8";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(service.PublicRoot), // excludes dot-prefixed temporary links
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "public,max-age=600",
        });
        // What the tree cannot answer under a locale: /{locale}/{key}/ listings, misses before the backfill finishes
        // (resolved from SQLite), and 404s, which are publicly cacheable so repeated misses stop at the CDN.
        app.Use(async (context, next) =>
        {
            var request = context.Request; var parts = request.Path.Value!.Split('/', 3);
            if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || context.GetEndpoint() != null || parts.Length < 3 || !service.IsPathLocale(parts[1]))
            {
                await next(context); return;
            }
            var (locale, rest) = (parts[1], parts[2]);
            IResult result;
            if (rest.Length == 0 || rest.EndsWith('/'))
            {
                var listed = service.ResolvePath(locale, rest.TrimEnd('/'));
                if (listed != null) context.Response.Headers.CacheControl = "public,max-age=60";
                result = listed != null ? Results.Json(listed.Listing, Json.Options) : NotFound(context);
            }
            else
            {
                var slash = rest.LastIndexOf('/');
                var resolved = !service.PublicTreeReady && slash > 0 ? service.ResolvePath(locale, rest[..slash]) : null;
                result = resolved != null && resolved.Files.TryGetValue(rest[(slash + 1)..], out var file) && file != null
                    ? ServeFile(context, file.Id, "public,max-age=600") : NotFound(context);
            }
            await result.ExecuteAsync(context);
        });
        static IResult NotFound(HttpContext context)
        {
            context.Response.Headers.CacheControl = "public,max-age=60";
            return Results.NotFound(new { error = "No published file at this path" });
        }
        app.MapGet("/health", () => new { status = "ok" });
        app.MapGet("/ready", () =>
        {
            service.Store.Execute("SELECT 1"); var probe = Path.Combine(service.Config.DataDir, "tmp", Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ready"); File.Delete(probe); return new { ready = true, reserved_temp_bytes = service.Budget.Used };
        });
        app.MapPost("/catalog/refresh", (string? region, string? locale, string? version) => Accepted(service.StartRefresh(region, locale, version)));
        app.MapGet("/catalogs", (string? region, string? locale) => service.ListCatalogs(region, locale));
        app.MapGet("/assets", (string? snapshot, string? region, string? locale, string? bundle, string? prefix, string? resource_type, int? offset, int? limit) =>
            service.ListAssets(snapshot, prefix, resource_type, offset ?? 0, limit ?? 100, region, locale, bundle));
        app.MapPost("/exports", (ExportRequest request) => Accepted(service.StartExport(request)));
        app.MapPost("/tasks/batches", (BatchRequest request) =>
        {
            var batch = service.StartBatch(request);
            return Results.Accepted($"/tasks/batches/{batch.Id}", batch);
        });
        app.MapGet("/tasks/batches", (int? offset, int? limit) => service.ListBatches(offset ?? 0, limit ?? 100));
        app.MapGet("/tasks/batches/{id}", (string id) => service.GetBatch(id));
        app.MapPost("/tasks/batches/{id}/cancel", (string id) => service.CancelBatch(id));
        app.MapGet("/tasks/{id}", (string id) => service.GetTask(id) is { } task ? Results.Json(task, Json.Options) : Results.NotFound(new { error = "Task not found" }));
        app.MapPost("/tasks/{id}/cancel", (string id) => Results.Json(service.Cancel(id), Json.Options));
        app.MapGet("/exports/{id}", (string id) => service.Manifest(id) is { } manifest ? Results.Json(manifest, Json.Options) : Results.NotFound(new { error = "Export not found" }));
        IResult ServeFile(HttpContext context, string id, string cacheControl)
        {
            var record = service.LookupFile(id); if (record == null) return Results.NotFound(new { error = "File not found" });
            var path = service.FilePath(record);
            if (!File.Exists(path)) return Results.NotFound(new { error = "File not found" });
            context.Response.Headers.CacheControl = cacheControl;
            return Results.File(path, record.File.MediaType, entityTag: new EntityTagHeaderValue('"' + record.File.Sha256 + '"'), enableRangeProcessing: true);
        }
        app.MapMethods("/files/{id}", ["GET", "HEAD"], (string id, HttpContext context) => ServeFile(context, id, "public,max-age=31536000,immutable"));
        app.MapGet("/regions", () => service.Config.Regions.Length == 0
            ? new[] { new { id = service.Config.Region, default_locale = service.Config.Locale, locales = service.Config.Locales.Length == 0 ? new[] { service.Config.Locale } : service.Config.Locales } }
            : service.Config.Regions.Select(r => new { id = r.Id, default_locale = r.Locale ?? service.Config.Locale, locales = r.Locales ?? new[] { r.Locale ?? service.Config.Locale } }).ToArray());
        app.MapGet("/bundles", (string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.ListBundles(snapshot, region, locale, prefix, offset ?? 0, limit ?? 100));
        app.MapGet("/contents", (string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.Store.Contents(service.ResolveSnapshot(snapshot, region, locale).Id, prefix, null, offset ?? 0, limit ?? 100));
        app.MapGet("/browse", (string? snapshot, string? region, string? locale, string? directory, string? query, int? offset, int? limit, bool? descending) => service.Store.Browse(service.ResolveSnapshot(snapshot, region, locale).Id, directory, query, offset ?? 0, limit ?? 100, descending ?? false));
        app.MapGet("/scan/status", (string? snapshot, string? region, string? locale) => service.Store.ScanStatus(service.ResolveSnapshot(snapshot, region, locale).Id));
        app.MapPost("/bundles/scan", () => Accepted(service.StartBundleScan()));
        app.MapGet("/bundles/{id}", (string id, string? snapshot, string? region, string? locale) => service.Store.Bundle(service.ResolveSnapshot(snapshot, region, locale).Id, id));
        app.MapGet("/bundles/{id}/contents", (string id, string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.Store.Contents(service.ResolveSnapshot(snapshot, region, locale).Id, prefix, id, offset ?? 0, limit ?? 100, true));
        app.MapGet("/bundles/{id}/assets", (string id, string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.ListAssets(snapshot, prefix, null, offset ?? 0, limit ?? 100, region, locale, id));
        app.MapGet("/bundles/{id}/equivalents", (string id, string? snapshot, string? region, string? locale, int? limit) => service.Store.Equivalents(service.ResolveSnapshot(snapshot, region, locale).Id, id, limit ?? 100));
        app.MapPost("/bundles/verify", (VerifyRequest request) => Accepted(service.StartVerify(request)));
        app.MapGet("/diffs", (string from, string to, string? prefix, int? offset, int? limit, bool? include_unchanged) => service.Store.Diff(from, to, prefix, offset ?? 0, limit ?? 100, include_unchanged ?? false));
        app.MapGet("/storage", () => service.Store.StorageStats(service.Budget.Used));

        return app;
    }
    private static IResult Accepted(TaskInfo task) => Results.Accepted($"/tasks/{task.Id}", task);
}
