using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class SiteConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SiteConfigurationStore(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string SettingsPath => Path.Combine(SupportRoot, "Config", "sites.json");

    public WindowsSiteSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<WindowsSiteSettings>(
                    File.ReadAllText(SettingsPath)
                );
                if (settings is not null) return Normalize(settings);
            }
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
        }
        return DefaultSettings();
    }

    public void Save(WindowsSiteSettings settings)
    {
        var normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }

    public void UpdateSites(
        IEnumerable<string> roots,
        string tld,
        bool? startAutomatically = null,
        bool? showPreviews = null
    )
    {
        var current = Load();
        current.Roots = roots.ToList();
        current.Tld = tld;
        if (startAutomatically is not null) current.StartAutomatically = startAutomatically.Value;
        if (showPreviews is not null) current.ShowPreviews = showPreviews.Value;
        Save(current);
    }

    public static WindowsSiteSettings Normalize(
        WindowsSiteSettings settings,
        string? userProfile = null,
        string? localApplicationData = null,
        string? applicationData = null
    )
    {
        var roots = new List<string>();
        foreach (var path in settings.Roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var normalized = Path.GetFullPath(path.Trim());
                if (BelongsToOtherHerd(
                    normalized,
                    userProfile,
                    localApplicationData,
                    applicationData
                )) continue;
                if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase)) roots.Add(normalized);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
            }
        }
        if (roots.Count == 0) roots.Add(DefaultRoot());
        var linkedSites = new List<string>();
        foreach (var path in settings.LinkedSites.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var normalized = Path.GetFullPath(path.Trim());
                if (BelongsToOtherHerd(
                    normalized,
                    userProfile,
                    localApplicationData,
                    applicationData
                )) continue;
                if (!linkedSites.Contains(normalized, StringComparer.OrdinalIgnoreCase)) linkedSites.Add(normalized);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
            }
        }

        var tld = settings.Tld.Trim().Trim('.').ToLowerInvariant();
        if (tld.Length is < 1 or > 63
            || tld[0] == '-'
            || tld[^1] == '-'
            || tld.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            tld = "test";
        }
        return new WindowsSiteSettings
        {
            Roots = roots,
            LinkedSites = linkedSites,
            Tld = tld,
            StartAutomatically = settings.StartAutomatically,
            ShowPreviews = settings.ShowPreviews,
            AutomaticUpdates = settings.AutomaticUpdates,
            UpdateChannel = settings.UpdateChannel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                ? "Beta"
                : "Stable"
        };
    }

    public static bool BelongsToOtherHerd(
        string path,
        string? userProfile = null,
        string? localApplicationData = null,
        string? applicationData = null
    )
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            var roots = new[]
            {
                Path.Combine(
                    userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Herd"
                ),
                Path.Combine(
                    localApplicationData ?? Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData
                    ),
                    "Herd"
                ),
                Path.Combine(
                    applicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Herd"
                )
            };
            return roots.Any(root => IsSameOrChild(normalized, root));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static WindowsSiteSettings DefaultSettings()
    {
        return new WindowsSiteSettings { Roots = [DefaultRoot()] };
    }

    private static string DefaultRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HerdMe");
    }
}
