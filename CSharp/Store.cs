using Microsoft.Data.Sqlite;
namespace MoenotesAssets;

public sealed partial class Store : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public Store(string directory)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "csharp.sqlite"), Pooling = false }.ToString());
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
    public T? Get<T>(string kind, string id)
    {
        lock (gate)
        {
            using var command = Command("SELECT body FROM records WHERE kind=$kind AND id=$id", [("$kind", kind), ("$id", id)]);
            return command.ExecuteScalar() is string text ? Json.Read<T>(text) : default;
        }
    }
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
    }
    public void Dispose() => connection.Dispose();
}
public sealed record FileRecord(string ExportId, PublishedFile File, string? BlobSha256 = null);
