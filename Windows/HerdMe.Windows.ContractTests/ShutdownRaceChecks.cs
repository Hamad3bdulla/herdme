using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyManagerShutdownContractsAsync(string supportRoot)
    {
        await VerifyInstallerShutdownAsync(Path.Combine(supportRoot, "installer-shutdown"));
        await VerifyQueuedServiceShutdownAsync(Path.Combine(supportRoot, "service-shutdown"));
        await VerifyQueuedEnvironmentShutdownAsync(Path.Combine(supportRoot, "environment-shutdown"));
    }

    private static async Task VerifyInstallerShutdownAsync(string supportRoot)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = false;
        await using var manager = new WindowsServiceManager(supportRoot, installPackage: async (definition, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await cancelled.Task;
            await releaseCleanup.Task;
            cleanedUp = true;
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The fixture installer must be cancelled.");
        });
        using var callerCancellation = new CancellationTokenSource();
        var installation = manager.InstallAsync("mysql", callerCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => installation,
            "cancelling an installation waiter leaves the shared installer owned by its manager");
        Check(!cancelled.Task.IsCompleted,
            "a caller cancellation does not interrupt another caller's shared installation");

        var disposal = manager.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!disposal.IsCompleted && !cleanedUp,
                "manager disposal cancels an abandoned installer and waits for its cleanup");
            Check(ReferenceEquals(disposal, manager.DisposeAsync().AsTask()),
                "concurrent manager disposal requests share one completion task");
            Throws<ObjectDisposedException>(() => manager.InstallAsync("mysql"),
                "shutdown prevents new installations while an existing installer drains");
            await ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(Guid.NewGuid()),
                "shutdown rejects service starts before accessing a runtime or credentials");
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cleanedUp && !manager.IsInstalling("mysql"),
            "manager disposal completes only after installer cleanup and tracking removal");
        await manager.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task VerifyQueuedServiceShutdownAsync(string supportRoot)
    {
        await using var manager = new WindowsServiceManager(supportRoot);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(
                () => manager.StartEnabledAsync(cancellation.Token),
                "automatic service startup preserves application shutdown cancellation");
        }
        // Hold the same gate as a runtime launch so ordering is deterministic,
        // without installing a real runtime or creating user service processes.
        var gate = PrivateDependency<SemaphoreSlim>(manager, "lifecycle");
        await gate.WaitAsync();
        Task stop;
        try
        {
            stop = manager.StopAllAsync();
            Check(!stop.IsCompleted,
                "stop-all waits for an in-flight launch before inspecting active services");
        }
        finally
        {
            gate.Release();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        await gate.WaitAsync();
        Task queuedStart;
        Task disposal;
        try
        {
            queuedStart = manager.StartAsync(Guid.NewGuid());
            disposal = manager.DisposeAsync().AsTask();
            Check(!queuedStart.IsCompleted && !disposal.IsCompleted,
                "disposal waits behind an outstanding service operation even before a process is registered");
        }
        finally
        {
            gate.Release();
        }
        await ThrowsAsync<ObjectDisposedException>(
            () => queuedStart.WaitAsync(TimeSpan.FromSeconds(5)),
            "a queued service launch rechecks disposal after it acquires the lifecycle gate");
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(manager.StopAsync(Guid.NewGuid()), manager.StopAllAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task VerifyQueuedEnvironmentShutdownAsync(string supportRoot)
    {
        await using var environment = new WindowsLocalEnvironment(
            runtimePolicy: new PhpRuntimePolicy(supportRoot: supportRoot));
        environment.RequestResumeRecovery();
        Check(!environment.IsRunning && !environment.IsDegraded,
            "a resume notification does not start an intentionally stopped environment");
        var sites = new[]
        {
            new SiteRecord { Name = "shutdown-fixture", Path = supportRoot, Domain = "shutdown-fixture.test" }
        };
        var gate = PrivateDependency<SemaphoreSlim>(environment, "operationLock");
        await gate.WaitAsync();
        Task queuedStart;
        Task queuedSynchronization;
        Task disposal;
        try
        {
            queuedStart = environment.StartAsync(sites);
            queuedSynchronization = environment.SynchronizeSitesAsync(sites);
            disposal = environment.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted && ReferenceEquals(disposal, environment.DisposeAsync().AsTask()),
                "environment disposal waits for outstanding operations and is idempotent");
        }
        finally
        {
            gate.Release();
        }
        await ThrowsAsync<ObjectDisposedException>(
            () => queuedStart.WaitAsync(TimeSpan.FromSeconds(5)),
            "queued environment startup cannot start runtimes after disposal begins");
        await ThrowsAsync<ObjectDisposedException>(
            () => queuedSynchronization.WaitAsync(TimeSpan.FromSeconds(5)),
            "queued site synchronization cannot re-enable recovery after disposal begins");
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        environment.RequestResumeRecovery();
        var store = new SiteConfigurationStore(supportRoot);
        await ThrowsAsync<ObjectDisposedException>(() => environment.StartConfiguredAsync(store),
            "a delayed UI continuation cannot restart the disposed environment");
        Check(!File.Exists(store.SettingsPath) && !environment.IsRunning && !environment.IsDegraded,
            "rejected restarts preserve settings and leave recovery disabled");
        await environment.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
