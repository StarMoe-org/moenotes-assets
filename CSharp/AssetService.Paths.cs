namespace MoenotesAssets;

// Path routes expose published files as /{locale}/{key}/{label}{extension}; export and file IDs stay internal.
public sealed partial class AssetService
{
    public sealed record PathEntry(string? Path, string File, string Label, string MediaType, long Bytes, string Sha256, object? Metadata);
    public sealed record PathListing(string Locale, string Key, string Snapshot, PathEntry[] Files);

    /// <summary>
    /// The newest published export of a key in a configured locale of the default region. The current snapshot
    /// comes first, then older retained ones, so a catalog refresh does not hide files until its export finishes.
    /// </summary>
    public Manifest? PublishedExport(string locale, string key)
    {
        var region = Config.ForRegion();
        var locales = region.Locales.Length == 0 ? [region.Locale] : region.Locales;
        if (key.Length == 0 || !locales.Contains(locale, StringComparer.Ordinal)) return null;
        foreach (var snapshot in Store.ScopeSnapshots(region.Region, locale, region.BiliVersion))
            foreach (var profile in new[] { Worker.Profile, Worker.MovieProfile })
                if (Store.Get<Manifest>("export", Crypto.Identity(snapshot, key, profile)) is { } manifest) return manifest;
        return null;
    }

    /// <summary>A file's name under its key: source label plus the published extension, or null when a label cannot be a path segment.</summary>
    public static string? PathName(PublishedFile file) =>
        file.Label.Length == 0 || file.Label.Contains('/') || file.Label is "." or ".." ? null : file.Label + Path.GetExtension(file.Name);

    /// <summary>Files named <paramref name="name"/>; several with different content make the name ambiguous.</summary>
    public static PublishedFile[] PathMatches(Manifest manifest, string name) => manifest.Files.Where(f => PathName(f) == name).ToArray();

    public static PathListing Listing(string locale, Manifest manifest) => new(locale, manifest.Key, manifest.Snapshot, manifest.Files.Select(file =>
    {
        var name = PathName(file);
        var unique = name != null && PathMatches(manifest, name).All(f => f.Sha256 == file.Sha256);
        var path = unique ? "/" + string.Join('/', new[] { locale }.Concat(manifest.Key.Split('/')).Append(name!).Select(Uri.EscapeDataString)) : null;
        return new PathEntry(path, "/files/" + file.Id, file.Label, file.MediaType, file.Bytes, file.Sha256, file.Metadata);
    }).ToArray());
}
