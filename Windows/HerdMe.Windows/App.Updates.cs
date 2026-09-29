using System.Runtime.InteropServices;
using HerdMe.Windows.Services;

namespace HerdMe.Windows;

// Updates without interruptions: nothing opens a dialog on its own. A check (at startup, then
// quietly once a day) sets the badge on Updates and the tray entry and shows at most one toast
// or notification with "View". Component updates can install by themselves when the PC is idle
// or when HerdMe quits (Updates page); HerdMe itself always waits for "Restart to update".
public partial class App
{
    private static readonly TimeSpan UpdateSchedulerInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan IdleBeforeAutomaticInstall = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OnExitInstallLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutomaticInstallCacheAge = TimeSpan.FromDays(2);
    private int updateSchedulerStarted;
    private int automaticInstallRunning;
    private string? announcedUpdates;

    private void StartUpdateScheduler()
    {
        if (Interlocked.Exchange(ref updateSchedulerStarted, 1) != 0) return;
        services.UpdatePreferences.Changed += UpdatePreferences_Changed;
        _ = backgroundTasks.RunAsync(cancellationToken => Task.Run(
            () => RunUpdateSchedulerAsync(cancellationToken),
            cancellationToken
        ));
    }

    private async Task RunUpdateSchedulerAsync(CancellationToken cancellationToken)
    {
        RecordFinishedApplicationUpdate();
        services.SelfUpdater.RemoveOldDownloads(keepVersion: null);
        services.Rollbacks.Prune(DateTimeOffset.UtcNow);
        using var timer = new PeriodicTimer(UpdateSchedulerInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (exitRequested) return;
            try
            {
                var settings = services.SiteSettings.Load();
                if (settings.AutomaticUpdates
                    && UpdatePreferencesStore.BackgroundCheckDue(services.UpdatePreferences.Load(), DateTimeOffset.UtcNow))
                {
                    await PublishAutomaticUpdateCheckAsync(CheckForUpdatesInBackgroundAsync(settings.UpdateChannel));
                }
                if (services.UpdatePreferences.Load().AutoInstallMode == AutoInstallMode.Idle && IsIdleForUpdates())
                {
                    await InstallPendingComponentUpdatesAsync(notify: true, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                await DiagnosticLog.WriteFailureAsync(
                    "updates",
                    "scheduler-failed",
                    "The background update check could not finish.",
                    error.ToString()
                );
            }
        }
    }

    private async Task PublishAutomaticUpdateCheckAsync(Task<AutomaticUpdateCheck> checkTask)
    {
        var result = await checkTask;
        if (exitRequested) return;
        var now = DateTimeOffset.UtcNow;
        var cache = new UpdateCheckCache(
            result.Components.CheckedAt,
            result.Components.Updates,
            result.Components.Failures
                .Select(failure => new CachedUpdateFailure(failure.Component, failure.Error.Message))
                .ToList(),
            result.ApplicationRelease,
            result.ApplicationUnavailable
        );
        services.UpdatePreferences.MarkBackgroundCheck(now);
        services.UpdatePreferences.SaveCache(cache);
        services.Rollbacks.Prune(now);
        PublishUpdates(cache, announce: true);
    }

    // Skip, "remind me in a week", a finished update or a check on the Updates page.
    private void UpdatePreferences_Changed(object? sender, EventArgs args)
    {
        if (exitRequested) return;
        if (services.UpdatePreferences.LoadCache() is { } cache) PublishUpdates(cache, announce: false);
    }

    private void PublishUpdates(UpdateCheckCache cache, bool announce)
    {
        var now = DateTimeOffset.UtcNow;
        var preferences = services.UpdatePreferences.Load();
        var components = cache.Updates
            .Where(update => UpdatePreferencesStore.IsOffered(update, preferences, now))
            .ToList();
        AppUpdateRelease? application = null;
        if (cache.ApplicationRelease is { } release
            && RuntimeVersionComparison.IsNewer(release.Version, services.Updates.CurrentVersion)
            && !UpdatePreferencesStore.IsHidden(UpdatePreferencesStore.ApplicationId, release.Version, preferences, now))
        {
            application = release;
        }
        var count = components.Count + (application is null ? 0 : 1);
        var signature = string.Join(
            "|",
            components.Select(update => update.Id + "=" + update.LatestVersion)
                .Append(UpdatePreferencesStore.ApplicationId + "=" + application?.Version)
        );
        // The same updates are announced once, however often the daily check finds them.
        var shouldAnnounce = announce
            && count > 0
            && !string.Equals(Interlocked.Exchange(ref announcedUpdates, signature), signature, StringComparison.Ordinal);
        MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (exitRequested) return;
            MainWindow.SetUpdatesBadge(count);
            if (!shouldAnnounce) return;
            if (IsMainWindowVisible)
            {
                MainWindow.ShowToast(
                    count == 1
                        ? AppLocalization.Get("UpdatesFoundToastOne")
                        : AppLocalization.Format("UpdatesFoundToast", count),
                    AppLocalization.Get("UpdatesFoundToastView"),
                    () =>
                    {
                        MainWindow.NavigateToPage("updates");
                        return Task.CompletedTask;
                    }
                );
            }
            else
            {
                NotifyUpdatesAvailable(count, components.Count == 0 ? application : null);
            }
        });
    }

