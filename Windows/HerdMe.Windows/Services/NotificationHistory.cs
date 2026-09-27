using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerdMe.Windows.Services;

public enum NotificationSeverity
{
    Information,
    Warning,
    Error
}

// One remembered notification. Action is a NotificationActions.Build string (or null) and is
// parsed again, through the same allow-list, when the entry is clicked.
public sealed record NotificationHistoryEntry
{
    public string Id { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public DateTimeOffset Time { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? Action { get; init; }

    public NotificationSeverity Severity { get; init; }

    // False when notifications were turned off or throttled, so the user never saw it.
    public bool Shown { get; init; }

    public bool Read { get; init; }

    // The same notification again while still unread bumps this instead of adding a row.
    public int Count { get; init; } = 1;
}

/// <summary>
/// The bell in the title bar: the last notifications HerdMe raised, including ones Windows
/// never showed (turned off, throttled, or HerdMe was in the tray). Kept in
/// %LOCALAPPDATA%\HerdMe\Config\notification-history.json, newest first, bounded, and written
/// atomically. Nothing leaves the PC.
/// </summary>
public sealed class NotificationHistory
{
    public const int Capacity = 50;
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object sync = new();
    private List<NotificationHistoryEntry> entries = [];
    private bool loaded;

    public NotificationHistory(string supportRoot)
    {
        HistoryPath = Path.Combine(supportRoot, "Config", "notification-history.json");
    }

    public string HistoryPath { get; }

    // Raised on the thread that changed the history; UI listeners marshal themselves.
    public event EventHandler? Changed;

    public IReadOnlyList<NotificationHistoryEntry> Entries
    {
        get
        {
            lock (sync)
            {
                EnsureLoaded();
                return entries.ToList();
            }
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (sync)
            {
                EnsureLoaded();
                return entries.Count(entry => !entry.Read);
            }
        }
    }

    public static NotificationSeverity SeverityFor(string key)
    {
        if (key.StartsWith("service:", StringComparison.Ordinal)
            || key.StartsWith("process:", StringComparison.Ordinal)
            || key.Equals("crash", StringComparison.Ordinal))
        {
            return NotificationSeverity.Error;
        }
        return key.StartsWith("share:", StringComparison.Ordinal)
            ? NotificationSeverity.Warning
            : NotificationSeverity.Information;
    }

    public NotificationHistoryEntry Add(AppNotification notification, DateTimeOffset now, bool shown)
    {
        NotificationHistoryEntry entry;
        lock (sync)
        {
            EnsureLoaded();
            var action = notification.Primary is { } primary ? NotificationActions.Build(primary) : null;
            var latest = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, notification.Key, StringComparison.Ordinal));
            if (latest is not null && !latest.Read && now - latest.Time < MergeWindow)
            {
                entries.Remove(latest);
                entry = latest with
                {
                    Time = now,
                    Title = notification.Title,
                    Message = notification.Message,
                    Action = action,
                    Shown = latest.Shown || shown,
                    Count = Math.Min(latest.Count + 1, 999)
                };
            }
            else
            {
                entry = new NotificationHistoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Key = notification.Key,
                    Time = now,
                    Title = notification.Title,
                    Message = notification.Message,
                    Action = action,
                    Severity = SeverityFor(notification.Key),
                    Shown = shown
                };
            }
            entries.Insert(0, entry);
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public void MarkRead(string id)
    {
        lock (sync)
        {
            EnsureLoaded();
            var index = entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0 || entries[index].Read) return;
            entries[index] = entries[index] with { Read = true };
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAllRead()
    {
        lock (sync)
        {
            EnsureLoaded();
            if (entries.All(entry => entry.Read)) return;
            entries = entries.Select(entry => entry with { Read = true }).ToList();
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (sync)
        {
            EnsureLoaded();
            if (entries.Count == 0) return;
            entries = [];
            SaveUnlocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var stored = JsonSerializer.Deserialize<List<NotificationHistoryEntry>>(
                File.ReadAllText(HistoryPath),
                JsonOptions
            );
            entries = (stored ?? [])
                .Where(entry => !string.IsNullOrEmpty(entry.Id) && !string.IsNullOrEmpty(entry.Title))
                .OrderByDescending(entry => entry.Time)
                .Take(Capacity)
                .Select(entry => entry with { Action = NotificationActions.Parse(entry.Action) is null ? null : entry.Action })
                .ToList();
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            // An unreadable history starts empty; the next notification rewrites it.
            entries = [];
        }
    }

    private void SaveUnlocked()
    {
        var temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, HistoryPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The in-memory history still works for this session.
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
