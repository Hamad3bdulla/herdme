using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HerdMe.Windows;

// The bottom status bar and the taskbar progress for PHP / Node downloads.
public sealed partial class MainWindow
{
    private bool statusBarSubscribed;
    private string? displayedStatusBarSignature;
    private DownloadSummary displayedDownloads = StatusBarPresentation.Idle;

    private void InitializeStatusBar()
    {
        if (!statusBarSubscribed)
        {
            RuntimeOperations.Shared.Changed += Downloads_Changed;
            statusBarSubscribed = true;
        }
        UpdateStatusBar();
        UpdateDownloads();
    }

    private void ReleaseStatusBar()
    {
        if (!statusBarSubscribed) return;
        RuntimeOperations.Shared.Changed -= Downloads_Changed;
        statusBarSubscribed = false;
    }

    // Raised on whichever thread reported progress.
    private void Downloads_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!shuttingDown) UpdateDownloads();
        });
    }

    private void UpdateStatusBar()
    {
        if (shuttingDown) return;
        StatusBar.Visibility = RequiresOnboarding ? Visibility.Collapsed : Visibility.Visible;
        if (RequiresOnboarding) return;
        var environment = services.Environment;
        var running = environment.IsRunning;
        var degraded = environment.IsDegraded;
        var signature = $"{running}|{degraded}|{environment.HttpPort}|{environment.HttpsPort}";
        if (signature == displayedStatusBarSignature) return;
        displayedStatusBarSignature = signature;
        var tone = running ? StatusTone.Success : degraded ? StatusTone.Caution : StatusTone.Critical;
        StatusBarDot.Style = StatusStyles.Dot(tone);
        StatusBarEnvironmentText.Text = AppLocalization.Get(
            running ? "DashboardRunning" : degraded ? "DashboardRecovering" : "DashboardStopped"
        );
        StatusBarPortsText.Text = running && environment.HttpsPort is { } https
            ? AppLocalization.Format("StatusBarPorts", environment.HttpPort?.ToString() ?? "-", https)
            : string.Empty;
        StatusBarPortsText.Visibility = StatusBarPortsText.Text.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateDownloads()
    {
        var summary = StatusBarPresentation.Summarize(RuntimeOperations.Shared.Snapshot().Select(operation =>
            new DownloadItem(
                operation.Name,
                operation.Progress.IsActive,
                operation.Progress.BytesReceived,
                operation.Progress.TotalBytes
            )));
        if (summary == displayedDownloads) return;
        displayedDownloads = summary;
        var busy = summary.Active > 0;
        StatusBarDownloadsButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusBarDownloadsRing.IsActive = busy;
        if (busy)
        {
            StatusBarDownloadsText.Text = (summary.Name, summary.State) switch
            {
                ({ } name, TaskbarProgressState.Normal) => AppLocalization.Format("StatusBarDownloadOne", name, summary.Percent),
                ({ } name, _) => AppLocalization.Format("StatusBarDownloadOneWaiting", name),
                (null, TaskbarProgressState.Normal) => AppLocalization.Format("StatusBarDownloadMany", summary.Active, summary.Percent),
                _ => AppLocalization.Format("StatusBarDownloadManyWaiting", summary.Active)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBarDownloadsButton, StatusBarDownloadsText.Text);
        }
        TaskbarOverlay.ApplyProgress(WindowNative.GetWindowHandle(this), summary.State, summary.Completed, summary.Total);
    }

    private void StatusBarDownloads_Click(object sender, RoutedEventArgs e) => NavigateToPage("updates");
}
