using System.Runtime.InteropServices;

namespace HerdMe.Windows.Services;

public sealed class WindowsUserPathManager
{
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public WindowsUserPathManager(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public void Synchronize(IEnumerable<string> managedDirectories)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows user PATH is only available on Windows.");
        }
        var directories = managedDirectories
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (directories.Length == 0)
        {
            throw new InvalidOperationException("No installed HerdMe command-line tools were found.");
        }

        var userPath = MergeManagedPath(
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
            SupportRoot,
            directories
        );
        Environment.SetEnvironmentVariable("Path", userPath, EnvironmentVariableTarget.User);

        var processPath = MergeManagedPath(
            Environment.GetEnvironmentVariable("Path"),
            SupportRoot,
            directories
        );
        Environment.SetEnvironmentVariable("Path", processPath);
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            UIntPtr.Zero,
            "Environment",
            SmtoAbortIfHung,
            5_000,
            out _
        );
    }

    internal static string MergeManagedPath(
        string? existingPath,
        string supportRoot,
        IEnumerable<string> managedDirectories
    )
    {
        var managed = managedDirectories
            .Select(NormalizeEntry)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preserved = (existingPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0 && !IsManagedEntry(entry, supportRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        managed.AddRange(preserved.Where(entry => !managed.Contains(
            entry,
            StringComparer.OrdinalIgnoreCase
        )));
        return string.Join(Path.PathSeparator, managed);
    }

    private static bool IsManagedEntry(string entry, string supportRoot)
    {
        var normalized = NormalizeEntry(Environment.ExpandEnvironmentVariables(entry));
        var root = NormalizeEntry(supportRoot);
        if (normalized is null || root is null) return false;
        var relative = Path.GetRelativePath(root, normalized);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return false;
        }
        return relative.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(Path.Combine("Composer", "vendor", "bin"), StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(Path.Combine("Runtimes", "php") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(Path.Combine("Runtimes", "node") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(Path.Combine("Runtimes", "git") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeEntry(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains(Path.PathSeparator)) return null;
            return Path.GetFullPath(value.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result
    );
}
