namespace MoenotesAssets;

public static class ExportSelection
{
    public static bool Supported(Location target) => target.Provider == Catalog.Cri || target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal)
        || target.ResourceType is "UnityEngine.TextAsset" or "UnityEngine.Texture2D" or "UnityEngine.Sprite" or "UnityEngine.U2D.SpriteAtlas" or Worker.SplitAcbType;

    public static string[] UniqueKeys(Catalog catalog, IEnumerable<string> keys)
    {
        // GUIDs, addresses and labels can point at the same object. Keep one readable
        // alias per exact target, but retain ambiguous labels as explicit skipped items.
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            Location target;
            try { target = catalog.Target(key); }
            catch (InvalidDataException) { selected["ambiguous:" + key] = key; continue; }
            var id = Crypto.Identity(target.Internal, target.Provider, target.ResourceType, string.Join(',', target.Dependencies));
            if (!selected.TryGetValue(id, out var old) || (key == target.Key && old != target.Key) || (!old.Contains('/') && key.Contains('/'))) selected[id] = key;
        }
        return selected.Values.Order(StringComparer.Ordinal).ToArray();
    }

    public static string? SkipReason(Catalog catalog, string key)
    {
        Location target;
        try { target = catalog.Target(key); }
        catch (InvalidDataException e) when (e.Message == "Ambiguous asset key") { return e.Message; }
        if (!Supported(target)) return $"Unsupported export type: {target.ResourceType}";
        var dependencies = Dependencies(catalog, key);
        if (dependencies.Any(l => l.Provider == Catalog.Cri) && (target.Provider == Catalog.Cri || target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal))) dependencies = dependencies.Where(l => l.Provider == Catalog.Cri).ToArray();
        var local = dependencies.FirstOrDefault(l => !Uri.TryCreate(l.Internal, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"));
        return local == null ? null : $"Unsupported local dependency: {local.Internal}";
    }

    public static Location[] Dependencies(Catalog catalog, string key)
    {
        var target = catalog.Target(key);
        var locations = catalog.Closure(key);
        // Data exports do not execute scripts. A script metadata bundle can be an
        // Addressables dependency of otherwise self-contained textures/text.
        // Keep all other dependencies; unresolved data references still fail in Worker.
        return Supported(target) ? locations.Where(l => l.Internal != "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/Android/shared_monoscripts.bundle").ToArray() : locations;
    }
}
