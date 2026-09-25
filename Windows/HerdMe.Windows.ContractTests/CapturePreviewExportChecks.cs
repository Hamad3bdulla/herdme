using System.Text;
using System.Text.Json;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyCapturePreviewExportsAsync(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "capture-exports");
        Directory.CreateDirectory(root);
        var boundary = new string('a', CapturePreview.MaximumTextCharacters - 1) + "🌍";
        var textPreview = CapturePreview.LimitText(boundary);
        Check(textPreview.IsTruncated && textPreview.Text.Length == CapturePreview.MaximumTextCharacters - 1
            && !char.IsSurrogate(textPreview.Text[^1]),
            "capture text previews do not split emoji at the display boundary");

        var message = new CapturedMail
        {
            Subject = new string('s', 1_000),
            Sender = new string('f', 1_000),
            Body = "مرحبا 🌍\r\n" + new string('م', CapturePreview.MaximumTextCharacters + 100),
            Raw = "Subject: captured Unicode\r\n\r\n" + boundary + "\r\nEnd of full message.",
            HtmlBody = "<p>" + new string('\u0800', MailMimeParser.MaximumPreviewCharacters * 4) + "</p>"
        };
        var preview = CapturePreview.ForMail(message);
        Check(preview.IsTruncated && preview.Body.Length <= CapturePreview.MaximumTextCharacters
            && preview.Raw.Length <= CapturePreview.MaximumTextCharacters,
            "large mail text and raw views have bounded display sizes");
        Check(Encoding.UTF8.GetByteCount(preview.HtmlDocument) < 2 * 1_024 * 1_024
            && preview.HtmlDocument.Contains("default-src 'none'", StringComparison.Ordinal)
            && preview.HtmlDocument.Contains("form-action 'none'", StringComparison.Ordinal),
            "mail previews fit the WebView2 byte limit with multibyte text and retain their content policy");
        Check(message.SenderPreview.Length <= 512 && message.SubjectPreview.Length <= 512,
            "large mail headers do not fill inbox rows with unbounded text");
        var plainPreview = CapturePreview.ForMail(new CapturedMail { Body = "<script>مرحبا</script>" });
        Check(!plainPreview.IsTruncated && plainPreview.HtmlDocument.Contains("&lt;script&gt;", StringComparison.Ordinal),
            "plain text email remains encoded when preparing its HTML preview");
        var missingText = CapturePreview.ForMail(new CapturedMail { Body = null!, Raw = null! });
        Check(missingText.Body.Length == 0 && missingText.Raw.Length == 0 && !missingText.IsTruncated,
            "legacy captures with missing text fields still produce an empty preview");

        var mailPath = Path.Combine(root, "message.eml");
        await CaptureExport.SaveMailAsync(message, mailPath);
        Check(await File.ReadAllTextAsync(mailPath) == message.Raw
            && new FileInfo(mailPath).Length == Encoding.UTF8.GetByteCount(message.Raw),
            "email export preserves full captured Unicode and line endings without preview truncation or a BOM");

        var dump = new CapturedDump
        {
            Source = new string('p', 1_000),
            Summary = boundary + "\nFull dump summary.",
            Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("Original payload مرحبا 🌍"))
        };
        var dumpPath = Path.Combine(root, "dump.json");
        await CaptureExport.SaveDumpAsync(dump, dumpPath);
        var dumpJson = await File.ReadAllTextAsync(dumpPath);
        var restored = JsonSerializer.Deserialize<CapturedDump>(dumpJson)!;
        Check(restored.Id == dump.Id && restored.ReceivedAt == dump.ReceivedAt
            && restored.Source == dump.Source && restored.Summary == dump.Summary && restored.Payload == dump.Payload
            && !dumpJson.Contains("SummaryPreview", StringComparison.Ordinal),
            "dump export retains complete payload and metadata without persisting display-only fields");
        Check(dump.SourcePreview.Length <= 512 && dump.SummaryPreview.Length <= 1_024,
            "dump list rows render bounded source and summary text");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => CaptureExport.SaveMailAsync(
            new CapturedMail { Raw = "replacement" }, mailPath, cancellation.Token),
            "cancelled capture exports preserve the existing destination");
        Check(await File.ReadAllTextAsync(mailPath) == message.Raw,
            "a cancelled export does not replace an existing mail file");
        if (OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(mailPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejected = false;
                try { await CaptureExport.SaveMailAsync(new CapturedMail { Raw = "replacement" }, mailPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "capture export reports a locked Windows destination without truncating it");
            }
            Check(await File.ReadAllTextAsync(mailPath) == message.Raw,
                "failed export promotion preserves the previously saved capture");
        }
        Check(!Directory.EnumerateFiles(root, ".herdme-capture-*.tmp").Any(),
            "capture exports remove staging files after success, cancellation, and failed promotion");
    }
}
