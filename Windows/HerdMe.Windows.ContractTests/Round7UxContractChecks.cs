using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 7 (front end): the Dashboard "Getting started" checklist, the tray quick panel, the
// taskbar thumbnail buttons, the notification bell and the startup snapshot.
internal static partial class ContractChecks
{
    internal static void VerifyRound7UxContracts(string repositoryRoot, string supportRoot)
    {
        VerifyGettingStarted(supportRoot);
        VerifyTrayPanelPlacement();
        VerifyThumbnailButtons();
        VerifyNotificationHistory(supportRoot);
        VerifyStartupSnapshot(supportRoot);
        VerifyRound7Sources(repositoryRoot);
    }

    private static void VerifyGettingStarted(string supportRoot)
    {
        var fresh = GettingStarted.Evaluate(null, 0, 0, 0);
        Check(fresh.Count == 4 && fresh.All(step => !step.Done), "a new installation starts with four open steps");
        Check(GettingStarted.NextStep(fresh) == GettingStarted.StepCreateSite, "the first open step is creating a site");
        Check(GettingStarted.ShouldShow(false, fresh), "the checklist shows while steps are open");
        Check(!GettingStarted.ShouldShow(true, fresh), "a dismissed checklist stays hidden");

        var partly = GettingStarted.Evaluate(["open-site", "unknown", "open-site"], 2, 0, 1);
        Check(GettingStarted.DoneCount(partly) == 3, "existing sites, dumps and marked steps count as done");
        Check(GettingStarted.NextStep(partly) == GettingStarted.StepSendMail, "the next step skips what is done");
        Check(
            GettingStarted.NormalizeSteps(["try-dump", "bogus", "create-site", "try-dump"])
                .SequenceEqual(new[] { GettingStarted.StepCreateSite, GettingStarted.StepTryDump }),
            "stored steps are de-duplicated, ordered and unknown ids dropped"
        );
        var all = GettingStarted.Evaluate(GettingStarted.Steps, 0, 0, 0);
        Check(!GettingStarted.ShouldShow(false, all), "the checklist hides itself once every step is done");

        var store = new SiteConfigurationStore(Path.Combine(supportRoot, "round7-getting-started"));
        Check(store.MarkGettingStartedStep(GettingStarted.StepOpenSite), "a new step is written");
        Check(!store.MarkGettingStartedStep(GettingStarted.StepOpenSite), "a step is written only once");
        Check(!store.MarkGettingStartedStep("../../evil"), "unknown steps are refused");
        store.UpdateGettingStartedDismissed(true);
        var reloaded = new SiteConfigurationStore(store.SupportRoot).Load();
        Check(
            reloaded.GettingStartedDismissed
                && reloaded.GettingStartedSteps.SequenceEqual(new[] { GettingStarted.StepOpenSite }),
            "the checklist state survives a restart"
        );
    }

    private static void VerifyTrayPanelPlacement()
    {
        var display = new PanelBounds(0, 0, 1920, 1080);
        var bottomWork = new PanelBounds(0, 0, 1920, 1032);
        Check(TrayPanelPlacement.Edge(display, bottomWork) == TaskbarEdge.Bottom, "a bottom taskbar is detected");
        Check(TrayPanelPlacement.Edge(display, new PanelBounds(0, 48, 1920, 1032)) == TaskbarEdge.Top, "a top taskbar is detected");
        Check(TrayPanelPlacement.Edge(display, new PanelBounds(62, 0, 1858, 1080)) == TaskbarEdge.Left, "a left taskbar is detected");
        Check(TrayPanelPlacement.Edge(display, new PanelBounds(0, 0, 1858, 1080)) == TaskbarEdge.Right, "a right taskbar is detected");

        var near = TrayPanelPlacement.Place(display, bottomWork, (1800, 1050), 360, 520, 12, false);
        Check(near.Right <= bottomWork.Right - 12 && near.Bottom == bottomWork.Bottom - 12, "the panel sits above the taskbar inside the work area");
        var keyboard = TrayPanelPlacement.Place(display, bottomWork, null, 360, 520, 12, true);
        Check(keyboard.X == 12, "without a click point a right-to-left panel opens in the left corner");
        var tiny = TrayPanelPlacement.Place(display, new PanelBounds(0, 0, 300, 400), (10, 10), 360, 520, 12, false);
        Check(tiny.Width == 276 && tiny.Height == 376 && tiny.X == 12 && tiny.Y == 12, "the panel shrinks to fit a small work area");
        var left = TrayPanelPlacement.Place(display, new PanelBounds(62, 0, 1858, 1080), (30, 5), 360, 520, 12, false);
        Check(left.X == 74 && left.Y == 12, "next to a left taskbar the panel stays on screen");
        Check(TrayPanelPlacement.Scale(360, 1.5) == 540 && TrayPanelPlacement.Scale(360, 0) == 360, "the panel size follows the monitor scale");
    }

