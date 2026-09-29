using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record AppUpdatePackage(string Version, string InstallerPath, long Size, string Sha256);

internal sealed record AppInstallerAsset(Uri DownloadUri, string Sha256, long? Size);

/// <summary>
/// Downloads a HerdMe update inside the app: only the official GitHub release asset, checked
/// against the SHA-256 in the signed feed or, when the feed has none, the digest GitHub
/// publishes for that asset. Nothing runs until the user presses "Restart to update"; then the
/// per-user setup runs silently and starts HerdMe again (installer.iss /RELAUNCH=1).
/// </summary>
public sealed class AppSelfUpdater
{
    public const string OperationId = UpdatePreferencesStore.ApplicationId;
    internal const string ReleasePathPrefix = "/Hamad3bdulla/herdme/releases/download/";
    private const string ReleaseApiFormat = "https://api.github.com/repos/Hamad3bdulla/herdme/releases/tags/windows-v{0}";
    internal const long MaximumInstallerSize = 512L * 1024 * 1024;
    internal static readonly string[] SilentArguments = ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RELAUNCH=1"];

    private static readonly HttpClient DefaultHttpClient = ManagedDownloadClient.Create();

    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim downloadLock = new(1, 1);

    public AppSelfUpdater(string supportRoot, HttpClient? httpClient = null)
    {
        DownloadRoot = Path.Combine(supportRoot, "Cache", "app-update");
        this.httpClient = httpClient ?? DefaultHttpClient;
    }

    public string DownloadRoot { get; }

    // Downloaded and verified, waiting for "Restart to update".
    public AppUpdatePackage? Ready { get; private set; }

    public event EventHandler? Changed;

    public static bool CanInstall(AppUpdateRelease release) => TryGetInstallerUri(release, out _);

