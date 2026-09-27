using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HerdMe.Windows.Services;

// Serves herdme CLI, Jump List, Explorer and herdme:// requests for the primary instance.
// The pipe is created with FirstPipeInstance (no squatting), an ACL that only allows the
// current user and denies network logons, and one small JSON line per request.
[SupportedOSPlatform("windows")]
public sealed class AppCommandServer : IAsyncDisposable
{
    private const int MaximumServerInstances = 4;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> handler;
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<Task> connections = [];
    private readonly object connectionsLock = new();
    private Task listener = Task.CompletedTask;
    private int started;

    public AppCommandServer(
        Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> handler
    )
    {
        this.handler = handler;
    }

    public string PipeName { get; } = AppCommandClient.CurrentPipeName();

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        listener = Task.Run(() => ListenAsync(cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try
        {
            await listener;
        }
        catch (OperationCanceledException)
        {
        }
        Task[] pending;
        lock (connectionsLock) pending = [.. connections];
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException)
        {
        }
        cancellation.Dispose();
    }

    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow
        ));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny
        ));
        return security;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    MaximumServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                    inBufferSize: 0,
                    outBufferSize: 0,
                    CreatePipeSecurity()
                );
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Another process already owns the name, or every instance is busy.
                await DiagnosticLog.WriteFailureAsync(
                    "command-pipe",
                    "listen-failed",
                    "HerdMe could not listen for command-line requests.",
                    error.ToString()
                );
                if (first) return;
                await DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }
            first = false;

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            var connection = ServeAsync(pipe, cancellationToken);
            lock (connectionsLock)
            {
                connections.RemoveAll(task => task.IsCompleted);
                connections.Add(connection);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            AppCommandResponse response;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(RequestReadTimeout);
                var line = await AppCommandProtocol.ReadLineAsync(pipe, readTimeout.Token);
                var request = line is null ? null : AppCommandProtocol.DeserializeRequest(line);
                var problem = AppCommandProtocol.Validate(request);
                if (problem is not null)
                {
                    response = AppCommandResponse.Failure(problem, AppCommandProtocol.UsageExitCode);
                }
                else
                {
                    using var handlerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    handlerTimeout.CancelAfter(HandlerTimeout);
                    response = await handler(request!, handlerTimeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                response = AppCommandResponse.Failure(cancellationToken.IsCancellationRequested
                    ? "HerdMe is shutting down."
                    : "The command timed out.");
            }
            catch (InvalidDataException error)
            {
                response = AppCommandResponse.Failure(error.Message, AppCommandProtocol.UsageExitCode);
            }
            catch (IOException)
            {
                return;
            }
            catch (Exception error)
            {
                await DiagnosticLog.WriteFailureAsync(
                    "command-pipe",
                    "handler-failed",
                    "A command-line request failed.",
                    error.ToString()
                );
                response = AppCommandResponse.Failure(error.Message);
            }

            try
            {
                using var writeTimeout = new CancellationTokenSource(RequestReadTimeout);
                await pipe.WriteAsync(AppCommandProtocol.Serialize(response), writeTimeout.Token);
                await pipe.FlushAsync(writeTimeout.Token);
                pipe.WaitForPipeDrain();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
