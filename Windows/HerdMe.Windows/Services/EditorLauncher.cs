using System.ComponentModel;
using System.Diagnostics;

namespace HerdMe.Windows.Services;

public enum EditorOpenKind
{
    VisualStudioCode,
    DefaultApp
}

// Opens a source file at a line. Visual Studio Code is started directly (Code.exe -g
// file:line, no console window); without it the file opens in its default app, which cannot
// jump to the line.
public static class EditorLauncher
{
    public static IReadOnlyList<string> VisualStudioCodeArguments(string path, int? line)
    {
        var target = Path.GetFullPath(path);
        return line is > 0 ? ["-g", target + ":" + line.Value] : ["-g", target];
    }

    public static string? FindVisualStudioCode(
        string? pathVariable = null,
        string? localApplicationData = null,
        string? programFiles = null
    )
    {
        foreach (var candidate in VisualStudioCodeCandidates(pathVariable, localApplicationData, programFiles))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IEnumerable<string> VisualStudioCodeCandidates(
        string? pathVariable = null,
        string? localApplicationData = null,
        string? programFiles = null
    )
    {
        // "code" on PATH is bin\code.cmd; the editor itself sits one folder up.
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? parent;
            try
            {
                if (!File.Exists(Path.Combine(entry, "code.cmd"))) continue;
                parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)));
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                continue;
            }
            if (parent is not null) yield return Path.Combine(parent, "Code.exe");
        }
        var local = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe");
        var programs = programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programs)) yield return Path.Combine(programs, "Microsoft VS Code", "Code.exe");
    }

    public static EditorOpenKind Open(string path, int? line)
    {
        var target = Path.GetFullPath(path);
        if (!File.Exists(target)) throw new FileNotFoundException("The file is not on this PC.", target);
        if (FindVisualStudioCode() is { } code)
        {
            var startInfo = new ProcessStartInfo(code)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty
            };
            foreach (var argument in VisualStudioCodeArguments(target, line)) startInfo.ArgumentList.Add(argument);
            try
            {
                using var editor = Process.Start(startInfo);
                return EditorOpenKind.VisualStudioCode;
            }
            catch (Win32Exception)
            {
                // A broken install falls through to the default app.
            }
        }
        // Handing a document to Explorer's file association needs the shell.
        using var fallback = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return EditorOpenKind.DefaultApp;
    }
}
