using System.Text;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    public static async Task VerifyLogContractsAsync(string supportRoot)
    {
        var root = Path.Combine(supportRoot, "log-reader");
        Check(LogFileReader.Discover(root).Count == 0 && !Directory.Exists(root),
            "browsing absent logs never creates project directories");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var path = Path.Combine(root, "nested", "laravel.log");
        const string text = "[INFO] بدأ التشغيل\r\n[ERROR] فشل الاتصال\n";
        foreach (var encoding in new Encoding[]
        {
            new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode,
            Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true)
        })
        {
            await File.WriteAllTextAsync(path, text, encoding);
            var content = await LogFileReader.ReadTailAsync(path);
            Check(content.Text == text && !content.Truncated,
                $"small {encoding.WebName} logs preserve Arabic, line endings and BOM handling");
        }

        // The tail starts inside the two-byte Arabic letter.
        var largeText = "ا" + new string('x', LogFileReader.MaximumBytes - 4) + "END";
        await File.WriteAllTextAsync(path, largeText, new UTF8Encoding(false));
        var tail = await LogFileReader.ReadTailAsync(path);
        Check(tail.Truncated && tail.Text.Length == LogFileReader.MaximumBytes - 1
            && !tail.Text.Contains('\uFFFD') && tail.Text.EndsWith("END", StringComparison.Ordinal),
            "large log tails stay bounded and never begin with a broken UTF-8 character");

        await File.WriteAllTextAsync(path, new string('ا', LogFileReader.MaximumBytes / 2) + "آخر سطر",
            Encoding.Unicode);
        tail = await LogFileReader.ReadTailAsync(path);
        Check(tail.Truncated && tail.Text.EndsWith("آخر سطر", StringComparison.Ordinal)
            && !tail.Text.Contains('\uFFFD'), "large Windows UTF-16 logs retain their encoding after seeking");

        foreach (var encoding in new[] { Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            await File.WriteAllTextAsync(path, "😀" + new string('x', LogFileReader.MaximumBytes / 2 - 1), encoding);
            tail = await LogFileReader.ReadTailAsync(path);
            Check(tail.Truncated && tail.Text.Length == LogFileReader.MaximumBytes / 2 - 1
                && !tail.Text.Contains('\uFFFD'), "UTF-16 tails skip split surrogate pairs");
        }

        // A logger is allowed to keep its file open while the viewer reads it.
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
            var live = await LogFileReader.ReadTailAsync(path);
            Check(live.Text == tail.Text, "live log reads coexist with the application's writer");
        }
        var records = LogFileReader.Discover(root);
        Check(records.Count == 1 && records[0].Path == path
            && records[0].Name == Path.Combine("nested", "laravel.log")
            && records[0].Size == new FileInfo(path).Length,
            "log discovery includes nested files and their current metadata");
        File.Move(path, path + ".1");
        await File.WriteAllTextAsync(path, "new log");
        Check((await LogFileReader.ReadTailAsync(path)).Text == "new log"
            && LogFileReader.Discover(root).Count == 2,
            "log rotation exposes the replacement file and its archive");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(
            () => LogFileReader.ReadTailAsync(path, cancellation.Token),
            "cancelled log reads stop before opening a file");
        Throws<OperationCanceledException>(() => LogFileReader.Discover(root, cancellation.Token),
            "cancelled discovery does not enumerate project trees");
        Throws<OperationCanceledException>(() => LogPresentation.FilterLines(text, "error", cancellation.Token),
            "superseded searches can be cancelled");
        Check(LogPresentation.FilterLines("match one\rskip\r\nMATCH اثنان\nend", "match")
            == "match one\nMATCH اثنان", "log search supports mixed newline styles and Arabic text");
    }
}
