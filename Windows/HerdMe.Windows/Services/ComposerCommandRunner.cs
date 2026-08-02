using System.Diagnostics;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed record ComposerCommandPreset(string Id, IReadOnlyList<string> Arguments);

public static class ComposerCommandRunner
{
    public static IReadOnlyList<ComposerCommandPreset> Presets { get; } =
    [
        new("install", ["install", "--no-interaction"]),
        new("update", ["update", "--no-interaction"]),
        new("validate", ["validate", "--no-interaction"]),
        new("audit", ["audit", "--no-interaction"]),
        new("dump-autoload", ["dump-autoload", "--optimize", "--no-interaction"])
    ];

    public static IReadOnlyList<string> RequireArguments(string package)
    {
        var value = package.Trim();
        if (value.Length is 0 or > 255
            || value[0] == '-'
            || value.Any(char.IsWhiteSpace)
            || value.Count(character => character == '/') != 1
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '/' or '-' or '_' or '.')))
        {
            throw new ArgumentException("Enter a valid Composer package such as vendor/package.");
        }
        return ["require", value, "--no-interaction"];
    }

    public static async Task<ArtisanCommandResult> RunAsync(
        string phpExecutable,
        string composerPath,
        string projectDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IProgress<string>? outputProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(phpExecutable) || !File.Exists(composerPath))
        {
            throw new FileNotFoundException("The managed PHP or Composer runtime is unavailable.");
        }
        if (arguments.Count is 0 or > 16 || arguments.Any(value =>
            string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
        {
            throw new ArgumentException("The Composer command is invalid.", nameof(arguments));
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = phpExecutable,
            WorkingDirectory = Path.GetFullPath(projectDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(composerPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment) startInfo.Environment[variable.Key] = variable.Value;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Composer could not be started.");
        var standardOutput = ReadAsync(process.StandardOutput, outputProgress, cancellationToken);
        var standardError = ReadAsync(process.StandardError, outputProgress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var output = await standardOutput;
        var error = await standardError;
        return new ArtisanCommandResult(
            process.ExitCode,
            (output + Environment.NewLine + error).Trim(),
            output,
            error
        );
    }

    private static async Task<string> ReadAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        var output = new StringBuilder();
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            var text = new string(buffer, 0, count);
            output.Append(text);
            progress?.Report(text);
        }
        return output.ToString();
    }
}