    private static void VerifyThumbnailButtons()
    {
        var stopped = TaskbarThumbnailButtons.For(running: false, degraded: false, busy: false, hasSite: true);
        Check(
            stopped.Select(button => button.Button).SequenceEqual(new[] { ThumbnailButton.StartAll, ThumbnailButton.StopAll, ThumbnailButton.OpenSite }),
            "the thumbnail buttons keep their order"
        );
        Check(stopped[0].Enabled && !stopped[1].Enabled && !stopped[2].Enabled, "a stopped environment can only be started");
        var running = TaskbarThumbnailButtons.For(running: true, degraded: false, busy: false, hasSite: true);
        Check(!running[0].Enabled && running[1].Enabled && running[2].Enabled, "a running environment can be stopped and a site opened");
        var busy = TaskbarThumbnailButtons.For(running: true, degraded: false, busy: true, hasSite: false);
        Check(!busy[0].Enabled && !busy[1].Enabled && !busy[2].Enabled, "nothing can be clicked while Start or Stop runs or no site is known");
        Check(TaskbarThumbnailButtons.For(false, true, false, false)[1].Enabled, "a degraded environment can still be stopped");

        Check(TaskbarThumbnailButtons.FromCommand(0x1800_0002UL) == ThumbnailButton.StopAll, "a thumbnail click is decoded");
        Check(TaskbarThumbnailButtons.FromCommand(0x0000_0002UL) is null, "a menu command is not a thumbnail click");
        Check(TaskbarThumbnailButtons.FromCommand(0x1800_0009UL) is null, "unknown thumbnail ids are ignored");
        Check(
            TaskbarThumbnailButtons.Signature(stopped, "a") != TaskbarThumbnailButtons.Signature(running, "a")
                && TaskbarThumbnailButtons.Signature(running, "a") != TaskbarThumbnailButtons.Signature(running, "b"),
            "the buttons redraw when their state or tooltip changes"
        );
    }

    private static void VerifyNotificationHistory(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "round7-bell");
        var history = new NotificationHistory(root);
        var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var changes = 0;
        history.Changed += (_, _) => changes++;

        var first = history.Add(AppNotifications.ServiceStopped("Redis", 1), start, shown: false);
        Check(first.Severity == NotificationSeverity.Error && !first.Shown && !first.Read, "a hidden notification is still kept, unread");
        var repeat = history.Add(AppNotifications.ServiceStopped("Redis", 1), start.AddMinutes(2), shown: true);
        Check(history.Entries.Count == 1 && repeat.Count == 2 && repeat.Shown, "a repeat within the merge window is counted, not duplicated");
        history.Add(AppNotifications.ShareEnded("blog.test"), start.AddMinutes(3), shown: true);
        Check(history.UnreadCount == 2 && history.Entries[0].Severity == NotificationSeverity.Warning, "the newest notification comes first");
        history.MarkRead(repeat.Id);
        Check(history.UnreadCount == 1, "one entry can be marked read");
        history.Add(AppNotifications.ServiceStopped("Redis", 1), start.AddMinutes(4), shown: true);
        Check(history.Entries.Count == 3, "a read entry is not merged with a new one");
        Check(
            history.Entries.All(entry => entry.Action is null || NotificationActions.Parse(entry.Action) is not null),
            "stored actions still pass the notification allow-list"
        );

