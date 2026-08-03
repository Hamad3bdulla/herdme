using System.Diagnostics;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record DatabaseConnectionInspection(
    bool Connected,
    string Engine,
    string ServerVersion,
    int TableCount,
    long SizeBytes,
    TimeSpan ResponseTime,
    string Message
);

public static class DatabaseConnectionInspector
{
    public static async Task<DatabaseConnectionInspection> InspectAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        CancellationToken cancellationToken = default
    )
    {
        var mysql = instance.DefinitionId is "mysql" or "mariadb";
        var client = FindClient(serverExecutable, mysql
            ? instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"]
            : ["psql.exe"], instance.Name);
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password }
            : new Dictionary<string, string?> { ["PGPASSWORD"] = provisioning.Password, ["PGPASSFILE"] = Path.Combine(dataDirectory, ".herdme-no-pgpass"), ["PGCONNECT_TIMEOUT"] = "2" };
        IReadOnlyList<string> arguments = mysql
            ? ["--no-defaults", "--protocol=TCP", "--host=127.0.0.1", $"--port={instance.Port}", $"--user={provisioning.Username}", $"--database={provisioning.DatabaseName}", "--connect-timeout=2", "--batch", "--skip-column-names"]
            : ["--host=127.0.0.1", $"--port={instance.Port}", $"--username={provisioning.Username}", $"--dbname={provisioning.DatabaseName}", "--no-password", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1"];
        var sql = mysql
            ? "SELECT VERSION(); SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE(); SELECT COALESCE(SUM(data_length + index_length),0) FROM information_schema.tables WHERE table_schema = DATABASE(); SELECT 1;"
            : "SELECT version(); SELECT COUNT(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema'); SELECT COALESCE(pg_database_size(current_database()),0); SELECT 1;";
        var started = Stopwatch.GetTimestamp();
        var result = await RunAsync(client, arguments, environment, sql, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (result.ExitCode != 0)
        {
            return new(false, instance.DefinitionId, string.Empty, 0, 0, elapsed, string.IsNullOrWhiteSpace(result.Error) ? "Connection failed." : result.Error.Trim());
        }
        string[] lines = result.Output.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return ParseSuccessful(instance.DefinitionId, lines, elapsed);
    }

    internal static DatabaseConnectionInspection ParseSuccessful(
        string engine,
        IReadOnlyList<string> lines,
        TimeSpan elapsed
    )
    {
        var version = lines.FirstOrDefault() ?? "Unknown";
        _ = long.TryParse(lines.ElementAtOrDefault(1), out var tableCount);
        _ = long.TryParse(lines.ElementAtOrDefault(2), out var size);
        return new(true, engine, version, (int)Math.Clamp(tableCount, 0, int.MaxValue), Math.Max(0, size), elapsed, "Connection successful.");
    }

    private static string FindClient(string serverExecutable, IReadOnlyList<string> names, string serviceName)
    {
        var dir = Path.GetDirectoryName(serverExecutable) ?? throw new FileNotFoundException($"The {serviceName} runtime path is invalid.");
        return names.Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"The {serviceName} runtime is missing its command-line client.");
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private static async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, string sql, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) if (pair.Value is null) info.Environment.Remove(pair.Key); else info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The database client could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteAsync(sql.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }
}
