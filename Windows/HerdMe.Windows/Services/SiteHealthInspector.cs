using System.Globalization;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record SiteHealthCheck(string Name, bool Healthy, string Detail);

public static class SiteHealthInspector
{
    public static Task<IReadOnlyList<SiteHealthCheck>> InspectAsync(
        string sitePath,
        string domain,
        string phpCycle,
        PhpRuntimeInstaller phpInstaller,
        ComposerToolManager composerTools,
        WindowsCertificateManager certificates,
        string? nodeVersion = null,
        CancellationToken cancellationToken = default
    )
    {
        return InspectAsync(
            sitePath,
            domain,
            phpCycle,
            phpInstaller,
            composerTools,
            certificates,
            nodeVersion,
            extensionReport: null,
            cancellationToken
        );
    }

    // extensionReport is an optional shared lookup keyed by php.exe path. Callers that inspect
    // many sites at once (the dashboard) pass a memoized lookup so each PHP version is
    // validated once per refresh instead of once per site.
    public static async Task<IReadOnlyList<SiteHealthCheck>> InspectAsync(
        string sitePath,
        string domain,
        string phpCycle,
        PhpRuntimeInstaller phpInstaller,
        ComposerToolManager composerTools,
        WindowsCertificateManager certificates,
        string? nodeVersion,
        Func<string, CancellationToken, Task<PhpExtensionReport>>? extensionReport,
        CancellationToken cancellationToken = default
    )
    {
        var path = Path.GetFullPath(sitePath);
        if (!Directory.Exists(path))
        {
            return [new SiteHealthCheck("Project", false, path)];
        }
        var environment = ProjectEnvironmentFile.Load(path);
        var composerJsonPath = Path.Combine(path, "composer.json");
        var hasComposer = File.Exists(composerJsonPath);
        var laravel = IsLaravelProject(path);
        var checks = new List<SiteHealthCheck>
        {
            new("Project", Directory.Exists(path), path),
            new("HTTPS", certificates.IsAuthorityTrusted(), domain)
        };
        if (laravel)
        {
            checks.Insert(1, new("Environment", environment.Exists, environment.Exists ? ".env" : ServiceText.Get("Health_EnvironmentMissing", ".env is missing")));
            checks.Insert(2, new("Laravel", File.Exists(Path.Combine(path, "artisan")), "artisan"));
            var coreFiles = new[]
            {
                Path.Combine(path, "artisan"),
                Path.Combine(path, "bootstrap", "app.php"),
                Path.Combine(path, "public", "index.php")
            };
            checks.Add(new SiteHealthCheck(
                "Laravel core files",
                coreFiles.All(File.Exists),
                coreFiles.All(File.Exists)
                    ? ServiceText.Get("Health_Ready", "Ready")
                    : ServiceText.Get("Health_LaravelCoreFilesMissing", "artisan, bootstrap/app.php, or public/index.php is missing")
            ));
        }
        if (hasComposer)
        {
            checks.Insert(laravel ? 3 : 1, new("Dependencies", File.Exists(Path.Combine(path, "vendor", "autoload.php")), "vendor/autoload.php"));
            checks.Insert(laravel ? 4 : 2, new("PHP", phpInstaller.IsInstalled(phpCycle), $"PHP {phpCycle}"));
            checks.Insert(laravel ? 5 : 3, new("Composer", File.Exists(composerTools.ComposerPath), "composer.phar"));
            checks.Add(JsonFileCheck(Path.Combine(path, "composer.lock"), "Composer lock"));
            var platformRequirements = RuntimeHealthInspector.ComposerPlatformRequirements(composerJsonPath);
            checks.Add(new SiteHealthCheck(
                "PHP platform requirements",
                !platformRequirements.Contains(RuntimeHealthInspector.InvalidComposerJson, StringComparer.Ordinal),
                platformRequirements.Count == 0
                    ? ServiceText.Get("Health_NoPlatformRequirements", "No explicit requirements")
                    : string.Join(", ", platformRequirements)
            ));
        }
        if (File.Exists(Path.Combine(path, "package.json")))
        {
            checks.Add(new SiteHealthCheck(
                "Node dependencies",
                Directory.Exists(Path.Combine(path, "node_modules")),
                Directory.Exists(Path.Combine(path, "node_modules"))
                    ? "node_modules"
                    : ServiceText.Get("Health_NodeModulesMissing", "node_modules is missing")
            ));
            checks.Add(JsonFileCheck(Path.Combine(path, "package-lock.json"), "Node lock"));
            checks.Add(NodeEngineCheck(Path.Combine(path, "package.json"), nodeVersion));
            var packageManager = RuntimeHealthInspector.NodePackageManager(path);
            checks.Add(new SiteHealthCheck(
                "Node package manager",
                packageManager.Equals("npm", StringComparison.Ordinal),
                packageManager.Equals("npm", StringComparison.Ordinal)
                    ? "npm"
                    : ServiceText.Format(
                        "Health_OtherPackageManager",
                        "Uses {0}; HerdMe will not run npm automatically",
                        packageManager
                    )
            ));
        }
        if (laravel)
        {
            var appKey = EnvironmentValue(environment.Contents, "APP_KEY");
            var storageDirectories = new[]
            {
                Path.Combine(path, "storage", "framework", "cache"),
                Path.Combine(path, "storage", "framework", "sessions"),
                Path.Combine(path, "storage", "framework", "views"),
                Path.Combine(path, "storage", "logs"),
                Path.Combine(path, "bootstrap", "cache")
            };
            checks.Add(new SiteHealthCheck(
                "Application key",
                !string.IsNullOrWhiteSpace(appKey),
                string.IsNullOrWhiteSpace(appKey)
                    ? ServiceText.Get("Health_AppKeyMissing", "APP_KEY is missing")
                    : ServiceText.Get("Health_AppKeyConfigured", "APP_KEY is configured")
            ));
            var missingEnvironment = new[] { "APP_URL" }
                .Where(key => string.IsNullOrWhiteSpace(EnvironmentValue(environment.Contents, key)))
                .ToArray();
            checks.Add(new SiteHealthCheck(
                "Environment configuration",
                missingEnvironment.Length == 0,
                missingEnvironment.Length == 0
                    ? ServiceText.Get("Health_Ready", "Ready")
                    : ServiceText.Format("Health_MissingEnvironment", "Missing: {0}", string.Join(", ", missingEnvironment))
            ));
            var storageReady = storageDirectories.All(directory =>
                Directory.Exists(directory) && IsWritableDirectory(directory));
            checks.Add(new SiteHealthCheck(
                "Storage directories",
                storageReady,
                storageReady
                    ? ServiceText.Get("Health_Ready", "Ready")
                    : ServiceText.Get("Health_StorageNotWritable", "Laravel writable directories are missing or read-only")
            ));
            checks.Add(new SiteHealthCheck(
                "Storage link",
                Directory.Exists(Path.Combine(path, "public", "storage")),
                "public/storage"
            ));
            var databaseConnection = EnvironmentValue(environment.Contents, "DB_CONNECTION");
            var databaseName = EnvironmentValue(environment.Contents, "DB_DATABASE");
            var databaseConfigured = !string.IsNullOrWhiteSpace(databaseConnection)
                && (databaseConnection.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
                    ? File.Exists(Path.Combine(path, "database", "database.sqlite"))
                        || !string.IsNullOrWhiteSpace(databaseName)
                    : !string.IsNullOrWhiteSpace(databaseName));
            checks.Add(new SiteHealthCheck(
                "Database configuration",
                databaseConfigured,
                databaseConfigured
                    ? databaseConnection ?? ServiceText.Get("Health_Configured", "Configured")
                    : ServiceText.Get("Health_ConfigureDatabase", "Configure a site database in .env")
            ));
            var logDirectory = Path.Combine(path, "storage", "logs");
            var logBytes = Directory.Exists(logDirectory)
                ? Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
            checks.Add(new SiteHealthCheck(
                "Laravel logs",
                logBytes < 100L * 1_024 * 1_024,
                ServiceText.Format(
                    "Health_LogSize",
                    "{0} MB",
                    (logBytes / 1_024d / 1_024d).ToString("0.0", CultureInfo.CurrentCulture)
                )
            ));
        }
        if (phpInstaller.IsInstalled(phpCycle))
        {
            try
            {
                var phpExecutable = phpInstaller.PhpExecutable(phpCycle);
                var report = extensionReport is null
                    ? await phpInstaller.ManagedExtensionReportAsync(phpExecutable, cancellationToken)
                    : await extensionReport(phpExecutable, cancellationToken);
                checks.Add(new SiteHealthCheck("PHP extensions", report.Missing.Count == 0,
                    report.Missing.Count == 0
                        ? ServiceText.Get("Health_Ready", "Ready")
                        : string.Join(", ", report.Missing)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException)
            {
                checks.Add(new SiteHealthCheck("PHP extensions", false, error.Message));
            }
        }
        return checks;
    }

    internal static string? EnvironmentValue(string contents, string key)
    {
        foreach (var line in contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var candidate = line.TrimStart();
            if (candidate.StartsWith('#')) continue;
            var separator = candidate.IndexOf('=');
            if (separator < 0 || !candidate[..separator].Trim().Equals(key, StringComparison.Ordinal))
            {
                continue;
            }
            return candidate[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return null;
    }

    internal static bool IsLaravelProject(string projectPath)
    {
        if (File.Exists(Path.Combine(projectPath, "artisan"))) return true;
        var composerJsonPath = Path.Combine(projectPath, "composer.json");
        if (!File.Exists(composerJsonPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(composerJsonPath));
            foreach (var section in new[] { "require", "require-dev" })
            {
                if (document.RootElement.TryGetProperty(section, out var dependencies)
                    && dependencies.TryGetProperty("laravel/framework", out _)) return true;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return false;
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".herdme-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SiteHealthCheck JsonFileCheck(string path, string name)
    {
        if (!File.Exists(path)) return new SiteHealthCheck(name, true, ServiceText.Get("Health_NotPresent", "Not present"));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return new SiteHealthCheck(name, document.RootElement.ValueKind == JsonValueKind.Object,
                ServiceText.Get("Health_Valid", "Valid"));
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new SiteHealthCheck(name, false, ServiceText.Get("Health_InvalidJson", "Invalid JSON"));
        }
    }

    private static SiteHealthCheck NodeEngineCheck(string packagePath, string? configuredVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            if (!document.RootElement.TryGetProperty("engines", out var engines)
                || !engines.TryGetProperty("node", out var node))
                return new SiteHealthCheck("Node version", true,
                    ServiceText.Get("Health_NoNodeConstraint", "No version constraint"));
            var requirement = node.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredVersion))
                return new SiteHealthCheck("Node version", true,
                    ServiceText.Format("Health_RequiresNode", "Requires Node {0}", requirement));
            var configuredMajor = configuredVersion.Split('.', 2)[0];
            var requiredMajor = new string(requirement.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
            var compatible = string.IsNullOrWhiteSpace(requiredMajor)
                || requirement.Contains(configuredMajor, StringComparison.Ordinal);
            return new SiteHealthCheck("Node version", compatible, compatible
                ? ServiceText.Format("Health_RequiresNode", "Requires Node {0}", requirement)
                : ServiceText.Format(
                    "Health_RequiresNodeConfigured",
                    "Requires Node {0}; configured {1}",
                    requirement,
                    configuredVersion
                ));
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new SiteHealthCheck("Node version", false,
                ServiceText.Get("Health_PackageJsonInvalid", "package.json is invalid"));
        }
    }
}
