using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HerdMe.Windows.Pages;

// Updates: a summary card (how many, how big, how long, one button), the HerdMe row with an
// in-app download and "Restart to update", component rows grouped by kind with in-row
// progress, Cancel and Retry, skip / remind me / stay on a major line, rollback, what is up to
// date, the update history and automatic installation. The installing itself happens in
// ComponentUpdateRunner, so it also works while this page is closed. Opening the page shows the
// last check (at most an hour old) instead of asking every server again; Check forces one.
public sealed partial class UpdatesPage : Page
{
    private const long Megabyte = 1024 * 1024;
    private readonly SiteConfigurationStore settingsStore;
    private readonly AppUpdateManager appUpdates;
    private readonly ManagedComponentUpdateManager componentUpdates;
    private readonly ComponentUpdateRunner runner;
    private readonly AppSelfUpdater selfUpdater;
    private readonly UpdatePreferencesStore preferences;
    private readonly UpdateHistoryStore history;
    private CancellationTokenSource? refreshCancellation;
    private AppUpdateRelease? applicationRelease;
    private bool applicationUnavailable;
    private List<ManagedComponentUpdate> updates = [];
    // Rows updated while the page was open keep a check mark until the next Check.
    private readonly List<ManagedComponentUpdate> finished = [];
    private List<(string Component, string Reason)> checkFailures = [];
    private DateTimeOffset? checkedAt;
    private IReadOnlyList<InstalledComponent> installed = [];
    private bool loaded;
    private bool busy;

    public UpdatesPage(
        SiteConfigurationStore settingsStore,
        AppUpdateManager appUpdates,
        ManagedComponentUpdateManager componentUpdates,
        ComponentUpdateRunner runner,
        AppSelfUpdater selfUpdater
    )
    {
        this.settingsStore = settingsStore;
        this.appUpdates = appUpdates;
        this.componentUpdates = componentUpdates;
        this.runner = runner;
        this.selfUpdater = selfUpdater;
        preferences = runner.Preferences;
        history = runner.History;
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        loaded = true;
        // The page is cached, so Loaded can run again; never subscribe twice.
        Unsubscribe();
        runner.StateChanged += Runner_StateChanged;
        RuntimeOperations.Shared.Changed += Operations_Changed;
        history.Changed += History_Changed;
        preferences.Changed += Preferences_Changed;
        selfUpdater.Changed += SelfUpdater_Changed;
        LoadAutoInstall();
        RenderHistory();
        RenderAll();
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        Unsubscribe();
        Interlocked.Exchange(ref refreshCancellation, null)?.Cancel();
    }

    private void Unsubscribe()
    {
        runner.StateChanged -= Runner_StateChanged;
        RuntimeOperations.Shared.Changed -= Operations_Changed;
        history.Changed -= History_Changed;
        preferences.Changed -= Preferences_Changed;
        selfUpdater.Changed -= SelfUpdater_Changed;
    }

