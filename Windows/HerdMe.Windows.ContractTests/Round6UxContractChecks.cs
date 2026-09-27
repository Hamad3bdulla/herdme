using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 6 (front end): resizable Sites panel, hover preview, F2 rename, one operations bar,
// deferred Undo, clearer errors, validation while typing, PHP compare and php.ini editor,
// service cards, the title bar search and status menu, and notifications with actions.
internal static partial class ContractChecks
{
    internal static void VerifyRound6UxContracts(string repositoryRoot, string supportRoot)
    {
        VerifySitesListWidth(supportRoot);
        VerifySiteQuickProbe(supportRoot);
        VerifySiteRenamer();
        VerifyErrorPresentation();
        VerifyInputValidation();
        VerifyPhpIniEditor(supportRoot);
        VerifyServiceLogTail(supportRoot);
        VerifyTitleBarSearch();
        VerifyNotificationActions();
        VerifyRound6Sources(repositoryRoot);
    }

    private static void VerifySitesListWidth(string supportRoot)
    {
        Check(
            SiteConfigurationStore.ClampSitesListWidth(double.NaN) == WindowsSiteSettings.SitesListWidthDefault,
            "an unreadable Sites list width falls back to the default"
        );
        Check(
            SiteConfigurationStore.ClampSitesListWidth(10) == WindowsSiteSettings.SitesListWidthMinimum
                && SiteConfigurationStore.ClampSitesListWidth(5000) == WindowsSiteSettings.SitesListWidthMaximum,
            "the Sites list width stays between its minimum and maximum"
        );
        var store = new SiteConfigurationStore(Path.Combine(supportRoot, "round6-layout"));
        store.UpdateSitesListWidth(333);
        Check(
            new SiteConfigurationStore(store.SupportRoot).Load().SitesListWidth == 333,
            "the dragged Sites list width is remembered"
        );
    }

