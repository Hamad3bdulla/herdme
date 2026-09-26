using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

/// <summary>
/// Asks database engines to stop cleanly before HerdMe falls back to terminating
/// their process tree. A clean stop avoids InnoDB/PostgreSQL crash recovery and
/// truncated Redis append-only files.
/// </summary>
internal static class ManagedServiceShutdown
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public static bool SupportsGracefulStop(string definitionId) => definitionId is
        "mysql" or "mariadb" or "postgresql" or "redis" or "mongodb";

    /// <summary>
    /// Sends the engine-specific shutdown request. Returns true when the request
    /// was accepted; the caller still waits for the process and kills it on timeout.
    /// </summary>
    public static async Task<bool> RequestAsync(
        string definitionId,
        int processId,
        int port,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials? credentials,
        CancellationToken cancellationToken = default
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return definitionId switch
            {
                "mysql" or "mariadb" => SignalMySqlShutdownEvent(processId)
                    || await RunMySqlAdminShutdownAsync(
                        definitionId, serverExecutable, port, credentials, timeout.Token
                    ),
                "postgresql" => await RunPgCtlStopAsync(serverExecutable, dataDirectory, timeout.Token),
                "redis" => await SendRedisShutdownAsync(port, timeout.Token),
                "mongodb" => await SendMongoShutdownAsync(port, timeout.Token),
                _ => false
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// mysqld on Windows waits on a named event called MySQLShutdown{pid}; setting it
    /// starts a normal shutdown without needing SQL credentials.
    /// </summary>
    internal static bool SignalMySqlShutdownEvent(int processId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var name in MySqlShutdownEventNames(processId))
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(name, out var handle)) continue;
                using (handle)
                {
                    if (handle.Set()) return true;
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException
                or WaitHandleCannotBeOpenedException)
            {
            }
        }
        return false;
    }

    internal static IReadOnlyList<string> MySqlShutdownEventNames(int processId) =>
    [
        $"MySQLShutdown{processId}",
        $"MariaDBShutdown{processId}"
    ];

    private static async Task<bool> RunMySqlAdminShutdownAsync(
        string definitionId,
        string serverExecutable,
        int port,
        ServiceCredentials? credentials,
        CancellationToken cancellationToken
    )
    {
        var binDirectory = Path.GetDirectoryName(serverExecutable);
        if (binDirectory is null || credentials is null) return false;
        string[] names = definitionId == "mariadb"
            ? ["mariadb-admin.exe", "mysqladmin.exe"]
            : ["mysqladmin.exe"];
        var admin = names.Select(name => Path.Combine(binDirectory, name)).FirstOrDefault(File.Exists);
        if (admin is null) return false;
        return await RunToolAsync(
            admin,
            [
                "--no-defaults",
                "--protocol=TCP",
                "--host=127.0.0.1",
                $"--port={port}",
                "--user=root",
                "--connect-timeout=2",
                "shutdown"
            ],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = credentials.Secret },
            cancellationToken
        );
    }

    private static async Task<bool> RunPgCtlStopAsync(
        string serverExecutable,
        string dataDirectory,
        CancellationToken cancellationToken
    )
    {
        var binDirectory = Path.GetDirectoryName(serverExecutable);
        if (binDirectory is null) return false;
        var pgCtl = Path.Combine(binDirectory, "pg_ctl.exe");
        if (!File.Exists(pgCtl)) return false;
        return await RunToolAsync(
            pgCtl,
            ["stop", "-D", dataDirectory, "-m", "fast", "-w", "-t", "15"],
            new Dictionary<string, string?>(),
            cancellationToken
        );
    }

    private static async Task<bool> RunToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment)
        {
            if (variable.Value is null) startInfo.Environment.Remove(variable.Key);
            else startInfo.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(startInfo);
        if (process is null) return false;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception killError) when (killError is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
            }
            throw;
        }
        finally
        {
            // Never wait forever on pipes inherited by a grandchild.
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (Exception pipeError) when (pipeError is TimeoutException or IOException
                or ObjectDisposedException)
            {
            }
        }
        return process.ExitCode == 0;
    }

    private static async Task<bool> SendRedisShutdownAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var stream = client.GetStream();
        // SHUTDOWN flushes the append-only file before Redis exits. A successful
        // shutdown closes the connection without a reply; errors start with '-'.
        await stream.WriteAsync("*1\r\n$8\r\nSHUTDOWN\r\n"u8.ToArray(), cancellationToken);
        var reply = new byte[256];
        int read;
        try
        {
            read = await stream.ReadAsync(reply, cancellationToken);
        }
        catch (IOException)
        {
            return true;
        }
        return read == 0 || reply[0] != (byte)'-';
    }

    private static async Task<bool> SendMongoShutdownAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(MongoShutdownMessage(requestId: 1), cancellationToken);
        var reply = new byte[512];
        try
        {
            // mongod usually closes the socket while shutting down; a reply is an error document.
            _ = await stream.ReadAsync(reply, cancellationToken);
        }
        catch (IOException)
        {
        }
        return true;
    }

    /// <summary>OP_MSG carrying <c>{ shutdown: 1, $db: "admin" }</c>.</summary>
    internal static byte[] MongoShutdownMessage(int requestId)
    {
        var document = new List<byte>();
        void AppendInt32(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            document.AddRange(bytes);
        }
        void AppendCString(string value)
        {
            document.AddRange(Encoding.UTF8.GetBytes(value));
            document.Add(0);
        }
        AppendInt32(0);
        document.Add(0x10);
        AppendCString("shutdown");
        AppendInt32(1);
        document.Add(0x02);
        AppendCString("$db");
        AppendInt32(Encoding.UTF8.GetByteCount("admin") + 1);
        AppendCString("admin");
        document.Add(0);
        var bson = document.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bson, bson.Length);

        var message = new byte[16 + 4 + 1 + bson.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(0), message.Length);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12), 2013);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 0);
        message[20] = 0;
        bson.CopyTo(message, 21);
        return message;
    }
}
