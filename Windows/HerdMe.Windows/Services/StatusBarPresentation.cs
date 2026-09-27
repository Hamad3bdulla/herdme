namespace HerdMe.Windows.Services;

// Values match TBPFLAG so they can go straight to ITaskbarList3.SetProgressState.
public enum TaskbarProgressState
{
    None = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8
}

public readonly record struct DownloadItem(string Name, bool Active, long Received, long? Total);

public sealed record DownloadSummary(
    int Active,
    string? Name,
    TaskbarProgressState State,
    ulong Completed,
    ulong Total,
    int Percent
);

// Pure: turns the running downloads into one status bar line and one taskbar progress value.
public static class StatusBarPresentation
{
    public static readonly DownloadSummary Idle = new(0, null, TaskbarProgressState.None, 0, 0, 0);

    public static DownloadSummary Summarize(IEnumerable<DownloadItem> items)
    {
        var active = items.Where(item => item.Active).ToArray();
        if (active.Length == 0) return Idle;
        var name = active.Length == 1 ? active[0].Name : null;
        // One download without a known size makes the whole bar indeterminate; a partial
        // sum would jump backwards when its size arrives.
        if (active.Any(item => item.Total is not > 0))
        {
            return new(active.Length, name, TaskbarProgressState.Indeterminate, 0, 0, 0);
        }
        var total = (ulong)active.Sum(item => item.Total!.Value);
        var completed = (ulong)active.Sum(item => Math.Clamp(item.Received, 0, item.Total!.Value));
        var percent = total == 0 ? 0 : (int)Math.Clamp(completed * 100 / total, 0, 100);
        return new(active.Length, name, TaskbarProgressState.Normal, completed, total, percent);
    }
}
