using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public enum SiteWarningKind
{
    PhpNotInstalled,
    EnvironmentMissing,
    AppKeyMissing,
    DependenciesMissing
}

// Detail is the PHP cycle for PhpNotInstalled and empty otherwise.
public sealed record SiteWarning(SiteWarningKind Kind, string Detail);

// Cheap problems shown as a badge on the site row. Only file checks: no processes, no
// network, so it can run for every site after each scan. Site Doctor stays the full check.
public static class SiteWarnings
{
    public static IReadOnlyList<SiteWarning> Inspect(
        SiteRecord site,
        string defaultPhpCycle,
        IReadOnlyCollection<string> installedPhpCycles
    )
    {
        var path = site.Path;
        if (string.IsNullOrWhiteSpace(path)) return [];
        try
        {
            if (!Directory.Exists(path)) return [];
            var warnings = new List<SiteWarning>();
            var usesPhp = !site.Framework.Equals("Node.js", StringComparison.OrdinalIgnoreCase);
            var cycle = site.PhpVersion ?? defaultPhpCycle;
            if (usesPhp && !string.IsNullOrWhiteSpace(cycle)
                && !installedPhpCycles.Contains(cycle, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add(new SiteWarning(SiteWarningKind.PhpNotInstalled, cycle));
            }
            if (File.Exists(Path.Combine(path, "artisan")))
            {
                var environmentPath = Path.Combine(path, ".env");
                if (!File.Exists(environmentPath))
                {
                    warnings.Add(new SiteWarning(SiteWarningKind.EnvironmentMissing, string.Empty));
                }
                else
                {
                    var info = new FileInfo(environmentPath);
                    if (info.Length <= ProjectEnvironmentFile.MaximumFileBytes
                        && string.IsNullOrWhiteSpace(SiteHealthInspector.EnvironmentValue(
                            File.ReadAllText(environmentPath),
                            "APP_KEY"
                        )))
                    {
                        warnings.Add(new SiteWarning(SiteWarningKind.AppKeyMissing, string.Empty));
                    }
                }
            }
            if (File.Exists(Path.Combine(path, "composer.json"))
                && !File.Exists(Path.Combine(path, "vendor", "autoload.php")))
            {
                warnings.Add(new SiteWarning(SiteWarningKind.DependenciesMissing, string.Empty));
            }
            return warnings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }
}
