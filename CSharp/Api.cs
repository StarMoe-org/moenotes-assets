using Microsoft.Net.Http.Headers;
using System.Text.Json;
namespace MoenotesAssets;

public static class Api
{
    public static WebApplication Build(AssetService service, string? url = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(Api).Assembly.GetName().Name });
        builder.WebHost.UseUrls(url ?? $"http://{service.Config.Listen}");
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
            policy.WithMethods("GET", "HEAD", "POST").WithHeaders("Content-Type", "Range", "If-None-Match", "If-Range").WithExposedHeaders("ETag", "Content-Range", "Accept-Ranges", "Content-Length", "Location");
        }));
        var app = builder.Build();
        if (service.Config.CorsOrigins.Length > 0) app.UseCors();
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
        app.MapGet("/tasks/{id}", (string id) => service.GetTask(id) is { } task ? Results.Json(task, Json.Options) : Results.NotFound(new { error = "Task not found" }));
        app.MapPost("/tasks/{id}/cancel", (string id) => Results.Json(service.Cancel(id), Json.Options));
        app.MapGet("/exports/{id}", (string id) => service.Manifest(id) is { } manifest ? Results.Json(manifest, Json.Options) : Results.NotFound(new { error = "Export not found" }));
        app.MapMethods("/files/{id}", ["GET", "HEAD"], (string id, HttpContext context) =>
        {
            var record = service.LookupFile(id); if (record == null) return Results.NotFound(new { error = "File not found" });
            var path = service.FilePath(record);
            if (!File.Exists(path)) return Results.NotFound(new { error = "File not found" });
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            return Results.File(path, record.File.MediaType, entityTag: new EntityTagHeaderValue('"' + record.File.Sha256 + '"'), enableRangeProcessing: true);
        });
        app.MapGet("/regions", () => service.Config.Regions.Length == 0
            ? new[] { new { id = service.Config.Region, default_locale = service.Config.Locale, locales = service.Config.Locales.Length == 0 ? new[] { service.Config.Locale } : service.Config.Locales } }
            : service.Config.Regions.Select(r => new { id = r.Id, default_locale = r.Locale ?? service.Config.Locale, locales = r.Locales ?? new[] { r.Locale ?? service.Config.Locale } }).ToArray());
        app.MapGet("/bundles", (string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.ListBundles(snapshot, region, locale, prefix, offset ?? 0, limit ?? 100));
        app.MapGet("/bundles/{id}", (string id, string? snapshot, string? region, string? locale) => service.Store.Bundle(service.ResolveSnapshot(snapshot, region, locale).Id, id));
        app.MapGet("/bundles/{id}/assets", (string id, string? snapshot, string? region, string? locale, string? prefix, int? offset, int? limit) => service.ListAssets(snapshot, prefix, null, offset ?? 0, limit ?? 100, region, locale, id));
        app.MapGet("/bundles/{id}/equivalents", (string id, string? snapshot, string? region, string? locale, int? limit) => service.Store.Equivalents(service.ResolveSnapshot(snapshot, region, locale).Id, id, limit ?? 100));
        app.MapPost("/bundles/verify", (VerifyRequest request) => Accepted(service.StartVerify(request)));
        app.MapGet("/diffs", (string from, string to, string? prefix, int? offset, int? limit, bool? include_unchanged) => service.Store.Diff(from, to, prefix, offset ?? 0, limit ?? 100, include_unchanged ?? false));
        app.MapGet("/storage", () => service.Store.StorageStats(service.Budget.Used));

        return app;
    }
    private static IResult Accepted(TaskInfo task) => Results.Accepted($"/tasks/{task.Id}", task);
}
