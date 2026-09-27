using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HerdMe.Windows.Services;

public enum SiteProbeTone
{
    Success,
    Caution,
    Critical
}

public sealed record SiteProbeResult(int? StatusCode, long ElapsedMilliseconds, string? Error)
{
    public SiteProbeTone Tone => StatusCode switch
    {
        >= 200 and < 400 => SiteProbeTone.Success,
        >= 400 and < 500 => SiteProbeTone.Caution,
        _ => SiteProbeTone.Critical
    };
}

/// <summary>
/// The hover preview on a Sites row: one quick request to the local site for its status and
/// response time, and the path of the thumbnail captured from the live preview.
/// </summary>
public static class SiteQuickProbe
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    public static async Task<SiteProbeResult> ProbeAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            return new SiteProbeResult((int)response.StatusCode, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SiteProbeResult(null, stopwatch.ElapsedMilliseconds, error.Message);
        }
    }

    // "HTTP 200 - 84 ms", or the reason the site did not answer.
    public static string Describe(SiteProbeResult result)
    {
        if (result.StatusCode is not { } status)
        {
            return ServiceText.Format(
                "SitesPreviewNoAnswer",
                "No answer: {0}",
                string.IsNullOrWhiteSpace(result.Error) ? "-" : result.Error
            );
        }
        return ServiceText.Format(
            "SitesPreviewStatus",
            "HTTP {0} - {1} ms",
            status,
            Math.Max(0, result.ElapsedMilliseconds)
        );
    }

    // Thumbnails live under %LOCALAPPDATA%\HerdMe\Cache\thumbnails, one file per site path.
    public static string ThumbnailDirectory(string supportRoot) =>
        Path.Combine(supportRoot, "Cache", "thumbnails");

    public static string ThumbnailPath(string supportRoot, string sitePath)
    {
        var key = Path.GetFullPath(sitePath).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(ThumbnailDirectory(supportRoot), hash + ".png");
    }
}