    // Only the official release download on github.com; anything else opens in the browser.
    public static bool TryGetInstallerUri(AppUpdateRelease release, out Uri installerUri)
    {
        installerUri = null!;
        if (!Uri.TryCreate(release.PlatformDownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }
        installerUri = uri;
        return true;
    }

    public async Task<AppUpdatePackage> DownloadAsync(
        AppUpdateRelease release,
        IProgress<ServiceInstallationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetInstallerUri(release, out var installerUri))
        {
            throw new InvalidOperationException(ServiceText.Get(
                "AppSelfUpdateUnsupported",
                "This update can only be downloaded in the browser."
            ));
        }
        await downloadLock.WaitAsync(cancellationToken);
        try
        {
            if (Ready is { } ready
                && ready.Version == release.Version
                && File.Exists(ready.InstallerPath))
            {
                return ready;
            }
            progress?.Report(new(OperationId, ServiceInstallationStage.Resolving));
            var asset = await ResolveAssetAsync(release, installerUri, cancellationToken);
            InstallationPreflight.EnsureStorage(DownloadRoot, (asset.Size ?? 256L * 1024 * 1024) + 128L * 1024 * 1024);
            RemoveOldDownloads(keepVersion: null);

            var fileName = Path.GetFileName(installerUri.AbsolutePath);
            var folder = Path.Combine(DownloadRoot, SafeVersion(release.Version));
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, fileName);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            AppUpdatePackage package;
            try
            {
                var (size, hash) = await DownloadAndHashAsync(asset, temporary, progress, cancellationToken);
                progress?.Report(new(OperationId, ServiceInstallationStage.Verifying, size, size));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)
                    || asset.Size is { } expected && expected != size)
                {
                    throw new InvalidDataException(ServiceText.Get(
                        "AppSelfUpdateChecksumFailed",
                        "The downloaded HerdMe setup did not match its official SHA-256 checksum."
                    ));
                }
                File.Move(temporary, destination, true);
                package = new AppUpdatePackage(release.Version, destination, size, hash.ToLowerInvariant());
                Ready = package;
            }
            finally
            {
                TryDeleteFile(temporary);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return package;
        }
        finally
        {
            downloadLock.Release();
        }
    }

    // Checks the file once more right before it runs, then starts setup silently. The caller
    // quits HerdMe next; setup waits for it (stop-for-update.ps1) and starts it again.
    public async Task LaunchInstallerAsync(AppUpdatePackage package, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(package.InstallerPath);
        if (!fullPath.StartsWith(Path.GetFullPath(DownloadRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new FileNotFoundException(ServiceText.Get(
                "AppSelfUpdateMissing",
                "The downloaded update is no longer available. Download it again."
            ));
        }
        await using (var stream = File.OpenRead(fullPath))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (stream.Length != package.Size || !actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Forget();
                throw new InvalidDataException(ServiceText.Get(
                    "AppSelfUpdateChecksumFailed",
                    "The downloaded HerdMe setup did not match its official SHA-256 checksum."
                ));
            }
        }
        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!
        };
        foreach (var argument in SilentArguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(ServiceText.Get(
                "AppSelfUpdateStartFailed",
                "HerdMe could not start the update."
            ));
    }

    public void Forget()
    {
        Ready = null;
        RemoveOldDownloads(keepVersion: null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // After an update (or when nothing newer is offered) the old setup files are removed.
    public void RemoveOldDownloads(string? keepVersion)
    {
        if (!Directory.Exists(DownloadRoot)) return;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(DownloadRoot).ToList())
            {
                if (keepVersion is not null
                    && string.Equals(Path.GetFileName(folder), SafeVersion(keepVersion), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Ready is { } ready
                    && ready.InstallerPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Directory.Delete(folder, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"HerdMe will remove old update downloads later: {error.Message}");
        }
    }

    private async Task<AppInstallerAsset> ResolveAssetAsync(
        AppUpdateRelease release,
        Uri installerUri,
        CancellationToken cancellationToken
    )
    {
        if (IsSha256(release.PlatformSha256))
        {
            return new AppInstallerAsset(installerUri, release.PlatformSha256!, null);
        }
        var metadataUri = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ReleaseApiFormat,
            Uri.EscapeDataString(release.Version)
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadAsStringAsync(cancellationToken);
        return SelectAsset(metadata, installerUri);
    }

    // The asset GitHub lists for the same file name and URL, with its "sha256:" digest.
    internal static AppInstallerAsset SelectAsset(string metadata, Uri installerUri)
    {
        var fileName = Path.GetFileName(installerUri.AbsolutePath);
        using var document = JsonDocument.Parse(metadata);
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The HerdMe release did not list its downloads.");
        }
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            if (!string.Equals(name, fileName, StringComparison.Ordinal)) continue;
            var digest = asset.TryGetProperty("digest", out var digestProperty) && digestProperty.ValueKind == JsonValueKind.String
                ? digestProperty.GetString()
                : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            var size = asset.TryGetProperty("size", out var sizeProperty) && sizeProperty.ValueKind == JsonValueKind.Number
                ? sizeProperty.GetInt64()
                : 0;
            var download = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;
            if (!IsSha256(sha256)
                || size is <= 0 or > MaximumInstallerSize
                || !Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)
                || downloadUri != installerUri)
            {
                throw new InvalidDataException(ServiceText.Get(
                    "AppSelfUpdateNoChecksum",
                    "The HerdMe release has no SHA-256 checksum for this download, so it opens in the browser instead."
                ));
            }
            return new AppInstallerAsset(downloadUri, sha256!, size);
        }
        throw new InvalidDataException(ServiceText.Get(
            "AppSelfUpdateNoChecksum",
            "The HerdMe release has no SHA-256 checksum for this download, so it opens in the browser instead."
        ));
    }

    private async Task<(long Size, string Sha256)> DownloadAndHashAsync(
        AppInstallerAsset asset,
        string destination,
        IProgress<ServiceInstallationProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        using var response = await httpClient.GetAsync(asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var limit = asset.Size ?? MaximumInstallerSize;
        var total = asset.Size ?? response.Content.Headers.ContentLength;
        if (total is > MaximumInstallerSize) throw new InvalidDataException("The HerdMe setup is larger than expected.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            received = checked(received + count);
            if (received > limit) throw new InvalidDataException("The HerdMe setup exceeded its published size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
            if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
            {
                lastReport = clock.Elapsed;
                var speed = clock.Elapsed.TotalSeconds > 0 ? received / clock.Elapsed.TotalSeconds : 0;
                progress?.Report(new(OperationId, ServiceInstallationStage.Downloading, received, total, 1, speed));
            }
        }
        return (received, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string SafeVersion(string version) =>
        new(version.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-').ToArray());

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"HerdMe will remove {path} later: {error.Message}");
        }
    }
}
