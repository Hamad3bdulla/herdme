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
        var current = await File.ReadAllTextAsync(HostsPath(), cancellationToken);
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
        var current = await File.ReadAllTextAsync(hostsPath, cancellationToken);
        var updated = Render(current, domains);
        if (NormalizeNewlines(current) == NormalizeNewlines(updated)) return;

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
            File.Copy(arguments[2], arguments[3], overwrite: true);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
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

    internal static bool ContainsManagedBlock(string content)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        return begin >= 0 && end > begin;
    }

    public static string Render(string current, IEnumerable<string> domains)
    {
        var kept = new List<string>();
        var insideManagedBlock = false;
        foreach (var line in NormalizeNewlines(current).Split('\n'))
        {
            if (line.Trim().Equals(BeginMarker, StringComparison.Ordinal))
            {
                insideManagedBlock = true;
                continue;
            }
            if (line.Trim().Equals(EndMarker, StringComparison.Ordinal))
            {
                insideManagedBlock = false;
                continue;
            }
            if (!insideManagedBlock) kept.Add(line.TrimEnd());
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
