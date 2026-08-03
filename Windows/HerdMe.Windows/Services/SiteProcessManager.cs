using System.Collections.Concurrent;

namespace HerdMe.Windows.Services;

public enum SiteBackgroundProcessKind
{
    Queue,
    Scheduler
}

public sealed record SiteQueueWorkerOptions(
    string Connection = "",
    string Queue = "",
    int Tries = 1,
    int TimeoutSeconds = 60,
    int SleepSeconds = 3,
    int MaximumJobs = 0,
    int MaximumSeconds = 0
)
{
    public IReadOnlyList<string> Arguments()
    {
        if (Tries is < 1 or > 100 || TimeoutSeconds is < 0 or > 86_400
            || SleepSeconds is < 0 or > 3_600 || MaximumJobs is < 0 or > 1_000_000
            || MaximumSeconds is < 0 or > 2_592_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Tries), "Queue worker settings are outside the supported range.");
        }
        if (!SafeName(Connection) || !SafeName(Queue))
        {
            throw new ArgumentException("Queue connection and name may contain only letters, numbers, dashes, underscores, commas, and dots.");
        }
        var arguments = new List<string> { "queue:work" };
        if (!string.IsNullOrWhiteSpace(Connection)) arguments.Add(Connection.Trim());
        arguments.Add("--no-interaction");
        if (!string.IsNullOrWhiteSpace(Queue)) arguments.Add($"--queue={Queue.Trim()}");
        arguments.Add($"--tries={Tries}");
        arguments.Add($"--timeout={TimeoutSeconds}");
        arguments.Add($"--sleep={SleepSeconds}");
        if (MaximumJobs > 0) arguments.Add($"--max-jobs={MaximumJobs}");
        if (MaximumSeconds > 0) arguments.Add($"--max-time={MaximumSeconds}");
        return arguments;
    }

    private static bool SafeName(string value) => value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or ',' or '.');
}

public sealed record SiteBackgroundProcessState(
    string SitePath,
    SiteBackgroundProcessKind Kind,
    bool Running,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    string Output
);

public sealed class SiteProcessManager : IAsyncDisposable
{
    private sealed class RunningProcess(
        CancellationTokenSource cancellation,
        DateTimeOffset startedAt
    )
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, RunningProcess> running =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SiteBackgroundProcessState> states =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public SiteBackgroundProcessState State(string sitePath, SiteBackgroundProcessKind kind)
    {
        var path = Path.GetFullPath(sitePath);
        return states.TryGetValue(Key(path, kind), out var state)
            ? state
            : new SiteBackgroundProcessState(path, kind, false, null, null, string.Empty);
    }

    public void Start(
        string sitePath,
        SiteBackgroundProcessKind kind,
        string phpExecutable,
        IReadOnlyDictionary<string, string> environment,
        SiteQueueWorkerOptions? queueOptions = null
    )
    {
        var path = Path.GetFullPath(sitePath);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (!File.Exists(Path.Combine(path, "artisan")))
        {
            throw new InvalidOperationException("This site does not contain Laravel Artisan.");
        }
        var key = Key(path, kind);
        if (running.ContainsKey(key)) return;

        var cancellation = new CancellationTokenSource();
        var startedAt = DateTimeOffset.UtcNow;
        states[key] = new SiteBackgroundProcessState(path, kind, true, startedAt, null, string.Empty);
        var process = new RunningProcess(cancellation, startedAt);
        if (!running.TryAdd(key, process))
        {
            cancellation.Dispose();
            return;
        }
        process.Task = RunAsync(key, path, kind, phpExecutable, environment, queueOptions, cancellation.Token);
        RaiseChanged();
    }

    public async Task StopAsync(string sitePath, SiteBackgroundProcessKind kind)
    {
        var key = Key(Path.GetFullPath(sitePath), kind);
        if (!running.TryGetValue(key, out var process)) return;
        process.Cancellation.Cancel();
        try
        {
            await process.Task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task StopAllAsync()
    {
        var processes = running.Values.ToArray();
        foreach (var process in processes) process.Cancellation.Cancel();
        try
        {
            await Task.WhenAll(processes.Select(process => process.Task));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(
        string key,
        string sitePath,
        SiteBackgroundProcessKind kind,
        string phpExecutable,
        IReadOnlyDictionary<string, string> environment,
        SiteQueueWorkerOptions? queueOptions,
        CancellationToken cancellationToken
    )
    {
        var command = kind == SiteBackgroundProcessKind.Queue
            ? new ArtisanCommandSpec(
                (queueOptions ?? new SiteQueueWorkerOptions()).Arguments(),
                TimeSpan.FromDays(30)
            )
            : new ArtisanCommandSpec(
                ["schedule:work", "--no-interaction"],
                TimeSpan.FromDays(30)
            );
        var output = new Progress<string>(text => UpdateOutput(key, text));
        int? exitCode = null;
        try
        {
            var result = await ArtisanCommandRunner.RunAsync(
                phpExecutable,
                sitePath,
                command.Arguments,
                environment,
                command.Timeout,
                output,
                cancellationToken
            );
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            UpdateOutput(key, error.Message + Environment.NewLine);
            exitCode = -1;
        }
        finally
        {
            running.TryRemove(key, out var process);
            process?.Cancellation.Dispose();
            var previous = states[key];
            states[key] = previous with { Running = false, ExitCode = exitCode };
            RaiseChanged();
        }
    }

    private void UpdateOutput(string key, string text)
    {
        const int maximumCharacters = 256 * 1_024;
        states.AddOrUpdate(
            key,
            _ => throw new InvalidOperationException("The site process state is missing."),
            (_, previous) =>
            {
                var combined = previous.Output + text;
                if (combined.Length > maximumCharacters) combined = combined[^maximumCharacters..];
                return previous with { Output = combined };
            }
        );
        RaiseChanged();
    }

    private static string Key(string sitePath, SiteBackgroundProcessKind kind) =>
        $"{sitePath}|{kind}";

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        GC.SuppressFinalize(this);
    }
}
