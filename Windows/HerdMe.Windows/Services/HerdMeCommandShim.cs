using System.Text;

namespace HerdMe.Windows.Services;

// Writes %LOCALAPPDATA%\HerdMe\bin\herdme.cmd, which forwards to herdme.exe next to the
// installed app. The bin directory is already on the user PATH (ComposerToolManager), so
// a new terminal can run "herdme status" without another PATH change.
public static class HerdMeCommandShim
{
    public const string ExecutableName = "herdme.exe";
    public const string ShimName = "herdme.cmd";

    public static string ShimPath(string supportRoot) => Path.Combine(supportRoot, "bin", ShimName);

    public static string BuildContent(string executablePath)
    {
        // Batch expands %VAR% even inside quotes; double the percent signs of a literal path.
        var escaped = executablePath.Replace("%", "%%", StringComparison.Ordinal);
        return string.Join("\r\n", [
            "@echo off",
            $"\"{escaped}\" %*",
            "exit /b %ERRORLEVEL%",
            string.Empty
        ]);
    }

    // Returns true when the shim now points at the executable. Missing executables (a
    // development build without the CLI) leave any existing shim alone.
    public static bool Ensure(string supportRoot, string applicationDirectory)
    {
        var executable = Path.Combine(applicationDirectory, ExecutableName);
        if (!File.Exists(executable)) return false;
        var shimPath = ShimPath(supportRoot);
        var content = BuildContent(Path.GetFullPath(executable));
        try
        {
            if (File.Exists(shimPath)
                && File.ReadAllText(shimPath).Equals(content, StringComparison.Ordinal))
            {
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
            var temporary = shimPath + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, shimPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
