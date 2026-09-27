namespace HerdMe.Windows.Services;

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Cancelled
}

// One row of the shared operations list as the tracker sees it. Outcome is null while running.
public sealed record OperationObservation(string Id, string Name, bool Active, OperationOutcome? Outcome, string? Error);

public sealed record FinishedOperation(string Id, string Name, OperationOutcome Outcome, TimeSpan Duration, string? Error);

// When a long install, download or project creation ends while HerdMe is minimized or behind
// another window, the taskbar button flashes and a notification says how it went. Short
// operations and ones the user cancelled stay quiet.
public static class OperationFinishAlert
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(10);

    public static bool ShouldAlert(FinishedOperation operation, bool windowInForeground) =>
        !windowInForeground
            && operation.Outcome != OperationOutcome.Cancelled
            && operation.Duration >= MinimumDuration;

    public static AppNotification Notification(FinishedOperation operation, NotificationAction? primary)
    {
        var failed = operation.Outcome == OperationOutcome.Failed;
        var error = Summary(operation.Error);
        return new AppNotification(
            "finished:" + operation.Id,
            failed
                ? ServiceText.Get("NotificationOperationFailedTitle", "Could not finish")
                : ServiceText.Get("NotificationOperationSucceededTitle", "Finished"),
            failed
                ? error.Length > 0
                    ? ServiceText.Format("NotificationOperationFailedWithError", "{0} failed: {1}", operation.Name, error)
                    : ServiceText.Format("NotificationOperationFailedMessage", "{0} failed.", operation.Name)
                : ServiceText.Format(
                    "NotificationOperationSucceededMessage",
                    "{0} finished in {1}.",
                    operation.Name,
                    Duration(operation.Duration)
                ),
            primary
        );
    }

    // "1:05" or "12 s": short enough for a notification line.
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalMinutes < 1) return $"{(int)duration.TotalSeconds} s";
        return duration.TotalHours < 1
            ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
            : $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string Summary(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return string.Empty;
        var line = error.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return line.Length <= 160 ? line : line[..157] + "...";
    }
}

// Turns successive snapshots of the operations list into "this one just finished" events.
// Operations only ever seen finished (old history rows) are ignored; a retried operation
// that becomes active again is timed from the retry.
public sealed class OperationFinishTracker
{
    private readonly Dictionary<string, DateTimeOffset> started = new(StringComparer.OrdinalIgnoreCase);

    public int Tracking => started.Count;

    public IReadOnlyList<FinishedOperation> Observe(IEnumerable<OperationObservation> snapshot, DateTimeOffset now)
    {
        var finished = new List<FinishedOperation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in snapshot)
        {
            seen.Add(operation.Id);
            if (operation.Active)
            {
                started.TryAdd(operation.Id, now);
                continue;
            }
            if (!started.Remove(operation.Id, out var start)) continue;
            finished.Add(new FinishedOperation(
                operation.Id,
                operation.Name,
                operation.Outcome ?? OperationOutcome.Succeeded,
                now - start,
                operation.Error
            ));
        }
        // Rows cleared from the list while running are forgotten, not reported.
        foreach (var id in started.Keys.Where(id => !seen.Contains(id)).ToArray()) started.Remove(id);
        return finished;
    }
}
