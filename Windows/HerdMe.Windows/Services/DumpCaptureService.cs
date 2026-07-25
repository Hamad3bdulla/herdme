using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class DumpCaptureService : IAsyncDisposable
{
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly string supportRoot;
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? acceptTask;
    private int sessionIdentifier;

    public event EventHandler<CapturedDump>? DumpCaptured;

    public DumpCaptureService(string? supportRoot = null)
    {
        this.supportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
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
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(64);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptLoopAsync(listener, cancellation.Token);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CapturedDump> Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        return Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(path =>
            {
                try { return JsonSerializer.Deserialize<CapturedDump>(File.ReadAllText(path)); }
                catch (Exception error) when (error is IOException or JsonException) { return null; }
            })
            .Where(dump => dump is not null)
            .Cast<CapturedDump>()
            .OrderByDescending(dump => dump.ReceivedAt)
            .ToList();
    }

    public void Clear()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")) File.Delete(path);
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
            var identifier = Interlocked.Increment(ref sessionIdentifier);
            var task = HandleSessionAsync(client, cancellationToken);
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

    private async Task HandleSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1_024))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = (await reader.ReadLineAsync(cancellationToken))?.Trim();
                if (payload is null) return;
                if (payload.Length == 0) continue;
                if (payload.Length > 16 * 1_024 * 1_024) throw new InvalidDataException("Dump payload exceeded the HerdMe limit.");
                var dump = CapturedDump.Decode(payload);
                Save(dump);
                DumpCaptured?.Invoke(this, dump);
            }
        }
    }

    private void Save(CapturedDump dump)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, dump.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(dump, jsonOptions));
        File.Move(temporary, path, true);
    }
}
