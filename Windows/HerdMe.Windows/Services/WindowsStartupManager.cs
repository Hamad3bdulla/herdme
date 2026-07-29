using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed class WindowsStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HerdMe";

    public string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "HerdMe.lnk"
    );

    [SupportedOSPlatform("windows")]
    public bool IsEnabled
    {
        get
        {
            EnsureWindows();
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            if (WindowsStartupShortcut.Matches(ShortcutPath, executable)) return true;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value
                && WindowsStartupCommand.Matches(value, executable);
        }
    }

    [SupportedOSPlatform("windows")]
    public void SetEnabled(bool enabled)
    {
        EnsureWindows();
        if (!enabled)
        {
            WindowsStartupShortcut.Delete(ShortcutPath);
            DeleteLegacyRegistration();
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("HerdMe could not determine its executable path.");
        }
        WindowsStartupShortcut.Create(ShortcutPath, executable);
        DeleteLegacyRegistration();
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteLegacyRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup registration requires Windows.");
        }
    }
}

internal static class WindowsStartupShortcut
{
    private const uint RawPath = 0x4;
    private const int ShowMinimizedWithoutActivation = 7;

    [SupportedOSPlatform("windows")]
    public static void Create(string shortcutPath, string executable)
    {
        _ = WindowsStartupCommand.Create(executable);
        var target = Path.GetFullPath(executable);
        var destination = Path.GetFullPath(shortcutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp.lnk";
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            var link = (IShellLinkW)shellLink;
            link.SetPath(target);
            link.SetArguments(WindowsStartupCommand.BackgroundArgument);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("Start HerdMe after signing in to Windows");
            link.SetIconLocation(target, 0);
            link.SetShowCmd(ShowMinimizedWithoutActivation);
            ((IPersistFile)shellLink).Save(temporary, true);
            File.Move(temporary, destination, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            if (shellLink is not null && Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool Matches(string shortcutPath, string executable)
    {
        if (!File.Exists(shortcutPath)) return false;
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            ((IPersistFile)shellLink).Load(Path.GetFullPath(shortcutPath), 0);
            var link = (IShellLinkW)shellLink;
            var target = new StringBuilder(32_768);
            var arguments = new StringBuilder(4_096);
            link.GetPath(target, target.Capacity, IntPtr.Zero, RawPath);
            link.GetArguments(arguments, arguments.Capacity);
            return Path.GetFullPath(target.ToString()).Equals(
                    Path.GetFullPath(executable),
                    StringComparison.OrdinalIgnoreCase
                )
                && arguments.ToString().Trim().Equals(
                    WindowsStartupCommand.BackgroundArgument,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or COMException)
        {
            return false;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    public static void Delete(string shortcutPath)
    {
        try
        {
            File.Delete(shortcutPath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath,
            IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
            int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int maximumPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

internal static class WindowsStartupCommand
{
    public const string BackgroundArgument = "--background";

    public static string Create(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("The HerdMe executable path is invalid.", nameof(executable));
        }
        return $"\"{executable}\" {BackgroundArgument}";
    }

    public static bool Matches(string? value, string executable)
    {
        return string.Equals(value, Create(executable), StringComparison.OrdinalIgnoreCase);
    }
}
