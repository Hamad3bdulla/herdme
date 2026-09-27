namespace HerdMe.Windows.Services;

// Watches the top level of each parked folder so a project created, renamed, or removed
// there shows up without pressing Refresh. Only direct child folders are watched; changes
// inside a project (node_modules, vendor, git) never trigger a rescan.
public sealed class SiteRootWatcher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1_500);
    public static readonly TimeSpan DefaultRootPoll = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<string>> rootsProvider;
    private readonly Func<CancellationToken, Task> onChanged;
    private readonly TimeSpan debounce;
    private readonly TimeSpan rootPoll;
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object watchersLock = new();
    private readonly SemaphoreSlim callbackGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer debounceTimer;
    private Task poller = Task.CompletedTask;
    private Task callback = Task.CompletedTask;
    private int started;
    private int disposed;

    public SiteRootWatcher(
        Func<IReadOnlyList<string>> rootsProvider,
        Func<CancellationToken, Task> onChanged,
        TimeSpan? debounce = null,
        TimeSpan? rootPoll = null
    )
    {
        this.rootsProvider = rootsProvider;
        this.onChanged = onChanged;
        this.debounce = debounce ?? DefaultDebounce;
        this.rootPoll = rootPoll ?? DefaultRootPoll;
        debounceTimer = new Timer(_ => RunCallback(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<string> WatchedRoots
    {
        get
        {
            lock (watchersLock) return [.. watchers.Keys];
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Refresh();
        poller = Task.Run(() => PollRootsAsync(cancellation.Token));
    }

    // Reconciles the watchers with the current parked folders (added, removed, or deleted).
    public void Refresh()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        IReadOnlyList<string> roots;
        try
        {
            roots = rootsProvider();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (Directory.Exists(full)) wanted.Add(full);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        lock (watchersLock)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach (var stale in watchers.Keys.Where(root => !wanted.Contains(root)).ToList())
            {
                watchers[stale].Dispose();
                watchers.Remove(stale);
            }
            foreach (var root in wanted.Where(root => !watchers.ContainsKey(root)))
            {
                var watcher = CreateWatcher(root);
                if (watcher is not null) watchers[root] = watcher;
            }
        }
    }

    private FileSystemWatcher? CreateWatcher(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName,
                InternalBufferSize = 16 * 1024
            };
            watcher.Created += (_, _) => Schedule();
            watcher.Deleted += (_, _) => Schedule();
            watcher.Renamed += (_, _) => Schedule();
            // Overflow or a root that went away: rescan and let the next poll rewatch.
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RunCallback()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        callback = RunCallbackAsync(cancellation.Token);
    }

    private async Task RunCallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await callbackGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await onChanged(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await DiagnosticLog.WriteFailureAsync(
                "site-watcher",
                "rescan-failed",
                "HerdMe could not refresh sites after a folder change.",
                error.ToString()
            );
        }
        finally
        {
            callbackGate.Release();
        }
    }

    private async Task PollRootsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(rootPoll);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) Refresh();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel();
        await debounceTimer.DisposeAsync();
        lock (watchersLock)
        {
            foreach (var watcher in watchers.Values) watcher.Dispose();
            watchers.Clear();
        }
        try
        {
            await poller.WaitAsync(TimeSpan.FromSeconds(5));
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
        }
        cancellation.Dispose();
    }
}
