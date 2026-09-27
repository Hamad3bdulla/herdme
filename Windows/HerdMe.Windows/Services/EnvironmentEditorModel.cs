namespace HerdMe.Windows.Services;

public sealed record EnvironmentEntry(
    int LineIndex,
    int LineCount,
    string Key,
    string Value,
    bool IsSecret
)
{
    public bool IsMultiline => LineCount > 1;
}

public enum EnvironmentDiffKind
{
    Added,
    Changed,
    Removed
}

// Before/After are display text: secret values are already masked.
public sealed record EnvironmentDiffLine(EnvironmentDiffKind Kind, string Key, string Before, string After);

public enum EnvironmentProblemKind
{
    InvalidLine,
    DuplicateKey,
    UnquotedWhitespace,
    UnclosedQuote
}

public sealed record EnvironmentProblem(int LineNumber, string Key, EnvironmentProblemKind Kind);

// The per-site .env editor works on the raw text so comments, blank lines, "export" prefixes
// and the file's line endings survive every edit. Rows edit the text after "=" as written.
public static class EnvironmentEditorModel
{
    public const string MaskText = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    private static readonly string[] SecretParts =
    [
        "KEY", "PASSWORD", "PASS", "PWD", "SECRET", "TOKEN", "PRIVATE", "CREDENTIALS", "SALT", "DSN"
    ];

