using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows;

// "New since you last looked": counts of captured mail, dumps, and unexpected stops that the
// nav InfoBadges, the taskbar overlay, and the tray menu share. Opening a page clears its
// count; nothing is counted for the page that is on screen.
public sealed partial class MainWindow
{
    private int unseenMail;
    private int unseenDumps;
    private int unseenErrors;
    private bool activitySubscribed;
    private string currentPageTag = "dashboard";
    private readonly DispatcherTimer toastTimer = new();
    private Func<Task>? toastAction;

    public int UnseenMail => unseenMail;

    public int UnseenDumps => unseenDumps;

    public int UnseenErrors => unseenErrors;

    // Raised on the UI thread whenever a count changes.
    public event EventHandler? ActivityChanged;

    private async void SubscribeActivity()
    {
        services.Services.ExitedUnexpectedly += Services_ExitedUnexpectedly;
        services.SiteProcesses.ExitedUnexpectedly += SiteProcesses_ExitedUnexpectedly;
        try
        {
            // The capture services open SQLite in their constructors; build them off the UI thread.
            await services.WarmUpAsync();
        }
        catch (Exception)
        {
            // Capture storage failed to open; the pages report it, badges just stay off.
            return;
        }
        if (shuttingDown) return;
        services.Mail.MessageCaptured += Mail_MessageCaptured;
        services.Dumps.DumpCaptured += Dumps_DumpCaptured;
        activitySubscribed = true;
    }

    private void UnsubscribeActivity()
    {
        services.Services.ExitedUnexpectedly -= Services_ExitedUnexpectedly;
        services.SiteProcesses.ExitedUnexpectedly -= SiteProcesses_ExitedUnexpectedly;
        if (!activitySubscribed) return;
        activitySubscribed = false;
        services.Mail.MessageCaptured -= Mail_MessageCaptured;
        services.Dumps.DumpCaptured -= Dumps_DumpCaptured;
    }

    private void Mail_MessageCaptured(object? sender, CapturedMail mail) => CountActivity("mail");

    private void Dumps_DumpCaptured(object? sender, CapturedDump dump) => CountActivity("dumps");

    private void Services_ExitedUnexpectedly(object? sender, ServiceExitedEventArgs args) => CountActivity("logs");

    private void SiteProcesses_ExitedUnexpectedly(object? sender, SiteBackgroundProcessState state) =>
        CountActivity("logs");

    private void CountActivity(string tag)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (shuttingDown) return;
            if (App.IsMainWindowVisible && currentPageTag == tag) return;
            switch (tag)
            {
                case "mail": unseenMail++; break;
                case "dumps": unseenDumps++; break;
                default: unseenErrors++; break;
            }
            UpdateActivityBadges();
        });
    }

    private void MarkPageSeen(string tag)
    {
        currentPageTag = tag;
        var changed = tag switch
        {
            "mail" => Interlocked.Exchange(ref unseenMail, 0) != 0,
            "dumps" => Interlocked.Exchange(ref unseenDumps, 0) != 0,
            "logs" => Interlocked.Exchange(ref unseenErrors, 0) != 0,
            _ => false
        };
        if (changed) UpdateActivityBadges();
    }

    private void UpdateActivityBadges()
    {
        SetBadge(MailBadge, unseenMail, "NavMailBadge");
        SetBadge(DumpsBadge, unseenDumps, "NavDumpsBadge");
        SetBadge(LogsBadge, unseenErrors, "NavLogsBadge");
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetBadge(InfoBadge badge, int count, string nameKey)
    {
        badge.Value = Math.Min(count, 99);
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(badge, count > 0 ? AppLocalization.Format(nameKey, count) : string.Empty);
    }

    // Settings changed elsewhere (for example the PHP version from the tray): a page that is
    // not on screen is rebuilt on its next visit instead of showing stale values.
    public void DiscardCachedPage(string tag)
    {
        if (shuttingDown || tag == currentPageTag) return;
        cachedPages.Remove(tag);
    }

    // One light confirmation for the whole window. An action (Undo) keeps it up a bit longer.
    public void ShowToast(string message, string? actionLabel = null, Func<Task>? action = null)
    {
        if (shuttingDown || string.IsNullOrWhiteSpace(message)) return;
        toastAction = action;
        ToastText.Text = message;
        var hasAction = action is not null && !string.IsNullOrWhiteSpace(actionLabel);
        ToastActionButton.Content = hasAction ? actionLabel : null;
        ToastActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.Opacity = 1;
        if (FrameworkElementAutomationPeer.FromElement(ToastText) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(hasAction ? 6 : 3);
        toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, object e)
    {
        toastTimer.Stop();
        HideToast();
    }

    private async void HideToast()
    {
        toastAction = null;
        ToastHost.Opacity = 0;
        await Task.Delay(250);
        // A newer toast may have been shown during the fade.
        if (ToastHost.Opacity == 0) ToastHost.Visibility = Visibility.Collapsed;
    }

    private async void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = toastAction;
        toastTimer.Stop();
        HideToast();
        if (action is null) return;
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "toast",
                "action-failed",
                "A toast action could not complete.",
                error.ToString()
            );
        }
    }
}
