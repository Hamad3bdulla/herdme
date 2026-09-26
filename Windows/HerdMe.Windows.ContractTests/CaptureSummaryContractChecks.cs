using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.Data.Sqlite;

internal static partial class ContractChecks
{
    internal static void VerifyCaptureSummaryContracts(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "capture-summaries");
        var database = new CaptureDatabase(root);
        var body = new string('b', 200_000);
        var message = new CapturedMail
        {
            Sender = "app@example.test",
            Recipients = ["dev@example.test"],
            Subject = "Welcome",
            Body = body,
            Raw = "Subject: Welcome\r\n\r\n" + body,
            HtmlBody = "<p>" + body + "</p>"
        };
        database.Save(message, 100, TimeSpan.FromDays(1));
        var row = database.LoadMailSummaries(100, TimeSpan.FromDays(1)).Single();
        Check(
            row.IsSummary
                && row.Id == message.Id
                && row.Subject == "Welcome"
                && row.Sender == message.Sender
                && row.Recipients.SequenceEqual(message.Recipients)
                && row.Body.Length == 0
                && row.Raw.Length == 0
                && row.HtmlBody is null
                && row.MatchesSearch("welcome"),
            "Mail list rows load only list columns"
        );
        var full = database.LoadMail(message.Id);
        Check(
            full is not null && !full.IsSummary && full.Body == body && full.Raw == message.Raw,
            "Mail previews load the complete message on demand"
        );
        Check(
            database.LoadMail(100, TimeSpan.FromDays(1)).Single().Body == body,
            "Full mail loads keep returning complete messages"
        );

        var dump = new CapturedDump
        {
            Source = "routes/web.php:12",
            Summary = new string('s', 10_000),
            Payload = new string('p', 300_000)
        };
        database.Save(dump, 100, TimeSpan.FromDays(1));
        var dumpRow = database.LoadDumpSummaries(100, TimeSpan.FromDays(1)).Single();
        Check(
            dumpRow.IsSummary
                && dumpRow.Payload.Length == 0
                && dumpRow.Summary.Length == CapturedDump.SummaryRowCharacters
                && dumpRow.Source == dump.Source,
            "Dump list rows load a bounded summary without the payload"
        );
        Check(
            database.LoadDump(dump.Id)?.Payload.Length == 300_000,
            "Dump details load the complete payload on demand"
        );
        Check(
            database.LoadMail(Guid.NewGuid()) is null && database.LoadDump(Guid.NewGuid()) is null,
            "Deleted captures load as missing"
        );

        // Databases written before the summary column existed stay readable.
        var legacyRoot = Path.Combine(supportRoot, "capture-summaries-legacy");
        Directory.CreateDirectory(legacyRoot);
        var legacyPath = Path.Combine(legacyRoot, "captures.sqlite3");
        var legacy = new CapturedMail { Subject = "Legacy", Body = "old body" };
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE mail (id TEXT PRIMARY KEY, received_at TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE dumps (id TEXT PRIMARY KEY, received_at TEXT NOT NULL, payload TEXT NOT NULL);
                INSERT INTO mail(id, received_at, payload) VALUES($id, $at, $payload);
                """;
            command.Parameters.AddWithValue("$id", legacy.Id.ToString());
            command.Parameters.AddWithValue("$at", legacy.ReceivedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(legacy));
            command.ExecuteNonQuery();
        }
        var migrated = new CaptureDatabase(legacyRoot);
        var legacyRow = migrated.LoadMailSummaries(100, TimeSpan.FromDays(1)).Single();
        Check(
            legacyRow.Subject == "Legacy" && !legacyRow.IsSummary && legacyRow.Body == "old body",
            "Capture rows written before list columns existed load from the full payload"
        );
        migrated.Save(new CapturedMail { Subject = "New" }, 100, TimeSpan.FromDays(1));
        Check(
            migrated.LoadMailSummaries(100, TimeSpan.FromDays(1)).Count == 2
                && new CaptureDatabase(legacyRoot).LoadMailSummaries(100, TimeSpan.FromDays(1)).Count == 2,
            "Upgraded capture databases accept new rows and reopen cleanly"
        );
        database.ReleasePooledConnections();
        migrated.ReleasePooledConnections();
    }
}
