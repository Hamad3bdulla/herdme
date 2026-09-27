using System.Text.Json;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyLaravelToolsContractsAsync(string repositoryRoot, string supportRoot)
    {
        VerifyLaravelLogParsing();
        VerifyLaravelLogLocations();
        VerifyEditorLauncher(supportRoot);
        VerifyQualityToolParsing(supportRoot);
        await VerifyQualityToolGuardsAsync(supportRoot);
        VerifyLaravelToolSources(repositoryRoot);
    }

    private const string SampleLaravelLog =
        "tail of an earlier entry\n"
        + "[2026-09-27 10:00:00] local.INFO: User signed in {\"id\":1}\n"
        + "[2026-09-27 10:00:01] local.WARNING: Slow query\n"
        + "[2026-09-27 10:00:02] local.ERROR: Undefined variable $user {\"exception\":\"[object] (ErrorException(code: 0): Undefined variable $user at C:\\\\site\\\\app\\\\Http\\\\Controllers\\\\HomeController.php:15)\n"
        + "[stacktrace]\n"
        + "#0 C:\\site\\vendor\\laravel\\framework\\src\\Illuminate\\Routing\\Controller.php(54): handle()\n"
        + "#1 {main}\n"
        + "\"}\n"
        + "[2026-09-27T10:00:03.123456+00:00] production.CRITICAL: Queue down\n";

    private static void VerifyLaravelLogParsing()
    {
        var entries = LaravelLog.Parse(SampleLaravelLog);
        Check(entries.Count == 5, "a Laravel log splits into the preamble and four entries");
        Check(entries[0].Level is null, "lines before the first header have no level");
        Check(entries[3].Level == LaravelLogLevel.Error && entries[3].Text.Contains("#1 {main}", StringComparison.Ordinal), "an entry keeps its stack trace");
        Check(entries[4].Level == LaravelLogLevel.Critical, "ISO timestamps and other channels are recognised");

        var errors = LaravelLog.FilterByMinimumLevel(SampleLaravelLog, LaravelLogLevel.Error);
        Check(errors.Contains("Undefined variable", StringComparison.Ordinal) && errors.Contains("Queue down", StringComparison.Ordinal), "the error filter keeps errors and above");
        Check(!errors.Contains("signed in", StringComparison.Ordinal) && !errors.Contains("Slow query", StringComparison.Ordinal), "the error filter drops info and warnings");
        Check(!errors.Contains("tail of an earlier entry", StringComparison.Ordinal), "a partial entry without a level is hidden by a level filter");
        Check(errors.Contains("[stacktrace]", StringComparison.Ordinal), "the level filter keeps whole entries");
        Check(LaravelLog.FilterByMinimumLevel(SampleLaravelLog, null) == SampleLaravelLog, "All levels shows the log unchanged");
        Check(LaravelLog.FilterByMinimumLevel(SampleLaravelLog, LaravelLogLevel.Warning).Contains("Slow query", StringComparison.Ordinal), "the warning filter keeps warnings");

        var summary = LaravelLog.Summarize(SampleLaravelLog);
        Check(summary.Entries == 4 && summary.Warnings == 1 && summary.Errors == 2, "the summary counts entries, warnings, and errors");
        Check(summary.LastError is { Line: 15 } location && location.Path.EndsWith("HomeController.php", StringComparison.Ordinal), "the summary points at the last error with a file");
        Check(LaravelLog.Summarize("plain text\nno headers").Entries == 0, "a non-Laravel log has no entries");
        Check(LaravelLog.ParseLevel("warning") == LaravelLogLevel.Warning && LaravelLog.ParseLevel("TRACE") is null, "levels parse case-insensitively");
    }

    private static void VerifyLaravelLogLocations()
    {
        var jsonEscaped = LaravelLog.FindSourceLocation("(ErrorException at C:\\\\site\\\\app\\\\Models\\\\User.php:42)");
        Check(jsonEscaped == new SourceLocation(@"C:\site\app\Models\User.php", 42), "JSON-escaped Windows paths are unescaped");
        var preferProject = LaravelLog.FindSourceLocation(
            "boom at C:\\site\\vendor\\laravel\\framework\\src\\Foundation\\Application.php:900)\n"
                + "#0 C:\\site\\vendor\\laravel\\framework\\src\\Routing\\Router.php(10): run()\n"
                + "#1 C:\\site\\app\\Jobs\\SendMail.php(33): handle()\n"
        );
        Check(preferProject == new SourceLocation(@"C:\site\app\Jobs\SendMail.php", 33), "a project frame wins over vendor frames");
        var vendorOnly = LaravelLog.FindSourceLocation("failed at /srv/site/vendor/package/src/Thing.php:7)");
        Check(vendorOnly == new SourceLocation("/srv/site/vendor/package/src/Thing.php", 7), "vendor code is used when nothing else is known");
        Check(LaravelLog.FindSourceLocation("no file here") is null, "text without a file has no location");
        Check(LaravelLog.FindSourceLocation("at C:\\site\\app\\X.php:0") is null, "line zero is not a location");
    }

    private static void VerifyEditorLauncher(string supportRoot)
    {
        var file = Path.Combine(supportRoot, "editor", "app", "Example.php");
        var arguments = EditorLauncher.VisualStudioCodeArguments(file, 12);
        Check(arguments.Count == 2 && arguments[0] == "-g" && arguments[1] == Path.GetFullPath(file) + ":12", "VS Code opens the file at the line");
        Check(EditorLauncher.VisualStudioCodeArguments(file, null)[1] == Path.GetFullPath(file), "without a line VS Code opens the file");

        var install = Path.Combine(supportRoot, "editor", "Microsoft VS Code");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "bin", "code.cmd"), "@echo off");
        var pathVariable = string.Join(Path.PathSeparator, Path.Combine(supportRoot, "editor", "missing"), Path.Combine(install, "bin"));
        var empty = Path.Combine(supportRoot, "editor", "none");
        var candidates = EditorLauncher.VisualStudioCodeCandidates(pathVariable, empty, empty).ToList();
        Check(candidates[0] == Path.Combine(install, "Code.exe"), "code on PATH points at the editor beside its bin folder");
        Check(EditorLauncher.FindVisualStudioCode(pathVariable, empty, empty) is null, "a missing Code.exe is not used");
        File.WriteAllText(Path.Combine(install, "Code.exe"), string.Empty);
        Check(EditorLauncher.FindVisualStudioCode(pathVariable, empty, empty) == Path.Combine(install, "Code.exe"), "an installed VS Code is found from PATH");
        Throws<FileNotFoundException>(() => EditorLauncher.Open(Path.Combine(supportRoot, "editor", "gone.php"), 3), "a missing file is reported instead of opening an editor");
    }

    private static void VerifyQualityToolParsing(string supportRoot)
    {
        var site = Path.Combine(supportRoot, "quality-site");
        Directory.CreateDirectory(site);

        var pint = LaravelQualityTools.ParsePint(
            "PHP Deprecated: noise\n{\"result\":\"fail\",\"files\":[{\"path\":\"app/Models/User.php\",\"fixers\":[\"no_unused_imports\",\"single_quote\"]}]}",
            site
        );
        Check(pint.Count == 1 && pint[0].Path == Path.GetFullPath(Path.Combine(site, "app", "Models", "User.php")), "Pint files resolve inside the project");
        Check(pint[0].Message == "no_unused_imports, single_quote" && pint[0].Line is null, "Pint findings list their rules");
        Check(LaravelQualityTools.ParsePint("not json", site).Count == 0, "unreadable Pint output has no findings");

        var controller = Path.Combine(site, "app", "Http", "HomeController.php");
        var phpstanJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["totals"] = new { errors = 1, file_errors = 2 },
            ["files"] = new Dictionary<string, object>
            {
                [controller] = new { errors = 2, messages = new object[] { new { message = "Undefined variable $user", line = 12 }, new { message = "No line" } } }
            },
            ["errors"] = new[] { "Ignored error pattern was not matched" }
        });
        var phpstan = LaravelQualityTools.ParsePhpStan(phpstanJson, site);
        Check(phpstan.Count == 3, "PHPStan file messages and general errors are listed");
        Check(phpstan[0] == new QualityIssue(Path.GetFullPath(controller), 12, "Undefined variable $user"), "a PHPStan message keeps its file and line");
        Check(phpstan[1].Line is null && phpstan[2].Path.Length == 0, "PHPStan messages without a line or file stay listed");

        var test = Path.Combine(site, "tests", "Feature", "ExampleTest.php");
        var vendor = Path.Combine(site, "vendor", "phpunit", "phpunit", "src", "Framework", "Assert.php");
        var output = "FAILED  Tests\\Feature\\ExampleTest > it works\n"
            + "Failed asserting that 500 is identical to 200.\n"
            + "at " + test + ":23\n"
            + vendor + ":99\n"
            + "  " + test + ":23\n"
            + "Tests:  1 failed, 4 passed\n";
        var failures = LaravelQualityTools.ParseTestFailures(output, site);
        Check(failures.Count == 1 && failures[0].Path == Path.GetFullPath(test) && failures[0].Line == 23, "a failing test points at its file and line once");
        Check(LaravelQualityTools.ParseTestFailures("Tests: 5 passed", site).Count == 0, "a passing run has no findings");

        var passed = LaravelQualityTools.Interpret(QualityTool.Pint, site, new ArtisanCommandResult(0, "{\"result\":\"pass\",\"files\":[]}", "{\"result\":\"pass\",\"files\":[]}"));
        Check(passed.Passed && passed.Issues.Count == 0, "exit code zero passes");
        var failed = LaravelQualityTools.Interpret(QualityTool.PhpStan, site, new ArtisanCommandResult(1, "At least one path must be specified", string.Empty, "At least one path must be specified"));
        Check(!failed.Passed && failed.Issues.Count == 0 && failed.Output.Contains("path", StringComparison.Ordinal), "a failure without findings keeps the output");
    }

    private static async Task VerifyQualityToolGuardsAsync(string supportRoot)
    {
        var site = Path.Combine(supportRoot, "quality-guard");
        Directory.CreateDirectory(site);
        Check(LaravelQualityTools.PhpArguments(QualityTool.Pint).Contains("--test"), "Pint only checks and never rewrites files");
        Check(LaravelQualityTools.PhpArguments(QualityTool.Pint)[0] == "vendor/bin/pint", "Pint comes from the project");
        Check(LaravelQualityTools.PhpArguments(QualityTool.PhpStan).Contains("--error-format=json"), "PHPStan reports JSON findings");
        Check(LaravelQualityTools.PhpArguments(QualityTool.Tests).Take(2).SequenceEqual(["artisan", "test"]), "tests run through artisan test");
        foreach (var tool in Enum.GetValues<QualityTool>())
        {
            Check(!LaravelQualityTools.IsAvailable(site, tool), $"{tool} is unavailable in an empty folder");
            Check(LaravelQualityTools.Timeout(tool) > TimeSpan.Zero, $"{tool} has a timeout");
        }
        var php = Path.Combine(supportRoot, "quality-php", "php.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(php)!);
        File.WriteAllText(php, string.Empty);
        await ThrowsAsync<InvalidOperationException>(
            () => LaravelQualityTools.RunAsync(QualityTool.Pint, php, site, new Dictionary<string, string>(), null, CancellationToken.None),
            "a project without Pint says how to add it instead of downloading it"
        );
        await ThrowsAsync<FileNotFoundException>(
            () => LaravelQualityTools.RunAsync(QualityTool.Tests, Path.Combine(supportRoot, "missing-php.exe"), site, new Dictionary<string, string>(), null, CancellationToken.None),
            "a missing PHP runtime is reported"
        );
        Directory.CreateDirectory(Path.Combine(site, "vendor", "bin"));
        File.WriteAllText(Path.Combine(site, "vendor", "bin", "phpstan"), "<?php");
        Check(LaravelQualityTools.IsAvailable(site, QualityTool.PhpStan), "PHPStan in vendor/bin is found");
    }

    private static void VerifyLaravelToolSources(string repositoryRoot)
    {
        var app = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var logsXaml = File.ReadAllText(Path.Combine(app, "Pages", "LogsPage.xaml"));
        foreach (var name in new[] { "LevelBox", "OpenLastErrorButton", "LevelCountsText", "LiveRefreshToggle" })
        {
            Check(logsXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal), $"Logs has {name}");
        }
        var logs = File.ReadAllText(Path.Combine(app, "Pages", "LogsPage.xaml.cs"));
        Check(logs.Contains("LaravelLog.FilterByMinimumLevel(content, level, cancellationToken)", StringComparison.Ordinal), "the level filter runs before search");
        Check(logs.Contains("EditorLauncher.Open(location.Path, location.Line)", StringComparison.Ordinal), "Open last error opens the file at its line");
        Check(logs.Contains("IsLaravelSource => SelectedSource is { IsApplication: false }", StringComparison.Ordinal), "level tools are limited to site logs");
        var sites = File.ReadAllText(Path.Combine(app, "Pages", "SitesPage.xaml"));
        Check(sites.Contains("x:Name=\"QualityMenu\"", StringComparison.Ordinal), "Sites has the Code quality menu");
        foreach (var handler in new[] { "QualityPint_Click", "QualityPhpStan_Click", "QualityTests_Click" })
        {
            Check(sites.Contains(handler, StringComparison.Ordinal), $"Sites wires {handler}");
        }
        var editor = File.ReadAllText(Path.Combine(app, "Services", "EditorLauncher.cs"));
        Check(editor.Contains("CreateNoWindow = true", StringComparison.Ordinal) && editor.Contains("UseShellExecute = false", StringComparison.Ordinal), "VS Code starts without a console window");
        var quality = File.ReadAllText(Path.Combine(app, "Services", "LaravelQualityTools.cs"));
        Check(quality.Contains("ArtisanCommandRunner.RunPhpAsync(", StringComparison.Ordinal), "quality tools share the hidden, job-bound PHP runner");
        Check(!quality.Contains("HttpClient", StringComparison.Ordinal), "quality tools never download anything");
    }
}
