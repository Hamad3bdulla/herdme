using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record CloudflaredRelease(
    string Version,
    long Size,
    string Sha256,
    Uri DownloadUri
);

public sealed record SiteShare(string Domain, Uri PublicUri, DateTimeOffset StartedAt);

/// <summary>
/// Downloads the official cloudflared Windows binary from Cloudflare's GitHub releases and
/// verifies it against the SHA-256 digest GitHub publishes for the asset.
/// </summary>
public sealed partial class CloudflaredInstaller
{
    private const string LatestReleaseUri =
        "https://api.github.com/repos/cloudflare/cloudflared/releases/latest";
    internal const string AssetName = "cloudflared-windows-amd64.exe";
    private const long MaximumBinarySize = 256L * 1_024 * 1_024;
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();
    private readonly SemaphoreSlim installLock = new(1, 1);

    public CloudflaredInstaller(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string RuntimeRoot => Path.Combine(SupportRoot, "Runtimes", "cloudflared");

    public string? InstalledExecutable()
    {
        if (!Directory.Exists(RuntimeRoot)) return null;
        return Directory.EnumerateDirectories(RuntimeRoot)
            .Select(Path.GetFileName)
            .Where(version => version is not null
                && ReleaseVersionPattern().IsMatch(version)
                && File.Exists(Path.Combine(RuntimeRoot, version, "cloudflared.exe")))
            .Select(version => version!)
            .OrderByDescending(version => version, StringComparer.Ordinal)
            .Select(version => Path.Combine(RuntimeRoot, version, "cloudflared.exe"))
            .FirstOrDefault();
    }

    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        await installLock.WaitAsync(cancellationToken);
        try
        {
            if (InstalledExecutable() is { } installed) return installed;
            var metadata = await HttpClient.GetStringAsync(LatestReleaseUri, cancellationToken);
            return await InstallReleaseAsync(SelectRelease(metadata), cancellationToken);
        }
        finally
        {
            installLock.Release();
        }
    }

