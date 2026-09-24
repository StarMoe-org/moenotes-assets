using Microsoft.Data.Sqlite;

namespace MoenotesAssets;

public sealed record BundleContentItem(string Path, string Kind, string Source, long? PathId);
public sealed record BundleContentEntry(string Path, string Kind, string Source, string? PathId, string BundleId, string BundleKey);
public sealed record BundleContentPage(string Snapshot, int Offset, int Limit, long Total, BundleContentEntry[] Contents);
public sealed record BundleBrowseFolder(string Path, string Name);
public sealed record BundleBrowsePage(string Snapshot, string Directory, string Query, int Offset, int Limit, long Total, BundleBrowseFolder[] Folders, BundleContentEntry[] Files);
public sealed record BundleScanStatus(string Snapshot, long Total, long Scanned, long Entries, long LocalBundles, long Failed);
public sealed record BundleScanTarget(string Snapshot, string BundleId);
public sealed record BundleScanResult(BundleContentItem[] Entries, string? Error);

public sealed partial class Store
{
    public BundleScanTarget[] PendingBundleScans()
    {
        lock (gate)
        {
            using var command = Command("""
                SELECT MIN(s.id),b.id FROM bundles b
                JOIN catalog_data d ON d.rowid=b.content_id
                JOIN catalog_snapshots s ON s.content_id=d.content_id
                LEFT JOIN bundle_scans x ON x.bundle_id=b.id
                WHERE b.remote=1 AND x.bundle_id IS NULL
                GROUP BY b.id
                ORDER BY MIN(CASE WHEN b.provider=$cri THEN 1 ELSE 0 END),MIN(b.bytes),b.id
                """, [("$cri", Catalog.Cri)]);
            using var reader = command.ExecuteReader(); var rows = new List<BundleScanTarget>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1)));
            return rows.ToArray();
        }
    }

    public void SaveBundleScan(string bundleId, string plainSha256, BundleContentItem[] entries)
    {
        Config.Require(entries.Length <= 100000, "Bundle content limit");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute("DELETE FROM bundle_contents WHERE bundle_id=$id", ("$id", bundleId));
            Execute("DELETE FROM bundle_directories WHERE bundle_id=$id", ("$id", bundleId));
            using var insert = Prepared("INSERT INTO bundle_contents VALUES($id,$ordinal,$path,$parent,$kind,$source,$path_id)", "$id", "$ordinal", "$path", "$parent", "$kind", "$source", "$path_id");
            using var insertDirectory = Prepared("INSERT OR IGNORE INTO bundle_directories VALUES($id,$path,$parent)", "$id", "$path", "$parent");
            var directories = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                Config.Require(entry.Path.Length is > 0 and <= 4096 && entry.Source.Length <= 1024 && entry.Kind is "asset" or "unity_file" or "payload", "Invalid bundle content entry");
                var parent = Parent(entry.Path);
                Insert(insert, bundleId, i, entry.Path, parent, entry.Kind, entry.Source, entry.PathId);
                if (entry.Kind == "unity_file") continue;
                for (var directory = parent; directory.Length > 0; directory = Parent(directory))
                    if (directories.Add(directory)) Insert(insertDirectory, bundleId, directory, Parent(directory));
            }
            Execute("INSERT INTO bundle_scans VALUES($id,$sha,$at,$count) ON CONFLICT(bundle_id) DO UPDATE SET plain_sha256=excluded.plain_sha256,scanned=excluded.scanned,entry_count=excluded.entry_count",
                ("$id", bundleId), ("$sha", plainSha256), ("$at", AssetService.Now), ("$count", entries.Length));
            Execute("DELETE FROM bundle_scan_failures WHERE bundle_id=$id", ("$id", bundleId));
            transaction.Commit();
        }
    }

    public void RecordBundleScanFailure(string bundleId, string error) => Execute(
        "INSERT INTO bundle_scan_failures VALUES($id,$error,$at) ON CONFLICT(bundle_id) DO UPDATE SET error=excluded.error,updated=excluded.updated",
        ("$id", bundleId), ("$error", error[..Math.Min(1000, error.Length)]), ("$at", AssetService.Now));

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public BundleBrowsePage Browse(string snapshot, string? directory, string? query, int offset, int limit, bool descending = false)
    {
        limit = PageLimit(offset, limit); var s = RequireSnapshot(snapshot);
        directory ??= ""; query = query?.Trim() ?? "";
        Config.Require(directory.Length <= 4096 && query.Length <= 256 && !directory.Contains('\\') && !directory.StartsWith('/') && !directory.EndsWith('/') && !directory.Split('/').Any(p => p is "." or ".."), "Invalid browser path");
        lock (gate)
        {
            var content = ContentNumber(s.ContentSha256);
            var folders = new List<BundleBrowseFolder>();
            if (query.Length == 0)
            {
                using var dirs = Command("SELECT DISTINCT d.path FROM bundle_directories d JOIN bundles b ON b.id=d.bundle_id WHERE b.content_id=$c AND d.parent=$parent ORDER BY d.path", [("$c", content), ("$parent", directory)]);
                using var reader = dirs.ExecuteReader();
                while (reader.Read()) { var path = reader.GetString(0); folders.Add(new(path, path[(directory.Length == 0 ? 0 : directory.Length + 1)..])); }
            }
            var filter = "b.content_id=$c AND c.kind<>'unity_file' AND ($query='' AND c.parent=$parent OR $query<>'' AND instr(lower(c.path),lower($query))>0 AND ($prefix='' OR c.path >= $prefix AND substr(c.path,1,length($prefix))=$prefix))";
            var args = new[] { ("$c", (object)content), ("$parent", (object)directory), ("$prefix", (object)(directory.Length == 0 ? "" : directory + "/")), ("$query", (object)query), ("$limit", (object)limit), ("$offset", (object)offset) };
            var total = Scalar("SELECT COUNT(*) FROM bundle_contents c JOIN bundles b ON b.id=c.bundle_id WHERE " + filter, args);
            var order = descending ? "DESC" : "ASC";
            using var command = Command("SELECT c.path,c.kind,c.source,c.path_id,b.id,b.key FROM bundle_contents c JOIN bundles b ON b.id=c.bundle_id WHERE " + filter + $" ORDER BY c.path {order},b.key,b.id,c.ordinal LIMIT $limit OFFSET $offset", args);
            using var rows = command.ExecuteReader(); var files = new List<BundleContentEntry>();
            while (rows.Read()) files.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture), rows.GetString(4), rows.GetString(5)));
            if (descending) folders.Reverse();
            return new(snapshot, directory, query, offset, limit, total, folders.ToArray(), files.ToArray());
        }
    }

    public BundleScanStatus ScanStatus(string snapshot)
    {
        var s = RequireSnapshot(snapshot);
        lock (gate)
        {
            var content = ContentNumber(s.ContentSha256);
            using var command = Command("""
                SELECT COUNT(*),COUNT(x.bundle_id),COALESCE(SUM(x.entry_count),0),
                  (SELECT COUNT(*) FROM bundles local WHERE local.content_id=$c AND local.remote=0),
                  (SELECT COUNT(*) FROM bundles failedBundle JOIN bundle_scan_failures f ON f.bundle_id=failedBundle.id WHERE failedBundle.content_id=$c AND failedBundle.remote=1)
                FROM bundles b LEFT JOIN bundle_scans x ON x.bundle_id=b.id
                WHERE b.content_id=$c AND b.remote=1
                """, [("$c", content)]);
            using var reader = command.ExecuteReader(); reader.Read();
            return new(snapshot, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }
    }

    public BundleContentPage Contents(string snapshot, string? prefix, string? bundleId, int offset, int limit, bool includeOuter = false)
    {
        limit = PageLimit(offset, limit); var s = RequireSnapshot(snapshot);
        if (bundleId != null) _ = Bundle(snapshot, bundleId);
        lock (gate)
        {
            var content = ContentNumber(s.ContentSha256);
            var filter = "b.content_id=$c AND c.path >= $prefix AND substr(c.path,1,length($prefix))=$prefix AND ($bundle='' OR b.id=$bundle) AND ($outer=1 OR c.kind<>'unity_file')";
            var args = new[] { ("$c", (object)content), ("$prefix", (object)(prefix ?? "")), ("$bundle", (object)(bundleId ?? "")), ("$outer", (object)(includeOuter ? 1 : 0)), ("$limit", (object)limit), ("$offset", (object)offset) };
            var total = Scalar("SELECT COUNT(*) FROM bundle_contents c JOIN bundles b ON b.id=c.bundle_id WHERE " + filter, args);
            using var command = Command("SELECT c.path,c.kind,c.source,c.path_id,b.id,b.key FROM bundle_contents c JOIN bundles b ON b.id=c.bundle_id WHERE " + filter + " ORDER BY c.path,b.key,b.id,c.ordinal LIMIT $limit OFFSET $offset", args);
            using var reader = command.ExecuteReader(); var rows = new List<BundleContentEntry>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture), reader.GetString(4), reader.GetString(5)));
            return new(snapshot, offset, limit, total, rows.ToArray());
        }
    }
}
