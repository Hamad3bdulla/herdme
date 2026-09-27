using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

// Round 8 (front end): the per-site .env editor, Artisan autocomplete and history, new-site
// templates, service .env snippets and the long-operation finish alert.
internal static partial class ContractChecks
{
    internal static void VerifyRound8UxContracts(string repositoryRoot, string supportRoot)
    {
        VerifyEnvironmentEditorModel();
        VerifyArtisanAutocomplete(supportRoot);
        VerifyProjectTemplates(supportRoot);
        VerifyServiceEnvironmentSnippets(supportRoot);
        VerifyOperationFinishAlert();
        VerifyRound8Sources(repositoryRoot);
    }

    private static void VerifyEnvironmentEditorModel()
    {
        const string contents = "# App\r\nAPP_NAME=Demo\r\nDB_PASSWORD=secret\r\n\r\nAPP_URL=https://demo.test\r\n";
        var entries = EnvironmentEditorModel.Entries(contents);
        Check(
            entries.Select(entry => entry.Key).SequenceEqual(new[] { "APP_NAME", "DB_PASSWORD", "APP_URL" }),
            "the .env editor lists variables in file order and skips comments and blank lines"
        );
        Check(entries.Single(entry => entry.Key == "DB_PASSWORD").IsSecret, "password variables are treated as secrets");
        Check(!entries.Single(entry => entry.Key == "APP_NAME").IsSecret, "ordinary variables are not masked");
        Check(
            EnvironmentEditorModel.SetValue(contents, 1, "Other")
                == "# App\r\nAPP_NAME=Other\r\nDB_PASSWORD=secret\r\n\r\nAPP_URL=https://demo.test\r\n",
            "editing one value keeps comments, blank lines and CRLF endings"
        );
        var added = EnvironmentEditorModel.Add(contents, "CACHE_STORE", "redis");
        Check(added.EndsWith("APP_URL=https://demo.test\r\nCACHE_STORE=redis\r\n", StringComparison.Ordinal), "new variables are appended with the file's line ending");
        Check(
            EnvironmentEditorModel.Entries(EnvironmentEditorModel.Add(contents, "APP_NAME", "Again")).Count(entry => entry.Key == "APP_NAME") == 1,
            "adding an existing key changes it instead of adding a duplicate"
        );
        var removed = EnvironmentEditorModel.Remove(contents, 2);
        Check(!removed.Contains("DB_PASSWORD", StringComparison.Ordinal) && removed.StartsWith("# App\r\n", StringComparison.Ordinal), "removing a variable keeps the other lines");
        Check(SafeThrows<ArgumentException>(() => EnvironmentEditorModel.Add(contents, "1BAD", "x")), "invalid variable names are refused");

        Check(EnvironmentEditorModel.IsValidKey("APP_NAME") && !EnvironmentEditorModel.IsValidKey("1A") && !EnvironmentEditorModel.IsValidKey("A-B"), "variable names follow the dotenv rules");
        Check(EnvironmentEditorModel.Display("DB_PASSWORD", "secret") == EnvironmentEditorModel.MaskText, "secret values are masked");
        Check(EnvironmentEditorModel.Display("APP_NAME", "Demo") == "Demo", "plain values are shown");
        Check(!EnvironmentEditorModel.IsSecret("DB_PASSWORD", "") && !EnvironmentEditorModel.IsSecret("REDIS_PASSWORD", "null"), "empty secrets are not masked");
        Check(EnvironmentEditorModel.IsSecret("DB_URL", "mysql://root:pw@127.0.0.1/app"), "connection URLs with a password are masked");
        Check(!EnvironmentEditorModel.IsSecret("APP_URL", "https://demo.test"), "URLs without credentials are not masked");

        var problems = EnvironmentEditorModel.Problems("A=1\nA=2\nB=hello world\n=bad\n");
        Check(
            problems.Any(problem => problem.LineNumber == 2 && problem.Kind == EnvironmentProblemKind.DuplicateKey)
                && problems.Any(problem => problem.LineNumber == 3 && problem.Kind == EnvironmentProblemKind.UnquotedWhitespace)
                && problems.Any(problem => problem.LineNumber == 4 && problem.Kind == EnvironmentProblemKind.InvalidLine),
            "the .env editor reports duplicates, unquoted spaces and invalid lines with line numbers"
        );
        Check(
            EnvironmentEditorModel.Problems("A=\"open\n").Any(problem => problem.Kind == EnvironmentProblemKind.UnclosedQuote),
            "an unclosed quote is reported"
        );
        var multiline = EnvironmentEditorModel.Entries("KEY=\"line1\nline2\"\nB=1\n");
        Check(multiline.Count == 2 && multiline[0].IsMultiline && multiline[1].Key == "B", "double-quoted values may span lines");

        var diff = EnvironmentEditorModel.Diff("A=1\nDB_PASSWORD=old\nC=3\n", "A=2\nDB_PASSWORD=new\nD=4\n");
        Check(
            diff.Select(line => (line.Kind, line.Key)).SequenceEqual(new[]
            {
                (EnvironmentDiffKind.Changed, "A"),
                (EnvironmentDiffKind.Changed, "DB_PASSWORD"),
                (EnvironmentDiffKind.Added, "D"),
                (EnvironmentDiffKind.Removed, "C")
            }),
            "the review lists changed, added and removed keys"
        );
        var secretChange = diff.Single(line => line.Key == "DB_PASSWORD");
        Check(
            secretChange.Before == EnvironmentEditorModel.MaskText && secretChange.After == EnvironmentEditorModel.MaskText,
            "the review never shows secret values"
        );
        Check(!EnvironmentEditorModel.OtherLinesChanged("# c\nA=1\n", "# c\nA=2\n"), "value edits are not reported as other line changes");
        Check(EnvironmentEditorModel.OtherLinesChanged("# c\nA=1\n", "# d\nA=1\n"), "comment edits are reported");

        Check(EnvironmentEditorModel.MissingFromExample("A=1\n", "A=\nB=2\n").SequenceEqual(new[] { "B" }), "keys missing from .env.example are found");
        var filled = EnvironmentEditorModel.AddMissingFromExample("A=1\n", "A=\nB=2\n");
        Check(filled.Contains("# Added by HerdMe from .env.example\nB=2\n", StringComparison.Ordinal) && filled.StartsWith("A=1\n", StringComparison.Ordinal), "missing keys are added under one comment");
        var suggestions = EnvironmentEditorModel.Suggestions("DB_", "DB_HOST=127.0.0.1\n", null);
        Check(
            suggestions.Count > 0
                && suggestions.All(key => key.StartsWith("DB_", StringComparison.Ordinal))
                && !suggestions.Contains("DB_HOST")
                && suggestions.Contains("DB_PASSWORD"),
            "key autocomplete offers known keys the file does not have yet"
        );
    }

