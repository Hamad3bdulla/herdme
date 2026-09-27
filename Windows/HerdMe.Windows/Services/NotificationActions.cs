namespace HerdMe.Windows.Services;

public enum NotificationActionKind
{
    OpenPage,
    OpenSite,
    OpenSiteLogs,
    StartService
}

// One button on a notification. LabelKey is a resource key; Argument is a page tag, a site
// (name, domain or folder) or a service id.
public sealed record NotificationAction(NotificationActionKind Kind, string Argument, string LabelKey);

/// <summary>
/// Turns notification buttons into the short argument string Windows hands back when one is
/// clicked, and back again. Anything that does not parse is ignored, so a stale or crafted
/// argument can only fail to do something, never do something else.
/// </summary>
public static class NotificationActions
{
    private const string Prefix = "herdme-action=";
    private const char Separator = ';';
    private const int MaximumLength = 1024;

    // Pages a notification may open. Anything else is refused.
    public static readonly IReadOnlyList<string> Pages =
    [
        "dashboard", "sites", "services", "mail", "logs", "general", "updates"
    ];

    public static NotificationAction OpenPage(string page, string labelKey) =>
        new(NotificationActionKind.OpenPage, page, labelKey);

    public static NotificationAction OpenSite(string site) =>
        new(NotificationActionKind.OpenSite, site, "NotificationActionOpenSite");

    public static NotificationAction OpenSiteLogs(string site) =>
        new(NotificationActionKind.OpenSiteLogs, site, "NotificationActionOpenLogs");

    public static NotificationAction StartService(Guid id) =>
        new(NotificationActionKind.StartService, id.ToString("D"), "NotificationActionRestartService");

    public static string Build(NotificationAction action) =>
        Prefix + KindName(action.Kind) + Separator + Uri.EscapeDataString(action.Argument);

    public static NotificationAction? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength) return null;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var body = text[Prefix.Length..];
        var split = body.IndexOf(Separator);
        if (split <= 0) return null;
        var kind = ParseKind(body[..split]);
        if (kind is null) return null;
        string argument;
        try
        {
            argument = Uri.UnescapeDataString(body[(split + 1)..]);
        }
        catch (UriFormatException)
        {
            return null;
        }
        if (argument.Length == 0 || argument.Any(char.IsControl)) return null;
        return kind.Value switch
        {
            NotificationActionKind.OpenPage when Pages.Contains(argument, StringComparer.Ordinal) =>
                new(kind.Value, argument, string.Empty),
            NotificationActionKind.StartService when Guid.TryParseExact(argument, "D", out var id) =>
                new(kind.Value, id.ToString("D"), string.Empty),
            NotificationActionKind.OpenSite or NotificationActionKind.OpenSiteLogs =>
                new(kind.Value, argument, string.Empty),
            _ => null
        };
    }

    private static string KindName(NotificationActionKind kind) => kind switch
    {
        NotificationActionKind.OpenPage => "page",
        NotificationActionKind.OpenSite => "site",
        NotificationActionKind.OpenSiteLogs => "site-logs",
        NotificationActionKind.StartService => "start-service",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static NotificationActionKind? ParseKind(string name) => name switch
    {
        "page" => NotificationActionKind.OpenPage,
        "site" => NotificationActionKind.OpenSite,
        "site-logs" => NotificationActionKind.OpenSiteLogs,
        "start-service" => NotificationActionKind.StartService,
        _ => null
    };
}
