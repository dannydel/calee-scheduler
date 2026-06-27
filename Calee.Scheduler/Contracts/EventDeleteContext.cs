namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnEventDeleted</c>, fired when the user presses the delete keystroke
/// on a focused event chip while <c>AllowDelete=true</c>. The library does NOT render
/// any confirmation prompt or phantom-removed state — the consumer's handler renders
/// any confirmation it wants (modal, drawer, toast — entirely consumer-owned per
/// ADR-0010) and either accepts (by persisting the deletion) or rejects (by setting
/// <see cref="Cancel"/> to <see langword="true"/>).
/// </summary>
/// <remarks>
/// <para>
/// Class (not record) for the same reason as <see cref="EventMoveContext"/>:
/// <see cref="Cancel"/> is mutated by the consumer's handler and read by the library
/// post-await. See ADR-0006.
/// </para>
/// <para>
/// Per ADR-0006 Option A (matching create): deletes do not optimistically pin
/// anything. The library renders no phantom-removed state — the chip remains in the
/// DOM until the consumer's next <c>Events</c> parameter set actually removes it.
/// </para>
/// </remarks>
public sealed class EventDeleteContext
{
    /// <summary>The event the user requested to delete.</summary>
    public required ICalendarEvent Event { get; init; }

    /// <summary>
    /// Set to <see langword="true"/> in the consumer's handler to reject the deletion
    /// (e.g., when a confirmation prompt is dismissed without confirming). The library
    /// reads this after the awaited callback completes; the chip stays in the
    /// selection set and stays rendered. See ADR-0006 / ADR-0010.
    /// </summary>
    public bool Cancel { get; set; }
}
