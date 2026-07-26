using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerdMe.Windows.Services;

public sealed record AppUpdateDownloadUrls(
    [property: JsonPropertyName("macOS")] string? MacOS,
    [property: JsonPropertyName("windowsX64")] string? WindowsX64
);

public sealed record AppUpdateRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("downloadURL")] string? DownloadUrl,
    [property: JsonPropertyName("downloadURLs")] AppUpdateDownloadUrls? DownloadUrls = null
)
{
    [JsonIgnore]
    public string? PlatformDownloadUrl => DownloadUrls?.WindowsX64 ?? DownloadUrl;
}

public sealed record AppUpdateCheck(
    string CurrentVersion,
    int CurrentBuild,
    AppUpdateRelease? AvailableRelease
)
{
    public bool IsAvailable => AvailableRelease is not null;
}

public sealed class AppUpdateManager
{
    private const string SignatureAlgorithm = "ECDSA_P256_SHA256";
    private const int MaximumFeedSize = 4 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string feedLocation;
    private readonly string currentVersion;
    private readonly int currentBuild;
    private readonly byte[]? verificationKey;

    public AppUpdateManager(
        string feedLocation,
        string currentVersion,
        int currentBuild,
        string? publicKey = null
    )
    {
        this.feedLocation = feedLocation;
        this.currentVersion = currentVersion;
        this.currentBuild = currentBuild;
        verificationKey = DecodePublicKey(publicKey);
    }