    internal static CloudflaredRelease SelectRelease(string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (version is null || !ReleaseVersionPattern().IsMatch(version))
        {
            throw new InvalidDataException("cloudflared release metadata did not contain a valid version.");
        }
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("cloudflared release metadata did not contain assets.");
        }
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            if (!string.Equals(name, AssetName, StringComparison.Ordinal)) continue;
            var digest = asset.TryGetProperty("digest", out var digestProperty)
                ? digestProperty.GetString()
                : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..]
                : null;
            var download = asset.TryGetProperty("browser_download_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;
            var size = asset.TryGetProperty("size", out var sizeProperty) ? sizeProperty.GetInt64() : 0;
            if (sha256 is not { Length: 64 }
                || !sha256.All(Uri.IsHexDigit)
                || size is <= 0 or > MaximumBinarySize
                || !Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !downloadUri.AbsolutePath.StartsWith(
                    "/cloudflare/cloudflared/releases/download/",
                    StringComparison.Ordinal
                ))
            {
                throw new InvalidDataException(
                    "cloudflared did not publish valid SHA-256 metadata for the Windows x64 binary."
                );
            }
            return new CloudflaredRelease(version, size, sha256, downloadUri);
        }
        throw new InvalidOperationException("No official Windows x64 cloudflared binary was found.");
    }

    private async Task<string> InstallReleaseAsync(
        CloudflaredRelease release,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(RuntimeRoot);
        var staging = Path.Combine(RuntimeRoot, $".install-{Guid.NewGuid():N}");
        var destination = Path.Combine(RuntimeRoot, release.Version);
        Directory.CreateDirectory(staging);
        try
        {
            var executable = Path.Combine(staging, "cloudflared.exe");
            await DownloadAndVerifyAsync(release, executable, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(staging, "herdme-runtime.json"),
                JsonSerializer.Serialize(new
                {
                    runtime = "cloudflared",
                    release.Version,
                    sha256 = release.Sha256.ToLowerInvariant()
                }),
                cancellationToken
            );
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
            return Path.Combine(destination, "cloudflared.exe");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static async Task DownloadAndVerifyAsync(
        CloudflaredRelease release,
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
                throw new InvalidDataException("The cloudflared binary exceeded its published size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (total != release.Size || !actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("cloudflared did not match its official size and SHA-256 checksum.");
        }
    }

    [GeneratedRegex(@"^[0-9]{4}\.[0-9]{1,2}\.[0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();
}

/// <summary>
/// Publishes a local site through a temporary Cloudflare quick tunnel. Sharing is always an
/// explicit per-site action; every tunnel stops when the user presses Stop, when the local
/// environment stops, and when HerdMe exits (the process lives in a kill-on-close job).
/// </summary>
public sealed partial class SiteShareManager : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private readonly CloudflaredInstaller installer;
    private readonly WindowsLocalEnvironment environment;
    private readonly ConcurrentDictionary<string, ActiveTunnel> tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private int disposed;

    public SiteShareManager(CloudflaredInstaller installer, WindowsLocalEnvironment environment)
    {
        this.installer = installer;
        this.environment = environment;
        environment.Stopped += Environment_Stopped;
    }

    private async void Environment_Stopped(object? sender, EventArgs e)
    {
        try
        {
            await StopAllAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            await DiagnosticLog.WriteFailureAsync(
                "share",
                "stop-with-environment",
                "A public share could not be stopped with the local sites.",
                error.ToString()
            );
        }
    }

    public event EventHandler? SharesChanged;

    // Raised when cloudflared ends a public share that the user did not stop.
    public event EventHandler<SiteShare>? ShareEndedUnexpectedly;

    public IReadOnlyList<SiteShare> Active => tunnels.Values.Select(tunnel => tunnel.Share).ToArray();

    public SiteShare? Find(string domain) =>
        tunnels.TryGetValue(NormalizeDomain(domain), out var tunnel) ? tunnel.Share : null;

    public async Task<SiteShare> StartAsync(string domain, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var key = NormalizeDomain(domain);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (tunnels.TryGetValue(key, out var existing) && !existing.Process.HasExited)
            {
                return existing.Share;
            }
            if (environment.HttpPort is not { } httpPort)
            {
                throw new InvalidOperationException(ServiceText.Get(
                    "ShareEnvironmentStopped",
                    "Start local sites before sharing."
                ));
            }
            var executable = await installer.EnsureInstalledAsync(cancellationToken);
            var tunnel = await LaunchAsync(executable, key, httpPort, cancellationToken);
            tunnels[key] = tunnel;
            tunnel.Process.EnableRaisingEvents = true;
            tunnel.Process.Exited += (_, _) => OnTunnelExited(key, tunnel);
            if (tunnel.Process.HasExited) OnTunnelExited(key, tunnel);
            SharesChanged?.Invoke(this, EventArgs.Empty);
            return tunnel.Share;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private void OnTunnelExited(string key, ActiveTunnel tunnel)
    {
        // cloudflared exited on its own (network loss, killed by the user): drop the share so
        // the UI never shows a public link that no longer works.
        if (!tunnels.TryRemove(new KeyValuePair<string, ActiveTunnel>(key, tunnel))) return;
        _ = tunnel.DisposeAsync(environment);
        SharesChanged?.Invoke(this, EventArgs.Empty);
        ShareEndedUnexpectedly?.Invoke(this, tunnel.Share);
    }

    public async Task StopAsync(string domain)
    {
        var key = NormalizeDomain(domain);
        if (tunnels.TryRemove(key, out var tunnel))
        {
            await tunnel.DisposeAsync(environment);
            SharesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAllAsync()
    {
        var active = tunnels.Keys.ToArray();
        foreach (var key in active)
        {
            if (tunnels.TryRemove(key, out var tunnel)) await tunnel.DisposeAsync(environment);
        }
        if (active.Length > 0) SharesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        environment.Stopped -= Environment_Stopped;
        await StopAllAsync();
    }

    /// <summary>Finds the public quick tunnel address in a cloudflared log line.</summary>
    internal static Uri? ParsePublicUri(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var match = QuickTunnelPattern().Match(line);
        return match.Success ? new Uri(match.Value.ToLowerInvariant() + "/") : null;
    }

    internal static IReadOnlyList<string> TunnelArguments(int httpPort) =>
    [
        "tunnel",
        "--no-autoupdate",
        "--url",
        $"http://127.0.0.1:{httpPort}"
    ];

    private async Task<ActiveTunnel> LaunchAsync(
        string executable,
        string domain,
        int httpPort,
        CancellationToken cancellationToken
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in TunnelArguments(httpPort)) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cloudflared could not be started.");
        var job = WindowsJobObject.TryAttach(process);
        var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recent = new ConcurrentQueue<string>();
        void Observe(string? line)
        {
            if (line is null) return;
            recent.Enqueue(line);
            while (recent.Count > 20) recent.TryDequeue(out _);
            if (ParsePublicUri(line) is { } uri) address.TrySetResult(uri);
        }
        process.OutputDataReceived += (_, data) => Observe(data.Data);
        process.ErrorDataReceived += (_, data) => Observe(data.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var exited = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(address.Task, exited).WaitAsync(StartTimeout, cancellationToken);
            if (first != address.Task)
            {
                throw new InvalidOperationException(
                    "cloudflared stopped before it created a tunnel: " + string.Join(" ", recent)
                );
            }
            var publicUri = await address.Task;
            environment.SetShareAlias(publicUri.Host, domain);
            return new ActiveTunnel(
                new SiteShare(domain, publicUri, DateTimeOffset.Now),
                process,
                job
            );
        }
        catch
        {
            StopProcess(process);
            job?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static string NormalizeDomain(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelPattern();

    private sealed record ActiveTunnel(SiteShare Share, Process Process, WindowsJobObject? Job)
    {
        public async Task DisposeAsync(WindowsLocalEnvironment environment)
        {
            environment.RemoveShareAlias(Share.PublicUri.Host);
            StopProcess(Process);
            try
            {
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }
            Job?.Dispose();
            Process.Dispose();
        }
    }
}
