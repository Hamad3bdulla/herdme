using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public enum LogLineLevel
{
    Error,
    Warning,
    Debug
}

public readonly record struct LogLineSpan(int Start, int Length, int Line, LogLineLevel Level);

// Finds error, warning and debug lines in displayed log text so the page can colour them
// and jump between errors. Works on Laravel, PHP, nginx, Apache and HerdMe logs.
public static partial class LogHighlighting
{
    public const int MaximumSpans = 4_000;

    public static IReadOnlyList<LogLineSpan> Scan(string text, int maximumSpans = MaximumSpans)
    {
        var spans = new List<LogLineSpan>();
        var start = 0;
        var line = 0;
        LogLineLevel? entryLevel = null;
        while (start <= text.Length && spans.Count < maximumSpans)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var length = end - start;
            if (length > 0 && text[end - 1] == '\r') length--;
            var content = text.AsSpan(start, length);
            var level = Classify(content);
            if (level is null && entryLevel == LogLineLevel.Error && IsContinuation(content))
            {
                // Stack traces belong to the error above them.
                level = LogLineLevel.Error;
            }
            else if (level is not null || StartsEntry(content))
            {
                entryLevel = level;
            }
            if (level is { } found && length > 0) spans.Add(new LogLineSpan(start, length, line, found));
            if (end >= text.Length) break;
            start = end + 1;
            line++;
        }
        return spans;
    }

    public static LogLineLevel? Classify(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return null;
        var text = line.Length > 400 ? line[..400].ToString() : line.ToString();
        if (LaravelLevel().Match(text) is { Success: true } laravel)
        {
            return laravel.Groups[1].Value.ToUpperInvariant() switch
            {
                "ERROR" or "CRITICAL" or "ALERT" or "EMERGENCY" => LogLineLevel.Error,
                "WARNING" or "NOTICE" => LogLineLevel.Warning,
                "DEBUG" => LogLineLevel.Debug,
                _ => null
            };
        }
        if (ErrorWord().IsMatch(text)) return LogLineLevel.Error;
        if (WarningWord().IsMatch(text)) return LogLineLevel.Warning;
        if (DebugWord().IsMatch(text)) return LogLineLevel.Debug;
        return null;
    }

    private static bool IsContinuation(ReadOnlySpan<char> line) =>
        line.IsEmpty
        || char.IsWhiteSpace(line[0])
        || line[0] == '#'
        || line.StartsWith("Stack trace", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("[stacktrace]", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("\"}", StringComparison.Ordinal);

    private static bool StartsEntry(ReadOnlySpan<char> line) =>
        !line.IsEmpty && (line[0] == '[' || char.IsDigit(line[0]));

    [GeneratedRegex("^\\[[^\\]]{8,40}\\]\\s+[A-Za-z0-9_-]+\\.([A-Z]+):", RegexOptions.None, 250)]
    private static partial Regex LaravelLevel();

    [GeneratedRegex("\\b(error|fatal|exception|critical|crit|emerg|alert|failed)\\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex ErrorWord();

    [GeneratedRegex("\\b(warn|warning|notice|deprecated)\\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex WarningWord();

    [GeneratedRegex("\\b(debug|trace)\\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex DebugWord();
}
