using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed class WindowsHostsManager
{
    private const string BeginMarker = "# BEGIN HerdMe local sites";
    private const string EndMarker = "# END HerdMe local sites";
    private const string HelperArgument = "--apply-hosts";
    internal const long MaximumStagedHostsBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task EnsureMappingsAsync(
        IEnumerable<string> domains,
        CancellationToken cancellationToken = default
    )
    {
        await ApplyMappingsAsync(domains, cancellationToken);
    }

    public async Task RemoveMappingsAsync(CancellationToken cancellationToken = default)
    {
        await ApplyMappingsAsync([], cancellationToken);
    }

    public async Task<bool> HasManagedMappingsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var current = await ReadHostsTextAsync(HostsPath(), cancellationToken);
        return ContainsManagedBlock(current);
    }

    private static async Task ApplyMappingsAsync(
        IEnumerable<string> domains,
        CancellationToken cancellationToken
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows hosts configuration requires Windows.");
        }
        var hostsPath = HostsPath();
        var current = await ReadHostsTextAsync(hostsPath, cancellationToken);
        var updated = Render(current, domains);
        if (NormalizeNewlines(current) == NormalizeNewlines(updated)) return;
        if (Encoding.UTF8.GetByteCount(updated) > MaximumStagedHostsBytes)
        {
            throw new InvalidDataException("The Windows hosts file is too large to update safely.");
        }

        var supportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var cachePath = Path.Combine(supportPath, "Cache", "hosts");
        var backupPath = Path.Combine(supportPath, "Backup");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(backupPath);
        var originalBackup = Path.Combine(backupPath, "hosts-before-herdme");
        if (!File.Exists(originalBackup)) File.Copy(hostsPath, originalBackup);

        var candidatePath = Path.Combine(cachePath, $"hosts-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(candidatePath, updated, new UTF8Encoding(false), cancellationToken);
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The HerdMe executable path is unavailable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(HelperArgument);
            startInfo.ArgumentList.Add(candidatePath);
            startInfo.ArgumentList.Add(hostsPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Windows hosts updater could not be started.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The Windows hosts updater exited with code {process.ExitCode}."
                );
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1_225)
        {
            throw new InvalidOperationException(
                "Administrator approval is required to map local HerdMe domains.",
                error
            );
        }
        finally
        {
            if (File.Exists(candidatePath)) File.Delete(candidatePath);
        }
    }

    public static bool TryRunElevatedHelper(
        IReadOnlyList<string> arguments,
        out int exitCode
    )
    {
        exitCode = 0;
        if (arguments.Count < 2
            || !arguments[1].Equals(HelperArgument, StringComparison.Ordinal))
        {
            return false;
        }
        if (!OperatingSystem.IsWindows() || arguments.Count != 4)
        {
            exitCode = 2;
            return true;
        }

        var supportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!IsAllowedHelperRequest(arguments[2], arguments[3], supportPath, windowsPath))
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var candidate = ReadStagedCandidate(arguments[2]);
            if ((File.GetAttributes(arguments[3]) & FileAttributes.ReparsePoint) != 0)
            {
                exitCode = 2;
                return true;
            }
            using (var destination = new FileStream(
                arguments[3],
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            ))
            {
                string current;
                using (var reader = new StreamReader(
                    destination,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4_096,
                    leaveOpen: true
                ))
                {
                    try
                    {
                        current = reader.ReadToEnd();
                    }
                    catch (DecoderFallbackException error)
                    {
                        throw new InvalidDataException(
                            "The Windows hosts file is not valid UTF-8 and was left unchanged.",
                            error
                        );
                    }
                }
                if (!IsAllowedHostsUpdate(current, candidate))
                {
                    exitCode = 2;
                    return true;
                }

                destination.Position = 0;
                destination.SetLength(0);
                using (var writer = new StreamWriter(
                    destination,
                    new UTF8Encoding(false),
                    bufferSize: 4_096,
                    leaveOpen: true
                ))
                {
                    writer.Write(candidate);
                    writer.Flush();
                }
                destination.Flush(flushToDisk: true);
            }
            var flush = new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            flush.ArgumentList.Add("/flushdns");
            using var process = Process.Start(flush)
                ?? throw new InvalidOperationException("The Windows DNS cache could not be flushed.");
            process.WaitForExit();
            if (process.ExitCode != 0) exitCode = 1;
        }
        catch (Exception error) when (
            error is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidDataException
        )
        {
            exitCode = 1;
        }
        return true;
    }

    internal static bool IsAllowedHelperRequest(
        string source,
        string destination,
        string supportPath,
        string windowsPath
    )
    {
        try
        {
            var cachePath = Path.GetFullPath(Path.Combine(supportPath, "Cache", "hosts"));
            var sourcePath = Path.GetFullPath(source);
            var expectedDestination = Path.GetFullPath(Path.Combine(
                windowsPath,
                "System32",
                "drivers",
                "etc",
                "hosts"
            ));
            var cachePrefix = cachePath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ) + Path.DirectorySeparatorChar;
            return sourcePath.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(sourcePath).StartsWith("hosts-", StringComparison.Ordinal)
                && destination.Length > 0
                && Path.GetFullPath(destination).Equals(
                    expectedDestination,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException
        )
        {
            return false;
        }
    }

    internal static string ReadStagedCandidate(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The staged Windows hosts update must be a regular file."
            );
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.SequentialScan
        );
        if (stream.Length > MaximumStagedHostsBytes)
        {
            throw new InvalidDataException("The staged Windows hosts update is too large.");
        }

        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true
        );
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The staged Windows hosts update is not valid UTF-8.",
                error
            );
        }
    }

    private static async Task<string> ReadHostsTextAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (stream.Length > MaximumStagedHostsBytes)
        {
            throw new InvalidDataException("The Windows hosts file is too large to update safely.");
        }

        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true
        );
        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The Windows hosts file is not valid UTF-8 and was left unchanged.",
                error
            );
        }
    }

    internal static bool IsAllowedHostsUpdate(string current, string candidate)
    {
        try
        {
            var domains = ManagedDomains(candidate);
            return NormalizeNewlines(Render(current, domains))
                .Equals(NormalizeNewlines(candidate), StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static bool ContainsManagedBlock(string content)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        return begin >= 0 && end > begin;
    }

    public static string Render(string current, IEnumerable<string> domains)
    {
        var lines = NormalizeNewlines(current).Split('\n');
        var beginMarkers = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Trim().Equals(BeginMarker, StringComparison.Ordinal))
            .Select(item => item.Index)
            .ToArray();
        var endMarkers = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Trim().Equals(EndMarker, StringComparison.Ordinal))
            .Select(item => item.Index)
            .ToArray();
        if (
            beginMarkers.Length != endMarkers.Length
            || beginMarkers.Length > 1
            || (beginMarkers.Length == 1 && beginMarkers[0] >= endMarkers[0])
        )
        {
            throw new InvalidDataException("The Windows hosts file contains a malformed HerdMe block.");
        }

        var kept = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (
                beginMarkers.Length == 1
                && index >= beginMarkers[0]
                && index <= endMarkers[0]
            ) continue;
            kept.Add(lines[index].TrimEnd());
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);

        var validDomains = domains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => Uri.CheckHostName(domain) == UriHostNameType.Dns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (validDomains.Count > 0)
        {
            if (kept.Count > 0) kept.Add(string.Empty);
            kept.Add(BeginMarker);
            kept.AddRange(validDomains.Select(domain => $"127.0.0.1\t{domain}"));
            kept.Add(EndMarker);
        }
        return string.Join("\r\n", kept) + "\r\n";
    }

    private static IReadOnlyList<string> ManagedDomains(string candidate)
    {
        var lines = NormalizeNewlines(candidate).Split('\n');
        var beginMarkers = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Trim().Equals(BeginMarker, StringComparison.Ordinal))
            .Select(item => item.Index)
            .ToArray();
        var endMarkers = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Trim().Equals(EndMarker, StringComparison.Ordinal))
            .Select(item => item.Index)
            .ToArray();
        if (beginMarkers.Length == 0 && endMarkers.Length == 0) return [];
        if (
            beginMarkers.Length != 1
            || endMarkers.Length != 1
            || beginMarkers[0] >= endMarkers[0]
        )
        {
            throw new InvalidDataException("The staged Windows hosts file contains malformed HerdMe markers.");
        }

        const string mappingPrefix = "127.0.0.1\t";
        var domains = new List<string>();
        for (var index = beginMarkers[0] + 1; index < endMarkers[0]; index++)
        {
            var line = lines[index];
            if (!line.StartsWith(mappingPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The staged HerdMe hosts block contains an invalid mapping.");
            }
            var domain = line[mappingPrefix.Length..];
            if (
                domain.Length == 0
                || !domain.Equals(domain.Trim().TrimEnd('.').ToLowerInvariant(), StringComparison.Ordinal)
                || Uri.CheckHostName(domain) != UriHostNameType.Dns
            )
            {
                throw new InvalidDataException("The staged HerdMe hosts block contains an invalid domain.");
            }
            domains.Add(domain);
        }
        return domains;
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string HostsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "drivers",
            "etc",
            "hosts"
        );
    }
}
