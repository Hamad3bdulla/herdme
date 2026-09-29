using System.Text.RegularExpressions;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 11: a calmer, Herd-like look. Flat surfaces, one accent colour, a lighter shell
// (search behind an icon / Ctrl+K, status bar off by default), a calm Dashboard home with
// active services and the global PHP picker, and a Sites detail with General/Information rows.
internal static partial class ContractChecks
{
    internal static void VerifyRound11UxContracts(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        var design = Read("Styles", "DesignSystem.xaml");
        var titleStyle = design.IndexOf("x:Key=\"PageTitleStyle\"", StringComparison.Ordinal);
        Check(
            titleStyle >= 0
                && design.IndexOf("<Setter Property=\"FontSize\" Value=\"22\" />", titleStyle, StringComparison.Ordinal) is var size
                && size > titleStyle
                && size < design.IndexOf("</Style>", titleStyle, StringComparison.Ordinal),
            "page titles are 22 px"
        );
        foreach (var key in new[] { "DetailRowStyle", "DetailRowAltStyle", "FormGridStyle", "FormLabelStyle", "RowAlternateBrush" })
        {
            Check(design.Contains("x:Key=\"" + key + "\"", StringComparison.Ordinal), $"the design system defines {key}");
        }

        var shell = Read("MainWindow.xaml");
        Check(
            !shell.Contains("NavigationViewItemHeader", StringComparison.Ordinal),
            "the navigation is one quiet list without group headers"
        );
        Check(
            shell.Contains("x:Name=\"TitleBarSearchButton\"", StringComparison.Ordinal)
                && shell.Contains("Click=\"TitleBarSearchButton_Click\"", StringComparison.Ordinal),
            "search sits behind a title bar icon"
        );
        var titleBar = Read("MainWindow.TitleBar.cs");
        Check(
            titleBar.Contains("OpenTitleBarSearch(", StringComparison.Ordinal)
                && titleBar.Contains("TitleBarSearchButton", StringComparison.Ordinal),
            "Ctrl+K and the search icon open the title bar search"
        );
        Check(!new WindowsSiteSettings().ShowStatusBar, "the status bar is hidden by default");
        Check(
            Read("Pages", "GeneralPage.xaml").Contains("AutomationProperties.AutomationId=\"GeneralShowStatusBar\"", StringComparison.Ordinal)
                && Read("MainWindow.StatusBar.cs").Contains("ApplyStatusBarPreference", StringComparison.Ordinal),
            "General can turn the status bar back on"
        );

        var dashboard = Read("Pages", "DashboardPage.xaml");
        Check(
            dashboard.Contains("x:Name=\"ActivityTab\"", StringComparison.Ordinal)
                && dashboard.Contains("AutomationProperties.AutomationId=\"DashboardActivityPanel\"", StringComparison.Ordinal),
            "counts, environment and recent items live on the Activity tab"
        );
        Check(
            dashboard.Contains("AutomationProperties.AutomationId=\"DashboardActiveServices\"", StringComparison.Ordinal)
                && dashboard.Contains("AutomationProperties.AutomationId=\"DashboardGlobalPhp\"", StringComparison.Ordinal)
                && dashboard.Contains("AutomationProperties.AutomationId=\"DashboardOpenMail\"", StringComparison.Ordinal),
            "the Dashboard home shows active services, Open buttons and the global PHP picker"
        );
        var home = Read("Pages", "DashboardPage.Home.cs");
        Check(
            home.Contains("SwitchDefaultPhp(", StringComparison.Ordinal)
                && home.Contains("Task.Run(phpInstaller.InstalledCycles", StringComparison.Ordinal),
            "the global PHP picker reads installed versions off the UI thread and switches through the app"
        );
        Check(
            Read("App.Tray.cs").Contains("internal void SwitchDefaultPhp(string cycle)", StringComparison.Ordinal),
            "the tray and the Dashboard share one PHP switch"
        );

        var sites = Read("Pages", "SitesPage.xaml");
        Check(
            sites.Contains("x:Name=\"SiteHeaderLock\"", StringComparison.Ordinal)
                && sites.Contains("AutomationProperties.AutomationId=\"SitesGeneralRows\"", StringComparison.Ordinal),
            "the site detail has a lock in the header and General key/value rows"
        );
        Check(
            sites.Contains("AutomationProperties.AutomationId=\"SitesMoreActions\"", StringComparison.Ordinal)
                && sites.Contains("OverflowButtonVisibility=\"Collapsed\"", StringComparison.Ordinal),
            "less common site actions live in one More menu"
        );

        var services = Read("Pages", "ServicesPage.xaml");
        Check(
            services.Contains("Style=\"{StaticResource FormGridStyle}\"", StringComparison.Ordinal)
                && services.Contains("x:Uid=\"ServicesFormServiceLabel\"", StringComparison.Ordinal)
                && services.Contains("Click=\"CancelAddService_Click\"", StringComparison.Ordinal),
            "Add Service is a label/field form with Cancel and Add"
        );

        VerifyGeneralSettingsWiring(app, Read);
    }

