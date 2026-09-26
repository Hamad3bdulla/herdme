using System.Globalization;

namespace HerdMe.Windows.Services;

public sealed record XdebugProfileFile(string Path, string Name, DateTimeOffset CreatedAt, long Bytes);

public sealed record CachegrindFunction(
    string Name,
    string? File,
    long Calls,
    TimeSpan SelfTime,
    TimeSpan InclusiveTime,
    long SelfMemory,
    long InclusiveMemory
);

public sealed record CachegrindProfile(
    string Command,
    TimeSpan TotalTime,
    long PeakMemory,
    IReadOnlyList<CachegrindFunction> Functions
);

/// <summary>
/// Xdebug profiler output under %LOCALAPPDATA%\HerdMe\Profiles. HerdMe only lists, reads, and
/// deletes files it asked Xdebug to write there (cachegrind.out.*).
/// </summary>
public sealed class XdebugProfileStore
{
    public const string OutputNamePattern = "cachegrind.out.%t.%r";
    public const int MaximumProfiles = 50;
    public const long MaximumTotalBytes = 1L * 1_024 * 1_024 * 1_024;
    private const string SearchPattern = "cachegrind.out.*";

    public XdebugProfileStore()
        : this(DirectoryFor(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        )))
    {
    }

    public XdebugProfileStore(string directory)
    {
        Directory = System.IO.Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public static string DirectoryFor(string supportPath) => System.IO.Path.Combine(supportPath, "Profiles");

    /// <summary>Newest first. Also prunes the oldest profiles beyond the count and size limits.</summary>
    public IReadOnlyList<XdebugProfileFile> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var files = new DirectoryInfo(Directory)
            .EnumerateFiles(SearchPattern, SearchOption.TopDirectoryOnly)
            .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        var kept = new List<XdebugProfileFile>();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (kept.Count >= MaximumProfiles || (kept.Count > 0 && total > MaximumTotalBytes))
            {
                TryDelete(file.FullName);
                continue;
            }
            kept.Add(new XdebugProfileFile(
                file.FullName,
                file.Name,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length
            ));
        }
        return kept;
    }

    public void Delete(string path)
    {
        TryDelete(Owned(path));
    }

    public void DeleteAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, SearchPattern)) TryDelete(file);
    }

    public CachegrindProfile Load(string path, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(Owned(path));
        return CachegrindParser.Parse(reader, cancellationToken);
    }

    private string Owned(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(fullPath), Directory, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(fullPath).StartsWith("cachegrind.out.", StringComparison.Ordinal))
        {
            throw new ArgumentException("The profile is not in the HerdMe profiles folder.", nameof(path));
        }
        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A profile that PHP is still writing is removed on a later refresh.
        }
    }
}

/// <summary>
/// Streams an Xdebug cachegrind file and aggregates self and inclusive cost per function.
/// Cost lines after "fn=" are self cost; the cost line after "calls=" is the inclusive cost of
/// that call, which counts toward the caller's inclusive cost and the callee's call count.
/// </summary>
public static class CachegrindParser
{
    public const int MaximumFunctions = 200_000;

