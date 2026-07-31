using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record XdebugInstallation(string Version, string ExtensionPath);

public sealed record XdebugWindowsRelease(
    string Version,
    string FileName,
    string DllName,
    string Sha256,
    Uri DownloadUri
);

public sealed class XdebugManager
{
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();

    public string ExtensionPath(string phpCycle)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe",
            "Extensions",
            "php",
            phpCycle,
            "php_xdebug.dll"
        );
    }

    public async Task<XdebugInstallation?> InstalledAsync(
        string phpExecutable,
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        var extensionPath = ExtensionPath(phpCycle);
        if (!File.Exists(extensionPath)) return null;

        var result = await RunAsync(
            phpExecutable,
            ["-n", "-d", $"zend_extension={extensionPath}", "-r", "echo phpversion('xdebug') ?: '';"],
            cancellationToken
        );
        var version = result.Output.Trim();
        return result.ExitCode == 0 && IsVersion(version)
            ? new XdebugInstallation(version, extensionPath)
            : null;
    }

    public async Task<XdebugInstallation> InstallAsync(
        string phpExecutable,
        CancellationToken cancellationToken = default
    )
    {
        var descriptor = await DescribePhpAsync(phpExecutable, cancellationToken);
        var releaseMetadata = await HttpClient.GetStringAsync(
            "https://api.github.com/repos/xdebug/xdebug/releases/latest",
            cancellationToken
        );
        var release = SelectWindowsRelease(
            releaseMetadata,
            descriptor.Cycle,
            descriptor.ThreadSafety,
            descriptor.Architecture
        );

        var destination = ExtensionPath(descriptor.Cycle);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The Xdebug destination is invalid.");
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, $".xdebug-{Guid.NewGuid():N}.zip");
        var candidate = Path.Combine(directory, $".xdebug-{Guid.NewGuid():N}.dll");
        try
        {
            await DownloadAndVerifyAsync(release, archive, cancellationToken);
            await ExtractDllAsync(release, archive, candidate, cancellationToken);

            var validation = await RunAsync(
                phpExecutable,
                ["-n", "-d", $"zend_extension={candidate}", "-r", "echo phpversion('xdebug') ?: '';"],
                cancellationToken
            );
            if (validation.ExitCode != 0 || validation.Output.Trim() != release.Version)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(validation.CombinedOutput)
                        ? "The downloaded Xdebug DLL could not be loaded by PHP."
                        : validation.CombinedOutput.Trim()
                );
            }

            File.Move(candidate, destination, true);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "VERSION"),
                release.Version + Environment.NewLine,
                cancellationToken
            );
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (File.Exists(candidate)) File.Delete(candidate);
        }

        return await InstalledAsync(phpExecutable, descriptor.Cycle, cancellationToken)
            ?? throw new InvalidOperationException("Xdebug validation failed after installation.");
    }

    public async Task<XdebugWindowsRelease> ResolveReleaseAsync(
        string phpExecutable,
        CancellationToken cancellationToken = default
    )
    {
        var descriptor = await DescribePhpAsync(phpExecutable, cancellationToken);
        var releaseMetadata = await HttpClient.GetStringAsync(
            "https://api.github.com/repos/xdebug/xdebug/releases/latest",
            cancellationToken
        );
        return SelectWindowsRelease(
            releaseMetadata,
            descriptor.Cycle,
            descriptor.ThreadSafety,
            descriptor.Architecture
        );
    }

    public async Task<string> PhpCycleAsync(
        string phpExecutable,
        CancellationToken cancellationToken = default
    )
    {
        return (await DescribePhpAsync(phpExecutable, cancellationToken)).Cycle;
    }

    internal static XdebugWindowsRelease SelectWindowsRelease(
        string releaseMetadata,
        string phpCycle,
        string threadSafety,
        string architecture
    )
    {
        using var document = JsonDocument.Parse(releaseMetadata);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            throw new InvalidDataException("The latest Xdebug GitHub release is not stable.");
        }
        var version = (root.GetProperty("tag_name").GetString() ?? string.Empty).TrimStart('v');
        if (!IsVersion(version)) throw new InvalidDataException("The Xdebug release metadata has an invalid version.");

        var filePattern = $"php_xdebug-{Regex.Escape(version)}-{Regex.Escape(phpCycle)}-"
            + $"{Regex.Escape(threadSafety)}-vs[0-9]+-{Regex.Escape(architecture)}\\.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var fileName = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!Regex.IsMatch(fileName, $"^{filePattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }
            var digest = asset.GetProperty("digest").GetString();
            var checksum = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..]
                : null;
            var download = asset.GetProperty("browser_download_url").GetString();
            if (checksum is not { Length: 64 } || !checksum.All(Uri.IsHexDigit)
                || !Uri.TryCreate(download, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/xdebug/xdebug/releases/download/", StringComparison.Ordinal))
            {
                continue;
            }
            return new XdebugWindowsRelease(
                version,
                fileName,
                Path.ChangeExtension(fileName, ".dll"),
                checksum,
                uri
            );
        }
        throw new InvalidOperationException(
            $"No verified Xdebug {version} build matches PHP {phpCycle} "
            + $"{threadSafety} {architecture}."
        );
    }

    private static async Task<PhpDescriptor> DescribePhpAsync(
        string phpExecutable,
        CancellationToken cancellationToken
    )
    {
        const string script = "echo PHP_MAJOR_VERSION.'.'.PHP_MINOR_VERSION.'|'"
            + ".(PHP_ZTS?'ts':'nts').'|'"
            + ".(PHP_INT_SIZE===8?'x86_64':'x86');";
        var result = await RunAsync(phpExecutable, ["-n", "-r", script], cancellationToken);
        var values = result.Output.Trim().Split('|');
        if (result.ExitCode != 0 || values.Length != 3 || values[2] != "x86_64")
        {
            throw new InvalidOperationException(
                result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? result.CombinedOutput.Trim()
                    : "HerdMe requires a supported 64-bit Windows PHP runtime for Xdebug."
            );
        }
        return new PhpDescriptor(values[0], values[1], values[2]);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PHP could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    internal static async Task DownloadAndVerifyAsync(
        XdebugWindowsRelease release,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await using var input = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1_024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Xdebug archive did not match its published SHA-256 checksum.");
        }
    }

    internal static async Task ExtractDllAsync(
        XdebugWindowsRelease release,
        string archivePath,
        string destination,
        CancellationToken cancellationToken
    )
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.SingleOrDefault(candidate =>
            candidate.FullName.Equals(release.DllName, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new InvalidDataException("The verified Xdebug archive did not contain its expected DLL.");
        await using var input = entry.Open();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static bool IsVersion(string value)
    {
        return Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+$");
    }

    private sealed record PhpDescriptor(string Cycle, string ThreadSafety, string Architecture);

    private sealed record ProcessResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => Output + Error;
    }
}
