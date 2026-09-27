using System.Text;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

/// <summary>
/// The newest line of a service log, for the service card. Reads only the end of the file,
/// shares it with the writer, drops terminal colour codes, and caps the length so one huge
/// line cannot stretch the card.
/// </summary>
public static partial class ServiceLogTail
{
    public const int DefaultTailBytes = 4096;
    public const int MaximumLineLength = 200;

    public static string LastLine(string path, int tailBytes = DefaultTailBytes)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            var length = stream.Length;
            if (length == 0) return string.Empty;
            var count = (int)Math.Min(length, Math.Max(256, tailBytes));
            stream.Seek(-count, SeekOrigin.End);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0) break;
                read += chunk;
            }
            return FromText(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static string FromText(string text)
    {
        var lines = text.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = Clean(lines[index]);
            if (line.Length == 0) continue;
            return line.Length <= MaximumLineLength
                ? line
                : line[..(MaximumLineLength - 1)] + "\u2026";
        }
        return string.Empty;
    }

    private static string Clean(string line)
    {
        var withoutColour = AnsiEscape().Replace(line, string.Empty);
        var builder = new StringBuilder(withoutColour.Length);
        foreach (var character in withoutColour)
        {
            if (!char.IsControl(character) || character == '\t') builder.Append(character == '\t' ? ' ' : character);
        }
        return builder.ToString().Trim();
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();
}
