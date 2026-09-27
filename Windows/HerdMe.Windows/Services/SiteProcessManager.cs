namespace HerdMe.Windows.Services;

public enum SiteBackgroundProcessKind
{
    Queue,
    Scheduler,
    Development
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
    private sealed class RunningProcess(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class OutputProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private readonly object sync = new();
    private readonly Dictionary<string, RunningProcess> running =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SiteBackgroundProcessState> states =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public event EventHandler? Changed;

    // Raised when a worker, scheduler, or Reverb process ends with an error that the user
    // did not ask for (Stop and app exit cancel first and are not reported).
    public event EventHandler<SiteBackgroundProcessState>? ExitedUnexpectedly;

    public SiteBackgroundProcessState State(string sitePath, SiteBackgroundProcessKind kind)
    {
        var path = Path.GetFullPath(sitePath);
        lock (sync)
        {
            return states.TryGetValue(Key(path, kind), out var state)
                ? state
                : new SiteBackgroundProcessState(path, kind, false, null, null, string.Empty);
        }
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
        var command = CreateCommand(kind, queueOptions);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running.ContainsKey(key)) return;

            var process = new RunningProcess(new CancellationTokenSource());
            running.Add(key, process);
            states[key] = new SiteBackgroundProcessState(
                path, kind, true, DateTimeOffset.UtcNow, null, string.Empty
            );
            // Publish the task before a concurrent stop can observe this process.
            process.Task = Task.Run(() => RunAsync(
                key, path, phpExecutable, environment, command, process
            ));
        }
        RaiseChanged();
    }

    public async Task StopAsync(string sitePath, SiteBackgroundProcessKind kind)
    {
        var key = Key(Path.GetFullPath(sitePath), kind);
        Task task;
        lock (sync)
        {
            if (!running.TryGetValue(key, out var process)) return;
            process.Cancellation.Cancel();
            task = process.Task;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task StopAllAsync()
    {
        Task[] tasks;
        lock (sync)
        {
            tasks = CancelAll();
        }
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ArtisanCommandSpec CreateCommand(
        SiteBackgroundProcessKind kind,
        SiteQueueWorkerOptions? queueOptions
    )
    {
        return kind switch
        {
            SiteBackgroundProcessKind.Queue => new ArtisanCommandSpec(
                (queueOptions ?? new SiteQueueWorkerOptions()).Arguments(),
                TimeSpan.FromDays(30)
            ),
            SiteBackgroundProcessKind.Scheduler => new ArtisanCommandSpec(
                ["schedule:work", "--no-interaction"],
                TimeSpan.FromDays(30)
            ),
            SiteBackgroundProcessKind.Development => new ArtisanCommandSpec(
                ["dev", "--no-interaction"],
                TimeSpan.FromDays(30)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private async Task RunAsync(
        string key,
        string sitePath,
        string phpExecutable,
        IReadOnlyDictionary<string, string> environment,
        ArtisanCommandSpec command,
        RunningProcess process
    )
    {
        var cancellationToken = process.Cancellation.Token;
        // Deliver output before completion so queued UI callbacks cannot leak into a restart.
        var output = new OutputProgress(text => UpdateOutput(key, text));
        int? exitCode = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            SiteBackgroundProcessState finished;
            var stoppedByUser = cancellationToken.IsCancellationRequested;
            lock (sync)
            {
                var previous = states[key];
                finished = previous with { Running = false, ExitCode = exitCode };
                states[key] = finished;
                running.Remove(key);
                process.Cancellation.Dispose();
            }
            RaiseChanged();
            if (!stoppedByUser && exitCode is not null and not 0)
            {
                ExitedUnexpectedly?.Invoke(this, finished);
            }
        }
    }

    private void UpdateOutput(string key, string text)
    {
        const int maximumCharacters = 256 * 1_024;
        lock (sync)
        {
            var previous = states[key];
            var combined = previous.Output + text;
            if (combined.Length > maximumCharacters) combined = combined[^maximumCharacters..];
            states[key] = previous with { Output = combined };
        }
        RaiseChanged();
    }

    private static string Key(string sitePath, SiteBackgroundProcessKind kind) =>
        $"{sitePath}|{kind}";

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (sync)
        {
            disposed = true;
            tasks = CancelAll();
        }
        await Task.WhenAll(tasks);
        GC.SuppressFinalize(this);
    }

    private Task[] CancelAll()
    {
        var processes = running.Values.ToArray();
        foreach (var process in processes) process.Cancellation.Cancel();
        return processes.Select(process => process.Task).ToArray();
    }
}
