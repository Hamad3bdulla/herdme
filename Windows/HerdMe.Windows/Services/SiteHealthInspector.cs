using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record SiteHealthCheck(string Name, bool Healthy, string Detail);

public static class SiteHealthInspector
{
    public static async Task<IReadOnlyList<SiteHealthCheck>> InspectAsync(
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
            checks.Insert(1, new("Environment", environment.Exists, environment.Exists ? ".env" : ".env is missing"));
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
                coreFiles.All(File.Exists) ? "Ready" : "artisan, bootstrap/app.php, or public/index.php is missing"
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
                !platformRequirements.Contains("composer.json is invalid", StringComparer.Ordinal),
                platformRequirements.Count == 0 ? "No explicit requirements" : string.Join(", ", platformRequirements)
            ));
        }
        if (File.Exists(Path.Combine(path, "package.json")))
        {
            checks.Add(new SiteHealthCheck(
                "Node dependencies",
                Directory.Exists(Path.Combine(path, "node_modules")),
                Directory.Exists(Path.Combine(path, "node_modules")) ? "node_modules" : "node_modules is missing"
            ));
            checks.Add(JsonFileCheck(Path.Combine(path, "package-lock.json"), "Node lock"));
            checks.Add(NodeEngineCheck(Path.Combine(path, "package.json"), nodeVersion));
            var packageManager = RuntimeHealthInspector.NodePackageManager(path);
            checks.Add(new SiteHealthCheck(
                "Node package manager",
                packageManager.Equals("npm", StringComparison.Ordinal),
                packageManager.Equals("npm", StringComparison.Ordinal)
                    ? "npm" : $"Uses {packageManager}; HerdMe will not run npm automatically"
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
                string.IsNullOrWhiteSpace(appKey) ? "APP_KEY is missing" : "APP_KEY is configured"
            ));
            var missingEnvironment = new[] { "APP_URL" }
                .Where(key => string.IsNullOrWhiteSpace(EnvironmentValue(environment.Contents, key)))
                .ToArray();
            checks.Add(new SiteHealthCheck(
                "Environment configuration",
                missingEnvironment.Length == 0,
                missingEnvironment.Length == 0 ? "Ready" : $"Missing: {string.Join(", ", missingEnvironment)}"
            ));
            var storageReady = storageDirectories.All(directory =>
                Directory.Exists(directory) && IsWritableDirectory(directory));
            checks.Add(new SiteHealthCheck(
                "Storage directories",
                storageReady,
                storageReady ? "Ready" : "Laravel writable directories are missing or read-only"
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
                    ? databaseConnection ?? "Configured"
                    : "Configure a site database in .env"
            ));
            var logDirectory = Path.Combine(path, "storage", "logs");
            var logBytes = Directory.Exists(logDirectory)
                ? Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                    .Sum(file => new FileInfo(file).Length)
                : 0;
            checks.Add(new SiteHealthCheck(
                "Laravel logs",
                logBytes < 100L * 1_024 * 1_024,
                $"{logBytes / 1_024d / 1_024d:0.0} MB"
            ));
        }
        if (phpInstaller.IsInstalled(phpCycle))
        {
            try
            {
                var report = await phpInstaller.ManagedExtensionReportAsync(phpInstaller.PhpExecutable(phpCycle), cancellationToken);
                checks.Add(new SiteHealthCheck("PHP extensions", report.Missing.Count == 0,
                    report.Missing.Count == 0 ? "Ready" : string.Join(", ", report.Missing)));
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
        if (!File.Exists(path)) return new SiteHealthCheck(name, true, "Not present");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return new SiteHealthCheck(name, document.RootElement.ValueKind == JsonValueKind.Object, "Valid");
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new SiteHealthCheck(name, false, "Invalid JSON");
        }
    }

    private static SiteHealthCheck NodeEngineCheck(string packagePath, string? configuredVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            if (!document.RootElement.TryGetProperty("engines", out var engines)
                || !engines.TryGetProperty("node", out var node))
                return new SiteHealthCheck("Node version", true, "No version constraint");
            var requirement = node.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredVersion))
                return new SiteHealthCheck("Node version", true, $"Requires Node {requirement}");
            var configuredMajor = configuredVersion.Split('.', 2)[0];
            var requiredMajor = new string(requirement.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
            var compatible = string.IsNullOrWhiteSpace(requiredMajor)
                || requirement.Contains(configuredMajor, StringComparison.Ordinal);
            return new SiteHealthCheck("Node version", compatible, compatible
                ? $"Requires Node {requirement}"
                : $"Requires Node {requirement}; configured {configuredVersion}");
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new SiteHealthCheck("Node version", false, "package.json is invalid");
        }
    }
}
