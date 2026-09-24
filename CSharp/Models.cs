using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace MoenotesAssets;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static readonly JsonSerializerOptions Strict = new(Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Null JSON document");
}
public sealed record Snapshot(string Id, string ContentSha256, string Region, string Locale, string BiliVersion,
    string CdnRoot, string RemoteHash, long Created);
public sealed record ExportRequest(string[]? Keys = null, string? Prefix = null, string? Snapshot = null, string? Region = null, string? Locale = null);
public sealed record ItemResult(string Key, string? ExportId, string? Error, string? SkipReason = null, bool Reused = false);
public sealed record TaskInfo(string Id, string Kind, string State, string? Snapshot, int Total, int Completed,
    ItemResult[] Results, string? Error, long Created, long Updated, int Skipped = 0, int Reused = 0);
public sealed record Artifact(string Name, string Label, string MediaType, long Bytes, string Sha256, object? Metadata);
public sealed record PublishedFile(string Id, string Name, string Label, string MediaType, long Bytes, string Sha256, object? Metadata);
public sealed record Source(Location Location, string DownloadSha256, string? PlainSha256 = null);
public sealed record Manifest(string Id, string Snapshot, string Key, string Profile, Source[] Sources, PublishedFile[] Files, string? Region = null, string? ReusedFrom = null);
public sealed record WorkerInput(Location Location, string Path);
public sealed record WorkerJob(Config Config, Location Target, WorkerInput[] Inputs, string Output, int ParentPid = 0);
public sealed record WorkerResult(Artifact[] Files, string? Error);
public sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } = status; }

public sealed record VerifyRequest(string[] Ids, string? Snapshot = null, string? Region = null, string? Locale = null);
