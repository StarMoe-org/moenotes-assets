using System.Net;
using System.Text.Json;
using Tomlyn;
namespace MoenotesAssets;

public sealed record Config
{
    public string Listen { get; init; } = "127.0.0.1:8091";
    public string DataDir { get; init; } = "data";
    public string Region { get; init; } = "hk";
    public string CdnRoot { get; init; } = "";
    public string Locale { get; init; } = "zh-Hant";
    public string BiliVersion { get; init; } = "main";
    public int Downloads { get; init; } = 8;
    public int Workers { get; init; } = 2;
    public int Videos { get; init; } = 1;
    public int FfmpegThreads { get; init; } = 4;
    public int QueueLimit { get; init; } = 128;
    public int MaxKeys { get; init; } = 50_000;
    public long TempBytes { get; init; } = 20L << 30;
    public long InputBytes { get; init; } = 1L << 30;
    public long OutputBytes { get; init; } = 2L << 30;
    public long ExpandedBytes { get; init; } = 2L << 30;
    public long MemoryThreshold { get; init; } = 32L << 20;
    public long WorkerMemoryBytes { get; init; } = 3L << 30;
    public int WorkerTimeoutSecs { get; init; } = 900;
    public int DownloadTimeoutSecs { get; init; } = 120;
    public string Ffmpeg { get; init; } = "ffmpeg";
    public string Ffprobe { get; init; } = "ffprobe";
    public ulong CriKey { get; init; } = 8594927479;
    public bool AllowLoopbackHttp { get; init; }
    public string[] CorsOrigins { get; init; } = [];
    public RegionSettings[] Regions { get; init; } = [];
    public string[] Locales { get; init; } = [];
    public string ClassData { get; init; } = "";
    // Chart site: the static base, a package downloaded from chart_base_url (checked against chart_base_sha256) or a
    // local chart_base directory (a package or an nnnotes `web` site), and the decoded MasterData root (<root>/<Table>.json).
    public string ChartBaseUrl { get; init; } = "";
    public string ChartBaseSha256 { get; init; } = "";
    public string ChartBase { get; init; } = "";
    public string MasterRoot { get; init; } = "";
    // Version tracking (docs/API.md): the metadata service's current_version.json, checked every version_poll_secs
    // (0: only on request). A region's entry is metadata_region (default: its id; "" in [[regions]] opts out).
    public string VersionUrl { get; init; } = "";
    public int VersionPollSecs { get; init; } = 600;
    public string MetadataRegion { get; init; } = "";
    public static Config Load(string path)
    {
        var table = Toml.ToModel(File.ReadAllText(path));
        var config = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(table), Json.Strict)
            ?? throw new InvalidDataException("Empty configuration");
        config.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return config with
        {
            DataDir = Path.GetFullPath(config.DataDir, directory),
            ChartBase = config.ChartBase.Length == 0 ? "" : Path.GetFullPath(config.ChartBase, directory),
        };
    }
    public void Validate()
    {
        Require(IPEndPoint.TryParse(Listen, out _), "Invalid listen endpoint");
        foreach (var (name, value, max) in new[] { ("downloads", Downloads, 64), ("workers", Workers, 16),
                     ("videos", Videos, 16), ("ffmpeg_threads", FfmpegThreads, 64),
                     ("queue_limit", QueueLimit, 4096), ("max_keys", MaxKeys, 50000) })
            Require(value > 0 && value <= max, $"Invalid {name}");
        foreach (var size in new[] { TempBytes, InputBytes, OutputBytes, ExpandedBytes, WorkerMemoryBytes })
            Require(size >= 1 << 20 && size <= 1L << 40, "Invalid byte budget");
        Require(MemoryThreshold >= 0 && MemoryThreshold <= InputBytes, "Invalid memory threshold");
        Require(InputBytes + OutputBytes + ExpandedBytes * 2 <= TempBytes, "Temporary budget too small");
        Require(DownloadTimeoutSecs is > 0 and <= 3600 && WorkerTimeoutSecs is > 0 and <= 86400, "Invalid timeout");
        foreach (var part in new[] { Region, Locale, BiliVersion })
            Require(part.Length <= 64 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Invalid configuration component");
        Require(Region.Length > 0 && BiliVersion.Length > 0, "Empty region or version");
        if (Regions.Length == 0) _ = Root();
        else
        {
            Require(Regions.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == Regions.Length, "Duplicate region ID");
            foreach (var region in Regions) ForRegion(region.Id).Validate();
        }
        foreach (var origin in CorsOrigins)
            Require(origin == "*" || (Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.UserInfo.Length == 0 && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0 && !origin.EndsWith('/')), "CORS entries must be '*' or exact origins without trailing slash");
        foreach (var language in Locales) Require(language.Length <= 64 && language.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Invalid locale");
        if (MasterRoot.Length > 0) _ = MasterUri();
        if (ChartBaseUrl.Length > 0) _ = ChartBaseUri();
        if (VersionUrl.Length > 0) _ = VersionUri();
        Require(VersionPollSecs == 0 || VersionPollSecs is >= 60 and <= 86400, "version_poll_secs must be 0 or 60 to 86400");
        foreach (var name in Regions.Select(r => r.MetadataRegion).Append(MetadataRegion).OfType<string>())
            Require(name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Invalid metadata_region");
        Require(ChartBaseUrl.Length == 0 || (ChartBaseSha256.Length == 64 && ChartBaseSha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')), "chart_base_url needs chart_base_sha256 (64 lowercase hex digits)");
    }
    public Uri ChartBaseUri()
    {
        Require(Uri.TryCreate(ChartBaseUrl, UriKind.Absolute, out var uri), "Invalid chart_base_url");
        Require(uri!.UserInfo.Length == 0 && uri.Host.Length > 0 && (uri.Scheme == "https" || (AllowLoopbackHttp && uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]")), "HTTPS chart_base_url required");
        return uri;
    }
    public Uri VersionUri()
    {
        Require(Uri.TryCreate(VersionUrl, UriKind.Absolute, out var uri), "Invalid version_url");
        Require(uri!.UserInfo.Length == 0 && uri.Host.Length > 0 && (uri.Scheme == "https" || (AllowLoopbackHttp && uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]")), "HTTPS version_url required");
        return uri;
    }
    /// <summary>The region's entry name in the version_url document, or null when the region is not tracked.</summary>
    public string? MetadataRegionFor(string region)
    {
        var settings = Regions.FirstOrDefault(r => r.Id == region);
        var name = settings != null ? settings.MetadataRegion ?? region : MetadataRegion.Length > 0 ? MetadataRegion : region;
        return VersionUrl.Length == 0 || name.Length == 0 ? null : name;
    }
    public Uri MasterUri()
    {
        Require(!MasterRoot.Any(c => "\\%?#".Contains(c)), "Invalid master_root");
        Require(Uri.TryCreate(MasterRoot, UriKind.Absolute, out var uri), "Invalid master_root");
        Require(uri!.UserInfo.Length == 0 && uri.Host.Length > 0 && (uri.Scheme == "https" || (AllowLoopbackHttp && uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]")), "HTTPS master_root required");
        return uri;
    }
    public Config ForRegion(string? region = null, string? locale = null, string? version = null)
    {
        region ??= Region;
        var settings = Regions.FirstOrDefault(r => r.Id == region);
        if (Regions.Length > 0 && settings == null) throw new ApiException(400, "Unknown configured region");
        if (Regions.Length == 0 && region != Region) throw new ApiException(400, "Unknown configured region");
        var result = this with
        {
            Regions = [],
            Region = region,
            CdnRoot = settings?.CdnRoot ?? CdnRoot,
            Locale = locale ?? settings?.Locale ?? Locale,
            Locales = settings?.Locales ?? Locales,
            BiliVersion = version ?? settings?.BiliVersion ?? BiliVersion,
            CriKey = settings?.CriKey ?? CriKey
        };
        Require(result.Locales.Length == 0 || result.Locales.Contains(result.Locale, StringComparer.Ordinal), "Locale is not configured");
        // Components must be checked before interpolating any request-supplied locale/version in URLs.
        foreach (var part in new[] { result.Region, result.Locale, result.BiliVersion })
            Require(part.Length <= 64 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "Invalid configuration component");
        Require(result.Region.Length > 0 && result.BiliVersion.Length > 0, "Empty region/version");
        return result;
    }
    public Config ForSnapshot(Snapshot snapshot) => this with
    {
        Regions = [],
        Region = snapshot.Region,
        Locale = snapshot.Locale,
        Locales = [],
        BiliVersion = snapshot.BiliVersion,
        CdnRoot = snapshot.CdnRoot,
        CriKey = Regions.FirstOrDefault(r => r.Id == snapshot.Region)?.CriKey ?? CriKey
    };
    public Uri Root()
    {
        Require(!CdnRoot.Any(c => "\\%?#".Contains(c)), "Unsafe CDN root");
        Require(Uri.TryCreate(CdnRoot, UriKind.Absolute, out var uri), "Invalid CDN root");
        Require(uri!.UserInfo.Length == 0 && uri.Host.Length > 0, "Invalid CDN authority");
        Require(uri.Scheme == "https" || (AllowLoopbackHttp && uri.Scheme == "http" &&
            uri.Host is "127.0.0.1" or "[::1]"), "HTTPS CDN required");
        return uri;
    }
    public Uri CatalogUri(string extension)
    {
        Require(extension is "bin" or "hash", "Invalid catalog extension");
        var locale = Locale.Length == 0 ? "" : $"_{Locale}";
        return new Uri($"{Root().AbsoluteUri.TrimEnd('/')}/asset/Android/catalog_{BiliVersion}{locale}.{extension}");
    }
    public Uri AssetUri(string id)
    {
        Require(!id.Any(c => "\\%?#".Contains(c)) && !id.Split('/').Any(p => p is "." or ".."), "Unsafe asset URL");
        Require(Uri.TryCreate(id, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Unsupported local dependency");
        var at = uri!.AbsolutePath.IndexOf("/asset/Android/", StringComparison.Ordinal);
        Require(at >= 0, "Asset path missing");
        return new Uri(Root().AbsoluteUri.TrimEnd('/') + uri.AbsolutePath[at..]);
    }
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed record RegionSettings(string Id, string CdnRoot, string? Locale = null, string[]? Locales = null, string? BiliVersion = null, ulong? CriKey = null, string? MetadataRegion = null);
