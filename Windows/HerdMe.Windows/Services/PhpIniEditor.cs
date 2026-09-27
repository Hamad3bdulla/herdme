namespace HerdMe.Windows.Services;

public enum PhpIniProblem
{
    None,
    MissingRequiredExtension,
    TooLarge
}

/// <summary>
/// The php.ini editor on the PHP page. Search is plain text (case-insensitive); saving keeps
/// the previous file as php.ini.bak and replaces the file atomically so a crash never leaves
/// half a php.ini. A file that drops an extension Laravel needs is refused, because the runtime
/// would restore it on the next start anyway.
/// </summary>
public static class PhpIniEditor
{
    public const int MaximumLength = 1024 * 1024;

    public static string ConfigurationPath(string runtimeRoot, string cycle) =>
        Path.Combine(runtimeRoot, cycle, "php.ini");

    public static string BackupPath(string configurationPath) => configurationPath + ".bak";

    public static IReadOnlyList<int> FindMatches(string? text, string? query)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return matches;
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            matches.Add(found);
            index = found + Math.Max(1, query.Length);
        }
        return matches;
    }

    // Next (or previous) match relative to the caret, wrapping at either end.
    public static int NextMatch(IReadOnlyList<int> matches, int caret, bool forward)
    {
        if (matches.Count == 0) return -1;
        if (forward)
        {
            foreach (var match in matches)
            {
                if (match > caret) return match;
            }
            return matches[0];
        }
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            if (matches[index] < caret) return matches[index];
        }
        return matches[^1];
    }

    // explicitlyDisabled: extensions the user turned off on the PHP page, which may be absent.
    public static PhpIniProblem Validate(string text, IReadOnlySet<string>? explicitlyDisabled = null)
    {
        if (text.Length > MaximumLength) return PhpIniProblem.TooLarge;
        var temporary = Path.Combine(Path.GetTempPath(), "herdme-php-ini-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            File.WriteAllText(temporary, NormalizeLineEndings(text));
            return PhpRuntimeInstaller.HasRequiredConfiguration(
                    temporary,
                    explicitlyDisabled ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                )
                ? PhpIniProblem.None
                : PhpIniProblem.MissingRequiredExtension;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string MessageKey(PhpIniProblem problem) => problem switch
    {
        PhpIniProblem.MissingRequiredExtension => "PhpIniMissingRequired",
        PhpIniProblem.TooLarge => "PhpIniTooLarge",
        _ => "PhpIniSaved"
    };

    // Windows line endings, as php.ini ships.
    public static string NormalizeLineEndings(string text) =>
        text.ReplaceLineEndings("\r\n");

    public static void SaveAtomically(string configurationPath, string text)
    {
        var directory = Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("php.ini has no folder.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var temporary = configurationPath + ".tmp";
        File.WriteAllText(temporary, NormalizeLineEndings(text));
        if (File.Exists(configurationPath))
        {
            File.Copy(configurationPath, BackupPath(configurationPath), overwrite: true);
        }
        File.Move(temporary, configurationPath, overwrite: true);
    }
}
