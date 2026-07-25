using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public static class SitePresentation
{
    private const int DesktopPreviewWidth = 1_440;
    private const int DesktopPreviewHeight = 934;

    public static IReadOnlyList<SiteRecord> Filter(
        IEnumerable<SiteRecord> sites,
        string? query
    )
    {
        var value = query?.Trim() ?? string.Empty;
        if (value.Length == 0) return sites.ToList();
        return sites.Where(site =>
            site.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
            || site.Domain.Contains(value, StringComparison.OrdinalIgnoreCase)
            || site.Framework.Contains(value, StringComparison.OrdinalIgnoreCase)
            || site.Path.Contains(value, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    public static Uri SiteUri(
        SiteRecord site,
        bool environmentRunning,
        int? httpPort,
        int? httpsPort
    )
    {
        var secure = environmentRunning && httpsPort is not null;
        var port = secure ? httpsPort : httpPort;
        return new UriBuilder(secure ? "https" : "http", site.Domain)
        {
            Port = port is null || port == (secure ? 443 : 80) ? -1 : port.Value
        }.Uri;
    }

    public static string DisplayAddress(
        SiteRecord site,
        bool environmentRunning,
        int? httpPort,
        int? httpsPort
    )
    {
        var scheme = SiteUri(site, environmentRunning, httpPort, httpsPort).Scheme;
        return $"{scheme}://{site.Domain}";
    }

    public static Uri DebugUri(
        SiteRecord site,
        bool environmentRunning,
        int? httpPort,
        int? httpsPort,
        string ideKey
    )
    {
        var builder = new UriBuilder(SiteUri(site, environmentRunning, httpPort, httpsPort));
        var trigger = "XDEBUG_TRIGGER=" + Uri.EscapeDataString(ideKey.Trim());
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? trigger
            : builder.Query.TrimStart('?') + "&" + trigger;
        return builder.Uri;
    }

    public static string RuntimeLabel(SiteRecord site)
    {
        return string.Join("  ", new[]
        {
            site.PhpVersion is null ? "Default PHP" : $"PHP {site.PhpVersion}",
            site.NodeVersion is null ? "Project Node.js" : $"Node.js {site.NodeVersion}"
        });
    }

    public static string DesktopPreviewMetricsJson(double previewWidth)
    {
        var scale = double.IsFinite(previewWidth) && previewWidth > 0
            ? Math.Clamp(previewWidth / DesktopPreviewWidth, 0.1, 1.0)
            : 1.0;
        return JsonSerializer.Serialize(new
        {
            width = DesktopPreviewWidth,
            height = DesktopPreviewHeight,
            deviceScaleFactor = 1,
            mobile = false,
            scale
        });
    }
}
