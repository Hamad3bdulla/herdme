using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record BackendOperation(string Name, string State, DateTimeOffset At, string? Detail = null);

public sealed class OperationJournal
{
    private readonly string path;
    private readonly object sync = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public OperationJournal(string supportRoot)
    {
        Directory.CreateDirectory(supportRoot);
        path = Path.Combine(supportRoot, "operations.jsonl");
    }

    public void Append(string name, string state, string? detail = null)
    {
        var entry = JsonSerializer.Serialize(new BackendOperation(name, state, DateTimeOffset.UtcNow, detail), Options);
        lock (sync)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4 * 1_024, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(entry);
            writer.Flush();
            stream.Flush(true);
        }
    }

    public IReadOnlyList<BackendOperation> ReadRecent(int limit = 100)
    {
        if (!File.Exists(path)) return [];
        lock (sync)
        {
            return File.ReadLines(path).Reverse().Take(Math.Max(1, limit))
                .Select(line => JsonSerializer.Deserialize<BackendOperation>(line, Options))
                .Where(entry => entry is not null).Cast<BackendOperation>().ToList();
        }
    }
}
