using System.Diagnostics;
using System.Text;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyDiagnosticsContractsAsync(string supportRoot)
    {
        const int maximumJournalBytes = 4 * 1_024 * 1_024;
        var journalRoot = Path.Combine(supportRoot, "diagnostics-journal");
        var journal = new OperationJournal(journalRoot);
        var path = Path.Combine(journalRoot, "operations.jsonl");
        journal.Append("first", "running");
        File.AppendAllText(path, "null\n{}\nnot json\n{\"name\":");
        journal.Append("after-crash", "running", "valid after an incomplete final record");
        Check(journal.ReadRecent().Select(item => item.Name).SequenceEqual(["after-crash", "first"]),
            "journal reads skip malformed records and preserve the first append after a torn write");
        Check(journal.ReadRecent(1).Single().Name == "after-crash" && journal.ReadRecent(0).Count == 0,
            "journal limits count valid records only");

        var content = File.ReadAllText(path);
        File.WriteAllText(path,
            new string('x', maximumJournalBytes - Encoding.UTF8.GetByteCount(content) - 1)
                + "\n" + content,
            new UTF8Encoding(false));
        journal.Append("after-rotation", "running");
        Check(new FileInfo(path).Length <= maximumJournalBytes
            && File.Exists(path + ".1")
            && journal.ReadRecent(3).Select(item => item.Name)
                .SequenceEqual(["after-rotation", "after-crash", "first"]),
            "journal rotation bounds the active file and retains recent history across the archive");

        var sharedRoot = Path.Combine(supportRoot, "shared-journal");
        var journals = Enumerable.Range(0, 4).Select(_ => new OperationJournal(sharedRoot)).ToArray();
        Parallel.For(0, 64, index => journals[index % journals.Length].Append(index.ToString(), "running"));
        var concurrentEntries = journals[0].ReadRecent(100);
        Check(concurrentEntries.Count == 64 && concurrentEntries.Select(item => item.Name).Distinct().Count() == 64,
            "multiple journal instances serialize writes without dropping or interleaving records");

        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                journal.Append("locked", "running");
                Check(journal.ReadRecent(1).Single().Name == "after-crash",
                    "a temporarily locked active journal does not prevent reading archived diagnostics");
            }
            Check(journal.ReadRecent(1).Single().Name == "after-rotation",
                "diagnostic write failures do not damage the existing active journal");

            content = File.ReadAllText(path);
            File.WriteAllText(path,
                new string('x', maximumJournalBytes - Encoding.UTF8.GetByteCount(content) - 1)
                    + "\n" + content,
                new UTF8Encoding(false));
            using (var locked = new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                journal.Append("blocked-rotation", "running");
                Check(new FileInfo(path).Length == maximumJournalBytes,
                    "a blocked archive rotation does not grow the journal or fail backend work");
            }
        }

        var staged = Path.Combine(supportRoot, "hosts-staged-fixture");
        File.WriteAllText(staged, "127.0.0.1 test.local");
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
            WindowsHostsManager.TryDeleteStagedCandidate(staged);
            Check(File.Exists(staged), "a helper holding its candidate cannot mask cancellation during cleanup");
        }
        WindowsHostsManager.TryDeleteStagedCandidate(staged);
        Check(!File.Exists(staged), "staged hosts files are removed after their handles close");

        using var process = StartHostsUpdaterFixture(delay: true);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var wait = WindowsHostsManager.WaitForUpdaterExitAsync(
                process, TimeSpan.FromSeconds(5), cancellation.Token);
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => wait,
                "hosts updater cancellation remains a cancellation error");
            Check(!process.HasExited,
                "cancelling a hosts update wait leaves the helper alive to finish its file write");
            await ThrowsAsync<TimeoutException>(
                () => WindowsHostsManager.WaitForUpdaterExitAsync(
                    process, TimeSpan.FromMilliseconds(50), CancellationToken.None),
                "hosts updater timeout is distinct from user cancellation");
            Check(!process.HasExited,
                "hosts updater timeout does not terminate a helper during a file write");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var completed = StartHostsUpdaterFixture(delay: false);
        await WindowsHostsManager.WaitForUpdaterExitAsync(
            completed, TimeSpan.FromSeconds(10), CancellationToken.None);
        Check(completed.ExitCode == 0, "hosts updater wait recognizes normal process completion");
    }

    private static Process StartHostsUpdaterFixture(bool delay)
    {
        var start = new ProcessStartInfo(ContractExecutablePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["HERDME_NPM_RUNNER_FIXTURE"] = delay ? "delay" : "arguments";
        return Process.Start(start) ?? throw new InvalidOperationException("Updater fixture did not start.");
    }
}
