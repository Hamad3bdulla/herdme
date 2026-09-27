using HerdMe.Windows.Services;

namespace HerdMe.Windows;

// Tray notifications for things that stop without the user asking (a crashed service, a
// failed worker, a dropped public link) and for an update found while HerdMe is in the tray.
// By default they use the tray icon's own balloon, so nothing is registered with Windows;
// clicking the balloon runs the notification's main action. "Buttons on notifications" in
// General is a separate opt-in (see App.ActionNotifications.cs). General has a switch to turn
// them off; acceptance runs never show them.
public partial class App
{
    private readonly NotificationThrottle notificationThrottle = new();
    private bool suppressNotifications;
    private CrashReporter? crashReporter;
    private NotificationAction? balloonAction;

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
        Notify(AppNotifications.ServiceStopped(args.Name, args.ExitCode, args.Id));
    }

    private void SiteProcesses_ExitedUnexpectedly(object? sender, SiteBackgroundProcessState state)
    {
        var siteName = Path.GetFileName(Path.TrimEndingDirectorySeparator(state.SitePath));
        Notify(AppNotifications.SiteProcessStopped(siteName, state.Kind, state.ExitCode ?? -1, state.SitePath));
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

    // A long operation ended while the window was not in front (MainWindow.FinishAlert.cs).
    internal void NotifyOperationFinished(AppNotification notification) => Notify(notification);

    private void NotifyPreviousCrash()
    {
        if (crashReporter?.ConsumePendingCrash() == true) Notify(AppNotifications.CrashReported());
    }

    private void Notify(AppNotification notification)
    {
        if (suppressNotifications || exitRequested || MainWindow is null) return;
        bool enabled;
        var withButtons = false;
        try
        {
            var settings = services.SiteSettings.Load();
            enabled = settings.ShowNotifications;
            withButtons = settings.ActionNotifications;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            enabled = true;
        }
        var now = DateTimeOffset.UtcNow;
        var show = enabled && notificationThrottle.TryAcquire(notification.Key, now);
        // The bell keeps every notification, also the ones Windows was not asked to show.
        services.NotificationHistory.Add(notification, now, show);
        if (!show) return;
        MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (exitRequested || trayIcon is null) return;
            if (withButtons && TryShowActionNotification(notification)) return;
            try
            {
                // Clicking the balloon runs the main action of the newest one.
                balloonAction = notification.Primary;
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

    // Wired in InitializeTrayIcon.
    private void TrayIcon_BalloonTipClicked()
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            var action = balloonAction;
            balloonAction = null;
            if (exitRequested) return;
            if (action is null)
            {
                ShowMainWindow();
                return;
            }
            _ = RunNotificationActionAsync(action);
        });
    }

    // A click on a bell entry. The stored action goes through the same allow-list again.
    internal Task RunHistoryActionAsync(string? storedAction)
    {
        return NotificationActions.Parse(storedAction) is { } action
            ? RunNotificationActionAsync(action)
            : Task.CompletedTask;
    }

    // Only opens HerdMe pages or starts a HerdMe service; see NotificationActions.Parse.
    private async Task RunNotificationActionAsync(NotificationAction action)
    {
        if (exitRequested || MainWindow is null) return;
        try
        {
            switch (action.Kind)
            {
                case NotificationActionKind.OpenPage:
                    await ShowPageAsync(action.Argument);
                    break;
                case NotificationActionKind.OpenSite:
                    await ReportIfFailedAsync(await ShowSiteAsync(action.Argument, null, CancellationToken.None));
                    break;
                case NotificationActionKind.OpenSiteLogs:
                    await ReportIfFailedAsync(await ShowLogsAsync(action.Argument, CancellationToken.None));
                    break;
                case NotificationActionKind.StartService when Guid.TryParse(action.Argument, out var id):
                    await RunOnUiAsync(ShowMainWindow);
                    if (services.Services.LoadInstances().Any(instance => instance.Id == id))
                    {
                        await services.Services.StartAsync(id);
                    }
                    await RunOnUiAsync(() => MainWindow.NavigateToPage("services"));
                    break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await DiagnosticLog.WriteFailureAsync(
                "notifications",
                "action-failed",
                "A notification button could not finish.",
                error.ToString()
            );
            await ShowCommandFailureAsync(error.Message);
        }
    }

    private async Task ReportIfFailedAsync(AppCommandResponse response)
    {
        if (!response.Ok) await ShowCommandFailureAsync(response.Output);
    }
}
