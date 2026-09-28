using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record RecentSiteEntry(string Path, DateTimeOffset OpenedAt);

/// <summary>
/// The sites the user opened last (browser, editor, terminal, Tinker), newest first. The tray
/// menu, the tray panel and the Jump List show them above the other sites. Kept in
/// %LOCALAPPDATA%\HerdMe\Config\recent-sites.json, bounded and written atomically; only folder
/// paths and times are stored.
/// </summary>
public sealed class RecentSitesStore
{
    public const int Capacity = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync = new();
    private List<RecentSiteEntry> entries = [];
    private bool loaded;

    public RecentSitesStore(string supportRoot)
    {
        StorePath = Path.Combine(supportRoot, "Config", "recent-sites.json");
    }

    public string StorePath { get; }

    public event EventHandler? Changed;

    // Newest first.
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (sync)
            {
                EnsureLoaded();
                return entries.Select(entry => entry.Path).ToList();
            }
        }
    }

    public void Record(string path, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (sync)
        {
            EnsureLoaded();
            if (entries.Count > 0
                && string.Equals(entries[0].Path, path, StringComparison.OrdinalIgnoreCase)
                && now - entries[0].OpenedAt < TimeSpan.FromSeconds(30))
            {
                // Opening the same site again right away changes nothing worth writing.
                return;
            }
            entries = Push(entries, path, now);
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Forget(string path)
    {
        lock (sync)
        {
            EnsureLoaded();
            if (entries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return;
            }
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Moves the path to the front, drops duplicates (case-insensitive) and keeps Capacity.
    public static List<RecentSiteEntry> Push(IEnumerable<RecentSiteEntry> current, string path, DateTimeOffset now)
    {
        return current
            .Where(entry => !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(new RecentSiteEntry(path, now))
            .Take(Capacity)
            .ToList();
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            var stored = JsonSerializer.Deserialize<List<RecentSiteEntry>>(File.ReadAllText(StorePath), JsonOptions);
            entries = (stored ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .OrderByDescending(entry => entry.OpenedAt)
                .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Take(Capacity)
                .ToList();
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            entries = [];
        }
    }

    private void SaveUnlocked()
    {
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, StorePath, true);
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
