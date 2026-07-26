using System.Text.Json.Serialization;
using HerdMe.Windows.Services;

namespace HerdMe.Windows.Models;

public sealed class CapturedMail
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Sender { get; init; } = "Unknown sender";

    public List<string> Recipients { get; init; } = [];

    public string Subject { get; init; } = "(No subject)";

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    public string Body { get; init; } = string.Empty;

    public string Raw { get; init; } = string.Empty;

    public string? HtmlBody { get; init; }

    [JsonIgnore]
    public string RecipientsText => string.Join(", ", Recipients);

    [JsonIgnore]
    public string ReceivedText => ReceivedAt.LocalDateTime.ToString("g");

    public bool MatchesSearch(string? query)
    {
        var normalized = query?.Trim();
        if (string.IsNullOrEmpty(normalized)) return true;
        return Sender.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
            || Subject.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
            || Recipients.Any(value => value.Contains(normalized, StringComparison.CurrentCultureIgnoreCase))
            || ReceivedText.Contains(normalized, StringComparison.CurrentCultureIgnoreCase);
    }

    public static CapturedMail Parse(string sender, IEnumerable<string> recipients, string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var headerText = separator >= 0 ? normalized[..separator] : normalized;
        var rawBody = separator >= 0 ? normalized[(separator + 2)..] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var line in headerText.Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && currentKey is not null)
            {
                headers[currentKey] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            currentKey = line[..colon].Trim();
            headers[currentKey] = line[(colon + 1)..].Trim();
        }
        var recipientList = recipients.ToList();
        if (recipientList.Count == 0)
        {
            recipientList.Add(headers.GetValueOrDefault("To", "Unknown recipient"));
        }
        var content = MailMimeParser.Parse(raw);
        var body = content.PlainText?.Trim()
            ?? (content.Html is null ? rawBody : MailMimeParser.PlainTextFromHtml(content.Html));
        return new CapturedMail
        {
            Sender = headers.GetValueOrDefault("From", sender),
            Recipients = recipientList,
            Subject = MailMimeParser.DecodeHeader(headers.GetValueOrDefault("Subject", "(No subject)")),
            Body = body,
            Raw = raw,
            HtmlBody = content.Html
        };
    }
}
