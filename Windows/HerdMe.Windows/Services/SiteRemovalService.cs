using System.Runtime.Versioning;
using HerdMe.Windows.Models;
using Microsoft.VisualBasic.FileIO;

namespace HerdMe.Windows.Services;

public enum SiteRemovalFailure
{
    LinkedProject,
    OutsideParkedFolder,
    Unavailable
}

public sealed class SiteRemovalException(
    SiteRemovalFailure failure,
    string message
) : InvalidOperationException(message)
{
    public SiteRemovalFailure Failure { get; } = failure;
}

public static class SiteRemovalService
{
    public static string ResolveRemovableDirectory(
        SiteRecord site,
        IEnumerable<string> parkedRoots
    )
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(parkedRoots);
        if (site.Linked)
        {
            throw new SiteRemovalException(
                SiteRemovalFailure.LinkedProject,
                "Unlink this project instead. HerdMe will not delete the original linked folder."
            );
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(site.Path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new SiteRemovalException(
                SiteRemovalFailure.Unavailable,
                "The site folder is missing or is not a removable project folder."
            );
        }

        var parent = Path.GetDirectoryName(candidate);
        var isDirectChild = parent is not null && parkedRoots.Any(root =>
        {
            try
            {
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                return parent.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                return false;
            }
        });
        if (!isDirectChild)
        {
            throw new SiteRemovalException(
                SiteRemovalFailure.OutsideParkedFolder,
                "HerdMe can only move a direct child of a configured sites folder to the Recycle Bin."
            );
        }

        if (!Directory.Exists(candidate)
            || !IsRemovableDirectoryAttributes(File.GetAttributes(candidate)))
        {
            throw new SiteRemovalException(
                SiteRemovalFailure.Unavailable,
                "The site folder is missing or is not a removable project folder."
            );
        }
        return candidate;
    }

    internal static bool IsRemovableDirectoryAttributes(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Directory)
            && !attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    [SupportedOSPlatform("windows")]
    public static Task MoveToRecycleBinAsync(
        SiteRecord site,
        IEnumerable<string> parkedRoots
    )
    {
        var roots = parkedRoots.ToArray();
        return Task.Run(() => MoveToRecycleBin(site, roots));
    }

    [SupportedOSPlatform("windows")]
    private static void MoveToRecycleBin(SiteRecord site, IEnumerable<string> parkedRoots)
    {
        var candidate = ResolveRemovableDirectory(site, parkedRoots);
        FileSystem.DeleteDirectory(
            candidate,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException
        );
    }
}
