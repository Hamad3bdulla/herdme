using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class DumpCaptureService : IAsyncDisposable
{
    private readonly CaptureDatabase database;
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly SemaphoreSlim sessionGate = new(32, 32);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object storageSync = new();
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private readonly string supportRoot;
    private readonly int retentionLimit;
    private readonly TimeSpan retentionAge;
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? acceptTask;
    private Task? persistenceTask;
    private Channel<CaptureRequest>? persistenceQueue;
    private long storageGeneration;
    private int sessionIdentifier;
    private bool disposed;

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

    public int RetentionLimit => retentionLimit;

    public TimeSpan RetentionAge => retentionAge;

    public string DirectoryPath => Path.Combine(supportRoot, "Dumps");

    public async Task StartAsync(int port = 9_912, CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning) return;
            if (port is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
            Directory.CreateDirectory(DirectoryPath);
            var activeListener = new TcpListener(IPAddress.Loopback, port);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                activeListener.Start(64);
                cancellationToken.ThrowIfCancellationRequested();
                var queue = Channel.CreateBounded<CaptureRequest>(new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
                cancellation = source;
                persistenceQueue = queue;
                persistenceTask = PersistDumpsAsync(queue.Reader);
                Port = ((IPEndPoint)activeListener.LocalEndpoint).Port;
                listener = activeListener;
                acceptTask = AcceptLoopAsync(activeListener, queue.Writer, source.Token);
            }
            catch
            {
                activeListener.Stop();
                source.Dispose();
                throw;
            }
        }
        finally { lifecycleGate.Release(); }
    }

    public IReadOnlyList<CapturedDump> Load()
    {
        return database.LoadDumps(retentionLimit, retentionAge);
    }

    /// <summary>List rows without large bodies; pass a row to <see cref="Complete"/> to show it.</summary>
    public IReadOnlyList<CapturedDump> LoadSummaries()
    {
        return database.LoadDumpSummaries(retentionLimit, retentionAge);
    }

    /// <summary>Returns the full capture for a list row, or null when it was deleted meanwhile.</summary>
    public CapturedDump? Complete(CapturedDump item)
    {
        return item.IsSummary ? database.LoadDump(item.Id) : item;
    }

    public void Clear()
    {
        lock (storageSync)
        {
            database.ClearDumps();
            Interlocked.Increment(ref storageGeneration);
        }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var source = cancellation;
        try
        {
            source?.Cancel();
            listener?.Stop();
            listener = null;
            Port = null;
            if (acceptTask is not null)
            {
                try { await acceptTask; }
                catch (OperationCanceledException) { }
                catch (SocketException) when (source?.IsCancellationRequested == true) { }
                catch (ObjectDisposedException) when (source?.IsCancellationRequested == true) { }
            }
            if (!sessions.IsEmpty)
            {
                try { await Task.WhenAll(sessions.Values).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
            }
        }
        finally
        {
            sessions.Clear();
            persistenceQueue?.Writer.TryComplete();
            try
            {
                if (persistenceTask is not null) await persistenceTask;
            }
            finally
            {
                cancellation = null;
                acceptTask = null;
                persistenceQueue = null;
                persistenceTask = null;
                source?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            disposed = true;
            await StopCoreAsync();
        }
        finally { lifecycleGate.Release(); }
        // Pooled SQLite connections keep captures.sqlite3 open; release them with the service.
        database.ReleasePooledConnections();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(
        TcpListener activeListener,
        ChannelWriter<CaptureRequest> writer,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken);
            try { await sessionGate.WaitAsync(cancellationToken); }
            catch
            {
                client.Dispose();
                throw;
            }
            var identifier = Interlocked.Increment(ref sessionIdentifier);
            var task = RunSessionAsync(client, writer, cancellationToken);
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

    private async Task RunSessionAsync(
        TcpClient client,
        ChannelWriter<CaptureRequest> writer,
        CancellationToken cancellationToken
    )
    {
        using var registration = cancellationToken.Register(client.Dispose);
        try { await HandleSessionAsync(client, writer, cancellationToken); }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ChannelClosedException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        finally { sessionGate.Release(); }
    }

    private async Task HandleSessionAsync(
        TcpClient client,
        ChannelWriter<CaptureRequest> persistenceWriter,
        CancellationToken cancellationToken
    )
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
                var request = new CaptureRequest(dump, Interlocked.Read(ref storageGeneration));
                await persistenceWriter.WriteAsync(request, cancellationToken);
                var error = await request.Completion.Task.WaitAsync(cancellationToken);
                if (error is not null) return;
            }
        }
    }

    private async Task PersistDumpsAsync(ChannelReader<CaptureRequest> reader)
    {
        await foreach (var request in reader.ReadAllAsync())
        {
            var failures = new List<(string Event, Exception Error)>();
            lock (storageSync)
            {
                try
                {
                    if (request.Generation == storageGeneration)
                    {
                        Save(request.Item);
                        request.Completion.TrySetResult(null);
                        if (DumpCaptured is { } captured)
                        {
                            foreach (EventHandler<CapturedDump> subscriber in captured.GetInvocationList())
                            {
                                try { subscriber(this, request.Item); }
                                catch (Exception error) { failures.Add(("observer", error)); }
                            }
                        }
                    }
                    request.Completion.TrySetResult(null);
                }
                catch (Exception error)
                {
                    request.Completion.TrySetResult(error);
                    failures.Add(("persistence", error));
                }
            }
            foreach (var failure in failures)
            {
                try
                {
                    await DiagnosticLog.WriteFailureAsync(
                        "dump-capture", failure.Event, failure.Error.Message,
                        failure.Error.ToString(), supportRoot
                    );
                }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine($"Capture diagnostic failed: {error.Message}");
                }
            }
        }
    }

    private sealed class CaptureRequest(CapturedDump item, long generation)
    {
        public CapturedDump Item { get; } = item;
        public long Generation { get; } = generation;
        public TaskCompletionSource<Exception?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Save(CapturedDump dump)
    {
        database.Save(dump, retentionLimit, retentionAge);
    }
}