        var reloaded = new NotificationHistory(root);
        Check(reloaded.Entries.Count == 3 && reloaded.UnreadCount == 2, "the bell survives a restart");
        reloaded.MarkAllRead();
        Check(reloaded.UnreadCount == 0, "closing the list marks everything read");
        for (var index = 0; index < NotificationHistory.Capacity + 10; index++)
        {
            reloaded.Add(AppNotifications.ShareEnded($"site{index}.test"), start.AddHours(index), shown: true);
        }
        Check(reloaded.Entries.Count == NotificationHistory.Capacity, "the bell keeps a bounded number of entries");
        reloaded.Clear();
        Check(reloaded.Entries.Count == 0 && new NotificationHistory(root).Entries.Count == 0, "Clear empties the bell for good");
        Check(changes >= 5, "the bell badge is told about every change");

        File.WriteAllText(history.HistoryPath, "{ not json");
        Check(new NotificationHistory(root).Entries.Count == 0, "a damaged history file is ignored");
    }

    private static void VerifyStartupSnapshot(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "round7-snapshot");
        var store = new StartupSnapshotStore(root);
        Check(
            store.SnapshotPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && store.SnapshotPath.EndsWith(Path.Combine("Cache", "startup-snapshot.json"), StringComparison.OrdinalIgnoreCase),
            "the snapshot stays in the HerdMe cache folder"
        );
        Check(store.Load() is null, "there is no snapshot on the first launch");
        var now = DateTimeOffset.UtcNow;
        store.SaveSites(
            [
                new SiteRecord { Name = "blog", Path = @"C:\Sites\blog", Domain = "blog.test", IsFavorite = true },
                new SiteRecord { Name = "broken", Path = "", Domain = "broken.test" }
            ],
            now
        );
        store.SaveCounts(3, 5, null, 1, now);
        store.SaveCounts(null, null, 7, null, now);
        var loaded = new StartupSnapshotStore(root).Load(now);
        Check(
            loaded is { ServiceCount: 3, MailCount: 5, DumpCount: 7, HealthIssueCount: 1 }
                && loaded.Sites.Count == 1
                && loaded.Sites[0].Domain == "blog.test",
            "sites and counts are restored, and incomplete sites are dropped"
        );
        Check(new StartupSnapshotStore(root).Load(now + StartupSnapshotStore.MaximumAge + TimeSpan.FromDays(1)) is null, "a very old snapshot is ignored");

        var copy = StartupSnapshotStore.CopySite(loaded!.Sites[0]);
        copy.Domain = "changed.test";
        Check(loaded.Sites[0].Domain == "blog.test", "a restored site is copied, never shared");

        File.WriteAllText(store.SnapshotPath, "{\"SchemaVersion\":99,\"Sites\":[]}");
        Check(new StartupSnapshotStore(root).Load() is null, "a snapshot from a newer HerdMe is ignored");
        File.WriteAllText(store.SnapshotPath, "not json");
        Check(new StartupSnapshotStore(root).Load() is null, "a damaged snapshot is ignored");
    }

    private static void VerifyRound7Sources(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        var dashboardXaml = Read("Pages", "DashboardPage.xaml");
        Check(dashboardXaml.Contains("AutomationProperties.AutomationId=\"DashboardGettingStarted\"", StringComparison.Ordinal), "the Dashboard has the Getting started card");
        Check(dashboardXaml.Contains("GettingStartedDismiss_Click", StringComparison.Ordinal), "the Getting started card can be hidden");
        var gettingStarted = Read("Pages", "DashboardPage.GettingStarted.cs");
        Check(gettingStarted.Contains("\"CommonUndo\"", StringComparison.Ordinal), "hiding Getting started can be undone");
        Check(gettingStarted.Contains("DashboardUpdating", StringComparison.Ordinal), "snapshot counts are marked as updating");
        var dashboard = Read("Pages", "DashboardPage.xaml.cs");
        Check(
            dashboard.IndexOf("ShowSnapshotCounts();", StringComparison.Ordinal) is var snapshot and >= 0
                && snapshot < dashboard.IndexOf("await RefreshAsync();", StringComparison.Ordinal),
            "the Dashboard paints the saved counts before refreshing"
        );
        var activity = Read("MainWindow.Activity.cs");
        foreach (var step in new[] { "StepSendMail", "StepTryDump", "StepOpenSite" })
        {
            Check(activity.Contains($"MarkGettingStarted(GettingStarted.{step})", StringComparison.Ordinal), $"the {step} step is marked when it happens");
        }

        var mainXaml = Read("MainWindow.xaml");
        Check(mainXaml.Contains("x:Name=\"TitleBarBellButton\"", StringComparison.Ordinal) && mainXaml.Contains("TitleBarBellBadge", StringComparison.Ordinal), "the title bar has the notification bell and its badge");
        Check(Read("MainWindow.TitleBar.cs").Contains("TitleBarBellButton", StringComparison.Ordinal), "the bell stays clickable inside the drag region");
        Check(Read("MainWindow.Bell.cs").Contains("RunHistoryActionAsync", StringComparison.Ordinal), "bell entries run their action through App");
        var notifications = Read("App.Notifications.cs");
        Check(
            notifications.IndexOf("NotificationHistory.Add(", StringComparison.Ordinal) is var added and >= 0
                && added < notifications.IndexOf("if (!show) return;", StringComparison.Ordinal),
            "notifications Windows does not show are still recorded in the bell"
        );
        Check(notifications.Contains("NotificationActions.Parse(storedAction)", StringComparison.Ordinal), "stored bell actions pass the allow-list again");

        var panel = Read("TrayPanelWindow.cs");
        Check(panel.Contains("IsShownInSwitchers = false", StringComparison.Ordinal), "the tray panel never shows in Alt+Tab");
        Check(panel.Contains("WindowActivationState.Deactivated", StringComparison.Ordinal) && panel.Contains("VirtualKey.Escape", StringComparison.Ordinal), "the tray panel hides on focus loss and Esc");
        Check(panel.Contains("args.Cancel = true", StringComparison.Ordinal), "Alt+F4 only hides the tray panel");
        var appXaml = Read("App.xaml.cs");
        Check(appXaml.Contains("LeftClickCommand = panelCommand", StringComparison.Ordinal), "a left click opens the quick panel");
        Check(appXaml.Contains("ContextFlyout", StringComparison.Ordinal) || appXaml.Contains("ContextMenuMode", StringComparison.Ordinal), "the right-click menu stays");
        Check(appXaml.Contains("CloseTrayPanel();", StringComparison.Ordinal), "the tray panel closes when HerdMe quits");

        var thumbnail = Read("Services", "TaskbarThumbnailToolbar.cs");
        Check(thumbnail.Contains("TaskbarThumbnailButtons.FromCommand", StringComparison.Ordinal), "thumbnail clicks are decoded by the pure helper");
        var project = Read("HerdMe.Windows.csproj");
        foreach (var icon in new[] { "Thumb-Start.ico", "Thumb-Stop.ico", "Thumb-Open.ico" })
        {
            Check(project.Contains(icon, StringComparison.Ordinal) && File.Exists(Path.Combine(app, "Assets", icon)), $"{icon} ships with the app");
        }

        Check(Read("App.Commands.cs").Contains("StartupSnapshot?.SaveSites(next)", StringComparison.Ordinal), "each scan updates the startup snapshot");
        Check(appXaml.Contains("SeedKnownSitesFromSnapshot();", StringComparison.Ordinal), "the tray starts from the saved sites");
        Check(Read("Pages", "SitesPage.xaml.cs").Contains("PrefillFromKnownSites();", StringComparison.Ordinal), "the Sites list starts from the saved sites");
    }
}
