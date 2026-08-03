namespace HerdMe.Windows.Services;

public sealed record QueueManagementResult(string Operation, int ExitCode, string Output);

public static class QueueManagementService
{
    public static readonly IReadOnlyList<string> SupportedActions =
        ["failed", "retry", "retry-all", "forget", "flush", "restart"];

    public static async Task<QueueManagementResult> RunAsync(
        string phpExecutable,
        string sitePath,
        string action,
        string? failedJobId,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<string> arguments = action switch
        {
            "failed" => ["queue:failed", "--no-ansi", "--no-interaction"],
            "retry" when ValidJobId(failedJobId) =>
                ["queue:retry", failedJobId!, "--no-ansi", "--no-interaction"],
            "retry-all" => ["queue:retry", "all", "--no-ansi", "--no-interaction"],
            "flush" => ["queue:flush", "--no-ansi", "--no-interaction"],
            "restart" => ["queue:restart", "--no-ansi", "--no-interaction"],
            "forget" when ValidJobId(failedJobId) =>
                ["queue:forget", failedJobId!, "--no-ansi", "--no-interaction"],
            _ => throw new ArgumentException("Unsupported queue action.", nameof(action))
        };
        var result = await ArtisanCommandRunner.RunAsync(
            phpExecutable, sitePath, arguments, environment, TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken
        );
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Output)
                    ? "The queue action failed."
                    : result.Output
            );
        }
        return new QueueManagementResult(action, result.ExitCode, result.Output);
    }

    internal static bool ValidJobId(string? value) => value is { Length: > 0 and <= 255 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
