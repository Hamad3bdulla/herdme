using System.Text.Json;
using HerdMe.Windows.Models;
using Microsoft.Data.Sqlite;

namespace HerdMe.Windows.Services;

public sealed class CaptureDatabase
{
    private readonly string connectionString;
    private readonly object migrationSync = new();

    public CaptureDatabase(string supportRoot)
    {
        Directory.CreateDirectory(supportRoot);
        SQLitePCL.Batteries_V2.Init();
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(supportRoot, "captures.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public IReadOnlyList<CapturedMail> LoadMail(int limit, TimeSpan maximumAge)
    {
        Prune("mail", limit, maximumAge);
        return Load<CapturedMail>("mail", limit);
    }

    public IReadOnlyList<CapturedDump> LoadDumps(int limit, TimeSpan maximumAge)
    {
        Prune("dumps", limit, maximumAge);
        return Load<CapturedDump>("dumps", limit);
    }

    public void Save(CapturedMail message, int limit, TimeSpan maximumAge) =>
        Save("mail", message.Id.ToString(), message.ReceivedAt, JsonSerializer.Serialize(message), limit, maximumAge);

    public void Save(CapturedDump dump, int limit, TimeSpan maximumAge) =>
        Save("dumps", dump.Id.ToString(), dump.ReceivedAt, JsonSerializer.Serialize(dump), limit, maximumAge);

    public void DeleteMail(Guid id) => Delete("mail", id.ToString());
    public void ClearMail() => Clear("mail");
    public void ClearDumps() => Clear("dumps");

    public void MigrateMail(string directory)
    {
        Migrate<CapturedMail>(directory, message => Save(message, int.MaxValue, TimeSpan.FromDays(365_000)));
    }

    public void MigrateDumps(string directory)
    {
        Migrate<CapturedDump>(directory, dump => Save(dump, int.MaxValue, TimeSpan.FromDays(365_000)));
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS mail (
                id TEXT PRIMARY KEY,
                received_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS mail_received_at ON mail(received_at DESC);
            CREATE TABLE IF NOT EXISTS dumps (
                id TEXT PRIMARY KEY,
                received_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS dumps_received_at ON dumps(received_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private IReadOnlyList<T> Load<T>(string table, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} ORDER BY received_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<T>(reader.GetString(0));
            if (item is not null) result.Add(item);
        }
        return result;
    }

    private void Save(string table, string id, DateTimeOffset receivedAt, string payload, int limit, TimeSpan maximumAge)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"INSERT OR REPLACE INTO {table}(id, received_at, payload) VALUES($id, $at, $payload)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", receivedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
        }
        Prune(connection, transaction, table, limit, maximumAge);
        transaction.Commit();
    }

    private void Prune(string table, int limit, TimeSpan maximumAge)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Prune(connection, transaction, table, limit, maximumAge);
        transaction.Commit();
    }

    private static void Prune(SqliteConnection connection, SqliteTransaction transaction, string table, int limit, TimeSpan maximumAge)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM {table} WHERE received_at < $cutoff;
            DELETE FROM {table} WHERE id NOT IN (
                SELECT id FROM {table} ORDER BY received_at DESC LIMIT $limit
            );
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(maximumAge).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        command.ExecuteNonQuery();
    }

    private void Delete(string table, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void Clear(string table)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table}";
        command.ExecuteNonQuery();
    }

    private void Migrate<T>(string directory, Action<T> save)
    {
        lock (migrationSync)
        {
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, ".sqlite-migrated-v1");
            if (File.Exists(marker)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
                    if (item is not null) save(item);
                }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}
