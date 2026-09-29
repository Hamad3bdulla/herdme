using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HerdMe.Windows.Services;

public enum DefenderExclusionOutcome
{
    Applied,
    Declined,
    Failed
}

public sealed record DefenderExclusionResult(DefenderExclusionOutcome Outcome, string Message);

/// <summary>
/// Opt-in "Speed up PHP": asks Microsoft Defender not to scan the HerdMe folder
/// (%LOCALAPPDATA%\HerdMe - PHP, Composer, Node and service binaries HerdMe downloaded and
/// SHA-256 verified), which makes every php.exe start and Composer install noticeably faster.
///
/// Only that one folder can be excluded: the elevated helper refuses any other path. Changing a
/// Defender exclusion needs administrator approval, so HerdMe relaunches itself with "runas"
/// (the same documented elevation exception as the hosts helper) and the helper runs
/// Add-MpPreference / Remove-MpPreference through the in-box Windows PowerShell with no window.
/// Standard users cannot read Defender exclusions, so the setting records what HerdMe added.
/// </summary>
public static class DefenderExclusion
{
    public const string HelperArgument = "--defender-exclusion";
    public const string UninstallArgument = "--remove-defender-exclusion";
    internal const int ExitOk = 0;
    internal const int ExitBadRequest = 2;
    internal const int ExitDefenderFailed = 3;
    internal const int ExitPowerShellMissing = 4;
    internal static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(60);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdMe"
    );

    // ...\AppData\Local\HerdMe and nothing else. The folder is matched by shape rather than
    // by the helper's own profile so an over-the-shoulder administrator can still approve it.
    public static bool IsAllowedExclusionPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\'', '"', '`', '\r', '\n', '\0']) >= 0) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!string.Equals(full.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
        var folder = new DirectoryInfo(full.TrimEnd('\\', '/'));
        var local = folder.Parent;
        var appData = local?.Parent;
        return string.Equals(folder.Name, "HerdMe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(local?.Name, "Local", StringComparison.OrdinalIgnoreCase)
            && string.Equals(appData?.Name, "AppData", StringComparison.OrdinalIgnoreCase)
            && appData?.Parent?.Parent is not null;
    }

    // A PowerShell single-quoted literal: nothing inside is expanded; ' is doubled.
    public static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string BuildScript(string path, bool add)
    {
        if (!IsAllowedExclusionPath(path)) throw new ArgumentException("Only the HerdMe folder can be excluded.", nameof(path));
        var command = add ? "Add-MpPreference" : "Remove-MpPreference";
        return "$ErrorActionPreference='Stop'; " + command + " -ExclusionPath " + QuotePowerShell(path.TrimEnd('\\', '/'));
    }

    // -EncodedCommand takes base64 of UTF-16LE, so no argument quoting can change the script.
    public static string EncodeCommand(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public static string PowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe"
    );

    // Runs inside the elevated copy of HerdMe: HerdMe.Windows.exe --defender-exclusion add|remove <path>
    public static bool TryRunElevatedHelper(IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = ExitOk;
        if (arguments.Count < 2 || !arguments[1].Equals(HelperArgument, StringComparison.Ordinal)) return false;
        if (!OperatingSystem.IsWindows()
            || arguments.Count != 4
            || arguments[2] is not ("add" or "remove")
            || !IsAllowedExclusionPath(arguments[3]))
        {
            exitCode = ExitBadRequest;
            return true;
        }
        var add = arguments[2] == "add";
        var path = arguments[3];
        if (add)
        {
            // Never exclude a link that could point somewhere else.
            try
            {
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    exitCode = ExitBadRequest;
                    return true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                exitCode = ExitBadRequest;
                return true;
            }
        }

        var powerShell = PowerShellPath();
        if (!File.Exists(powerShell))
        {
            exitCode = ExitPowerShellMissing;
            return true;
        }
        try
        {
            var startInfo = new ProcessStartInfo(powerShell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(EncodeCommand(BuildScript(path, add)));
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                exitCode = ExitPowerShellMissing;
                return true;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)(HelperTimeout - TimeSpan.FromSeconds(10)).TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                exitCode = ExitDefenderFailed;
                return true;
            }
            _ = output.Result;
            _ = error.Result;
            exitCode = process.ExitCode == 0 ? ExitOk : ExitDefenderFailed;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            exitCode = ExitPowerShellMissing;
        }
        return true;
    }

    // Asks for administrator approval and adds (or removes) the exclusion for the HerdMe folder.
    public static async Task<DefenderExclusionResult> ApplyAsync(
        bool add,
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(DefenderExclusionOutcome.Failed, ServiceText.Get("DefenderExclusionUnsupported", "Windows Defender exclusions are only available on Windows."));
        }
        if (!IsAllowedExclusionPath(path))
        {
            return new(DefenderExclusionOutcome.Failed, ServiceText.Get("DefenderExclusionBadPath", "Only the HerdMe folder in your local app data can be excluded."));
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return new(DefenderExclusionOutcome.Failed, ServiceText.Get("DefenderExclusionNoExecutable", "The HerdMe executable path is unavailable."));
        }
        try
        {
            // Elevation needs the shell ("runas"); the helper itself starts PowerShell with no window.
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(HelperArgument);
            startInfo.ArgumentList.Add(add ? "add" : "remove");
            startInfo.ArgumentList.Add(path.TrimEnd('\\', '/'));
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(DefenderExclusionOutcome.Failed, ServiceText.Get("DefenderExclusionNoExecutable", "The HerdMe executable path is unavailable."));
            }
            await WindowsHostsManager.WaitForUpdaterExitAsync(process, HelperTimeout, cancellationToken);
            return process.ExitCode switch
            {
                ExitOk => new(DefenderExclusionOutcome.Applied, add
                    ? ServiceText.Get("DefenderExclusionAdded", "Windows Defender no longer scans the HerdMe folder.")
                    : ServiceText.Get("DefenderExclusionRemoved", "Windows Defender scans the HerdMe folder again.")),
                ExitDefenderFailed => new(DefenderExclusionOutcome.Failed, ServiceText.Get(
                    "DefenderExclusionDefenderFailed",
                    "Windows Defender did not accept the change. Another antivirus may be in charge, or Tamper Protection or an organisation policy may block exclusions."
                )),
                ExitPowerShellMissing => new(DefenderExclusionOutcome.Failed, ServiceText.Get(
                    "DefenderExclusionPowerShellMissing",
                    "Windows PowerShell is not available, so the Defender setting could not be changed."
                )),
                _ => new(DefenderExclusionOutcome.Failed, ServiceText.Format(
                    "DefenderExclusionExitCode",
                    "The Defender helper stopped with code {0}.",
                    process.ExitCode
                ))
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1_223)
        {
            return new(DefenderExclusionOutcome.Declined, ServiceText.Get(
                "DefenderExclusionDeclined",
                "Administrator approval was not given, so nothing changed."
            ));
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new(DefenderExclusionOutcome.Failed, error.Message);
        }
    }

    // Uninstaller: HerdMe.Windows.exe --remove-defender-exclusion. Does nothing (and asks
    // nothing) unless "Speed up PHP" was turned on; otherwise asks once to remove it.
    public static bool TryRunUninstallCleanup(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || !arguments[1].Equals(UninstallArgument, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var store = new SiteConfigurationStore();
            var path = store.Load().DefenderExclusionPath;
            if (!string.IsNullOrWhiteSpace(path) && IsAllowedExclusionPath(path))
            {
                var result = ApplyAsync(add: false, path).GetAwaiter().GetResult();
                if (result.Outcome == DefenderExclusionOutcome.Applied) store.UpdateDefenderExclusion(string.Empty);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            Debug.WriteLine($"HerdMe could not remove its Defender exclusion: {error.Message}");
        }
        return true;
    }
}
