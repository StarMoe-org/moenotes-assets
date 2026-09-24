using Microsoft.Extensions.Caching.Memory;
namespace MoenotesAssets;

// Path routes expose published files as /{locale}/{key}/{label}{extension}; export and file IDs stay internal.
public sealed partial class AssetService
{
    public sealed record PathEntry(string? Path, string File, string Label, string MediaType, long Bytes, string Sha256, object? Metadata);
    public sealed record PathListing(string Locale, string Key, string Snapshot, PathEntry[] Files);
    /// <summary>A key's newest published export: files by path name and its listing.</summary>
    public sealed record PathResolution(Manifest Manifest, IReadOnlyDictionary<string, PublishedFile> Files, PathListing Listing);
    // Resolutions (including misses) and scope snapshot lists carry the store versions they were read at and are
    // checked on every hit, so a publish or catalog refresh shows immediately without explicit removal.
    private sealed record Stamped<T>(T Value, long Generation, long Version);

    // Request paths hit memory, not SQLite: resolutions, snapshot lists and immutable file records are cached.
    // Size counts files (about 1-3 KB each with metadata), bounding the cache near 100-150 MB.
    private readonly MemoryCache pathCache = new(new MemoryCacheOptions { SizeLimit = 50_000 });
    private static MemoryCacheEntryOptions CacheEntry(int files = 0) => new() { Size = 1 + files, SlidingExpiration = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// The newest published export of a key in a configured locale of the default region. The current snapshot
    /// comes first, then older retained ones, so a catalog refresh does not hide files until its export finishes.
    /// </summary>
    public PathResolution? ResolvePath(string locale, string key)
    {
        var region = Config.ForRegion();
        var locales = region.Locales.Length == 0 ? [region.Locale] : region.Locales;
        if (key.Length == 0 || !locales.Contains(locale, StringComparer.Ordinal)) return null;
        // Read the versions before the database, so a concurrent publish leaves this entry stale rather than wrong.
        var generation = Store.SnapshotGeneration; var version = Store.ExportVersion(key);
        var cacheKey = ("path", locale, key);
        if (pathCache.TryGetValue(cacheKey, out Stamped<PathResolution?>? cached) && cached!.Generation == generation && cached.Version == version)
            return cached.Value;
        var candidates = ScopeSnapshots(region, locale, generation)
            .SelectMany(snapshot => new[] { Crypto.Identity(snapshot, key, Worker.Profile), Crypto.Identity(snapshot, key, Worker.MovieProfile) }).ToArray();
        var manifest = Store.FirstExport(candidates);
        var resolution = manifest == null ? null : new PathResolution(manifest, PathFiles(manifest), Listing(locale, manifest));
        pathCache.Set(cacheKey, new Stamped<PathResolution?>(resolution, generation, version), CacheEntry(manifest?.Files.Length ?? 0));
        return resolution;
    }

    private string[] ScopeSnapshots(Config region, string locale, long generation)
    {
        var cacheKey = ("scope", locale);
        if (pathCache.TryGetValue(cacheKey, out Stamped<string[]>? cached) && cached!.Generation == generation) return cached.Value;
        var snapshots = Store.ScopeSnapshots(region.Region, locale, region.BiliVersion);
        pathCache.Set(cacheKey, new Stamped<string[]>(snapshots, generation, 0), CacheEntry());
        return snapshots;
    }

    /// <summary>Published file records never change, so hits skip SQLite entirely; unknown IDs are not cached.</summary>
    public FileRecord? LookupFile(string id)
    {
        var cacheKey = ("file", id);
        if (pathCache.TryGetValue(cacheKey, out FileRecord? cached)) return cached;
        var record = Store.Find<FileRecord>("file", id);
        if (record != null) pathCache.Set(cacheKey, record, CacheEntry());
        return record;
    }

    /// <summary>A file's name under its key: source label plus the published extension, or null when it cannot be a path segment.</summary>
    public static string? PathName(PublishedFile file) => file.Label + Path.GetExtension(file.Name) is var name && SafeSegment(name) ? name : null;

    /// <summary>
    /// Every file's name under its key. A texture and a sprite cut from it can share a label: the first in export
    /// order owns `{label}{ext}` (as the old bucket and index chose), a later file with different content is
    /// `{label}__{seq}{ext}` after its published name (00003), and a later identical file shares the first name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PathNamesById(Manifest manifest)
    {
        var owners = new Dictionary<string, PublishedFile>(StringComparer.Ordinal); var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (PathName(file) is not { } name) continue;
            if (owners.TryGetValue(name, out var owner) && owner.Sha256 != file.Sha256)
                name = file.Label + "__" + Path.GetFileNameWithoutExtension(file.Name) + Path.GetExtension(file.Name);
            if (!SafeSegment(name) || (owners.TryGetValue(name, out owner) && owner.Sha256 != file.Sha256)) continue;
            owners.TryAdd(name, file); names[file.Id] = name;
        }
        return names;
    }

    /// <summary>Files by path name (see PathNamesById); identical duplicates resolve to the first.</summary>
    public static IReadOnlyDictionary<string, PublishedFile> PathFiles(Manifest manifest)
    {
        var names = PathNamesById(manifest); var files = new Dictionary<string, PublishedFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files) if (names.TryGetValue(file.Id, out var name)) files.TryAdd(name, file);
        return files;
    }

    public static PathListing Listing(string locale, Manifest manifest)
    {
        var names = PathNamesById(manifest); var addressable = manifest.Key.Split('/').All(SafeSegment);
        return new(locale, manifest.Key, manifest.Snapshot, manifest.Files.Select(file =>
        {
            var path = addressable && names.TryGetValue(file.Id, out var name) ? "/" + string.Join('/', new[] { locale }.Concat(manifest.Key.Split('/')).Append(name).Select(Uri.EscapeDataString)) : null;
            return new PathEntry(path, "/files/" + file.Id, file.Label, file.MediaType, file.Bytes, file.Sha256, file.Metadata);
        }).ToArray());
    }
}
