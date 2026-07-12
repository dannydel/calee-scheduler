#nullable enable
namespace Calee.Scheduler.Internal;

using System.Collections.Generic;
using Calee.Scheduler.Contracts;

/// <summary>
/// The frozen-by-construction view of events that the renderers consume each
/// parameter-set. Owns the pipeline: filter (already applied by the caller) →
/// classify as all-day vs timed → split multi-day timed events (per <see cref="EventSplitMode"/>) →
/// build a lookup table from event Id back to the consumer's original TEvent so
/// click handlers can fire with the consumer's authoritative event reference.
/// </summary>
/// <remarks>
/// Day and Week views construct one VisibleEventSet covering their entire visible
/// range (one day or seven days respectively) with <see cref="EventSplitMode.PerDay"/>;
/// they group <see cref="TimedChunks"/> by chunk start date at render time for the
/// per-day engine call. TimelineView constructs one VisibleEventSet per lane row
/// with <see cref="EventSplitMode.Continuous"/>; multi-day chunks span the visible
/// X-axis as single blocks. Month view does its own preprocessing — its output shape
/// (per-cell chips + cross-cell bar segments) doesn't fit this seam.
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
internal sealed class VisibleEventSet<TEvent> where TEvent : ICalendarEvent
{
    /// <summary>
    /// An empty placeholder — zero events, sentinel range bounds. Use as a field initializer
    /// in view components so the non-nullable field has a value before <c>OnParametersSet</c>
    /// builds the real one, without leaking <c>DateTimeOffset.MinValue</c>/<c>MaxValue</c>
    /// and <c>TimeZoneInfo.Utc</c> magic constants into the consumer.
    /// </summary>
    public static VisibleEventSet<TEvent> Empty { get; } = new(
        Array.Empty<TEvent>(),
        DateTimeOffset.MinValue,
        DateTimeOffset.MaxValue,
        TimeZoneInfo.Utc,
        EventSplitMode.PerDay);

    private readonly Dictionary<string, TEvent> _lookup;

    /// <summary>
    /// Build the pre-processed view of <paramref name="events"/> for the supplied
    /// half-open range, time zone, and split mode.
    /// </summary>
    /// <param name="events">Consumer events to classify. Filtering is assumed already done by the caller.</param>
    /// <param name="rangeStart">Inclusive start of the visible range.</param>
    /// <param name="rangeEndExclusive">Exclusive end of the visible range.</param>
    /// <param name="timeZone">Grid time zone — used to compute per-day midnight boundaries in <see cref="EventSplitMode.PerDay"/> mode.</param>
    /// <param name="splitMode">How multi-day timed events should be handled.</param>
    public VisibleEventSet(
        IEnumerable<TEvent> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        TimeZoneInfo timeZone,
        EventSplitMode splitMode)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(timeZone);

        var allDay = new List<TEvent>();
        var timedChunks = new List<EventChunk<TEvent>>();
        _lookup = new Dictionary<string, TEvent>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (!seenIds.Add(ev.Id))
            {
                throw new ArgumentException(
                    $"Events contains duplicate event Id '{ev.Id}'. Event Id values must be unique within the rendered event set.",
                    nameof(events));
            }

            if (ev.End <= rangeStart || ev.Start >= rangeEndExclusive)
            {
                continue;
            }

            _lookup.Add(ev.Id, ev);

            if (ev.IsAllDay)
            {
                allDay.Add(ev);
                continue;
            }

