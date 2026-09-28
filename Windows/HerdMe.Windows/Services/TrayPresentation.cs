using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public enum TrayIconState
{
    Running,
    Recovering,
    Stopped
}

// What the notification-area icon shows. Pure so the contract tests can pin it.
public static class TrayPresentation
{
    public const int SiteMenuLimit = 10;

    public static TrayIconState Choose(bool running, bool degraded) =>
        running ? TrayIconState.Running : degraded ? TrayIconState.Recovering : TrayIconState.Stopped;

    // Running keeps the normal app icon; the other states add an amber or grey badge.
    public static string IconUri(TrayIconState state) => state switch
    {
        TrayIconState.Recovering => "ms-appx:///Assets/HerdMe-Degraded.ico",
        TrayIconState.Stopped => "ms-appx:///Assets/HerdMe-Stopped.ico",
        _ => "ms-appx:///Assets/HerdMe.ico"
    };

    // Favorites first, then by domain, capped so the menu stays short.
    public static IReadOnlyList<SiteRecord> MenuSites(IEnumerable<SiteRecord> sites) =>
        sites
            .OrderByDescending(site => site.IsFavorite)
            .ThenBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)
            .Take(SiteMenuLimit)
            .ToList();

    public const int RecentLimit = 3;

    // The known sites the user opened last, newest first. Paths that are no longer sites
    // (removed or unlinked) are skipped.
    public static IReadOnlyList<SiteRecord> RecentSites(IEnumerable<SiteRecord> sites, IEnumerable<string> recentPaths)
    {
        var known = sites
            .GroupBy(site => site.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return recentPaths
            .Where(known.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => known[path])
            .Take(RecentLimit)
            .ToList();
    }

    // The rest of the menu after the recent sites, so the menu stays SiteMenuLimit long.
    public static IReadOnlyList<SiteRecord> MenuSites(IEnumerable<SiteRecord> sites, IReadOnlyList<SiteRecord> recent)
    {
        var shown = recent.Select(site => site.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sites
            .Where(site => !shown.Contains(site.Path))
            .OrderByDescending(site => site.IsFavorite)
            .ThenBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, SiteMenuLimit - recent.Count))
            .ToList();
    }

    // Win32 popup menus treat a single ampersand as a mnemonic marker.
    public static string MenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}
