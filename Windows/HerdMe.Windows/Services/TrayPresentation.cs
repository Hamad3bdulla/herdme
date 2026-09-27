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

    // Win32 popup menus treat a single ampersand as a mnemonic marker.
    public static string MenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}