    // Keys offered by autocomplete in addition to the project's own .env.example.
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "APP_NAME", "APP_ENV", "APP_KEY", "APP_DEBUG", "APP_URL", "APP_TIMEZONE",
        "APP_LOCALE", "APP_FALLBACK_LOCALE", "APP_FAKER_LOCALE", "APP_MAINTENANCE_DRIVER",
        "APP_MAINTENANCE_STORE", "APP_PREVIOUS_KEYS", "PHP_CLI_SERVER_WORKERS", "BCRYPT_ROUNDS",
        "LOG_CHANNEL", "LOG_STACK", "LOG_DEPRECATIONS_CHANNEL", "LOG_LEVEL",
        "DB_CONNECTION", "DB_HOST", "DB_PORT", "DB_DATABASE", "DB_USERNAME", "DB_PASSWORD", "DB_URL",
        "SESSION_DRIVER", "SESSION_LIFETIME", "SESSION_ENCRYPT", "SESSION_PATH", "SESSION_DOMAIN",
        "BROADCAST_CONNECTION", "FILESYSTEM_DISK", "QUEUE_CONNECTION", "CACHE_STORE", "CACHE_PREFIX",
        "MEMCACHED_HOST", "REDIS_CLIENT", "REDIS_HOST", "REDIS_PASSWORD", "REDIS_PORT", "REDIS_URL",
        "MAIL_MAILER", "MAIL_SCHEME", "MAIL_HOST", "MAIL_PORT", "MAIL_USERNAME", "MAIL_PASSWORD",
        "MAIL_FROM_ADDRESS", "MAIL_FROM_NAME",
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_DEFAULT_REGION", "AWS_BUCKET",
        "AWS_URL", "AWS_ENDPOINT", "AWS_USE_PATH_STYLE_ENDPOINT",
        "VITE_APP_NAME", "SCOUT_DRIVER", "MEILISEARCH_HOST", "MEILISEARCH_KEY",
        "PUSHER_APP_ID", "PUSHER_APP_KEY", "PUSHER_APP_SECRET", "PUSHER_HOST", "PUSHER_PORT",
        "PUSHER_SCHEME", "PUSHER_APP_CLUSTER",
        "REVERB_APP_ID", "REVERB_APP_KEY", "REVERB_APP_SECRET", "REVERB_HOST", "REVERB_PORT",
        "REVERB_SCHEME", "TELESCOPE_ENABLED", "DEBUGBAR_ENABLED", "HORIZON_PREFIX"
    ];

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128) return false;
        if (!(IsAsciiLetter(key[0]) || key[0] == '_')) return false;
        return key.All(character => IsAsciiLetter(character) || char.IsAsciiDigit(character)
            || character is '_' or '.');
    }

    public static bool IsSecret(string key, string value)
    {
        var trimmed = value.Trim().Trim('"', '\'');
        if (trimmed.Length == 0 || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = key.ToUpperInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => SecretParts.Contains(part))) return true;
        // Connection URLs with credentials in them ("scheme://user:password@host").
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var at = trimmed.IndexOf('@', scheme + 3);
            var colon = trimmed.IndexOf(':', scheme + 3);
            return at > 0 && colon > 0 && colon < at;
        }
        return false;
    }

    public static string Display(string key, string value) =>
        IsSecret(key, value) ? MaskText : value;

    public static IReadOnlyList<EnvironmentEntry> Entries(string contents)
    {
        var lines = Parse(contents).Lines;
        var entries = new List<EnvironmentEntry>();
        foreach (var record in Scan(lines))
        {
            if (record.Key is null || record.Problem == EnvironmentProblemKind.UnclosedQuote) continue;
            var value = string.Join("\n", Enumerable.Range(record.LineIndex, record.LineCount)
                .Select(index => index == record.LineIndex ? Split(lines[index])!.Value.Value : lines[index]));
            entries.Add(new EnvironmentEntry(
                record.LineIndex,
                record.LineCount,
                record.Key,
                value,
                IsSecret(record.Key, value)
            ));
        }
        return entries;
    }

    public static string SetValue(string contents, int lineIndex, string value)
    {
        var parsed = Parse(contents);
        if (lineIndex < 0 || lineIndex >= parsed.Lines.Count) throw new ArgumentOutOfRangeException(nameof(lineIndex));
        var split = Split(parsed.Lines[lineIndex])
            ?? throw new InvalidOperationException("The line does not hold a variable.");
        parsed.Lines[lineIndex] = split.Prefix + split.Key + "=" + SingleLine(value);
        return Render(parsed);
    }

    // Adds KEY=value at the end, or changes the first existing KEY instead of adding a duplicate.
    public static string Add(string contents, string key, string value)
    {
        if (!IsValidKey(key)) throw new ArgumentException("The variable name is not valid.", nameof(key));
        var existing = Entries(contents).FirstOrDefault(entry => entry.Key == key && !entry.IsMultiline);
        if (existing is not null) return SetValue(contents, existing.LineIndex, value);
        var parsed = Parse(contents);
        parsed.Lines.Add(key + "=" + SingleLine(value));
        return Render(parsed with { TrailingNewline = true });
    }

    public static string Remove(string contents, int lineIndex)
    {
        var parsed = Parse(contents);
        var record = Scan(parsed.Lines).FirstOrDefault(candidate => candidate.LineIndex == lineIndex && candidate.Key is not null)
            ?? throw new ArgumentOutOfRangeException(nameof(lineIndex));
        parsed.Lines.RemoveRange(record.LineIndex, record.LineCount);
        return Render(parsed);
    }

    // Keys the project's .env.example defines that the .env does not.
    public static IReadOnlyList<string> MissingFromExample(string contents, string? example)
    {
        if (string.IsNullOrEmpty(example)) return [];
        var present = Entries(contents).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        return Entries(example)
            .Select(entry => entry.Key)
            .Where(key => !present.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // Adds the missing keys with the example's (non-secret) values under one comment.
    public static string AddMissingFromExample(string contents, string example)
    {
        var missing = MissingFromExample(contents, example);
        if (missing.Count == 0) return contents;
        var exampleValues = FirstValues(example);
        var parsed = Parse(contents);
        if (parsed.Lines.Count > 0 && parsed.Lines[^1].Trim().Length > 0) parsed.Lines.Add(string.Empty);
        parsed.Lines.Add("# Added by HerdMe from .env.example");
        foreach (var key in missing)
        {
            var value = exampleValues.TryGetValue(key, out var exampleValue) && !exampleValue.Contains('\n')
                ? exampleValue
                : string.Empty;
            parsed.Lines.Add(key + "=" + value);
        }
        return Render(parsed with { TrailingNewline = true });
    }

    public static IReadOnlyList<string> Suggestions(string text, string contents, string? example, int maximum = 12)
    {
        var query = (text ?? string.Empty).Trim();
        var present = Entries(contents).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var candidates = (example is null ? Enumerable.Empty<string>() : Entries(example).Select(entry => entry.Key))
            .Concat(KnownKeys)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !present.Contains(key))
            .ToArray();
        if (query.Length == 0) return candidates.Take(maximum).ToArray();
        return candidates
            .Where(key => key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Concat(candidates.Where(key => !key.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                && key.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(maximum)
            .ToArray();
    }

    public static IReadOnlyList<EnvironmentProblem> Problems(string contents)
    {
        var problems = new List<EnvironmentProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in Scan(Parse(contents).Lines))
        {
            if (record.Problem is { } problem)
            {
                problems.Add(new EnvironmentProblem(record.LineIndex + 1, record.Key ?? string.Empty, problem));
            }
            if (record.Key is not null && !seen.Add(record.Key))
            {
                problems.Add(new EnvironmentProblem(record.LineIndex + 1, record.Key, EnvironmentProblemKind.DuplicateKey));
            }
        }
        return problems;
    }

    // A key-level summary of what saving would change; comments and spacing are reported
    // separately by OtherLinesChanged.
    public static IReadOnlyList<EnvironmentDiffLine> Diff(string before, string after)
    {
        var old = FirstValues(before);
        var next = FirstValues(after);
        var lines = new List<EnvironmentDiffLine>();
        foreach (var (key, value) in next)
        {
            if (!old.TryGetValue(key, out var previous))
            {
                lines.Add(new EnvironmentDiffLine(EnvironmentDiffKind.Added, key, string.Empty, Display(key, value)));
            }
            else if (!string.Equals(previous, value, StringComparison.Ordinal))
            {
                lines.Add(new EnvironmentDiffLine(EnvironmentDiffKind.Changed, key, Display(key, previous), Display(key, value)));
            }
        }
        foreach (var (key, value) in old)
        {
            if (!next.ContainsKey(key))
            {
                lines.Add(new EnvironmentDiffLine(EnvironmentDiffKind.Removed, key, Display(key, value), string.Empty));
            }
        }
        return lines;
    }

    public static bool OtherLinesChanged(string before, string after)
    {
        static string[] Others(string contents)
        {
            var lines = Parse(contents).Lines;
            var owned = new HashSet<int>();
            foreach (var record in Scan(lines).Where(record => record.Key is not null))
            {
                for (var index = 0; index < record.LineCount; index++) owned.Add(record.LineIndex + index);
            }
            return lines.Where((_, index) => !owned.Contains(index)).ToArray();
        }
        return !Others(before).SequenceEqual(Others(after), StringComparer.Ordinal);
    }

    private static Dictionary<string, string> FirstValues(string contents)
    {
        // Laravel (phpdotenv) keeps the first definition of a repeated key.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries(contents)) values.TryAdd(entry.Key, entry.Value);
        return values;
    }

    private sealed record ParsedDocument(List<string> Lines, string Newline, bool TrailingNewline);

    private sealed record LineRecord(int LineIndex, int LineCount, string? Key, EnvironmentProblemKind? Problem);

    private static ParsedDocument Parse(string contents)
    {
        contents ??= string.Empty;
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal);
        var trailing = normalized.EndsWith('\n');
        if (trailing) normalized = normalized[..^1];
        var lines = normalized.Length == 0 && !trailing ? new List<string>() : normalized.Split('\n').ToList();
        return new ParsedDocument(lines, newline, trailing);
    }

    private static string Render(ParsedDocument document)
    {
        if (document.Lines.Count == 0) return string.Empty;
        return string.Join(document.Newline, document.Lines) + (document.TrailingNewline ? document.Newline : string.Empty);
    }

    private static IEnumerable<LineRecord> Scan(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var split = Split(lines[index]);
            if (split is null)
            {
                yield return new LineRecord(index, 1, null, EnvironmentProblemKind.InvalidLine);
                continue;
            }
            var value = split.Value.Value.TrimStart();
            if (value.StartsWith('"') && ClosingQuote(value, '"') < 0)
            {
                // Double-quoted values may continue on the following lines.
                var end = index + 1;
                while (end < lines.Count && ClosingQuote("\"" + lines[end], '"') < 0) end++;
                if (end >= lines.Count)
                {
                    yield return new LineRecord(index, 1, split.Value.Key, EnvironmentProblemKind.UnclosedQuote);
                    continue;
                }
                yield return new LineRecord(index, end - index + 1, split.Value.Key, null);
                index = end;
                continue;
            }
            if (value.StartsWith('\'') && ClosingQuote(value, '\'') < 0)
            {
                yield return new LineRecord(index, 1, split.Value.Key, EnvironmentProblemKind.UnclosedQuote);
                continue;
            }
            EnvironmentProblemKind? problem = null;
            if (!value.StartsWith('"') && !value.StartsWith('\''))
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                var bare = (comment >= 0 ? value[..comment] : value).Trim();
                if (bare.Any(char.IsWhiteSpace)) problem = EnvironmentProblemKind.UnquotedWhitespace;
            }
            yield return new LineRecord(index, 1, split.Value.Key, problem);
        }
    }

    private static int ClosingQuote(string value, char quote)
    {
        for (var index = 1; index < value.Length; index++)
        {
            if (quote == '"' && value[index] == '\\') { index++; continue; }
            if (value[index] == quote) return index;
        }
        return -1;
    }

    private static (string Prefix, string Key, string Value)? Split(string line)
    {
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        if (string.CompareOrdinal(line, start, "export ", 0, 7) == 0)
        {
            start += 7;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        }
        var separator = line.IndexOf('=', start);
        if (separator <= start) return null;
        var key = line[start..separator].TrimEnd();
        if (!IsValidKey(key)) return null;
        return (line[..start], key, line[(separator + 1)..]);
    }

    private static string SingleLine(string value) =>
        (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static bool IsAsciiLetter(char character) => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