    // Idle: the window is in the tray, nothing is installing, and nobody touched the PC for
    // ten minutes.
    private bool IsIdleForUpdates()
    {
        if (IsMainWindowVisible || services.ComponentUpdater.IsBusy) return false;
        if (RuntimeOperations.Shared.Snapshot().Any(operation => operation.Progress.IsActive)) return false;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return false;
        var idle = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        return idle >= IdleBeforeAutomaticInstall;
    }

    // Components only, from the last check, and never a new major version: those always wait
    // for the user on the Updates page.
    private async Task InstallPendingComponentUpdatesAsync(bool notify, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref automaticInstallRunning, 1) != 0) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cache = services.UpdatePreferences.LoadCache();
            if (cache is null || now - cache.CheckedAt > AutomaticInstallCacheAge) return;
            var preferences = services.UpdatePreferences.Load();
            var updates = cache.Updates
                .Where(update => UpdatePreferencesStore.IsOffered(update, preferences, now)
                    && !UpdatePreferencesStore.IsMajorChange(update.InstalledVersion, update.LatestVersion))
                .ToList();
            if (updates.Count == 0) return;
            var results = await services.ComponentUpdater.RunAsync(updates, cancellationToken);
            var updated = results.Count(result => result.Outcome == UpdateOutcome.Updated);
            var failed = results.Count(result => result.Outcome is UpdateOutcome.Failed or UpdateOutcome.Restored);
            if (notify && updated + failed > 0) Notify(AppNotifications.UpdatesInstalled(updated, failed));
        }
        finally
        {
            Interlocked.Exchange(ref automaticInstallRunning, 0);
        }
    }

    // "Install when HerdMe quits": runs from Quit, before the services shut down, and gives up
    // after five minutes (what is not done yet is left for the next time).
    private async Task InstallUpdatesOnExitAsync()
    {
        if (suppressAutomaticUpdateCheck) return;
        if (services.UpdatePreferences.Load().AutoInstallMode != AutoInstallMode.OnExit) return;
        using var limit = new CancellationTokenSource(OnExitInstallLimit);
        await Task.Run(() => InstallPendingComponentUpdatesAsync(notify: false, limit.Token));
    }

    // "Restart to update" on the Updates page: setup runs silently, closes this HerdMe and
    // starts the new one (installer.iss /RELAUNCH=1).
    internal async Task RestartToUpdateAsync(AppUpdatePackage package)
    {
        var from = services.Updates.CurrentVersion;
        services.UpdatePreferences.Update(value =>
        {
            value.PendingApplicationVersion = package.Version;
            value.PendingApplicationFrom = from;
        });
        try
        {
            await services.SelfUpdater.LaunchInstallerAsync(package);
        }
        catch
        {
            services.UpdatePreferences.Update(value =>
            {
                value.PendingApplicationVersion = null;
                value.PendingApplicationFrom = null;
            });
            throw;
        }
        await RequestExitAsync();
    }

    // The first start after "Restart to update" writes how it went into the update history.
    private void RecordFinishedApplicationUpdate()
    {
        var preferences = services.UpdatePreferences.Load();
        if (preferences.PendingApplicationVersion is not { } version) return;
        var current = services.Updates.CurrentVersion;
        var installed = RuntimeVersionComparison.Compare(current, version) >= 0;
        services.UpdateHistory.Add(new UpdateHistoryEntry(
            UpdatePreferencesStore.ApplicationId,
            "HerdMe",
            preferences.PendingApplicationFrom ?? current,
            version,
            DateTimeOffset.UtcNow,
            installed ? UpdateOutcome.Updated : UpdateOutcome.Failed,
            installed ? null : ServiceText.Get("AppSelfUpdateNotInstalled", "Setup did not finish; HerdMe is still on the previous version.")
        ));
        services.UpdatePreferences.Update(value =>
        {
            value.PendingApplicationVersion = null;
            value.PendingApplicationFrom = null;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
