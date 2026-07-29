using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record GitWindowsRelease(
    string Version,
    string FileName,
    long Size,
    string Sha256,
    Uri DownloadUri
);

public sealed partial class GitRuntimeInstaller
{
    private const string LatestReleaseUri =
        "https://api.github.com/repos/git-for-windows/git/releases/latest";
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();
    private readonly SemaphoreSlim installLock = new(1, 1);

    public GitRuntimeInstaller(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string RuntimeRoot => Path.Combine(SupportRoot, "Runtimes", "git");

    public string? InstalledVersion()
    {
        if (!Directory.Exists(RuntimeRoot)) return null;
        return Directory.EnumerateDirectories(RuntimeRoot)
            .Select(Path.GetFileName)
            .Where(version => version is not null
                && Version.TryParse(version, out _)
                && File.Exists(GitExecutable(version)))
            .Select(version => version!)
            .OrderByDescending(Version.Parse)
            .FirstOrDefault();
    }

    public string? InstalledExecutable()
    {
        var version = InstalledVersion();
        return version is null ? null : GitExecutable(version);
    }

    public string GitExecutable(string version)
    {
        return Path.Combine(RuntimeRoot, version, "cmd", "git.exe");
    }

    public async Task<string> EnsureInstalledAsync(
        CancellationToken cancellationToken = default
    )
    {
        await installLock.WaitAsync(cancellationToken);
        try
        {
            return InstalledExecutable()
                ?? await InstallCoreAsync(cancellationToken);
        }
        finally
        {
            installLock.Release();
        }
    }

    public async Task<GitWindowsRelease> ResolveReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var metadata = await HttpClient.GetStringAsync(LatestReleaseUri, cancellationToken);
        return SelectRelease(metadata);
    }

    internal static GitWindowsRelease SelectRelease(string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        if (!document.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Git for Windows release metadata did not contain assets.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var fileName = asset.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            var match = fileName is null ? null : MinGitArchivePattern().Match(fileName);
            if (match is not { Success: true }) continue;

            var digest = asset.TryGetProperty("digest", out var digestProperty)
                ? digestProperty.GetString()
                : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..]
                : null;
            var download = asset.TryGetProperty("browser_download_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;
            var size = asset.TryGetProperty("size", out var sizeProperty)
                ? sizeProperty.GetInt64()
                : 0;
            if (sha256 is not { Length: 64 }
                || !sha256.All(Uri.IsHexDigit)
                || size <= 0
                || !Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Git for Windows did not publish valid SHA-256 metadata for MinGit x64."
                );
            }
            return new GitWindowsRelease(
                match.Groups["version"].Value,
                fileName!,
                size,
                sha256,
                downloadUri
            );
        }

        throw new InvalidOperationException(
            "No official 64-bit MinGit ZIP was found in the latest Git for Windows release."
        );
    }

    private async Task<string> InstallCoreAsync(CancellationToken cancellationToken)
    {
        var release = await ResolveReleaseAsync(cancellationToken);
        var cacheDirectory = Path.Combine(SupportRoot, "Cache", "git");
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(RuntimeRoot);
        var archive = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(RuntimeRoot, $".install-{Guid.NewGuid():N}");
        var destination = Path.Combine(RuntimeRoot, release.Version);
        try
        {
            await DownloadAndVerifyAsync(release, archive, cancellationToken);
            await SafeZipExtractor.ExtractAsync(archive, staging, cancellationToken);
            var git = Path.Combine(staging, "cmd", "git.exe");
            if (!File.Exists(git))
            {
                throw new InvalidDataException("The verified MinGit package did not contain cmd\\git.exe.");
            }
            var versionOutput = await RunVersionAsync(git, cancellationToken);
            if (!versionOutput.StartsWith("git version ", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The verified MinGit executable did not report a version.");
            }
            await File.WriteAllTextAsync(
                Path.Combine(staging, "herdme-runtime.json"),
                JsonSerializer.Serialize(new
                {
                    runtime = "git",
                    release.Version,
                    archive = release.FileName,
                    sha256 = release.Sha256.ToLowerInvariant()
                }),
                cancellationToken
            );

            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
            return GitExecutable(release.Version);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static async Task DownloadAndVerifyAsync(
        GitWindowsRelease release,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await using var source = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1_024];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            total = checked(total + count);
            if (total > release.Size)
            {
                throw new InvalidDataException("The MinGit archive exceeded its published size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (total != release.Size
            || !actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "MinGit did not match its official size and SHA-256 checksum."
            );
        }
    }

    private static async Task<string> RunVersionAsync(
        string executable,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The verified MinGit executable could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = ((await standardOutput) + Environment.NewLine + (await standardError)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The verified MinGit executable failed with exit code {process.ExitCode}: {output}"
            );
        }
        return output;
    }

    [GeneratedRegex(
        @"^MinGit-(?<version>[0-9]+(?:\.[0-9]+){2,3})-64-bit\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    )]
    private static partial Regex MinGitArchivePattern();
}
