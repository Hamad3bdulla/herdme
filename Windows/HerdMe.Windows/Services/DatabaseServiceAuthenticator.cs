using System.Diagnostics;
using System.Text;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public static class DatabaseServiceAuthenticator
{
    private const string MarkerName = ".herdme-auth-v1";
    public static readonly IReadOnlySet<string> ProtectedDefinitions = new HashSet<string>(
        ["mysql", "mariadb", "postgresql"],
        StringComparer.OrdinalIgnoreCase
    );

    public static async Task SecureAsync(
        ManagedServiceInstance instance,
        string executable,
        string dataDirectory,
        ServiceCredentials credentials,
        CancellationToken cancellationToken = default
    )
    {
        if (!ProtectedDefinitions.Contains(instance.DefinitionId)) return;
        var markerPath = Path.Combine(dataDirectory, MarkerName);
        if (instance.DefinitionId is "mysql" or "mariadb")
        {
            await SecureMySqlCompatibleAsync(
                instance, executable, credentials, markerPath, cancellationToken
            );
        }
        else
        {
            await SecurePostgreSqlAsync(
                instance, executable, dataDirectory, credentials, markerPath, cancellationToken
            );
        }
    }

    public static string MySqlProvisioningSql(ServiceCredentials credentials)
    {
        return string.Join("; ", new[]
        {
            "CREATE DATABASE IF NOT EXISTS laravel",
            $"CREATE USER IF NOT EXISTS '{credentials.Username}'@'127.0.0.1' IDENTIFIED BY '{credentials.Secret}'",
            $"ALTER USER '{credentials.Username}'@'127.0.0.1' IDENTIFIED BY '{credentials.Secret}'",
            $"GRANT ALL PRIVILEGES ON *.* TO '{credentials.Username}'@'127.0.0.1' WITH GRANT OPTION",
            $"CREATE USER IF NOT EXISTS 'root'@'127.0.0.1' IDENTIFIED BY '{credentials.Secret}'",
            $"ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY '{credentials.Secret}'",
            $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{credentials.Secret}'",
            "DELETE FROM mysql.user WHERE User = ''",
            "FLUSH PRIVILEGES"
        }) + ";";
    }

    public static (string Contents, bool Changed) SecurePostgreSqlHba(string contents)
    {
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var terminated = contents.EndsWith('\n');
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var commentIndex = lines[index].IndexOf('#');
            var body = commentIndex >= 0 ? lines[index][..commentIndex] : lines[index];
            var fields = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            var expected = fields[0] == "local" ? 4 : fields[0].StartsWith("host", StringComparison.Ordinal) ? 5 : 0;
            if (expected == 0 || fields.Length < expected || fields[^1] != "trust") continue;
            var tokenIndex = body.LastIndexOf("trust", StringComparison.Ordinal);
            if (tokenIndex < 0) continue;
            lines[index] = lines[index].Remove(tokenIndex, "trust".Length)
                .Insert(tokenIndex, "scram-sha-256");
            changed = true;
        }
        var result = string.Join(newline, lines);
        if (terminated && !result.EndsWith(newline, StringComparison.Ordinal)) result += newline;
        return (result, changed);
    }

    private static async Task SecureMySqlCompatibleAsync(
        ManagedServiceInstance instance,
        string executable,
        ServiceCredentials credentials,
        string markerPath,
        CancellationToken cancellationToken
    )
    {
        var binDirectory = Path.GetDirectoryName(executable)!;
        var clientNames = instance.DefinitionId == "mariadb"
            ? new[] { "mariadb.exe", "mysql.exe" }
            : new[] { "mysql.exe" };
        var client = clientNames.Select(name => Path.Combine(binDirectory, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"The {instance.Name} runtime is missing its command-line client."
            );

        var managedLoginWorks = false;
        var rootPasswordLoginWorks = false;
        var rootPasswordlessLoginWorks = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            managedLoginWorks = await MySqlLoginAsync(
                client, instance, credentials.Username, credentials.Secret, cancellationToken
            );
            rootPasswordLoginWorks = await MySqlLoginAsync(
                client, instance, "root", credentials.Secret, cancellationToken
            );
            rootPasswordlessLoginWorks = await MySqlLoginAsync(
                client, instance, "root", null, cancellationToken
            );
            if (managedLoginWorks || rootPasswordLoginWorks || rootPasswordlessLoginWorks) break;
            await Task.Delay(100, cancellationToken);
        }
        if (!managedLoginWorks && !rootPasswordLoginWorks && !rootPasswordlessLoginWorks)
        {
            throw new InvalidOperationException(
                $"HerdMe could not reach {instance.Name}'s SQL authentication endpoint. The data was preserved."
            );
        }

        if (managedLoginWorks
            && !await MySqlLoginAsync(client, instance, credentials.Username, null, cancellationToken)
            && !rootPasswordlessLoginWorks)
        {
            await WriteMarkerAsync(markerPath, cancellationToken);
            return;
        }
        if (File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"HerdMe could not verify managed authentication for {instance.Name}. The data was preserved."
            );
        }

        var bootstrapUser = managedLoginWorks ? credentials.Username : "root";
        var bootstrapPassword = managedLoginWorks || rootPasswordLoginWorks
            ? credentials.Secret
            : null;
        var bootstrap = await RunAsync(
            client,
            [
                "--no-defaults",
                "--protocol=TCP",
                "--host=127.0.0.1",
                $"--port={instance.Port}",
                $"--user={bootstrapUser}",
                "--connect-timeout=1",
                "--batch"
            ],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = bootstrapPassword },
            cancellationToken,
            MySqlProvisioningSql(credentials)
        );
        if (bootstrap.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"HerdMe could not migrate the existing {instance.Name} data to managed authentication. The data was preserved unchanged."
            );
        }
        var verified = await MySqlLoginAsync(
            client, instance, credentials.Username, credentials.Secret, cancellationToken
        ) && !await MySqlLoginAsync(
            client, instance, credentials.Username, null, cancellationToken
        ) && !await MySqlLoginAsync(client, instance, "root", null, cancellationToken);
        if (!verified)
        {
            throw new InvalidOperationException(
                $"HerdMe could not verify managed authentication for {instance.Name}. The data was preserved."
            );
        }
        await WriteMarkerAsync(markerPath, cancellationToken);
    }

    private static async Task<bool> MySqlLoginAsync(
        string client,
        ManagedServiceInstance instance,
        string username,
        string? password,
        CancellationToken cancellationToken
    )
    {
        var result = await RunAsync(
            client,
            [
                "--no-defaults",
                "--protocol=TCP",
                "--host=127.0.0.1",
                $"--port={instance.Port}",
                $"--user={username}",
                "--connect-timeout=1",
                "--batch",
                "--skip-column-names",
                "--execute=SELECT 1"
            ],
            new Dictionary<string, string?> { ["MYSQL_PWD"] = password },
            cancellationToken
        );
        return result.ExitCode == 0;
    }

    private static async Task SecurePostgreSqlAsync(
        ManagedServiceInstance instance,
        string executable,
        string dataDirectory,
        ServiceCredentials credentials,
        string markerPath,
        CancellationToken cancellationToken
    )
    {
        var binDirectory = Path.GetDirectoryName(executable)!;
        var client = Path.Combine(binDirectory, "psql.exe");
        var pgControl = Path.Combine(binDirectory, "pg_ctl.exe");
        if (!File.Exists(client) || !File.Exists(pgControl))
        {
            throw new FileNotFoundException(
                $"The {instance.Name} runtime is missing its command-line client."
            );
        }

        var passwordWorks = false;
        for (var attempt = 0; attempt < 30 && !passwordWorks; attempt++)
        {
            passwordWorks = await PostgreSqlLoginAsync(
                client, instance, credentials.Username, credentials.Secret, dataDirectory, cancellationToken
            );
            if (!passwordWorks) await Task.Delay(100, cancellationToken);
        }
        var passwordlessWorks = await PostgreSqlLoginAsync(
            client, instance, credentials.Username, null, dataDirectory, cancellationToken
        );
        var hbaPath = Path.Combine(dataDirectory, "pg_hba.conf");
        var original = await File.ReadAllTextAsync(hbaPath, cancellationToken);
        var secured = SecurePostgreSqlHba(original);
        if (passwordWorks && !passwordlessWorks && !secured.Changed)
        {
            await WriteMarkerAsync(markerPath, cancellationToken);
            return;
        }
        if (File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"HerdMe could not verify managed authentication for {instance.Name}. The data was preserved."
            );
        }

        if (!passwordWorks)
        {
            CommandResult bootstrap = new(-1, string.Empty);
            for (var attempt = 0; attempt < 30 && bootstrap.ExitCode != 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bootstrap = await PostgreSqlCommandAsync(
                    client, instance, "postgres", "SELECT 1", null, dataDirectory, cancellationToken
                );
                if (bootstrap.ExitCode != 0) await Task.Delay(100, cancellationToken);
            }
            if (bootstrap.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"HerdMe could not migrate the existing {instance.Name} data to managed authentication. The data was preserved unchanged."
                );
            }
            var exists = await PostgreSqlCommandAsync(
                client,
                instance,
                "postgres",
                $"SELECT 1 FROM pg_roles WHERE rolname = '{credentials.Username}'",
                null,
                dataDirectory,
                cancellationToken
            );
            var roleSql = exists.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("1")
                ? $"ALTER ROLE \"{credentials.Username}\" WITH LOGIN SUPERUSER PASSWORD '{credentials.Secret}'"
                : $"CREATE ROLE \"{credentials.Username}\" WITH LOGIN SUPERUSER PASSWORD '{credentials.Secret}'";
            var provision = await PostgreSqlCommandAsync(
                client, instance, "postgres", roleSql, null, dataDirectory, cancellationToken
            );
            if (provision.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"HerdMe could not migrate the existing {instance.Name} data to managed authentication. The data was preserved unchanged."
                );
            }
        }

        if (!secured.Changed)
        {
            throw new InvalidOperationException(
                $"HerdMe could not safely migrate the existing {instance.Name} authentication rules. The data was preserved unchanged."
            );
        }
        await AtomicWriteAsync(hbaPath, secured.Contents, cancellationToken);
        await RunAsync(pgControl, ["reload", "-D", dataDirectory], null, cancellationToken);
        await Task.Delay(200, cancellationToken);

        var verified = await PostgreSqlLoginAsync(
            client, instance, credentials.Username, credentials.Secret, dataDirectory, cancellationToken
        ) && !await PostgreSqlLoginAsync(
            client, instance, credentials.Username, null, dataDirectory, cancellationToken
        );
        if (!verified)
        {
            await AtomicWriteAsync(hbaPath, original, cancellationToken);
            await RunAsync(pgControl, ["reload", "-D", dataDirectory], null, cancellationToken);
            throw new InvalidOperationException(
                $"HerdMe could not verify managed authentication for {instance.Name}. The previous access rules were restored."
            );
        }
        await WriteMarkerAsync(markerPath, cancellationToken);
    }

    private static Task<CommandResult> PostgreSqlCommandAsync(
        string client,
        ManagedServiceInstance instance,
        string username,
        string command,
        string? password,
        string dataDirectory,
        CancellationToken cancellationToken
    ) => RunAsync(
        client,
        [
            "--host=127.0.0.1",
            $"--port={instance.Port}",
            $"--username={username}",
            "--dbname=postgres",
            "--no-password",
            "--tuples-only",
            "--no-align",
            "--set=ON_ERROR_STOP=1"
        ],
        PostgreSqlEnvironment(password, dataDirectory),
        cancellationToken,
        command + ";" + Environment.NewLine
    );

    private static async Task<bool> PostgreSqlLoginAsync(
        string client,
        ManagedServiceInstance instance,
        string username,
        string? password,
        string dataDirectory,
        CancellationToken cancellationToken
    )
    {
        var result = await PostgreSqlCommandAsync(
            client, instance, username, "SELECT 1", password, dataDirectory, cancellationToken
        );
        return result.ExitCode == 0;
    }

    private static Dictionary<string, string?> PostgreSqlEnvironment(
        string? password,
        string dataDirectory
    ) => new()
    {
        ["PGPASSWORD"] = password,
        ["PGPASSFILE"] = Path.Combine(dataDirectory, ".herdme-no-pgpass"),
        ["PGCONNECT_TIMEOUT"] = "2"
    };

    private static async Task WriteMarkerAsync(string markerPath, CancellationToken cancellationToken)
    {
        await AtomicWriteAsync(markerPath, "auth-v1\n", cancellationToken);
    }

    private static async Task AtomicWriteAsync(
        string path,
        string contents,
        CancellationToken cancellationToken
    )
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken,
        string? standardInput = null
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                if (variable.Value is null) startInfo.Environment.Remove(variable.Key);
                else startInfo.Environment[variable.Key] = variable.Value;
            }
        }
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The database client could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode, (await output) + Environment.NewLine + (await error));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new CommandResult(-1, string.Empty);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