    public static AppUpdateManager Configured(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(AppUpdateManager).Assembly;
        var assemblyVersion = assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}"
            : informationalVersion;
        var build = Math.Max(assemblyVersion.Revision, 0);
        string? feed = null;
        string? publicKey = null;
#if DEBUG
        feed = Environment.GetEnvironmentVariable("HERDME_UPDATE_FEED_URL");
        publicKey = Environment.GetEnvironmentVariable("HERDME_UPDATE_PUBLIC_KEY");
#endif
        if (string.IsNullOrWhiteSpace(feed))
        {
            var feedConfigurationPath = Path.Combine(AppContext.BaseDirectory, "release-feed-url.txt");
            var configuredFeed = File.Exists(feedConfigurationPath)
                ? File.ReadAllText(feedConfigurationPath).Trim()
                : null;
            feed = Uri.TryCreate(configuredFeed, UriKind.Absolute, out var configuredUri)
                && configuredUri.Scheme == Uri.UriSchemeHttps
                    ? configuredFeed
                    : Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
        }
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            var publicKeyPath = Path.Combine(AppContext.BaseDirectory, "release-public-key.txt");
            if (File.Exists(publicKeyPath))
            {
                publicKey = File.ReadAllText(publicKeyPath);
            }
        }
        return new AppUpdateManager(feed, version, build, publicKey);
    }

    public async Task<AppUpdateCheck> CheckAsync(
        string channel,
        CancellationToken cancellationToken = default
    )
    {
        var data = await ReadFeedAsync(cancellationToken);
        var manifest = DecodeManifest(data, IsRemoteFeed(feedLocation));
        var normalizedChannel = channel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        var acceptedChannels = normalizedChannel == "beta"
            ? new HashSet<string>(["stable", "beta"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["stable"], StringComparer.OrdinalIgnoreCase);
        var latest = manifest.Releases
            .Where(release => acceptedChannels.Contains(release.Channel))
            .OrderByDescending(release => release, AppUpdateReleaseComparer.Instance)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"No {normalizedChannel} release is available in the update feed."
            );
        var available = IsNewer(latest) ? latest : null;
        return new AppUpdateCheck(currentVersion, currentBuild, available);
    }

    private async Task<byte[]> ReadFeedAsync(CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(feedLocation, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                using var response = await HttpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                );
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumFeedSize)
                {
                    throw new InvalidDataException("The update feed is too large.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await ReadBoundedAsync(input, cancellationToken);
            }
            if (uri.IsFile)
            {
                return await ReadFileAsync(uri.LocalPath, cancellationToken);
            }
        }
        return await ReadFileAsync(feedLocation, cancellationToken);
    }

    private AppUpdateManifest DecodeManifest(byte[] data, bool requireSignature)
    {
        AppUpdateSignedEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AppUpdateSignedEnvelope>(data, JsonOptions);
        }
        catch (JsonException)
        {
            envelope = null;
        }
        if (envelope is not null
            && envelope.Algorithm is not null
            && envelope.Payload is not null
            && envelope.Signature is not null)
        {
            return VerifyEnvelope(envelope);
        }
        if (requireSignature)
        {
            throw new InvalidDataException("The remote update feed is not signed and was rejected.");
        }
        return ValidateManifest(
            JsonSerializer.Deserialize<AppUpdateManifest>(data, JsonOptions)
                ?? throw new InvalidDataException("The update feed is invalid."),
            requirePlatformDownloads: false
        );
    }

    private AppUpdateManifest VerifyEnvelope(AppUpdateSignedEnvelope envelope)
    {
        if (envelope.Payload is not { } encodedPayload
            || envelope.Signature is not { } encodedSignature)
        {
            throw new InvalidDataException("The update feed signature is invalid.");
        }
        if (!string.Equals(envelope.Algorithm, SignatureAlgorithm, StringComparison.Ordinal)
            || verificationKey is null)
        {
            throw new InvalidDataException(
                verificationKey is null
                    ? "This build does not contain the public key required to verify updates."
                    : "The update feed signature is invalid."
            );
        }
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(encodedPayload);
            signature = Convert.FromBase64String(encodedSignature);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The update feed signature is invalid.", error);
        }

        try
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = verificationKey[1..33],
                    Y = verificationKey[33..65]
                }
            };
            using var verifier = ECDsa.Create(parameters);
            if (!verifier.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence
                ))
            {
                throw new InvalidDataException("The update feed signature is invalid.");
            }
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("The update feed signature is invalid.", error);
        }
        if (payload.Length > MaximumFeedSize)
        {
            throw new InvalidDataException("The signed update feed is too large.");
        }
        return ValidateManifest(
            JsonSerializer.Deserialize<AppUpdateManifest>(payload, JsonOptions)
                ?? throw new InvalidDataException("The signed update feed is invalid."),
            requirePlatformDownloads: true
        );
    }

    private static AppUpdateManifest ValidateManifest(
        AppUpdateManifest manifest,
        bool requirePlatformDownloads
    )
    {
        if (manifest.Releases is null)
        {
            throw new InvalidDataException("The update feed has no release collection.");
        }
        foreach (var release in manifest.Releases)
        {
            var channelIsValid = string.Equals(
                    release.Channel,
                    "stable",
                    StringComparison.OrdinalIgnoreCase
                ) || string.Equals(release.Channel, "beta", StringComparison.OrdinalIgnoreCase);
            var legacyDownloadIsValid = IsValidHttpsUrl(release.DownloadUrl, required: false);
            var platformDownloadsAreComplete = HasCompletePlatformDownloads(release);
            if (release.Build < 0
                || string.IsNullOrWhiteSpace(release.Version)
                || !RuntimeVersionComparison.IsSemanticVersion(release.Version)
                || release.Notes is null
                || !channelIsValid
                || !legacyDownloadIsValid
                || release.DownloadUrls is not null && !platformDownloadsAreComplete)
            {
                throw new InvalidDataException("The update feed contains an invalid release.");
            }
            if (requirePlatformDownloads && !platformDownloadsAreComplete)
            {
                throw new InvalidDataException(
                    "The signed update feed must contain HTTPS downloads for macOS and Windows x64."
                );
            }
        }
        return manifest;
    }

    private static bool HasCompletePlatformDownloads(AppUpdateRelease release)
    {
        return release.DownloadUrls is { } downloads
            && TryGetHttpsUri(downloads.MacOS, out var macOSUri)
            && TryGetHttpsUri(downloads.WindowsX64, out var windowsUri)
            && !macOSUri.Equals(windowsUri);
    }

    private static bool IsValidHttpsUrl(string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value)) return !required;
        return TryGetHttpsUri(value, out _);
    }

    private static bool TryGetHttpsUri(string? value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static async Task<byte[]> ReadFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var file = new FileInfo(path);
        if (file.Length > MaximumFeedSize)
        {
            throw new InvalidDataException("The update feed is too large.");
        }
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        CancellationToken cancellationToken
    )
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumFeedSize)
            {
                throw new InvalidDataException("The update feed is too large.");
            }
            output.Write(buffer, 0, count);
        }
    }

    private static byte[]? DecodePublicKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value.Trim());
            return key.Length == 65 && key[0] == 4 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsRemoteFeed(string location)
    {
        return Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }

    private bool IsNewer(AppUpdateRelease release)
    {
        var versionOrder = RuntimeVersionComparison.Compare(release.Version, currentVersion);
        return versionOrder > 0 || versionOrder == 0 && release.Build > currentBuild;
    }

    private sealed class AppUpdateReleaseComparer : IComparer<AppUpdateRelease>
    {
        public static AppUpdateReleaseComparer Instance { get; } = new();

        public int Compare(AppUpdateRelease? left, AppUpdateRelease? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var versionOrder = RuntimeVersionComparison.Compare(left.Version, right.Version);
            return versionOrder != 0 ? versionOrder : left.Build.CompareTo(right.Build);
        }
    }

    private sealed class AppUpdateManifest
    {
        [JsonPropertyName("releases")]
        public List<AppUpdateRelease> Releases { get; init; } = [];
    }

    private sealed class AppUpdateSignedEnvelope
    {
        [JsonPropertyName("algorithm")]
        public string? Algorithm { get; init; }

        [JsonPropertyName("payload")]
        public string? Payload { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }
    }
}