    private static void VerifySiteQuickProbe(string supportRoot)
    {
        Check(new SiteProbeResult(200, 12, null).Tone == SiteProbeTone.Success, "a 200 answer is shown as healthy");
        Check(new SiteProbeResult(404, 12, null).Tone == SiteProbeTone.Caution, "a 404 answer is shown as a warning");
        Check(new SiteProbeResult(null, 0, "refused").Tone == SiteProbeTone.Critical, "no answer is shown as an error");
        Check(SiteQuickProbe.Describe(new SiteProbeResult(200, 84, null)) == "HTTP 200 - 84 ms", "the preview names the status and time");
        var first = SiteQuickProbe.ThumbnailPath(supportRoot, @"C:\Sites\blog");
        Check(
            first == SiteQuickProbe.ThumbnailPath(supportRoot, @"C:\SITES\Blog\")
                && first.StartsWith(SiteQuickProbe.ThumbnailDirectory(supportRoot), StringComparison.Ordinal)
                && first != SiteQuickProbe.ThumbnailPath(supportRoot, @"C:\Sites\shop"),
            "each site has one thumbnail under the HerdMe cache, whatever the path casing"
        );
    }

    private static void VerifySiteRenamer()
    {
        Check(SiteRenamer.Normalize(" Blog.test ", "test") == "blog", "a rename accepts the name with or without .test");
        Check(SiteRenamer.Validate("", "blog.test", "test", [], false) == SiteNameProblem.Empty, "an empty site name is refused");
        Check(SiteRenamer.Validate("my_blog", "blog.test", "test", [], false) == SiteNameProblem.InvalidCharacters, "a site name must be a DNS label");
        Check(SiteRenamer.Validate("-blog", "blog.test", "test", [], false) == SiteNameProblem.EdgeHyphen, "a site name cannot start with a hyphen");
        Check(SiteRenamer.Validate(new string('a', 64), "blog.test", "test", [], false) == SiteNameProblem.TooLong, "a site name fits one DNS label");
        Check(SiteRenamer.Validate("blog", "blog.test", "test", [], false) == SiteNameProblem.Unchanged, "the same name is not a rename");
        Check(SiteRenamer.Validate("shop", "blog.test", "test", ["SHOP.test"], false) == SiteNameProblem.Taken, "a name another site uses is refused");
        Check(SiteRenamer.Validate("shop", "blog.test", "test", [], true) == SiteNameProblem.FolderExists, "a rename never overwrites a folder");
        Check(SiteRenamer.Validate("shop", "blog.test", "test", [], false) == SiteNameProblem.None, "a free name is accepted");
        Check(
            SiteRenamer.TargetPath(@"C:\Sites\blog\", "shop") == @"C:\Sites\shop",
            "a rename keeps the folder next to the old one"
        );
    }

    private static void VerifyErrorPresentation()
    {
        Check(ErrorPresentation.Classify(new UnauthorizedAccessException()) == ErrorKind.AccessDenied, "access denied gets its own title");
        Check(ErrorPresentation.Classify(new OperationCanceledException()) == ErrorKind.Cancelled, "a cancelled task is not shown as a failure");
        Check(ErrorPresentation.Classify("The process cannot access the file because it is being used by another process.") == ErrorKind.FileInUse, "a locked file gets its own title");
        Check(ErrorPresentation.Classify("Only one usage of each socket address is normally permitted.") == ErrorKind.PortInUse, "a busy port gets its own title");
        Check(ErrorPresentation.Classify("something odd") == ErrorKind.General, "an unknown error keeps the general title");
        var details = ErrorPresentation.Details("Title", "message", "stack", "9.9.9", DateTimeOffset.UnixEpoch);
        Check(
            details.Contains("message", StringComparison.Ordinal)
                && details.Contains("stack", StringComparison.Ordinal)
                && details.Contains("HerdMe 9.9.9", StringComparison.Ordinal),
            "Copy details keeps the raw message, the details and the version"
        );
    }

    private static void VerifyInputValidation()
    {
        static bool Free(int port) => port != 5555;
        Check(InputValidation.Port(double.NaN, [], Free).Blocks, "an empty port blocks Add");
        Check(InputValidation.Port(70000, [], Free).Blocks, "a port past 65535 blocks Add");
        Check(InputValidation.Port(443, [], Free).MessageKey == "InputPortSites", "the site ports are reserved for sites");
        Check(InputValidation.Port(3306, [3306], Free).MessageKey == "InputPortHerdMe", "a port another HerdMe service uses is refused");
        Check(InputValidation.Port(5555, [], Free).MessageKey == "InputPortBusy", "a port in use on this PC is refused");
        Check(InputValidation.Port(80.5, [], Free).Blocks, "a fractional port is refused");
        Check(InputValidation.Port(6379, [], Free).Severity == InputSeverity.Valid, "a free port is accepted");
        Check(!InputValidation.ServiceName(" ", []).Blocks, "an empty service name keeps the default");
        Check(InputValidation.ServiceName("Redis", ["redis"]).Severity == InputSeverity.Warning, "a duplicate service name only warns");
        Check(InputValidation.FolderPath("relative\\path", _ => true).MessageKey == "InputPathNotAbsolute", "a relative folder is refused");
        Check(InputValidation.FolderPath(@"C:\Missing", _ => false).MessageKey == "InputPathMissing", "a missing folder is refused");
        Check(InputValidation.PhpTimezone("Arab Standard Time").MessageKey == "InputTimezoneFormat", "PHP wants an IANA time zone");
        Check(InputValidation.PhpTimezone("UTC") == InputCheck.Ok, "UTC is accepted");
    }

    private static void VerifyPhpIniEditor(string supportRoot)
    {
        var matches = PhpIniEditor.FindMatches("memory_limit\nMEMORY_limit\nmax", "memory");
        Check(matches.Count == 2 && matches[0] == 0 && matches[1] == 13, "php.ini search is case-insensitive");
        Check(PhpIniEditor.NextMatch(matches, 0, forward: true) == 13, "Next finds the following match");
        Check(PhpIniEditor.NextMatch(matches, 13, forward: true) == 0, "Next wraps to the first match");
        Check(PhpIniEditor.NextMatch(matches, 0, forward: false) == 13, "Previous wraps to the last match");
        Check(PhpIniEditor.NextMatch([], 0, forward: true) == -1, "no matches leaves the caret alone");
        Check(PhpIniEditor.Validate(PhpRuntimeInstaller.ManagedPhpIni) == PhpIniProblem.None, "the managed php.ini passes the editor check");
        Check(PhpIniEditor.Validate("[PHP]\nmemory_limit=128M\n") == PhpIniProblem.MissingRequiredExtension, "a php.ini without Laravel's extensions is refused");
        var withoutIntl = string.Join('\n', PhpRuntimeInstaller.ManagedPhpIni.Split('\n')
            .Where(line => !line.Contains("intl", StringComparison.OrdinalIgnoreCase)));
        Check(PhpIniEditor.Validate(withoutIntl) == PhpIniProblem.MissingRequiredExtension, "dropping a required extension in the editor is refused");
        Check(
            PhpIniEditor.Validate(withoutIntl, new HashSet<string>(["intl"], StringComparer.OrdinalIgnoreCase)) == PhpIniProblem.None,
            "an extension the user turned off on the PHP page may stay off in the editor"
        );
        Check(PhpIniEditor.Validate(new string(';', PhpIniEditor.MaximumLength + 1)) == PhpIniProblem.TooLarge, "a huge php.ini is refused");
        Check(PhpIniEditor.NormalizeLineEndings("a\rb\nc") == "a\r\nb\r\nc", "php.ini is saved with Windows line endings");

        var directory = Path.Combine(supportRoot, "round6-php", "8.4");
        Directory.CreateDirectory(directory);
        var path = PhpIniEditor.ConfigurationPath(Path.Combine(supportRoot, "round6-php"), "8.4");
        File.WriteAllText(path, "old");
        PhpIniEditor.SaveAtomically(path, "new\nline");
        Check(File.ReadAllText(path) == "new\r\nline", "saving replaces php.ini");
        Check(File.ReadAllText(PhpIniEditor.BackupPath(path)) == "old", "saving keeps the previous php.ini as a backup");
        Check(!File.Exists(path + ".tmp"), "saving leaves no temporary file");
    }

    private static void VerifyServiceLogTail(string supportRoot)
    {
        Check(ServiceLogTail.FromText("first\r\nsecond\r\n\r\n") == "second", "a card shows the newest non-empty log line");
        Check(ServiceLogTail.FromText("\u001b[32mready\u001b[0m\n") == "ready", "log colour codes are removed");
        var longLine = ServiceLogTail.FromText(new string('x', 500));
        Check(longLine.Length == ServiceLogTail.MaximumLineLength && longLine.EndsWith('\u2026'), "a long log line is shortened");
        Check(ServiceLogTail.LastLine(Path.Combine(supportRoot, "missing.log")) == string.Empty, "a service without a log shows nothing");
        var log = Path.Combine(supportRoot, "round6-service.log");
        File.WriteAllText(log, new string('a', 10_000) + "\nlast line\n");
        using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Check(ServiceLogTail.LastLine(log) == "last line", "the log tail is read while the service still writes it");
        }
    }

    private static void VerifyTitleBarSearch()
    {
        TitleBarSearchItem[] pages =
        [
            new(TitleBarSearchKind.Page, "sites", "Sites", "Page"),
            new(TitleBarSearchKind.Page, "services", "Services", "Page")
        ];
        TitleBarSearchItem[] sites =
        [
            new(TitleBarSearchKind.Site, @"C:\Sites\my-shop", "my-shop.test", @"C:\Sites\my-shop"),
            new(TitleBarSearchKind.Site, @"C:\Sites\blog", "blog.test", @"C:\Sites\blog")
        ];
        TitleBarSearchItem[] actions = [new(TitleBarSearchKind.Action, "stop-all", "Stop all", "Action")];
        Check(TitleBarSearch.Suggest(" ", pages, sites, actions).Count == 0, "an empty search suggests nothing");
        var results = TitleBarSearch.Suggest("s", pages, sites, actions);
        Check(results.Count > 0 && results[0].Key == "sites", "prefix matches come first, in page order");
        Check(TitleBarSearch.Suggest("shop", pages, sites, actions).Single().Kind == TitleBarSearchKind.Site, "a site matches by a word in its domain");
        Check(TitleBarSearch.Suggest("stop", pages, sites, actions).Any(item => item.Key == "stop-all"), "actions are searchable");
        Check(TitleBarSearch.Suggest("s", pages, sites, actions, maximum: 2).Count == 2, "the suggestion list is capped");
        Check(TitleBarSearch.Suggest("zzz", pages, sites, actions).Count == 0, "no match suggests nothing");
    }

    private static void VerifyNotificationActions()
    {
        var id = Guid.NewGuid();
        foreach (var action in new[]
        {
            NotificationActions.StartService(id),
            NotificationActions.OpenSite(@"C:\Sites\my blog;x=1"),
            NotificationActions.OpenSiteLogs("blog.test"),
            NotificationActions.OpenPage("logs", "NotificationActionOpenLogs")
        })
        {
            var parsed = NotificationActions.Parse(NotificationActions.Build(action));
            Check(parsed is not null && parsed.Kind == action.Kind && parsed.Argument == action.Argument, $"a {action.Kind} button survives the round trip");
        }
        Check(NotificationActions.Parse("herdme-action=page;settings-shell") is null, "a notification cannot open a page outside the list");
        Check(NotificationActions.Parse("herdme-action=start-service;not-a-guid") is null, "a notification cannot start something that is not a service id");
        Check(NotificationActions.Parse("herdme-action=run;calc.exe") is null, "unknown notification actions are ignored");
        Check(NotificationActions.Parse("page;logs") is null && NotificationActions.Parse(null) is null, "arguments from elsewhere are ignored");
        Check(NotificationActions.Parse("herdme-action=site;" + new string('a', 2000)) is null, "an oversized argument is ignored");

        var service = AppNotifications.ServiceStopped("Redis", 1, id);
        Check(
            service.Primary?.Kind == NotificationActionKind.StartService && service.Primary.Argument == id.ToString("D")
                && service.Secondary is not null,
            "a stopped service offers Restart and a second button"
        );
        Check(AppNotifications.ServiceStopped("Redis", 1).Primary is null, "without an id there is no Restart button");
        var worker = AppNotifications.SiteProcessStopped("blog", SiteBackgroundProcessKind.Queue, 1, @"C:\Sites\blog");
        Check(worker.Primary?.Kind == NotificationActionKind.OpenSite && worker.Secondary?.Kind == NotificationActionKind.OpenSiteLogs, "a stopped worker offers Open site and Open logs");
        Check(AppNotifications.ShareEnded("blog.test").Primary?.Argument == "blog.test", "a dropped public link opens its site");
        Check(AppNotifications.UpdateAvailable("1.2.3").Primary?.Argument == "updates", "an update notification opens Updates");
        Check(AppNotifications.CrashReported().Primary?.Argument == "general", "a crash notification opens General");
        Check(!new WindowsSiteSettings().ActionNotifications, "notification buttons are off by default");
    }

    private static void VerifyRound6Sources(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        var sites = Read("Pages", "SitesPage.xaml");
        Check(sites.Contains("SitesListColumn", StringComparison.Ordinal), "the Sites page keeps a resizable list column");
        Check(Read("Pages", "SitesPage.Filters.cs").Contains("VirtualKey.F2", StringComparison.Ordinal), "F2 renames the selected site");

        var mainXaml = Read("MainWindow.xaml");
        foreach (var id in new[] { "OperationsBar", "OperationsBarCancel", "TitleBarSearch", "TitleBarStatusButton" })
        {
            Check(mainXaml.Contains($"AutomationProperties.AutomationId=\"{id}\"", StringComparison.Ordinal), $"the main window has {id}");
        }
        Check(Read("MainWindow.StatusBar.cs").Contains("RuntimeOperations.Shared.CancelAll()", StringComparison.Ordinal), "the operations bar cancels every running operation");
        var deferred = Read("MainWindow.Deferred.cs");
        Check(deferred.Contains("DeferredActionSeconds = 5", StringComparison.Ordinal), "Undo waits five seconds");
        Check(Read("App.xaml.cs").Contains("FlushDeferredActionsAsync", StringComparison.Ordinal), "quitting carries out actions still showing Undo");

        var titleBar = Read("MainWindow.TitleBar.cs");
        Check(
            titleBar.Contains("NonClientRegionKind.Passthrough", StringComparison.Ordinal)
                && titleBar.Contains("FlowDirection.RightToLeft", StringComparison.Ordinal),
            "only the title bar controls are clickable, also in right-to-left layouts"
        );
        Check(titleBar.Contains("DashboardQuickStopConfirmTitle", StringComparison.Ordinal), "Stop all from the title bar asks first");

        var php = Read("Pages", "PhpPage.xaml");
        Check(
            php.Contains("PhpCompareSection", StringComparison.Ordinal) && php.Contains("PhpIniSection", StringComparison.Ordinal),
            "the PHP page has the version comparison and the php.ini editor"
        );
        var ini = Read("Pages", "PhpPage.Ini.cs");
        Check(ini.Contains("PhpIniEditor.SaveAtomically", StringComparison.Ordinal), "php.ini is saved atomically with a backup");
        Check(ini.Contains("PhpModuleProbeCache.Invalidate", StringComparison.Ordinal), "saving php.ini refreshes the loaded extensions");

        var servicesXaml = Read("Pages", "ServicesPage.xaml");
        Check(servicesXaml.Contains("AutomationProperties.AutomationId=\"ServiceCards\"", StringComparison.Ordinal), "services are shown as cards");
        Check(Read("Pages", "ServicesPage.Cards.cs").Contains("Task.Run", StringComparison.Ordinal), "service logs are read off the UI thread");

        var notifications = Read("App.Notifications.cs");
        Check(notifications.Contains("TryShowActionNotification(notification)", StringComparison.Ordinal), "Windows notifications with buttons fall back to the tray balloon");
        var actionNotifications = Read("App.ActionNotifications.cs");
        Check(actionNotifications.Contains("UnregisterAll()", StringComparison.Ordinal), "turning notification buttons off removes the registration");
        Check(actionNotifications.Contains("if (!wanted || !TryRegisterActionNotifications(out _)) return;", StringComparison.Ordinal), "HerdMe registers for notifications only when the switch is on");
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.EndsWith("App.ActionNotifications.cs", StringComparison.Ordinal)) continue;
            Check(!File.ReadAllText(file).Contains("AppNotificationManager", StringComparison.Ordinal), $"{Path.GetFileName(file)} does not register notifications");
        }
        var installer = File.ReadAllText(Path.Combine(repositoryRoot, "Windows", "installer.iss"));
        Check(installer.Contains("--unregister-notifications", StringComparison.Ordinal), "uninstall removes the notification registration");
        Check(Read("App.xaml.cs").Contains("TryRunUnregisterNotifications(", StringComparison.Ordinal), "the uninstall switch runs before the single-instance check");
        Check(Read("Pages", "GeneralPage.xaml").Contains("x:Name=\"ActionNotificationsToggle\"", StringComparison.Ordinal), "General has the notification buttons switch");
    }
}
