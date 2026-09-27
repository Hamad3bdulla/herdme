using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace HerdMe.Windows.Services;

public enum ShellIntegrationFeature
{
    ExplorerLink,
    UriProtocol,
    TerminalProfile
}

// Opt-in Windows integration. Each feature is off until the user turns it on in General,
// writes only keys and files named for HerdMe, and is removed again when turned off or on
// uninstall (installer.iss). Nothing here needs administrator rights.
public sealed class WindowsShellIntegration
{
    public const string ExplorerVerb = "HerdMe.Link";
    public const string DirectoryVerbKey = @"Software\Classes\Directory\shell\" + ExplorerVerb;
    public const string BackgroundVerbKey = @"Software\Classes\Directory\Background\shell\" + ExplorerVerb;
    public const string ProtocolKey = @"Software\Classes\" + AppCommandProtocol.UriScheme;
    public const string TerminalFragmentFolderName = "HerdMe";
    public const string TerminalFragmentFileName = "herdme.json";
    public const string TerminalProfileName = "HerdMe";

    private readonly string supportRoot;
    private readonly string localApplicationData;

    public WindowsShellIntegration(string supportRoot, string? localApplicationData = null)
    {
        this.supportRoot = supportRoot;
        this.localApplicationData = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public string TerminalFragmentPath => Path.Combine(
        localApplicationData,
        "Microsoft",
        "Windows Terminal",
        "Fragments",
        TerminalFragmentFolderName,
        TerminalFragmentFileName
    );

    public static string ExplorerCommand(string executable) =>
        $"{Quote(executable)} {AppCommandProtocol.LinkSwitch} \"%V\"";

    public static string ProtocolCommand(string executable) => $"{Quote(executable)} \"%1\"";

    // A Windows Terminal JSON fragment that opens Windows PowerShell with HerdMe's tools on
    // PATH and prints "herdme status". Terminal picks it up without editing its settings.
    public static string BuildTerminalFragment(
        string applicationDirectory,
        string supportRoot,
        string? startingDirectory
    )
    {
        var bin = Path.Combine(supportRoot, "bin");
        var shim = Path.Combine(bin, HerdMeCommandShim.ShimName);
        var command = "$env:PATH = " + PowerShellLiteral(bin) + " + ';' + $env:PATH; & "
            + PowerShellLiteral(shim) + " status";
        var profile = new JsonObject
        {
            ["name"] = TerminalProfileName,
            ["commandline"] = "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoLogo -NoExit -Command \""
                + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            ["icon"] = Path.Combine(applicationDirectory, "Assets", "HerdMe.ico"),
            ["startingDirectory"] = string.IsNullOrWhiteSpace(startingDirectory) ? "%USERPROFILE%" : startingDirectory,
            ["tabTitle"] = TerminalProfileName,
            ["suppressApplicationTitle"] = true
        };
        var fragment = new JsonObject { ["profiles"] = new JsonArray(profile) };
        return fragment.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    [SupportedOSPlatform("windows")]
    public bool IsEnabled(ShellIntegrationFeature feature) => feature switch
    {
        ShellIntegrationFeature.ExplorerLink => KeyExists(DirectoryVerbKey + @"\command")
            || KeyExists(BackgroundVerbKey + @"\command"),
        ShellIntegrationFeature.UriProtocol => KeyExists(ProtocolKey + @"\shell\open\command"),
        ShellIntegrationFeature.TerminalProfile => File.Exists(TerminalFragmentPath),
        _ => false
    };

    [SupportedOSPlatform("windows")]
    public void SetEnabled(
        ShellIntegrationFeature feature,
        bool enabled,
        string executable,
        string? startingDirectory = null
    )
    {
        executable = ValidateExecutable(executable);
        switch (feature)
        {
            case ShellIntegrationFeature.ExplorerLink:
                if (enabled)
                {
                    WriteVerb(DirectoryVerbKey, executable);
                    WriteVerb(BackgroundVerbKey, executable);
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(DirectoryVerbKey, throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree(BackgroundVerbKey, throwOnMissingSubKey: false);
                }
                break;
            case ShellIntegrationFeature.UriProtocol:
                if (enabled) WriteProtocol(executable);
                else Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
                NotifyAssociationsChanged();
                break;
            case ShellIntegrationFeature.TerminalProfile:
                if (enabled) WriteTerminalFragment(executable, startingDirectory);
                else DeleteTerminalFragment();
                break;
        }
    }

    // Keeps enabled integrations pointing at the running copy (a portable folder may move,
    // or an update may change the install path). Disabled integrations stay untouched.
    [SupportedOSPlatform("windows")]
    public void RepairEnabled(string executable, string? startingDirectory)
    {
        foreach (var feature in Enum.GetValues<ShellIntegrationFeature>())
        {
            try
            {
                if (IsEnabled(feature) && !PointsAt(feature, executable))
                {
                    SetEnabled(feature, enabled: true, executable, startingDirectory);
                }
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException)
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public bool PointsAt(ShellIntegrationFeature feature, string executable)
    {
        return feature switch
        {
            ShellIntegrationFeature.ExplorerLink => CommandEquals(DirectoryVerbKey + @"\command", ExplorerCommand(executable))
                && CommandEquals(BackgroundVerbKey + @"\command", ExplorerCommand(executable)),
            ShellIntegrationFeature.UriProtocol => CommandEquals(ProtocolKey + @"\shell\open\command", ProtocolCommand(executable)),
            ShellIntegrationFeature.TerminalProfile => File.Exists(TerminalFragmentPath)
                && File.ReadAllText(TerminalFragmentPath).Contains(
                    JsonEncodedText.Encode(Path.GetDirectoryName(executable)!).ToString(),
                    StringComparison.OrdinalIgnoreCase
                ),
            _ => false
        };
    }

    [SupportedOSPlatform("windows")]
    private static void WriteVerb(string keyPath, string executable)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        verb.SetValue(string.Empty, ServiceText.Get("ShellLinkWithHerdMe", "Link with HerdMe"));
        verb.SetValue("Icon", $"{Quote(executable)},0");
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue(string.Empty, ExplorerCommand(executable));
    }

    [SupportedOSPlatform("windows")]
    private static void WriteProtocol(string executable)
    {
        using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey, writable: true);
        protocol.SetValue(string.Empty, "URL:HerdMe");
        protocol.SetValue("URL Protocol", string.Empty);
        using (var icon = protocol.CreateSubKey("DefaultIcon", writable: true))
        {
            icon.SetValue(string.Empty, $"{Quote(executable)},0");
        }
        using var command = protocol.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(string.Empty, ProtocolCommand(executable));
    }

    private void WriteTerminalFragment(string executable, string? startingDirectory)
    {
        var path = TerminalFragmentPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = BuildTerminalFragment(Path.GetDirectoryName(executable)!, supportRoot, startingDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private void DeleteTerminalFragment()
    {
        var directory = Path.GetDirectoryName(TerminalFragmentPath)!;
        if (File.Exists(TerminalFragmentPath)) File.Delete(TerminalFragmentPath);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key is not null;
    }

    [SupportedOSPlatform("windows")]
    private static bool CommandEquals(string path, string expected)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return string.Equals(key?.GetValue(string.Empty) as string, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.IndexOfAny(['"', '\r', '\n', '%']) >= 0
            || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The HerdMe executable path is invalid.", nameof(executable));
        }
        return Path.GetFullPath(executable);
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [SupportedOSPlatform("windows")]
    private static void NotifyAssociationsChanged()
    {
        try
        {
            SHChangeNotify(AssociationsChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const int AssociationsChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
