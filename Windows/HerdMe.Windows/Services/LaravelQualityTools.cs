using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public enum QualityTool
{
    Pint,
    PhpStan,
    Tests
}

public sealed record QualityIssue(string Path, int? Line, string Message);

public sealed record QualityToolResult(
    QualityTool Tool,
    int ExitCode,
    bool Passed,
    IReadOnlyList<QualityIssue> Issues,
    string Output
);

// Runs the project's own Pint, PHPStan, and test suite with the site's HerdMe PHP. The tools
// come from the project's vendor folder; HerdMe does not download or install them. Pint only
// checks (--test) and never rewrites files.
public static partial class LaravelQualityTools
{
    public static readonly TimeSpan PintTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PhpStanTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TestsTimeout = TimeSpan.FromMinutes(30);
    private const int MaximumIssues = 500;

    public static string ScriptPath(string sitePath, QualityTool tool) => tool switch
    {
        QualityTool.Pint => Path.Combine(sitePath, "vendor", "bin", "pint"),
        QualityTool.PhpStan => Path.Combine(sitePath, "vendor", "bin", "phpstan"),
        _ => Path.Combine(sitePath, "artisan")
    };

    public static bool IsAvailable(string sitePath, QualityTool tool) => File.Exists(ScriptPath(sitePath, tool));

    public static IReadOnlyList<string> PhpArguments(QualityTool tool) => tool switch
    {
        QualityTool.Pint => ["vendor/bin/pint", "--test", "--format=json", "--no-interaction"],
        QualityTool.PhpStan =>
            ["vendor/bin/phpstan", "analyse", "--error-format=json", "--no-progress", "--no-interaction", "--memory-limit=1G"],
        _ => ["artisan", "test", "--colors=never", "--no-interaction"]
    };

    public static TimeSpan Timeout(QualityTool tool) => tool switch
    {
        QualityTool.Pint => PintTimeout,
        QualityTool.PhpStan => PhpStanTimeout,
        _ => TestsTimeout
    };

    public static async Task<QualityToolResult> RunAsync(
        QualityTool tool,
        string phpExecutable,
        string sitePath,
        IReadOnlyDictionary<string, string> environment,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        var projectPath = Path.GetFullPath(sitePath);
        if (!File.Exists(phpExecutable))
        {
            throw new FileNotFoundException("Install the selected HerdMe PHP runtime first.", phpExecutable);
        }
        if (!IsAvailable(projectPath, tool))
        {
            throw new InvalidOperationException(tool switch
            {
                QualityTool.Pint => "Pint is not installed in this project. Run: composer require laravel/pint --dev",
                QualityTool.PhpStan => "PHPStan is not installed in this project. Run: composer require larastan/larastan --dev",
                _ => "Tests run through Artisan, and this project has no artisan file."
            });
        }
        // JSON tools keep their output quiet so the progress pane only shows the test runner.
        var result = await ArtisanCommandRunner.RunPhpAsync(
            phpExecutable,
            projectPath,
            PhpArguments(tool),
            environment,
            Timeout(tool),
            "The selected PHP runtime could not start the tool.",
            tool switch { QualityTool.Pint => "Pint", QualityTool.PhpStan => "PHPStan", _ => "The test run" },
            tool == QualityTool.Tests ? progress : null,
            cancellationToken
        );
        return Interpret(tool, projectPath, result);
    }

    public static QualityToolResult Interpret(QualityTool tool, string sitePath, ArtisanCommandResult result)
    {
        var issues = tool switch
        {
            QualityTool.Pint => ParsePint(result.StandardOutput, sitePath),
            QualityTool.PhpStan => ParsePhpStan(result.StandardOutput, sitePath),
            _ => ParseTestFailures(result.Output, sitePath)
        };
        return new QualityToolResult(tool, result.ExitCode, result.ExitCode == 0, issues, result.Output);
    }

