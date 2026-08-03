using System.IO.Compression;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public static class SiteWorkflowArchive
{
    private const int MaximumEntries = 200_000;
    private const long MaximumSourceBytes = 20L * 1_024 * 1_024 * 1_024;
    private static readonly IReadOnlySet<string> ExcludedDirectoryNames = new HashSet<string>(
        [".git", "vendor", "node_modules"],
        StringComparer.OrdinalIgnoreCase
    );

    public static bool ShouldInclude(string relativePath, bool directory)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return true;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => ExcludedDirectoryNames.Contains(segment)
            || segment.StartsWith(".herdme-", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (segments.Length >= 2
            && segments[0].Equals("storage", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("logs", StringComparison.OrdinalIgnoreCase)) return false;
        if (segments.Length >= 3
            && segments[0].Equals("storage", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("framework", StringComparison.OrdinalIgnoreCase)
            && (segments[2].Equals("cache", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("sessions", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("views", StringComparison.OrdinalIgnoreCase))) return false;
        return directory || !segments[^1].Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task CreateAsync(
        string projectPath,
        string destination,
        string siteName,
        string? databaseDump = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var output = Path.GetFullPath(destination);
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The project export must use a .zip file.", nameof(destination));
        }
        if (IsWithin(root, output))
        {
            throw new InvalidOperationException("Save the export outside the project directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + $".partial-{Guid.NewGuid():N}";
        try
        {
            var entryCount = 0;
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                128 * 1_024,
                FileOptions.Asynchronous
            ))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                {
                    var files = EnumerateFiles(root, cancellationToken);
                    long totalBytes = 0;
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entryCount++;
                        if (entryCount > MaximumEntries)
                        {
                            throw new InvalidDataException("The project export exceeds 200,000 files.");
                        }
                        totalBytes = checked(totalBytes + file.Length);
                        if (totalBytes > MaximumSourceBytes)
                        {
                            throw new InvalidDataException("The project export exceeds 20 GB.");
                        }
                        var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                        await using var source = new FileStream(
                            file.FullName,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            128 * 1_024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan
                        );
                        await using var target = entry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                        if (entryCount % 100 == 0)
                        {
                            progress?.Report($"{entryCount} files · {totalBytes / 1_048_576d:F1} MB\n");
                        }
                    }

                    if (databaseDump is not null && File.Exists(databaseDump))
                    {
                        var databaseEntry = archive.CreateEntry("database/database.sql", CompressionLevel.Optimal);
                        await using var source = new FileStream(
                            databaseDump,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            128 * 1_024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan
                        );
                        await using var target = databaseEntry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                    }

                    var manifest = archive.CreateEntry("herdme-export.json", CompressionLevel.Optimal);
                    await using (var target = manifest.Open())
                    {
                        await JsonSerializer.SerializeAsync(target, new
                        {
                            format = 1,
                            site = siteName,
                            exportedAt = DateTimeOffset.UtcNow,
                            databaseIncluded = databaseDump is not null && File.Exists(databaseDump),
                            excluded = new[] { ".git", "vendor", "node_modules", "storage/logs", "storage/framework caches" }
                        }, cancellationToken: cancellationToken);
                    }
                }
            }
            progress?.Report($"{entryCount} files exported.\n");
            File.Move(temporary, output, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateFiles(
        string root,
        CancellationToken cancellationToken
    )
    {
        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, item.FullName);
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (item is DirectoryInfo child)
                {
                    if (ShouldInclude(relative, directory: true)) pending.Push(child);
                }
                else if (item is FileInfo file && ShouldInclude(relative, directory: false))
                {
                    files.Add(file);
                }
            }
        }
        return files.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
