using System.Collections.ObjectModel;

namespace HerdMe.Windows.Services;

internal sealed class CaptureRefreshSession<T>(Func<T> load) : IDisposable where T : class
{
    private int requested = 1;
    private int completed;
    private int revision;
    private int running;
    private int disposed;

    public bool NeedsRefresh => Volatile.Read(ref requested) != Volatile.Read(ref completed);
    public bool IsLoading => Volatile.Read(ref running) != 0;

    public void RequestRefresh() => Interlocked.Increment(ref requested);

    public void Invalidate()
    {
        Interlocked.Increment(ref revision);
        RequestRefresh();
    }

    public async Task<T?> RefreshAsync()
    {
        if (Volatile.Read(ref disposed) != 0 || !NeedsRefresh
            || Interlocked.CompareExchange(ref running, 1, 0) != 0) return null;
        var request = Volatile.Read(ref requested);
        var snapshotRevision = Volatile.Read(ref revision);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return null;
            var result = await Task.Run(load);
            return Volatile.Read(ref disposed) == 0
                && snapshotRevision == Volatile.Read(ref revision) ? result : null;
        }
        finally
        {
            Volatile.Write(ref completed, request);
            Volatile.Write(ref running, 0);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
    }
}

internal static class CaptureListUpdater
{
    internal static void Apply<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> snapshot,
        Func<T, Guid> identify
    ) where T : class
    {
        var desired = snapshot.DistinctBy(identify).ToArray();
        var desiredIds = desired.Select(identify).ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(identify(target[index]))) target.RemoveAt(index);
        }
        var existing = target.ToDictionary(identify);
        for (var index = 0; index < desired.Length; index++)
        {
            var id = identify(desired[index]);
            if (index < target.Count && identify(target[index]) == id) continue;
            if (existing.TryGetValue(id, out var item)) target.Move(target.IndexOf(item), index);
            else target.Insert(index, desired[index]);
        }
    }
}
