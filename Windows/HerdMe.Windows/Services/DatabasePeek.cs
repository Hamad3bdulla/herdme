using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record DatabasePeekTable(string Schema, string Name)
{
    // PostgreSQL tables outside "public" keep their schema so two same-named tables differ.
    public string DisplayName => Schema.Length == 0 || Schema == "public" ? Name : Schema + "." + Name;
}

public sealed record DatabasePeekRows(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows);

/// <summary>
/// Read-only look inside a site's database from the Sites Database tab: the table list and the
/// first rows of one table. It uses the service's own command-line client over 127.0.0.1 with the
/// site's .env credentials, runs inside a READ ONLY transaction that is rolled back, and only ever
/// builds SELECTs for a table name the server itself listed (quoted, never free text).
/// </summary>
public static class DatabasePeek
{
    public const int RowLimit = 50;
    public const int MaximumTables = 500;
    public const int MaximumIdentifierCharacters = 128;

    public static bool IsMySqlFamily(string definitionId) => definitionId is "mysql" or "mariadb";

    public static async Task<IReadOnlyList<DatabasePeekTable>> ListTablesAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        CancellationToken cancellationToken = default
    )
    {
        var output = await RunReadOnlyAsync(
            instance, serverExecutable, dataDirectory, provisioning,
            ListTablesSql(instance.DefinitionId), cancellationToken
        );
        return ParseTables(instance.DefinitionId, output);
    }

    public static async Task<DatabasePeekRows> ReadRowsAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        DatabasePeekTable table,
        CancellationToken cancellationToken = default
    )
    {
        var output = await RunReadOnlyAsync(
            instance, serverExecutable, dataDirectory, provisioning,
            RowsSql(instance.DefinitionId, table), cancellationToken
        );
        return IsMySqlFamily(instance.DefinitionId) ? ParseMySqlRows(output) : ParsePostgreSqlRows(output);
    }

    internal static string ListTablesSql(string definitionId) => IsMySqlFamily(definitionId)
        ? "SET SESSION TRANSACTION READ ONLY; START TRANSACTION READ ONLY; "
            + "SELECT '' AS table_schema, table_name FROM information_schema.tables "
            + "WHERE table_schema = DATABASE() ORDER BY table_name LIMIT " + MaximumTables + "; ROLLBACK;"
        : "BEGIN READ ONLY; SELECT table_schema, table_name FROM information_schema.tables "
            + "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') "
            + "ORDER BY (table_schema = 'public') DESC, table_schema, table_name LIMIT " + MaximumTables + "; ROLLBACK;";

    // MySQL answers tab-separated rows (values escaped by the client). PostgreSQL answers one
    // JSON array so values with line breaks cannot split a row.
    internal static string RowsSql(string definitionId, DatabasePeekTable table)
    {
        if (IsMySqlFamily(definitionId))
        {
            return "SET SESSION TRANSACTION READ ONLY; START TRANSACTION READ ONLY; "
                + "SELECT * FROM " + QuoteMySql(table.Name) + " LIMIT " + RowLimit + "; ROLLBACK;";
        }
        var source = QuotePostgreSql(table.Schema) + "." + QuotePostgreSql(table.Name);
        return "BEGIN READ ONLY; SELECT COALESCE(json_agg(row_to_json(peek)), '[]'::json) FROM "
            + "(SELECT * FROM " + source + " LIMIT " + RowLimit + ") AS peek; ROLLBACK;";
    }

    internal static string QuoteMySql(string identifier)
    {
        ValidateIdentifier(identifier);
        return "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    internal static string QuotePostgreSql(string identifier)
    {
        ValidateIdentifier(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static bool IsValidIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier)
        && identifier.Length <= MaximumIdentifierCharacters
        && !identifier.Any(char.IsControl);

    private static void ValidateIdentifier(string identifier)
    {
        if (!IsValidIdentifier(identifier))
        {
            throw new ArgumentException(
                ServiceText.Get("SitesDatabasePeekInvalidTable", "That table name cannot be read."),
                nameof(identifier)
            );
        }
    }

    // MySQL prints a header line first (--batch); psql prints bare tuples (--tuples-only).
    internal static IReadOnlyList<DatabasePeekTable> ParseTables(string definitionId, string output)
    {
        var mysql = IsMySqlFamily(definitionId);
        var tables = new List<DatabasePeekTable>();
        foreach (var line in SplitLines(output).Skip(mysql ? 1 : 0))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2) continue;
            var schema = mysql ? string.Empty : fields[0];
            var name = mysql ? UnescapeMySql(fields[1]) : fields[1];
            if (!IsValidIdentifier(name) || (!mysql && !IsValidIdentifier(schema))) continue;
            tables.Add(new DatabasePeekTable(schema, name));
            if (tables.Count == MaximumTables) break;
        }
        return tables;
    }

    // mysql --batch: first line is the column names, NULL is the bare word NULL and tabs, line
    // breaks and backslashes inside values are escaped.
    internal static DatabasePeekRows ParseMySqlRows(string output)
    {
        var lines = SplitLines(output).ToList();
        if (lines.Count == 0) return new DatabasePeekRows([], []);
        var columns = lines[0].Split('\t').Select(UnescapeMySql).ToArray();
        var rows = lines.Skip(1)
            .Take(RowLimit)
            .Select(line => line.Split('\t').Select(UnescapeMySql).ToArray())
            .ToList();
        return new DatabasePeekRows(columns, rows);
    }

    internal static DatabasePeekRows ParsePostgreSqlRows(string output)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(output.Trim());
        }
        catch (JsonException)
        {
            return new DatabasePeekRows([], []);
        }
        if (node is not JsonArray array) return new DatabasePeekRows([], []);
        var records = array.OfType<JsonObject>().Take(RowLimit).ToList();
        var columns = records.SelectMany(record => record.Select(property => property.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var rows = records
            .Select(record => columns.Select(column => PostgreSqlCell(record, column)).ToArray())
            .ToList();
        return new DatabasePeekRows(columns, rows);
    }

    private static string PostgreSqlCell(JsonObject record, string column)
    {
        if (!record.TryGetPropertyValue(column, out var value)) return string.Empty;
        if (value is null) return "NULL";
        if (value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            return scalar.GetValue<string>();
        }
        return value.ToJsonString();
    }

    internal static string UnescapeMySql(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index == value.Length - 1)
            {
                builder.Append(character);
                continue;
            }
            index++;
            builder.Append(value[index] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                'b' => '\b',
                'Z' => '\u001a',
                var other => other
            });
        }
        return builder.ToString();
    }

    private static IEnumerable<string> SplitLines(string output) => output
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<string> RunReadOnlyAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        string sql,
        CancellationToken cancellationToken
    )
    {
        var mysql = IsMySqlFamily(instance.DefinitionId);
        IReadOnlyList<string> clientNames = mysql
            ? instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"]
            : ["psql.exe"];
        var client = DatabaseConnectionInspector.FindClient(serverExecutable, clientNames, instance.Name);
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password }
            : new Dictionary<string, string?>
            {
                ["PGPASSWORD"] = provisioning.Password,
                ["PGPASSFILE"] = Path.Combine(dataDirectory, ".herdme-no-pgpass"),
                ["PGCONNECT_TIMEOUT"] = "2",
                // Together with BEGIN READ ONLY: the whole session refuses writes.
                ["PGOPTIONS"] = "-c default_transaction_read_only=on -c statement_timeout=8000"
            };
        IReadOnlyList<string> arguments = mysql
            ? ["--no-defaults", "--protocol=TCP", "--host=127.0.0.1", $"--port={instance.Port}", $"--user={provisioning.Username}", $"--database={provisioning.DatabaseName}", "--connect-timeout=2", "--batch"]
            : ["--host=127.0.0.1", $"--port={instance.Port}", $"--username={provisioning.Username}", $"--dbname={provisioning.DatabaseName}", "--no-password", "--quiet", "--tuples-only", "--no-align", "--field-separator=\t", "--set=ON_ERROR_STOP=1"];
        var result = await DatabaseConnectionInspector.RunAsync(client, arguments, environment, sql, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? ServiceText.Get("SitesDatabasePeekFailed", "The database could not be read.")
                : result.Error.Trim());
        }
        return result.Output;
    }
}
