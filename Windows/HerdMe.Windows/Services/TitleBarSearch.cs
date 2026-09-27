namespace HerdMe.Windows.Services;

public enum TitleBarSearchKind
{
    Page,
    Site,
    Action
}

public sealed record TitleBarSearchItem(TitleBarSearchKind Kind, string Key, string Label, string Detail)
{
    // Segoe Fluent Icons: page, globe, play.
    public string Glyph => Kind switch
    {
        TitleBarSearchKind.Site => "\uE774",
        TitleBarSearchKind.Action => "\uE768",
        _ => "\uE8A5"
    };

    public override string ToString() => Label;
}

/// <summary>
/// The search box in the title bar: pages, sites (by domain or folder name) and a few
/// actions. Prefix matches rank above matches inside a word; ties keep the given order.
/// </summary>
public static class TitleBarSearch
{
    public const int MaximumResults = 8;

    public static IReadOnlyList<TitleBarSearchItem> Suggest(
        string? query,
        IEnumerable<TitleBarSearchItem> pages,
        IEnumerable<TitleBarSearchItem> sites,
        IEnumerable<TitleBarSearchItem> actions,
        int maximum = MaximumResults
    )
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || maximum <= 0) return [];
        return pages.Concat(sites).Concat(actions)
            .Select((item, order) => (Item: item, Order: order, Rank: Rank(item, text)))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Order)
            .Select(candidate => candidate.Item)
            .DistinctBy(item => (item.Kind, item.Key.ToUpperInvariant()))
            .Take(maximum)
            .ToList();
    }

    // 0: label starts with the text; 1: a word in the label starts with it; 2: label or detail
    // contains it; -1: no match.
    public static int Rank(TitleBarSearchItem item, string text)
    {
        if (item.Label.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return 0;
        var words = item.Label.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))) return 1;
        if (item.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || item.Detail.Contains(text, StringComparison.CurrentCultureIgnoreCase)) return 2;
        return -1;
    }
}
