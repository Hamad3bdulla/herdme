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
        CancellationToken cancellationToken = default
    )
    {
        var path = Path.GetFullPath(sitePath);
        if (!Directory.Exists(path))
        {
            return [new SiteHealthCheck("Project", false, path)];
        }
        var environment = ProjectEnvironmentFile.Load(path);
        var laravel = File.Exists(Path.Combine(path, "artisan"));
        var checks = new List<SiteHealthCheck>
        {
            new("Project", Directory.Exists(path), path),
            new("Environment", environment.Exists, environment.Exists ? ".env" : ".env is missing"),
            new("Laravel", laravel, "artisan"),
            new("Dependencies", File.Exists(Path.Combine(path, "vendor", "autoload.php")), "vendor/autoload.php"),
            new("PHP", phpInstaller.IsInstalled(phpCycle), $"PHP {phpCycle}"),
            new("Composer", File.Exists(composerTools.ComposerPath), "composer.phar"),
            new("HTTPS", certificates.IsAuthorityTrusted(), domain)
        };
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
            checks.Add(new SiteHealthCheck(
                "Storage directories",
                storageDirectories.All(Directory.Exists),
                storageDirectories.All(Directory.Exists) ? "Ready" : "Laravel writable directories are missing"
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
        }
        if (phpInstaller.IsInstalled(phpCycle))
        {
            try
            {
                await phpInstaller.EnsureManagedConfigurationAsync(phpCycle, cancellationToken);
                checks.Add(new SiteHealthCheck("PHP extensions", true, "Ready"));
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
}
