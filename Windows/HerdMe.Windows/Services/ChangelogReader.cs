using System.Text;

namespace HerdMe.Windows.Services;

public sealed record ChangelogGroup(string Title, IReadOnlyList<string> Items);

// Reads one "## [x.y.z]" section of the bundled CHANGELOG.md for the What's new dialog.
// Pure and tolerant: unknown markdown is skipped, wrapped bullet lines are joined.
public static class ChangelogReader
{
    public const string FileName = "CHANGELOG.md";

    public static IReadOnlyList<ChangelogGroup> Section(string markdown, string version)
    {
        var groups = new List<ChangelogGroup>();
        var inSection = false;
        string? title = null;
        var items = new List<string>();
        StringBuilder? item = null;

        void FlushItem()
        {
            if (item is null) return;
            var text = item.ToString().Trim();
            if (text.Length > 0) items.Add(text);
            item = null;
        }

        void FlushGroup()
        {
            FlushItem();
            if (items.Count > 0) groups.Add(new ChangelogGroup(title ?? string.Empty, items.ToArray()));
            items.Clear();
        }

        foreach (var raw in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection) break;
                inSection = line.StartsWith("## [" + version + "]", StringComparison.Ordinal);
                continue;
            }
            if (!inSection) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushGroup();
                title = line[4..].Trim();
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushItem();
                item = new StringBuilder(trimmed[2..].Trim());
            }
            else if (trimmed.Length == 0)
            {
                FlushItem();
            }
            else if (item is not null)
            {
                item.Append(' ').Append(trimmed);
            }
        }
        FlushGroup();
        return groups;
    }

    // True once per upgrade: never on a first install, never twice for the same version.
    public static bool ShouldShow(string? lastSeenVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(lastSeenVersion)) return false;
        if (!Version.TryParse(lastSeenVersion, out var last) || !Version.TryParse(currentVersion, out var current))
        {
            return !string.Equals(lastSeenVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        }
        return current > last;
    }
}
