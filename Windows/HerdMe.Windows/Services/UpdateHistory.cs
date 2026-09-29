using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public enum UpdateOutcome
{
    Updated,
    Failed,
    Cancelled,
    RolledBack,
    Restored
}

public sealed record UpdateHistoryEntry(
    string Id,
    string Name,
    string From,
    string To,
    DateTimeOffset At,
    UpdateOutcome Outcome,
    string? Error = null
);

/// <summary>
/// What was updated, when, from which version to which, and how it ended. The Updates page
/// shows it under History. Kept in %LOCALAPPDATA%\HerdMe\Config\update-history.json, newest
/// first and bounded; only names, versions, times and the error text are stored.
/// </summary>
public sealed class UpdateHistoryStore
{
    public const int Capacity = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync = new();
    private List<UpdateHistoryEntry>? entries;

    public UpdateHistoryStore(string supportRoot)
    {
        StorePath = Path.Combine(supportRoot, "Config", "update-history.json");
    }

    public string StorePath { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<UpdateHistoryEntry> Entries
    {
        get
        {
            lock (sync) return Loaded().ToList();
        }
    }

    public void Add(UpdateHistoryEntry entry)
    {
        lock (sync)
        {
            entries = Push(Loaded(), entry);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (sync)
        {
            entries = [];
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static List<UpdateHistoryEntry> Push(IEnumerable<UpdateHistoryEntry> current, UpdateHistoryEntry entry) =>
        current.Prepend(entry).OrderByDescending(item => item.At).Take(Capacity).ToList();

    private List<UpdateHistoryEntry> Loaded()
    {
        if (entries is not null) return entries;
        try
        {
            entries = File.Exists(StorePath)
                ? (JsonSerializer.Deserialize<List<UpdateHistoryEntry>>(File.ReadAllText(StorePath), JsonOptions) ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .OrderByDescending(item => item.At)
                    .Take(Capacity)
                    .ToList()
                : [];
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            entries = [];
        }
        return entries;
    }

    private void Save()
    {
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries ?? [], JsonOptions), new UTF8Encoding(false));
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
