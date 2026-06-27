namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload passed to <c>OnDayOverflowClicked</c> when the user activates an
/// overflow chip ("+N more", "+N earlier", "+N later", or an overlap block).
/// </summary>
/// <typeparam name="TEvent">The consumer's calendar event type.</typeparam>
/// <param name="Date">The day the chip belongs to, in the view's configured <c>TimeZone</c>.</param>
/// <param name="Kind">Which overflow chip was clicked — see <see cref="OverflowKind"/>.</param>
/// <param name="Events">
/// The collapsed/overflowed events represented by the chip. Populated by Day/Week/Timeline
/// views for <see cref="OverflowKind.Earlier"/>, <see cref="OverflowKind.Later"/>, and
/// (in Tasks 4-6) <see cref="OverflowKind.Overlap"/>. Month view passes an empty list
/// (the consumer re-derives the day's events from <paramref name="Date"/>).
/// </param>
/// <param name="RegionStart">
/// Set only for <see cref="OverflowKind.Overlap"/> — the start of the time-grid region
/// where the overflowing events overlap. Null for Earlier/Later/Month chips.
/// </param>
/// <param name="RegionEnd">
/// Set only for <see cref="OverflowKind.Overlap"/> — the end of the time-grid region
/// where the overflowing events overlap. Null for Earlier/Later/Month chips.
/// </param>
/// <param name="LaneId">
/// Set when the click originated in TimelineView (always populated there — overflow chips
/// in TimelineView are per-row, so the row's lane is the disambiguator). Null when the
/// click came from Day/Week/Month views, whose overflow chips are global to the day. Same
/// pattern as <see cref="EventMoveContext.NewLaneId"/>. See ADR-0011.
/// </param>
public sealed record DayOverflowContext<TEvent>(
    DateOnly Date,
    OverflowKind Kind,
    IReadOnlyList<TEvent> Events,
    DateTimeOffset? RegionStart = null,
    DateTimeOffset? RegionEnd = null,
    string? LaneId = null)
    where TEvent : ICalendarEvent;
