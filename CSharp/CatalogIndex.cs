using Microsoft.Data.Sqlite;
using System.Globalization;
namespace MoenotesAssets;

public sealed record BundleEntry(string Id, string Key, string BundleName, string Internal, string Provider, string ResourceType,
    string CatalogHash, long Crc, long Bytes, string? CandidateId, bool Remote, string? DownloadSha256, string? PlainSha256);
public sealed record CatalogStats(string Snapshot, string Region, string Locale, string Version, long Created, bool Current,
    string ContentSha256, long Bundles, long Assets, long DeclaredBytes, long RemoteBundles, long VerifiedBundles);
public sealed record AssetEntry(string Key, string ResourceType, string Internal, bool Ambiguous);
public sealed record BundlePage(string Snapshot, int Offset, int Limit, long Total, BundleEntry[] Bundles);
public sealed record AssetPage(string Snapshot, int Offset, int Limit, long Total, AssetEntry[] Assets);
public sealed record BundleDifference(string Key, string Change, string Evidence, string? FromId, string? ToId, long FromCount, long ToCount);
public sealed record DiffPage(string From, string To, string Kind, Dictionary<string, long> Summary, int Offset, int Limit, BundleDifference[] Entries);
public sealed record BundleEquivalent(string Snapshot, string Region, string Locale, string Id, string Key, string Evidence);

public static class BundleIdentity
{
    public static string Id(Location location) => Crypto.Identity(location.Internal, location.Provider, Json.Write(location.Options));
    public static string Key(Location location)
    {
        var options = location.Options!;
        var path = location.Internal.Replace('\\', '/');
        var marker = path.IndexOf("/asset/Android/", StringComparison.Ordinal);
        var name = marker >= 0 ? path[(marker + "/asset/Android/".Length)..] : path.Split('/').Last();
        if (name.Length == 0) name = options.BundleName;
        var extension = name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? ".bundle" : "";
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        var hash = options.Hash;
        if (hash.Length > 0 && stem.EndsWith("_" + hash, StringComparison.OrdinalIgnoreCase)) stem = stem[..^(hash.Length + 1)];
        return stem + extension;
    }
    public static string? Candidate(Location location)
    {
        var options = location.Options!;
        if (options.Hash.Length == 0 || options.Hash.All(c => c == '0')) return null;
        return Crypto.Identity(options.Hash.ToLowerInvariant(), options.Size.ToString(CultureInfo.InvariantCulture), options.Crc.ToString(CultureInfo.InvariantCulture),
            location.Provider == Catalog.Cri ? "cri" : location.Provider is Catalog.Crypt or Catalog.Plain ? "unity" : location.Provider);
    }
}

