using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class DumpCaptureService : IAsyncDisposable
{
    private readonly CaptureDatabase database;
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly SemaphoreSlim sessionGate = new(32, 32);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private readonly string supportRoot;
    private readonly int retentionLimit;
    private readonly TimeSpan retentionAge;
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? acceptTask;
    private Task? persistenceTask;
    private Channel<CapturedDump>? persistenceQueue;
    private int sessionIdentifier;

    public event EventHandler<CapturedDump>? DumpCaptured;

    public DumpCaptureService(
        string? supportRoot = null,
        int retentionLimit = CaptureRetention.DefaultItemLimit,
        TimeSpan? retentionAge = null
    )
    {
        this.supportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        this.retentionLimit = Math.Max(1, retentionLimit);
        this.retentionAge = retentionAge ?? CaptureRetention.DefaultMaximumAge;
        database = new CaptureDatabase(this.supportRoot);
        database.MigrateDumps(DirectoryPath);
    }

    public bool IsRunning => listener is not null;

    public int? Port { get; private set; }

    public string DirectoryPath => Path.Combine(supportRoot, "Dumps");

    public Task StartAsync(int port = 9_912, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        if (port is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        Directory.CreateDirectory(DirectoryPath);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        persistenceQueue = Channel.CreateBounded<CapturedDump>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        persistenceTask = PersistDumpsAsync(persistenceQueue.Reader);
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(64);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptLoopAsync(listener, cancellation.Token);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CapturedDump> Load()
    {
        return database.LoadDumps(retentionLimit, retentionAge);
    }

    public void Clear()
    {
        database.ClearDumps();
    }

    public async Task StopAsync()
    {
        var source = Interlocked.Exchange(ref cancellation, null);
        source?.Cancel();
        listener?.Stop();
        listener = null;
        Port = null;
        if (acceptTask is not null)
        {
            try { await acceptTask; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (source?.IsCancellationRequested == true) { }
        }
        acceptTask = null;
        if (!sessions.IsEmpty)
        {
            try { await Task.WhenAll(sessions.Values).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        }
        sessions.Clear();
        persistenceQueue?.Writer.TryComplete();
        if (persistenceTask is not null) await persistenceTask;
        persistenceQueue = null;
        persistenceTask = null;
        source?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken);
            await sessionGate.WaitAsync(cancellationToken);
            var identifier = Interlocked.Increment(ref sessionIdentifier);
            var task = RunSessionAsync(client, cancellationToken);
            sessions[identifier] = task;
            _ = task.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    sessions.TryRemove(identifier, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleSessionAsync(client, cancellationToken); }
        finally { sessionGate.Release(); }
    }

    private async Task HandleSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1_024))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(ReadTimeout);
                var payload = (await reader.ReadLineAsync(readTimeout.Token))?.Trim();
                if (payload is null) return;
                if (payload.Length == 0) continue;
                if (payload.Length > 16 * 1_024 * 1_024) throw new InvalidDataException("Dump payload exceeded the HerdMe limit.");
                var dump = CapturedDump.Decode(payload);
                var queue = persistenceQueue
                    ?? throw new InvalidOperationException("Dump persistence is not running.");
                await queue.Writer.WriteAsync(dump, cancellationToken);
            }
        }
    }

    private async Task PersistDumpsAsync(ChannelReader<CapturedDump> reader)
    {
        await foreach (var dump in reader.ReadAllAsync())
        {
            Save(dump);
            DumpCaptured?.Invoke(this, dump);
        }
    }

    private void Save(CapturedDump dump)
    {
        database.Save(dump, retentionLimit, retentionAge);
    }
}
