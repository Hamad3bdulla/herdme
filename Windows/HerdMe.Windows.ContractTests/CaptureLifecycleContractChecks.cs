using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyCaptureLifecycleContractsAsync(string supportRoot)
    {
        await using var mail = new MailCaptureService(Path.Combine(supportRoot, "mail-lifecycle"));
        using var mailObserver = new CaptureLifecycleObserver();
        mail.MessageCaptured += (_, _) => mailObserver.Captured();
        await VerifyCaptureLifecycleAsync(
            "SMTP", mail.StartAsync, mail.StopAsync, mail.DisposeAsync,
            () => mail.IsRunning, () => mail.Port, mailObserver,
            async (client, token) =>
            {
                using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };
                Check((await reader.ReadLineAsync(token))?.StartsWith("220 ", StringComparison.Ordinal) == true,
                    "SMTP restart accepts a fresh client");
                await writer.WriteLineAsync("MAIL FROM:<sender@example.test>".AsMemory(), token);
                await reader.ReadLineAsync(token);
                await writer.WriteLineAsync("RCPT TO:<recipient@example.test>".AsMemory(), token);
                await reader.ReadLineAsync(token);
                await writer.WriteLineAsync("DATA".AsMemory(), token);
                await reader.ReadLineAsync(token);
                await writer.WriteLineAsync("Subject: lifecycle\r\n\r\nCaptured after restart.\r\n.".AsMemory(), token);
                Check((await reader.ReadLineAsync(token))?.StartsWith("250 ", StringComparison.Ordinal) == true,
                    "SMTP acknowledges a complete message after restart");
            }
        );
        Check(mail.Load().Count >= 34, "SMTP captures remain durable across lifecycle transitions");

        await using var dumps = new DumpCaptureService(Path.Combine(supportRoot, "dump-lifecycle"));
        using var dumpObserver = new CaptureLifecycleObserver();
        dumps.DumpCaptured += (_, _) => dumpObserver.Captured();
        await VerifyCaptureLifecycleAsync(
            "VarDumper", dumps.StartAsync, dumps.StopAsync, dumps.DisposeAsync,
            () => dumps.IsRunning, () => dumps.Port, dumpObserver,
            async (client, token) =>
            {
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("s:9:\"lifecycle\";")) + "\n";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(payload), token);
            }
        );
        Check(dumps.Load().Count >= 34, "VarDumper captures remain durable across lifecycle transitions");
    }

    private static async Task VerifyCaptureLifecycleAsync(
        string name,
        Func<int, CancellationToken, Task> start,
        Func<Task> stop,
        Func<ValueTask> dispose,
        Func<bool> isRunning,
        Func<int?> port,
        CaptureLifecycleObserver observer,
        Func<TcpClient, CancellationToken, Task> capture
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => start(0, canceled.Token),
                name + " rejects an already cancelled start");
            Check(!isRunning() && port() is null, name + " cancellation leaves no published listener");
        }

        using (var occupied = new TcpListener(IPAddress.Loopback, 0))
        {
            occupied.ExclusiveAddressUse = true;
            occupied.Start();
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            await ThrowsAsync<SocketException>(() => start(occupiedPort, token),
                name + " reports a port conflict");
            Check(!isRunning() && port() is null, name + " failed binds leave no phantom running service");
            await start(0, token);
            Check(isRunning() && port() is not null, name + " immediately retries a failed bind");
        }

        await start(0, token);
        var originalPort = port();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => start(0, token)));
        Check(isRunning() && port() == originalPort, name + " concurrent starts preserve one listener");

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port()!.Value, token);
            await capture(client, token);
            await observer.WaitForCaptureAsync(token);
        }

        observer.BlockNextCapture();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port()!.Value, token);
            await capture(client, token);
            await observer.PersistenceBlocked.WaitAsync(token);
            var stopping = stop();
            var restarting = start(0, token);
            try
            {
                Check(!stopping.IsCompleted && !restarting.IsCompleted,
                    name + " restart waits for the previous persistence worker to drain");
                using var canceledRestart = new CancellationTokenSource();
                var waitingRestart = start(0, canceledRestart.Token);
                canceledRestart.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => waitingRestart,
                    name + " queued starts honor cancellation while shutdown drains");
            }
            finally { observer.ReleasePersistence(); }
            await Task.WhenAll(stopping, restarting).WaitAsync(token);
            await observer.WaitForCaptureAsync(token);
            Check(isRunning() && port() is not null, name + " restarts after concurrent shutdown");
        }

        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, port()!.Value, token);
                await capture(client, token);
                await observer.WaitForCaptureAsync(token);
            }
            var pendingClient = new TcpClient();
            clients.Add(pendingClient);
            await pendingClient.ConnectAsync(IPAddress.Loopback, port()!.Value, token);
            // Let the accept loop reach the saturated session gate before cancellation.
            await Task.Delay(100, token);
            await Task.WhenAll(stop(), stop()).WaitAsync(token);
            Check(!isRunning() && port() is null, name + " concurrent stops release the listener");
            foreach (var client in clients)
            {
                await VerifyCaptureClientClosedAsync(client, name, token);
            }
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }

        await start(0, token);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port()!.Value, token);
            await capture(client, token);
            await observer.WaitForCaptureAsync(token);
        }
        await dispose().AsTask().WaitAsync(token);
        await ThrowsAsync<ObjectDisposedException>(() => start(0, token),
            name + " disposal prevents orphan listeners from being restarted");
    }

    private static async Task VerifyCaptureClientClosedAsync(
        TcpClient client,
        string name,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var count = await client.GetStream().ReadAsync(new byte[1], timeout.Token);
            Check(count == 0, name + " shutdown closes active and waiting clients");
        }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private sealed class CaptureLifecycleObserver : IDisposable
    {
        private readonly Channel<bool> captures = Channel.CreateUnbounded<bool>();
        private readonly ManualResetEventSlim releasePersistence = new(false);
        private readonly TaskCompletionSource persistenceBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blockNext;

        public Task PersistenceBlocked => persistenceBlocked.Task;

        public void BlockNextCapture() => Interlocked.Exchange(ref blockNext, 1);

        public void ReleasePersistence() => releasePersistence.Set();

        public void Captured()
        {
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                persistenceBlocked.TrySetResult();
                if (!releasePersistence.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Capture persistence fixture was not released.");
                }
            }
            captures.Writer.TryWrite(true);
        }

        public async Task WaitForCaptureAsync(CancellationToken cancellationToken) =>
            await captures.Reader.ReadAsync(cancellationToken);

        public void Dispose()
        {
            releasePersistence.Set();
            releasePersistence.Dispose();
        }
    }
}
