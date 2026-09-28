using System.Text.RegularExpressions;
using System.Xml.Linq;

// Issues reported after 0.1.28: toggle buttons given a Button style, the update check
// warning without a reason, and Tinker moved from its own page into the Sites details.
internal static partial class ContractChecks
{
    internal static void VerifyReportedIssueContracts(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        VerifyStyleTargets(app);

        var components = Read("Services", "ManagedComponentUpdateManager.cs");
        Check(
            components.Contains("catch (XdebugBuildUnavailableException)", StringComparison.Ordinal),
            "a missing Xdebug build for this PHP line is not reported as a failed update check"
        );
        var updates = Read("Pages", "UpdatesPage.xaml.cs");
        Check(
            updates.Contains("FailureReason(", StringComparison.Ordinal)
                && updates.Contains("UpdatesFailureDetail", StringComparison.Ordinal)
                && updates.Contains("UpdatesFeedNotPublished", StringComparison.Ordinal),
            "the Updates page says why each check could not complete"
        );

        var mainWindow = XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        Check(
            !mainWindow.Descendants().Any(element => element.Name.LocalName == "NavigationViewItem"
                && element.Attribute("Tag")?.Value == "tinker"),
            "Tinker is not a separate navigation page"
        );
        var mainWindowCode = Read("MainWindow.xaml.cs");
        Check(
            mainWindowCode.Contains("public void NavigateToTinker(string? sitePath)", StringComparison.Ordinal)
                && mainWindowCode.Contains("RequestTinker(sitePath)", StringComparison.Ordinal),
            "the tinker route opens the Tinker tab in Sites"
        );
        var sites = Read("Pages", "SitesPage.xaml");
        Check(
            sites.Contains("AutomationProperties.AutomationId=\"SitesTinkerTab\"", StringComparison.Ordinal)
                && sites.Contains("x:Name=\"TinkerPanel\"", StringComparison.Ordinal)
                && sites.Contains("Click=\"ContextOpenTinker_Click\"", StringComparison.Ordinal),
            "Sites has a Tinker tab and an Open in Tinker row action"
        );
        var sitesTinker = Read("Pages", "SitesPage.Tinker.cs");
        Check(
            sitesTinker.Contains("embedded: true", StringComparison.Ordinal)
                && Read("Pages", "SitesPage.xaml.cs").Contains("ShowTinkerFor(site);", StringComparison.Ordinal),
            "the Tinker tab reuses the Tinker page and follows the selected site"
        );
        Check(
            Read("App.Commands.cs").Contains("\"tinker\" => await ShowTinkerAsync(argument", StringComparison.Ordinal),
            "herdme tinker <site> opens Tinker for that site"
        );
        var acceptance = File.ReadAllText(Path.Combine(repositoryRoot, "Windows", "acceptance.ps1"));
        Check(
            acceptance.Contains("Assert-SitesTinkerTab $window $navigation", StringComparison.Ordinal)
                && acceptance.Contains("\"SitesTinkerTab\"", StringComparison.Ordinal),
            "native Windows acceptance opens Tinker from the Sites details"
        );
    }

    // A Style whose TargetType is Button throws at runtime on a ToggleButton (and the reverse).
    private static void VerifyStyleTargets(string app)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var files = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var style in XDocument.Load(file).Descendants().Where(element => element.Name.LocalName == "Style"))
            {
                var key = style.Attribute(xaml + "Key")?.Value;
                var target = style.Attribute("TargetType")?.Value;
                if (key is not null && target is not null) targets[key] = target.Split(':')[^1];
            }
        }
        var reference = new Regex(@"^\{StaticResource\s+([A-Za-z0-9_]+)\s*\}$");
        foreach (var file in files)
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                var control = element.Name.LocalName;
                if (control is not ("Button" or "ToggleButton")) continue;
                var match = reference.Match(element.Attribute("Style")?.Value ?? string.Empty);
                if (!match.Success || !targets.TryGetValue(match.Groups[1].Value, out var target)) continue;
                Check(
                    target == control,
                    $"{Path.GetFileName(file)}: {control} uses {match.Groups[1].Value} (TargetType {target})"
                );
            }
        }
    }
}
