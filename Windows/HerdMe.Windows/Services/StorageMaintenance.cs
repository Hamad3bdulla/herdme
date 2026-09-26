using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace HerdMe.Windows.Services;

internal static class BoundedLog
{
    internal const long DefaultMaximumBytes = 10 * 1_024 * 1_024;
    internal const int DefaultArchiveCount = 5;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void AppendText(
        string path,
        string text,
        long maximumBytes = DefaultMaximumBytes,
        int archiveCount = DefaultArchiveCount
    )
    {
        if (text.Length == 0) return;
        var gate = Gate(path);
        gate.Wait();
        try
        {
            Prepare(path, maximumBytes, archiveCount);
            File.AppendAllText(path, text);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static void AppendLine(
        string path,
        string line,
        long maximumBytes = DefaultMaximumBytes,
        int archiveCount = DefaultArchiveCount
    )
    {
        AppendText(path, line.TrimEnd('\r', '\n') + Environment.NewLine, maximumBytes, archiveCount);
    }

    internal static async Task AppendLineAsync(
        string path,
        string line,
        CancellationToken cancellationToken = default
    )
    {
        var gate = Gate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Prepare(path, DefaultMaximumBytes, DefaultArchiveCount);
            await File.AppendAllTextAsync(
                path,
                line.TrimEnd('\r', '\n') + Environment.NewLine,
                cancellationToken
            );
        }
        finally
        {
            gate.Release();
        }
    }

    internal static void RotateIfNeeded(
        string path,
        long maximumBytes = DefaultMaximumBytes,
        int archiveCount = DefaultArchiveCount
    )
    {
        var gate = Gate(path);
        gate.Wait();
        try
        {
            RotateCore(path, maximumBytes, archiveCount);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim Gate(string path)
    {
        return Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
    }

    private static void Prepare(string path, long maximumBytes, int archiveCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateCore(path, maximumBytes, archiveCount);
    }

    private static void RotateCore(string path, long maximumBytes, int archiveCount)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (archiveCount < 0) throw new ArgumentOutOfRangeException(nameof(archiveCount));
        if (!File.Exists(path) || new FileInfo(path).Length < maximumBytes) return;
        if (archiveCount == 0)
        {
            File.Delete(path);
            return;
        }
        File.Delete(path + "." + archiveCount);
        for (var index = archiveCount - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            if (File.Exists(source)) File.Move(source, path + "." + (index + 1), true);
        }
        File.Move(path, path + ".1", true);
    }
}

/// <summary>
/// Moves log writes off request and process-output threads. Text is queued per log file and a
/// single background writer appends it through <see cref="BoundedLog"/>, so rotation is unchanged.
/// FlushAsync drains a file (or every file) and is called when the owning server stops; a
/// process-exit hook drains whatever is still queued.
/// </summary>
internal static class QueuedLog
{
    internal const int QueueCapacity = 4_096;
    private static readonly ConcurrentDictionary<string, Writer> Writers =
        new(StringComparer.OrdinalIgnoreCase);
    private static int processExitHooked;

    internal static void Append(string path, string text)
    {
        if (text.Length == 0) return;
        HookProcessExit();
        var key = Path.GetFullPath(path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var writer = Writers.GetOrAdd(key, static target => new Writer(target));
            if (writer.TryWrite(text)) return;
            if (!writer.IsCompleted) break;
            // The writer is being flushed; drop it so the next attempt starts a fresh one.
            Writers.TryRemove(new KeyValuePair<string, Writer>(key, writer));
        }
        // The queue is full or unavailable: write directly instead of losing the text.
        BoundedLog.AppendText(key, text);
    }

    internal static void AppendLine(string path, string line) =>
        Append(path, line.TrimEnd('\r', '\n') + Environment.NewLine);

    internal static Task FlushAsync(string path)
    {
        var key = Path.GetFullPath(path);
        return Writers.TryRemove(key, out var writer) ? writer.CompleteAsync() : Task.CompletedTask;
    }

    internal static Task FlushAllAsync()
    {
        var pending = new List<Task>();
        foreach (var key in Writers.Keys)
        {
            if (Writers.TryRemove(key, out var writer)) pending.Add(writer.CompleteAsync());
        }
        return Task.WhenAll(pending);
    }

    private static void HookProcessExit()
    {
        if (Interlocked.Exchange(ref processExitHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                FlushAllAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        };
    }

    private sealed class Writer
    {
        private readonly string path;
        private readonly Channel<string> queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            }
        );
        private readonly Task drain;
        private int completed;

        public Writer(string path)
        {
            this.path = path;
            drain = Task.Run(DrainAsync);
        }

        public bool IsCompleted => Volatile.Read(ref completed) != 0;

        // FullMode.Wait makes TryWrite return false when full instead of dropping text.
        public bool TryWrite(string text) => queue.Writer.TryWrite(text);

        public Task CompleteAsync()
        {
            Volatile.Write(ref completed, 1);
            queue.Writer.TryComplete();
            return drain;
        }

        private async Task DrainAsync()
        {
            var batch = new StringBuilder();
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Length < 256 * 1_024 && queue.Reader.TryRead(out var text))
                {
                    batch.Append(text);
                }
                if (batch.Length == 0) continue;
                try
                {
                    BoundedLog.AppendText(path, batch.ToString());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Logging is best effort; keep draining so the queue cannot fill up.
                }
            }
        }
    }
}

internal static class CaptureRetention
{
    internal const int DefaultItemLimit = 1_000;
    internal static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(30);

    internal static void Prune(
        string directoryPath,
        int itemLimit = DefaultItemLimit,
        TimeSpan? maximumAge = null,
        DateTimeOffset? now = null
    )
    {
        if (itemLimit <= 0) throw new ArgumentOutOfRangeException(nameof(itemLimit));
        var age = maximumAge ?? DefaultMaximumAge;
        if (age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        if (!Directory.Exists(directoryPath)) return;

        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - age;
        var retained = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json"))
        {
            var file = new FileInfo(path);
            if (file.LastWriteTimeUtc < cutoff)
            {
                file.Delete();
            }
            else
            {
                retained.Add(file);
            }
        }
        foreach (var file in retained
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Skip(itemLimit))
        {
            file.Delete();
        }
    }
}
