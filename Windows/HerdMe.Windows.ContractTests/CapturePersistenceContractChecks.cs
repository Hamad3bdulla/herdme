using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.Data.Sqlite;

internal static partial class ContractChecks
{
    internal static async Task VerifyCapturePersistenceContractsAsync(string supportRoot)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var mailRoot = Path.Combine(supportRoot, "mail-persistence");
        await using (var mail = new MailCaptureService(mailRoot, retentionLimit: 3, retentionAge: TimeSpan.FromDays(2)))
        {
            var captured = Channel.CreateUnbounded<CapturedMail>();
            mail.MessageCaptured += (_, _) => throw new InvalidOperationException("Mail observer fixture failure");
            mail.MessageCaptured += (_, message) => captured.Writer.TryWrite(message);
            Check(mail.RetentionLimit == 3 && mail.RetentionAge == TimeSpan.FromDays(2),
                "Mail exposes its configured retention bounds");
            await mail.StartAsync(0, token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, mail.Port!.Value, token);
            using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            Check((await reader.ReadLineAsync(token))?.StartsWith("220 ", StringComparison.Ordinal) == true,
                "SMTP persistence fixture receives the greeting");

            await DropCaptureTableAsync(mailRoot, "mail");
            for (var index = 0; index < 3; index++)
            {
                var response = await SendPersistenceMailAsync(reader, writer, "failed-" + index, token);
                Check(response?.StartsWith("451 ", StringComparison.Ordinal) == true,
                    "SMTP reports a temporary storage failure instead of acknowledging lost mail");
            }
            Check(!captured.Reader.TryRead(out _), "Failed SMTP saves do not publish captures");

            _ = new CaptureDatabase(mailRoot);
            var accepted = await SendPersistenceMailAsync(reader, writer, "recovered", token);
            Check(accepted?.StartsWith("250 ", StringComparison.Ordinal) == true,
                "SMTP retries successfully after storage recovers without restarting the worker");
            Check(mail.Load().Single().Subject == "recovered", "SMTP acknowledgement follows durable storage");
            var message = await captured.Reader.ReadAsync(token);
            Check(message.Subject == "recovered", "A throwing mail observer does not suppress later observers");

            mail.Delete(message);
            Check(mail.Load().Count == 0, "Deleting an acknowledged message removes its durable capture");
            await SendPersistenceMailAsync(reader, writer, "before-clear", token);
            await captured.Reader.ReadAsync(token);
            mail.Clear();
            await mail.StopAsync().WaitAsync(token);
            Check(mail.Load().Count == 0, "Stopping after clear cannot restore acknowledged captures");
            await mail.StartAsync(0, token);
            await mail.StopAsync().WaitAsync(token);
        }

        var dumpRoot = Path.Combine(supportRoot, "dump-persistence");
        await using (var dumps = new DumpCaptureService(dumpRoot, retentionLimit: 4, retentionAge: TimeSpan.FromDays(3)))
        {
            var captured = Channel.CreateUnbounded<CapturedDump>();
            dumps.DumpCaptured += (_, _) => throw new InvalidOperationException("Dump observer fixture failure");
            dumps.DumpCaptured += (_, dump) => captured.Writer.TryWrite(dump);
            Check(dumps.RetentionLimit == 4 && dumps.RetentionAge == TimeSpan.FromDays(3),
                "VarDumper exposes its configured retention bounds");
            await dumps.StartAsync(0, token);
            await DropCaptureTableAsync(dumpRoot, "dumps");
            for (var index = 0; index < 3; index++)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, dumps.Port!.Value, token);
                await WritePersistenceDumpAsync(client, "failure", token);
                await VerifyCaptureClientClosedAsync(client, "VarDumper storage failure", token);
            }
            Check(!captured.Reader.TryRead(out _), "Failed VarDumper saves do not publish captures");

            _ = new CaptureDatabase(dumpRoot);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, dumps.Port!.Value, token);
                await WritePersistenceDumpAsync(client, "recovered", token);
                var dump = await captured.Reader.ReadAsync(token);
                Check(dump.Summary.Contains("recovered", StringComparison.Ordinal),
                    "A failed save and throwing dump observer do not stop later captures");
                Check(dumps.Load().Single().Id == dump.Id, "Published VarDumper captures are durable");
            }
            dumps.Clear();
            await dumps.StopAsync().WaitAsync(token);
            Check(dumps.Load().Count == 0, "Stopping VarDumper after clear does not restore captures");
            await dumps.StartAsync(0, token);
            await dumps.StopAsync().WaitAsync(token);
        }
    }

    private static async Task DropCaptureTableAsync(string supportRoot, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(supportRoot, "captures.sqlite3"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = table == "mail" ? "DROP TABLE mail" : "DROP TABLE dumps";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> SendPersistenceMailAsync(
        StreamReader reader,
        StreamWriter writer,
        string subject,
        CancellationToken cancellationToken
    )
    {
        await writer.WriteLineAsync("MAIL FROM:<sender@example.test>".AsMemory(), cancellationToken);
        await reader.ReadLineAsync(cancellationToken);
        await writer.WriteLineAsync("RCPT TO:<recipient@example.test>".AsMemory(), cancellationToken);
        await reader.ReadLineAsync(cancellationToken);
        await writer.WriteLineAsync("DATA".AsMemory(), cancellationToken);
        Check((await reader.ReadLineAsync(cancellationToken))?.StartsWith("354 ", StringComparison.Ordinal) == true,
            "SMTP accepts the DATA command in the persistence fixture");
        await writer.WriteLineAsync(("Subject: " + subject + "\r\n\r\nCapture body.\r\n.").AsMemory(), cancellationToken);
        return await reader.ReadLineAsync(cancellationToken);
    }

    private static async Task WritePersistenceDumpAsync(TcpClient client, string value, CancellationToken cancellationToken)
    {
        var serialized = "s:" + Encoding.UTF8.GetByteCount(value) + ":\"" + value + "\";";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized)) + "\n";
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
    }
}