    public static CachegrindProfile Parse(TextReader reader, CancellationToken cancellationToken = default)
    {
        var fileNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var functionNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var totals = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var command = string.Empty;
        var timeIndex = -1;
        var memoryIndex = -1;
        var timeTicksPerUnit = 0.1;
        long? summaryTime = null;
        long? summaryMemory = null;
        string? currentFile = null;
        Accumulator? current = null;
        string? pendingCallee = null;
        long pendingCalls = 0;
        var sawEvents = false;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            if ((++lineNumber & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] == '#') continue;
            if (char.IsAsciiDigit(line[0]) || line[0] is '+' or '-' or '*')
            {
                if (current is null) continue;
                var (costTime, costMemory) = Costs(line, timeIndex, memoryIndex);
                if (pendingCallee is not null)
                {
                    current.InclusiveTime += costTime;
                    current.InclusiveMemory += costMemory;
                    if (totals.TryGetValue(pendingCallee, out var callee)) callee.Calls += pendingCalls;
                    pendingCallee = null;
                }
                else
                {
                    current.SelfTime += costTime;
                    current.SelfMemory += costMemory;
                    current.InclusiveTime += costTime;
                    current.InclusiveMemory += costMemory;
                }
                continue;
            }

            // "fn=(1) name" and "events: Time" both occur; the first separator wins so paths
            // such as the Windows script path after "cmd:" keep their drive colon.
            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            var separator = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals);
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "cmd":
                    command = value;
                    break;
                case "events":
                    var events = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    timeIndex = Array.FindIndex(events, name => name.StartsWith("Time", StringComparison.Ordinal));
                    memoryIndex = Array.FindIndex(events, name => name.StartsWith("Memory", StringComparison.Ordinal));
                    if (timeIndex >= 0)
                    {
                        // Xdebug 3 reports Time_(10ns); Xdebug 2 reported microseconds.
                        timeTicksPerUnit = events[timeIndex].Contains("10ns", StringComparison.Ordinal) ? 0.1 : 10;
                    }
                    sawEvents = true;
                    break;
                case "summary":
                    var summary = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    summaryTime = Number(summary, timeIndex);
                    summaryMemory = Number(summary, memoryIndex);
                    break;
                case "fl":
                case "fi":
                case "fe":
                    currentFile = Resolve(fileNames, value);
                    break;
                case "cfl":
                case "cfi":
                    Resolve(fileNames, value);
                    break;
                case "fn":
                    var name = Resolve(functionNames, value);
                    current = Function(totals, name, currentFile);
                    pendingCallee = null;
                    break;
                case "cfn":
                    var calleeName = Resolve(functionNames, value);
                    Function(totals, calleeName, null);
                    pendingCallee = calleeName;
                    pendingCalls = 0;
                    break;
                case "calls":
                    var callParts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    pendingCalls = Number(callParts, 0) ?? 1;
                    break;
            }
            if (totals.Count > MaximumFunctions)
            {
                throw new InvalidDataException("The profile has too many functions to summarize.");
            }
        }
        if (!sawEvents) throw new InvalidDataException("The file is not an Xdebug cachegrind profile.");

        foreach (var function in totals.Values.Where(function => function.Calls == 0)) function.Calls = 1;
        var functions = totals.Values
            .Select(function => new CachegrindFunction(
                function.Name,
                function.File,
                function.Calls,
                Duration(function.SelfTime, timeTicksPerUnit),
                Duration(function.InclusiveTime, timeTicksPerUnit),
                function.SelfMemory,
                function.InclusiveMemory
            ))
            .OrderByDescending(function => function.InclusiveTime)
            .ThenByDescending(function => function.SelfTime)
            .ToArray();
        var totalTime = summaryTime is { } time
            ? Duration(time, timeTicksPerUnit)
            : functions.Length == 0 ? TimeSpan.Zero : functions.Max(function => function.InclusiveTime);
        var peakMemory = summaryMemory
            ?? (functions.Length == 0 ? 0 : functions.Max(function => function.InclusiveMemory));
        return new CachegrindProfile(command, totalTime, peakMemory, functions);
    }

    private static (long Time, long Memory) Costs(string line, int timeIndex, int memoryIndex)
    {
        // The first column is the position (line number); costs follow in "events:" order.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (
            timeIndex < 0 ? 0 : Number(parts, timeIndex + 1) ?? 0,
            memoryIndex < 0 ? 0 : Number(parts, memoryIndex + 1) ?? 0
        );
    }

    private static long? Number(string[] parts, int index)
    {
        return index >= 0
            && index < parts.Length
            && long.TryParse(parts[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static TimeSpan Duration(long units, double ticksPerUnit)
    {
        var ticks = units * ticksPerUnit;
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)Math.Max(0, ticks));
    }

    // Name compression: "(12) name" defines id 12, a later "(12)" refers back to it.
    private static string Resolve(Dictionary<string, string> names, string value)
    {
        if (value.Length == 0 || value[0] != '(') return value;
        var close = value.IndexOf(')');
        if (close < 0) return value;
        var id = value[..(close + 1)];
        var name = value[(close + 1)..].Trim();
        if (name.Length > 0)
        {
            names[id] = name;
            return name;
        }
        return names.TryGetValue(id, out var known) ? known : id;
    }

    private static Accumulator Function(Dictionary<string, Accumulator> totals, string name, string? file)
    {
        if (!totals.TryGetValue(name, out var function))
        {
            function = new Accumulator(name);
            totals[name] = function;
        }
        function.File ??= file;
        return function;
    }

    private sealed class Accumulator(string name)
    {
        public string Name { get; } = name;

        public string? File { get; set; }

        public long Calls { get; set; }

        public long SelfTime { get; set; }

        public long InclusiveTime { get; set; }

        public long SelfMemory { get; set; }

        public long InclusiveMemory { get; set; }
    }
}
