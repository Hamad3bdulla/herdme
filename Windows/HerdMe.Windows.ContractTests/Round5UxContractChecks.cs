using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 5 (front end): the pure helpers behind the new views (language, status bar,
// What's new, mail inspection, dump tree, log colours, ANSI console), the settings they
// persist, and the source-level promises of the new screens.
internal static partial class ContractChecks
{
    internal static void VerifyRound5UxContracts(string repositoryRoot, string supportRoot)
    {
        VerifyUiLanguageNormalization();
        VerifyStatusBarSummary();
        VerifyActivityTimelineBound();
        VerifyChangelogReader();
        VerifyMailInspection();
        VerifyDumpTree();
        VerifyLogHighlighting();
        VerifyAnsiParser();
        VerifyRound5Settings(supportRoot);
        VerifyRound5Sources(repositoryRoot);
    }

    private static void VerifyUiLanguageNormalization()
    {
        Check(UiLanguageSettings.Normalize(null) == UiLanguageSettings.System, "no language choice follows Windows");
        Check(UiLanguageSettings.Normalize(" en ") == UiLanguageSettings.English, "en is normalized to en-US");
        Check(UiLanguageSettings.Normalize("AR-sa") == UiLanguageSettings.Arabic, "any Arabic region uses the Arabic resources");
        Check(UiLanguageSettings.Normalize("fr-FR") == UiLanguageSettings.System, "an unsupported language falls back to Windows");
        Check(UiLanguageSettings.Supported.Count == 3, "the language setting offers Windows, English and Arabic");
    }

    private static void VerifyStatusBarSummary()
    {
        Check(StatusBarPresentation.Summarize([]) == StatusBarPresentation.Idle, "no downloads leaves the status bar idle");
        Check(
            StatusBarPresentation.Summarize([new DownloadItem("PHP 8.4", false, 10, 10)]) == StatusBarPresentation.Idle,
            "finished downloads are not shown"
        );
        var one = StatusBarPresentation.Summarize([new DownloadItem("PHP 8.4", true, 25, 100)]);
        Check(
            one.Active == 1 && one.Name == "PHP 8.4" && one.State == TaskbarProgressState.Normal && one.Percent == 25,
            "one sized download shows its name and percent"
        );
        var two = StatusBarPresentation.Summarize([
            new DownloadItem("PHP 8.4", true, 50, 100),
            new DownloadItem("Node.js", true, 150, 100)
        ]);
        Check(
            two.Active == 2 && two.Name is null && two.Completed == 150 && two.Total == 200 && two.Percent == 75,
            "several downloads add up and received bytes are clamped to the size"
        );
        var unknown = StatusBarPresentation.Summarize([
            new DownloadItem("PHP 8.4", true, 50, 100),
            new DownloadItem("MariaDB", true, 10, null)
        ]);
        Check(
            unknown.State == TaskbarProgressState.Indeterminate && unknown.Percent == 0,
            "a download without a known size makes taskbar progress indeterminate"
        );
        Check((int)TaskbarProgressState.Error == 4 && (int)TaskbarProgressState.Paused == 8, "taskbar states match TBPFLAG");
    }

    private static void VerifyActivityTimelineBound()
    {
        var timeline = new ActivityTimeline();
        for (var index = 0; index < ActivityTimeline.Capacity + 10; index++)
        {
            timeline.Add(new ActivityEvent(ActivityEventKind.Mail, "mail " + index, string.Empty, DateTimeOffset.UtcNow, "mail"));
        }
        Check(timeline.Count == ActivityTimeline.Capacity, "the session timeline is bounded");
        var latest = timeline.Snapshot(3);
        Check(
            latest.Count == 3 && latest[0].Title == "mail " + (ActivityTimeline.Capacity + 9),
            "the timeline lists the newest event first"
        );
        Check(timeline.Snapshot(-1).Count == 0, "a negative snapshot size returns nothing");
    }

    private static void VerifyChangelogReader()
    {
        const string markdown = "# Changelog\r\n\r\n## [Unreleased]\n\n### Added\n\n- Not yet\n\n"
            + "## [1.2.0] - 2026-09-27\n\n### Added\n\n- First item\n  wrapped onto two lines\n* Second item\n\n"
            + "Loose paragraph\n\n### Fixed\n\n- A fix\n\n## [1.1.0] - 2026-09-01\n\n### Added\n\n- Old item\n";
        var section = ChangelogReader.Section(markdown, "1.2.0");
        Check(
            section.Count == 2 && section[0].Title == "Added" && section[1].Title == "Fixed",
            "What's new reads only the groups of the current version"
        );
        Check(
            section[0].Items.SequenceEqual(["First item wrapped onto two lines", "Second item"]),
            "wrapped bullets are joined and loose paragraphs are skipped"
        );
        Check(ChangelogReader.Section(markdown, "9.9.9").Count == 0, "an unknown version has no notes");
        Check(
            !section.SelectMany(group => group.Items).Contains("Old item"),
            "the next version heading ends the section"
        );
        Check(
            !ChangelogReader.ShouldShow(null, "1.2.0") && !ChangelogReader.ShouldShow("", "1.2.0"),
            "What's new never opens on a first install"
        );
        Check(ChangelogReader.ShouldShow("1.1.0", "1.2.0"), "What's new opens once after an upgrade");
        Check(
            !ChangelogReader.ShouldShow("1.2.0", "1.2.0") && !ChangelogReader.ShouldShow("1.3.0", "1.2.0"),
            "What's new does not reopen for the same or an older version"
        );
        Check(
            ChangelogReader.ShouldShow("dev", "1.2.0") && !ChangelogReader.ShouldShow("DEV", "dev"),
            "non-numeric versions compare as text"
        );
    }

    private static void VerifyMailInspection()
    {
        var raw = "From: HerdMe <app@example.test>\r\nSubject: =?UTF-8?B?SGVsbG8=?=\r\nX-Long: first\r\n\tsecond\r\n"
            + "no colon here\r\n\r\nBody: not a header\r\n";
        var headers = MailInspection.Headers(raw);
        Check(headers.Count == 3, "mail headers stop at the blank line and skip malformed lines");
        Check(headers.Any(header => header.Name == "Subject" && header.Value == "Hello"), "encoded mail headers are decoded");
        Check(headers.Any(header => header.Name == "X-Long" && header.Value == "first second"), "folded mail headers are unfolded");
        var many = new StringBuilder();
        for (var index = 0; index < MailInspection.MaximumHeaders + 20; index++)
        {
            many.Append("X-").Append(index).Append(": v\n");
        }
        Check(MailInspection.Headers(many.ToString()).Count == MailInspection.MaximumHeaders, "the header list is bounded");

        var html = "<p><a href=\"https://example.test/a\">Open <b>it</b></a> "
            + "<a href='http://example.test/b'>Plain</a> <a href=http://localhost:8000/c>Local</a> "
            + "<a href=\"/relative\">Rel</a> <a href=\"#\">Empty</a> <a href=\"mailto:a@example.test\">Mail</a> "
            + "<a href=\"https://example.test/a\">Duplicate</a> <a href=\"https://127.0.0.1/x\">Loop</a></p>";
        var links = MailInspection.Links(html);
        Check(links.Count == 7, "mail links are listed once each");
        Check(
            links[0] is { Url: "https://example.test/a", Text: "Open it", Issue: MailLinkIssue.None },
            "link text drops inner tags"
        );
        Check(links[1].Issue == MailLinkIssue.Insecure, "http links are flagged as insecure");
        Check(
            links[2].Issue == MailLinkIssue.Localhost && links[6].Issue == MailLinkIssue.Localhost,
            "localhost and loopback links are flagged"
        );
        Check(links[3].Issue == MailLinkIssue.Relative, "relative links are flagged");
        Check(links[4].Issue == MailLinkIssue.Empty, "empty links are flagged");
        Check(links[5].Issue == MailLinkIssue.None, "mailto links are fine");
        Check(
            MailInspection.Links(null).Count == 0 && MailInspection.Links("plain text").Count == 0,
            "a text-only message has no links"
        );
        Check(
            MailInspection.Classify("ftp://example.test/file") == MailLinkIssue.Relative,
            "non-web links are flagged as not opening for recipients"
        );
    }

    private static void VerifyDumpTree()
    {
        var json = DumpTree.FromJson("payload", "{\"user\":{\"id\":7,\"tags\":[\"a\",\"b\"]},\"ok\":true,\"none\":null}");
        Check(json is not null && json.Label == "payload" && json.Children.Count == 3, "a JSON object becomes an expandable node");
        var user = json!.Children.First(child => child.Label == "user");
        Check(
            user.Children.Count == 2 && user.Children.Any(child => child.Label == "tags" && child.Children.Count == 2),
            "nested JSON objects and arrays expand"
        );
        Check(
            DumpTree.FromJson("x", "not json") is null && DumpTree.FromJson("x", "\"text\"") is null,
            "plain strings are not turned into trees"
        );
        Check(DumpTree.FromJson("x", "{broken") is null, "broken JSON is left as text");

        var wide = "[" + string.Join(",", Enumerable.Range(0, DumpTree.MaximumNodes * 2)) + "]";
        var bounded = DumpTree.FromJson("wide", wide);
        Check(bounded is not null && DumpTree.Count(bounded) <= DumpTree.MaximumNodes + 2, "a huge dump tree is bounded");
        var deep = new string('[', DumpTree.MaximumDepth + 8) + new string(']', DumpTree.MaximumDepth + 8);
        var deepTree = DumpTree.FromJson("deep", deep);
        Check(
            deepTree is null || DumpTree.Count(deepTree) <= DumpTree.MaximumDepth + 2,
            "a deeply nested dump cannot recurse without limit"
        );

        var serialized = "a:2:{s:4:\"name\";s:6:\"HerdMe\";s:4:\"list\";a:1:{i:0;i:42;}}";
        var tree = DumpTree.FromPayload(Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized)));
        Check(tree is not null && DumpTree.Count(tree) == 4, "a PHP serialized dump becomes a tree");
        Check(
            DumpTree.FromPayload("%%%") is null && DumpTree.FromPayload(string.Empty) is null,
            "an unreadable dump payload has no tree"
        );
    }

    private static void VerifyLogHighlighting()
    {
        const string text = "[2026-09-27 10:00:00] local.ERROR: boom\n"
            + "#0 /app/Http/Kernel.php(12): handle()\n"
            + "[2026-09-27 10:00:01] local.INFO: fine\n"
            + "[2026-09-27 10:00:02] local.WARNING: careful\r\n"
            + "PHP Deprecated: old call\n"
            + "[2026-09-27 10:00:03] local.DEBUG: details\n"
            + "plain line\n"
            + "2026/09/27 10:00:04 [error] 12#0: upstream failed\n";
        var spans = LogHighlighting.Scan(text);
        Check(spans.Select(span => span.Line).SequenceEqual([0, 1, 3, 4, 5, 7]), "log colouring finds the right lines");
        Check(
            spans[0].Level == LogLineLevel.Error && spans[1].Level == LogLineLevel.Error,
            "a stack trace line belongs to the error above it"
        );
        Check(
            spans[2].Level == LogLineLevel.Warning && spans[3].Level == LogLineLevel.Warning,
            "warnings and deprecations are warnings"
        );
        Check(
            spans[4].Level == LogLineLevel.Debug && spans[5].Level == LogLineLevel.Error,
            "debug and nginx error lines are classified"
        );
        Check(
            text.Substring(spans[2].Start, spans[2].Length).EndsWith("careful", StringComparison.Ordinal),
            "a span covers its line without the carriage return"
        );
        Check(
            LogHighlighting.Classify("[2026-09-27 10:00:01] local.INFO: an error word") is null,
            "a Laravel level wins over words in the message"
        );
        var noisy = string.Concat(Enumerable.Repeat("error\n", 50));
        Check(LogHighlighting.Scan(noisy, 10).Count == 10, "log colouring is bounded");
        Check(LogHighlighting.Scan(string.Empty).Count == 0, "an empty log has no colours");
    }

    private static void VerifyAnsiParser()
    {
        var parsed = AnsiParser.Parse("\u001b[32mINFO\u001b[39m done \u001b[1;31mFAIL\u001b[0m\r\n");
        Check(parsed.Text == "INFO done FAIL\n", "ANSI codes and carriage returns are removed from console text");
        Check(
            parsed.Spans.Count == 2
                && parsed.Spans[0] == new AnsiSpan(0, 4, AnsiColor.Green)
                && parsed.Spans[1] == new AnsiSpan(10, 4, AnsiColor.Red),
            "ANSI colours become spans over the visible text"
        );
        Check(
            AnsiParser.Parse("\u001b[90mgray\u001b[95mpink").Spans.Select(span => span.Color)
                .SequenceEqual([AnsiColor.Gray, AnsiColor.Magenta]),
            "bright ANSI colours map to the palette"
        );
        Check(
            AnsiParser.Strip("\u001b]8;;https://example.test\u0007link\u001b]8;;\u0007 \u001b[2K\u001b[1Gok") == "link ok",
            "hyperlinks and cursor sequences are dropped"
        );
        Check(
            AnsiParser.Parse("\u001b[38;5;208morange\u001b[0m").Spans.Count == 0,
            "extended colours fall back to the default colour"
        );

        var parser = new AnsiParser();
        var first = parser.Append("start \u001b[3");
        var second = parser.Append("3mwarn\u001b[0m end");
        Check(first.Text == "start " && first.Spans.Count == 0, "an escape split across chunks is held back");
        Check(
            second.Text == "warn end" && second.Spans.Count == 1 && second.Spans[0] == new AnsiSpan(0, 4, AnsiColor.Yellow),
            "the held escape applies once the rest arrives"
        );
        var carried = new AnsiParser();
        carried.Append("\u001b[36m");
        Check(
            carried.Current == AnsiColor.Cyan && carried.Append("next").Spans.SequenceEqual([new AnsiSpan(0, 4, AnsiColor.Cyan)]),
            "a colour carries over into the next chunk"
        );

        Check(
            AnsiParser.WithArtisanColors(["migrate", "--force"]).SequenceEqual(["migrate", "--ansi", "--force"]),
            "Artisan is asked for colours right after the command name"
        );
        Check(
            AnsiParser.WithArtisanColors(["list", "--no-ansi"]).SequenceEqual(["list", "--no-ansi"]),
            "an explicit --no-ansi is kept"
        );
        Check(AnsiParser.WithArtisanColors([]).Count == 0, "no Artisan command stays empty");
    }

    private static void VerifyRound5Settings(string supportRoot)
    {
        var defaults = new WindowsSiteSettings();
        Check(
            defaults.UiLanguage == UiLanguageSettings.System && !defaults.ReduceMotion
                && defaults.LastSeenVersion.Length == 0 && defaults.SeenTips.Count == 0,
            "round 5 settings default to Windows language, motion on, and nothing seen"
        );
        var store = new SiteConfigurationStore(Path.Combine(supportRoot, "round5-settings"));
        store.UpdateUiLanguage("ar-SA");
        store.UpdateReduceMotion(true);
        store.UpdateLastSeenVersion(" 1.2.0 ");
        store.MarkTipSeen("status-bar");
        store.MarkTipSeen("status-bar");
        store.MarkTipSeen("dashboard-health");
        var loaded = new SiteConfigurationStore(store.SupportRoot).Load();
        Check(loaded.UiLanguage == UiLanguageSettings.Arabic, "the language choice is saved normalized");
        Check(loaded.ReduceMotion, "reduce motion is saved");
        Check(loaded.LastSeenVersion == "1.2.0", "the last seen version is saved trimmed");
        Check(loaded.SeenTips.SequenceEqual(["status-bar", "dashboard-health"]), "each tip is remembered once");
        store.UpdateUiLanguage("klingon");
        Check(
            new SiteConfigurationStore(store.SupportRoot).Load().UiLanguage == UiLanguageSettings.System,
            "an unknown language is saved as follow Windows"
        );
    }

    private static void VerifyRound5Sources(string repositoryRoot)
    {
        var project = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(string relative) => File.ReadAllText(Path.Combine(project, relative));
        bool Has(string text, string value) => text.Contains(value, StringComparison.Ordinal);

        var mainWindow = Read("MainWindow.xaml");
        Check(
            Has(mainWindow, "AutomationProperties.AutomationId=\"StatusBar\"") && Has(mainWindow, "x:Name=\"StatusBarTip\""),
            "the main window has a status bar and its tip"
        );
        Check(Has(Read("Services/TaskbarOverlay.cs"), "SetProgressState"), "downloads drive taskbar progress");

        var dashboard = Read("Pages/DashboardPage.xaml");
        Check(
            Has(dashboard, "x:Name=\"HealthTab\"") && Has(dashboard, "x:Name=\"HealthTabTip\""),
            "dashboard health has its own tab and a one-time tip"
        );
        var fixIt = Read("Pages/DashboardPage.FixIt.cs");
        Check(
            Has(fixIt, "[443, 80]") && Has(fixIt, "PortConflictInspector.Inspect"),
            "fix-it cards name the program holding the web ports"
        );

        var general = Read("Pages/GeneralPage.xaml");
        Check(
            Has(general, "x:Name=\"LanguageBox\"") && Has(general, "x:Name=\"LanguageTip\""),
            "General has the language setting and its tip"
        );
        Check(Has(Read("MainWindow.Appearance.cs"), "ReduceMotion"), "reduce motion is honoured by the shell");

        Check(
            Has(Read("Pages/SitesPage.Manifest.cs"), "UnsavedChangesGuard.Attach"),
            "the manifest editor asks before discarding changes"
        );
        Check(Has(Read("Views/DangerStyles.cs"), "ContentDialogButton.Close"), "destructive dialogs default to Cancel");
        foreach (var page in new[]
        {
            "Pages/MailPage.xaml", "Pages/DumpsPage.xaml", "Pages/DebuggerPage.xaml", "Pages/NodePage.xaml", "Pages/TinkerPage.xaml"
        })
        {
            Check(Has(Read(page), "Danger"), page + " uses the danger style for destructive buttons");
        }

        var changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        Check(
            Has(changelog, "## [Unreleased]") && ChangelogReader.Section(changelog, "0.1.23").Count > 0,
            "CHANGELOG.md has sections What's new can read"
        );
        Check(Has(Read("HerdMe.Windows.csproj"), @"..\..\CHANGELOG.md"), "CHANGELOG.md is copied next to the app");
        foreach (var script in new[] { "build.ps1", "package-installer.ps1", "package-portable.ps1" })
        {
            Check(
                Has(File.ReadAllText(Path.Combine(repositoryRoot, "Windows", script)), "\"CHANGELOG.md\""),
                script + " ships CHANGELOG.md"
            );
        }
        var whatsNew = Read("MainWindow.WhatsNew.cs");
        Check(Has(whatsNew, "RequiresOnboarding"), "What's new never covers onboarding");
        Check(Has(whatsNew, "Load(version).Count == 0"), "What's new opens by itself only when the version has notes");

        var mail = Read("Pages/MailPage.xaml");
        Check(
            Has(mail, "x:Uid=\"MailHeadersTab\"") && Has(mail, "x:Name=\"LinksTab\"") && Has(mail, "x:Name=\"MobileWidthToggle\""),
            "Mail has Headers and Links tabs and a phone width preview"
        );
        Check(!Has(Read("Services/MailInspection.cs"), "HttpClient"), "the mail link check never fetches links");
        Check(Has(Read("Pages/DumpsPage.xaml"), "x:Name=\"DumpTreeView\""), "dumps have a tree view");
        var logs = Read("Pages/LogsPage.xaml");
        Check(
            Has(logs, "x:Name=\"FollowTailToggle\"") && Has(logs, "x:Name=\"NextErrorButton\""),
            "logs can follow the tail and jump to errors"
        );
        var console = Read("Pages/SitesPage.CommandConsole.cs");
        Check(
            Has(console, "SitesConsoleStop") && Has(console, "SitesConsoleRunAgain"),
            "the command console offers Stop and Run again"
        );
        Check(Has(Read("Pages/SitesPage.Commands.cs"), "AnsiParser.WithArtisanColors"), "Artisan output is coloured");
        Check(
            Has(Read("Pages/SitesPage.xaml"), "AutomationProperties.AutomationId=\"SitesRowPhpPicker\"")
                && Has(Read("Pages/SitesPage.PhpPicker.cs"), "siteRuntimeStore.SetPhp"),
            "each site row has an inline PHP picker that uses the runtime store"
        );
    }
}
