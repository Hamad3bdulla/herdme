namespace HerdMe.Windows.Services;

internal sealed class ApplicationTaskLifetime
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<Task> tasks = [];
    private Task? stopTask;

    public Task RunAsync(Func<CancellationToken, Task> operation)
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (stopTask is not null) return Task.CompletedTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks.Add(completion.Task);
        }
        _ = CompleteOperationAsync(operation, completion);
        return completion.Task;
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task[] pending;
        lock (sync)
        {
            if (stopTask is not null) return stopTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            pending = tasks.ToArray();
        }
        _ = CancelAndDrainAsync(pending, completion);
        return completion.Task;
    }

    private async Task CompleteOperationAsync(
        Func<CancellationToken, Task> operation,
        TaskCompletionSource completion
    )
    {
        try
        {
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            await operation(token);
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            lock (sync) tasks.Remove(completion.Task);
        }
    }

    private async Task CancelAndDrainAsync(Task[] pending, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { cancellation.Cancel(); }
        catch (Exception error) { failures.Add(error); }
        try { await Task.WhenAll(pending); }
        catch (Exception error) { failures.Add(error); }
        cancellation.Dispose();
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException(failures));
    }
}
