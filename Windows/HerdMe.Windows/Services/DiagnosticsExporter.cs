using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HerdMe.Windows.Services;

// Masks values that look like secrets and the Windows user name before text leaves HerdMe's
// folder in an export or a crash report.
public static partial class DiagnosticsRedaction
{
    public const string Mask = "<redacted>";

    public static string Redact(string text, string? userProfile = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = SecretAssignment().Replace(text, match => match.Groups["name"].Value + match.Groups["separator"].Value + Mask);
        result = BearerToken().Replace(result, "Bearer " + Mask);
        result = UrlCredentials().Replace(result, match => match.Groups["scheme"].Value + Mask + "@");
        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && profile.Length > 3)
        {
            result = result.Replace(Path.TrimEndingDirectorySeparator(profile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    [GeneratedRegex(
        @"(?<name>\b[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|app[_-]?key)[A-Za-z0-9_.-]*""?)(?<separator>\s*[:=]\s*""?)(?<value>[^\s"",;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();
}

public sealed record DiagnosticsExportResult(string Path, int Files, bool IncludedCrashDumps);

// Builds a zip the user can attach to a bug report. Nothing is sent: the zip is written where
// the user chose, and only HerdMe logs, HerdMe settings, and crash reports are included.
// Text is redacted; minidumps are only added when the user ticks the box.
public sealed class DiagnosticsExporter
{
    public const long MaximumTextBytesPerFile = 1024 * 1024;
    public const int MaximumFiles = 400;

    private static readonly string[] TextExtensions = [".log", ".jsonl", ".json", ".txt"];
    private static readonly string[] BlockedNameParts = ["credential", "secret", "token", "password", ".key", ".pem", ".pfx", ".p12"];

    private readonly string supportRoot;
    private readonly string applicationVersion;
    private readonly string? userProfile;

    public DiagnosticsExporter(string supportRoot, string applicationVersion, string? userProfile = null)
    {
        this.supportRoot = Path.GetFullPath(supportRoot);
        this.applicationVersion = applicationVersion;
        this.userProfile = userProfile;
    }

    public static string DefaultFileName(DateTimeOffset now) =>
        "herdme-diagnostics-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip";

    public async Task<DiagnosticsExportResult> ExportAsync(
        string destination,
        bool includeCrashDumps,
        CancellationToken cancellationToken = default
    )
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var files = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await WriteTextEntryAsync(archive, "about.txt", About(), cancellationToken);
                files++;
                foreach (var (source, entryName) in Candidates(includeCrashDumps))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (files >= MaximumFiles) break;
                    if (await AddFileAsync(archive, source, entryName, cancellationToken)) files++;
                }
            }
            File.Move(temporary, destination, overwrite: true);
            return new DiagnosticsExportResult(destination, files, includeCrashDumps);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Relative entry names for everything that would be exported, for tests and the UI.
    public IReadOnlyList<string> PlannedEntries(bool includeCrashDumps) =>
        Candidates(includeCrashDumps).Select(item => item.EntryName).Prepend("about.txt").Take(MaximumFiles).ToList();

    private IEnumerable<(string Source, string EntryName)> Candidates(bool includeCrashDumps)
    {
        var config = Path.Combine(supportRoot, "Config");
        if (Directory.Exists(config))
        {
            foreach (var path in Directory.EnumerateFiles(config, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Allowed(path, includeCrashDumps)) yield return (path, "Config/" + Path.GetFileName(path));
            }
        }
        var log = Path.Combine(supportRoot, "Log");
        if (Directory.Exists(log))
        {
            var entries = new List<(string, string)>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(log, "*", SearchOption.AllDirectories))
                {
                    if (!Allowed(path, includeCrashDumps)) continue;
                    var relative = Path.GetRelativePath(log, path).Replace(Path.DirectorySeparatorChar, '/');
                    entries.Add((path, "Log/" + relative));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            foreach (var entry in entries.OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase)) yield return entry;
        }
    }

    private static bool Allowed(string path, bool includeCrashDumps)
    {
        var name = Path.GetFileName(path);
        if (BlockedNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return false;
        var extension = Path.GetExtension(path);
        if (extension.Equals(CrashReporter.DumpExtension, StringComparison.OrdinalIgnoreCase)) return includeCrashDumps;
        return TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> AddFileAsync(ZipArchive archive, string source, string entryName, CancellationToken cancellationToken)
    {
        try
        {
            if (Path.GetExtension(source).Equals(CrashReporter.DumpExtension, StringComparison.OrdinalIgnoreCase))
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = entry.Open();
                await input.CopyToAsync(output, cancellationToken);
                return true;
            }
            var text = await ReadTailAsync(source, cancellationToken);
            await WriteTextEntryAsync(archive, entryName, DiagnosticsRedaction.Redact(text, userProfile), cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Long logs keep their newest part, which is where the failure is.
    private static async Task<string> ReadTailAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var truncated = stream.Length > MaximumTextBytesPerFile;
        if (truncated) stream.Seek(-MaximumTextBytesPerFile, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: !truncated);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (!truncated) return text;
        var firstLine = text.IndexOf('\n');
        return "[HerdMe: earlier lines were left out]\n" + (firstLine >= 0 ? text[(firstLine + 1)..] : text);
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        await output.WriteAsync(bytes, cancellationToken);
    }

    private string About()
    {
        var builder = new StringBuilder();
        builder.Append("HerdMe diagnostics\n");
        builder.Append("Created (UTC): ").Append(DateTimeOffset.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("Version: ").Append(applicationVersion).Append('\n');
        builder.Append("OS: ").Append(RuntimeInformation.OSDescription).Append('\n');
        builder.Append("Architecture: ").Append(RuntimeInformation.OSArchitecture).Append(" / process ").Append(RuntimeInformation.ProcessArchitecture).Append('\n');
        builder.Append("Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        builder.Append("Culture: ").Append(System.Globalization.CultureInfo.CurrentUICulture.Name).Append('\n');
        builder.Append('\n');
        builder.Append("This file was created on request. HerdMe does not send diagnostics anywhere.\n");
        builder.Append("Secrets that look like password=, token=, API keys, and URL credentials are masked.\n");
        return builder.ToString();
    }
}
