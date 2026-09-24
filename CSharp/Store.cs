using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
namespace MoenotesAssets;

public sealed partial class Store : IDisposable
{
    // One writer connection serialized by gate; request paths read on pooled read-only connections instead (see Read).
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private readonly string readerConnectionString;
    private readonly ConcurrentBag<SqliteConnection> readers = new();
    private readonly int maxIdleReaders = Math.Max(4, Environment.ProcessorCount * 2);
    private volatile bool disposed;
    // Versions let caches validate entries: a publish bumps its key, a catalog index bumps the generation.
    private readonly ConcurrentDictionary<string, long> exportVersions = new(StringComparer.Ordinal);
    private long snapshotGeneration;
    public long SnapshotGeneration => Interlocked.Read(ref snapshotGeneration);
    public long ExportVersion(string key) => exportVersions.GetValueOrDefault(key);
    public Store(string directory)
    {
        var path = Path.Combine(directory, "csharp.sqlite");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        readerConnectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL,id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(kind,id));");
        InitializeIndex();
    }
    public void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        lock (gate) { using var c = Command(sql, parameters); c.ExecuteNonQuery(); }
    }
    private SqliteCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    /// <summary>Reads on the writer connection, so it sees the caller's uncommitted writes inside a transaction.</summary>
    public T? Get<T>(string kind, string id)
    {
        string? text;
        lock (gate)
        {
            using var command = Command("SELECT body FROM records WHERE kind=$kind AND id=$id", [("$kind", kind), ("$id", id)]);
            text = command.ExecuteScalar() as string;
        }
        // Parse after releasing gate: other callers wait for SQLite, not for JSON.
        return text != null ? Json.Read<T>(text) : default;
    }
    /// <summary>
    /// Runs a query on a pooled read-only connection. WAL readers see committed state, run concurrently and are
    /// not blocked by the writer, so request paths never wait on gate. Use Get inside write transactions.
    /// </summary>
    public T Read<T>(Func<SqliteConnection, T> query)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!readers.TryTake(out var reader)) { reader = new SqliteConnection(readerConnectionString); reader.Open(); }
        try { return query(reader); }
        finally { if (disposed || readers.Count >= maxIdleReaders) reader.Dispose(); else readers.Add(reader); }
    }
    /// <summary>A committed record read concurrently (see Read), parsed straight from its UTF-8 bytes.</summary>
    public T? Find<T>(string kind, string id) => Read(c =>
    {
        using var command = c.CreateCommand(); command.CommandText = "SELECT body FROM records WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Json.Read<T>(reader.GetFieldValue<byte[]>(0)) : default;
    });
    /// <summary>The first of <paramref name="ids"/>, in order, that is a published export; one query for all candidates.</summary>
    public Manifest? FirstExport(IReadOnlyList<string> ids) => ids.Count == 0 ? null : Read(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = $"SELECT id, body FROM records WHERE kind='export' AND id IN ({string.Join(',', ids.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue("$p" + i, ids[i]);
        using var reader = command.ExecuteReader(); var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read()) found[reader.GetString(0)] = reader.GetFieldValue<byte[]>(1);
        var first = ids.FirstOrDefault(found.ContainsKey);
        return first == null ? null : Json.Read<Manifest>(found[first]);
    });
    public T[] All<T>(string kind)
    {
        lock (gate)
        {
            using var command = Command("SELECT body FROM records WHERE kind=$kind ORDER BY id", [("$kind", kind)]);
            using var reader = command.ExecuteReader(); var values = new List<T>();
            while (reader.Read()) values.Add(Json.Read<T>(reader.GetString(0)));
            return values.ToArray();
        }
    }
    /// <summary>Record IDs of one kind, without deserializing bodies.</summary>
    public HashSet<string> Ids(string kind) => Strings("SELECT id FROM records WHERE kind=$kind", ("$kind", kind));
    /// <summary>Blob hashes referenced by published files, read in SQL so startup does not parse every file record.</summary>
    public HashSet<string> ReferencedBlobs() =>
        Strings("SELECT json_extract(body,'$.blob_sha256') FROM records WHERE kind='file' AND json_extract(body,'$.blob_sha256') IS NOT NULL");
    public TaskInfo[] UnfinishedTasks()
    {
        lock (gate)
        {
            using var command = Command("SELECT body FROM records WHERE kind='task' AND json_extract(body,'$.state') IN ('queued','running')", []);
            using var reader = command.ExecuteReader(); var values = new List<TaskInfo>();
            while (reader.Read()) values.Add(Json.Read<TaskInfo>(reader.GetString(0)));
            return values.ToArray();
        }
    }
    private HashSet<string> Strings(string sql, params (string Name, object Value)[] parameters)
    {
        lock (gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader(); var values = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) values.Add(reader.GetString(0));
            return values;
        }
    }
    public void Put<T>(string kind, string id, T value) => Execute("INSERT INTO records VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body",
        ("$kind", kind), ("$id", id), ("$body", Json.Write(value)));
    public void Publish(Manifest manifest, bool contentAddressed = false)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Put("export", manifest.Id, manifest);
            foreach (var file in manifest.Files) Put("file", file.Id, new FileRecord(manifest.Id, file, contentAddressed ? file.Sha256 : null));
            transaction.Commit();
        }
        exportVersions.AddOrUpdate(manifest.Key, 1, (_, version) => version + 1);
    }
    public void Dispose()
    {
        disposed = true; connection.Dispose();
        while (readers.TryTake(out var reader)) reader.Dispose();
    }
}
public sealed record FileRecord(string ExportId, PublishedFile File, string? BlobSha256 = null);
