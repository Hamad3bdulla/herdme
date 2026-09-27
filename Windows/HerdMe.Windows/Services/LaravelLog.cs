using System.Text;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public enum LaravelLogLevel
{
    Debug,
    Info,
    Notice,
    Warning,
    Error,
    Critical,
    Alert,
    Emergency
}

// One Laravel log record: the header line and every line after it up to the next header, so a
// stack trace stays with its message. Level is null for lines before the first header, which
// happens when only the tail of a large log was read.
public sealed record LaravelLogEntry(LaravelLogLevel? Level, string Text);

public sealed record SourceLocation(string Path, int Line);

public sealed record LaravelLogSummary(int Entries, int Warnings, int Errors, SourceLocation? LastError);

// Reads the Monolog line format Laravel writes to storage\logs, for example
// [2026-09-27 10:15:02] local.ERROR: message {"exception":"[object] (... at C:\site\app\X.php:42)"}
public static partial class LaravelLog
{
    public static IReadOnlyList<LaravelLogEntry> Parse(string content, CancellationToken cancellationToken = default)
    {
        var entries = new List<LaravelLogEntry>();
        if (string.IsNullOrEmpty(content)) return entries;
        LaravelLogLevel? level = null;
        var text = new StringBuilder();
        var started = false;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = Header().Match(line);
            if (header.Success)
            {
                if (started || text.Length > 0) entries.Add(new LaravelLogEntry(level, text.ToString()));
                text.Clear();
                level = ParseLevel(header.Groups["level"].Value);
                started = true;
            }
            else if (started || text.Length > 0)
            {
                text.Append('\n');
            }
            text.Append(line);
        }
        if (started || text.Length > 0) entries.Add(new LaravelLogEntry(level, text.ToString()));
        return entries;
    }

    // Keeps whole entries at or above the level; null keeps everything as it was written.
    public static string FilterByMinimumLevel(
        string content,
        LaravelLogLevel? minimum,
        CancellationToken cancellationToken = default
    )
    {
        if (minimum is not { } floor) return content;
        var result = new StringBuilder();
        foreach (var entry in Parse(content, cancellationToken))
        {
            if (entry.Level is not { } level || level < floor) continue;
            if (result.Length > 0) result.Append('\n');
            result.Append(entry.Text);
        }
        return result.ToString();
    }

    public static LaravelLogSummary Summarize(string content, CancellationToken cancellationToken = default)
    {
        var warnings = 0;
        var errors = 0;
        var total = 0;
        SourceLocation? lastError = null;
        foreach (var entry in Parse(content, cancellationToken))
        {
            if (entry.Level is not { } level) continue;
            total++;
            if (level == LaravelLogLevel.Warning) warnings++;
            if (level < LaravelLogLevel.Error) continue;
            errors++;
            if (FindSourceLocation(entry.Text) is { } location) lastError = location;
        }
        return new LaravelLogSummary(total, warnings, errors, lastError);
    }

    // The file and line an error points at. The throw site ("... at C:\site\app\X.php:42")
    // comes first, then stack frames; project files win over vendor code and compiled views.
    public static SourceLocation? FindSourceLocation(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var candidates = new List<SourceLocation>();
        foreach (Match match in ThrowSite().Matches(text)) Add(candidates, match);
        foreach (Match match in StackFrame().Matches(text)) Add(candidates, match);
        return candidates.FirstOrDefault(candidate => !IsFrameworkPath(candidate.Path))
            ?? candidates.FirstOrDefault();
    }

    public static LaravelLogLevel? ParseLevel(string value) => value.ToUpperInvariant() switch
    {
        "DEBUG" => LaravelLogLevel.Debug,
        "INFO" => LaravelLogLevel.Info,
        "NOTICE" => LaravelLogLevel.Notice,
        "WARNING" => LaravelLogLevel.Warning,
        "ERROR" => LaravelLogLevel.Error,
        "CRITICAL" => LaravelLogLevel.Critical,
        "ALERT" => LaravelLogLevel.Alert,
        "EMERGENCY" => LaravelLogLevel.Emergency,
        _ => null
    };

    private static void Add(List<SourceLocation> candidates, Match match)
    {
        if (!int.TryParse(match.Groups["line"].Value, out var line) || line <= 0) return;
        // The exception context is JSON, so its backslashes arrive doubled.
        var path = match.Groups["path"].Value
            .Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace(@"\/", "/", StringComparison.Ordinal);
        candidates.Add(new SourceLocation(path, line));
    }

    private static bool IsFrameworkPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/vendor/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/storage/framework/", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"^\[\d{4}-\d{2}-\d{2}[ T][^\]]*\]\s+[\w.-]+\.(?<level>DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|ALERT|EMERGENCY):",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex Header();

    [GeneratedRegex(
        @"\bat\s+(?<path>(?:[A-Za-z]:[\\/]|/)[^:*?""<>|\r\n()]*?\.php):(?<line>\d+)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ThrowSite();

    [GeneratedRegex(
        @"^#\d+\s+(?<path>(?:[A-Za-z]:[\\/]|/)[^:*?""<>|\r\n()]*?\.php)\((?<line>\d+)\)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline
    )]
    private static partial Regex StackFrame();
}
