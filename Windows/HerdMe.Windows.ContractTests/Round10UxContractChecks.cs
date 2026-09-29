using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 10: Services page grouped list + details pane (documentation, environment, log),
// "Copy mail settings" on the Mail page and the opt-in "Speed up PHP" Defender exclusion.
internal static partial class ContractChecks
{
    internal static void VerifyRound10UxContracts(string repositoryRoot, string supportRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));
        var root = Path.Combine(supportRoot, "round10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        VerifyServiceDirectory();
        VerifyServiceLogDetail(root);
        VerifyMailSettingsText(Read("Pages", "MailPage.CopySettings.cs"));
        VerifyDefenderExclusion(root);

        var services = Read("Pages", "ServicesPage.xaml");
        Check(
            services.Contains("IsSourceGrouped=\"True\"", StringComparison.Ordinal)
                && services.Contains("<GroupStyle", StringComparison.Ordinal),
            "the Services list is grouped by kind"
        );
        Check(
            services.Contains("AutomationProperties.AutomationId=\"ServiceDetails\"", StringComparison.Ordinal)
                && services.Contains("Click=\"OpenDocumentation_Click\"", StringComparison.Ordinal)
                && services.Contains("Click=\"OpenLog_Click\"", StringComparison.Ordinal)
                && services.Contains("x:Name=\"DetailEnvironmentText\"", StringComparison.Ordinal),
            "the Services page has a details pane with documentation, environment and log"
        );
        Check(
            services.Contains("x:Name=\"AddServiceToggleButton\"", StringComparison.Ordinal)
                && services.Contains("x:Name=\"AddServiceCard\"", StringComparison.Ordinal),
            "Add Service is a header button that opens the add form"
        );
        var details = Read("Pages", "ServicesPage.Details.cs");
        Check(
            details.Contains("ServiceDirectory.MaskedEnvironment(", StringComparison.Ordinal)
                && details.Contains("ApplyDetailLayout(", StringComparison.Ordinal),
            "the details pane masks secrets and stacks under the list on narrow windows"
        );
        var cards = Read("Pages", "ServicesPage.Cards.cs");
        Check(
            cards.Contains("ServiceLogTail.LastLines(", StringComparison.Ordinal)
                && cards.Contains("Task.Run", StringComparison.Ordinal),
            "the selected service log is read off the UI thread"
        );

        var mail = Read("Pages", "MailPage.xaml");
        Check(
            mail.Contains("AutomationProperties.AutomationId=\"CopyMailSettingsButton\"", StringComparison.Ordinal)
                && mail.Contains("Click=\"CopyMailSettings_Click\"", StringComparison.Ordinal),
            "the Mail header has Copy mail settings"
        );

        var general = Read("Pages", "GeneralPage.xaml");
        Check(
            general.Contains("x:Name=\"DefenderExclusionToggle\"", StringComparison.Ordinal)
                && general.Contains("Toggled=\"DefenderExclusionToggle_Toggled\"", StringComparison.Ordinal),
            "General has the opt-in Speed up PHP switch"
        );
        var defenderPage = Read("Pages", "GeneralPage.Defender.cs");
        Check(
            defenderPage.Contains("ConfirmDefenderExclusionAsync", StringComparison.Ordinal)
                && defenderPage.Contains("DefaultButton = ContentDialogButton.Close", StringComparison.Ordinal),
            "turning on Speed up PHP asks first and Cancel is the default"
        );
        var appSource = Read("App.xaml.cs");
        var helper = appSource.IndexOf("DefenderExclusion.TryRunElevatedHelper", StringComparison.Ordinal);
        Check(
            helper >= 0 && helper < appSource.IndexOf("new SingleInstanceCoordinator", StringComparison.Ordinal),
            "the Defender helper runs before the single-instance check"
        );
        var installer = File.ReadAllText(Path.Combine(repositoryRoot, "Windows", "installer.iss"));
        Check(
            installer.Contains("--remove-defender-exclusion", StringComparison.Ordinal),
            "uninstalling removes the Defender exclusion HerdMe added"
        );
        Check(
            new WindowsSiteSettings().DefenderExclusionPath.Length == 0,
            "Speed up PHP is off by default"
        );
    }

    private static void VerifyServiceDirectory()
    {
        Check(
            ServiceDirectory.GroupOf("MySQL") == ServiceGroupKind.Database
                && ServiceDirectory.GroupOf("postgresql") == ServiceGroupKind.Database
                && ServiceDirectory.GroupOf("redis") == ServiceGroupKind.CacheAndQueue
                && ServiceDirectory.GroupOf("meilisearch") == ServiceGroupKind.Search
                && ServiceDirectory.GroupOf("minio") == ServiceGroupKind.Storage
                && ServiceDirectory.GroupOf("something-new") == ServiceGroupKind.Other,
            "services are grouped by kind"
        );
        foreach (var id in new[] { "mysql", "mariadb", "postgresql", "mongodb", "redis", "valkey", "meilisearch", "typesense", "minio", "rustfs" })
        {
            Check(
                ServiceDirectory.DocumentationUri(id) is { Scheme: "https" },
                $"{id} documentation opens an official https page"
            );
        }
        Check(ServiceDirectory.DocumentationUri("unknown") is null, "unknown services have no documentation link");
        foreach (var kind in Enum.GetValues<ServiceGroupKind>())
        {
            Check(
                ServiceDirectory.GroupTitleKey(kind).StartsWith("ServicesGroup", StringComparison.Ordinal)
                    && ServiceDirectory.GroupTitleFallback(kind).Length > 0,
                $"the {kind} section has a title"
            );
        }

        var grouped = ServiceDirectory.Group(
            new[] { ("redis", "Redis"), ("postgresql", "b-postgres"), ("mysql", "A-mysql"), ("minio", "MinIO") },
            item => item.Item1,
            item => item.Item2
        );
        Check(
            grouped.Select(group => group.Kind).SequenceEqual([ServiceGroupKind.Database, ServiceGroupKind.CacheAndQueue, ServiceGroupKind.Storage])
                && grouped[0].Items.Select(item => item.Item2).SequenceEqual(["A-mysql", "b-postgres"]),
            "sections keep a fixed order and services are sorted by name inside them"
        );

        var masked = ServiceDirectory.MaskedEnvironment(
        [
            new ServiceEnvironmentVariable("DB_HOST", "127.0.0.1"),
            new ServiceEnvironmentVariable("DB_PASSWORD", "hunter2-secret")
        ]);
        Check(
            masked.Contains("DB_HOST=127.0.0.1", StringComparison.Ordinal)
                && !masked.Contains("hunter2-secret", StringComparison.Ordinal)
                && masked.Contains("DB_PASSWORD=" + EnvironmentEditorModel.MaskText + "\n", StringComparison.Ordinal)
                && !masked.Contains('\r'),
            "the details environment block masks secrets"
        );
    }

    private static void VerifyServiceLogDetail(string root)
    {
        var text = string.Join('\n', Enumerable.Range(1, 60).Select(index => "line " + index)) + "\n\n";
        var last = ServiceLogTail.LinesFromText(text, 40).Split('\n');
        Check(
            last.Length == 40 && last[0] == "line 21" && last[^1] == "line 60",
            "the details log shows the last 40 non-empty lines"
        );
        Check(
            ServiceLogTail.LinesFromText(new string('x', 5000), 5).Length == ServiceLogTail.MaximumDetailLineLength,
            "very long log lines are cut"
        );
        Check(ServiceLogTail.LinesFromText("a\nb", 0).Length == 0, "zero lines asks for nothing");

        var path = Path.Combine(root, "service.log");
        File.WriteAllText(path, new string('z', 10_000) + "\nfirst\nsecond\n", new UTF8Encoding(false));
        var tail = ServiceLogTail.LastLines(path, 10, 256);
        Check(
            tail == "first\nsecond",
            "reading only the end of a big log drops the partial first line"
        );
        Check(ServiceLogTail.LastLines(Path.Combine(root, "missing.log")).Length == 0, "a missing log is empty");
    }

    private static void VerifyMailSettingsText(string page)
    {
        var text = ServiceEnvironmentFile.FormatLines(MailEnvironmentConfiguration.Variables(2525));
        Check(
            text.Contains("MAIL_MAILER=smtp", StringComparison.Ordinal)
                && text.Contains("MAIL_HOST=127.0.0.1", StringComparison.Ordinal)
                && text.Contains("MAIL_PORT=2525", StringComparison.Ordinal),
            "Copy mail settings gives the same MAIL_* lines as Add to .env"
        );
        Check(
            page.Contains("MailEnvironmentConfiguration.Variables(port)", StringComparison.Ordinal),
            "the copied mail settings come from the shared MAIL_* definition"
        );
    }

    private static void VerifyDefenderExclusion(string root)
    {
        var profile = Path.Combine(root, "Users", "dev");
        var allowed = Path.Combine(profile, "AppData", "Local", "HerdMe");
        Directory.CreateDirectory(allowed);
        Check(DefenderExclusion.IsAllowedExclusionPath(allowed), "the HerdMe folder in local app data can be excluded");
        Check(
            DefenderExclusion.IsAllowedExclusionPath(allowed + Path.DirectorySeparatorChar),
            "a trailing separator is accepted"
        );
        foreach (var refused in new[]
        {
            Path.Combine(profile, "AppData", "Local"),
            Path.Combine(profile, "AppData", "Local", "HerdMe", "bin"),
            Path.Combine(profile, "AppData", "Roaming", "HerdMe"),
            Path.Combine(profile, "Downloads", "HerdMe"),
            Path.Combine(profile, "AppData", "Local", "HerdMe", "..", "..", "Local", "HerdMe"),
            Path.Combine(profile, "AppData", "Local", "Herd'Me"),
            "HerdMe",
            string.Empty
        })
        {
            Check(!DefenderExclusion.IsAllowedExclusionPath(refused), $"only the HerdMe folder can be excluded ({refused})");
        }
        Check(!DefenderExclusion.IsAllowedExclusionPath(null), "no path cannot be excluded");

        Check(DefenderExclusion.QuotePowerShell("a'b") == "'a''b'", "PowerShell literals double single quotes");
        var add = DefenderExclusion.BuildScript(allowed, add: true);
        var remove = DefenderExclusion.BuildScript(allowed + Path.DirectorySeparatorChar, add: false);
        Check(
            add.StartsWith("$ErrorActionPreference='Stop'; Add-MpPreference -ExclusionPath '", StringComparison.Ordinal)
                && add.EndsWith("HerdMe'", StringComparison.Ordinal)
                && remove.Contains("Remove-MpPreference -ExclusionPath '", StringComparison.Ordinal)
                && remove.EndsWith("HerdMe'", StringComparison.Ordinal),
            "the Defender script only adds or removes the one folder"
        );
        Check(
            !add.Contains("ExclusionProcess", StringComparison.Ordinal) && !add.Contains("ExclusionExtension", StringComparison.Ordinal),
            "no process or extension exclusions are added"
        );
        Throws<ArgumentException>(
            () => DefenderExclusion.BuildScript(Path.Combine(profile, "Documents"), add: true),
            "other folders are refused before any script is built"
        );
        Check(
            Encoding.Unicode.GetString(Convert.FromBase64String(DefenderExclusion.EncodeCommand(add))) == add,
            "the script is passed as UTF-16LE base64 so no quoting can change it"
        );

        Check(!DefenderExclusion.TryRunElevatedHelper(["HerdMe.Windows.exe"], out _), "plain launches are not the Defender helper");
        Check(
            DefenderExclusion.TryRunElevatedHelper(["HerdMe.Windows.exe", DefenderExclusion.HelperArgument, "add", Path.Combine(profile, "Documents")], out var badPath)
                && badPath == DefenderExclusion.ExitBadRequest,
            "the helper refuses any other folder"
        );
        Check(
            DefenderExclusion.TryRunElevatedHelper(["HerdMe.Windows.exe", DefenderExclusion.HelperArgument, "wipe", allowed], out var badVerb)
                && badVerb == DefenderExclusion.ExitBadRequest,
            "the helper only knows add and remove"
        );
        Check(
            !DefenderExclusion.TryRunUninstallCleanup(["HerdMe.Windows.exe", "--something-else"]),
            "uninstall cleanup only runs for its own argument"
        );

        var store = new SiteConfigurationStore(Path.Combine(root, "defender-settings"));
        store.UpdateDefenderExclusion(Path.Combine(profile, "Documents"));
        Check(store.Load().DefenderExclusionPath.Length == 0, "the setting never records another folder");
        store.UpdateDefenderExclusion(allowed + Path.DirectorySeparatorChar);
        Check(store.Load().DefenderExclusionPath == allowed, "the setting records the excluded HerdMe folder");
        store.UpdateDefenderExclusion(string.Empty);
        Check(store.Load().DefenderExclusionPath.Length == 0, "turning Speed up PHP off clears the setting");
    }
}
