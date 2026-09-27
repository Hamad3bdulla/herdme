using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;

namespace HerdMe.Windows;

// Delete with Undo instead of "Are you sure?": the page hides the item at once, the toast
// counts down with Undo, and the real delete runs only when the countdown ends. Another
// toast, leaving the app, or a second deferred action commits the pending one right away.
public sealed partial class MainWindow
{
    public const int DeferredActionSeconds = 5;
    private readonly DispatcherTimer deferredTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeferredAction? pendingDeferred;
    private int deferredSecondsLeft;
    private bool showingDeferredToast;
    private bool deferredTimerSubscribed;

    private sealed class DeferredAction(Func<Task> commit, Func<Task>? undo)
    {
        public Func<Task> Commit { get; } = commit;

        public Func<Task>? Undo { get; } = undo;

        public bool Finished { get; set; }
    }

    // The caller has already hidden the item; commit deletes it, undo shows it again.
    public void RunDeferred(string message, Func<Task> commit, Func<Task>? undo)
    {
        if (shuttingDown)
        {
            _ = RunCommitAsync(new DeferredAction(commit, undo));
            return;
        }
        if (!deferredTimerSubscribed)
        {
            deferredTimerSubscribed = true;
            deferredTimer.Tick += DeferredTimer_Tick;
        }
        _ = CommitPendingDeferredAsync();
        var action = new DeferredAction(commit, undo);
        pendingDeferred = action;
        deferredSecondsLeft = DeferredActionSeconds;
        showingDeferredToast = true;
        try
        {
            ShowToast(message, UndoLabel(), () => UndoDeferredAsync(action));
        }
        finally
        {
            showingDeferredToast = false;
        }
        // The countdown owns the toast while it runs.
        toastTimer.Stop();
        deferredTimer.Start();
    }

    // Before exit: whatever the user did not undo is carried out.
    internal async Task FlushDeferredActionsAsync()
    {
        deferredTimer.Stop();
        await CommitPendingDeferredAsync();
    }

    private string UndoLabel() => deferredSecondsLeft > 0
        ? AppLocalization.Format("DeferredUndoCountdown", AppLocalization.Get("CommonUndo"), deferredSecondsLeft)
        : AppLocalization.Get("CommonUndo");

    private async void DeferredTimer_Tick(object? sender, object e)
    {
        deferredSecondsLeft--;
        if (deferredSecondsLeft > 0 && pendingDeferred is not null)
        {
            ToastActionButton.Content = UndoLabel();
            return;
        }
        deferredTimer.Stop();
        HideToast();
        await CommitPendingDeferredAsync();
    }

    private async Task UndoDeferredAsync(DeferredAction action)
    {
        deferredTimer.Stop();
        if (action.Finished) return;
        action.Finished = true;
        if (ReferenceEquals(pendingDeferred, action)) pendingDeferred = null;
        if (action.Undo is { } undo) await undo();
    }

    private async Task CommitPendingDeferredAsync()
    {
        if (pendingDeferred is not { } action) return;
        pendingDeferred = null;
        await RunCommitAsync(action);
    }

    private static async Task RunCommitAsync(DeferredAction action)
    {
        if (action.Finished) return;
        action.Finished = true;
        try
        {
            await action.Commit();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "deferred-action",
                "commit-failed",
                "A delete that waited for Undo could not complete.",
                error.ToString()
            );
        }
    }

    // A different toast replaces the Undo button, so the pending delete goes ahead now.
    private void CommitDeferredForNewToast()
    {
        if (showingDeferredToast || pendingDeferred is null) return;
        deferredTimer.Stop();
        _ = CommitPendingDeferredAsync();
    }
}
