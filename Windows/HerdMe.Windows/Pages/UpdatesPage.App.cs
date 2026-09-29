using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HerdMe.Windows.Pages;

// HerdMe itself: download in the app (SHA-256 checked), then "Restart to update" runs setup
// silently and starts the new HerdMe. Builds without an installer link fall back to the browser.
public sealed partial class UpdatesPage
{
    private CancellationTokenSource? applicationDownload;

    private AppUpdateRelease? PendingApplication() => PendingApplication(preferences.Load(), DateTimeOffset.UtcNow);

    private AppUpdateRelease? PendingApplication(UpdatePreferences prefs, DateTimeOffset now) =>
        applicationRelease is { } release
        && RuntimeVersionComparison.IsNewer(release.Version, appUpdates.CurrentVersion)
        && !UpdatePreferencesStore.IsHidden(UpdatePreferencesStore.ApplicationId, release.Version, prefs, now)
            ? release
            : null;

    private void RenderApplication(Exception? error = null)
    {
        ApplicationVersionText.Text = AppLocalization.Format("UpdatesInstalledVersion", appUpdates.CurrentVersion);
        ApplicationBadges.Children.Clear();
        DownloadApplicationButton.Visibility = Visibility.Collapsed;
        RestartApplicationButton.Visibility = Visibility.Collapsed;
        CancelApplicationButton.Visibility = Visibility.Collapsed;
        ApplicationMoreButton.Visibility = Visibility.Collapsed;
        ApplicationMoreButton.Flyout = null;

        var release = PendingApplication();
        if (release is null)
        {
            ApplicationStageText.Visibility = Visibility.Collapsed;
            ApplicationProgress.Visibility = Visibility.Collapsed;
            ApplicationStatusText.Text = error is not null || applicationUnavailable
                ? AppLocalization.Get("UpdatesApplicationCheckUnavailable")
                : AppLocalization.Get("UpdatesCurrent");
            if (applicationRelease is { } hidden
                && RuntimeVersionComparison.IsNewer(hidden.Version, appUpdates.CurrentVersion))
            {
                // Skipped or "remind me later": say so and offer to bring it back.
                ApplicationVersionText.Text = AppLocalization.Format("UpdatesHiddenSkipped", hidden.Version);
                var menu = new MenuFlyout();
                menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesShowAgain"), "\uE890", () =>
                {
                    preferences.Unhide(UpdatePreferencesStore.ApplicationId);
                    return Task.CompletedTask;
                }));
                SetApplicationMenu(menu);
            }
            return;
        }

        ApplicationStatusText.Text = AppLocalization.Format("UpdatesVersionAvailable", release.Version);
        ApplicationVersionText.Text = AppLocalization.Format(
            "UpdatesVersionChange",
            appUpdates.CurrentVersion,
            release.Version
        );
        if (UpdatePreferencesStore.IsMajorChange(appUpdates.CurrentVersion, release.Version))
            ApplicationBadges.Children.Add(Pill(AppLocalization.Get("UpdatesBadgeMajor"), "Caution"));
        SetApplicationMenu(ApplicationMenu(release));

        if (selfUpdater.Ready is { } ready && string.Equals(ready.Version, release.Version, StringComparison.OrdinalIgnoreCase))
        {
            RestartApplicationButton.Visibility = Visibility.Visible;
            RestartApplicationButton.IsEnabled = !runner.IsBusy && !busy;
            ApplicationStageText.Text = AppLocalization.Get("UpdatesApplicationReady");
            ApplicationStageText.Visibility = Visibility.Visible;
            ApplicationProgress.Visibility = Visibility.Collapsed;
            return;
        }
        if (applicationDownload is not null)
        {
            CancelApplicationButton.Visibility = Visibility.Visible;
            return;
        }
        ApplicationStageText.Visibility = Visibility.Collapsed;
        ApplicationProgress.Visibility = Visibility.Collapsed;
        DownloadApplicationButton.Visibility = Visibility.Visible;
        DownloadApplicationText.Text = AppLocalization.Get(
            AppSelfUpdater.CanInstall(release) ? "UpdatesApplicationDownload" : "UpdatesApplicationOpenDownload"
        );
    }

    private void SetApplicationMenu(MenuFlyout menu)
    {
        ApplicationMoreButton.Flyout = menu;
        ApplicationMoreButton.Visibility = menu.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var label = AppLocalization.Get("UpdatesMoreOptions");
        ToolTipService.SetToolTip(ApplicationMoreButton, label);
        AutomationProperties.SetName(ApplicationMoreButton, label);
    }

    private MenuFlyout ApplicationMenu(AppUpdateRelease release)
    {
        var menu = new MenuFlyout();
        if (ComponentUpdateRunner.ReleaseNotesUri(UpdatePreferencesStore.ApplicationId, release.Version) is { } notes)
            menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesWhatsNew"), "\uE8A5", () => OpenAsync(notes)));
        menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesSkipVersion"), "\uE8D8", () =>
        {
            ForgetDownloadedApplication(release.Version);
            preferences.Skip(UpdatePreferencesStore.ApplicationId, release.Version);
            Toast(
                AppLocalization.Format("UpdatesSkippedToast", "HerdMe", release.Version),
                UpdatePreferencesStore.ApplicationId,
                preferences
            );
            return Task.CompletedTask;
        }));
        menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesRemindLater"), "\uE823", () =>
        {
            preferences.Snooze(UpdatePreferencesStore.ApplicationId, DateTimeOffset.UtcNow);
            Toast(
                AppLocalization.Format("UpdatesSnoozedToast", "HerdMe"),
                UpdatePreferencesStore.ApplicationId,
                preferences
            );
            return Task.CompletedTask;
        }));
        return menu;
    }

    private void ForgetDownloadedApplication(string version)
    {
        if (selfUpdater.Ready is { } ready && string.Equals(ready.Version, version, StringComparison.OrdinalIgnoreCase))
            selfUpdater.Forget();
    }

    private async void DownloadApplication_Click(object sender, RoutedEventArgs e)
    {
        if (PendingApplication() is not { } release || applicationDownload is not null) return;
        ApplicationErrorText.Visibility = Visibility.Collapsed;
        if (!AppSelfUpdater.CanInstall(release))
        {
            await OpenApplicationDownloadAsync(release);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        applicationDownload = cancellation;
        ApplicationStageText.Text = AppLocalization.Get("UpdatesStagePreparing");
        ApplicationStageText.Visibility = Visibility.Visible;
        ApplicationProgress.IsIndeterminate = true;
        ApplicationProgress.Visibility = Visibility.Visible;
        RenderApplication();
        var progress = new Progress<ServiceInstallationProgress>(value =>
        {
            if (!loaded || applicationDownload is null) return;
            var row = ServiceDownloadRow.From(value, "HerdMe");
            ApplicationStageText.Text = row.Detail;
            ApplicationProgress.IsIndeterminate = row.IsIndeterminate;
            ApplicationProgress.Value = row.Percentage;
        });
        try
        {
            var package = await selfUpdater.DownloadAsync(release, progress, cancellation.Token);
            Toast(AppLocalization.Format("UpdatesApplicationReadyToast", package.Version));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancelled from the row; nothing to report.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ApplicationErrorText.Text = AppLocalization.Format("UpdatesApplicationDownloadFailed", error.Message);
            ApplicationErrorText.Visibility = Visibility.Visible;
            await DiagnosticLog.WriteFailureAsync(
                "updates",
                "app-download-failed",
                "The HerdMe update could not be downloaded.",
                error.ToString()
            );
        }
        finally
        {
            applicationDownload = null;
        }
        if (!loaded) return;
        ApplicationStageText.Visibility = Visibility.Collapsed;
        ApplicationProgress.Visibility = Visibility.Collapsed;
        RenderApplication();
        RenderSummary();
    }

    private async Task OpenApplicationDownloadAsync(AppUpdateRelease release)
    {
        if (!Uri.TryCreate(release.PlatformDownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                AppLocalization.Get("UpdatesDownloadFailedTitle"),
                AppLocalization.Get("UpdatesDownloadUnavailable")
            );
            return;
        }
        if (await Launcher.LaunchUriAsync(uri)) return;
        ShowStatus(
            InfoBarSeverity.Error,
            AppLocalization.Get("UpdatesDownloadFailedTitle"),
            AppLocalization.Format("UpdateOpenInBrowser", uri.AbsoluteUri)
        );
    }

    private void CancelApplication_Click(object sender, RoutedEventArgs e)
    {
        applicationDownload?.Cancel();
    }

    private async void RestartApplication_Click(object sender, RoutedEventArgs e)
    {
        if (selfUpdater.Ready is not { } package || runner.IsBusy) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = AppLocalization.LayoutDirection,
            Title = AppLocalization.Format("UpdatesRestartTitle", package.Version),
            Content = new TextBlock
            {
                Text = AppLocalization.Get("UpdatesRestartMessage"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = AppLocalization.Get("UpdatesRestartNow"),
            CloseButtonText = AppLocalization.Get("UpdatesLater"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true, AppLocalization.Get("UpdatesRestarting"));
        try
        {
            await ((App)Application.Current).RestartToUpdateAsync(package);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!loaded) return;
            SetBusy(false, string.Empty);
            ApplicationErrorText.Text = AppLocalization.Format("UpdatesRestartFailed", error.Message);
            ApplicationErrorText.Visibility = Visibility.Visible;
            RenderApplication();
        }
    }
}
