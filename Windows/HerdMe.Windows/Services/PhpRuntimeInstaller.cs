using System.Security.Cryptography;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record PhpWindowsRelease(
    string Cycle,
    string Version,
    string FileName,
    string Sha256,
    Uri DownloadUri
);

public sealed class PhpRuntimeInstaller
{
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CoreClient coreClient;

    public static IReadOnlyList<string> SupportedCycles { get; } = Array.AsReadOnly(
        new[] { "8.5", "8.4", "8.3", "8.2", "8.1", "8.0" }
    );

    public PhpRuntimeInstaller(CoreClient? coreClient = null, string? supportRoot = null)
    {
        this.coreClient = coreClient ?? new CoreClient();
        RuntimeRoot = Path.Combine(
            supportRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HerdMe"
            ),
            "Runtimes",
            "php"
        );
    }

    public string RuntimeRoot { get; }

    public static bool IsSupportedCycle(string cycle)
    {
        return SupportedCycles.Contains(cycle, StringComparer.Ordinal);
    }

    public string PhpExecutable(string cycle) => Path.Combine(RuntimeRoot, cycle, "php.exe");

    public string PhpCgiExecutable(string cycle) => Path.Combine(RuntimeRoot, cycle, "php-cgi.exe");

    public bool IsInstalled(string cycle)
    {
        return File.Exists(PhpExecutable(cycle)) && File.Exists(PhpCgiExecutable(cycle));
    }

    public string? InstalledVersion(string cycle)
    {
        if (!IsInstalled(cycle)) return null;
        var manifestPath = Path.Combine(RuntimeRoot, cycle, "herdme-runtime.json");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var version = manifest.RootElement.GetProperty("version").GetString();
            return string.IsNullOrWhiteSpace(version)
                ? null
                : RuntimeVersionComparison.Normalize(version);
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> InstalledCycles()
    {
        if (!Directory.Exists(RuntimeRoot)) return [];
        return Directory.EnumerateDirectories(RuntimeRoot)
            .Select(Path.GetFileName)
            .Where(cycle => cycle is not null && IsInstalled(cycle))
            .Select(cycle => cycle!)
            .OrderByDescending(cycle => Version.TryParse(cycle, out var version) ? version : new Version())
            .ToList();
    }

    public async Task<PhpWindowsRelease> ResolveReleaseAsync(
        string cycle,
        CancellationToken cancellationToken = default
    )
    {
        EnsureSupportedCycle(cycle);
        using var stream = await HttpClient.GetStreamAsync(
            "https://windows.php.net/downloads/releases/releases.json",
            cancellationToken
        );
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(cycle, out var release))
        {
            throw new InvalidOperationException($"No supported Windows PHP {cycle} release was found.");
        }

        var version = release.GetProperty("version").GetString();
        JsonElement package = default;
        var found = false;
        foreach (var property in release.EnumerateObject())
        {
            if (property.Name.StartsWith("nts-", StringComparison.OrdinalIgnoreCase)
                && property.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetProperty("zip", out package))
            {
                found = true;
                break;
            }
        }
        if (!found || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"No 64-bit NTS Windows PHP {cycle} package was found.");
        }

        var fileName = package.GetProperty("path").GetString();
        var sha256 = package.GetProperty("sha256").GetString();
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sha256))
        {
            throw new InvalidDataException("The Windows PHP release metadata is incomplete.");
        }
        return new PhpWindowsRelease(
            cycle,
            version,
            fileName,
            sha256,
            new Uri($"https://windows.php.net/downloads/releases/{fileName}")
        );
    }

    public async Task<PhpWindowsRelease> InstallAsync(
        string cycle,
        CancellationToken cancellationToken = default
    )
    {
        EnsureSupportedCycle(cycle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows PHP packages can only be installed on Windows.");
        }

        var release = await ResolveReleaseAsync(cycle, cancellationToken);
        var supportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var cachePath = Path.Combine(supportPath, "Cache", "php");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(RuntimeRoot);
        var archivePath = Path.Combine(cachePath, $"{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Combine(RuntimeRoot, $".install-{Guid.NewGuid():N}");
        var destination = Path.Combine(RuntimeRoot, cycle);
        var backupPath = Path.Combine(RuntimeRoot, $".backup-{Guid.NewGuid():N}");
        try
        {
            await DownloadAndVerifyAsync(release, archivePath, cancellationToken);
            await SafeZipExtractor.ExtractAsync(
                archivePath,
                stagingPath,
                cancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, "php.ini"),
                PhpIni,
                cancellationToken
            );
            var php = Path.Combine(stagingPath, "php.exe");
            var phpCgi = Path.Combine(stagingPath, "php-cgi.exe");
            if (!File.Exists(php) || !File.Exists(phpCgi))
            {
                throw new InvalidDataException("The official PHP package did not contain php.exe and php-cgi.exe.");
            }

            var extensions = await coreClient.ValidatePhpAsync(php, cancellationToken);
            if (!extensions.Compatible)
            {
                throw new InvalidOperationException(
                    "The PHP package is missing Laravel extensions: "
                    + string.Join(", ", extensions.Missing)
                );
            }
            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, "herdme-runtime.json"),
                JsonSerializer.Serialize(new
                {
                    runtime = "php",
                    cycle = release.Cycle,
                    version = release.Version,
                    source = release.DownloadUri.ToString(),
                    sha256 = release.Sha256
                }, JsonOptions),
                cancellationToken
            );

            if (Directory.Exists(destination)) Directory.Move(destination, backupPath);
            try
            {
                Directory.Move(stagingPath, destination);
                if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
            }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, destination);
                }
                throw;
            }
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
        }
        return release;
    }

    private static void EnsureSupportedCycle(string cycle)
    {
        if (!IsSupportedCycle(cycle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycle),
                cycle,
                "HerdMe supports new PHP installations from 8.0 through 8.5."
            );
        }
    }

    private static async Task DownloadAndVerifyAsync(
        PhpWindowsRelease release,
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
            throw new InvalidDataException("The Windows PHP archive did not match its official SHA-256 checksum.");
        }
    }

    private const string PhpIni = """
        [PHP]
        extension_dir = "ext"
        extension = curl
        extension = fileinfo
        extension = mbstring
        extension = openssl
        date.timezone = UTC
        cgi.fix_pathinfo = 1
        expose_php = Off
        display_errors = On
        display_startup_errors = On
        log_errors = On
        variables_order = "GPCS"
        """;
}
