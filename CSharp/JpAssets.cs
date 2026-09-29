using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

// Public metadata. Passwords and Authorization headers must never be fields here:
// this record is persisted with releases, batches and snapshots.
public sealed record JpAssetSource(string Provider, string Platform, string Version, string Hash,
    string CatalogUrl, string BundleRoot, string ApiRoot, string ClientVersion)
{
    public const string Placeholder = "{Fwk.Resource.RemoteAssetDir}/";
    public void Validate(Config config)
    {
        Require(Provider == "jp" && Platform == "Android", "Unsupported asset source");
        Require(SafeVersion(Version) && Hash.Length == 32 && Hash.All(char.IsAsciiHexDigit), "Invalid JP asset identity");
        Require(System.Version.TryParse(ClientVersion, out _) && ClientVersion.Length <= 64, "Invalid JP client version");
        Require(ApiRoot == config.JpApiOrigin.TrimEnd('/'), "Unapproved JP API origin");
        var root = config.JpCdnOrigin.TrimEnd('/') + $"/asset/{Version}/Android/{Hash}";
        Require(BundleRoot == root && CatalogUrl == root + "/catalog_main.bin", "JP asset paths do not match identity/origin");
    }
    public static bool SafeVersion(string value) => value.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(value[0]) &&
        !value.Contains("..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public Uri AssetUri(string id)
    {
        Require(id.StartsWith(Placeholder, StringComparison.Ordinal), "Unsupported JP asset placeholder");
        var name = id[Placeholder.Length..];
        Require(name.Length is > 0 and <= 1024 && !name.Any(c => "\\%?#{}".Contains(c) || char.IsControl(c)) &&
            !name.Split('/').Any(p => p is "" or "." or ".."), "Unsafe JP asset path");
        return new Uri(BundleRoot + "/" + name);
    }
}

public sealed partial class AssetService
{
    private readonly SemaphoreSlim jpAuthGate = new(1);
    private readonly Dictionary<string, JpCredential> jpCredentials = new();
    private sealed class JpCredential(string authorization, string userAgent, string version, string hash)
    {
        public string Authorization { get; } = authorization;
        public string UserAgent { get; } = userAgent;
        public string Version { get; } = version;
        public string Hash { get; } = hash;
        public long Created { get; } = Now;
        public override string ToString() => "[JP CDN credential]";
    }

    // Public only to make the selection contract independently testable. No live
    // fallback is allowed when the live array exists but has no applicable entry.
    public static (string Version, string Hash) SelectJpAsset(string raw, string clientVersion)
    {
        Require(raw.Length <= 65536, "JP asset header limit");
        var payload = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("Invalid JP asset payload");
        JsonObject? selected = payload;
        Require(payload["live"] is null or JsonArray, "Invalid JP live entries");
        static Version? Numeric(string? s) => Version.TryParse(s, out var v) ? new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)) : null;
        if (payload["live"] is JsonArray { Count: > 0 } live)
        {
            selected = null; var client = Numeric(clientVersion); Version? best = null;
            foreach (var node in live)
            {
                if (node == null) continue;
                Require(node is JsonObject, "Invalid JP live entry");
                var item = (JsonObject)node;
                var minimum = Numeric(item["minClientVersion"]?.GetValue<string>());
                if (client != null && minimum != null && minimum <= client && (best == null || minimum > best))
                { selected = item; best = minimum; }
            }
        }
        var version = selected?["version"]?.GetValue<string>()?.Trim() ?? "";
        var hash = selected?["Android"]?.GetValue<string>()?.Trim() ?? "";
        Require(JpAssetSource.SafeVersion(version) && hash.Length == 32 && hash.All(char.IsAsciiHexDigit), "No applicable JP assets");
        return (version, hash);
    }

    private async Task<JpCredential> GetJpCredential(JpAssetSource source, CancellationToken token, JpCredential? rejected = null)
    {
        source.Validate(Config);
        var key = Crypto.Identity(source.ApiRoot, Config.JpCdnOrigin, source.ClientVersion, source.Version, source.Hash);
        await jpAuthGate.WaitAsync(token);
        try
        {
            if (jpCredentials.TryGetValue(key, out var existing) && existing != rejected && Now - existing.Created < 300) return existing;
            using var request = new HttpRequestMessage(HttpMethod.Post, source.ApiRoot + "/app.masterdata.MasterdataService/Version")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(new byte[5]) // uncompressed empty protobuf message
            };
            request.Content.Headers.ContentType = new("application/grpc");
            request.Headers.Add("te", "trailers"); request.Headers.Add("x-platform", "android");
            request.Headers.Add("x-client-version", source.ClientVersion); request.Headers.Add("x-request-id", Guid.NewGuid().ToString());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            Require(response.StatusCode == HttpStatusCode.OK, $"JP Version HTTP {(int)response.StatusCode}");
            var body = await ReadResponse(response, 65536, token);
            string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() :
                response.TrailingHeaders.TryGetValues(name, out values) ? values.SingleOrDefault() : null;
            // Error text/metadata may contain credentials. Only emit fixed classifications.
            Require(Header("grpc-status") == "0", "JP Version rejected; check client version or maintenance");
            Require(response.Content.Headers.ContentType?.MediaType == "application/grpc" && body.Length >= 5 && body[0] == 0 &&
                BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1, 4)) == body.Length - 5, "Invalid JP Version frame");
            Require(Header("x-sirius-env")?.TrimEnd('/') == Config.JpCdnOrigin.TrimEnd('/'), "Unapproved JP CDN origin");
            var password = Header("x-sirius-cred") ?? "";
            Require(password.Length is > 0 and <= 4096 && !password.Any(char.IsControl), "Missing JP CDN credential");
            (string Version, string Hash) asset;
            try { asset = SelectJpAsset(Header("x-asset-version") ?? "", source.ClientVersion); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { throw new InvalidDataException("Invalid JP asset payload"); }
            var credential = new JpCredential("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("sirius:" + password)),
                "OurNotes/" + source.ClientVersion, asset.Version, asset.Hash);
            if (jpCredentials.Count >= 16) jpCredentials.Clear();
            jpCredentials[key] = credential;
            return credential;
        }
        finally { jpAuthGate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsset(Uri uri, JpAssetSource? source, bool catalog, CancellationToken token)
    {
        if (source == null) return await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        source.Validate(Config);
        Require(uri.AbsoluteUri.StartsWith(source.BundleRoot + "/", StringComparison.Ordinal), "JP credential target mismatch");
        var credential = await GetJpCredential(source, token);
        for (var attempt = 0; ; attempt++)
        {
            Require(!catalog || credential.Version == source.Version && credential.Hash == source.Hash,
                "JP resource metadata changed; check metadata service again");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(credential.Authorization);
            request.Headers.UserAgent.ParseAdd(credential.UserAgent);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (attempt != 0 || response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;
            response.Dispose();
            credential = await GetJpCredential(source, token, credential);
        }
    }

    private static async Task<byte[]> ReadResponse(HttpResponseMessage response, int limit, CancellationToken token)
    {
        Require(response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength <= limit, "Response size limit");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[65536]; int n;
        while ((n = await stream.ReadAsync(buffer, token)) > 0)
        { Require(output.Length + n <= limit, "Response size limit"); output.Write(buffer, 0, n); }
        return output.ToArray();
    }
}
