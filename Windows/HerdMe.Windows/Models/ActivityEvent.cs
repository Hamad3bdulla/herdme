namespace HerdMe.Windows.Models;

public enum ActivityEventKind
{
    Mail,
    Dump,
    ServiceStopped,
    ProcessStopped,
    EnvironmentStopped,
    Repair
}

// One entry of the dashboard "This session" timeline. PageTag is where a click goes.
public sealed record ActivityEvent(
    ActivityEventKind Kind,
    string Title,
    string Detail,
    DateTimeOffset At,
    string? PageTag
);

// Newest first, bounded, so a noisy mail loop cannot grow memory.
public sealed class ActivityTimeline
{
    public const int Capacity = 30;
    private readonly LinkedList<ActivityEvent> events = new();
    private readonly object gate = new();

    public void Add(ActivityEvent item)
    {
        lock (gate)
        {
            events.AddFirst(item);
            while (events.Count > Capacity) events.RemoveLast();
        }
    }

    public IReadOnlyList<ActivityEvent> Snapshot(int count)
    {
        lock (gate)
        {
            return events.Take(Math.Max(0, count)).ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (gate) return events.Count;
        }
    }
}
