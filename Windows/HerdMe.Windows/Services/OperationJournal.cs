using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record BackendOperation(string Name, string State, DateTimeOffset At, string? Detail = null);

public sealed class OperationJournal
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private readonly string path;
    // Several pages and services can open the same journal independently.
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public OperationJournal(string supportRoot)
    {
        Directory.CreateDirectory(supportRoot);
        path = Path.Combine(supportRoot, "operations.jsonl");
    }

    public void Append(string name, string state, string? detail = null)
    {
        var entry = JsonSerializer.Serialize(new BackendOperation(name, state, DateTimeOffset.UtcNow, detail), Options);
        var bytes = Encoding.UTF8.GetBytes(entry + Environment.NewLine);
        if (bytes.Length > MaxBytes) return;
        lock (Sync)
        {
            try
            {
                RotateIfNeeded(bytes.Length);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.Read, 4 * 1_024, FileOptions.WriteThrough);
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
                }
                // Separate the next record from a final write interrupted by a crash.
                stream.Write(bytes);
                stream.Flush(true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A locked or unavailable diagnostics file must not fail backend work.
                // If rotation fails, skip this record instead of growing without bounds.
            }
        }
    }

    public IReadOnlyList<BackendOperation> ReadRecent(int limit = 100)
    {
        if (limit <= 0) return [];
        lock (Sync)
        {
            var entries = ReadRecentFile(path, limit);
            if (entries.Count < limit)
                entries.AddRange(ReadRecentFile(path + ".1", limit - entries.Count));
            return entries;
        }
    }

    private static List<BackendOperation> ReadRecentFile(string filePath, int limit)
    {
        var entries = new Queue<BackendOperation>();
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4 * 1_024, FileOptions.SequentialScan);
            // Older journals may predate rotation. Read a bounded tail of those too.
            var offset = Math.Max(0, stream.Length - MaxBytes);
            stream.Position = offset;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (offset > 0) reader.ReadLine();
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<BackendOperation>(line, Options);
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Name)
                        || string.IsNullOrWhiteSpace(entry.State) || entry.At == default) continue;
                    if (entries.Count == limit) entries.Dequeue();
                    entries.Enqueue(entry);
                }
                catch (JsonException)
                {
                    // Ignore incomplete or malformed records while retaining valid history.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Rotation or external cleanup may remove a file before it can be opened.
        }
        return entries.Reverse().ToList();
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length + incomingBytes + 1 <= MaxBytes) return;
        File.Move(path, path + ".1", overwrite: true);
    }
}
