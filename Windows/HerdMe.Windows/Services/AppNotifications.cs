namespace HerdMe.Windows.Services;

// Primary runs when the notification itself is clicked; Secondary is a second button. Both are
// optional and only ever open HerdMe pages or start a HerdMe service.
public sealed record AppNotification(
    string Key,
    string Title,
    string Message,
    NotificationAction? Primary = null,
    NotificationAction? Secondary = null
);

// Text for the tray notifications HerdMe shows when something stops without the user asking.
// By default they are local tray balloons: nothing is registered with Windows and nothing is
// sent anywhere. Buttons on Windows notifications are a separate opt-in in General. They are
// off in General when the user does not want them.
public static class AppNotifications
{
    public static AppNotification ServiceStopped(string name, int? exitCode, Guid? id = null) => new(
        "service:" + name,
        ServiceText.Get("NotificationServiceStoppedTitle", "Service stopped"),
        exitCode is { } code
            ? ServiceText.Format(
                "NotificationServiceStoppedWithCode",
                "{0} stopped unexpectedly (exit code {1}).",
                name,
                code
            )
            : ServiceText.Format("NotificationServiceStoppedMessage", "{0} stopped unexpectedly.", name),
        id is { } serviceId ? NotificationActions.StartService(serviceId) : null,
        NotificationActions.OpenPage("services", "NotificationActionOpenServices")
    );

    public static AppNotification SiteProcessStopped(
        string siteName,
        SiteBackgroundProcessKind kind,
        int exitCode,
        string? sitePath = null
    ) => new(
        "process:" + siteName + ":" + kind,
        ServiceText.Get("NotificationSiteProcessStoppedTitle", "Site process stopped"),
        ServiceText.Format(
            "NotificationSiteProcessStoppedMessage",
            "{0} for {1} stopped with exit code {2}.",
            ProcessName(kind),
            siteName,
            exitCode
        ),
        string.IsNullOrEmpty(sitePath) ? null : NotificationActions.OpenSite(sitePath),
        string.IsNullOrEmpty(sitePath) ? null : NotificationActions.OpenSiteLogs(sitePath)
    );

    public static AppNotification ShareEnded(string domain) => new(
        "share:" + domain,
        ServiceText.Get("NotificationShareEndedTitle", "Public link ended"),
        ServiceText.Format(
            "NotificationShareEndedMessage",
            "The public link for {0} stopped. The site is private again.",
            domain
        ),
        NotificationActions.OpenSite(domain)
    );

    public static AppNotification UpdateAvailable(string version) => new(
        "update:" + version,
        ServiceText.Get("NotificationUpdateTitle", "HerdMe update available"),
        ServiceText.Format("NotificationUpdateMessage", "HerdMe {0} is ready. Open HerdMe to install it.", version),
        NotificationActions.OpenPage("updates", "NotificationActionOpenUpdates")
    );

    // The quiet daily check: one notification for everything it found, never a dialog.
    public static AppNotification UpdatesAvailable(int count) => new(
        "updates-available",
        ServiceText.Get("NotificationUpdatesTitle", "Updates available"),
        count == 1
            ? ServiceText.Get("NotificationUpdatesOne", "1 update is ready to install.")
            : ServiceText.Format("NotificationUpdatesMany", "{0} updates are ready to install.", count),
        NotificationActions.OpenPage("updates", "NotificationActionOpenUpdates")
    );

    // Automatic installation (on idle or on exit) finished.
    public static AppNotification UpdatesInstalled(int updated, int failed) => new(
        "updates-installed",
        failed == 0
            ? ServiceText.Get("NotificationUpdatesInstalledTitle", "Updates installed")
            : ServiceText.Get("NotificationUpdatesFailedTitle", "Some updates did not install"),
        failed == 0
            ? ServiceText.Format("NotificationUpdatesInstalledMessage", "{0} updated. Open Updates to see what changed.", updated)
            : ServiceText.Format("NotificationUpdatesFailedMessage", "{0} updated, {1} did not. Open Updates to retry.", updated, failed),
        NotificationActions.OpenPage("updates", "NotificationActionOpenUpdates")
    );

    public const int MailSnippetCharacters = 120;

    // Captured mail stays on this PC; the notification shows only sender and subject.
    public static AppNotification MailCaptured(string? subject, string? sender, int unseen)
    {
        var title = string.IsNullOrWhiteSpace(subject)
            ? ServiceText.Get("NotificationMailNoSubject", "(no subject)")
            : subject;
        var from = string.IsNullOrWhiteSpace(sender)
            ? ServiceText.Get("NotificationMailTitle", "New mail")
            : ServiceText.Format("NotificationMailFrom", "New mail from {0}", sender);
        var message = Snippet(title);
        if (unseen > 1)
        {
            message = ServiceText.Format("NotificationMailMore", "{0} ({1} unread)", message, unseen);
        }
        return new AppNotification(
            "mail",
            Snippet(from),
            message,
            NotificationActions.OpenPage("mail", "NotificationActionOpenMail")
        );
    }

    private static string Snippet(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > MailSnippetCharacters ? line[..(MailSnippetCharacters - 3)] + "..." : line;
    }

    public static AppNotification CrashReported() => new(
        "crash",
        ServiceText.Get("NotificationCrashTitle", "HerdMe closed unexpectedly"),
        ServiceText.Get(
            "NotificationCrashMessage",
            "A crash report was saved on this PC. Use Export diagnostics in General if you want to share it."
        ),
        NotificationActions.OpenPage("general", "NotificationActionOpenGeneral")
    );

    private static string ProcessName(SiteBackgroundProcessKind kind) => kind switch
    {
        SiteBackgroundProcessKind.Queue => ServiceText.Get("NotificationProcessQueue", "Queue worker"),
        SiteBackgroundProcessKind.Scheduler => ServiceText.Get("NotificationProcessScheduler", "Scheduler"),
        _ => ServiceText.Get("NotificationProcessDevelopment", "Development server")
    };
}

// Keeps a crashing service or a flapping tunnel from filling the notification area.
public sealed class NotificationThrottle(TimeSpan interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> lastShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    public NotificationThrottle() : this(DefaultInterval)
    {
    }

    public bool TryAcquire(string key, DateTimeOffset now)
    {
        lock (sync)
        {
            if (lastShown.TryGetValue(key, out var previous) && now - previous < interval) return false;
            lastShown[key] = now;
            if (lastShown.Count > 256)
            {
                foreach (var stale in lastShown.Where(item => now - item.Value >= interval).Select(item => item.Key).ToList())
                {
                    lastShown.Remove(stale);
                }
            }
            return true;
        }
    }
}
