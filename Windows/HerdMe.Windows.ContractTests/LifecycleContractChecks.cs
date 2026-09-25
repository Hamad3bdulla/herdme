using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyApplicationLifecycleContractsAsync()
    {
        var lifetime = new ApplicationTaskLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = false;
        var disposed = false;
        var startup = lifetime.RunAsync(async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            entered.TrySetResult();
            // Simulate a launch step which needs time to finish its own cleanup.
            await releaseStartup.Task;
            Check(!disposed, "shutdown waits for an in-flight startup before disposing its services");
            token.ThrowIfCancellationRequested();
            running = true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = lifetime.StopAsync();
        var repeatedStop = lifetime.StopAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(stop, repeatedStop) && !stop.IsCompleted,
            "concurrent exit requests share the pending cancellation and drain task");
        var lateStartCalled = false;
        await lifetime.RunAsync(_ =>
        {
            lateStartCalled = true;
            return Task.CompletedTask;
        });
        Check(!lateStartCalled, "a queued startup command cannot run after shutdown begins");
        releaseStartup.TrySetResult();
        await Task.WhenAll(startup, stop).WaitAsync(TimeSpan.FromSeconds(5));
        disposed = true;
        Check(!running, "a delayed startup cannot restart services after application cancellation");

        var failingLifetime = new ApplicationTaskLifetime();
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = failingLifetime.RunAsync(async _ =>
        {
            await releaseFailure.Task;
            throw new IOException("fixture startup cleanup failed");
        });
        var failureDrain = failingLifetime.StopAsync();
        releaseFailure.TrySetResult();
        await ThrowsAsync<IOException>(() => failure,
            "startup failures remain visible to their caller");
        await ThrowsAsync<AggregateException>(() => failureDrain,
            "shutdown drains faulted startup work and reports the failure");
    }
}
