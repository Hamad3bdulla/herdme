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
        var checks = new List<SiteHealthCheck>
        {
            new("Project", Directory.Exists(path), path),
            new("Environment", File.Exists(Path.Combine(path, ".env")), ".env"),
            new("Laravel", File.Exists(Path.Combine(path, "artisan")), "artisan"),
            new("Dependencies", File.Exists(Path.Combine(path, "vendor", "autoload.php")), "vendor/autoload.php"),
            new("PHP", phpInstaller.IsInstalled(phpCycle), $"PHP {phpCycle}"),
            new("Composer", File.Exists(composerTools.ComposerPath), "composer.phar"),
            new("HTTPS", certificates.IsAuthorityTrusted(), domain)
        };
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
}
