using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public enum AutoInstallMode
{
    Off,
    Idle,
    OnExit
}

/// <summary>
/// What the user chose on the Updates page: versions to skip, "remind me in a week", major
/// lines to stay on, automatic installation of component updates, and when the quiet daily
/// check ran last. Kept in %LOCALAPPDATA%\HerdMe\Config\update-preferences.json.
/// </summary>
public sealed class UpdatePreferences
{
    // Component id (or "herdme") -> the version the user skipped. A newer version shows again.
    public Dictionary<string, string> SkippedVersions { get; set; } = [];

    // Component id (or "herdme") -> hidden until this time.
    public Dictionary<string, DateTimeOffset> SnoozedUntil { get; set; } = [];

    // Component id -> the major version it stays on ("8" keeps MySQL 8.x).
    public Dictionary<string, string> PinnedLines { get; set; } = [];

    public string AutoInstall { get; set; } = nameof(AutoInstallMode.Off);

    public DateTimeOffset? LastBackgroundCheck { get; set; }

    // Set right before "Restart to update" runs setup; the next start records the outcome in
    // the update history and clears it.
    public string? PendingApplicationVersion { get; set; }

    public string? PendingApplicationFrom { get; set; }

    public AutoInstallMode AutoInstallMode =>
        Enum.TryParse<AutoInstallMode>(AutoInstall, ignoreCase: true, out var mode) ? mode : AutoInstallMode.Off;
}

public sealed record CachedUpdateFailure(string Component, string Reason);

/// <summary>
/// The last check's result, so opening the Updates page shows it at once instead of asking
/// every server again. Kept in %LOCALAPPDATA%\HerdMe\Cache\update-check.json.
/// </summary>
public sealed record UpdateCheckCache(
    DateTimeOffset CheckedAt,
    IReadOnlyList<ManagedComponentUpdate> Updates,
    IReadOnlyList<CachedUpdateFailure> Failures,
    AppUpdateRelease? ApplicationRelease,
    bool ApplicationUnavailable
);

public sealed class UpdatePreferencesStore
{
    public const string ApplicationId = "herdme";
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object sync = new();
    private UpdatePreferences? preferences;

    public UpdatePreferencesStore(string supportRoot)
    {
        PreferencesPath = Path.Combine(supportRoot, "Config", "update-preferences.json");
        CachePath = Path.Combine(supportRoot, "Cache", "update-check.json");
    }

    public string PreferencesPath { get; }

    public string CachePath { get; }

    public event EventHandler? Changed;

    public UpdatePreferences Load()
    {
        lock (sync)
        {
            preferences ??= Read<UpdatePreferences>(PreferencesPath) ?? new UpdatePreferences();
            return Copy(preferences);
        }
    }

    public void Update(Action<UpdatePreferences> change)
    {
        lock (sync)
        {
            var current = Copy(preferences ??= Read<UpdatePreferences>(PreferencesPath) ?? new UpdatePreferences());
            change(current);
            preferences = current;
            Write(PreferencesPath, current);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Skip(string id, string version) => Update(value =>
    {
        value.SkippedVersions[id] = version;
        value.SnoozedUntil.Remove(id);
    });

    public void Snooze(string id, DateTimeOffset now) => Update(value => value.SnoozedUntil[id] = now + SnoozeFor);

    public void Pin(string id, string line) => Update(value => value.PinnedLines[id] = line);

    public void Unpin(string id) => Update(value => value.PinnedLines.Remove(id));

    // "Show again" for a skipped or snoozed update.
    public void Unhide(string id) => Update(value =>
    {
        value.SkippedVersions.Remove(id);
        value.SnoozedUntil.Remove(id);
    });

    public void SetAutoInstall(AutoInstallMode mode) => Update(value => value.AutoInstall = mode.ToString());

    public void MarkBackgroundCheck(DateTimeOffset now) => Update(value => value.LastBackgroundCheck = now);

    public UpdateCheckCache? LoadCache()
    {
        lock (sync) return Read<UpdateCheckCache>(CachePath);
    }

    public void SaveCache(UpdateCheckCache cache)
    {
        lock (sync) Write(CachePath, cache);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // A finished update leaves the cache; the next check would not list it either.
    public void ForgetCachedUpdate(string id)
    {
        lock (sync)
        {
            var cache = Read<UpdateCheckCache>(CachePath);
            if (cache is null) return;
            Write(CachePath, cache with
            {
                Updates = cache.Updates
                    .Where(update => !update.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsFresh(UpdateCheckCache? cache, DateTimeOffset now) =>
        cache is not null && now - cache.CheckedAt < FreshFor && cache.CheckedAt <= now;

    public static bool BackgroundCheckDue(UpdatePreferences value, DateTimeOffset now) =>
        value.LastBackgroundCheck is not { } last || now - last >= BackgroundInterval || last > now;

    // Skipped (this exact version) or snoozed (until the week is over).
    public static bool IsHidden(string id, string latestVersion, UpdatePreferences value, DateTimeOffset now)
    {
        if (value.SkippedVersions.TryGetValue(id, out var skipped)
            && string.Equals(skipped, latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return value.SnoozedUntil.TryGetValue(id, out var until) && until > now;
    }

    // The resolvers only return the newest release, so an update outside the pinned major
    // line is held back instead of offered.
    public static bool IsHeldByPin(ManagedComponentUpdate update, UpdatePreferences value) =>
        value.PinnedLines.TryGetValue(update.Id, out var line)
        && !string.Equals(MajorOf(update.LatestVersion), line, StringComparison.OrdinalIgnoreCase);

    public static bool IsOffered(ManagedComponentUpdate update, UpdatePreferences value, DateTimeOffset now) =>
        !IsHidden(update.Id, update.LatestVersion, value, now) && !IsHeldByPin(update, value);

    // PHP and Node rows are already one line each (php:8.3, node:22); services and tools can
    // move to a new major version, so those can be pinned.
    public static bool CanPin(string id) =>
        id.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
        || id is "git" or "composer" or "laravel-installer";

    public static string MajorOf(string version)
    {
        var normalized = RuntimeVersionComparison.Normalize(version);
        var end = normalized.IndexOfAny(['.', '-', '+']);
        return end < 0 ? normalized : normalized[..end];
    }

    public static bool IsMajorChange(string from, string to) =>
        !string.Equals(MajorOf(from), MajorOf(to), StringComparison.OrdinalIgnoreCase);

    private static UpdatePreferences Copy(UpdatePreferences value) => new()
    {
        SkippedVersions = new(value.SkippedVersions ?? [], StringComparer.OrdinalIgnoreCase),
        SnoozedUntil = new(value.SnoozedUntil ?? [], StringComparer.OrdinalIgnoreCase),
        PinnedLines = new(value.PinnedLines ?? [], StringComparer.OrdinalIgnoreCase),
        AutoInstall = value.AutoInstall ?? nameof(AutoInstallMode.Off),
        LastBackgroundCheck = value.LastBackgroundCheck,
        PendingApplicationVersion = value.PendingApplicationVersion,
        PendingApplicationFrom = value.PendingApplicationFrom
    };

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
