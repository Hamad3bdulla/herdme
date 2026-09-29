using System.IO.Compression;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyNotificationAndDiagnosticsContractsAsync(string repositoryRoot, string supportRoot)
    {
        VerifyNotificationText();
        VerifyNotificationThrottle();
        VerifyNotificationSetting(supportRoot);
        VerifyDiagnosticsRedaction();
        VerifyCrashReporter(supportRoot);
        await VerifyDiagnosticsExportAsync(supportRoot);
        VerifyNotificationSources(repositoryRoot);
    }

    private static void VerifyNotificationText()
    {
        var service = AppNotifications.ServiceStopped("MariaDB", 3);
        Check(service.Message.Contains("MariaDB", StringComparison.Ordinal) && service.Message.Contains('3'), "a service notification names the service and exit code");
        Check(AppNotifications.ServiceStopped("Redis", null).Message == "Redis stopped unexpectedly.", "a service notification without an exit code stays short");
        var worker = AppNotifications.SiteProcessStopped("blog", SiteBackgroundProcessKind.Queue, 1);
        Check(worker.Message.Contains("Queue worker", StringComparison.Ordinal) && worker.Message.Contains("blog", StringComparison.Ordinal), "a worker notification names the process and site");
        Check(worker.Key != AppNotifications.SiteProcessStopped("blog", SiteBackgroundProcessKind.Scheduler, 1).Key, "each site process is throttled separately");
        Check(AppNotifications.ShareEnded("blog.test").Message.Contains("private again", StringComparison.Ordinal), "a dropped public link says the site is private again");
        Check(AppNotifications.UpdateAvailable("1.2.3").Message.Contains("1.2.3", StringComparison.Ordinal), "an update notification names the version");
        Check(AppNotifications.CrashReported().Message.Contains("Export diagnostics", StringComparison.Ordinal), "a crash notification points at Export diagnostics");
    }

    private static void VerifyNotificationThrottle()
    {
        var throttle = new NotificationThrottle(TimeSpan.FromSeconds(60));
        var now = DateTimeOffset.UtcNow;
        Check(throttle.TryAcquire("service:MariaDB", now), "the first notification is shown");
        Check(!throttle.TryAcquire("service:mariadb", now.AddSeconds(30)), "a repeat within a minute is dropped");
        Check(throttle.TryAcquire("service:Redis", now.AddSeconds(30)), "another source is not throttled");
        Check(throttle.TryAcquire("service:MariaDB", now.AddSeconds(61)), "the notification is shown again after a minute");
    }

    private static void VerifyNotificationSetting(string supportRoot)
    {
        Check(new WindowsSiteSettings().ShowNotifications, "notifications are on by default");
        var store = new SiteConfigurationStore(Path.Combine(supportRoot, "notification-settings"));
        Check(store.Load().ShowNotifications, "a new settings file shows notifications");
        store.UpdateShowNotifications(false);
        Check(!new SiteConfigurationStore(store.SupportRoot).Load().ShowNotifications, "turning notifications off is saved");
        store.UpdateShowNotifications(true);
        Check(new SiteConfigurationStore(store.SupportRoot).Load().ShowNotifications, "turning notifications on is saved");
    }

    private static void VerifyDiagnosticsRedaction()
    {
        const string profile = "/home/example-user";
        var text = DiagnosticsRedaction.Redact(
            "DB_PASSWORD=hunter2 api_key: abc123 \"token\": \"xyz\" Authorization: Bearer eyJabc.def "
                + "redis://user:pa55@127.0.0.1:6379 path /home/example-user/project",
            profile
        );
        foreach (var secret in new[] { "hunter2", "abc123", "xyz", "eyJabc", "pa55", "example-user" })
        {
            Check(!text.Contains(secret, StringComparison.Ordinal), $"diagnostics mask {secret}");
        }
        Check(text.Contains("DB_PASSWORD=" + DiagnosticsRedaction.Mask, StringComparison.Ordinal), "diagnostics keep the name of a masked setting");
        Check(text.Contains("%USERPROFILE%/project", StringComparison.Ordinal), "diagnostics replace the user folder");
        Check(DiagnosticsRedaction.Redact("port=3306 name=blog", profile) == "port=3306 name=blog", "ordinary settings are not masked");
    }

    private static void VerifyCrashReporter(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "crash-reports");
        var crashes = Path.Combine(root, "Log", "Crashes");
        Directory.CreateDirectory(crashes);
        for (var index = 0; index < 7; index++)
        {
            File.WriteAllText(Path.Combine(crashes, $"crash-20000101-00000{index}-000.txt"), "old");
        }
        var reporter = new CrashReporter(root, "9.9.9");
        Check(!reporter.ConsumePendingCrash(), "no crash notice without a crash");
        var path = reporter.TryWrite(new InvalidOperationException("password=hunter2 failed"), writeDump: false);
        Check(path is not null && File.Exists(path), "a crash report is written under Log\\Crashes");
        var report = File.ReadAllText(path!);
        Check(report.Contains("Version: 9.9.9", StringComparison.Ordinal) && report.Contains("InvalidOperationException", StringComparison.Ordinal), "the crash report names the version and exception");
        Check(!report.Contains("hunter2", StringComparison.Ordinal), "the crash report masks secrets");
        Check(reporter.TryWrite(new InvalidOperationException("again"), writeDump: false) is null, "one crash writes one report");
        Check(reporter.Reports().Count == CrashReporter.MaximumReports, "only the newest five crash reports are kept");
        Check(reporter.Reports()[0] == path, "the newest crash report is kept");
        Check(reporter.ConsumePendingCrash(), "the next start sees the crash once");
        Check(!reporter.ConsumePendingCrash(), "the crash notice is shown only once");
    }

    private static async Task VerifyDiagnosticsExportAsync(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "diagnostics-export");
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        Directory.CreateDirectory(Path.Combine(root, "Log", "Crashes"));
        Directory.CreateDirectory(Path.Combine(root, "Runtimes"));
        File.WriteAllText(Path.Combine(root, "Config", "sites.json"), "{\"tld\":\"test\"}");
        File.WriteAllText(Path.Combine(root, "Log", "diagnostics.jsonl"), "{\"message\":\"password=hunter2\"}\n");
        File.WriteAllText(Path.Combine(root, "Log", "service-token.log"), "never exported");
        File.WriteAllText(Path.Combine(root, "Log", "Crashes", "crash-1.txt"), "report");
        File.WriteAllBytes(Path.Combine(root, "Log", "Crashes", "crash-1.dmp"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "Runtimes", "php.ini"), "not a log");
        File.WriteAllText(Path.Combine(root, "Log", "big.log"), "first line\n" + new string('x', (int)DiagnosticsExporter.MaximumTextBytesPerFile) + "\nlast line\n");

        var exporter = new DiagnosticsExporter(root, "9.9.9", "/home/example-user");
        var destination = Path.Combine(supportRoot, "exports", "diagnostics.zip");
        var result = await exporter.ExportAsync(destination, includeCrashDumps: false);
        Check(File.Exists(destination) && result.Path == Path.GetFullPath(destination), "Export diagnostics writes the chosen zip");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp").Any(), "the export leaves no temporary file");
        using (var archive = ZipFile.OpenRead(destination))
        {
            var names = archive.Entries.Select(entry => entry.FullName).ToList();
            foreach (var expected in new[] { "about.txt", "Config/sites.json", "Log/diagnostics.jsonl", "Log/Crashes/crash-1.txt", "Log/big.log" })
            {
                Check(names.Contains(expected), $"the diagnostics zip contains {expected}");
            }
            Check(!names.Contains("Log/Crashes/crash-1.dmp"), "crash dumps are left out unless the user asks");
            Check(!names.Contains("Log/service-token.log"), "files named like secrets are left out");
            Check(!names.Any(name => name.StartsWith("Runtimes/", StringComparison.Ordinal)), "only logs and settings are exported");
            using var diagnostics = new StreamReader(archive.GetEntry("Log/diagnostics.jsonl")!.Open());
            Check(!(await diagnostics.ReadToEndAsync()).Contains("hunter2", StringComparison.Ordinal), "exported logs are redacted");
            using var big = new StreamReader(archive.GetEntry("Log/big.log")!.Open());
            var bigText = await big.ReadToEndAsync();
            Check(bigText.Contains("last line", StringComparison.Ordinal) && !bigText.Contains("first line", StringComparison.Ordinal), "long logs keep their newest part");
            using var about = new StreamReader(archive.GetEntry("about.txt")!.Open());
            Check((await about.ReadToEndAsync()).Contains("does not send diagnostics", StringComparison.Ordinal), "the export says nothing is sent");
        }
        await exporter.ExportAsync(destination, includeCrashDumps: true);
        using (var archive = ZipFile.OpenRead(destination))
        {
            Check(archive.GetEntry("Log/Crashes/crash-1.dmp") is not null, "crash dumps are included when the user asks");
        }
        Check(DiagnosticsExporter.DefaultFileName(DateTimeOffset.UnixEpoch) == "herdme-diagnostics-19700101-000000.zip", "the suggested export name is dated");
    }

    private static void VerifyNotificationSources(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var notifications = File.ReadAllText(Path.Combine(app, "App.Notifications.cs"));
        Check(notifications.Contains("trayIcon.ShowNotification(", StringComparison.Ordinal), "notifications use the tray balloon, not a Windows registration");
        Check(notifications.Contains("ShowNotifications", StringComparison.Ordinal), "notifications respect the General switch");
        var updates = File.ReadAllText(Path.Combine(app, "App.Updates.cs"));
        Check(updates.Contains("if (IsMainWindowVisible)", StringComparison.Ordinal)
            && updates.Contains("NotifyUpdatesAvailable", StringComparison.Ordinal), "update notifications use an in-app toast while visible and a tray notification while hidden");
        var main = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        Check(main.Contains("suppressNotifications = acceptanceRun || onboardingAcceptance;", StringComparison.Ordinal), "acceptance runs never show notifications");
        Check(main.Contains("crashReporter?.TryWrite(args.Exception, writeDump: true);", StringComparison.Ordinal), "a fatal UI error saves a crash report");
        Check(main.Contains("UnsubscribeNotifications();", StringComparison.Ordinal), "exit stops notifications");
        var services = File.ReadAllText(Path.Combine(app, "Services", "WindowsServiceManager.cs"));
        Check(services.Contains("ExitedUnexpectedly?.Invoke", StringComparison.Ordinal), "services report unexpected exits");
        var processes = File.ReadAllText(Path.Combine(app, "Services", "SiteProcessManager.cs"));
        Check(processes.Contains("!stoppedByUser && exitCode is not null and not 0", StringComparison.Ordinal), "a stopped worker is not reported as a failure");
        var shares = File.ReadAllText(Path.Combine(app, "Services", "SiteShareManager.cs"));
        Check(shares.Contains("ShareEndedUnexpectedly?.Invoke", StringComparison.Ordinal), "a dropped public link is reported");
        var general = File.ReadAllText(Path.Combine(app, "Pages", "GeneralPage.xaml"));
        foreach (var name in new[] { "NotificationsToggle", "ExportDiagnosticsButton", "IncludeCrashDumpsCheckBox" })
        {
            Check(general.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal), $"General has {name}");
        }
        var crash = File.ReadAllText(Path.Combine(app, "Services", "CrashReporter.cs"));
        Check(!crash.Contains("MiniDumpWithFullMemory", StringComparison.Ordinal), "crash dumps never include the full process memory");
        Check(!crash.Contains("HttpClient", StringComparison.Ordinal), "crash reports are never uploaded");
        var exporterSource = File.ReadAllText(Path.Combine(app, "Services", "DiagnosticsExporter.cs"));
        Check(!exporterSource.Contains("HttpClient", StringComparison.Ordinal), "diagnostics are never uploaded");
    }
}
