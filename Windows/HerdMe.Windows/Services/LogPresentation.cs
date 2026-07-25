namespace HerdMe.Windows.Services;

public static class LogPresentation
{
    public static string FilterLines(string content, string? query)
    {
        var value = query?.Trim() ?? string.Empty;
        if (value.Length == 0) return content;

        return string.Join("\n", content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => line.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }
}
