using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HerdMe.Windows.Services;

// Saves a crash report (and a small minidump on Windows) under Log\Crashes when HerdMe is
// about to close because of an error it cannot recover from. Reports stay on this PC: HerdMe
// never uploads them. The user can include them in Export diagnostics.
public sealed class CrashReporter
{
    public const int MaximumReports = 5;
    public const string ReportExtension = ".txt";
    public const string DumpExtension = ".dmp";
    public const string PendingMarkerName = "pending";

    private readonly string supportRoot;
    private readonly string applicationVersion;
    private int written;

    public CrashReporter(string supportRoot, string applicationVersion)
    {
        this.supportRoot = Path.GetFullPath(supportRoot);
        this.applicationVersion = applicationVersion;
    }

    public string CrashDirectory => Path.Combine(supportRoot, "Log", "Crashes");

    public string PendingMarkerPath => Path.Combine(CrashDirectory, PendingMarkerName);

    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) TryWrite(error, writeDump: true);
        };
    }

    // Writes at most one report per process; a crash can reach several handlers.
    public string? TryWrite(Exception error, bool writeDump)
    {
        if (Interlocked.Exchange(ref written, 1) != 0) return null;
        try
        {
            Directory.CreateDirectory(CrashDirectory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            var baseName = Path.Combine(CrashDirectory, "crash-" + stamp);
            var report = BuildReport(error, applicationVersion, DateTimeOffset.UtcNow);
            File.WriteAllText(baseName + ReportExtension, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (writeDump && OperatingSystem.IsWindows()) TryWriteMinidump(baseName + DumpExtension);
            File.WriteAllText(PendingMarkerPath, stamp);
            Prune();
            return baseName + ReportExtension;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // True once after a crash; the next start shows a notification and clears the marker.
    public bool ConsumePendingCrash()
    {
        try
        {
            if (!File.Exists(PendingMarkerPath)) return false;
            File.Delete(PendingMarkerPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> Reports()
    {
        if (!Directory.Exists(CrashDirectory)) return [];
        return Directory.EnumerateFiles(CrashDirectory, "crash-*" + ReportExtension)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToList();
    }

    public static string BuildReport(Exception error, string applicationVersion, DateTimeOffset time)
    {
        var builder = new StringBuilder();
        builder.Append("HerdMe crash report\n");
        builder.Append("Time (UTC): ").Append(time.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("Version: ").Append(applicationVersion).Append('\n');
        builder.Append("OS: ").Append(RuntimeInformation.OSDescription).Append('\n');
        builder.Append("Architecture: ").Append(RuntimeInformation.ProcessArchitecture).Append('\n');
        builder.Append("Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        builder.Append("Exception: ").Append(error.GetType().FullName).Append('\n');
        builder.Append('\n');
        builder.Append(DiagnosticsRedaction.Redact(error.ToString())).Append('\n');
        return builder.ToString();
    }

    private void Prune()
    {
        foreach (var extension in new[] { ReportExtension, DumpExtension })
        {
            var stale = Directory.EnumerateFiles(CrashDirectory, "crash-*" + extension)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(MaximumReports)
                .ToList();
            foreach (var path in stale)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // A small dump: thread stacks and module list only, not the process heap, so it does not
    // carry page contents, request bodies, or database rows.
    [SupportedOSPlatform("windows")]
    private static void TryWriteMinidump(string path)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!MiniDumpWriteDump(
                    process.Handle,
                    (uint)process.Id,
                    file.SafeFileHandle,
                    MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                file.Dispose();
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or DllNotFoundException
            or EntryPointNotFoundException
            or InvalidOperationException)
        {
        }
    }

    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr process,
        uint processId,
        SafeFileHandle file,
        uint dumpType,
        IntPtr exceptionParameter,
        IntPtr userStreamParameter,
        IntPtr callbackParameter
    );
}
