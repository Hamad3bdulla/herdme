using Microsoft.Win32;
using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

public sealed class WindowsStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HerdMe";

    [SupportedOSPlatform("windows")]
    public bool IsEnabled
    {
        get
        {
            EnsureWindows();
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value
                && WindowsStartupCommand.Matches(value, executable);
        }
    }

    [SupportedOSPlatform("windows")]
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
        key.SetValue(
            ValueName,
            WindowsStartupCommand.Create(executable),
            RegistryValueKind.String
        );
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration requires Windows.");
        }
    }
}

internal static class WindowsStartupCommand
{
    public static string Create(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("The HerdMe executable path is invalid.", nameof(executable));
        }
        return $"\"{executable}\" --background";
    }

    public static bool Matches(string? value, string executable)
    {
        return string.Equals(value, Create(executable), StringComparison.OrdinalIgnoreCase);
    }
}