    // Every control on General is wired to a handler that exists, each setting that can apply
    // live does, and the domain suffix refuses bad values instead of falling back to "test".
    private static void VerifyGeneralSettingsWiring(string app, Func<string[], string> read)
    {
        var xaml = read(["Pages", "GeneralPage.xaml"]);
        var code = string.Concat(Directory.GetFiles(Path.Combine(app, "Pages"), "GeneralPage*.cs").Select(File.ReadAllText));
        var handlers = Regex.Matches(xaml, "\\b(?:Toggled|Click|SelectionChanged|LostFocus|KeyDown|TextChanged|Loaded|Unloaded)=\"([A-Za-z_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Check(handlers.Length >= 20, "General declares its event handlers in XAML");
        foreach (var handler in handlers)
        {
            Check(
                Regex.IsMatch(code, "\\bvoid " + Regex.Escape(handler) + "\\("),
                $"General handler {handler} exists"
            );
        }
        foreach (var toggle in Regex.Matches(xaml, "<ToggleSwitch x:Name=\"([A-Za-z]+)\"").Select(match => match.Groups[1].Value))
        {
            Check(
                code.Contains(toggle + ".IsOn =", StringComparison.Ordinal),
                $"General loads the saved state of {toggle}"
            );
        }

        Check(
            code.Contains("App.MainWindow.ApplyCompactPreference(", StringComparison.Ordinal)
                && code.Contains("App.MainWindow.ApplyMotionPreference(", StringComparison.Ordinal)
                && code.Contains("App.MainWindow.ApplyStatusBarPreference(", StringComparison.Ordinal),
            "compact, motion and status bar switches apply to the open window"
        );
        Check(
            code.Contains("SiteConfigurationStore.IsValidTld(", StringComparison.Ordinal)
                && code.Contains("ApplyDomainSuffixChange()", StringComparison.Ordinal)
                && xaml.Contains("KeyDown=\"TldTextBox_KeyDown\"", StringComparison.Ordinal),
            "a new domain suffix is validated, applies on Enter and moves the running sites over"
        );
        foreach (var good in new[] { "test", "local", "dev-test", "x1" })
        {
            Check(SiteConfigurationStore.IsValidTld(good), $"{good} is a valid domain suffix");
        }
        foreach (var bad in new[] { string.Empty, "my site", "-test", "test-", "a.b", "t\u00e9st", new string('a', 64) })
        {
            Check(!SiteConfigurationStore.IsValidTld(bad), $"'{bad}' is not a valid domain suffix");
        }
        Check(SiteConfigurationStore.NormalizeTld(" .Local. ") == "local", "the domain suffix is trimmed and lower-cased");
    }
}
