using System.Collections.Concurrent;

namespace HerdMe.Windows.Services;

/// <summary>
/// Keeps the text of small HerdMe-owned settings files in memory so repeated loads (often on the
/// UI thread) do not reopen and reread the file. A cached copy is used only while the file still
/// has the same size and LastWriteTimeUtc. Files modified within the last few seconds are never
/// served from the cache because coarse file-system timestamps could hide a second write.
/// </summary>
internal static class SettingsFileCache
{
    internal static readonly TimeSpan SettledAge = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(long LastWriteTicks, long Length, string Text);

    public static string ReadAllText(string path)
    {
        var key = Path.GetFullPath(path);
        var file = new FileInfo(key);
        if (!file.Exists)
        {
            Entries.TryRemove(key, out _);
            return File.ReadAllText(key);
        }
        var lastWrite = file.LastWriteTimeUtc;
        var length = file.Length;
        if (Entries.TryGetValue(key, out var cached)
            && cached.LastWriteTicks == lastWrite.Ticks
            && cached.Length == length)
        {
            return cached.Text;
        }

        var text = File.ReadAllText(key);
        if (IsSettled(lastWrite, DateTime.UtcNow))
        {
            Entries[key] = new Entry(lastWrite.Ticks, length, text);
        }
        else
        {
            Entries.TryRemove(key, out _);
        }
        return text;
    }

    public static void Invalidate(string path)
    {
        try
        {
            Entries.TryRemove(Path.GetFullPath(path), out _);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            Entries.Clear();
        }
    }

    internal static bool IsSettled(DateTime lastWriteUtc, DateTime nowUtc) =>
        nowUtc - lastWriteUtc >= SettledAge;

    internal static bool IsCached(string path) => Entries.ContainsKey(Path.GetFullPath(path));
}