    // A row started, finished or left the queue (from any thread).
    private void Runner_StateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!loaded) return;
        RenderComponents();
        RenderSummary();
        RenderApplication();
    });

    // Download and install progress: only the rows change, nothing is rebuilt.
    private void Operations_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (loaded) RefreshRowStates();
    });

    private void History_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (loaded) RenderHistory();
    });

    // Skip, remind me, pin, a finished update (it leaves the cache) or the daily check.
    private void Preferences_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!loaded || busy) return;
        if (preferences.LoadCache() is { } cache) ApplyCache(cache);
        LoadAutoInstall();
        RenderAll();
    });

    private void SelfUpdater_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!loaded) return;
        RenderApplication();
        RenderSummary();
    });

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        finished.Clear();
        await RefreshAsync(force: true);
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (busy) return;
        if (!force
            && preferences.LoadCache() is { } cache
            && UpdatePreferencesStore.IsFresh(cache, DateTimeOffset.UtcNow))
        {
            ApplyCache(cache);
            RenderAll();
            ShowCheckStatus();
            await LoadInstalledAsync();
            return;
        }
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref refreshCancellation, cancellation);
        previous?.Cancel();
        SetBusy(true, AppLocalization.Get("UpdatesChecking"));
        try
        {
            AppUpdateCheck? application = null;
            Exception? applicationError = null;
            var applicationTask = appUpdates.CheckAsync(
                settingsStore.Load().UpdateChannel,
                cancellation.Token
            );
            var componentsTask = componentUpdates.CheckAsync(cancellation.Token);
            try
            {
                application = await applicationTask;
            }
            catch (Exception error) when (
                error is not OperationCanceledException || !cancellation.IsCancellationRequested
            )
            {
                applicationError = error;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) return;

            applicationRelease = application?.AvailableRelease;
            applicationUnavailable = applicationError is not null || application?.UsedBundledFallback == true;
            RenderApplication(applicationError);
            BusyOverlay.Visibility = Visibility.Collapsed;
            ComponentCheckProgress.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;

            var components = await componentsTask;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) return;

            var failures = components.Failures
                .Select(failure => (failure.Component, Reason: FailureReason(failure.Error)))
                .ToList();
            if (applicationError is not null)
                failures.Insert(0, ("HerdMe", FailureReason(applicationError)));
            else if (application?.UsedBundledFallback == true)
                failures.Insert(0, ("HerdMe", AppLocalization.Get("UpdatesFeedNotPublished")));
            ReplaceUpdates(components.Updates);
            checkFailures = failures;
            checkedAt = components.CheckedAt;
            // The badge, the tray and the next visit read this; the page itself is already current.
            preferences.SaveCache(new UpdateCheckCache(
                components.CheckedAt,
                components.Updates,
                failures.Select(item => new CachedUpdateFailure(item.Component, item.Reason)).ToList(),
                applicationRelease,
                applicationUnavailable
            ));
            ShowCheckStatus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (loaded)
                ShowStatus(
                    InfoBarSeverity.Error,
                    AppLocalization.Get("UpdatesCheckFailed"),
                    error.Message
                );
        }
        finally
        {
            Interlocked.CompareExchange(ref refreshCancellation, null, cancellation);
            cancellation.Dispose();
            ComponentCheckProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, string.Empty);
        }
        if (!loaded) return;
        RenderAll();
        await LoadInstalledAsync();
    }

    private void ApplyCache(UpdateCheckCache cache)
    {
        applicationRelease = cache.ApplicationRelease;
        applicationUnavailable = cache.ApplicationUnavailable;
        ReplaceUpdates(cache.Updates);
        checkFailures = cache.Failures.Select(item => (item.Component, item.Reason)).ToList();
        if (applicationUnavailable
            && !checkFailures.Any(item => item.Component.Equals("HerdMe", StringComparison.OrdinalIgnoreCase)))
        {
            checkFailures.Insert(0, ("HerdMe", AppLocalization.Get("UpdatesApplicationCheckUnavailable")));
        }
        checkedAt = cache.CheckedAt;
    }

    // A row that left the list because it was just updated stays, with a check mark.
    private void ReplaceUpdates(IReadOnlyList<ManagedComponentUpdate> next)
    {
        foreach (var old in updates)
        {
            if (next.Any(update => SameId(update, old)) || IsFinished(old)) continue;
            if (runner.LastResult(ComponentUpdateRunner.OperationKey(old))?.Outcome == UpdateOutcome.Updated)
                finished.Add(old);
        }
        finished.RemoveAll(item => next.Any(update => SameId(update, item)));
        updates = next.ToList();
    }

    private void RenderAll()
    {
        LastCheckedText.Text = checkedAt is { } time
            ? AppLocalization.Format("UpdatesLastChecked", time.ToLocalTime().ToString("g"))
            : string.Empty;
        RenderApplication();
        RenderComponents();
        RenderUpToDate();
        RenderSummary();
    }

    private void RenderSummary()
    {
        SummaryWarning.Visibility = Visibility.Collapsed;
        UpdateAllButton.IsEnabled = !busy;
        if (runner.IsBusy)
        {
            var active = updates.Concat(finished).FirstOrDefault(update => string.Equals(
                ComponentUpdateRunner.OperationKey(update), runner.Active, StringComparison.OrdinalIgnoreCase));
            SetGlyph(SummaryGlyph, "\uE895", "Neutral");
            SummaryTitle.Text = active is null
                ? AppLocalization.Get("UpdatesInstallingTitle")
                : AppLocalization.Format("UpdatesInstallingComponent", active.Name);
            SummaryDetail.Text = AppLocalization.Get("UpdatesInstallingDetail");
            UpdateAllButton.Visibility = Visibility.Collapsed;
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var prefs = preferences.Load();
        var offered = Offered(prefs, now);
        var application = PendingApplication(prefs, now);
        var count = offered.Select(ComponentUpdateRunner.OperationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            + (application is null ? 0 : 1);
        if (count == 0)
        {
            SetGlyph(SummaryGlyph, "\uE930", "Success");
            SummaryTitle.Text = AppLocalization.Get(checkedAt is null ? "UpdatesNotCheckedTitle" : "UpdatesCurrentTitle");
            SummaryDetail.Text = AppLocalization.Get(checkedAt is null ? "UpdatesNotCheckedDetail" : "ManagedUpdatesUpToDate");
            UpdateAllButton.Visibility = Visibility.Collapsed;
            return;
        }
        var security = offered.Any(update => update.Security);
        var majors = offered.Count(IsMajor);
        SetGlyph(SummaryGlyph, "\uE896", security ? "Caution" : "Neutral");
        SummaryTitle.Text = count == 1
            ? AppLocalization.Get("UpdatesSummaryOne")
            : AppLocalization.Format("UpdatesSummaryMany", count);
        var parts = new List<string>();
        var impact = offered.Count > 0 ? TryEstimate(offered) : null;
        if (impact is not null)
        {
            parts.Add(MegabytesText(impact.DownloadBytes));
            parts.Add(DurationText(impact.Duration));
        }
        if (majors > 0)
            parts.Add(majors == 1
                ? AppLocalization.Get("UpdatesSummaryMajorOne")
                : AppLocalization.Format("UpdatesSummaryMajorMany", majors));
        if (security) parts.Add(AppLocalization.Get("UpdatesSummarySecurity"));
        if (application is not null) parts.Add(AppLocalization.Get("UpdatesSummaryApplication"));
        SummaryDetail.Text = string.Join("  \u00B7  ", parts);
        if (impact is not null && FreeBytes() is { } free && free < impact.RequiredBytes)
        {
            SummaryWarning.Text = AppLocalization.Format(
                "UpdatesSummaryDiskWarning",
                free / Megabyte,
                impact.RequiredBytes / Megabyte
            );
            SummaryWarning.Visibility = Visibility.Visible;
        }
        // New major versions are left out of "Update all" unless ticked in the confirmation.
        var minor = offered.Where(update => !IsMajor(update))
            .Select(ComponentUpdateRunner.OperationKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        UpdateAllButton.Visibility = minor > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllText.Text = AppLocalization.Format("UpdatesUpdateAllCount", minor);
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var candidates = Offered(preferences.Load(), DateTimeOffset.UtcNow)
            .Where(update => !IsRunningOrQueued(update))
            .ToList();
        // Composer and the Laravel installer are one operation; GroupBy(OperationKey) keeps them together.
        var minor = candidates.Where(update => !IsMajor(update))
            .GroupBy(ComponentUpdateRunner.OperationKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group)
            .ToList();
        var majors = candidates.Where(IsMajor).ToList();
        await RunUpdatesAsync(minor, majors);
    }

    // Asks first when something has to stop or a major version changes, then hands the batch
    // to the runner (which stops only what uses each component, right before it is swapped).
    private async Task RunUpdatesAsync(
        IReadOnlyList<ManagedComponentUpdate> selected,
        IReadOnlyList<ManagedComponentUpdate>? optionalMajors = null
    )
    {
        if (busy || selected.Count == 0 && (optionalMajors is null || optionalMajors.Count == 0)) return;
        var chosen = await ConfirmImpactAsync(selected.ToList(), optionalMajors?.ToList() ?? []);
        if (chosen is null || chosen.Count == 0) return;
        StatusBar.IsOpen = false;
        IReadOnlyList<ComponentUpdateResult> results;
        try
        {
            results = await runner.RunAsync(chosen, CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (loaded)
                ShowStatus(InfoBarSeverity.Error, AppLocalization.Get("UpdatesFailedTitle"), error.Message);
            return;
        }
        foreach (var result in results.Where(result => result.Outcome == UpdateOutcome.Updated))
        {
            if (!IsFinished(result.Update)) finished.Add(result.Update);
        }
        if (!loaded) return;
        ShowRunSummary(results);
        RenderAll();
        await LoadInstalledAsync();
    }

    private void ShowRunSummary(IReadOnlyList<ComponentUpdateResult> results)
    {
        var updated = results.Count(result => result.Outcome == UpdateOutcome.Updated);
        var failed = results.Count(result => result.Outcome is UpdateOutcome.Failed or UpdateOutcome.Restored);
        var cancelled = results.Count(result => result.Outcome == UpdateOutcome.Cancelled);
        if (failed == 0 && cancelled == 0)
        {
            if (updated == 0) return;
            ShowStatus(
                InfoBarSeverity.Success,
                AppLocalization.Get("UpdatesCompletedTitle"),
                AppLocalization.Format("UpdatesCompletedMessage", updated)
            );
            return;
        }
        ShowStatus(
            failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            AppLocalization.Get(failed > 0 ? "UpdatesRunFailedTitle" : "UpdatesRunCancelledTitle"),
            AppLocalization.Format("UpdatesRunSummary", updated, failed, cancelled)
        );
    }

    // null means "Later". Nothing to stop and no new major version: no question at all.
    private async Task<List<ManagedComponentUpdate>?> ConfirmImpactAsync(
        List<ManagedComponentUpdate> chosen,
        List<ManagedComponentUpdate> optionalMajors
    )
    {
        var first = TryEstimate(chosen);
        if (optionalMajors.Count == 0
            && !chosen.Any(IsMajor)
            && first is { StopsSomething: false })
        {
            return chosen;
        }
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        var details = new StackPanel { Spacing = 8 };
        panel.Children.Add(details);
        CheckBox? includeMajors = null;
        if (optionalMajors.Count > 0)
        {
            includeMajors = new CheckBox
            {
                Content = optionalMajors.Count == 1
                    ? AppLocalization.Format("UpdatesIncludeMajorOne", optionalMajors[0].Name)
                    : AppLocalization.Format("UpdatesIncludeMajors", optionalMajors.Count)
            };
            panel.Children.Add(includeMajors);
        }
        List<ManagedComponentUpdate> Selection() => includeMajors?.IsChecked == true
            ? chosen.Concat(optionalMajors).ToList()
            : chosen;
        void Fill()
        {
            details.Children.Clear();
            var selection = Selection();
            foreach (var line in ImpactLines(selection, TryEstimate(selection)))
                details.Children.Add(line);
        }
        Fill();
        if (includeMajors is not null)
        {
            includeMajors.Checked += (_, _) => Fill();
            includeMajors.Unchecked += (_, _) => Fill();
        }
        var operations = chosen.Concat(optionalMajors)
            .Select(ComponentUpdateRunner.OperationKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = AppLocalization.LayoutDirection,
            Title = operations == 1
                ? AppLocalization.Format("UpdatesConfirmOne", chosen.Concat(optionalMajors).First().Name)
                : AppLocalization.Format("UpdatesConfirmMany", operations),
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = AppLocalization.Get("UpdatesUpdateNow"),
            CloseButtonText = AppLocalization.Get("UpdatesLater"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return Selection();
    }

    private IEnumerable<UIElement> ImpactLines(List<ManagedComponentUpdate> selection, UpdateImpact? impact)
    {
        TextBlock Line(string text, string style = "SecondaryTextStyle") => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = S(style)
        };
        if (selection.Count == 0)
        {
            yield return Line(AppLocalization.Get("UpdatesImpactNothingSelected"));
            yield break;
        }
        if (impact is not null)
        {
            if (impact.StoppedServices.Count > 0)
                yield return Line(AppLocalization.Format(
                    "UpdatesImpactServices",
                    string.Join(", ", impact.StoppedServices.Distinct(StringComparer.OrdinalIgnoreCase))
                ));
            if (impact.StoppedSites > 0)
                yield return Line(AppLocalization.Format(
                    "UpdatesImpactSites",
                    impact.StoppedSites,
                    string.Join(", ", impact.StoppedPhpLines.Select(line => "PHP " + line)),
                    Math.Max(5, (int)Math.Ceiling(impact.SiteDowntime.TotalSeconds))
                ));
            if (!impact.StopsSomething) yield return Line(AppLocalization.Get("UpdatesImpactNothingStops"));
            yield return Line(AppLocalization.Format(
                "UpdatesImpactTime",
                DurationText(impact.Duration),
                MegabytesText(impact.DownloadBytes)
            ));
            if (FreeBytes() is { } free && free < impact.RequiredBytes)
                yield return Line(AppLocalization.Format(
                    "UpdatesSummaryDiskWarning",
                    free / Megabyte,
                    impact.RequiredBytes / Megabyte
                ), "StatusCriticalTextStyle");
        }
        foreach (var major in selection.Where(IsMajor))
        {
            yield return Line(AppLocalization.Format(
                "UpdatesImpactMajor",
                major.Name,
                UpdatePreferencesStore.MajorOf(major.InstalledVersion),
                UpdatePreferencesStore.MajorOf(major.LatestVersion)
            ), "StatusCriticalTextStyle");
        }
    }

    private UpdateImpact? TryEstimate(IReadOnlyList<ManagedComponentUpdate> selection)
    {
        if (selection.Count == 0) return null;
        try
        {
            return runner.EstimateImpact(selection);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(settingsStore.SupportRoot);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void ShowCheckStatus()
    {
        if (checkFailures.Count == 0)
        {
            StatusBar.IsOpen = false;
            return;
        }
        // Name each reason so a failed check can be acted on instead of guessed at.
        var details = checkFailures.Select(item =>
            AppLocalization.Format("UpdatesFailureDetail", item.Component, item.Reason));
        ShowStatus(
            InfoBarSeverity.Warning,
            AppLocalization.Get("UpdatesPartialTitle"),
            AppLocalization.Format(
                "UpdatesPartialMessage",
                string.Join(", ", checkFailures.Select(item => item.Component))
            ) + "\n" + string.Join("\n", details)
        );
    }

    private static string FailureReason(Exception error)
    {
        if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests })
            return AppLocalization.Get("UpdatesFailureRateLimited");
        if (error is HttpRequestException { StatusCode: { } status })
            return AppLocalization.Format("UpdatesFailureHttp", (int)status);
        if (error is HttpRequestException)
            return AppLocalization.Get("UpdatesFailureOffline");
        if (error is OperationCanceledException or TimeoutException)
            return AppLocalization.Get("UpdatesFailureTimeout");
        var message = error.Message.Split('\n', 2)[0].Trim();
        return message.Length <= 140 ? message : message[..137] + "...";
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void SetBusy(bool value, string status)
    {
        busy = value;
        BusyText.Text = status;
        BusyOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !value;
        UpdateAllButton.IsEnabled = !value;
        if (loaded) RefreshRowStates();
    }

    private static async Task OpenAsync(Uri? uri)
    {
        if (uri is not null && uri.Scheme == Uri.UriSchemeHttps) await Launcher.LaunchUriAsync(uri);
    }

    private static void Toast(string message, string? id = null, UpdatePreferencesStore? undo = null)
    {
        Func<Task>? action = null;
        if (id is not null && undo is not null)
        {
            action = () =>
            {
                undo.Unhide(id);
                return Task.CompletedTask;
            };
        }
        App.MainWindow?.ShowToast(message, action is null ? null : AppLocalization.Get("CommonUndo"), action);
    }

    private List<ManagedComponentUpdate> Offered(UpdatePreferences prefs, DateTimeOffset now) => updates
        .Where(update => UpdatePreferencesStore.IsOffered(update, prefs, now) && !IsFinished(update))
        .ToList();

    private bool IsFinished(ManagedComponentUpdate update) => finished.Any(item => SameId(item, update));

    private bool IsRunningOrQueued(ManagedComponentUpdate update)
    {
        var key = ComponentUpdateRunner.OperationKey(update);
        return string.Equals(runner.Active, key, StringComparison.OrdinalIgnoreCase) || runner.IsQueued(key);
    }

    private static bool IsMajor(ManagedComponentUpdate update) =>
        UpdatePreferencesStore.IsMajorChange(update.InstalledVersion, update.LatestVersion);

    private static bool SameId(ManagedComponentUpdate left, ManagedComponentUpdate right) =>
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase);

    private static string DurationText(TimeSpan duration) => duration < TimeSpan.FromMinutes(1)
        ? AppLocalization.Format("UpdatesAboutSeconds", Math.Max(5, (int)Math.Ceiling(duration.TotalSeconds)))
        : AppLocalization.Format("UpdatesAboutMinutes", (int)Math.Ceiling(duration.TotalMinutes));

    private static string MegabytesText(long bytes) =>
        AppLocalization.Format("UpdatesAboutMegabytes", Math.Max(1, (long)Math.Ceiling(bytes / (double)Megabyte)));

    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static void SetGlyph(FontIcon icon, string glyph, string kind)
    {
        icon.Glyph = glyph;
        icon.Style = S("StatusGlyph" + kind + "Style");
    }

    private static Border Pill(string text, string kind) => new()
    {
        Style = S("StatusPill" + kind + "Style"),
        Padding = new Thickness(8, 1, 8, 2),
        Child = new TextBlock { Text = text, FontSize = 12, Style = S("StatusTextPrimaryStyle") }
    };

    private static Microsoft.UI.Xaml.Shapes.Rectangle Divider() => new() { Style = S("CardDividerStyle") };
}
