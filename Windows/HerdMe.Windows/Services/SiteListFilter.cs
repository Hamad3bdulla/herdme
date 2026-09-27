using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public enum SiteListFilterKind
{
    All,
    Favorites,
    Laravel,
    Running,
    Errors,
    Shared
}

public enum SiteListSortKind
{
    FavoritesFirst,
    Name,
    Framework
}

// The Sites list query: free-text search, one quick filter, and a sort order. Pure so the
// contract tests can pin the behaviour without WinUI.
public static class SiteListFilter
{
    public static SiteListFilterKind ParseFilter(string? value) =>
        Enum.TryParse<SiteListFilterKind>(value, ignoreCase: false, out var kind) ? kind : SiteListFilterKind.All;

    public static SiteListSortKind ParseSort(string? value) =>
        Enum.TryParse<SiteListSortKind>(value, ignoreCase: false, out var kind) ? kind : SiteListSortKind.FavoritesFirst;

    public static bool Matches(SiteRecord site, SiteListFilterKind filter) => filter switch
    {
        SiteListFilterKind.Favorites => site.IsFavorite,
        SiteListFilterKind.Laravel => string.Equals(site.Framework, "Laravel", StringComparison.Ordinal),
        SiteListFilterKind.Running => site.IsRunning,
        SiteListFilterKind.Errors => !string.IsNullOrWhiteSpace(site.LastError),
        SiteListFilterKind.Shared => site.IsShared,
        _ => true
    };

    public static List<SiteRecord> Apply(
        IEnumerable<SiteRecord> sites,
        string? query,
        SiteListFilterKind filter,
        SiteListSortKind sort
    )
    {
        var matches = SitePresentation.Filter(sites, query).Where(site => Matches(site, filter));
        return (sort switch
        {
            SiteListSortKind.Name => matches
                .OrderBy(site => site.Domain, StringComparer.OrdinalIgnoreCase),
            SiteListSortKind.Framework => matches
                .OrderBy(site => site.Framework, StringComparer.OrdinalIgnoreCase)
                .ThenBy(site => site.Domain, StringComparer.OrdinalIgnoreCase),
            // Stable: favorites float up and scan order is kept otherwise.
            _ => matches.OrderByDescending(site => site.IsFavorite)
        }).ToList();
    }
}
