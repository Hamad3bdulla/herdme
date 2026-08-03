using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record SiteDatabaseProvisioning(
    string DatabaseName,
    string Username,
    string Password
);

public sealed record DatabaseTransferProgress(
    long BytesTransferred,
    long TotalBytes,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    int CompatibilityFixes
)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100);
}

public static class SiteDatabaseProvisioner
{
    private static readonly Regex UnsupportedMySqlCollation = new(
        @"\b(COLLATE\s*=?\s*)utf8mb4_(?:(?:0900)|(?:uca\d+))[a-z0-9_]*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );
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
            progress: null,
            normalizeSql: false,
            mysql: false,
            mergeExisting: false,
            cancellationToken
        );
    }

    public static async Task RestoreAsync(
        ManagedServiceInstance instance,
        string serverExecutable,
        string dataDirectory,
        SiteDatabaseProvisioning provisioning,
        string source,
        CancellationToken cancellationToken = default,
        IProgress<DatabaseTransferProgress>? progress = null,
        bool mergeExisting = false
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
        IReadOnlyList<string> arguments;
        if (mysql)
        {
            arguments =
            [
                .. MySqlArguments(instance, provisioning.Username),
                "--default-character-set=utf8mb4",
                "--max-allowed-packet=1G",
                "--binary-mode=1",
                $"--database={provisioning.DatabaseName}"
            ];
        }
        else
        {
            arguments =
            [
                .. PostgreSqlArguments(instance, provisioning.Username, provisioning.DatabaseName),
                "--set=ON_ERROR_STOP=on"
            ];
        }
        var environment = mysql
            ? new Dictionary<string, string?> { ["MYSQL_PWD"] = provisioning.Password }
            : PostgreSqlEnvironment(provisioning.Password, dataDirectory);
        await RunFileTransferAsync(
            executable,
            arguments,
            environment,
            source,
            outputPath: null,
            progress,
            normalizeSql: true,
            mysql,
            mergeExisting,
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
        IProgress<DatabaseTransferProgress>? progress,
        bool normalizeSql,
        bool mysql,
        bool mergeExisting,
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
            ? CopyInputAsync(
                process,
                inputPath,
                normalizeSql,
                mysql,
                mergeExisting,
                progress,
                cancellationToken
            )
            : CopyOutputAsync(process, outputPath!, cancellationToken);
        Exception? transferFailure = null;
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
        catch (Exception caught) when (caught is IOException or ObjectDisposedException)
        {
            transferFailure = caught;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var errorText = await error;
        var failureMessage = TransferFailureMessage(
            process.ExitCode,
            errorText,
            transferFailure is not null
        );
        if (failureMessage is not null)
        {
            if (outputPath is not null && File.Exists(outputPath)) File.Delete(outputPath);
            throw new InvalidOperationException(failureMessage, transferFailure);
        }
    }

    internal static string? TransferFailureMessage(
        int exitCode,
        string standardError,
        bool inputPipeFailed
    )
    {
        if (exitCode != 0)
        {
            return string.IsNullOrWhiteSpace(standardError)
                ? "The database transfer failed."
                : standardError.Trim();
        }
        return inputPipeFailed
            ? "The database client stopped before the SQL file was fully imported."
            : null;
    }

    private static async Task CopyInputAsync(
        Process process,
        string path,
        bool normalizeSql,
        bool mysql,
        bool mergeExisting,
        IProgress<DatabaseTransferProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var timer = Stopwatch.StartNew();
        var totalBytes = new FileInfo(path).Length;
        var lastReport = TimeSpan.MinValue;
        var compatibilityFixes = 0;

        void Report(long bytes, bool force = false)
        {
            if (!force && timer.Elapsed - lastReport < TimeSpan.FromMilliseconds(200)) return;
            lastReport = timer.Elapsed;
            var transferred = Math.Min(bytes, totalBytes);
            var bytesPerSecond = timer.Elapsed.TotalSeconds > 0
                ? transferred / timer.Elapsed.TotalSeconds
                : 0;
            TimeSpan? remaining = bytesPerSecond > 0
                ? TimeSpan.FromSeconds((totalBytes - transferred) / bytesPerSecond)
                : null;
            progress?.Report(new DatabaseTransferProgress(
                transferred,
                totalBytes,
                timer.Elapsed,
                remaining,
                compatibilityFixes
            ));
        }

        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using Stream sqlSource = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true)
                : source;
            Report(0, force: true);
            if (normalizeSql)
            {
                using var reader = new StreamReader(
                    sqlSource,
                    new UTF8Encoding(false, false),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 64 * 1_024,
                    leaveOpen: true
                );
                await using var writer = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(false),
                    bufferSize: 64 * 1_024,
                    leaveOpen: true
                );
                var buffer = new char[64 * 1_024];
                var pending = string.Empty;
                var mysqlNormalizer = mysql
                    ? new MySqlStreamNormalizer(mergeExisting)
                    : null;
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (count == 0) break;
                    for (var index = 0; index < count; index++)
                    {
                        if (buffer[index] == '\uFFFD') compatibilityFixes++;
                    }
                    var combined = pending + new string(buffer, 0, count);
                    var safeLength = Math.Max(0, combined.Length - 256);
                    var ready = combined[..safeLength];
                    pending = combined[safeLength..];
                    if (mysql)
                    {
                        var previousFixes = mysqlNormalizer!.Fixes;
                        ready = mysqlNormalizer.Rewrite(ready);
                        compatibilityFixes += mysqlNormalizer.Fixes - previousFixes;
                    }
                    await writer.WriteAsync(ready.AsMemory(), cancellationToken);
                    Report(source.Position);
                }
                if (mysql)
                {
                    var previousFixes = mysqlNormalizer!.Fixes;
                    pending = mysqlNormalizer.Rewrite(pending);
                    compatibilityFixes += mysqlNormalizer.Fixes - previousFixes;
                }
                await writer.WriteAsync(pending.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            else
            {
                await sqlSource.CopyToAsync(
                    process.StandardInput.BaseStream,
                    64 * 1_024,
                    cancellationToken
                );
            }
            Report(totalBytes, force: true);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    internal static string NormalizeMySql(
        string sql,
        ref int fixes,
        bool mergeExisting = false
    )
    {
        var normalizer = new MySqlStreamNormalizer(mergeExisting);
        var normalized = normalizer.Rewrite(sql);
        fixes += normalizer.Fixes;
        return normalized;
    }

    internal sealed class MySqlStreamNormalizer(bool mergeExisting)
    {
        private bool skippingDestructiveStatement;
        private bool atLineStart = true;
        private bool escaped;
        private bool inBlockComment;
        private bool inLineComment;
        private char quote;

        public int Fixes { get; private set; }

        public string Rewrite(string sql)
        {
            var normalized = UnsupportedMySqlCollation.Replace(sql, match =>
            {
                Fixes++;
                return match.Groups[1].Value + "utf8mb4_unicode_ci";
            });
            if (!mergeExisting) return normalized;

            return RewriteMergeStatements(normalized);
        }

        private string RewriteMergeStatements(string sql)
        {
            var output = new StringBuilder(sql.Length);
            var index = 0;
            while (index < sql.Length)
            {
                if (skippingDestructiveStatement)
                {
                    SkipDestructiveCharacter(sql, output, ref index);
                    continue;
                }

                if (quote == '\0' && !inBlockComment && !inLineComment && atLineStart)
                {
                    var token = index;
                    while (token < sql.Length && sql[token] is ' ' or '\t') token++;
                    if (token > index)
                    {
                        output.Append(sql.AsSpan(index, token - index));
                        index = token;
                        continue;
                    }
                    if (TryConsumePhrase(sql, token, "DROP", "TABLE", out _)
                        || TryConsumePhrase(sql, token, "DROP", "DATABASE", out _)
                        || TryConsumePhrase(sql, token, "TRUNCATE", "TABLE", out _)
                        || TryConsumePhrase(sql, token, "DELETE", "FROM", out _)
                        || TryConsumePhrase(sql, token, "ALTER", "TABLE", out _)
                        || TryConsumePhrase(sql, token, "CREATE", "DATABASE", out _)
                        || TryConsumeKeyword(sql, token, "UPDATE", out _)
                        || TryConsumeKeyword(sql, token, "USE", out _))
                    {
                        skippingDestructiveStatement = true;
                        Fixes++;
                        continue;
                    }
                    if (TryConsumePhrase(sql, token, "CREATE", "TABLE", out var createEnd)
                        && !TryConsumePhrase(sql, createEnd, "IF", "NOT", "EXISTS", out _))
                    {
                        output.Append("CREATE TABLE IF NOT EXISTS");
                        index = createEnd;
                        atLineStart = false;
                        Fixes++;
                        continue;
                    }
                    if (TryConsumePhrase(sql, token, "INSERT", "INTO", out var insertEnd)
                        || TryConsumePhrase(sql, token, "REPLACE", "INTO", out insertEnd))
                    {
                        output.Append("INSERT IGNORE INTO");
                        index = insertEnd;
                        atLineStart = false;
                        Fixes++;
                        continue;
                    }
                }

                AppendCharacter(sql, output, ref index);
            }
            return output.ToString();
        }

        private void SkipDestructiveCharacter(
            string sql,
            StringBuilder output,
            ref int index
        )
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
            }
            else if (character is '\'' or '"' or '`')
            {
                quote = character;
            }
            else if (character == ';')
            {
                skippingDestructiveStatement = false;
                atLineStart = false;
            }
            if (character == '\n')
            {
                output.Append(character);
                atLineStart = true;
            }
            index++;
        }

        private void AppendCharacter(string sql, StringBuilder output, ref int index)
        {
            var character = sql[index];
            output.Append(character);
            if (inLineComment)
            {
                if (character == '\n')
                {
                    inLineComment = false;
                    atLineStart = true;
                }
                index++;
                return;
            }
            if (inBlockComment)
            {
                if (character == '*' && index + 1 < sql.Length && sql[index + 1] == '/')
                {
                    output.Append('/');
                    index += 2;
                    inBlockComment = false;
                    return;
                }
                if (character == '\n') atLineStart = true;
                else if (character is not ' ' and not '\t' and not '\r') atLineStart = false;
                index++;
                return;
            }
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        output.Append(quote);
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                if (character == '\n') atLineStart = true;
                else if (character is not ' ' and not '\t' and not '\r') atLineStart = false;
                index++;
                return;
            }
            if (character is '\'' or '"' or '`')
            {
                quote = character;
            }
            else if (character == '#')
            {
                inLineComment = true;
            }
            else if (character == '-' && index + 2 < sql.Length
                && sql[index + 1] == '-' && char.IsWhiteSpace(sql[index + 2]))
            {
                output.Append('-');
                index++;
                inLineComment = true;
            }
            else if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                output.Append('*');
                index++;
                inBlockComment = true;
            }
            if (character == '\n') atLineStart = true;
            else if (character is not ' ' and not '\t' and not '\r') atLineStart = false;
            index++;
        }

        private static bool TryConsumePhrase(
            string sql,
            int start,
            string first,
            string second,
            out int end
        )
        {
            end = start;
            return ConsumeKeyword(sql, ref end, first)
                && ConsumeRequiredWhitespace(sql, ref end)
                && ConsumeKeyword(sql, ref end, second);
        }

        private static bool TryConsumeKeyword(
            string sql,
            int start,
            string keyword,
            out int end
        )
        {
            end = start;
            return ConsumeKeyword(sql, ref end, keyword);
        }

        private static bool TryConsumePhrase(
            string sql,
            int start,
            string first,
            string second,
            string third,
            out int end
        )
        {
            end = start;
            return ConsumeKeyword(sql, ref end, first)
                && ConsumeRequiredWhitespace(sql, ref end)
                && ConsumeKeyword(sql, ref end, second)
                && ConsumeRequiredWhitespace(sql, ref end)
                && ConsumeKeyword(sql, ref end, third);
        }

        private static bool ConsumeKeyword(string sql, ref int index, string keyword)
        {
            if (!sql.AsSpan(index).StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var end = index + keyword.Length;
            if (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
            {
                return false;
            }
            index = end;
            return true;
        }

        private static bool ConsumeRequiredWhitespace(string sql, ref int index)
        {
            if (index >= sql.Length || !char.IsWhiteSpace(sql[index])) return false;
            while (index < sql.Length && char.IsWhiteSpace(sql[index])) index++;
            return true;
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
