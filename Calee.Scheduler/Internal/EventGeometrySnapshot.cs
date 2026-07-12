#nullable enable
using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Exact snapshot of the event inputs that affect geometry. Views use it to avoid
/// rebuilding layout when a parent render only changes selection or other visual state,
/// while still detecting in-place mutation of consumer event objects and filter results.
/// </summary>
internal sealed class EventGeometrySnapshot<TEvent> where TEvent : ICalendarEvent
{
    private readonly Entry[] _entries;

    private EventGeometrySnapshot(Entry[] entries) => _entries = entries;

    public static EventGeometrySnapshot<TEvent> Capture(
        IReadOnlyList<TEvent> events,
        Func<TEvent, string?>? laneKey = null)
    {
        var entries = new Entry[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            entries[i] = new Entry(ev, ev.Id, ev.Start, ev.End, ev.IsAllDay, laneKey?.Invoke(ev));
        }
        return new EventGeometrySnapshot<TEvent>(entries);
    }

    public bool Matches(
        IReadOnlyList<TEvent> events,
        Func<TEvent, string?>? laneKey = null)
    {
        if (_entries.Length != events.Count) return false;

        for (var i = 0; i < events.Count; i++)
        {
            var previous = _entries[i];
            var current = events[i];
            if (!ReferenceEquals(previous.Event, current)
                || !string.Equals(previous.Id, current.Id, StringComparison.Ordinal)
                || previous.Start != current.Start
                || previous.End != current.End
                || previous.IsAllDay != current.IsAllDay
                || !string.Equals(previous.LaneId, laneKey?.Invoke(current), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Entry(
        TEvent Event,
        string Id,
        DateTimeOffset Start,
        DateTimeOffset End,
        bool IsAllDay,
        string? LaneId);
}
