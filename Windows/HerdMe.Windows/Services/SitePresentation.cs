using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record SiteGitStatus(bool IsRepository, string? Branch, int ChangeCount)
{
    public static SiteGitStatus Unavailable { get; } = new(false, null, 0);
}

internal sealed class SiteScanGeneration
{
    private int current;

    public int Begin()
    {
        return Interlocked.Increment(ref current);
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref current);
    }

    public bool IsCurrent(int generation)
    {
        return generation == Volatile.Read(ref current);
    }
}

public static class SitePresentation
{
    private const int DesktopPreviewWidth = 1_440;
    private const int DesktopPreviewHeight = 934;
    private static readonly TimeSpan GitInspectionTimeout = TimeSpan.FromSeconds(5);

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
            || site.GitSummary?.Contains(value, StringComparison.OrdinalIgnoreCase) == true
        ).ToList();
    }

    public static async Task<IReadOnlyDictionary<string, SiteGitStatus>> InspectGitStatusesAsync(
        IEnumerable<SiteRecord> sites,
        CancellationToken cancellationToken = default
    )
    {
        var records = sites.ToArray();
        using var limiter = new SemaphoreSlim(4);
        var tasks = records.Select(async site =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                return (site.Path, Status: await InspectGitAsync(site.Path, cancellationToken));
            }
            finally
            {
                limiter.Release();
            }
        }).ToArray();
        var inspected = await Task.WhenAll(tasks);
        var result = new Dictionary<string, SiteGitStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inspected) result[item.Path] = item.Status;
        return result;
    }

    public static async Task<SiteGitStatus> InspectGitAsync(
        string sitePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(sitePath)) return SiteGitStatus.Unavailable;
        var executable = OperatingSystem.IsWindows() ? "git.exe" : "/usr/bin/git";
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(sitePath);
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain=v1");
        startInfo.ArgumentList.Add("--branch");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return SiteGitStatus.Unavailable;
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitInspectionTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopProcess(process);
                await process.WaitForExitAsync();
                await Task.WhenAll(standardOutput, standardError);
                return SiteGitStatus.Unavailable;
            }
            catch (OperationCanceledException)
            {
                StopProcess(process);
                await process.WaitForExitAsync();
                await Task.WhenAll(standardOutput, standardError);
                throw;
            }

            var output = await standardOutput;
            _ = await standardError;
            return process.ExitCode == 0
                ? ParseGitStatus(output)
                : SiteGitStatus.Unavailable;
        }
        catch (Exception error) when (
            error is Win32Exception or IOException or InvalidOperationException
        )
        {
            return SiteGitStatus.Unavailable;
        }
    }

    internal static SiteGitStatus ParseGitStatus(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var header = lines.FirstOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal));
        var branch = header is null ? null : ParseBranch(header[3..]);
        return new SiteGitStatus(
            true,
            branch,
            lines.Count(line => !line.StartsWith("## ", StringComparison.Ordinal))
        );
    }

    private static string? ParseBranch(string value)
    {
        foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..];
        }
        if (value.StartsWith("HEAD ", StringComparison.Ordinal)) return null;
        return value.Split("...", StringSplitOptions.None)[0];
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
        }
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

    public static string RuntimeLabel(
        SiteRecord site,
        string defaultPhpLabel = "Default PHP",
        string projectNodeLabel = "Project Node.js"
    )
    {
        return string.Join("  ", new[]
        {
            site.PhpVersion is null ? defaultPhpLabel : $"PHP {site.PhpVersion}",
            site.NodeVersion is null ? projectNodeLabel : $"Node.js {site.NodeVersion}"
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
