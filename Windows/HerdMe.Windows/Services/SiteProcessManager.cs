using System.Collections.Concurrent;

namespace HerdMe.Windows.Services;

public enum SiteBackgroundProcessKind
{
    Queue,
    Scheduler
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
        IReadOnlyDictionary<string, string> environment
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
        process.Task = RunAsync(key, path, kind, phpExecutable, environment, cancellation.Token);
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
        CancellationToken cancellationToken
    )
    {
        var command = kind == SiteBackgroundProcessKind.Queue
            ? new ArtisanCommandSpec(
                ["queue:work", "--no-interaction", "--tries=1"],
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
