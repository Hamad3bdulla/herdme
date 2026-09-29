using HerdMe.Windows.Services;

// Updates round: one calm Updates page (summary, grouped rows, up to date, history), HerdMe
// updates itself in the app (SHA-256 checked, restart to update), no startup modals, skip /
// remind / pin / roll back, and optional install on idle or on exit.
internal static partial class ContractChecks
{
    internal static void VerifyUpdatesUxContracts(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        var xaml = Read("Pages", "UpdatesPage.xaml");
        foreach (var name in new[] { "SummaryCard", "UpToDateExpander", "HistoryRows", "AutoInstallCombo", "RestartApplicationButton" })
        {
            Check(xaml.Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal), $"the Updates page has {name}");
        }
        Check(
            xaml.Contains("Tag=\"Off\"", StringComparison.Ordinal)
                && xaml.Contains("Tag=\"Idle\"", StringComparison.Ordinal)
                && xaml.Contains("Tag=\"OnExit\"", StringComparison.Ordinal),
            "automatic install offers Off, on idle and on exit"
        );
        Check(
            !xaml.Contains("Downloads", StringComparison.Ordinal),
            "History replaces the old Downloads list on the Updates page"
        );

        var page = string.Concat(
            Read("Pages", "UpdatesPage.xaml.cs"),
            Read("Pages", "UpdatesPage.Rows.cs"),
            Read("Pages", "UpdatesPage.App.cs"),
            Read("Pages", "UpdatesPage.History.cs")
        );
        foreach (var marker in new[]
        {
            "UpdatesBadgeMajor",
            "UpdatesBadgeSecurity",
            "ReleaseNotesUri",
            "preferences.Skip(",
            "preferences.Snooze(",
            "preferences.Pin(",
            "runner.RollBackAsync(",
            "RuntimeOperations.Shared.Cancel",
            "runner.Dequeue(",
            "ConfirmImpactAsync",
            "UpdatesSummaryDiskWarning",
            "RestartToUpdateAsync"
        })
        {
            Check(page.Contains(marker, StringComparison.Ordinal), $"the Updates page uses {marker}");
        }
        Check(
            !page.Contains("AppUpdatePrompt", StringComparison.Ordinal)
                && !page.Contains("ManagedComponentUpdatePrompt", StringComparison.Ordinal),
            "the Updates page works in its rows instead of modal prompts"
        );

        var selfUpdater = Read("Services", "AppSelfUpdater.cs");
        Check(
            selfUpdater.Contains("SHA256", StringComparison.Ordinal)
                && selfUpdater.Contains("AppSelfUpdateChecksumFailed", StringComparison.Ordinal),
            "the in-app HerdMe download is SHA-256 verified before it can be installed"
        );
        Check(
            selfUpdater.Contains("\"/RELAUNCH=1\"", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(repositoryRoot, "Windows", "installer.iss"))
                    .Contains("{param:RELAUNCH|0}", StringComparison.Ordinal),
            "restart to update runs setup silently and setup starts HerdMe again"
        );

        var appUpdates = Read("App.Updates.cs");
        Check(
            appUpdates.Contains("AutoInstallMode.Idle", StringComparison.Ordinal)
                && appUpdates.Contains("AutoInstallMode.OnExit", StringComparison.Ordinal)
                && appUpdates.Contains("!UpdatePreferencesStore.IsMajorChange(", StringComparison.Ordinal),
            "automatic install runs on idle or on exit and never crosses a major version"
        );

        // Pure rules behind the page.
        Check(UpdatePreferencesStore.SnoozeFor == TimeSpan.FromDays(7), "remind me later waits a week");
        Check(UpdatePreferencesStore.FreshFor == TimeSpan.FromHours(1), "update results are reused for an hour");
        Check(UpdateRollbackStore.KeepFor == TimeSpan.FromDays(7), "roll back points are kept for seven days");
        Check(UpdatePreferencesStore.MajorOf("v8.3.12") == "8", "the major line of a version is its first number");
        Check(
            UpdatePreferencesStore.IsMajorChange("7.4.1", "8.0.0")
                && !UpdatePreferencesStore.IsMajorChange("8.0.1", "8.4.0"),
            "only a new first number is a major change"
        );

        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var prefs = new UpdatePreferences();
        prefs.SkippedVersions["git"] = "2.50.0";
        prefs.SnoozedUntil["composer"] = now.AddDays(1);
        prefs.PinnedLines["service:mysql"] = "8";
        Check(
            UpdatePreferencesStore.IsHidden("git", "2.50.0", prefs, now)
                && !UpdatePreferencesStore.IsHidden("git", "2.51.0", prefs, now),
            "skipping hides only that version"
        );
        Check(
            UpdatePreferencesStore.IsHidden("composer", "2.9.0", prefs, now)
                && !UpdatePreferencesStore.IsHidden("composer", "2.9.0", prefs, now.AddDays(2)),
            "a snoozed update comes back when the week is over"
        );
        Check(
            UpdatePreferencesStore.IsHeldByPin(new ManagedComponentUpdate("service:mysql", "MySQL", "8.4.0", "9.1.0", "Services"), prefs)
                && !UpdatePreferencesStore.IsHeldByPin(new ManagedComponentUpdate("service:mysql", "MySQL", "8.4.0", "8.4.3", "Services"), prefs),
            "a pinned major line holds back only updates outside that line"
        );
        Check(
            UpdatePreferencesStore.IsFresh(new UpdateCheckCache(now.AddMinutes(-30), [], [], null, false), now)
                && !UpdatePreferencesStore.IsFresh(new UpdateCheckCache(now.AddHours(-2), [], [], null, false), now),
            "the cached update check is fresh for one hour"
        );

        var history = Enumerable.Range(0, UpdateHistoryStore.Capacity + 5)
            .Aggregate(
                new List<UpdateHistoryEntry>(),
                (list, index) => UpdateHistoryStore.Push(
                    list,
                    new UpdateHistoryEntry("git", "Git", "1", "2", now.AddMinutes(index), UpdateOutcome.Updated)
                )
            );
        Check(
            history.Count == UpdateHistoryStore.Capacity && history[0].At == now.AddMinutes(UpdateHistoryStore.Capacity + 4),
            "update history keeps the newest entries first and stays bounded"
        );
    }
}
