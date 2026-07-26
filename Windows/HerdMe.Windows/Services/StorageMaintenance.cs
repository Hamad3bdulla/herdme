using System.Collections.Concurrent;

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
