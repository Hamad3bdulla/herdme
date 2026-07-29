using System.Collections.Concurrent;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class SiteConfigurationStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SettingsLocks = new(
        StringComparer.OrdinalIgnoreCase
    );

    public SiteConfigurationStore(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string SettingsPath => Path.Combine(SupportRoot, "Config", "sites.json");

    public string? LastLoadWarning { get; private set; }

    public string? LastBackupPath { get; private set; }

    public WindowsSiteSettings Load()
    {
        lock (SettingsLock()) return LoadUnlocked();
    }

    private WindowsSiteSettings LoadUnlocked()
    {
        if (!File.Exists(SettingsPath)) return DefaultSettings();
        LastLoadWarning = null;
        LastBackupPath = null;
        try
        {
            var json = File.ReadAllText(SettingsPath);
            using var document = JsonDocument.Parse(json);
            var sourceSchemaVersion = 0;
            if (document.RootElement.TryGetProperty(
                nameof(WindowsSiteSettings.SchemaVersion),
                out var schemaElement
            ))
            {
                if (schemaElement.ValueKind != JsonValueKind.Number
                    || !schemaElement.TryGetInt32(out sourceSchemaVersion)
                    || sourceSchemaVersion < 0)
                {
                    throw new JsonException("The site settings schema version is invalid.");
                }
            }
            if (sourceSchemaVersion > CurrentSchemaVersion)
            {
                PreserveUnsupportedSettings(sourceSchemaVersion);
                return DefaultSettings();
            }
            var settings = JsonSerializer.Deserialize<WindowsSiteSettings>(json)
                ?? throw new JsonException("The site settings document is empty.");
            if (!document.RootElement.TryGetProperty(
                nameof(WindowsSiteSettings.OnboardingCompleted),
                out _
            ))
            {
                // Settings written before the wizard belong to an existing installation.
                settings.OnboardingCompleted = true;
            }
            var normalized = Normalize(settings);
            if (sourceSchemaVersion < CurrentSchemaVersion) SaveUnlocked(normalized);
            return normalized;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            PreserveUnreadableSettings();
        }
        return DefaultSettings();
    }

    private void PreserveUnreadableSettings()
    {
        var backupPath = Path.Combine(
            Path.GetDirectoryName(SettingsPath)!,
            $"sites.corrupt-{Guid.NewGuid():N}.json"
        );
        try
        {
            File.Move(SettingsPath, backupPath);
            LastBackupPath = backupPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            backupPath = SettingsPath;
        }
        LastLoadWarning =
            $"HerdMe could not read its site settings. The original file was preserved at {backupPath}. No replacement settings were saved.";
    }

    private void PreserveUnsupportedSettings(int schemaVersion)
    {
        var backupPath = Path.Combine(
            Path.GetDirectoryName(SettingsPath)!,
            $"sites.unsupported-v{schemaVersion}-{Guid.NewGuid():N}.json"
        );
        try
        {
            File.Move(SettingsPath, backupPath);
            LastBackupPath = backupPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            backupPath = SettingsPath;
        }
        LastLoadWarning =
            $"HerdMe did not replace site settings created by a newer release (schema {schemaVersion}). The original file was preserved at {backupPath}. Update HerdMe before restoring it.";
    }

    public void Save(WindowsSiteSettings settings)
    {
        lock (SettingsLock()) SaveUnlocked(settings);
    }

    private void SaveUnlocked(WindowsSiteSettings settings)
    {
        if (settings.SchemaVersion is < 0 or > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported site settings schema {settings.SchemaVersion}; this release supports up to {CurrentSchemaVersion}."
            );
        }
        var normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }

    public void UpdateRoots(IEnumerable<string> roots)
    {
        var rootsSnapshot = roots.ToList();
        Update(current => current.Roots = rootsSnapshot);
    }

    public void UpdateShowPreviews(bool showPreviews)
    {
        Update(settings => settings.ShowPreviews = showPreviews);
    }

    public void UpdateTld(string tld)
    {
        Update(settings => settings.Tld = tld);
    }

    public void UpdateStartAutomatically(bool startAutomatically)
    {
        Update(settings => settings.StartAutomatically = startAutomatically);
    }

    public void UpdateUpdatePreferences(bool automaticUpdates, string updateChannel)
    {
        Update(settings =>
        {
            settings.AutomaticUpdates = automaticUpdates;
            settings.UpdateChannel = updateChannel;
        });
    }

    public void UpdateOnboardingCompleted(bool completed)
    {
        Update(settings => settings.OnboardingCompleted = completed);
    }

    public bool AddLinkedSite(string path)
    {
        var added = false;
        Update(settings =>
        {
            if (settings.LinkedSites.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            settings.LinkedSites.Add(path);
            added = true;
        });
        return added;
    }

    public void RemoveLinkedSite(string path)
    {
        Update(settings => settings.LinkedSites.RemoveAll(candidate => candidate.Equals(
            path,
            StringComparison.OrdinalIgnoreCase
        )));
    }

    private void Update(Action<WindowsSiteSettings> update)
    {
        lock (SettingsLock())
        {
            var current = LoadUnlocked();
            update(current);
            SaveUnlocked(current);
        }
    }

    private object SettingsLock()
    {
        return SettingsLocks.GetOrAdd(Path.GetFullPath(SettingsPath), static _ => new object());
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
            SchemaVersion = CurrentSchemaVersion,
            Roots = roots,
            LinkedSites = linkedSites,
            Tld = tld,
            StartAutomatically = settings.StartAutomatically,
            ShowPreviews = settings.ShowPreviews,
            AutomaticUpdates = settings.AutomaticUpdates,
            UpdateChannel = settings.UpdateChannel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                ? "Beta"
                : "Stable",
            OnboardingCompleted = settings.OnboardingCompleted
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
        return new WindowsSiteSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            Roots = [DefaultRoot()],
            StartAutomatically = true
        };
    }

    private static string DefaultRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HerdMe");
    }
}
