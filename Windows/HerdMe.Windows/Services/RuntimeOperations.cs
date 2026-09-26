using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed record RuntimeOperation(string Id, string Name, ServiceInstallationProgress Progress, DateTimeOffset UpdatedAt);

public sealed class RuntimeOperations
{
    public static RuntimeOperations Shared { get; } = new();
    private readonly object sync = new();
    private readonly Dictionary<string, RuntimeOperation> history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<Task>> retries = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler? Changed;

    public IReadOnlyList<RuntimeOperation> Snapshot()
    {
        lock (sync) return history.Values.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task<T> RunAsync<T>(string id, string name,
        Func<CancellationToken, IProgress<ServiceInstallationProgress>, Task<T>> action,
        CancellationToken cancellationToken, Func<Task>? retry = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (sync)
        {
            if (cancellations.ContainsKey(id)) throw new InvalidOperationException("This runtime already has an active operation.");
            cancellations[id] = cancellation;
            retries[id] = retry ?? (async () => { await RunAsync(id, name, action, CancellationToken.None); });
        }
        var reporter = new Reporter(this, id, name);
        reporter.Report(new(id, ServiceInstallationStage.Resolving));
        try
        {
            var result = await action(cancellation.Token, reporter);
            reporter.Report(new(id, ServiceInstallationStage.Completed));
            return result;
        }
        catch (Exception error)
        {
            reporter.Report(new(id, error is OperationCanceledException
                ? ServiceInstallationStage.Cancelled : ServiceInstallationStage.Failed, Error: error.Message));
            throw;
        }
        finally
        {
            lock (sync) cancellations.Remove(id);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel(string id)
    {
        lock (sync)
        {
            if (cancellations.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        }
    }

    public Task RetryAsync(string id)
    {
        Func<Task>? retry;
        lock (sync)
        {
            if (cancellations.ContainsKey(id)) return Task.CompletedTask;
            retries.TryGetValue(id, out retry);
        }
        return retry?.Invoke() ?? Task.CompletedTask;
    }

    public void ClearCompleted()
    {
        lock (sync)
        {
            foreach (var id in history.Where(item => !item.Value.Progress.IsActive).Select(item => item.Key).ToArray())
            {
                history.Remove(id);
                retries.Remove(id);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Reporter(RuntimeOperations owner, string id, string name) : IProgress<ServiceInstallationProgress>
    {
        public void Report(ServiceInstallationProgress value)
        {
            lock (owner.sync) owner.history[id] = new(id, name, value, DateTimeOffset.UtcNow);
            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }
}
