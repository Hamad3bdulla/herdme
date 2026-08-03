using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record SiteDatabaseProvisioning(
    string DatabaseName,
    string Username,
    string Password
);

public static class SiteDatabaseProvisioner
{
    public static readonly IReadOnlySet<string> SupportedDefinitions = new HashSet<string>(
        ["mysql", "mariadb", "postgresql"],
        StringComparer.OrdinalIgnoreCase
    );

    public static string SuggestedDatabaseName(string siteName)
    {
        var characters = siteName.Trim().ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '_'
        ).ToArray();
        var compact = string.Join('_', new string(characters).Split(
            '_',
            StringSplitOptions.RemoveEmptyEntries
        ));
        if (compact.Length == 0) compact = "site";
        if (!char.IsAsciiLetter(compact[0])) compact = "site_" + compact;
        return compact.Length <= 63 ? compact : compact[..63].TrimEnd('_');
    }

    public static bool IsValidDatabaseName(string databaseName)
    {
        return databaseName.Length is > 0 and <= 63
            && char.IsAsciiLetter(databaseName[0])
            && databaseName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    public static SiteDatabaseProvisioning Generate(string databaseName)
    {
        if (!IsValidDatabaseName(databaseName))
        {
            throw new ArgumentException(
                "Database names must start with a letter and contain only letters, numbers, and underscores (maximum 63 characters).",
                nameof(databaseName)
            );
        }
        return new SiteDatabaseProvisioning(
            databaseName,
            "herdme_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
        );
    }

    public static async Task<SiteDatabaseProvisioning> CreateAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials administrator,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(administrator);
        if (!SupportedDefinitions.Contains(instance.DefinitionId))
        {
            throw new NotSupportedException($"{instance.Name} cannot create site databases.");
        }
        var provisioning = Generate(databaseName);
        if (instance.DefinitionId is "mysql" or "mariadb")
        {
            await CreateMySqlCompatibleAsync(
                instance,
                serverExecutable,
                administrator,
                provisioning,
                cancellationToken
            );
        }
        else
        {
            await CreatePostgreSqlAsync(
                instance,
                serverExecutable,
                dataDirectory,
                administrator,
                provisioning,
                cancellationToken
            );
        }
        return provisioning;
    }

    public static async Task BackupAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        RequireConnectionProvisioning(provisioning);
        var environment = instance.DefinitionId is "mysql" or "mariadb"
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password }
            : PostgreSqlEnvironment(provisioning.Password, dataDirectory);
        string executable;
        IReadOnlyList<string> arguments;
        if (instance.DefinitionId is "mysql" or "mariadb")
        {
            executable = FindClient(
                serverExecutable,
                instance.DefinitionId == "mariadb"
                    ? ["mariadb-dump.exe", "mysqldump.exe"]
                    : ["mysqldump.exe"],
                instance.Name
            );
            arguments =
            [
                "--no-defaults",
                "--protocol=TCP",
                "--host=127.0.0.1",
                $"--port={instance.Port}",
                $"--user={provisioning.Username}",
                "--single-transaction",
                "--routines",
                "--events",
                provisioning.DatabaseName
            ];
        }
        else if (instance.DefinitionId == "postgresql")
        {
            executable = FindClient(serverExecutable, ["pg_dump.exe"], instance.Name);
            arguments =
            [
                "--host=127.0.0.1",
                $"--port={instance.Port}",
                $"--username={provisioning.Username}",
                "--no-password",
                "--format=plain",
                "--no-owner",
                "--no-privileges",
                provisioning.DatabaseName
            ];
        }
        else
        {
            throw new NotSupportedException($"{instance.Name} cannot back up site databases.");
        }
        await RunFileTransferAsync(
            executable,
            arguments,
            environment,
            inputPath: null,
            outputPath: destination,
            cancellationToken
        );
    }

    public static async Task RestoreAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        RequireConnectionProvisioning(provisioning);
        if (!File.Exists(source)) throw new FileNotFoundException("The SQL backup was not found.", source);
        if (!IsSupportedImportFile(source))
        {
            throw new InvalidDataException("Choose a .sql or .sql.gz database export.");
        }
        var mysql = instance.DefinitionId is "mysql" or "mariadb";
        var executable = mysql
            ? FindClient(
                serverExecutable,
                instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"],
                instance.Name
            )
            : FindClient(serverExecutable, ["psql.exe"], instance.Name);
        var arguments = mysql
            ? [.. MySqlArguments(instance, provisioning.Username), $"--database={provisioning.DatabaseName}"]
            : PostgreSqlArguments(instance, provisioning.Username, provisioning.DatabaseName);
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password }
            : PostgreSqlEnvironment(provisioning.Password, dataDirectory);
        await RunFileTransferAsync(
            executable,
            arguments,
            environment,
            source,
            outputPath: null,
            cancellationToken
        );
    }

    internal static bool IsSupportedImportFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> ExistsAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials administrator,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(administrator);
        if (!SupportedDefinitions.Contains(instance.DefinitionId))
        {
            throw new NotSupportedException($"{instance.Name} cannot inspect site databases.");
        }
        if (!IsValidDatabaseName(databaseName))
        {
            throw new ArgumentException("The database name is invalid.", nameof(databaseName));
        }
        var mysql = instance.DefinitionId is "mysql" or "mariadb";
        var executable = mysql
            ? FindClient(
                serverExecutable,
                instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"],
                instance.Name
            )
            : FindClient(serverExecutable, ["psql.exe"], instance.Name);
        var arguments = mysql
            ? MySqlArguments(instance, administrator.Username)
            : PostgreSqlArguments(instance, administrator.Username, "postgres");
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = administrator.Secret }
            : PostgreSqlEnvironment(administrator.Secret, dataDirectory);
        var sql = mysql
            ? $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{databaseName}';\n"
            : $"SELECT datname FROM pg_database WHERE datname = '{databaseName}';\n";
        var result = await RunAsync(executable, arguments, environment, sql, cancellationToken);
        RequireSuccess(result, instance.Name, "check whether the database exists");
        return !string.IsNullOrWhiteSpace(result.Output);
    }

    public static async Task ResetPasswordAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials administrator,
        SiteDatabaseProvisioning provisioning,
        string newPassword,
        CancellationToken cancellationToken = default
    )
    {
        RequireValidProvisioning(provisioning);
        if (!IsValidGeneratedValue(newPassword, 128))
        {
            throw new ArgumentException("The new database password is invalid.", nameof(newPassword));
        }
        var mysql = instance.DefinitionId is "mysql" or "mariadb";
        var executable = mysql
            ? FindClient(
                serverExecutable,
                instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"],
                instance.Name
            )
            : FindClient(serverExecutable, ["psql.exe"], instance.Name);
        var arguments = mysql
            ? MySqlArguments(instance, administrator.Username)
            : PostgreSqlArguments(instance, administrator.Username, "postgres");
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = administrator.Secret }
            : PostgreSqlEnvironment(administrator.Secret, dataDirectory);
        var sql = mysql
            ? $"ALTER USER '{provisioning.Username}'@'127.0.0.1' IDENTIFIED BY '{newPassword}';\n"
            : $"ALTER ROLE \"{provisioning.Username}\" WITH PASSWORD '{newPassword}';\n";
        RequireSuccess(
            await RunAsync(executable, arguments, environment, sql, cancellationToken),
            instance.Name,
            "reset the site database password"
        );
    }

    public static async Task DeleteAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials administrator,
        SiteDatabaseProvisioning provisioning,
        CancellationToken cancellationToken = default
    )
    {
        RequireValidProvisioning(provisioning);
        var mysql = instance.DefinitionId is "mysql" or "mariadb";
        var executable = mysql
            ? FindClient(
                serverExecutable,
                instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"],
                instance.Name
            )
            : FindClient(serverExecutable, ["psql.exe"], instance.Name);
        var arguments = mysql
            ? MySqlArguments(instance, administrator.Username)
            : PostgreSqlArguments(instance, administrator.Username, "postgres");
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = administrator.Secret }
            : PostgreSqlEnvironment(administrator.Secret, dataDirectory);
        var sql = mysql
            ? $"DROP DATABASE `{provisioning.DatabaseName}`;\nDROP USER '{provisioning.Username}'@'127.0.0.1';\n"
            : $"DROP DATABASE \"{provisioning.DatabaseName}\";\nDROP ROLE \"{provisioning.Username}\";\n";
        RequireSuccess(
            await RunAsync(executable, arguments, environment, sql, cancellationToken),
            instance.Name,
            "delete the site database"
        );
    }

    internal static string MySqlCreateDatabaseSql(SiteDatabaseProvisioning provisioning)
    {
        RequireValidProvisioning(provisioning);
        return $"CREATE DATABASE `{provisioning.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;\n"
            + $"CREATE USER '{provisioning.Username}'@'127.0.0.1' IDENTIFIED BY '{provisioning.Password}';\n"
            + $"GRANT ALL PRIVILEGES ON `{provisioning.DatabaseName}`.* TO '{provisioning.Username}'@'127.0.0.1';\n"
            + "FLUSH PRIVILEGES;\n";
    }

    internal static (string CreateRole, string CreateDatabase) PostgreSqlCreateDatabaseSql(
        SiteDatabaseProvisioning provisioning
    )
    {
        RequireValidProvisioning(provisioning);
        return (
            $"CREATE ROLE \"{provisioning.Username}\" WITH LOGIN PASSWORD '{provisioning.Password}';",
            $"CREATE DATABASE \"{provisioning.DatabaseName}\" OWNER \"{provisioning.Username}\" ENCODING 'UTF8';"
        );
    }

    private static async Task CreateMySqlCompatibleAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        ServiceCredentials administrator,
        SiteDatabaseProvisioning provisioning,
        CancellationToken cancellationToken
    )
    {
        var client = FindClient(
            serverExecutable,
            instance.DefinitionId == "mariadb" ? ["mariadb.exe", "mysql.exe"] : ["mysql.exe"],
            instance.Name
        );
        var arguments = MySqlArguments(instance, administrator.Username);
        var environment = new Dictionary<string, string?> { ["MYSQL_PWD"] = administrator.Secret };
        var exists = await RunAsync(
            client,
            arguments,
            environment,
            $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{provisioning.DatabaseName}';\n",
            cancellationToken
        );
        RequireSuccess(exists, instance.Name, "check whether the database already exists");
        if (!string.IsNullOrWhiteSpace(exists.Output))
        {
            throw new InvalidOperationException($"A database named {provisioning.DatabaseName} already exists.");
        }

        try
        {
            var created = await RunAsync(
                client,
                arguments,
                environment,
                MySqlCreateDatabaseSql(provisioning),
                cancellationToken
            );
            if (created.ExitCode != 0) throw ProvisioningFailed(instance.Name);

            var verified = await RunAsync(
                client,
                [.. MySqlArguments(instance, provisioning.Username), $"--database={provisioning.DatabaseName}", "--execute=SELECT 1"],
                new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password },
                null,
                cancellationToken
            );
            if (verified.ExitCode != 0) throw ProvisioningFailed(instance.Name);
        }
        catch
        {
            await CleanupMySqlAsync(client, arguments, environment, provisioning);
            throw;
        }
    }

    private static async Task CleanupMySqlAsync(
        string client,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        SiteDatabaseProvisioning provisioning
    )
    {
        var sql = $"DROP DATABASE IF EXISTS `{provisioning.DatabaseName}`;\n"
            + $"DROP USER IF EXISTS '{provisioning.Username}'@'127.0.0.1';\n";
        _ = await RunAsync(client, arguments, environment, sql, CancellationToken.None);
    }

    private static async Task CreatePostgreSqlAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        ServiceCredentials administrator,
        SiteDatabaseProvisioning provisioning,
        CancellationToken cancellationToken
    )
    {
        var client = FindClient(serverExecutable, ["psql.exe"], instance.Name);
        var environment = PostgreSqlEnvironment(administrator.Secret, dataDirectory);
        var arguments = PostgreSqlArguments(instance, administrator.Username, "postgres");
        var exists = await RunAsync(
            client,
            arguments,
            environment,
            $"SELECT datname FROM pg_database WHERE datname = '{provisioning.DatabaseName}';\n",
            cancellationToken
        );
        RequireSuccess(exists, instance.Name, "check whether the database already exists");
        if (!string.IsNullOrWhiteSpace(exists.Output))
        {
            throw new InvalidOperationException($"A database named {provisioning.DatabaseName} already exists.");
        }

        try
        {
            var sql = PostgreSqlCreateDatabaseSql(provisioning);
            var role = await RunAsync(
                client,
                arguments,
                environment,
                sql.CreateRole + "\n",
                cancellationToken
            );
            if (role.ExitCode != 0) throw ProvisioningFailed(instance.Name);
            var database = await RunAsync(
                client,
                arguments,
                environment,
                sql.CreateDatabase + "\n",
                cancellationToken
            );
            if (database.ExitCode != 0) throw ProvisioningFailed(instance.Name);

            var verified = await RunAsync(
                client,
                PostgreSqlArguments(instance, provisioning.Username, provisioning.DatabaseName),
                PostgreSqlEnvironment(provisioning.Password, dataDirectory),
                "SELECT 1;\n",
                cancellationToken
            );
            if (verified.ExitCode != 0) throw ProvisioningFailed(instance.Name);
        }
        catch
        {
            await CleanupPostgreSqlAsync(client, arguments, environment, provisioning);
            throw;
        }
    }

    private static async Task CleanupPostgreSqlAsync(
        string client,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        SiteDatabaseProvisioning provisioning
    )
    {
        var sql = $"DROP DATABASE IF EXISTS \"{provisioning.DatabaseName}\";\n"
            + $"DROP ROLE IF EXISTS \"{provisioning.Username}\";\n";
        _ = await RunAsync(client, arguments, environment, sql, CancellationToken.None);
    }

    private static IReadOnlyList<string> MySqlArguments(
        ManagedServiceInstance instance,
        string username
    ) =>
    [
        "--no-defaults",
        "--protocol=TCP",
        "--host=127.0.0.1",
        $"--port={instance.Port}",
        $"--user={username}",
        "--connect-timeout=2",
        "--batch",
        "--skip-column-names"
    ];

    private static IReadOnlyList<string> PostgreSqlArguments(
        ManagedServiceInstance instance,
        string username,
        string databaseName
    ) =>
    [
        "--host=127.0.0.1",
        $"--port={instance.Port}",
        $"--username={username}",
        $"--dbname={databaseName}",
        "--no-password",
        "--tuples-only",
        "--no-align",
        "--set=ON_ERROR_STOP=1"
    ];

    private static IReadOnlyDictionary<string, string?> PostgreSqlEnvironment(
        string password,
        string dataDirectory
    ) => new Dictionary<string, string?>
    {
        ["PGPASSWORD"] = password,
        ["PGPASSFILE"] = Path.Combine(dataDirectory, ".herdme-no-pgpass"),
        ["PGCONNECT_TIMEOUT"] = "2"
    };

    private static string FindClient(
        string serverExecutable,
        IReadOnlyList<string> names,
        string serviceName
    )
    {
        var binDirectory = Path.GetDirectoryName(serverExecutable)
            ?? throw new FileNotFoundException($"The {serviceName} runtime path is invalid.");
        return names.Select(name => Path.Combine(binDirectory, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"The {serviceName} runtime is missing its command-line client."
            );
    }

    private static void RequireValidProvisioning(SiteDatabaseProvisioning provisioning)
    {
        ArgumentNullException.ThrowIfNull(provisioning);
        if (!IsValidDatabaseName(provisioning.DatabaseName)
            || !IsValidGeneratedValue(provisioning.Username, 32)
            || !IsValidGeneratedValue(provisioning.Password, 128))
        {
            throw new ArgumentException("The generated database credentials are invalid.", nameof(provisioning));
        }
    }

    private static void RequireConnectionProvisioning(SiteDatabaseProvisioning provisioning)
    {
        ArgumentNullException.ThrowIfNull(provisioning);
        if (!IsValidDatabaseName(provisioning.DatabaseName)
            || !IsValidGeneratedValue(provisioning.Username, 128)
            || provisioning.Password.Length > 512)
        {
            throw new ArgumentException("The database connection settings are invalid.", nameof(provisioning));
        }
    }

    private static bool IsValidGeneratedValue(string value, int maximumLength)
    {
        return value.Length is > 0 && value.Length <= maximumLength
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static void RequireSuccess(CommandResult result, string serviceName, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"HerdMe could not {operation} in {serviceName}.");
        }
    }

    private static InvalidOperationException ProvisioningFailed(string serviceName) => new(
        $"HerdMe could not create and verify the site database in {serviceName}. Any partial changes were rolled back."
    );

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string? standardInput,
        CancellationToken cancellationToken
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
        foreach (var variable in environment)
        {
            if (variable.Value is null) startInfo.Environment.Remove(variable.Key);
            else startInfo.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The database client could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var standardOutput = (await output).Trim();
        _ = await error;
        return new CommandResult(process.ExitCode, standardOutput);
    }

    private static async Task RunFileTransferAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string? inputPath,
        string? outputPath,
        CancellationToken cancellationToken
    )
    {
        if ((inputPath is null) == (outputPath is null))
        {
            throw new ArgumentException("Choose exactly one SQL transfer direction.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = inputPath is not null,
            RedirectStandardOutput = outputPath is not null,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment)
        {
            if (variable.Value is null) startInfo.Environment.Remove(variable.Key);
            else startInfo.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The database transfer could not be started.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var transfer = inputPath is not null
            ? CopyInputAsync(process, inputPath, cancellationToken)
            : CopyOutputAsync(process, outputPath!, cancellationToken);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), transfer);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (outputPath is not null && File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
        var errorText = await error;
        if (process.ExitCode != 0)
        {
            if (outputPath is not null && File.Exists(outputPath)) File.Delete(outputPath);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(errorText)
                    ? "The database transfer failed."
                    : errorText.Trim()
            );
        }
    }

    private static async Task CopyInputAsync(
        Process process,
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var decompressed = new GZipStream(
                    source,
                    CompressionMode.Decompress,
                    leaveOpen: false
                );
                await decompressed.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            }
            else
            {
                await source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            }
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task CopyOutputAsync(
        Process process,
        string path,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
