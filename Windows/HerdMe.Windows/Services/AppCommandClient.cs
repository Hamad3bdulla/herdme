using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

// Sends one command to the running HerdMe window over the per-session named pipe. The pipe is
// local-only; CurrentUserOnly makes the client refuse a pipe owned by another account.
[SupportedOSPlatform("windows")]
public static class AppCommandClient
{
    public static string CurrentPipeName()
    {
        using var process = Process.GetCurrentProcess();
        return AppCommandProtocol.PipeName(process.SessionId);
    }

    // Returns null when no HerdMe instance accepted the connection in time.
    public static async Task<AppCommandResponse?> TrySendAsync(
        AppCommandRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default
    )
    {
        var payload = AppCommandProtocol.Serialize(request);
        using var pipe = new NamedPipeClientStream(
            ".",
            CurrentPipeName(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        try
        {
            await pipe.ConnectAsync(
                (int)Math.Clamp(connectTimeout.TotalMilliseconds, 1, int.MaxValue),
                cancellationToken
            );
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeout);
        try
        {
            await pipe.WriteAsync(payload, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var line = await AppCommandProtocol.ReadLineAsync(pipe, timeout.Token);
            if (line is null) return AppCommandResponse.Failure("HerdMe closed the connection.");
            return AppCommandProtocol.DeserializeResponse(line)
                ?? AppCommandResponse.Failure("HerdMe sent an unreadable response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AppCommandResponse.Failure("HerdMe did not answer in time.");
        }
        catch (IOException error)
        {
            return AppCommandResponse.Failure($"The connection to HerdMe failed: {error.Message}");
        }
    }

    // Retries until the primary instance starts listening (it may still be starting).
    public static async Task<AppCommandResponse?> SendWithRetryAsync(
        AppCommandRequest request,
        TimeSpan totalWait,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default
    )
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var remaining = totalWait - deadline.Elapsed;
            var attempt = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            if (attempt <= TimeSpan.Zero) return null;
            var response = await TrySendAsync(request, attempt, responseTimeout, cancellationToken);
            if (response is not null) return response;
            if (deadline.Elapsed >= totalWait) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
    }
}
