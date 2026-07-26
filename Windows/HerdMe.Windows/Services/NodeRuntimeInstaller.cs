using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class NodeRuntimeInstaller
{
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NodeRuntimeInstaller(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string RuntimeRoot => Path.Combine(SupportRoot, "Runtimes", "node");

    public string SettingsPath => Path.Combine(SupportRoot, "Config", "node.json");

    public NodeRuntimeSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<NodeRuntimeSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SetActive(string version)
    {
        var directory = Path.Combine(RuntimeRoot, version);
        if (!File.Exists(Path.Combine(directory, "node.exe")))
        {
            throw new InvalidOperationException($"HerdMe Node.js {version} is not installed.");
        }
        var settingsDirectory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(settingsDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new NodeRuntimeSettings { ActiveVersion = version }, JsonOptions)
        );
        File.Move(temporary, SettingsPath, true);
    }

    public string? InstalledVersion(string major)
    {
        if (!Directory.Exists(RuntimeRoot)) return null;
        return Directory.EnumerateDirectories(RuntimeRoot)
            .Select(Path.GetFileName)
            .Where(version => version is not null
                && version.StartsWith(major + ".", StringComparison.Ordinal)
                && File.Exists(Path.Combine(RuntimeRoot, version, "node.exe")))
            .OrderByDescending(version => Version.Parse(version!))
            .FirstOrDefault();
    }

    public IReadOnlyList<string> InstalledVersions()
    {
        if (!Directory.Exists(RuntimeRoot)) return [];
        return Directory.EnumerateDirectories(RuntimeRoot)
            .Select(Path.GetFileName)
            .Where(version => version is not null
                && Version.TryParse(version, out _)
                && File.Exists(Path.Combine(RuntimeRoot, version, "node.exe")))
            .Select(version => version!)
            .OrderByDescending(Version.Parse)
            .ToList();
    }

    public async Task<string> EnsureActiveRuntimeAsync(
        string fallbackMajor = "22",
        CancellationToken cancellationToken = default
    )
    {
        var active = LoadSettings().ActiveVersion;
        if (!string.IsNullOrWhiteSpace(active))
        {
            var activeDirectory = Path.Combine(RuntimeRoot, active);
            if (File.Exists(Path.Combine(activeDirectory, "node.exe"))
                && File.Exists(Path.Combine(activeDirectory, "npm.cmd")))
            {
                return activeDirectory;
            }
        }

        var installed = InstalledVersions().FirstOrDefault();
        if (installed is not null)
        {
            SetActive(installed);
            return Path.Combine(RuntimeRoot, installed);
        }

        var release = await InstallAsync(fallbackMajor, cancellationToken);
        var installedDirectory = Path.Combine(RuntimeRoot, release.Version);
        if (!File.Exists(Path.Combine(installedDirectory, "npm.cmd")))
        {
            throw new InvalidDataException("The verified Node.js package did not contain npm.cmd.");
        }
        return installedDirectory;
    }

    public async Task<NodeWindowsRelease> ResolveReleaseAsync(
        string major,
        CancellationToken cancellationToken = default
    )
    {
        var releases = await HttpClient.GetFromJsonAsync<List<NodeRelease>>(
            "https://nodejs.org/dist/index.json",
            cancellationToken
        ) ?? throw new InvalidDataException("The Node.js release index was empty.");
        var release = releases.FirstOrDefault(candidate =>
            candidate.Version.StartsWith($"v{major}.", StringComparison.Ordinal)
            && candidate.Files.Contains("win-x64-zip", StringComparer.Ordinal)
        ) ?? throw new InvalidOperationException($"No Windows Node.js {major} release was found.");

        var archiveName = $"node-{release.Version}-win-x64.zip";
        var checksums = await HttpClient.GetStringAsync(
            $"https://nodejs.org/dist/{release.Version}/SHASUMS256.txt",
            cancellationToken
        );
        var checksumLine = checksums.Split('\n').FirstOrDefault(line =>
            line.TrimEnd().EndsWith("  " + archiveName, StringComparison.Ordinal)
        );
        var sha256 = checksumLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha256 is null || sha256.Length != 64)
        {
            throw new InvalidDataException("The Node.js checksum list did not contain the Windows archive.");
        }
        return new NodeWindowsRelease(
            major,
            release.Version.TrimStart('v'),
            archiveName,
            sha256,
            new Uri($"https://nodejs.org/dist/{release.Version}/{archiveName}")
        );
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveLatestVersionsAsync(
        IEnumerable<string> majors,
        CancellationToken cancellationToken = default
    )
    {
        var releases = await HttpClient.GetFromJsonAsync<List<NodeRelease>>(
            "https://nodejs.org/dist/index.json",
            cancellationToken
        ) ?? throw new InvalidDataException("The Node.js release index was empty.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var major in majors.Distinct(StringComparer.Ordinal))
        {
            var release = releases.FirstOrDefault(candidate =>
                candidate.Version.StartsWith($"v{major}.", StringComparison.Ordinal)
                && candidate.Files.Contains("win-x64-zip", StringComparer.Ordinal)
            );
            if (release is not null)
            {
                result[major] = RuntimeVersionComparison.Normalize(release.Version);
            }
        }
        return result;
    }

    public async Task<NodeWindowsRelease> InstallAsync(
        string major,
        CancellationToken cancellationToken = default
    )
    {
        var release = await ResolveReleaseAsync(major, cancellationToken);
        var cache = Path.Combine(SupportRoot, "Cache", "node");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(RuntimeRoot);
        var archive = Path.Combine(cache, $"{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(RuntimeRoot, $".install-{Guid.NewGuid():N}");
        var destination = Path.Combine(RuntimeRoot, release.Version);
        try
        {
            await DownloadAndVerifyAsync(release, archive, cancellationToken);
            await SafeZipExtractor.ExtractAsync(archive, staging, cancellationToken);
            var extracted = Directory.EnumerateDirectories(staging).SingleOrDefault()
                ?? throw new InvalidDataException("The Node.js archive layout was invalid.");
            if (!File.Exists(Path.Combine(extracted, "node.exe")))
            {
                throw new InvalidDataException("The Node.js archive did not contain node.exe.");
            }
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(extracted, destination);
            SetActive(release.Version);
            return release;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public void Remove(string version)
    {
        var destination = Path.Combine(RuntimeRoot, version);
        if (!Directory.Exists(destination)) return;
        Directory.Delete(destination, true);
        var settings = LoadSettings();
        if (settings.ActiveVersion == version) SetActiveFallback();
    }

    private void SetActiveFallback()
    {
        var fallback = Directory.Exists(RuntimeRoot)
            ? Directory.EnumerateDirectories(RuntimeRoot)
                .Select(Path.GetFileName)
                .Where(version => version is not null && File.Exists(Path.Combine(RuntimeRoot, version, "node.exe")))
                .OrderByDescending(version => Version.Parse(version!))
                .FirstOrDefault()
            : null;
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            SettingsPath,
            JsonSerializer.Serialize(new NodeRuntimeSettings { ActiveVersion = fallback ?? string.Empty }, JsonOptions)
        );
    }

    private static async Task DownloadAndVerifyAsync(
        NodeWindowsRelease release,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await using var source = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1_024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Node.js archive did not match its official SHA-256 checksum.");
        }
    }
}
