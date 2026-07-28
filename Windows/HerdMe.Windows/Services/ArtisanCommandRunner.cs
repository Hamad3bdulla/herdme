using System.Diagnostics;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed record ArtisanCommandPreset(
    string Id,
    string Title,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout
);

public sealed record ArtisanCommandSpec(
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout
);

public sealed record ArtisanCommandResult(int ExitCode, string Output);

public static class ArtisanCommandCatalog
{
    private const int MaximumCommandBytes = 4 * 1_024;
    private const int MaximumArgumentBytes = 512;
    private const int MaximumArguments = 32;

    public static IReadOnlyList<ArtisanCommandPreset> Presets { get; } = Array.AsReadOnly(
        new[]
        {
            new ArtisanCommandPreset("route-list", "Route List", ["route:list", "--no-interaction"], TimeSpan.FromMinutes(5)),
            new ArtisanCommandPreset("migrate-status", "Migration Status", ["migrate:status", "--no-interaction"], TimeSpan.FromMinutes(5)),
            new ArtisanCommandPreset("migrate", "Migrate", ["migrate", "--no-interaction"], TimeSpan.FromMinutes(15)),
            new ArtisanCommandPreset("queue-work", "Queue Worker", ["queue:work", "--no-interaction"], TimeSpan.FromHours(24)),
            new ArtisanCommandPreset("custom", "Custom", [], TimeSpan.FromMinutes(15))
        }
    );

    public static ArtisanCommandSpec Resolve(string presetId, string customCommand)
    {
        var preset = Presets.SingleOrDefault(item => item.Id == presetId)
            ?? throw new ArgumentException("Choose a supported Artisan command.", nameof(presetId));
        return preset.Id == "custom"
            ? new ArtisanCommandSpec(Parse(customCommand), preset.Timeout)
            : new ArtisanCommandSpec(preset.Arguments, preset.Timeout);
    }

    public static IReadOnlyList<string> Parse(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Enter an Artisan command.", nameof(command));
        if (Encoding.UTF8.GetByteCount(trimmed) > MaximumCommandBytes)
        {
            throw new ArgumentException("Artisan commands are limited to 4 KB.", nameof(command));
        }
        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The Artisan command contains an unsupported control character.",
                nameof(command)
            );
        }

        var arguments = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';
        var escaping = false;
        var tokenStarted = false;

        void AppendToken()
        {
            var value = token.ToString();
            if (Encoding.UTF8.GetByteCount(value) > MaximumArgumentBytes)
            {
                throw new ArgumentException(
                    "Artisan arguments are limited to 512 bytes.",
                    nameof(command)
                );
            }
            arguments.Add(value);
            if (arguments.Count > MaximumArguments)
            {
                throw new ArgumentException(
                    "Artisan commands are limited to 32 arguments.",
                    nameof(command)
                );
            }
            token.Clear();
            tokenStarted = false;
        }

        foreach (var character in trimmed)
        {
            if (escaping)
            {
                token.Append(character);
                tokenStarted = true;
                escaping = false;
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                tokenStarted = true;
                continue;
            }
            if (character == '\'' && quote != '"')
            {
                quote = quote == '\'' ? '\0' : '\'';
                tokenStarted = true;
                continue;
            }
            if (character == '"' && quote != '\'')
            {
                quote = quote == '"' ? '\0' : '"';
                tokenStarted = true;
                continue;
            }
            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (tokenStarted) AppendToken();
                continue;
            }
            token.Append(character);
            tokenStarted = true;
        }

        if (escaping || quote != '\0')
        {
            throw new ArgumentException(
                "The Artisan command contains an unfinished quote.",
                nameof(command)
            );
        }
        if (tokenStarted) AppendToken();
        if (arguments.FirstOrDefault() == "artisan") arguments.RemoveAt(0);
        if (arguments.Count == 0
            || arguments[0].Length == 0
            || arguments[0].StartsWith("-", StringComparison.Ordinal)
            || arguments[0] == "php")
        {
            throw new ArgumentException(
                "Enter an Artisan command such as route:list, without php or shell syntax.",
                nameof(command)
            );
        }
        return arguments;
    }
}

public static class ArtisanCommandRunner
{
    private const int MaximumCapturedCharacters = 1 * 1_024 * 1_024;

    public static async Task<ArtisanCommandResult> RunAsync(
        string phpExecutable,
        string projectDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        IProgress<string>? outputProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        var projectPath = Path.GetFullPath(projectDirectory);
        if (!File.Exists(phpExecutable))
        {
            throw new FileNotFoundException("Install the selected HerdMe PHP runtime first.", phpExecutable);
        }
        if (!Directory.Exists(projectPath) || !File.Exists(Path.Combine(projectPath, "artisan")))
        {
            throw new InvalidOperationException(
                "Artisan is available only for Laravel projects with an artisan executable."
            );
        }
        if (arguments.Count == 0 || timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The Artisan invocation is incomplete.", nameof(arguments));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = phpExecutable,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("artisan");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment) startInfo.Environment[variable.Key] = variable.Value;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The selected PHP runtime could not start Artisan.");
        var capture = new BoundedOutputCapture(MaximumCapturedCharacters);
        var standardOutput = PumpAsync(process.StandardOutput, capture, outputProgress);
        var standardError = PumpAsync(process.StandardError, capture, outputProgress);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token
        );
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while cancellation was being delivered.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The Artisan command did not finish within {timeout.TotalMinutes:0} minutes."
            );
        }
        await Task.WhenAll(standardOutput, standardError);
        return new ArtisanCommandResult(process.ExitCode, capture.Value);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        BoundedOutputCapture capture,
        IProgress<string>? outputProgress
    )
    {
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) return;
            var chunk = new string(buffer, 0, count);
            capture.Append(chunk);
            outputProgress?.Report(chunk);
        }
    }

    private sealed class BoundedOutputCapture(int maximumCharacters)
    {
        private readonly object sync = new();
        private readonly StringBuilder output = new();

        public string Value
        {
            get
            {
                lock (sync) return output.ToString();
            }
        }

        public void Append(string value)
        {
            lock (sync)
            {
                output.Append(value);
                if (output.Length > maximumCharacters)
                {
                    output.Remove(0, output.Length - maximumCharacters);
                }
            }
        }
    }
}
