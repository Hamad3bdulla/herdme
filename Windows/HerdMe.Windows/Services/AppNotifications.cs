namespace HerdMe.Windows.Services;

public sealed record AppNotification(string Key, string Title, string Message);

// Text for the tray notifications HerdMe shows when something stops without the user asking.
// Notifications are local tray balloons: nothing is registered with Windows and nothing is sent
// anywhere. They are off in General when the user does not want them.
public static class AppNotifications
{
    public static AppNotification ServiceStopped(string name, int? exitCode) => new(
        "service:" + name,
        ServiceText.Get("NotificationServiceStoppedTitle", "Service stopped"),
        exitCode is { } code
            ? ServiceText.Format(
                "NotificationServiceStoppedWithCode",
                "{0} stopped unexpectedly (exit code {1}).",
                name,
                code
            )
            : ServiceText.Format("NotificationServiceStoppedMessage", "{0} stopped unexpectedly.", name)
    );

    public static AppNotification SiteProcessStopped(string siteName, SiteBackgroundProcessKind kind, int exitCode) => new(
        "process:" + siteName + ":" + kind,
        ServiceText.Get("NotificationSiteProcessStoppedTitle", "Site process stopped"),
        ServiceText.Format(
            "NotificationSiteProcessStoppedMessage",
            "{0} for {1} stopped with exit code {2}.",
            ProcessName(kind),
            siteName,
            exitCode
        )
    );

    public static AppNotification ShareEnded(string domain) => new(
        "share:" + domain,
        ServiceText.Get("NotificationShareEndedTitle", "Public link ended"),
        ServiceText.Format(
            "NotificationShareEndedMessage",
            "The public link for {0} stopped. The site is private again.",
            domain
        )
    );

    public static AppNotification UpdateAvailable(string version) => new(
        "update:" + version,
        ServiceText.Get("NotificationUpdateTitle", "HerdMe update available"),
        ServiceText.Format("NotificationUpdateMessage", "HerdMe {0} is ready. Open HerdMe to install it.", version)
    );

    public static AppNotification CrashReported() => new(
        "crash",
        ServiceText.Get("NotificationCrashTitle", "HerdMe closed unexpectedly"),
        ServiceText.Get(
            "NotificationCrashMessage",
            "A crash report was saved on this PC. Use Export diagnostics in General if you want to share it."
        )
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
