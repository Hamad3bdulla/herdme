using System.Text;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

// What the last session saw, so the next launch can paint the window at once and refresh in
// the background. Only names, paths and counts; nothing from inside a site.
public sealed class StartupSnapshotData
{
    public int SchemaVersion { get; set; } = StartupSnapshotStore.CurrentSchemaVersion;

    public DateTimeOffset SavedAt { get; set; }

    public List<SiteRecord> Sites { get; set; } = [];

    public int? ServiceCount { get; set; }

    public int? MailCount { get; set; }

    public int? DumpCount { get; set; }

    public int? HealthIssueCount { get; set; }
}

/// <summary>
/// %LOCALAPPDATA%\HerdMe\Cache\startup-snapshot.json. Written after a successful scan or
/// Dashboard refresh (only when something changed) and read once at startup. A missing,
/// unreadable, future-schema or very old snapshot is ignored, so the worst case is the old
/// behaviour: skeletons until the first scan finishes.
/// </summary>
public sealed class StartupSnapshotStore
{
    public const int CurrentSchemaVersion = 1;
    public const int SiteLimit = 500;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object sync = new();
    private StartupSnapshotData? current;
    private string? lastWritten;
    private DateTimeOffset lastWrittenAt;
    private bool loaded;

    public StartupSnapshotStore(string supportRoot)
    {
        SnapshotPath = Path.Combine(supportRoot, "Cache", "startup-snapshot.json");
    }

    public string SnapshotPath { get; }

    public StartupSnapshotData? Load(DateTimeOffset? now = null)
    {
        lock (sync)
        {
            EnsureLoaded();
            if (current is null) return null;
            if ((now ?? DateTimeOffset.UtcNow) - current.SavedAt > MaximumAge) return null;
            return Copy(current);
        }
    }

    public void SaveSites(IEnumerable<SiteRecord> sites, DateTimeOffset? now = null)
    {
        var list = sites
            .Where(site => !string.IsNullOrWhiteSpace(site.Path) && !string.IsNullOrWhiteSpace(site.Domain))
            .Take(SiteLimit)
            .Select(CopySite)
            .ToList();
        Update(snapshot => snapshot.Sites = list, now);
    }

    public void SaveCounts(int? services, int? mail, int? dumps, int? healthIssues, DateTimeOffset? now = null)
    {
        Update(snapshot =>
        {
            if (services is not null) snapshot.ServiceCount = Math.Max(0, services.Value);
            if (mail is not null) snapshot.MailCount = Math.Max(0, mail.Value);
            if (dumps is not null) snapshot.DumpCount = Math.Max(0, dumps.Value);
            if (healthIssues is not null) snapshot.HealthIssueCount = Math.Max(0, healthIssues.Value);
        }, now);
    }

    private void Update(Action<StartupSnapshotData> change, DateTimeOffset? now)
    {
        lock (sync)
        {
            EnsureLoaded();
            var next = current is null ? new StartupSnapshotData() : Copy(current);
            change(next);
            next.SchemaVersion = CurrentSchemaVersion;
            // SavedAt is left out of the comparison so an unchanged state is not rewritten.
            next.SavedAt = default;
            var body = JsonSerializer.Serialize(next, JsonOptions);
            var time = now ?? DateTimeOffset.UtcNow;
            // Unchanged state is rewritten at most once a day, only to keep it from ageing out.
            if (string.Equals(body, lastWritten, StringComparison.Ordinal) && time - lastWrittenAt < TimeSpan.FromDays(1))
            {
                next.SavedAt = lastWrittenAt;
                current = next;
                return;
            }
            next.SavedAt = time;
            current = next;
            if (Write(JsonSerializer.Serialize(next, JsonOptions)))
            {
                lastWritten = body;
                lastWrittenAt = time;
            }
        }
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(SnapshotPath)) return;
            var stored = JsonSerializer.Deserialize<StartupSnapshotData>(File.ReadAllText(SnapshotPath), JsonOptions);
            if (stored is null || stored.SchemaVersion != CurrentSchemaVersion) return;
            stored.Sites = (stored.Sites ?? [])
                .Where(site => !string.IsNullOrWhiteSpace(site.Path) && !string.IsNullOrWhiteSpace(site.Domain))
                .Take(SiteLimit)
                .ToList();
            current = stored;
            var saved = stored.SavedAt;
            stored.SavedAt = default;
            lastWritten = JsonSerializer.Serialize(stored, JsonOptions);
            stored.SavedAt = saved;
            lastWrittenAt = saved;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            current = null;
        }
    }

    private bool Write(string contents)
    {
        var temporary = SnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, SnapshotPath, true);
            return true;
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
            return false;
        }
    }

    private static StartupSnapshotData Copy(StartupSnapshotData source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        SavedAt = source.SavedAt,
        Sites = source.Sites.Select(CopySite).ToList(),
        ServiceCount = source.ServiceCount,
        MailCount = source.MailCount,
        DumpCount = source.DumpCount,
        HealthIssueCount = source.HealthIssueCount
    };

    // Only the fields the snapshot stores; a new object so nothing shares state with a page.
    public static SiteRecord CopySite(SiteRecord site) => new()
    {
        Name = site.Name,
        Path = site.Path,
        Domain = site.Domain,
        Framework = site.Framework,
        Linked = site.Linked,
        PhpVersion = site.PhpVersion,
        NodeVersion = site.NodeVersion
    };
}