            if (splitMode == EventSplitMode.Continuous)
            {
                AddContinuousChunk(ev, rangeStart, rangeEndExclusive, timedChunks);
            }
            else
            {
                AddPerDayChunks(ev, rangeStart, rangeEndExclusive, timeZone, timedChunks);
            }
        }

        // Determinism: sort timed chunks by chunk start ascending. Matches the input
        // ordering the engine wants (sweep-line walks events left-to-right).
        timedChunks.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        AllDay = allDay;
        TimedChunks = timedChunks;
    }

    /// <summary>All-day events touching the visible range, in input order.</summary>
    public IReadOnlyList<TEvent> AllDay { get; }

    /// <summary>
    /// Timed-event chunks visible in the range. PerDay: multi-day events are pre-split.
    /// Continuous: multi-day events stay as one chunk clipped to the range bounds.
    /// Sorted by chunk Start ascending.
    /// </summary>
    public IReadOnlyList<EventChunk<TEvent>> TimedChunks { get; }

    /// <summary>
    /// Look up the original consumer event by its <see cref="ICalendarEvent.Id"/>.
    /// Multi-day events have one entry regardless of how many chunks they were split into.
    /// Returns null if no event with that Id is in this set.
    /// </summary>
    public TEvent? FindById(string id) =>
        _lookup.TryGetValue(id, out var ev) ? ev : default;

    /// <summary>
    /// Produce ONE chunk covering the visible portion of <paramref name="ev"/> (Continuous mode).
    /// The chunk's Start/End are clipped to the range bounds; clip flags reflect whether the
    /// original event extended past either edge of the visible range.
    /// </summary>
    private static void AddContinuousChunk(
        TEvent ev,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        List<EventChunk<TEvent>> outChunks)
    {
        var chunkStart = ev.Start < rangeStart ? rangeStart : ev.Start;
        var chunkEnd = ev.End > rangeEndExclusive ? rangeEndExclusive : ev.End;
        var clippedStart = ev.Start < rangeStart;
        var clippedEnd = ev.End > rangeEndExclusive;
        outChunks.Add(new EventChunk<TEvent>(ev, chunkStart, chunkEnd, clippedStart, clippedEnd));
    }

    /// <summary>
    /// Produce one chunk per day-window (in the grid time zone) the event touches within the
    /// visible range (PerDay mode). A zero-duration event (Start == End) produces exactly one
    /// chunk at that instant with no clip flags.
    /// </summary>
    private static void AddPerDayChunks(
        TEvent ev,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        TimeZoneInfo timeZone,
        List<EventChunk<TEvent>> outChunks)
    {
        // Zero-duration events: emit one zero-width chunk at the event's instant, no clip flags.
        if (ev.End == ev.Start)
        {
            outChunks.Add(new EventChunk<TEvent>(ev, ev.Start, ev.End,
                ClippedAtTimeStart: false, ClippedAtTimeEnd: false));
            return;
        }

        // Walk day-by-day from the chunk's first visible day to its last visible day.
        // Day boundaries are computed in the configured time zone so DST/zone transitions
        // are honored — the same approach SchedulerViewPrimitives.ComputeWeekDays uses.

        // Start day: the local midnight in `timeZone` that contains max(ev.Start, rangeStart).
        var effectiveStart = ev.Start < rangeStart ? rangeStart : ev.Start;
        var effectiveEnd = ev.End > rangeEndExclusive ? rangeEndExclusive : ev.End;

        var firstDay = ToLocalDate(effectiveStart, timeZone);
        var lastDayInclusive = ToLocalDate(effectiveEnd.AddTicks(-1), timeZone);

        var day = firstDay;
        while (day <= lastDayInclusive)
        {
            var dayStart = SchedulerViewPrimitives.MidnightInZone(day, timeZone);
            var dayEnd = SchedulerViewPrimitives.MidnightInZone(day.AddDays(1), timeZone);

            // Intersect the event's full span (not just the effective span) with the day,
            // so clip flags reflect whether the original event extends past the day edge.
            if (ev.End <= dayStart || ev.Start >= dayEnd)
            {
                day = day.AddDays(1);
                continue;
            }

            var chunkStart = ev.Start < dayStart ? dayStart : ev.Start;
            var chunkEnd = ev.End > dayEnd ? dayEnd : ev.End;
            var clippedStart = ev.Start < dayStart;
            var clippedEnd = ev.End > dayEnd;

            outChunks.Add(new EventChunk<TEvent>(ev, chunkStart, chunkEnd, clippedStart, clippedEnd));
            day = day.AddDays(1);
        }
    }

    /// <summary>Get the local-to-<paramref name="tz"/> calendar date that contains <paramref name="instant"/>.</summary>
    private static DateTime ToLocalDate(DateTimeOffset instant, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(instant, tz).Date;

}
