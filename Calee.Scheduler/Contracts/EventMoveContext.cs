namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnEventMoved</c>. The library has already applied the move optimistically
/// when this callback fires; the consumer either accepts (by doing nothing or persisting
/// the change) or rejects by setting <see cref="Cancel"/> to <see langword="true"/>.
/// </summary>
/// <remarks>
/// This is a class — not a record — because <see cref="Cancel"/> is mutated by the
/// consumer's handler and read by the library after the awaited callback completes.
/// Records' value-equality semantics are wrong for an in/out parameter; a class with
/// <c>required init</c> immutables and a single mutable property mirrors the
/// <c>FormClosingEventArgs</c>/<c>MudDialogResult</c> pattern. See ADR-0006.
/// </remarks>
public sealed class EventMoveContext
{
    /// <summary>The event being moved, in its pre-drop form.</summary>
    public required ICalendarEvent Event { get; init; }

    /// <summary>New start instant after the move.</summary>
    public required DateTimeOffset NewStart { get; init; }

    /// <summary>New end instant after the move. Duration is preserved by the library.</summary>
    public required DateTimeOffset NewEnd { get; init; }

    /// <summary>
    /// Target lane identifier when the move originated in <c>CaleeSchedulerTimelineView</c>.
    /// Always populated there — even for same-row, time-only moves — so the consumer can
    /// compare against the event's known lane to detect a reassignment.
    /// <see langword="null"/> for moves originating in Day, Week, or Month views.
    /// See ADR-0011.
    /// </summary>
    public string? NewLaneId { get; init; }

    /// <summary>
    /// Set to <see langword="true"/> in the consumer's handler to reject the move.
    /// The library reads this after the handler's task completes and reverts the
    /// optimistic position on the next render. See ADR-0006.
    /// </summary>
    public bool Cancel { get; set; }
}
