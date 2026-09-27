namespace HerdMe.Windows.Services;

public enum SiteNameProblem
{
    None,
    Empty,
    TooLong,
    InvalidCharacters,
    EdgeHyphen,
    Unchanged,
    Taken,
    FolderExists
}

/// <summary>
/// Inline rename of a site. The domain is the folder name (the core uses it as the DNS label
/// when it is already valid), so renaming the site renames its folder; the link and favorite
/// entries follow the new path. Per-site files (.herdme-php, .herdme-node) live inside the
/// folder and move with it.
/// </summary>
public static class SiteRenamer
{
    public const int MaximumLabelLength = 63;

    // "Blog.test" and " blog " both mean "blog".
    public static string Normalize(string? candidate, string tld)
    {
        var value = (candidate ?? string.Empty).Trim().ToLowerInvariant();
        var suffix = "." + tld.Trim().Trim('.').ToLowerInvariant();
        if (suffix.Length > 1 && value.EndsWith(suffix, StringComparison.Ordinal))
        {
            value = value[..^suffix.Length];
        }
        return value;
    }

    public static SiteNameProblem Validate(
        string label,
        string currentDomain,
        string tld,
        IEnumerable<string> takenDomains,
        bool targetFolderExists
    )
    {
        if (label.Length == 0) return SiteNameProblem.Empty;
        if (label.Length > MaximumLabelLength) return SiteNameProblem.TooLong;
        if (label.Any(character => !IsLabelCharacter(character))) return SiteNameProblem.InvalidCharacters;
        if (label[0] == '-' || label[^1] == '-') return SiteNameProblem.EdgeHyphen;
        var domain = $"{label}.{tld.Trim().Trim('.').ToLowerInvariant()}";
        if (domain.Equals(currentDomain, StringComparison.OrdinalIgnoreCase)) return SiteNameProblem.Unchanged;
        if (takenDomains.Any(taken => taken.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            return SiteNameProblem.Taken;
        }
        return targetFolderExists ? SiteNameProblem.FolderExists : SiteNameProblem.None;
    }

    public static string MessageKey(SiteNameProblem problem) => problem switch
    {
        SiteNameProblem.Empty => "SitesRenameEmpty",
        SiteNameProblem.TooLong => "SitesRenameTooLong",
        SiteNameProblem.InvalidCharacters => "SitesRenameInvalidCharacters",
        SiteNameProblem.EdgeHyphen => "SitesRenameEdgeHyphen",
        SiteNameProblem.Unchanged => "SitesRenameUnchanged",
        SiteNameProblem.Taken => "SitesRenameTaken",
        SiteNameProblem.FolderExists => "SitesRenameFolderExists",
        _ => "SitesRenameAvailable"
    };

    public static string TargetPath(string sitePath, string label)
    {
        var full = Path.GetFullPath(sitePath).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("The site folder has no parent folder.");
        return Path.Combine(parent, label);
    }

    // A case-only rename (Blog -> blog) goes through a temporary name, which Windows needs.
    public static void MoveFolder(string from, string to)
    {
        var source = Path.GetFullPath(from).TrimEnd('\\', '/');
        var target = Path.GetFullPath(to).TrimEnd('\\', '/');
        if (source.Equals(target, StringComparison.Ordinal)) return;
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = source + ".herdme-rename-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(source, temporary);
            Directory.Move(temporary, target);
            return;
        }
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException(ServiceText.Get("SitesRenameFolderExists", "A folder with that name already exists."));
        }
        Directory.Move(source, target);
    }

    private static bool IsLabelCharacter(char character) =>
        character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
}
