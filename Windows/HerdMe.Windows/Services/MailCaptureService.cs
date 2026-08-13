using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class MailCaptureService : IAsyncDisposable
{
    public const int DefaultPort = 2_525;

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
    private Channel<CapturedMail>? persistenceQueue;
    private int sessionIdentifier;

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

    public string DirectoryPath => Path.Combine(supportRoot, "Mail");

    public Task StartAsync(int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        if (port is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
        Directory.CreateDirectory(DirectoryPath);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        persistenceQueue = Channel.CreateBounded<CapturedMail>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        persistenceTask = PersistMessagesAsync(persistenceQueue.Reader);
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(64);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptLoopAsync(listener, cancellation.Token);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CapturedMail> Load()
    {
        return database.LoadMail(retentionLimit, retentionAge);
    }

    public void Delete(CapturedMail message)
    {
        database.DeleteMail(message.Id);
    }

    public void Clear()
    {
        database.ClearMail();
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
                    var queue = persistenceQueue
                        ?? throw new InvalidOperationException("Mail persistence is not running.");
                    await queue.Writer.WriteAsync(message, cancellationToken);
                    await writer.WriteLineAsync("250 2.0.0 Message accepted");
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

    private async Task PersistMessagesAsync(ChannelReader<CapturedMail> reader)
    {
        await foreach (var message in reader.ReadAllAsync())
        {
            Save(message);
            MessageCaptured?.Invoke(this, message);
        }
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
