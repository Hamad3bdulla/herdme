using System.Collections.ObjectModel;
using System.Collections.Specialized;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyCapturePresentationAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var session = new CaptureRefreshSession<string>(() =>
        {
            var count = Interlocked.Increment(ref reads);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Snapshot fixture was not released.");
            return count.ToString();
        });
        var first = session.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!first.IsCompleted, "capture loading does not block the caller during slow storage reads");
            for (var index = 0; index < 1_000; index++) session.RequestRefresh();
            var overlapping = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => session.RefreshAsync()));
            Check(overlapping.All(value => value is null) && reads == 1,
                "capture bursts cannot queue overlapping storage reads");
        }
        finally { release.Set(); }
        Check(await first == "1" && session.NeedsRefresh,
            "a valid snapshot remains usable while later captures request a follow-up");
        Check(await session.RefreshAsync() == "2" && !session.NeedsRefresh,
            "one follow-up read coalesces the entire burst of capture events");
        Check(await session.RefreshAsync() is null && reads == 2,
            "idle refresh ticks avoid reading the capture database again");

        using var staleRelease = new ManualResetEventSlim();
        var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var staleSession = new CaptureRefreshSession<string>(() =>
        {
            staleEntered.TrySetResult();
            if (!staleRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Stale snapshot fixture was not released.");
            return "before deletion";
        });
        var staleRead = staleSession.RefreshAsync();
        try
        {
            await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            staleSession.Invalidate();
        }
        finally { staleRelease.Set(); }
        Check(await staleRead is null && staleSession.NeedsRefresh,
            "a read started before deletion cannot repopulate the inbox with stale records");

        using var unloadRelease = new ManualResetEventSlim();
        var unloadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unloadSession = new CaptureRefreshSession<string>(() =>
        {
            unloadEntered.TrySetResult();
            if (!unloadRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Unload fixture was not released.");
            return "old page";
        });
        var unloadedRead = unloadSession.RefreshAsync();
        try
        {
            await unloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            unloadSession.Dispose();
            unloadSession.RequestRefresh();
        }
        finally { unloadRelease.Set(); }
        Check(await unloadedRead is null && await unloadSession.RefreshAsync() is null,
            "leaving a capture page suppresses pending results and late capture notifications");

        var attempts = 0;
        using var failingSession = new CaptureRefreshSession<string>(() =>
            ++attempts == 1 ? throw new IOException("fixture storage failure") : "recovered");
        await ThrowsAsync<IOException>(() => failingSession.RefreshAsync(), "capture load failures reach the error state");
        Check(!failingSession.IsLoading && !failingSession.NeedsRefresh,
            "failed storage reads release the refresh slot without a busy retry loop");
        failingSession.RequestRefresh();
        Check(await failingSession.RefreshAsync() == "recovered", "a failed capture load can be retried");

        var retained = new CapturedMail { Subject = "Reading this message" };
        var expired = new CapturedMail();
        var latest = new CapturedMail();
        ObservableCollection<CapturedMail> items = [retained, expired];
        var notifications = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, args) => notifications.Add(args.Action);
        CapturedMail[] snapshot = [latest, new() { Id = retained.Id }, latest];
        CaptureListUpdater.Apply(items, snapshot, message => message.Id);
        Check(items.Count == 2 && items[0] == latest && items[1] == retained,
            "capture snapshots remove expired records, deduplicate IDs, and preserve selected object identity");
        Check(!notifications.Contains(NotifyCollectionChangedAction.Reset),
            "capture refresh preserves list state without resetting the entire inbox");
        notifications.Clear();
        CaptureListUpdater.Apply(items, snapshot, message => message.Id);
        Check(notifications.Count == 0, "unchanged capture snapshots do not rebuild list rows or previews");
    }
}
