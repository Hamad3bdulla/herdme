using HerdMe.Windows.Services;

namespace HerdMe.Windows;

// Marks steps of the Dashboard "Getting started" checklist as they happen (a site opened, a
// mail or dump captured). Each step is written to sites.json at most once per session, off
// the UI thread; the Dashboard listens to GettingStartedChanged.
public sealed partial class MainWindow
{
    private readonly HashSet<string> gettingStartedMarked = new(StringComparer.Ordinal);

    // Raised on the UI thread when a step was newly marked.
    public event EventHandler? GettingStartedChanged;

    internal void MarkGettingStarted(string step)
    {
        if (shuttingDown || !GettingStarted.IsKnownStep(step)) return;
        lock (gettingStartedMarked)
        {
            if (!gettingStartedMarked.Add(step)) return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!services.SiteSettings.MarkGettingStartedStep(step)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!shuttingDown) GettingStartedChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                // Try again the next time it happens.
                lock (gettingStartedMarked) gettingStartedMarked.Remove(step);
            }
        });
    }
}
