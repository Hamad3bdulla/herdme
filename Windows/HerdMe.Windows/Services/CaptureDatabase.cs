using System.Text.Json;
using HerdMe.Windows.Models;
using Microsoft.Data.Sqlite;

namespace HerdMe.Windows.Services;

public sealed class CaptureDatabase
{
    // Retention is enforced by the queries themselves, so pruning only has to reclaim space.
    internal const int PruneEveryInserts = 32;
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);
    private static readonly Lazy<bool> SqliteProvider = new(
        () =>
        {
            SQLitePCL.Batteries_V2.Init();
            return true;
        },
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly string connectionString;
    private readonly object migrationSync = new();
    private readonly object pruneSync = new();
    private readonly Dictionary<string, PruneState> pruneStates = new(StringComparer.Ordinal);

    public CaptureDatabase(string supportRoot)
    {
        Directory.CreateDirectory(supportRoot);
        _ = SqliteProvider.Value;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(supportRoot, "captures.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: shared-cache mode is discouraged together with WAL and pooling.
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
        // Creating a database instance (re)creates a missing schema; the statements are
        // idempotent and cheap on a pooled connection.
        Initialize();
    }

    public IReadOnlyList<CapturedMail> LoadMail(int limit, TimeSpan maximumAge)
    {
        PruneIfDue("mail", limit, maximumAge, inserted: false);
        return Load<CapturedMail>("mail", limit, maximumAge);
    }

    public IReadOnlyList<CapturedDump> LoadDumps(int limit, TimeSpan maximumAge)
    {
        PruneIfDue("dumps", limit, maximumAge, inserted: false);
        return Load<CapturedDump>("dumps", limit, maximumAge);
    }

    /// <summary>
    /// Loads only the list columns (sender, subject, recipients) so opening Mail does not
    /// read every body and raw MIME message. Use <see cref="LoadMail(Guid)"/> for a preview.
    /// </summary>
    public IReadOnlyList<CapturedMail> LoadMailSummaries(int limit, TimeSpan maximumAge)
    {
        PruneIfDue("mail", limit, maximumAge, inserted: false);
        return Load<CapturedMail>("mail", limit, maximumAge, summaries: true);
    }

    /// <summary>Loads only the list columns of dumps; use <see cref="LoadDump(Guid)"/> for details.</summary>
    public IReadOnlyList<CapturedDump> LoadDumpSummaries(int limit, TimeSpan maximumAge)
    {
        PruneIfDue("dumps", limit, maximumAge, inserted: false);
        return Load<CapturedDump>("dumps", limit, maximumAge, summaries: true);
    }

    public CapturedMail? LoadMail(Guid id) => LoadOne<CapturedMail>("mail", id.ToString());

    public CapturedDump? LoadDump(Guid id) => LoadOne<CapturedDump>("dumps", id.ToString());

    public void Save(CapturedMail message, int limit, TimeSpan maximumAge) =>
        Save(
            "mail",
            message.Id.ToString(),
            message.ReceivedAt,
            JsonSerializer.Serialize(message),
            JsonSerializer.Serialize(message.ToSummary()),
            limit,
            maximumAge
        );

    public void Save(CapturedDump dump, int limit, TimeSpan maximumAge) =>
        Save(
            "dumps",
            dump.Id.ToString(),
            dump.ReceivedAt,
            JsonSerializer.Serialize(dump),
            JsonSerializer.Serialize(dump.ToSummary()),
            limit,
            maximumAge
        );

    /// <summary>Closes idle pooled connections so the database file is not kept open.</summary>
    public void ReleasePooledConnections()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public void DeleteMail(Guid id) => Delete("mail", id.ToString());
    public void ClearMail() => Clear("mail");
    public void DeleteDump(Guid id) => Delete("dumps", id.ToString());
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
            PRAGMA synchronous=NORMAL;
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
        EnsureSummaryColumn(connection, "mail");
        EnsureSummaryColumn(connection, "dumps");
    }

    /// <summary>
    /// Adds the list-column copy used by the capture lists. Rows written before it existed
    /// keep a NULL summary and are read from the full payload instead.
    /// </summary>
    private static void EnsureSummaryColumn(SqliteConnection connection, string table)
    {
        if (HasSummaryColumn(connection, table)) return;
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN summary TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) when (HasSummaryColumn(connection, table))
        {
            // Another HerdMe component added the column at the same time.
        }
    }

    private static bool HasSummaryColumn(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'summary'";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        try
        {
            // synchronous is a per-connection setting. In WAL mode NORMAL keeps the database
            // consistent after a crash; only the last commits may be lost on power failure.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    private IReadOnlyList<T> Load<T>(string table, int limit, TimeSpan maximumAge, bool summaries = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var column = summaries ? "COALESCE(summary, payload)" : "payload";
        command.CommandText = $"SELECT {column} FROM {table} WHERE received_at >= $cutoff ORDER BY received_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$cutoff", Cutoff(maximumAge));
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

    private T? LoadOne<T>(string table, string id) where T : class
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<T>(payload) : null;
    }

    private void Save(
        string table,
        string id,
        DateTimeOffset receivedAt,
        string payload,
        string summary,
        int limit,
        TimeSpan maximumAge
    )
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"INSERT OR REPLACE INTO {table}(id, received_at, payload, summary) VALUES($id, $at, $payload, $summary)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", receivedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$summary", summary);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        try
        {
            PruneIfDue(table, limit, maximumAge, inserted: true);
        }
        catch (SqliteException)
        {
            // The capture is already durable and reads enforce retention; pruning retries later.
        }
    }

    private void PruneIfDue(string table, int limit, TimeSpan maximumAge, bool inserted)
    {
        lock (pruneSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!pruneStates.TryGetValue(table, out var state))
            {
                state = new PruneState();
                pruneStates[table] = state;
            }
            if (inserted) state.InsertsSincePrune++;
            if (!ShouldPrune(state.LastPrunedAt, state.InsertsSincePrune, now)) return;
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Prune(connection, transaction, table, limit, maximumAge);
            transaction.Commit();
            state.LastPrunedAt = now;
            state.InsertsSincePrune = 0;
        }
    }

    internal static bool ShouldPrune(DateTimeOffset? lastPrunedAt, int insertsSincePrune, DateTimeOffset now) =>
        lastPrunedAt is not { } last
            || insertsSincePrune >= PruneEveryInserts
            || now - last >= PruneInterval;

    private static string Cutoff(TimeSpan maximumAge) =>
        DateTimeOffset.UtcNow.Subtract(maximumAge).UtcDateTime.ToString("O");

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
        command.Parameters.AddWithValue("$cutoff", Cutoff(maximumAge));
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

    private sealed class PruneState
    {
        public DateTimeOffset? LastPrunedAt { get; set; }

        public int InsertsSincePrune { get; set; }
    }
}
