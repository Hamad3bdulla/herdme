using System.Net;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

public sealed record MailHeader(string Name, string Value);

public enum MailLinkIssue
{
    None,
    Empty,
    Insecure,
    Localhost,
    Relative
}

public sealed record MailLink(string Url, string Text, MailLinkIssue Issue);

// Headers and a static link check for captured mail. No link is ever fetched: the check
// only flags what would break for a real recipient (localhost, relative, http, empty).
public static partial class MailInspection
{
    public const int MaximumHeaders = 200;
    public const int MaximumLinks = 200;

    public static IReadOnlyList<MailHeader> Headers(string raw)
    {
        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headerText = separator >= 0 ? text[..separator] : text;
        var headers = new List<MailHeader>();
        foreach (var line in headerText.Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && headers.Count > 0)
            {
                var last = headers[^1];
                headers[^1] = last with { Value = last.Value + " " + line.Trim() };
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (headers.Count >= MaximumHeaders) break;
            headers.Add(new MailHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return headers
            .Select(header => header with { Value = MailMimeParser.DecodeHeader(header.Value) })
            .ToArray();
    }

    public static IReadOnlyList<MailLink> Links(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var links = new List<MailLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AnchorPattern().Matches(html))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (!seen.Add(url)) continue;
            var text = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups["text"].Value, " "));
            text = WhitespacePattern().Replace(text, " ").Trim();
            links.Add(new MailLink(url, text, Classify(url)));
            if (links.Count >= MaximumLinks) break;
        }
        return links;
    }

    public static MailLinkIssue Classify(string url)
    {
        if (url.Length == 0 || url == "#") return MailLinkIssue.Empty;
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return MailLinkIssue.None;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return MailLinkIssue.Relative;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("0.0.0.0", StringComparison.Ordinal)) return MailLinkIssue.Localhost;
        return uri.Scheme == "http" ? MailLinkIssue.Insecure : MailLinkIssue.None;
    }

    [GeneratedRegex("<a\\b[^>]*?\\bhref\\s*=\\s*(?:\"(?<url>[^\"]*)\"|'(?<url>[^']*)'|(?<url>[^\\s>]+))[^>]*>(?<text>.*?)</a\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline, 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\s+", RegexOptions.None, 1000)]
    private static partial Regex WhitespacePattern();
}
