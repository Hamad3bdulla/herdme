using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
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

    public string OnboardingAfterReinstallPath => Path.Combine(
        SupportRoot,
        "Config",
        "onboarding-after-reinstall.flag"
    );

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
            // Pages load settings repeatedly on the UI thread; reuse the unchanged file text.
            var json = SettingsFileCache.ReadAllText(SettingsPath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The site settings document must be an object.");
            }
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
            if (settings.Roots is null || settings.LinkedSites is null
                || settings.Tld is null || settings.UpdateChannel is null)
            {
                throw new JsonException("The site settings contain null required fields.");
            }
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
        SettingsFileCache.Invalidate(SettingsPath);
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
        SettingsFileCache.Invalidate(SettingsPath);
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
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteDurably(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, SettingsPath, true);
        SettingsFileCache.Invalidate(SettingsPath);
    }

    private static void WriteDurably(string path, string contents)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1_024, FileOptions.WriteThrough);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(contents);
        writer.Flush();
        file.Flush(true);
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

    public void UpdateCompactMode(bool compactMode)
    {
        Update(settings => settings.CompactMode = compactMode);
    }

    public void UpdateShowNotifications(bool showNotifications)
    {
        Update(settings => settings.ShowNotifications = showNotifications);
    }

    public void UpdateActionNotifications(bool enabled)
    {
        Update(settings => settings.ActionNotifications = enabled);
    }

    public void UpdateMailNotifications(bool enabled)
    {
        Update(settings => settings.MailNotifications = enabled);
    }

    public void UpdateDefenderExclusion(string path)
    {
        var value = DefenderExclusion.IsAllowedExclusionPath(path) ? path.TrimEnd('\\', '/') : string.Empty;
        Update(settings => settings.DefenderExclusionPath = value);
    }

    public void UpdateSitesListWidth(double width)
    {
        var clamped = ClampSitesListWidth(width);
        Update(settings => settings.SitesListWidth = clamped);
    }

    public static double ClampSitesListWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width)) return WindowsSiteSettings.SitesListWidthDefault;
        return Math.Round(Math.Clamp(
            width,
            WindowsSiteSettings.SitesListWidthMinimum,
            WindowsSiteSettings.SitesListWidthMaximum
        ));
    }

    // A renamed site folder keeps its link and favorite entries.
    public void MoveSitePath(string oldPath, string newPath)
    {
        var from = Path.GetFullPath(oldPath);
        var to = Path.GetFullPath(newPath);
        Update(settings =>
        {
            for (var index = 0; index < settings.LinkedSites.Count; index++)
            {
                if (settings.LinkedSites[index].Equals(from, StringComparison.OrdinalIgnoreCase))
                    settings.LinkedSites[index] = to;
            }
            settings.FavoriteSites ??= [];
            for (var index = 0; index < settings.FavoriteSites.Count; index++)
            {
                if (settings.FavoriteSites[index].Equals(from, StringComparison.OrdinalIgnoreCase))
                    settings.FavoriteSites[index] = to;
            }
        });
    }

    public void UpdateUiLanguage(string? language)
    {
        var normalized = UiLanguageSettings.Normalize(language);
        Update(settings => settings.UiLanguage = normalized);
    }

    public void UpdateReduceMotion(bool reduceMotion)
    {
        Update(settings => settings.ReduceMotion = reduceMotion);
    }

    public void UpdateShowStatusBar(bool showStatusBar)
    {
        Update(settings => settings.ShowStatusBar = showStatusBar);
    }

    public void UpdateLastSeenVersion(string version)
    {
        Update(settings => settings.LastSeenVersion = version.Trim());
    }

    public void MarkTipSeen(string tipId)
    {
        Update(settings =>
        {
            settings.SeenTips ??= [];
            if (!settings.SeenTips.Contains(tipId, StringComparer.Ordinal)) settings.SeenTips.Add(tipId);
        });
    }

    // Returns true when the step was newly marked, so callers refresh the checklist only then.
    public bool MarkGettingStartedStep(string step)
    {
        if (!GettingStarted.IsKnownStep(step)) return false;
        var added = false;
        Update(settings =>
        {
            settings.GettingStartedSteps ??= [];
            if (settings.GettingStartedSteps.Contains(step, StringComparer.Ordinal)) return;
            settings.GettingStartedSteps.Add(step);
            added = true;
        });
        return added;
    }

    public void UpdateGettingStartedDismissed(bool dismissed)
    {
        Update(settings => settings.GettingStartedDismissed = dismissed);
    }

    public static string NormalizeTld(string? tld) =>
        (tld ?? string.Empty).Trim().Trim('.').ToLowerInvariant();

    /// <summary>One DNS label: 1-63 ASCII letters, digits or inner hyphens.</summary>
    public static bool IsValidTld(string tld) =>
        tld.Length is >= 1 and <= 63
            && tld[0] != '-'
            && tld[^1] != '-'
            && tld.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

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
        if (completed) DeleteOnboardingAfterReinstallRequest();
    }

    public bool ApplyOnboardingAfterReinstallRequest()
    {
        if (!File.Exists(OnboardingAfterReinstallPath)) return false;
        try
        {
            Update(settings => settings.OnboardingCompleted = false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep forcing onboarding for this launch; the marker remains for the next attempt.
        }
        return true;
    }

    private void DeleteOnboardingAfterReinstallRequest()
    {
        try
        {
            File.Delete(OnboardingAfterReinstallPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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

    public void ToggleFavorite(string path)
    {
        var normalized = Path.GetFullPath(path);
        Update(settings =>
        {
            settings.FavoriteSites ??= [];
            if (settings.FavoriteSites.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)) == 0)
                settings.FavoriteSites.Add(normalized);
        });
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

        var tld = NormalizeTld(settings.Tld);
        if (!IsValidTld(tld)) tld = "test";
        return new WindowsSiteSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            Roots = roots,
            LinkedSites = linkedSites,
            FavoriteSites = (settings.FavoriteSites ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tld = tld,
            StartAutomatically = settings.StartAutomatically,
            ShowPreviews = settings.ShowPreviews,
            CompactMode = settings.CompactMode,
            ShowNotifications = settings.ShowNotifications,
            ActionNotifications = settings.ActionNotifications,
            MailNotifications = settings.MailNotifications,
            DefenderExclusionPath = DefenderExclusion.IsAllowedExclusionPath(settings.DefenderExclusionPath)
                ? settings.DefenderExclusionPath.TrimEnd('\\', '/')
                : string.Empty,
            SitesListWidth = ClampSitesListWidth(settings.SitesListWidth),
            UiLanguage = UiLanguageSettings.Normalize(settings.UiLanguage),
            ReduceMotion = settings.ReduceMotion,
            ShowStatusBar = settings.ShowStatusBar,
            AutomaticUpdates = settings.AutomaticUpdates,
            UpdateChannel = settings.UpdateChannel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                ? "Beta"
                : "Stable",
            OnboardingCompleted = settings.OnboardingCompleted,
            LastSeenVersion = (settings.LastSeenVersion ?? string.Empty).Trim(),
            SeenTips = (settings.SeenTips ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal).Take(64).ToList(),
            GettingStartedSteps = GettingStarted.NormalizeSteps(settings.GettingStartedSteps),
            GettingStartedDismissed = settings.GettingStartedDismissed
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