    // pint --test --format=json: {"result":"fail","files":[{"path":"app/User.php","fixers":["..."]}]}
    public static IReadOnlyList<QualityIssue> ParsePint(string json, string sitePath)
    {
        var issues = new List<QualityIssue>();
        if (!TryParseObject(json, out var document)) return issues;
        using (document)
        {
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return issues;
            foreach (var file in files.EnumerateArray())
            {
                if (issues.Count >= MaximumIssues) break;
                if (file.ValueKind != JsonValueKind.Object
                    || !file.TryGetProperty("path", out var path)
                    || path.ValueKind != JsonValueKind.String) continue;
                var fixers = file.TryGetProperty("fixers", out var list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()))
                    : string.Empty;
                issues.Add(new QualityIssue(Resolve(sitePath, path.GetString()!), null, fixers));
            }
        }
        return issues;
    }

    // phpstan --error-format=json: {"files":{"C:\\site\\app\\X.php":{"messages":[{"message":"...","line":12}]}},"errors":["..."]}
    public static IReadOnlyList<QualityIssue> ParsePhpStan(string json, string sitePath)
    {
        var issues = new List<QualityIssue>();
        if (!TryParseObject(json, out var document)) return issues;
        using (document)
        {
            if (document.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
            {
                foreach (var file in files.EnumerateObject())
                {
                    if (!file.Value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
                    foreach (var message in messages.EnumerateArray())
                    {
                        if (issues.Count >= MaximumIssues) return issues;
                        var text = message.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
                            ? value.GetString() ?? string.Empty
                            : string.Empty;
                        int? line = message.TryGetProperty("line", out var number) && number.ValueKind == JsonValueKind.Number
                            && number.TryGetInt32(out var parsed) && parsed > 0
                                ? parsed
                                : null;
                        issues.Add(new QualityIssue(Resolve(sitePath, file.Name), line, text));
                    }
                }
            }
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (issues.Count >= MaximumIssues) break;
                    if (error.ValueKind == JsonValueKind.String) issues.Add(new QualityIssue(string.Empty, null, error.GetString() ?? string.Empty));
                }
            }
        }
        return issues;
    }

    // PHPUnit and Pest print "path\to\SomeTest.php:23" under each failure. Only files inside the
    // project are listed, so vendor frames do not crowd out the tests.
    public static IReadOnlyList<QualityIssue> ParseTestFailures(string output, string sitePath)
    {
        var issues = new List<QualityIssue>();
        if (string.IsNullOrEmpty(output)) return issues;
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sitePath));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FileAndLine().Matches(output))
        {
            if (issues.Count >= MaximumIssues) break;
            if (!int.TryParse(match.Groups["line"].Value, out var line) || line <= 0) continue;
            var path = Resolve(sitePath, match.Groups["path"].Value.Trim());
            var normalized = path.Replace('\\', '/');
            if (!path.StartsWith(project, StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/vendor/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(path + ":" + line)) continue;
            issues.Add(new QualityIssue(path, line, LineText(output, match.Index)));
        }
        return issues;
    }

    private static string Resolve(string sitePath, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (OperatingSystem.IsWindows() || !IsWindowsRooted(path))
            {
                return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(sitePath, normalized));
            }
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsWindowsRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static string LineText(string output, int index)
    {
        var start = output.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = output.IndexOf('\n', index);
        var line = (end < 0 ? output[start..] : output[start..end]).Trim();
        return line.Length > 240 ? line[..240] + "..." : line;
    }

    private static bool TryParseObject(string json, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        // Composer or PHP notices can precede the JSON; start at the first object.
        var start = json.IndexOf('{');
        if (start < 0) return false;
        try
        {
            document = JsonDocument.Parse(json[start..]);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return true;
            document.Dispose();
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(
        @"(?<path>(?:[A-Za-z]:[\\/]|/|(?:tests|app|src|database|routes)[\\/])[^:*?""<>|\r\n()]*?\.php):(?<line>\d+)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex FileAndLine();
}
