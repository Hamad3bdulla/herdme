using System.Collections.Concurrent;

namespace HerdMe.Windows.ViewModels;

/// <summary>Project files that decide which Sites actions are available.</summary>
public sealed record SitesDetection(bool HasArtisan, bool HasPackageJson, bool HasComposerJson);

/// <summary>Cheap file-system stamps that invalidate a cached Git summary.</summary>
public readonly record struct SitesGitStamp(DateTime Folder, DateTime Head, DateTime Index);

/// <summary>
/// Per-site detection cache keyed by project path. Entries are validated with cheap
/// LastWriteTimeUtc stamps (site folder, .env, .git/HEAD, .git/index) so repeated
/// selection, search, and process events never re-probe or re-run Git for unchanged
/// projects. Probing methods touch the file system and must run off the UI thread;
/// <see cref="Peek"/> is memory-only and safe on the UI thread.
/// </summary>
public sealed class SitesDetectionCache
{
    private readonly record struct DetectionStamp(DateTime Folder, DateTime Environment);

    private sealed record DetectionEntry(DetectionStamp Stamp, SitesDetection Detection);

    private sealed record GitEntry(SitesGitStamp Stamp, string? Summary);

    private readonly ConcurrentDictionary<string, DetectionEntry> detections =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, GitEntry> gitSummaries =
        new(StringComparer.OrdinalIgnoreCase);

    public SitesDetection? Peek(string sitePath)
    {
        return detections.TryGetValue(sitePath, out var entry) ? entry.Detection : null;
    }

    public SitesDetection Detect(string sitePath)
    {
        var stamp = new DetectionStamp(
            Stamp(sitePath, isDirectory: true),
            Stamp(Path.Combine(sitePath, ".env"), isDirectory: false)
        );
        if (detections.TryGetValue(sitePath, out var cached) && cached.Stamp == stamp)
        {
            return cached.Detection;
        }
        var detection = new SitesDetection(
            File.Exists(Path.Combine(sitePath, "artisan")),
            File.Exists(Path.Combine(sitePath, "package.json")),
            File.Exists(Path.Combine(sitePath, "composer.json"))
        );
        detections[sitePath] = new DetectionEntry(stamp, detection);
        return detection;
    }

    public static SitesGitStamp CaptureGitStamp(string sitePath)
    {
        var git = Path.Combine(sitePath, ".git");
        return new SitesGitStamp(
            Stamp(sitePath, isDirectory: true),
            Stamp(Path.Combine(git, "HEAD"), isDirectory: false),
            Stamp(Path.Combine(git, "index"), isDirectory: false)
        );
    }

    public bool TryGetGitSummary(string sitePath, SitesGitStamp stamp, out string? summary)
    {
        if (gitSummaries.TryGetValue(sitePath, out var cached) && cached.Stamp == stamp)
        {
            summary = cached.Summary;
            return true;
        }
        summary = null;
        return false;
    }

    public void StoreGitSummary(string sitePath, SitesGitStamp stamp, string? summary)
    {
        gitSummaries[sitePath] = new GitEntry(stamp, summary);
    }

    public void Invalidate(string sitePath)
    {
        detections.TryRemove(sitePath, out _);
        gitSummaries.TryRemove(sitePath, out _);
    }

    public void Clear()
    {
        detections.Clear();
        gitSummaries.Clear();
    }

    private static DateTime Stamp(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
