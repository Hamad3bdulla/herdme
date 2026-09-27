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
        ObserveFinishedOperations();
        var summary = StatusBarPresentation.Summarize(RuntimeOperations.Shared.Snapshot().Select(operation =>
            new DownloadItem(
                operation.Name,
                operation.Progress.IsActive,
                operation.Progress.BytesReceived,
                operation.Progress.TotalBytes
            )));
        // Stage text can change while the byte counts stay the same.
        UpdateOperationsBar(summary);
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

    private void UpdateOperationsBar(DownloadSummary summary)
    {
        var active = RuntimeOperations.Shared.Snapshot().Where(operation => operation.Progress.IsActive).ToArray();
        if (active.Length == 0 || RequiresOnboarding)
        {
            OperationsBar.Visibility = Visibility.Collapsed;
            return;
        }
        var stage = active.Length == 1 ? AppLocalization.Get("OperationsStage" + active[0].Progress.Stage) : null;
        OperationsBarText.Text = active.Length == 1
            ? AppLocalization.Format("OperationsOne", active[0].Name, stage ?? string.Empty)
            : AppLocalization.Format("OperationsMany", active.Length);
        var known = summary.State == TaskbarProgressState.Normal;
        OperationsBarProgress.IsIndeterminate = !known;
        if (known) OperationsBarProgress.Value = summary.Percent;
        OperationsBarPercent.Text = known ? AppLocalization.Format("OperationsPercent", summary.Percent) : string.Empty;
        OperationsBarCancel.Content = AppLocalization.Get(active.Length == 1 ? "CommonCancel" : "OperationsCancelAll");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OperationsBarProgress, OperationsBarText.Text);
        OperationsBar.Visibility = Visibility.Visible;
    }

    private void OperationsBarCancel_Click(object sender, RoutedEventArgs e)
    {
        // Each operation reports Cancelled itself; the bar hides when the last one stops.
        if (RuntimeOperations.Shared.CancelAll() > 0) OperationsBarText.Text = AppLocalization.Get("OperationsCancelling");
    }

    private void StatusBarDownloads_Click(object sender, RoutedEventArgs e) => NavigateToPage("updates");
}
