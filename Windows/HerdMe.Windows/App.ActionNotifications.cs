using HerdMe.Windows.Services;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HerdMe.Windows;

// Opt-in (General > Buttons on notifications): Windows notifications with buttons such as
// Restart service or Open logs. Turning it on registers HerdMe for notification activation
// under the current user (Windows App SDK writes the HKCU keys); turning it off, or
// uninstalling (--unregister-notifications from setup), removes that registration again.
// While it is off HerdMe uses the tray balloon and registers nothing.
public partial class App
{
    internal const string UnregisterNotificationsArgument = "--unregister-notifications";
    private const string ActionArgumentName = "action";

    private bool actionNotificationsRegistered;
    private NotificationAction? coldStartNotificationAction;

    // Runs before the single-instance check, like the elevated hosts helper: setup calls it
    // while uninstalling, and it must not start or wake HerdMe.
    private static bool TryRunUnregisterNotifications(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2
            || !string.Equals(arguments[1], UnregisterNotificationsArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            if (AppNotificationManager.IsSupported()) AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"HerdMe could not remove its notification registration: {error.Message}");
        }
        return true;
    }

    // Called from OnLaunched. Also picks up the button that started HerdMe, if one did.
    private void InitializeActionNotifications()
    {
        if (suppressNotifications) return;
        bool wanted;
        try
        {
            wanted = services.SiteSettings.Load().ActionNotifications;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            wanted = false;
        }
        if (!wanted || !TryRegisterActionNotifications(out _)) return;
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification
                && activation.Data is AppNotificationActivatedEventArgs notification)
            {
                coldStartNotificationAction = ActionFrom(notification);
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "notifications",
                "activation-read-failed",
                "HerdMe could not read which notification button started it.",
                error.ToString()
            );
        }
    }

    // After startup finished, so the page or service it points at exists.
    private void RunColdStartNotificationAction()
    {
        if (Interlocked.Exchange(ref coldStartNotificationAction, null) is { } action)
        {
            _ = RunNotificationActionAsync(action);
        }
    }

    // General's switch. Returns false (with the reason) when Windows refused the registration.
    internal bool SetActionNotifications(bool enabled, out string? problem)
    {
        problem = null;
        if (enabled)
        {
            if (!TryRegisterActionNotifications(out problem)) return false;
            services.SiteSettings.UpdateActionNotifications(true);
            return true;
        }
        services.SiteSettings.UpdateActionNotifications(false);
        UnregisterActionNotifications(removeRegistration: true);
        return true;
    }

    private bool TryRegisterActionNotifications(out string? problem)
    {
        problem = null;
        if (actionNotificationsRegistered) return true;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                problem = AppLocalization.Get("GeneralActionNotificationsUnsupported");
                return false;
            }
            var manager = AppNotificationManager.Default;
            // Subscribe before Register so an activation that is waiting is not missed.
            manager.NotificationInvoked += AppNotificationManager_NotificationInvoked;
            try
            {
                manager.Register();
            }
            catch
            {
                manager.NotificationInvoked -= AppNotificationManager_NotificationInvoked;
                throw;
            }
            actionNotificationsRegistered = true;
            return true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            problem = error.Message;
            _ = DiagnosticLog.WriteFailureAsync(
                "notifications",
                "register-failed",
                "Windows did not accept the notification registration.",
                error.ToString()
            );
            return false;
        }
    }

    // removeRegistration: true deletes the HKCU registration (switch off); false only stops
    // listening in this process (quitting HerdMe with the switch still on).
    private void UnregisterActionNotifications(bool removeRegistration)
    {
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            var manager = AppNotificationManager.Default;
            if (actionNotificationsRegistered)
            {
                manager.NotificationInvoked -= AppNotificationManager_NotificationInvoked;
                actionNotificationsRegistered = false;
                if (!removeRegistration) manager.Unregister();
            }
            if (removeRegistration) manager.UnregisterAll();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "notifications",
                "unregister-failed",
                "HerdMe could not remove its notification registration.",
                error.ToString()
            );
        }
    }

    // Returns false when the notification should fall back to the tray balloon.
    private bool TryShowActionNotification(HerdMe.Windows.Services.AppNotification notification)
    {
        if (!actionNotificationsRegistered) return false;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(notification.Title)
                .AddText(notification.Message);
            if (notification.Primary is { } primary)
            {
                builder.AddArgument(ActionArgumentName, NotificationActions.Build(primary));
                builder.AddButton(Button(primary));
            }
            if (notification.Secondary is { } secondary) builder.AddButton(Button(secondary));
            var shown = builder.BuildNotification();
            AppNotificationManager.Default.Show(shown);
            return shown.Id != 0;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException
            or InvalidOperationException
            or ArgumentException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "notifications",
                "show-action-failed",
                "HerdMe could not show a Windows notification; it used the tray balloon.",
                error.ToString()
            );
            return false;
        }
    }

    private static AppNotificationButton Button(NotificationAction action) =>
        new AppNotificationButton(AppLocalization.Get(action.LabelKey))
            .AddArgument(ActionArgumentName, NotificationActions.Build(action));

    private static NotificationAction? ActionFrom(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue(ActionArgumentName, out var value) ? NotificationActions.Parse(value) : null;

    // Windows calls this on a background thread.
    private void AppNotificationManager_NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args
    )
    {
        var action = ActionFrom(args);
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (exitRequested) return;
            if (action is null)
            {
                ShowMainWindow();
                return;
            }
            _ = RunNotificationActionAsync(action);
        });
    }
}
