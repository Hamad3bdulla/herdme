using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class MailCaptureService : IAsyncDisposable
{
    public const int DefaultPort = 2_525;

    private readonly CaptureDatabase database;
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly SemaphoreSlim sessionGate = new(32, 32);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object storageSync = new();
    private readonly Dictionary<Guid, CaptureRequest> pendingMessages = [];
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

    public event EventHandler<CapturedMail>? MessageCaptured;

    public MailCaptureService(
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
        database.MigrateMail(DirectoryPath);
    }

    public bool IsRunning => listener is not null;

    public int? Port { get; private set; }

    public int RetentionLimit => retentionLimit;

    public TimeSpan RetentionAge => retentionAge;

    public string DirectoryPath => Path.Combine(supportRoot, "Mail");

    public async Task StartAsync(int port = DefaultPort, CancellationToken cancellationToken = default)
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
                persistenceTask = PersistMessagesAsync(queue.Reader);
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

    public IReadOnlyList<CapturedMail> Load()
    {
        return database.LoadMail(retentionLimit, retentionAge);
    }

    /// <summary>List rows without large bodies; pass a row to <see cref="Complete"/> to show it.</summary>
    public IReadOnlyList<CapturedMail> LoadSummaries()
    {
        return database.LoadMailSummaries(retentionLimit, retentionAge);
    }

    /// <summary>Returns the full capture for a list row, or null when it was deleted meanwhile.</summary>
    public CapturedMail? Complete(CapturedMail item)
    {
        return item.IsSummary ? database.LoadMail(item.Id) : item;
    }

    public void Delete(CapturedMail message)
    {
        lock (storageSync)
        {
            database.DeleteMail(message.Id);
            if (pendingMessages.TryGetValue(message.Id, out var pending)) pending.Deleted = true;
        }
    }

    public void Clear()
    {
        lock (storageSync)
        {
            database.ClearMail();
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
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 16 * 1_024, leaveOpen: true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4 * 1_024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        })
        {
            await writer.WriteLineAsync("220 HerdMe SMTP ready");
            var sender = "Unknown sender";
            var recipients = new List<string>();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(ReadTimeout);
                var line = await reader.ReadLineAsync(readTimeout.Token);
                if (line is null) return;
                if (line.Length > 1_048_576) return;
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                {
                    await writer.WriteAsync("250-HerdMe\r\n250-8BITMIME\r\n250 SIZE 52428800\r\n");
                }
                else if (upper.StartsWith("MAIL FROM:"))
                {
                    sender = Address(line);
                    recipients.Clear();
                    await writer.WriteLineAsync("250 2.1.0 Sender accepted");
                }
                else if (upper.StartsWith("RCPT TO:"))
                {
                    recipients.Add(Address(line));
                    await writer.WriteLineAsync("250 2.1.5 Recipient accepted");
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var data = await ReadMessageAsync(reader, cancellationToken);
                    var message = CapturedMail.Parse(sender, recipients, data);
                    var request = CreateCaptureRequest(message);
                    try
                    {
                        await persistenceWriter.WriteAsync(request, cancellationToken);
                        var error = await request.Completion.Task.WaitAsync(cancellationToken);
                        await writer.WriteLineAsync(error is null
                            ? "250 2.0.0 Message accepted"
                            : "451 4.3.0 Unable to store message; try again later");
                    }
                    finally
                    {
                        lock (storageSync) pendingMessages.Remove(message.Id);
                    }
                }
                else if (upper == "RSET")
                {
                    sender = "Unknown sender";
                    recipients.Clear();
                    await writer.WriteLineAsync("250 2.0.0 Reset");
                }
                else if (upper == "NOOP")
                {
                    await writer.WriteLineAsync("250 2.0.0 OK");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("502 5.5.1 Command not implemented");
                }
            }
        }
    }

    private CaptureRequest CreateCaptureRequest(CapturedMail message)
    {
        var request = new CaptureRequest(message, Interlocked.Read(ref storageGeneration));
        lock (storageSync)
        {
            pendingMessages.Add(message.Id, request);
            return request;
        }
    }

    private async Task PersistMessagesAsync(ChannelReader<CaptureRequest> reader)
    {
        await foreach (var request in reader.ReadAllAsync())
        {
            var failures = new List<(string Event, Exception Error)>();
            lock (storageSync)
            {
                try
                {
                    if (request.Generation == storageGeneration && !request.Deleted)
                    {
                        Save(request.Item);
                        // Acknowledge durable storage before notifying observers. An observer may be
                        // slow or throw, but must not turn a committed capture into a protocol failure.
                        request.Completion.TrySetResult(null);
                        if (MessageCaptured is { } captured)
                        {
                            foreach (EventHandler<CapturedMail> subscriber in captured.GetInvocationList())
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
                finally { pendingMessages.Remove(request.Item.Id); }
            }
            foreach (var failure in failures)
            {
                try
                {
                    await DiagnosticLog.WriteFailureAsync(
                        "mail-capture", failure.Event, failure.Error.Message,
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

    private sealed class CaptureRequest(CapturedMail item, long generation)
    {
        public CapturedMail Item { get; } = item;
        public long Generation { get; } = generation;
        public bool Deleted { get; set; }
        public TaskCompletionSource<Exception?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<string> ReadMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken
    )
    {
        var output = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("SMTP DATA ended unexpectedly.");
            if (line == ".") return output.ToString();
            if (line.StartsWith("..", StringComparison.Ordinal)) line = line[1..];
            output.Append(line).Append("\r\n");
            if (output.Length > 50 * 1_024 * 1_024)
            {
                throw new InvalidDataException("SMTP message exceeded the HerdMe limit.");
            }
        }
    }

    private void Save(CapturedMail message)
    {
        database.Save(message, retentionLimit, retentionAge);
    }

    private static string Address(string command)
    {
        var colon = command.IndexOf(':');
        return colon < 0 ? command : command[(colon + 1)..].Trim(' ', '<', '>');
    }
}
