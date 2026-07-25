using Microsoft.Win32;
using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HerdMe";

    public bool IsEnabled
    {
        get
        {
            EnsureWindows();
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value
                && !string.IsNullOrWhiteSpace(value);
        }
    }

    public void SetEnabled(bool enabled)
    {
        EnsureWindows();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("HerdMe could not determine its executable path.");
        }
        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration requires Windows.");
        }
    }
}
