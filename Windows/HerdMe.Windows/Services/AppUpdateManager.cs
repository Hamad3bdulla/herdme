using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HerdMe.Windows.Services;

public sealed record AppUpdateRelease(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("downloadURL")] string? DownloadUrl
);

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
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string feedLocation;
    private readonly string currentVersion;
    private readonly int currentBuild;

    public AppUpdateManager(string feedLocation, string currentVersion, int currentBuild)
    {
        this.feedLocation = feedLocation;
        this.currentVersion = currentVersion;
        this.currentBuild = currentBuild;
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
        var feed = Environment.GetEnvironmentVariable("HERDME_UPDATE_FEED_URL");
        if (string.IsNullOrWhiteSpace(feed))
        {
            feed = Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
        }
        return new AppUpdateManager(feed, version, build);
    }

    public async Task<AppUpdateCheck> CheckAsync(
        string channel,
        CancellationToken cancellationToken = default
    )
    {
        var json = await ReadFeedAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("The update feed is invalid.");
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

    private async Task<string> ReadFeedAsync(CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(feedLocation, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                using var response = await HttpClient.GetAsync(uri, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            if (uri.IsFile)
            {
                return await File.ReadAllTextAsync(uri.LocalPath, cancellationToken);
            }
        }
        return await File.ReadAllTextAsync(feedLocation, cancellationToken);
    }

    private bool IsNewer(AppUpdateRelease release)
    {
        var versionOrder = CompareVersions(release.Version, currentVersion);
        return versionOrder > 0 || versionOrder == 0 && release.Build > currentBuild;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ParseVersion(left);
        var rightParts = ParseVersion(right);
        for (var index = 0; index < Math.Max(leftParts.Count, rightParts.Count); index++)
        {
            var leftPart = index < leftParts.Count ? leftParts[index] : 0;
            var rightPart = index < rightParts.Count ? rightParts[index] : 0;
            var order = leftPart.CompareTo(rightPart);
            if (order != 0) return order;
        }
        return 0;
    }

    private static IReadOnlyList<int> ParseVersion(string value)
    {
        var core = value.Trim().TrimStart('v').Split(['-', '+'], 2)[0];
        var parts = core.Split('.');
        if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _)))
        {
            throw new InvalidDataException($"Invalid release version: {value}");
        }
        return parts.Select(int.Parse).ToArray();
    }

    private sealed class AppUpdateReleaseComparer : IComparer<AppUpdateRelease>
    {
        public static AppUpdateReleaseComparer Instance { get; } = new();

        public int Compare(AppUpdateRelease? left, AppUpdateRelease? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var versionOrder = CompareVersions(left.Version, right.Version);
            return versionOrder != 0 ? versionOrder : left.Build.CompareTo(right.Build);
        }
    }

    private sealed class AppUpdateManifest
    {
        [JsonPropertyName("releases")]
        public List<AppUpdateRelease> Releases { get; init; } = [];
    }
}
