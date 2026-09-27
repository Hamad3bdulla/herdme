using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using System.Runtime.Versioning;

// Round 4 (front end): the Sites list query, the tray and taskbar presentation, and the
// source-level promises of the new screens (General search, onboarding steps, Mail setup).
internal static partial class ContractChecks
{
    internal static void VerifyUxContracts(string repositoryRoot)
    {
        VerifySiteListFilter();
        VerifyEditorFolderArguments();
        VerifyTrayPresentation();
        VerifyTaskbarOverlayChoice();
        VerifyUxSources(Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows"));
    }

    private static SiteRecord UxSite(
        string domain,
        string framework = "Laravel",
        bool favorite = false,
        bool running = false,
        bool shared = false,
        string? error = null
    ) => new()
    {
        Name = domain.Split('.')[0],
        Path = @"C:\Sites\" + domain.Split('.')[0],
        Domain = domain,
        Framework = framework,
        IsFavorite = favorite,
        IsRunning = running,
        IsShared = shared,
        LastError = error
    };

    private static void VerifySiteListFilter()
    {
        var sites = new[]
        {
            UxSite("zeta.test", favorite: true),
            UxSite("alpha.test", framework: "Static", running: true),
            UxSite("mid.test", framework: "Symfony", shared: true, error: "boom"),
            UxSite("beta.test")
        };
        Check(
            SiteListFilter.Apply(sites, null, SiteListFilterKind.All, SiteListSortKind.FavoritesFirst).Count == 4,
            "the Sites list shows every site with no search or filter"
        );
        var favoritesFirst = SiteListFilter.Apply(sites, null, SiteListFilterKind.All, SiteListSortKind.FavoritesFirst);
        Check(
            favoritesFirst[0].Domain == "zeta.test" && favoritesFirst[1].Domain == "alpha.test",
            "favorites float to the top and scan order is kept otherwise"
        );
        var byName = SiteListFilter.Apply(sites, null, SiteListFilterKind.All, SiteListSortKind.Name);
        Check(
            byName.Select(site => site.Domain).SequenceEqual(["alpha.test", "beta.test", "mid.test", "zeta.test"]),
            "sorting by name orders sites by domain"
        );
        var byFramework = SiteListFilter.Apply(sites, null, SiteListFilterKind.All, SiteListSortKind.Framework);
        Check(
            byFramework.Select(site => site.Domain).SequenceEqual(["beta.test", "zeta.test", "alpha.test", "mid.test"]),
            "sorting by framework groups sites and orders each group by domain"
        );
        Check(
            SiteListFilter.Apply(sites, null, SiteListFilterKind.Favorites, SiteListSortKind.Name).Single().Domain == "zeta.test"
                && SiteListFilter.Apply(sites, null, SiteListFilterKind.Laravel, SiteListSortKind.Name).Count == 2
                && SiteListFilter.Apply(sites, null, SiteListFilterKind.Running, SiteListSortKind.Name).Single().Domain == "alpha.test"
                && SiteListFilter.Apply(sites, null, SiteListFilterKind.Errors, SiteListSortKind.Name).Single().Domain == "mid.test"
                && SiteListFilter.Apply(sites, null, SiteListFilterKind.Shared, SiteListSortKind.Name).Single().Domain == "mid.test",
            "each quick filter keeps only the matching sites"
        );
        Check(
            SiteListFilter.Apply(sites, "bet", SiteListFilterKind.Laravel, SiteListSortKind.Name).Single().Domain == "beta.test"
                && SiteListFilter.Apply(sites, "bet", SiteListFilterKind.Running, SiteListSortKind.Name).Count == 0,
            "search and the quick filter narrow the list together"
        );
        Check(
            SiteListFilter.ParseFilter("Shared") == SiteListFilterKind.Shared
                && SiteListFilter.ParseFilter("nonsense") == SiteListFilterKind.All
                && SiteListFilter.ParseFilter(null) == SiteListFilterKind.All
                && SiteListFilter.ParseSort("Framework") == SiteListSortKind.Framework
                && SiteListFilter.ParseSort("") == SiteListSortKind.FavoritesFirst,
            "saved filter and sort choices fall back to the defaults when unknown"
        );
    }

    private static void VerifyEditorFolderArguments()
    {
        var arguments = EditorLauncher.VisualStudioCodeFolderArguments(@"C:\Sites\blog");
        Check(
            arguments.Contains(@"C:\Sites\blog", StringComparer.Ordinal)
                && !arguments.Any(argument => argument.StartsWith("--goto", StringComparison.Ordinal)),
            "Open in VS Code passes the project folder as its own argument"
        );
    }

    private static void VerifyTrayPresentation()
    {
        Check(
            TrayPresentation.Choose(running: true, degraded: false) == TrayIconState.Running
                && TrayPresentation.Choose(running: true, degraded: true) == TrayIconState.Running
                && TrayPresentation.Choose(running: false, degraded: true) == TrayIconState.Recovering
                && TrayPresentation.Choose(running: false, degraded: false) == TrayIconState.Stopped,
            "the tray icon follows running, recovering and stopped"
        );
        Check(
            TrayPresentation.IconUri(TrayIconState.Running) == "ms-appx:///Assets/HerdMe.ico"
                && TrayPresentation.IconUri(TrayIconState.Recovering) == "ms-appx:///Assets/HerdMe-Degraded.ico"
                && TrayPresentation.IconUri(TrayIconState.Stopped) == "ms-appx:///Assets/HerdMe-Stopped.ico",
            "each tray state uses a packaged icon"
        );
        var many = Enumerable.Range(0, 15)
            .Select(index => UxSite($"site{index:00}.test", favorite: index == 14))
            .Reverse()
            .ToList();
        var menu = TrayPresentation.MenuSites(many);
        Check(
            menu.Count == TrayPresentation.SiteMenuLimit
                && menu[0].Domain == "site14.test"
                && menu[1].Domain == "site00.test"
                && menu[^1].Domain == "site08.test",
            "the tray Sites menu lists favorites first, then by domain, and stays short"
        );
        Check(
            TrayPresentation.MenuText("R&D.test") == "R&&D.test",
            "tray menu text keeps a literal ampersand"
        );
    }

    private static void VerifyTaskbarOverlayChoice()
    {
        Check(
            TaskbarOverlay.Choose(0, 0, 0) == TaskbarOverlayKind.None
                && TaskbarOverlay.Choose(2, 0, 0) == TaskbarOverlayKind.Activity
                && TaskbarOverlay.Choose(0, 1, 0) == TaskbarOverlayKind.Activity
                && TaskbarOverlay.Choose(5, 5, 1) == TaskbarOverlayKind.Error,
            "the taskbar badge shows unexpected stops before new mail or dumps"
        );
        Check(
            TaskbarOverlay.AssetName(TaskbarOverlayKind.None) is null
                && TaskbarOverlay.AssetName(TaskbarOverlayKind.Activity) == "Overlay-Activity.ico"
                && TaskbarOverlay.AssetName(TaskbarOverlayKind.Error) == "Overlay-Error.ico",
            "the taskbar badge uses packaged icons and clears when nothing is new"
        );
    }

    private static void VerifyUxSources(string projectRoot)
    {
        var project = File.ReadAllText(Path.Combine(projectRoot, "HerdMe.Windows.csproj"));
        var build = File.ReadAllText(Path.Combine(projectRoot, "..", "build.ps1"));
        var installer = File.ReadAllText(Path.Combine(projectRoot, "..", "package-installer.ps1"));
        var portable = File.ReadAllText(Path.Combine(projectRoot, "..", "package-portable.ps1"));
        foreach (var asset in new[] { "HerdMe-Degraded.ico", "HerdMe-Stopped.ico", "Overlay-Activity.ico", "Overlay-Error.ico" })
        {
            Check(
                File.Exists(Path.Combine(projectRoot, "Assets", asset))
                    && project.Contains(@"Assets\" + asset, StringComparison.Ordinal)
                    && build.Contains(@"Assets\" + asset, StringComparison.Ordinal)
                    && installer.Contains(@"Assets\" + asset, StringComparison.Ordinal)
                    && portable.Contains(@"Assets\" + asset, StringComparison.Ordinal),
                $"{asset} ships with every build and package"
            );
        }

        var tray = File.ReadAllText(Path.Combine(projectRoot, "App.Tray.cs"));
        Check(
            !tray.Contains("GeneratedIconSource", StringComparison.Ordinal)
                && !tray.Contains("CommandParameter", StringComparison.Ordinal)
                && tray.Contains("services.Shares.StopAllAsync()", StringComparison.Ordinal)
                && tray.Contains("UseShellExecute = true", StringComparison.Ordinal),
            "the tray menu uses packaged icons, per-item commands, and an explicit Stop sharing"
        );

        var generalXaml = File.ReadAllText(Path.Combine(projectRoot, "Pages", "GeneralPage.xaml"));
        foreach (var name in new[]
        {
            "TldTextBox", "NotificationsToggle", "ExportDiagnosticsButton", "IncludeCrashDumpsCheckBox",
            "ExplorerLinkToggle", "UriProtocolToggle", "TerminalProfileToggle", "SettingsSearchBox"
        })
        {
            Check(
                generalXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal),
                $"the General page keeps {name} inside its collapsible sections"
            );
        }
        Check(
            generalXaml.Contains("GeneralPageRoot", StringComparison.Ordinal)
                && generalXaml.Contains("Style=\"{StaticResource GeneralSectionExpanderStyle}\"", StringComparison.Ordinal),
            "the General page groups settings in expanders and keeps its automation root"
        );

        var onboardingXaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "OnboardingView.xaml"));
        var onboardingSource = File.ReadAllText(Path.Combine(projectRoot, "Views", "OnboardingView.xaml.cs"));
        var welcomeStart = onboardingXaml.IndexOf("x:Name=\"WelcomePanel\"", StringComparison.Ordinal);
        var explainStart = onboardingXaml.IndexOf("x:Name=\"ExplainPanel\"", StringComparison.Ordinal);
        var startButton = onboardingXaml.IndexOf("\"OnboardingStartButton\"", StringComparison.Ordinal);
        Check(
            welcomeStart >= 0 && explainStart > welcomeStart
                && startButton > welcomeStart && startButton < explainStart,
            "the first onboarding screen keeps its Set up button; the explanation step is optional"
        );
        Check(
            onboardingXaml.Contains("Click=\"CreateLaravel_Click\"", StringComparison.Ordinal)
                && onboardingXaml.Contains("Click=\"ParkFolder_Click\"", StringComparison.Ordinal)
                && onboardingSource.Contains("OnboardingNextStep.CreateLaravel", StringComparison.Ordinal)
                && onboardingSource.Contains("OnboardingNextStep.ParkFolder", StringComparison.Ordinal),
            "onboarding ends with Create Laravel and Park a folder"
        );

        var mailXaml = File.ReadAllText(Path.Combine(projectRoot, "Pages", "MailPage.xaml"));
        var mailSource = File.ReadAllText(Path.Combine(projectRoot, "Pages", "MailPage.xaml.cs"));
        Check(
            mailXaml.Contains("x:Name=\"SmtpSnippetText\"", StringComparison.Ordinal)
                && mailXaml.Contains("IsTextSelectionEnabled=\"True\"", StringComparison.Ordinal)
                && mailSource.Contains("MailEnvironmentConfiguration.Variables(", StringComparison.Ordinal),
            "an empty Mail inbox shows the same SMTP settings that Add to .env writes"
        );
        var smtp = MailEnvironmentConfiguration.Variables(2525);
        Check(
            smtp.Any(variable => variable.Key == "MAIL_HOST" && variable.Value == "127.0.0.1")
                && smtp.Any(variable => variable.Key == "MAIL_PORT" && variable.Value == "2525"),
            "the SMTP settings point at loopback"
        );
    }
}
