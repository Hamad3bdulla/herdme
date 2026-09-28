using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 9: site warning badges with fixes, recent sites in the tray and the Jump List, Tinker
// snippets / selection / pretty output, new-mail notifications and the read-only table peek.
internal static partial class ContractChecks
{
    internal static void VerifyRound9UxContracts(string repositoryRoot, string supportRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));
        var root = Path.Combine(supportRoot, "round9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        VerifySiteWarnings(root);
        VerifyRecentSites(root);
        VerifyTinkerOutputAndSnippets(root);
        VerifyMailNotification();
        VerifyDatabasePeek();

        var sites = Read("Pages", "SitesPage.xaml");
        Check(
            sites.Contains("AutomationProperties.AutomationId=\"SitesRowWarnings\"", StringComparison.Ordinal)
                && sites.Contains("Opening=\"RowWarningsFlyout_Opening\"", StringComparison.Ordinal),
            "site rows show a warning badge with a menu of fixes"
        );
        Check(
            sites.Contains("AutomationProperties.AutomationId=\"BrowseDatabaseButton\"", StringComparison.Ordinal)
                && sites.Contains("Click=\"BrowseDatabase_Click\"", StringComparison.Ordinal),
            "the Database tab has a Browse tables button"
        );
        var tinker = Read("Pages", "TinkerPage.xaml.cs");
        Check(
            tinker.Contains("CodeBox.SelectedText", StringComparison.Ordinal),
            "Tinker runs only the selected code when there is a selection"
        );
        var tinkerXaml = Read("Pages", "TinkerPage.xaml");
        Check(
            tinkerXaml.Contains("x:Name=\"SnippetBox\"", StringComparison.Ordinal)
                && tinkerXaml.Contains("x:Name=\"OutputViewBox\"", StringComparison.Ordinal),
            "Tinker has saved snippets and an output view picker"
        );
        var activity = Read("MainWindow.Activity.cs");
        Check(
            activity.Contains("NotifyNewMail(", StringComparison.Ordinal)
                && activity.Contains("currentPageTag == \"mail\"", StringComparison.Ordinal),
            "new mail notifies unless the Mail page is already on screen"
        );
        Check(
            Read("App.Notifications.cs").Contains("MailNotifications", StringComparison.Ordinal),
            "mail notifications follow their own General setting"
        );
        Check(
            new WindowsSiteSettings().MailNotifications,
            "mail notifications are on by default (still behind the notifications switch)"
        );
        var peekPage = Read("Pages", "SitesPage.DatabasePeek.cs");
        Check(
            peekPage.Contains("PeekSiteDatabaseTablesAsync", StringComparison.Ordinal)
                && peekPage.Contains("PeekSiteDatabaseRowsAsync", StringComparison.Ordinal)
                && !peekPage.Contains("ExecuteAsync", StringComparison.Ordinal),
            "the table peek only lists tables and reads rows"
        );
        var jump = Read("App.Commands.cs");
        Check(
            jump.Contains("JumpListManager.Apply(executable, next, RecentSitePaths)", StringComparison.Ordinal),
            "the Jump List is built with the recent sites"
        );
    }

    private static void VerifySiteWarnings(string root)
    {
        var laravel = Path.Combine(root, "warn-laravel");
        Directory.CreateDirectory(laravel);
        File.WriteAllText(Path.Combine(laravel, "artisan"), "<?php");
        File.WriteAllText(Path.Combine(laravel, "composer.json"), "{}");
        var site = new SiteRecord { Name = "warn", Path = laravel, Domain = "warn.test", Framework = "Laravel", PhpVersion = "8.4" };
        var warnings = SiteWarnings.Inspect(site, "8.3", ["8.3"]);
        Check(
            warnings.Any(warning => warning.Kind == SiteWarningKind.PhpNotInstalled && warning.Detail == "8.4")
                && warnings.Any(warning => warning.Kind == SiteWarningKind.EnvironmentMissing)
                && warnings.Any(warning => warning.Kind == SiteWarningKind.DependenciesMissing),
            "site warnings find a missing PHP, a missing .env and missing vendor packages"
        );
        File.WriteAllText(Path.Combine(laravel, ".env"), "APP_NAME=warn\nAPP_KEY=\n");
        Check(
            SiteWarnings.Inspect(site, "8.3", ["8.3", "8.4"]).Any(warning => warning.Kind == SiteWarningKind.AppKeyMissing),
            "an empty APP_KEY is a site warning"
        );
        File.WriteAllText(Path.Combine(laravel, ".env"), "APP_NAME=warn\nAPP_KEY=base64:abc\n");
        Directory.CreateDirectory(Path.Combine(laravel, "vendor"));
        File.WriteAllText(Path.Combine(laravel, "vendor", "autoload.php"), "<?php");
        Check(
            SiteWarnings.Inspect(site, "8.3", ["8.3", "8.4"]).Count == 0,
            "a healthy site has no warnings"
        );
        Check(
            SiteWarnings.Inspect(new SiteRecord { Name = "gone", Path = Path.Combine(root, "missing") }, "8.3", []).Count == 0,
            "a missing folder gives no warnings rather than an error"
        );
    }

    private static void VerifyRecentSites(string root)
    {
        var now = DateTimeOffset.UtcNow;
        var pushed = RecentSitesStore.Push(
            [new RecentSiteEntry("C:\\a", now.AddMinutes(-2)), new RecentSiteEntry("C:\\b", now.AddMinutes(-1))],
            "c:\\A",
            now
        );
        Check(pushed.Count == 2 && pushed[0].Path == "c:\\A", "opening a site again moves it to the top without duplicates");
        var many = new List<RecentSiteEntry>();
        for (var index = 0; index < 15; index++) many = RecentSitesStore.Push(many, "C:\\s" + index, now.AddSeconds(index));
        Check(many.Count == RecentSitesStore.Capacity && many[0].Path == "C:\\s14", "recent sites are bounded, newest first");

        var storeRoot = Path.Combine(root, "recent");
        var store = new RecentSitesStore(storeRoot);
        store.Record("C:\\one", now);
        store.Record("C:\\two", now.AddSeconds(1));
        Check(new RecentSitesStore(storeRoot).Paths.SequenceEqual(["C:\\two", "C:\\one"]), "recent sites persist across restarts");
        File.WriteAllText(store.StorePath, "{ not json");
        Check(new RecentSitesStore(storeRoot).Paths.Count == 0, "a damaged recent-sites file is ignored");

        var sites = new List<SiteRecord>();
        for (var index = 0; index < 12; index++)
        {
            sites.Add(new SiteRecord { Name = "site" + index, Domain = "site" + index + ".test", Path = "C:\\site" + index });
        }
        var recent = TrayPresentation.RecentSites(sites, ["C:\\site5", "C:\\gone", "C:\\site2", "C:\\site9", "C:\\site1"]);
        Check(
            recent.Select(site => site.Name).SequenceEqual(["site5", "site2", "site9"]),
            "the tray shows up to three known recent sites, newest first"
        );
        var rest = TrayPresentation.MenuSites(sites, recent);
        Check(
            rest.All(site => !recent.Contains(site)) && rest.Count + recent.Count <= TrayPresentation.SiteMenuLimit,
            "the tray site list does not repeat recent sites and keeps its length"
        );
        var jumpRecent = JumpListManager.BuildRecent(sites, ["C:\\site3", "C:\\site3", "C:\\site7"]);
        Check(
            jumpRecent.Select(entry => entry.Title).SequenceEqual(["site3", "site7"])
                && jumpRecent[0].Arguments == "--command site site3",
            "the Jump List Recent category opens recent sites"
        );
        Check(
            JumpListManager.BuildSites(sites, jumpRecent).All(entry => entry.Title != "site3" && entry.Title != "site7"),
            "the Jump List Sites category does not repeat recent sites"
        );
    }

    private static void VerifyTinkerOutputAndSnippets(string root)
    {
        Check(
            TinkerScript.BuildRunner("C:\\p", "C:\\p\\code.php", TinkerOutputMode.Json).Contains("json_encode(", StringComparison.Ordinal)
                && !TinkerScript.BuildRunner("C:\\p", "C:\\p\\code.php").Contains("__HERDME_PRINT__", StringComparison.Ordinal),
            "Tinker JSON mode prints json_encode output and the placeholder is always replaced"
        );
        var table = TinkerOutputFormatter.Table("[{\"id\":1,\"name\":\"Ann\"},{\"id\":2,\"name\":\"Bo\\nb\"}]");
        Check(
            table is not null && table.Contains("id | name", StringComparison.Ordinal)
                && table.Contains("2  | Bo b", StringComparison.Ordinal),
            "Tinker shows arrays of records as a text table"
        );
        Check(
            TinkerOutputFormatter.Table("{\"data\":[{\"a\":1}],\"total\":1}")?.Contains("a", StringComparison.Ordinal) == true,
            "a Laravel paginator shows its data rows"
        );
        Check(
            TinkerOutputFormatter.Table("Hello") is null && TinkerOutputFormatter.IndentJson("PHP Warning: x") is null,
            "non-JSON output falls back to the raw text"
        );
        Check(
            TinkerOutputFormatter.IndentJson("{\"a\":1}")?.Contains("\n", StringComparison.Ordinal) == true,
            "JSON output is indented"
        );

        var snippets = new TinkerSnippetStore(Path.Combine(root, "tinker-snippets.json"));
        var site = Path.Combine(root, "tinker-site");
        snippets.Save(site, "Users", "User::count();");
        snippets.Save(site, "users", "User::latest()->first();");
        var saved = snippets.Load(site);
        Check(
            saved.Count == 1 && saved[0].Code == "User::latest()->first();",
            "saving a snippet with the same name replaces it"
        );
        Check(snippets.Delete(site, "USERS").Count == 0 && snippets.Load(site).Count == 0, "snippets can be deleted");
        Throws<ArgumentException>(() => TinkerSnippetStore.NormalizeName("   "), "snippets need a name");
        Throws<ArgumentException>(
            () => TinkerSnippetStore.NormalizeName(new string('x', TinkerSnippetStore.MaximumNameCharacters + 1)),
            "snippet names are bounded"
        );
        for (var index = 0; index < TinkerSnippetStore.MaximumSnippetsPerSite + 5; index++)
        {
            snippets.Save(site, "s" + index, "echo " + index + ";");
        }
        Check(snippets.Load(site).Count == TinkerSnippetStore.MaximumSnippetsPerSite, "snippets per site are bounded");
        File.WriteAllText(snippets.FilePath, "[broken");
        Check(snippets.Load(site).Count == 0, "a damaged snippets file is ignored");
    }

    private static void VerifyMailNotification()
    {
        var notification = AppNotifications.MailCaptured("Welcome\r\naboard", "app@example.test", 3);
        Check(
            notification.Key == "mail"
                && notification.Primary is { Kind: NotificationActionKind.OpenPage, Argument: "mail" }
                && !notification.Message.Contains('\n'),
            "new mail notifications open the Mail page and show a one-line subject"
        );
        Check(
            AppNotifications.MailCaptured(new string('s', 400), null, 1).Message.Length <= AppNotifications.MailSnippetCharacters,
            "the mail snippet is bounded"
        );
    }

    private static void VerifyDatabasePeek()
    {
        foreach (var engine in new[] { "mysql", "mariadb", "postgresql" })
        {
            var sql = DatabasePeek.RowsSql(engine, new DatabasePeekTable(engine == "postgresql" ? "public" : string.Empty, "users"));
            Check(
                sql.Contains("READ ONLY", StringComparison.Ordinal)
                    && sql.Contains("LIMIT 50", StringComparison.Ordinal)
                    && sql.TrimEnd().EndsWith("ROLLBACK;", StringComparison.Ordinal),
                $"the {engine} table peek is a read-only, rolled back SELECT of 50 rows"
            );
            Check(
                DatabasePeek.ListTablesSql(engine).Contains("READ ONLY", StringComparison.Ordinal),
                $"the {engine} table list is read-only"
            );
        }
        Check(
            DatabasePeek.QuoteMySql("we`ird") == "`we``ird`" && DatabasePeek.QuotePostgreSql("we\"ird") == "\"we\"\"ird\"",
            "table names are quoted, never pasted into SQL"
        );
        Throws<ArgumentException>(
            () => DatabasePeek.RowsSql("mysql", new DatabasePeekTable(string.Empty, "users;\nDROP TABLE users")),
            "table names with control characters are refused"
        );
        var tables = DatabasePeek.ParseTables("mysql", "table_schema\ttable_name\n\tmigrations\n\tusers\n");
        Check(
            tables.Select(table => table.Name).SequenceEqual(["migrations", "users"]),
            "the MySQL table list skips the header line"
        );
        var pgTables = DatabasePeek.ParseTables("postgresql", "public\tusers\naudit\tevents\n");
        Check(
            pgTables.Select(table => table.DisplayName).SequenceEqual(["users", "audit.events"]),
            "PostgreSQL tables outside public keep their schema"
        );
        var rows = DatabasePeek.ParseMySqlRows("id\tbio\n1\tline\\nbreak\n2\tNULL\n");
        Check(
            rows.Columns.SequenceEqual(["id", "bio"]) && rows.Rows.Count == 2 && rows.Rows[0][1] == "line\nbreak",
            "MySQL rows are unescaped"
        );
        var pgRows = DatabasePeek.ParsePostgreSqlRows("[{\"id\":1,\"name\":\"Ann\",\"note\":null}]");
        Check(
            pgRows.Columns.SequenceEqual(["id", "name", "note"]) && pgRows.Rows[0][2] == "NULL",
            "PostgreSQL rows come back as columns and cells"
        );
        Check(
            DatabasePeek.ParsePostgreSqlRows("ERROR").Rows.Count == 0,
            "unexpected PostgreSQL output shows no rows rather than failing"
        );
        var text = TinkerOutputFormatter.TextTable(rows.Columns, rows.Rows);
        Check(
            text.Contains("id | bio", StringComparison.Ordinal) && text.Contains("line break", StringComparison.Ordinal),
            "peeked rows render as a one-line-per-row text table"
        );
    }
}
