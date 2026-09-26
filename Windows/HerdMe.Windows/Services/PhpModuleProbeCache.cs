using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace HerdMe.Windows.Services;

/// <summary>
/// Caches results of expensive PHP probes (php -m, core extension reports, Xdebug version checks)
/// per php.exe. An entry is reused only while php.exe, php.ini, the ext folder and any extra
/// dependency keep the same timestamps and sizes. HerdMe invalidates entries explicitly whenever
/// it changes extensions, Xdebug or php.ini itself.
/// </summary>
public static class PhpModuleProbeCache
{
    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static long generation;

    private sealed record Entry(string Fingerprint, object Value);

    internal static int Count => Entries.Count;

    public static async Task<T> GetOrProbeAsync<T>(
        string phpExecutable,
        string probe,
        IReadOnlyList<string> dependencies,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default,
        Func<T, bool>? shouldCache = null
    )
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);
        var key = Key(phpExecutable, probe);
        var fingerprint = Fingerprint(phpExecutable, dependencies);
        if (fingerprint is not null
            && Entries.TryGetValue(key, out var cached)
            && cached.Fingerprint == fingerprint
            && cached.Value is T cachedValue)
        {
            return cachedValue;
        }

        var startedGeneration = Interlocked.Read(ref generation);
        var value = await factory(cancellationToken);
        // Only store the result when no invalidation happened while the probe was running and the
        // inputs did not change underneath it; otherwise the next caller probes again.
        if (fingerprint is not null
            && (shouldCache is null || shouldCache(value))
            && Interlocked.Read(ref generation) == startedGeneration
            && Fingerprint(phpExecutable, dependencies) == fingerprint)
        {
            Entries[key] = new Entry(fingerprint, value);
        }
        return value;
    }

    /// <summary>Forgets every probe result of one php.exe or of every php.exe below a runtime directory.</summary>
    public static void Invalidate(string phpExecutableOrRuntimeDirectory)
    {
        Interlocked.Increment(ref generation);
        string target;
        try
        {
            target = Path.GetFullPath(phpExecutableOrRuntimeDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            InvalidateAll();
            return;
        }
        var directoryPrefix = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var key in Entries.Keys)
        {
            var separator = key.IndexOf('\n', StringComparison.Ordinal);
            var executable = separator < 0 ? key : key[..separator];
            if (executable.Equals(target, StringComparison.OrdinalIgnoreCase)
                || executable.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Entries.TryRemove(key, out _);
            }
        }
    }

    public static void InvalidateAll()
    {
        Interlocked.Increment(ref generation);
        Entries.Clear();
    }

    private static string Key(string phpExecutable, string probe) =>
        Path.GetFullPath(phpExecutable) + "\n" + probe;

    internal static string? Fingerprint(string phpExecutable, IReadOnlyList<string> dependencies)
    {
        var executable = Path.GetFullPath(phpExecutable);
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var builder = new StringBuilder();
        if (!Append(builder, executable)
            || !Append(builder, Path.Combine(directory, "php.ini"))
            || !Append(builder, Path.Combine(directory, "ext")))
        {
            return null;
        }
        foreach (var dependency in dependencies)
        {
            if (!Append(builder, dependency)) return null;
        }
        return builder.ToString();
    }

    private static bool Append(StringBuilder builder, string path)
    {
        try
        {
            builder.Append(path).Append('|');
            var file = new FileInfo(path);
            if (file.Exists)
            {
                builder.Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(file.Length.ToString(CultureInfo.InvariantCulture));
            }
            else if (Directory.Exists(path))
            {
                builder.Append('d')
                    .Append(Directory.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append('-');
            }
            builder.Append(';');
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
