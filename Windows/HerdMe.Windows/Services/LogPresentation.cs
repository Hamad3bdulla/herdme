namespace HerdMe.Windows.Services;

public static class LogPresentation
{
    public static string SiteLogRoot(string sitePath)
    {
        return Path.Combine(Path.GetFullPath(sitePath), "storage", "logs");
    }

    public static string FilterLines(string content, string? query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = query?.Trim() ?? string.Empty;
        if (value.Length == 0) return content;

        var result = new System.Text.StringBuilder();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Contains(value, StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Length > 0) result.Append('\n');
            result.Append(line);
        }
        return result.ToString();
    }
}
