using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record MailMimeContent(string? PlainText = null, string? Html = null);

public static partial class MailMimeParser
{
    public static MailMimeContent Parse(string raw) => ParsePart(raw.Replace("\r\n", "\n"));

    public static string DecodeHeader(string value)
    {
        return EncodedWord().Replace(value, match =>
        {
            var data = match.Groups[2].Value.Equals("b", StringComparison.OrdinalIgnoreCase)
                ? TryBase64(match.Groups[3].Value)
                : DecodeQuotedPrintable(match.Groups[3].Value.Replace('_', ' '));
            return data is null ? match.Value : Decode(data, match.Groups[1].Value);
        });
    }

    public static string PlainTextFromHtml(string html)
    {
        var separated = Regex.Replace(html, "<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        var stripped = Regex.Replace(separated, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(stripped), "[ \\t]+", " ")
            .Replace("\r", string.Empty)
            .Trim();
    }

    public static string SafeHtmlDocument(string html)
    {
        const string policy = "default-src 'none'; img-src data: cid:; style-src 'unsafe-inline'; font-src data:";
        return "<!doctype html><html><head><meta charset=\"utf-8\">"
            + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">"
            + "<meta name=\"color-scheme\" content=\"light dark\">"
            + "<style>body{font:14px Segoe UI;margin:18px;line-height:1.45;overflow-wrap:anywhere}img{max-width:100%;height:auto}</style>"
            + "</head><body>" + html + "</body></html>";
    }

    private static MailMimeContent ParsePart(string raw)
    {
        var (headers, body) = Split(raw);
        var contentType = headers.GetValueOrDefault("content-type", "text/plain; charset=utf-8");
        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (mediaType.StartsWith("multipart/") && Parameter(contentType, "boundary") is { } boundary)
        {
            string? plain = null;
            string? html = null;
            foreach (var part in MultipartParts(body, boundary))
            {
                var parsed = ParsePart(part);
                plain ??= parsed.PlainText;
                html ??= parsed.Html;
            }
            return new MailMimeContent(plain, html);
        }
        if (headers.GetValueOrDefault("content-disposition", string.Empty)
            .StartsWith("attachment", StringComparison.OrdinalIgnoreCase)) return new();
        if (mediaType is not ("text/plain" or "text/html")) return new();

        var transferEncoding = headers.GetValueOrDefault("content-transfer-encoding", string.Empty);
        var data = transferEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
            ? TryBase64(body) ?? []
            : transferEncoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase)
                ? DecodeQuotedPrintable(body)
                : Encoding.UTF8.GetBytes(body);
        var decoded = Decode(data, Parameter(contentType, "charset") ?? "utf-8");
        return mediaType == "text/html" ? new(Html: decoded) : new(PlainText: decoded);
    }

    private static (Dictionary<string, string> Headers, string Body) Split(string raw)
    {
        var separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var headerText = separator >= 0 ? raw[..separator] : raw;
        var body = separator >= 0 ? raw[(separator + 2)..] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in headerText.Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && current is not null)
            {
                headers[current] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            current = line[..colon].Trim();
            headers[current] = line[(colon + 1)..].Trim();
        }
        return (headers, body);
    }

    private static string? Parameter(string header, string name)
    {
        var match = Regex.Match(
            header,
            $"(?:^|;)\\s*{Regex.Escape(name)}\\s*=\\s*(?:\"([^\"]*)\"|([^;\\s]*))",
            RegexOptions.IgnoreCase
        );
        return !match.Success ? null : match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static IEnumerable<string> MultipartParts(string body, string boundary)
    {
        return body.Split("--" + boundary, StringSplitOptions.None)
            .Skip(1)
            .TakeWhile(section => !section.StartsWith("--", StringComparison.Ordinal))
            .Select(section => section.Trim('\r', '\n'))
            .Where(section => section.Length > 0);
    }

    private static byte[] DecodeQuotedPrintable(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value.Replace("=\r\n", string.Empty).Replace("=\n", string.Empty));
        using var output = new MemoryStream();
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '=' && index + 2 < bytes.Length
                && TryHex(bytes[index + 1], out var high) && TryHex(bytes[index + 2], out var low))
            {
                output.WriteByte((byte)(high << 4 | low));
                index += 2;
            }
            else output.WriteByte(bytes[index]);
        }
        return output.ToArray();
    }

    private static byte[]? TryBase64(string value)
    {
        try { return Convert.FromBase64String(Regex.Replace(value, "\\s", string.Empty)); }
        catch (FormatException) { return null; }
    }

    private static string Decode(byte[] data, string charset)
    {
        var normalized = charset.Trim().Trim('"').ToLowerInvariant();
        return normalized switch
        {
            "iso-8859-1" or "latin1" or "latin-1" => Encoding.Latin1.GetString(data),
            _ => Encoding.UTF8.GetString(data)
        };
    }

    private static bool TryHex(byte value, out int result)
    {
        result = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            _ => -1
        };
        return result >= 0;
    }

    [GeneratedRegex(@"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWord();
}
