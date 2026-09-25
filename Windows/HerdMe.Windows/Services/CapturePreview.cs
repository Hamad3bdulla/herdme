using System.Net;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

internal sealed record CaptureTextPreview(string Text, bool IsTruncated);
internal sealed record MailCapturePreview(string Body, string Raw, string HtmlDocument, bool IsTruncated);

internal static class CapturePreview
{
    internal const int MaximumTextCharacters = 128 * 1_024;

    internal static CaptureTextPreview LimitText(string? text, int maximumCharacters = MaximumTextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        text ??= string.Empty;
        if (text.Length <= maximumCharacters) return new(text, false);
        var length = maximumCharacters;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return new(text[..length], true);
    }

    internal static MailCapturePreview ForMail(CapturedMail message)
    {
        var body = LimitText(message.Body);
        var raw = LimitText(message.Raw);
        var html = LimitText(
            message.HtmlBody ?? $"<pre>{WebUtility.HtmlEncode(body.Text)}</pre>",
            MailMimeParser.MaximumPreviewCharacters
        );
        return new(body.Text, raw.Text, MailMimeParser.SafeHtmlDocument(html.Text),
            body.IsTruncated || raw.IsTruncated || html.IsTruncated);
    }
}