public sealed partial class Store
{
    private void InitializeIndex()
    {
        // Rebuild only derived browser tables. Snapshot binaries, exports, tasks and observations remain intact.
        Config.Require(Get<int>("setting", "browser_index_version") <= 3, "Browser index is newer than this service");
        var existing = Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bundles'") > 0;
        if (existing && Get<int>("setting", "browser_index_version") != 3)
        {
            using (var tx = connection.BeginTransaction())
            {
                using (var command = Command("SELECT id,region,locale,version FROM catalog_snapshots WHERE current=1", []))
                {
                    using var reader = command.ExecuteReader(); var selected = new List<(string Id, string Scope)>();
                    while (reader.Read()) selected.Add((reader.GetString(0), ScopeSetting(reader.GetString(1), reader.GetString(2), reader.GetString(3))));
                    reader.Close(); foreach (var item in selected) Put("setting", item.Scope, item.Id);
                }
                Execute("DROP TABLE IF EXISTS asset_locations;DROP TABLE IF EXISTS catalog_edges;DROP TABLE IF EXISTS catalog_locations;DROP TABLE IF EXISTS assets;DROP TABLE IF EXISTS bundles;DROP TABLE IF EXISTS catalog_snapshots;DROP TABLE IF EXISTS catalog_data;");
                tx.Commit();
            }
            Execute("VACUUM");
        }
        Execute("""
        CREATE TABLE IF NOT EXISTS catalog_data(
          content_id TEXT PRIMARY KEY, bundle_count INTEGER NOT NULL, asset_count INTEGER NOT NULL, declared_bytes INTEGER NOT NULL, remote_count INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_snapshots(
          id TEXT PRIMARY KEY, content_id TEXT NOT NULL, region TEXT NOT NULL, locale TEXT NOT NULL, version TEXT NOT NULL, created INTEGER NOT NULL, current INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_snapshots_region ON catalog_snapshots(region,locale,version,current,created);
        CREATE INDEX IF NOT EXISTS ix_snapshots_content ON catalog_snapshots(content_id,id);
        CREATE TABLE IF NOT EXISTS bundle_defs(
          bid INTEGER PRIMARY KEY,id TEXT NOT NULL UNIQUE,key TEXT NOT NULL,bundle_name TEXT NOT NULL,internal TEXT NOT NULL,provider TEXT NOT NULL,
          resource_type TEXT NOT NULL,catalog_hash TEXT NOT NULL,crc INTEGER NOT NULL,bytes INTEGER NOT NULL,candidate_id TEXT,remote INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_bundle_defs_key ON bundle_defs(key,bid);
        CREATE INDEX IF NOT EXISTS ix_bundle_defs_candidate ON bundle_defs(candidate_id,bid);
        CREATE TABLE IF NOT EXISTS dataset_bundles(content_id INTEGER NOT NULL,bid INTEGER NOT NULL,PRIMARY KEY(content_id,bid)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_dataset_bundles_reverse ON dataset_bundles(bid,content_id);
        CREATE VIEW IF NOT EXISTS bundles AS SELECT m.content_id,b.id,b.key,b.bundle_name,b.internal,b.provider,b.resource_type,b.catalog_hash,b.crc,b.bytes,b.candidate_id,b.remote FROM dataset_bundles m JOIN bundle_defs b ON b.bid=m.bid;
        CREATE TABLE IF NOT EXISTS catalog_locations(content_id INTEGER NOT NULL,id INTEGER NOT NULL,bundle_id TEXT,PRIMARY KEY(content_id,id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_locations_bundle ON catalog_locations(content_id,bundle_id,id);
        CREATE TABLE IF NOT EXISTS catalog_edges(content_id INTEGER NOT NULL,source INTEGER NOT NULL,target INTEGER NOT NULL,PRIMARY KEY(content_id,source,target)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_edges_reverse ON catalog_edges(content_id,target,source);
        CREATE TABLE IF NOT EXISTS asset_defs(aid INTEGER PRIMARY KEY,signature TEXT NOT NULL UNIQUE,key TEXT NOT NULL,resource_type TEXT NOT NULL,internal TEXT NOT NULL,ambiguous INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_asset_defs_key ON asset_defs(key,aid);
        CREATE INDEX IF NOT EXISTS ix_asset_defs_type ON asset_defs(resource_type,key,aid);
        CREATE TABLE IF NOT EXISTS dataset_assets(content_id INTEGER NOT NULL,aid INTEGER NOT NULL,PRIMARY KEY(content_id,aid)) WITHOUT ROWID;
        CREATE VIEW IF NOT EXISTS assets AS SELECT m.content_id,a.key,a.resource_type,a.internal,a.ambiguous FROM dataset_assets m JOIN asset_defs a ON a.aid=m.aid;
        CREATE TABLE IF NOT EXISTS asset_links(content_id INTEGER NOT NULL,aid INTEGER NOT NULL,location INTEGER NOT NULL,PRIMARY KEY(content_id,aid,location)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_asset_links_reverse ON asset_links(content_id,location,aid);
        CREATE VIEW IF NOT EXISTS asset_locations AS SELECT l.content_id,a.key,l.location FROM asset_links l JOIN asset_defs a ON a.aid=l.aid;
        CREATE TABLE IF NOT EXISTS observations(snapshot TEXT NOT NULL,bundle_id TEXT NOT NULL,download_sha256 TEXT NOT NULL,plain_sha256 TEXT NOT NULL,verified INTEGER NOT NULL,PRIMARY KEY(snapshot,bundle_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_observations_plain ON observations(plain_sha256,snapshot,bundle_id);
        """);
        Put("setting", "browser_index_version", 3);
    }
    private SqliteCommand Prepared(string sql, params string[] names)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var name in names) command.Parameters.AddWithValue(name, DBNull.Value);
        command.Prepare(); return command;
    }
    private static void Insert(SqliteCommand command, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
        command.ExecuteNonQuery();
    }
    private long Scalar(string sql, params (string Name, object Value)[] args)
    { using var command = Command(sql, args); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    public bool HasCatalogIndex(string snapshot)
    { lock (gate) return Scalar("SELECT COUNT(*) FROM catalog_snapshots WHERE id=$id", ("$id", snapshot)) > 0; }
    public bool HasCatalogContent(string content)
    { lock (gate) return Scalar("SELECT COUNT(*) FROM catalog_data WHERE content_id=$id", ("$id", content)) > 0; }
    public static string ScopeSetting(string region, string locale, string version) => "browser_current:" + Crypto.Identity(region, locale, version);
    private long ContentNumber(string sha) => Scalar("SELECT rowid FROM catalog_data WHERE content_id=$id", ("$id", sha));
    public void IndexSnapshot(Snapshot snapshot, Catalog? catalog, bool current = true)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            if (!HasCatalogContent(snapshot.ContentSha256))
            {
                Config.Require(catalog != null, "Catalog graph required for a new content digest");

                var bundleLocations = catalog!.Locations.Values.Where(l => l.Options != null).GroupBy(BundleIdentity.Id).Select(g => g.First()).ToArray();
                bool Remote(Location l) => Uri.TryCreate(l.Internal, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                Execute("INSERT INTO catalog_data VALUES($c,$b,$a,$bytes,$r)", ("$c", snapshot.ContentSha256), ("$b", bundleLocations.Length), ("$a", catalog.Keys.Count), ("$bytes", bundleLocations.Sum(l => l.Options!.Size)), ("$r", bundleLocations.Count(Remote)));
                var content = ContentNumber(snapshot.ContentSha256);
                using var bundle = Prepared("INSERT OR IGNORE INTO bundle_defs(id,key,bundle_name,internal,provider,resource_type,catalog_hash,crc,bytes,candidate_id,remote) VALUES($id,$key,$name,$internal,$provider,$type,$hash,$crc,$bytes,$candidate,$remote)", "$id", "$key", "$name", "$internal", "$provider", "$type", "$hash", "$crc", "$bytes", "$candidate", "$remote");
                using var bundleLink = Prepared("INSERT INTO dataset_bundles SELECT $c,bid FROM bundle_defs WHERE id=$id", "$c", "$id");
                foreach (var l in bundleLocations)
                {
                    var id = BundleIdentity.Id(l);
                    Insert(bundle, id, BundleIdentity.Key(l), l.Options!.BundleName, l.Internal, l.Provider, l.ResourceType, l.Options.Hash, l.Options.Crc, l.Options.Size, BundleIdentity.Candidate(l), Remote(l) ? 1 : 0);
                    Insert(bundleLink, content, id);
                }
                using var location = Prepared("INSERT INTO catalog_locations VALUES($c,$id,$bundle)", "$c", "$id", "$bundle");
                using var edge = Prepared("INSERT OR IGNORE INTO catalog_edges VALUES($c,$from,$to)", "$c", "$from", "$to");
                foreach (var l in catalog.Locations.Values)
                {
                    Insert(location, content, l.Id, l.Options == null ? null : BundleIdentity.Id(l));
                    foreach (var dependency in l.Dependencies) Insert(edge, content, l.Id, dependency);
                }
                using var asset = Prepared("INSERT OR IGNORE INTO asset_defs(signature,key,resource_type,internal,ambiguous) VALUES($sig,$key,$type,$internal,$ambiguous)", "$sig", "$key", "$type", "$internal", "$ambiguous");
                using var assetLink = Prepared("INSERT INTO dataset_assets SELECT $c,aid FROM asset_defs WHERE signature=$sig", "$c", "$sig");
                using var link = Prepared("INSERT OR IGNORE INTO asset_links SELECT $c,aid,$location FROM asset_defs WHERE signature=$sig", "$c", "$location", "$sig");
                foreach (var (key, ids) in catalog.Keys)
                {
                    Location? target = null; var ambiguous = 0;
                    try { target = catalog.Target(key); } catch (InvalidDataException) { ambiguous = 1; if (ids.Count > 0) target = catalog.Locations[ids[0]]; }
                    var signature = Crypto.Identity(key, target?.ResourceType ?? "", target?.Internal ?? "", ambiguous.ToString(CultureInfo.InvariantCulture));
                    Insert(asset, signature, key, target?.ResourceType ?? "", target?.Internal ?? "", ambiguous); Insert(assetLink, content, signature);
                    foreach (var id in ids) Insert(link, content, id, signature);
                }

            }
            if (Get<Snapshot>("snapshot", snapshot.Id) == null) Put("snapshot", snapshot.Id, snapshot);
            Execute("INSERT OR IGNORE INTO catalog_snapshots VALUES($id,$content,$region,$locale,$version,$created,0)", ("$id", snapshot.Id), ("$content", snapshot.ContentSha256), ("$region", snapshot.Region), ("$locale", snapshot.Locale), ("$version", snapshot.BiliVersion), ("$created", snapshot.Created));
            if (current)
            {
                Execute("UPDATE catalog_snapshots SET current=0 WHERE region=$r AND locale=$l AND version=$v", ("$r", snapshot.Region), ("$l", snapshot.Locale), ("$v", snapshot.BiliVersion));
                Execute("UPDATE catalog_snapshots SET current=1 WHERE id=$id", ("$id", snapshot.Id));
                Put("setting", ScopeSetting(snapshot.Region, snapshot.Locale, snapshot.BiliVersion), snapshot.Id);
                Put("setting", "current", snapshot.Id); // compatibility only; selection uses the scoped pointer above.
            }
            transaction.Commit();
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
    }
    public string? CurrentSnapshot(string region, string locale, string version)
    {
        lock (gate) { using var command = Command("SELECT id FROM catalog_snapshots WHERE region=$r AND locale=$l AND version=$v AND current=1", [("$r", region), ("$l", locale), ("$v", version)]); return command.ExecuteScalar() as string; }
    }
    public Snapshot RequireSnapshot(string id) => Get<Snapshot>("snapshot", id) ?? throw new ApiException(404, "Catalog snapshot not found");
    public CatalogStats[] Catalogs(string? region = null, string? locale = null)
    {
        lock (gate)
        {
            using var command = Command("""
                SELECT s.id,s.region,s.locale,s.version,s.created,s.current,s.content_id,d.bundle_count,d.asset_count,d.declared_bytes,d.remote_count,
                (SELECT COUNT(*) FROM observations o WHERE o.snapshot=s.id)
                FROM catalog_snapshots s JOIN catalog_data d ON d.content_id=s.content_id
                WHERE ($r='' OR s.region=$r) AND ($l='' OR s.locale=$l) ORDER BY s.created DESC,s.id
                """, [("$r", region ?? ""), ("$l", locale ?? "")]);
            using var reader = command.ExecuteReader(); var list = new List<CatalogStats>();
            while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5) != 0, reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11)));
            return list.ToArray();
        }
    }
    private const string BundleColumns = "b.id,b.key,b.bundle_name,b.internal,b.provider,b.resource_type,b.catalog_hash,b.crc,b.bytes,b.candidate_id,b.remote,o.download_sha256,o.plain_sha256";
    private static BundleEntry ReadBundle(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10) != 0, r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12));
    private static int PageLimit(int offset, int limit) { Config.Require(offset >= 0 && offset <= 10000000 && limit >= 0, "Invalid pagination"); return Math.Min(limit, 1000); }
    public BundlePage Bundles(string snapshot, string? prefix, int offset, int limit)
    {
        limit = PageLimit(offset, limit); var s = RequireSnapshot(snapshot);
        lock (gate)
        {
            var filter = "b.content_id=$c AND b.key >= $prefix AND substr(b.key,1,length($prefix))=$prefix";
            var total = Scalar("SELECT COUNT(*) FROM bundles b WHERE " + filter, ("$c", ContentNumber(s.ContentSha256)), ("$prefix", prefix ?? ""));
            using var command = Command($"SELECT {BundleColumns} FROM bundles b LEFT JOIN observations o ON o.snapshot=$s AND o.bundle_id=b.id WHERE {filter} ORDER BY b.key,b.id LIMIT $limit OFFSET $offset", [("$s", snapshot), ("$c", ContentNumber(s.ContentSha256)), ("$prefix", prefix ?? ""), ("$limit", limit), ("$offset", offset)]);
            using var reader = command.ExecuteReader(); var rows = new List<BundleEntry>(); while (reader.Read()) rows.Add(ReadBundle(reader)); return new(snapshot, offset, limit, total, rows.ToArray());
        }
    }
    public BundleEntry Bundle(string snapshot, string id)
    {
        var s = RequireSnapshot(snapshot); lock (gate)
        {
            using var command = Command($"SELECT {BundleColumns} FROM bundles b LEFT JOIN observations o ON o.snapshot=$s AND o.bundle_id=b.id WHERE b.content_id=$c AND b.id=$id", [("$s", snapshot), ("$c", ContentNumber(s.ContentSha256)), ("$id", id)]);
            using var r = command.ExecuteReader(); if (!r.Read()) throw new ApiException(404, "Bundle not found in snapshot"); return ReadBundle(r);
        }
    }
    public Location BundleLocation(string snapshot, string id)
    {
        var s = RequireSnapshot(snapshot); var b = Bundle(snapshot, id);
        lock (gate)
        {
            using var command = Command("SELECT id FROM catalog_locations WHERE content_id=$c AND bundle_id=$id ORDER BY id LIMIT 1", [("$c", ContentNumber(s.ContentSha256)), ("$id", id)]);
            var value = command.ExecuteScalar(); if (value == null) throw new ApiException(404, "Bundle not found");
            return new(Convert.ToUInt32(value, CultureInfo.InvariantCulture), b.Key, b.Internal, b.Provider, b.ResourceType, [], new(b.CatalogHash, b.BundleName, (uint)b.Crc, b.Bytes));
        }
    }
    public AssetPage Assets(string snapshot, string? prefix, string? type, string? bundleId, int offset, int limit)
    {
        limit = PageLimit(offset, limit); var s = RequireSnapshot(snapshot);
        if (bundleId != null) _ = Bundle(snapshot, bundleId);
        lock (gate)
        {
            var cte = bundleId == null ? "" : """
                WITH RECURSIVE dependents(id) AS (
                  SELECT id FROM catalog_locations WHERE content_id=$c AND bundle_id=$bundle
                  UNION SELECT e.source FROM catalog_edges e JOIN dependents d ON e.target=d.id WHERE e.content_id=$c
                ), selected(key) AS (SELECT DISTINCT a.key FROM asset_locations a JOIN dependents d ON d.id=a.location WHERE a.content_id=$c)
                """;
            var filter = "a.content_id=$c AND a.key >= $prefix AND substr(a.key,1,length($prefix))=$prefix AND ($type='' OR a.resource_type=$type)" + (bundleId == null ? "" : " AND a.key IN (SELECT key FROM selected)");
            var parameters = new[] { ("$c", (object)ContentNumber(s.ContentSha256)), ("$prefix", (object)(prefix ?? "")), ("$type", (object)(type ?? "")), ("$bundle", (object)(bundleId ?? "")), ("$limit", (object)limit), ("$offset", (object)offset) };
            var total = Scalar(cte + "SELECT COUNT(*) FROM assets a WHERE " + filter, parameters);
            using var command = Command(cte + "SELECT a.key,a.resource_type,a.internal,a.ambiguous FROM assets a WHERE " + filter + " ORDER BY a.key LIMIT $limit OFFSET $offset", parameters);
            using var reader = command.ExecuteReader(); var rows = new List<AssetEntry>(); while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0)); return new(snapshot, offset, limit, total, rows.ToArray());
        }
    }
    public void Observe(string snapshot, Location location, string raw, string plain)
    {
        lock (gate) Execute("INSERT INTO observations VALUES($s,$id,$raw,$plain,$at) ON CONFLICT(snapshot,bundle_id) DO UPDATE SET download_sha256=excluded.download_sha256,plain_sha256=excluded.plain_sha256,verified=excluded.verified",
            ("$s", snapshot), ("$id", BundleIdentity.Id(location)), ("$raw", raw), ("$plain", plain), ("$at", AssetService.Now));
    }
    public BundleEquivalent[] Equivalents(string snapshot, string id, int limit = 100)
    {
        limit = PageLimit(0, limit); var bundle = Bundle(snapshot, id);
        lock (gate)
        {
            using var command = Command("""
                SELECT s.id,s.region,s.locale,b.id,b.key,
                  CASE WHEN o.plain_sha256=$plain AND $plain<>'' THEN 'verified_plain_sha256' WHEN o.plain_sha256 IS NOT NULL AND $plain<>'' THEN 'conflicting_plain_sha256' ELSE 'catalog_candidate' END
                FROM bundles b JOIN catalog_data d ON d.rowid=b.content_id JOIN catalog_snapshots s ON s.content_id=d.content_id
                LEFT JOIN observations o ON o.snapshot=s.id AND o.bundle_id=b.id
                WHERE ((b.candidate_id=$candidate AND $candidate<>'') OR (o.plain_sha256=$plain AND $plain<>''))
                  AND NOT(s.id=$s AND b.id=$id)
                ORDER BY s.region,s.locale,s.id,b.id LIMIT $limit
                """, [("$plain", bundle.PlainSha256 ?? ""), ("$candidate", bundle.CandidateId ?? ""), ("$s", snapshot), ("$id", id), ("$limit", limit)]);
            using var reader = command.ExecuteReader(); var rows = new List<BundleEquivalent>(); while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))); return rows.ToArray();
        }
    }
    public object StorageStats(long reserved)
    {
        lock (gate)
        {
            var snapshots = Scalar("SELECT COUNT(*) FROM catalog_snapshots"); var datasets = Scalar("SELECT COUNT(*) FROM catalog_data");
            var bundles = Scalar("SELECT COUNT(*) FROM bundles"); var logical = Scalar("SELECT COALESCE(SUM(json_extract(body,'$.file.bytes')),0) FROM records WHERE kind='file'");
            var unique = Scalar("SELECT COALESCE(SUM(bytes),0) FROM (SELECT json_extract(body,'$.blob_sha256') sha,MAX(json_extract(body,'$.file.bytes')) bytes FROM records WHERE kind='file' AND json_extract(body,'$.blob_sha256') IS NOT NULL GROUP BY sha)");
            var legacy = Scalar("SELECT COALESCE(SUM(json_extract(body,'$.file.bytes')),0) FROM records WHERE kind='file' AND json_extract(body,'$.blob_sha256') IS NULL");
            var database = Scalar("PRAGMA page_count") * Scalar("PRAGMA page_size");
            var bundleDefinitions = Scalar("SELECT COUNT(*) FROM bundle_defs"); var assetDefinitions = Scalar("SELECT COUNT(*) FROM asset_defs");
            return new { snapshots, unique_bundle_definitions = bundleDefinitions, unique_asset_definitions = assetDefinitions, unique_catalogs = datasets, indexed_bundle_rows = bundles, sqlite_main_bytes = database, logical_output_bytes = logical, referenced_output_bytes = unique + legacy, deduplicated_output_bytes = logical - unique - legacy, reserved_temp_bytes = reserved };
        }
    }
    public DiffPage Diff(string from, string to, string? prefix, int offset, int limit, bool unchanged = false)
    {
        limit = PageLimit(offset, limit); var left = RequireSnapshot(from); var right = RequireSnapshot(to);
        const string cte = """
            WITH l AS (
              SELECT b.key,COUNT(*) n,MIN(b.id) id,MIN(b.candidate_id) candidate,MIN(o.plain_sha256) plain
              FROM bundles b LEFT JOIN observations o ON o.snapshot=$from AND o.bundle_id=b.id
              WHERE b.content_id=$lc GROUP BY b.key
            ), r AS (
              SELECT b.key,COUNT(*) n,MIN(b.id) id,MIN(b.candidate_id) candidate,MIN(o.plain_sha256) plain
              FROM bundles b LEFT JOIN observations o ON o.snapshot=$to AND o.bundle_id=b.id
              WHERE b.content_id=$rc GROUP BY b.key
            ), keys AS (SELECT key FROM l UNION SELECT key FROM r), changes AS (
              SELECT k.key,l.id from_id,r.id to_id,coalesce(l.n,0) from_count,coalesce(r.n,0) to_count,
              CASE WHEN l.n IS NULL THEN 'added' WHEN r.n IS NULL THEN 'removed'
                WHEN l.n>1 OR r.n>1 THEN 'ambiguous'
                WHEN l.plain IS NOT NULL AND r.plain IS NOT NULL THEN CASE WHEN l.plain=r.plain THEN 'unchanged' ELSE 'changed' END
                WHEN l.candidate IS NOT NULL AND r.candidate IS NOT NULL THEN CASE WHEN l.candidate=r.candidate THEN 'unchanged' ELSE 'changed' END
                ELSE 'unknown' END change,
              CASE WHEN l.n=1 AND r.n=1 AND l.plain IS NOT NULL AND r.plain IS NOT NULL THEN 'verified_plain_sha256'
                WHEN l.n=1 AND r.n=1 AND l.candidate IS NOT NULL AND r.candidate IS NOT NULL THEN 'catalog_metadata' ELSE 'inventory' END evidence
              FROM keys k LEFT JOIN l ON l.key=k.key LEFT JOIN r ON r.key=k.key
              WHERE k.key >= $prefix AND substr(k.key,1,length($prefix))=$prefix
            )
            """;
        lock (gate)
        {
            var args = new[] { ("$from", (object)from), ("$to", (object)to), ("$lc", (object)ContentNumber(left.ContentSha256)), ("$rc", (object)ContentNumber(right.ContentSha256)), ("$prefix", (object)(prefix ?? "")), ("$unchanged", (object)(unchanged ? 1 : 0)), ("$limit", (object)limit), ("$offset", (object)offset) };
            var summary = new Dictionary<string, long> { { "added", 0 }, { "removed", 0 }, { "changed", 0 }, { "unchanged", 0 }, { "ambiguous", 0 }, { "unknown", 0 } };
            using (var command = Command(cte + " SELECT change,COUNT(*) FROM changes GROUP BY change", args))
            using (var reader = command.ExecuteReader()) while (reader.Read()) summary[reader.GetString(0)] = reader.GetInt64(1);
            using var page = Command(cte + " SELECT key,change,evidence,from_id,to_id,from_count,to_count FROM changes WHERE ($unchanged=1 OR change<>'unchanged') ORDER BY key LIMIT $limit OFFSET $offset", args);
            using var r = page.ExecuteReader(); var entries = new List<BundleDifference>(); while (r.Read()) entries.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
            return new(from, to, left.Region == right.Region ? (left.Locale == right.Locale ? "version" : "locale") : "region", summary, offset, limit, entries.ToArray());
        }
    }
}
