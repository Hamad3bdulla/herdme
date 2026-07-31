using System.IO.Compression;

namespace HerdMe.Windows.Services;

public static class SafeZipExtractor
{
    public const int DefaultMaximumEntries = 50_000;
    public const long DefaultMaximumExpandedBytes = 4L * 1_024 * 1_024 * 1_024;
    private const int MaximumEntryPathCharacters = 1_024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public static async Task ExtractAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default,
        int maximumEntries = DefaultMaximumEntries,
        long maximumExpandedBytes = DefaultMaximumExpandedBytes
    )
    {
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumExpandedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedBytes));
        }

        var destinationRoot = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(destinationRoot);
        var destinationPrefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        long extractedBytes = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
        {
            throw new InvalidDataException(
                $"The ZIP archive must contain between 1 and {maximumEntries:N0} entries."
            );
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeEntryPath(entry.FullName);
            if (!paths.Add(relativePath.TrimEnd('/')))
            {
                throw new InvalidDataException("The ZIP archive contains duplicate paths.");
            }
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException("The ZIP archive contains a symbolic link.");
            }

            declaredBytes = CheckedTotal(declaredBytes, entry.Length, maximumExpandedBytes);
            var destination = Path.GetFullPath(Path.Combine(
                destinationRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            ));
            if (!destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The ZIP archive contains an unsafe path.");
            }

            if (IsDirectory(entry))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1_024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            var buffer = new byte[128 * 1_024];
            long entryBytes = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                entryBytes = CheckedTotal(entryBytes, count, entry.Length);
                extractedBytes = CheckedTotal(extractedBytes, count, maximumExpandedBytes);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (entryBytes != entry.Length)
            {
                throw new InvalidDataException("A ZIP entry did not match its declared size.");
            }
        }
        if (extractedBytes != declaredBytes)
        {
            throw new InvalidDataException("The ZIP archive did not match its declared expanded size.");
        }
    }

    internal static string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEntryPathCharacters)
        {
            throw new InvalidDataException("The ZIP archive contains an invalid path.");
        }
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains('\0'))
        {
            throw new InvalidDataException("The ZIP archive contains an unsafe path.");
        }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
            segment is "." or ".."
            || segment.Contains(':')
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || IsReservedWindowsDeviceName(segment)))
        {
            throw new InvalidDataException("The ZIP archive contains an unsafe path.");
        }
        return string.Join('/', segments) + (IsDirectoryPath(normalized) ? "/" : string.Empty);
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return ReservedWindowsDeviceNames.Contains(stem);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => IsDirectoryPath(entry.FullName);

    private static bool IsDirectoryPath(string value) => value.EndsWith('/') || value.EndsWith('\\');

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    private static long CheckedTotal(long current, long addition, long maximum)
    {
        if (addition < 0 || current > maximum - addition)
        {
            throw new InvalidDataException(
                $"The ZIP archive expands beyond the supported {maximum:N0}-byte limit."
            );
        }
        return current + addition;
    }
}