    private static void VerifyArtisanAutocomplete(string supportRoot)
    {
        const string json = """
            {"commands":[
              {"name":"migrate","description":"Run the database migrations","usage":["migrate [--force]"]},
              {"name":"secret:thing","description":"Hidden","hidden":true},
              {"name":"bad name","description":"Spaces"},
              {"name":"make:model","description":"Create a new\n  Eloquent model"}
            ]}
            """;
        var commands = ArtisanCommandIndex.ParseCommandInfoJson(json);
        Check(commands.Select(info => info.Name).SequenceEqual(new[] { "make:model", "migrate" }), "hidden and malformed Artisan commands are dropped");
        Check(commands[0].Description == "Create a new Eloquent model", "Artisan descriptions are flattened to one line");
        Check(commands[1].Usage == "migrate [--force]", "Artisan usage is kept for the hint");

        var merged = ArtisanCommandIndex.WithFallback(commands);
        Check(merged.Count(info => info.Name == "migrate") == 1, "the built-in list does not duplicate discovered commands");
        Check(merged.Any(info => info.Name == "about"), "built-in commands fill in what the project did not report");

        var recent = new[] { "migrate --force", "cache:clear" };
        var suggestions = ArtisanCommandIndex.Suggest("mi", commands, recent, "recent");
        Check(
            suggestions.Count == 2
                && suggestions[0].IsRecent && suggestions[0].Command == "migrate --force"
                && !suggestions[1].IsRecent && suggestions[1].Command == "migrate"
                && suggestions[1].ToString().Contains("Run the database migrations", StringComparison.Ordinal),
            "recent commands come first, then matching commands with their descriptions"
        );
        Check(ArtisanCommandIndex.Suggest("migrate --f", commands, recent, "recent").Select(item => item.Command).SequenceEqual(new[] { "migrate --force" }), "once arguments are typed only history matches");
        Check(ArtisanCommandIndex.Suggest("", commands, [], "recent", maximum: 1).Count == 1, "suggestions are capped");
        Check(ArtisanCommandIndex.Describe("migrate --force", commands)?.Name == "migrate", "the hint describes the command being typed");
        Check(ArtisanCommandIndex.Describe(" ", commands) is null, "an empty box has no hint");
        Check(ArtisanCommandIndex.HistoryText(["migrate", "--force", "--no-interaction"]) == "migrate --force", "history stores what the user would type");

        var site = Path.Combine(supportRoot, "round8-artisan-site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, "composer.lock"), "{}");
        var cache = new ArtisanCommandCache(Path.Combine(supportRoot, "round8-artisan-cache"));
        var now = DateTimeOffset.UtcNow;
        Check(cache.Load(site, now) is null, "an unknown site has no cached commands");
        cache.Save(site, commands, now);
        Check(cache.Load(site, now) is { Fresh: true } fresh && fresh.Commands.Count == 2, "cached commands load back fresh");
        Check(cache.Load(site, now + ArtisanCommandCache.MaximumAge + TimeSpan.FromMinutes(1)) is { Fresh: false }, "cached commands go stale after a week");
        File.WriteAllText(Path.Combine(site, "composer.lock"), "{\"packages\":[]}");
        Check(cache.Load(site, now) is { Fresh: false }, "cached commands go stale when composer.lock changes");
        Check(cache.CachePath.EndsWith(Path.Combine("Cache", "artisan-commands.json"), StringComparison.OrdinalIgnoreCase), "the Artisan cache lives under the HerdMe Cache folder");

        var history = SiteCommandFavoritesStore.History(Path.Combine(supportRoot, "round8-history"));
        for (var index = 0; index < SiteCommandFavoritesStore.HistoryLimit + 5; index++)
        {
            history.Add(site, "artisan", $"route:list --page={index}");
        }
        history.Add(site, "artisan", "route:list --page=3");
        var loaded = history.Load(site, "artisan");
        Check(loaded.Count == SiteCommandFavoritesStore.HistoryLimit, "per-site command history is capped");
        Check(loaded[0].Command == "route:list --page=3" && loaded.Count(item => item.Command == "route:list --page=3") == 1, "a repeated command moves to the top without duplicating");
    }

    private static void VerifyProjectTemplates(string supportRoot)
    {
        var templates = ProjectTemplateCatalog.All;
        Check(templates.Select(template => template.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == templates.Count, "template ids are unique");
        Check(templates[0].Id == "None" && templates[^1].Group == ProjectTemplateGroup.Custom, "Blank comes first and Custom last");
        Check(templates.All(template => template.DocumentationUrl.StartsWith("https://", StringComparison.Ordinal)), "every template links to its official documentation over HTTPS");
        Check(
            templates.All(template => (template.Group == ProjectTemplateGroup.Package)
                == (template.ComposerPackage.Length > 0 && template.InstallCommand.Count > 0)),
            "only package templates carry a Composer package and install command"
        );
        Check(ProjectTemplateCatalog.Find(" breeze ")?.Id == "Breeze" && ProjectTemplateCatalog.Find("nope") is null, "templates are found by id");

        var breeze = ProjectTemplateCatalog.Find("Breeze")!;
        var filament = ProjectTemplateCatalog.Find("Filament")!;
        Check(breeze.InstallArguments("Pest").SequenceEqual(new[] { "breeze:install", "blade", "--pest", "--no-interaction" }), "Breeze installs with the chosen test framework");
        Check(breeze.InstallArguments("PHPUnit").SequenceEqual(new[] { "breeze:install", "blade", "--no-interaction" }), "PHPUnit projects do not pass --pest");
        Check(filament.InstallArguments("Pest").SequenceEqual(new[] { "filament:install", "--panels", "--no-interaction" }), "Filament installs its panel builder");
        Check(ProjectTemplateCatalog.Find("None")!.InstallArguments("Pest").Count == 0, "the blank template runs no installer");
        Check(
            LaravelProjectCreator.BuildTemplateRequireArguments("composer.phar", breeze)
                .SequenceEqual(new[] { "composer.phar", "require", "laravel/breeze", "--dev", "--no-interaction", "--no-progress", "--no-ansi" }),
            "Breeze is required as a development dependency without prompts"
        );
        Check(
            !LaravelProjectCreator.BuildTemplateRequireArguments("composer.phar", filament).Contains("--dev"),
            "Filament is a runtime dependency"
        );
        Check(SafeThrows<ArgumentException>(() => LaravelProjectCreator.BuildTemplateRequireArguments("composer.phar", ProjectTemplateCatalog.Find("React")!)), "starter kits are not installed with Composer require");

        Check(
            LaravelProjectCreationStages.For(new LaravelProjectRequest(
                "demo-app", supportRoot, "Filament", "Pest", InstallBoost: false, InitializeGit: false
            )).SequenceEqual([
                LaravelProjectCreationStage.ValidatingRequest,
                LaravelProjectCreationStage.PreparingLaravelInstaller,
                LaravelProjectCreationStage.CreatingLaravelProject,
                LaravelProjectCreationStage.InstallingTemplate,
                LaravelProjectCreationStage.VerifyingProject,
                LaravelProjectCreationStage.RegisteringSite,
                LaravelProjectCreationStage.Completed
            ]),
            "Filament projects install the template without a frontend build"
        );
        Check(
            LaravelProjectCreationStages.For(new LaravelProjectRequest(
                "demo-app", supportRoot, "Breeze", "Pest", InstallBoost: false, InitializeGit: false
            )).SequenceEqual([
                LaravelProjectCreationStage.ValidatingRequest,
                LaravelProjectCreationStage.PreparingLaravelInstaller,
                LaravelProjectCreationStage.CreatingLaravelProject,
                LaravelProjectCreationStage.PreparingNodeRuntime,
                LaravelProjectCreationStage.InstallingTemplate,
                LaravelProjectCreationStage.InstallingFrontendDependencies,
                LaravelProjectCreationStage.BuildingFrontendAssets,
                LaravelProjectCreationStage.VerifyingProject,
                LaravelProjectCreationStage.RegisteringSite,
                LaravelProjectCreationStage.Completed
            ]),
            "Breeze prepares Node.js before its installer runs npm"
        );
        Check(
            !LaravelProjectCreationStages.For(new LaravelProjectRequest(
                "demo-app", supportRoot, "Custom", "Pest", InstallBoost: false, InitializeGit: false, CustomStarterKit: "vendor/kit"
            )).Contains(LaravelProjectCreationStage.InstallingTemplate),
            "custom starter kits are handled by the Laravel installer"
        );
    }

    private static void VerifyServiceEnvironmentSnippets(string supportRoot)
    {
        Check(
            ServiceEnvironmentFile.FormatLines([
                new ServiceEnvironmentVariable("DB_HOST", "127.0.0.1"),
                new ServiceEnvironmentVariable("DB_PASSWORD", "p w\"x")
            ]) == "DB_HOST=127.0.0.1\r\nDB_PASSWORD=\"p w\\\"x\"\r\n",
            "copied service settings are ready-to-paste .env lines with values quoted when needed"
        );
        Check(ServiceEnvironmentFile.FormatLines([]) == string.Empty, "a service without settings copies nothing");

        var project = Path.Combine(supportRoot, "round8-env-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".env.example"), "A=1\n");
        Check(ServiceEnvironmentFile.ReadStartingContents(project, out var exists) == "A=1\n" && !exists, "the preview starts from .env.example when there is no .env");
        File.WriteAllText(Path.Combine(project, ".env"), "B=2\n");
        Check(ServiceEnvironmentFile.ReadStartingContents(project, out exists) == "B=2\n" && exists, "the preview starts from the site's .env");
    }

    private static void VerifyOperationFinishAlert()
    {
        var tracker = new OperationFinishTracker();
        var start = DateTimeOffset.UtcNow;
        Check(tracker.Observe([new OperationObservation("php", "PHP 8.4", true, null, null)], start).Count == 0, "a running operation is not reported");
        Check(tracker.Tracking == 1, "running operations are tracked");
        var finished = tracker.Observe(
            [
                new OperationObservation("php", "PHP 8.4", false, OperationOutcome.Failed, "boom\nstack"),
                new OperationObservation("old", "Old row", false, OperationOutcome.Succeeded, null)
            ],
            start.AddSeconds(30)
        );
        Check(
            finished.Count == 1 && finished[0].Id == "php" && finished[0].Outcome == OperationOutcome.Failed && finished[0].Duration == TimeSpan.FromSeconds(30),
            "an operation that stops running is reported once with its duration; history rows are ignored"
        );
        Check(tracker.Observe([new OperationObservation("php", "PHP 8.4", false, OperationOutcome.Failed, "boom")], start.AddSeconds(40)).Count == 0, "a finished operation is reported only once");
        tracker.Observe([new OperationObservation("node", "Node", true, null, null)], start);
        Check(tracker.Observe([], start.AddMinutes(1)).Count == 0 && tracker.Tracking == 0, "rows cleared while running are forgotten");

        var succeeded = new FinishedOperation("php", "PHP 8.4", OperationOutcome.Succeeded, TimeSpan.FromSeconds(30), null);
        Check(OperationFinishAlert.ShouldAlert(succeeded, windowInForeground: false), "long operations alert when HerdMe is in the background");
        Check(!OperationFinishAlert.ShouldAlert(succeeded, windowInForeground: true), "nothing flashes while the user is looking at HerdMe");
        Check(!OperationFinishAlert.ShouldAlert(succeeded with { Duration = TimeSpan.FromSeconds(5) }, false), "short operations stay quiet");
        Check(!OperationFinishAlert.ShouldAlert(succeeded with { Outcome = OperationOutcome.Cancelled }, false), "cancelled operations stay quiet");

        Check(OperationFinishAlert.Duration(TimeSpan.FromSeconds(12)) == "12 s", "short durations are shown in seconds");
        Check(OperationFinishAlert.Duration(TimeSpan.FromSeconds(65)) == "1:05", "durations over a minute are shown as m:ss");
        Check(OperationFinishAlert.Duration(TimeSpan.FromSeconds(3725)) == "1:02:05", "durations over an hour are shown as h:mm:ss");

        var localize = ServiceText.Localize;
        ServiceText.Localize = null;
        try
        {
            var action = NotificationActions.OpenPage("updates", "NotificationActionOpenUpdates");
            var success = OperationFinishAlert.Notification(succeeded, action);
            Check(success.Key == "finished:php" && success.Primary == action && success.Message.Contains("30 s", StringComparison.Ordinal), "the finish notification names the operation, its duration and an action");
            var failure = OperationFinishAlert.Notification(
                succeeded with { Outcome = OperationOutcome.Failed, Error = "boom\nstack trace" },
                null
            );
            Check(failure.Message.Contains("boom", StringComparison.Ordinal) && !failure.Message.Contains("stack", StringComparison.Ordinal), "a failure notification shows only the first line of the error");
            var longFailure = OperationFinishAlert.Notification(
                succeeded with { Outcome = OperationOutcome.Failed, Error = new string('x', 400) },
                null
            );
            Check(!longFailure.Message.Contains(new string('x', 161), StringComparison.Ordinal) && longFailure.Message.EndsWith("...", StringComparison.Ordinal), "long errors are shortened in the notification");
        }
        finally
        {
            ServiceText.Localize = localize;
        }
    }

    private static void VerifyRound8Sources(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));

        var editor = Read("Pages", "SitesPage.EnvironmentEditor.cs");
        Check(editor.Contains("EnvironmentEditorModel.Diff(", StringComparison.Ordinal), "the .env editor reviews a diff before saving");
        Check(editor.Contains("SitesEnvironmentRevealSecrets", StringComparison.Ordinal), "secret values stay hidden until revealed");

        var commands = Read("Pages", "SitesPage.Commands.cs");
        Check(commands.Contains("ArtisanCommandIndex.Suggest(", StringComparison.Ordinal), "the Artisan dialog autocompletes from the project's commands");
        Check(commands.Contains("RememberCommand(", StringComparison.Ordinal), "run Artisan commands are remembered per site");

        var creation = Read("Pages", "SitesPage.ProjectCreation.cs");
        Check(creation.Contains("CreateTemplatePicker()", StringComparison.Ordinal) && creation.Contains("DocumentationUrl", StringComparison.Ordinal), "new projects pick a described template with a documentation link");
        Check(creation.Contains("NotificationActions.OpenSite(", StringComparison.Ordinal) && creation.Contains("OperationOutcome.Failed", StringComparison.Ordinal), "project creation reports when it finishes in the background");
        Check(Read("Services", "LaravelProjectCreator.cs").Contains("LaravelProjectCreationStage.InstallingTemplate", StringComparison.Ordinal), "the creator reports the template install stage");

        var servicesXaml = Read("Pages", "ServicesPage.xaml");
        Check(servicesXaml.Contains("x:Uid=\"ServicesCopyEnvironmentMenu\"", StringComparison.Ordinal) && servicesXaml.Contains("Click=\"CopyEnvironment_Click\"", StringComparison.Ordinal), "services offer Copy .env settings");
        var snippets = Read("Pages", "ServicesPage.EnvironmentSnippets.cs");
        Check(snippets.Contains("ServiceEnvironmentFile.FormatLines(", StringComparison.Ordinal) && snippets.Contains("EnvironmentEditorModel.Diff(", StringComparison.Ordinal), "service settings are copied as .env lines and previewed as a masked diff");
        Check(Read("Pages", "ServicesPage.xaml.cs").Contains("ShowEnvironmentPreviewAsync(", StringComparison.Ordinal), "Add to .env previews the change before applying");

        var finish = Read("MainWindow.FinishAlert.cs");
        Check(finish.Contains("FlashWindowEx", StringComparison.Ordinal) && finish.Contains("OperationFinishAlert.ShouldAlert(", StringComparison.Ordinal), "finished background work flashes the taskbar button");
        Check(Read("MainWindow.StatusBar.cs").Contains("ObserveFinishedOperations();", StringComparison.Ordinal), "the status bar refresh watches for finished operations");
        Check(Read("App.Notifications.cs").Contains("NotifyOperationFinished", StringComparison.Ordinal), "finish alerts go through the notification setting and throttle");

        var english = Read("Strings", "en-US", "Resources.resw");
        var arabic = Read("Strings", "ar", "Resources.resw");
        var keys = ProjectTemplateCatalog.All.SelectMany(template => new[] { template.NameKey, template.DescriptionKey })
            .Concat(Enum.GetValues<ProjectTemplateGroup>().Select(group => $"SitesTemplateGroup{group}"))
            .Concat([
                "NotificationOperationSucceededTitle",
                "NotificationOperationSucceededMessage",
                "NotificationOperationFailedTitle",
                "NotificationOperationFailedMessage",
                "NotificationOperationFailedWithError",
                "ServicesEnvironmentCopied",
                "ServicesEnvironmentCopiedSecret",
                "ServicesEnvironmentNoVariables"
            ]);
        foreach (var key in keys)
        {
            Check(
                english.Contains($"<data name=\"{key}\"", StringComparison.Ordinal) && arabic.Contains($"<data name=\"{key}\"", StringComparison.Ordinal),
                $"{key} is translated"
            );
        }
    }

    private static bool SafeThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
