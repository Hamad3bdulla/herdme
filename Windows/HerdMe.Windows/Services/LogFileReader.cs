using System.Text;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record LogFileContent(string Text, bool Truncated);

public static class LogFileReader
{
    public const int MaximumBytes = 4 * 1_024 * 1_024;

    public static IReadOnlyList<LogFileRecord> Discover(
        string root,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return [];
        var records = new List<LogFileRecord>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                records.Add(new LogFileRecord
                {
                    Name = Path.GetRelativePath(root, path),
                    Path = path,
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return records.OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<LogFileContent> ReadTailAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1_024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var header = new byte[4];
        var headerLength = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var (encoding, unitSize) = EncodingFor(header, headerLength);
        var count = (int)Math.Min(length, MaximumBytes);
        var bytes = new byte[count];
        stream.Seek(length - count, SeekOrigin.Begin);
        var read = 0;
        // Read only the captured length, even if the writer keeps appending.
        while (read < count)
        {
            var received = await stream.ReadAsync(bytes.AsMemory(read, count - read), cancellationToken)
                .ConfigureAwait(false);
            if (received == 0) break; // The log may have been truncated during rotation.
            read += received;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var offset = 0;
        if (length > count && unitSize == 1)
        {
            // A byte limit can land inside an Arabic or other UTF-8 character.
            while (offset < read && (bytes[offset] & 0xC0) == 0x80) offset++;
        }
        else if (length > count)
        {
            offset = (int)((unitSize - (length - count) % unitSize) % unitSize);
            offset = Math.Min(offset, read);
            if (unitSize == 2 && read - offset >= 2)
            {
                var first = encoding.CodePage == Encoding.Unicode.CodePage
                    ? bytes[offset] | bytes[offset + 1] << 8
                    : bytes[offset] << 8 | bytes[offset + 1];
                if (first is >= 0xDC00 and <= 0xDFFF) offset += 2;
            }
        }
        using var buffer = new MemoryStream(bytes, offset, read - offset, writable: false);
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: length <= count);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return new LogFileContent(text, length > count);
    }

    private static (Encoding Encoding, int UnitSize) EncodingFor(byte[] header, int count)
    {
        if (count >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0)
            return (Encoding.UTF32, 4);
        if (count >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (count >= 2 && header[0] == 0xFF && header[1] == 0xFE) return (Encoding.Unicode, 2);
        if (count >= 2 && header[0] == 0xFE && header[1] == 0xFF) return (Encoding.BigEndianUnicode, 2);
        return (Encoding.UTF8, 1);
    }
}
