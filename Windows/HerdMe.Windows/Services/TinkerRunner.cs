using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record TinkerResult(int ExitCode, string Output, TimeSpan Elapsed);

/// <summary>
/// Builds the PHP files for one Tinker run. The user's code runs in its own file inside a closure
/// so a top-level "return" becomes the printed result, and its line numbers match the editor.
/// Leading "use" imports are hoisted in front of the closure on the same first line.
/// </summary>
public static partial class TinkerScript
{
    public const int MaximumCodeCharacters = 16 * 1_024;

    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var text = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.Length > MaximumCodeCharacters)
        {
            throw new ArgumentException(
                ServiceText.Get("TinkerCodeTooLong", "Tinker code is limited to 16 KB."),
                nameof(code)
            );
        }
        if (text.Contains('\0'))
        {
            throw new ArgumentException(
                ServiceText.Get("TinkerCodeInvalid", "The code contains an unsupported character."),
                nameof(code)
            );
        }
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<?php", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the line break so the remaining lines keep their numbers.
            var skipped = text.Length - trimmed.Length + 5;
            text = new string('\n', text[..skipped].Count(character => character == '\n')) + text[skipped..];
        }
        var end = text.TrimEnd();
        if (end.EndsWith("?>", StringComparison.Ordinal)) text = end[..^2];
        if (text.Trim().Length == 0)
        {
            throw new ArgumentException(
                ServiceText.Get("TinkerCodeEmpty", "Enter some PHP code to run."),
                nameof(code)
            );
        }
        return text;
    }

    /// <summary>The file the user's code lives in, starting on line 1.</summary>
    public static string BuildCodeFile(string code)
    {
        var lines = Normalize(code).Split('\n');
        var imports = new List<string>();
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                index++;
                continue;
            }
            if (!UseImport().IsMatch(line)) break;
            imports.Add(line);
            lines[index] = string.Empty;
            index++;
        }
        var builder = new StringBuilder("<?php ");
        foreach (var import in imports) builder.Append(import).Append(' ');
        builder.Append("return (static function () use ($app) { ");
        builder.Append(lines[0]);
        for (var line = 1; line < lines.Length; line++) builder.Append('\n').Append(lines[line]);
        builder.Append("\n})();\n");
        return builder.ToString();
    }

    /// <summary>Boots the project (Composer autoload, Laravel when present), then runs the code file.</summary>
    public static string BuildRunner(string projectDirectory, string codeFile)
    {
        return "<?php\n"
            + "$__herdmeRoot = " + PhpString(Path.GetFullPath(projectDirectory)) + ";\n"
            + "$__herdmeCode = " + PhpString(Path.GetFullPath(codeFile)) + ";\n"
            + """
            chdir($__herdmeRoot);
            $app = null;
            try {
                if (is_file($__herdmeRoot . '/vendor/autoload.php')) {
                    require $__herdmeRoot . '/vendor/autoload.php';
                }
                if (is_file($__herdmeRoot . '/artisan') && is_file($__herdmeRoot . '/bootstrap/app.php')) {
                    $app = require $__herdmeRoot . '/bootstrap/app.php';
                    $app->make(\Illuminate\Contracts\Console\Kernel::class)->bootstrap();
                }
                $__herdmeResult = (static function (string $__herdmeFile, $app) {
                    return require $__herdmeFile;
                })($__herdmeCode, $app);
                if ($__herdmeResult !== null) {
                    if (function_exists('dump')) {
                        dump($__herdmeResult);
                    } else {
                        var_export($__herdmeResult);
                        echo PHP_EOL;
                    }
                }
            } catch (\Throwable $__herdmeError) {
                $__herdmeLine = $__herdmeError->getFile() === $__herdmeCode
                    ? 'line ' . $__herdmeError->getLine()
                    : $__herdmeError->getFile() . ':' . $__herdmeError->getLine();
                fwrite(STDERR, get_class($__herdmeError) . ': ' . $__herdmeError->getMessage() . ' (' . $__herdmeLine . ')' . PHP_EOL);
                exit(1);
            }

            """;
    }

    internal static string PhpString(string value)
    {
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex(@"^use\s+(function\s+|const\s+)?\\?[A-Za-z_][A-Za-z0-9_\\]*(\s*\{[A-Za-z0-9_\\,\s]*\})?(\s+as\s+[A-Za-z_][A-Za-z0-9_]*)?(\s*,\s*\\?[A-Za-z_][A-Za-z0-9_\\]*(\s+as\s+[A-Za-z_][A-Za-z0-9_]*)?)*\s*;$")]
    private static partial Regex UseImport();
}

/// <summary>
/// Runs a Tinker snippet with the site's PHP. Files are written under %LOCALAPPDATA%\HerdMe\Tinker
/// and removed after the run; nothing is written into the project.
/// </summary>
public sealed class TinkerRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public TinkerRunner()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe",
            "Tinker"
        ))
    {
    }

    public TinkerRunner(string workDirectory)
    {
        WorkDirectory = Path.GetFullPath(workDirectory);
    }

    public string WorkDirectory { get; }

    public async Task<TinkerResult> RunAsync(
        string phpExecutable,
        string projectDirectory,
        string code,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        IProgress<string>? outputProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        var projectPath = Path.GetFullPath(projectDirectory);
        if (!File.Exists(phpExecutable))
        {
            throw new FileNotFoundException(
                ServiceText.Get("TinkerPhpMissing", "Install the selected HerdMe PHP runtime first."),
                phpExecutable
            );
        }
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException(
                ServiceText.Get("TinkerProjectMissing", "The site folder no longer exists.")
            );
        }
        var codeFile = TinkerScript.BuildCodeFile(code);
        var runDirectory = Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var codePath = Path.Combine(runDirectory, "code.php");
            var runnerPath = Path.Combine(runDirectory, "runner.php");
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(codePath, codeFile, utf8, cancellationToken);
            await File.WriteAllTextAsync(
                runnerPath,
                TinkerScript.BuildRunner(projectPath, codePath),
                utf8,
                cancellationToken
            );
            var result = await ArtisanCommandRunner.RunPhpAsync(
                phpExecutable,
                projectPath,
                ["-d", "display_errors=stderr", runnerPath],
                environment,
                timeout,
                ServiceText.Get("TinkerStartFailed", "The selected PHP runtime could not start."),
                ServiceText.Get("TinkerTimeoutSubject", "The Tinker code"),
                outputProgress,
                cancellationToken
            );
            return new TinkerResult(result.ExitCode, result.Output, stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    /// <summary>Removes leftovers from runs that ended with the app (for example a crash).</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(WorkDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(WorkDirectory))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1)) TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A PHP process that is still exiting can hold the files; CleanUp removes them later.
        }
    }
}

/// <summary>The last Tinker snippets, newest first, in %LOCALAPPDATA%\HerdMe\tinker-history.json.</summary>
public sealed class TinkerHistoryStore
{
    public const int MaximumEntries = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TinkerHistoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe",
            "tinker-history.json"
        ))
    {
    }

    public TinkerHistoryStore(string path)
    {
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry) && entry.Length <= TinkerScript.MaximumCodeCharacters)
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumEntries)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> Add(string code)
    {
        var entry = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var entries = Load().Where(existing => !string.Equals(existing, entry, StringComparison.Ordinal)).ToList();
        if (entry.Length > 0 && entry.Length <= TinkerScript.MaximumCodeCharacters) entries.Insert(0, entry);
        if (entries.Count > MaximumEntries) entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
        return entries;
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
