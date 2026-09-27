using HerdMe.Windows.Services;

namespace HerdMe.Windows;

// Tray notifications for things that stop without the user asking (a crashed service, a
// failed worker, a dropped public link) and for an update found while HerdMe is in the tray.
// They use the tray icon's own balloon, so nothing is registered with Windows. General has a
// switch to turn them off; acceptance runs never show them.
public partial class App
{
    private readonly NotificationThrottle notificationThrottle = new();
    private bool suppressNotifications;
    private CrashReporter? crashReporter;

    private void InstallCrashReporter()
    {
        crashReporter = new CrashReporter(services.SiteSettings.SupportRoot, services.Updates.CurrentVersion);
        crashReporter.Install();
    }

    private void SubscribeNotifications()
    {
        services.Services.ExitedUnexpectedly += Services_ExitedUnexpectedly;
        services.SiteProcesses.ExitedUnexpectedly += SiteProcesses_ExitedUnexpectedly;
        services.Shares.ShareEndedUnexpectedly += Shares_ShareEndedUnexpectedly;
    }

    private void UnsubscribeNotifications()
    {
        services.Services.ExitedUnexpectedly -= Services_ExitedUnexpectedly;
        services.SiteProcesses.ExitedUnexpectedly -= SiteProcesses_ExitedUnexpectedly;
        services.Shares.ShareEndedUnexpectedly -= Shares_ShareEndedUnexpectedly;
    }

    private void Services_ExitedUnexpectedly(object? sender, ServiceExitedEventArgs args)
    {
        Notify(AppNotifications.ServiceStopped(args.Name, args.ExitCode));
    }

    private void SiteProcesses_ExitedUnexpectedly(object? sender, SiteBackgroundProcessState state)
    {
        var siteName = Path.GetFileName(Path.TrimEndingDirectorySeparator(state.SitePath));
        Notify(AppNotifications.SiteProcessStopped(siteName, state.Kind, state.ExitCode ?? -1));
    }

    private void Shares_ShareEndedUnexpectedly(object? sender, SiteShare share)
    {
        Notify(AppNotifications.ShareEnded(share.Domain));
    }

    private void NotifyUpdateAvailable(AppUpdateRelease release)
    {
        // A visible window already shows the update dialog.
        if (IsMainWindowVisible) return;
        Notify(AppNotifications.UpdateAvailable(release.Version));
    }

    private void NotifyPreviousCrash()
    {
        if (crashReporter?.ConsumePendingCrash() == true) Notify(AppNotifications.CrashReported());
    }

    private void Notify(AppNotification notification)
    {
        if (suppressNotifications || exitRequested || MainWindow is null) return;
        bool enabled;
        try
        {
            enabled = services.SiteSettings.Load().ShowNotifications;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            enabled = true;
        }
        if (!enabled || !notificationThrottle.TryAcquire(notification.Key, DateTimeOffset.UtcNow)) return;
        MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (exitRequested || trayIcon is null) return;
            try
            {
                trayIcon.ShowNotification(notification.Title, notification.Message);
            }
            catch (Exception error) when (error is InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                _ = DiagnosticLog.WriteFailureAsync(
                    "notifications",
                    "show-failed",
                    "HerdMe could not show a tray notification.",
                    error.ToString()
                );
            }
        });
    }
}
