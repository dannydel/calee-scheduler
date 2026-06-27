namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnEventResized</c>. Only the trailing edge of the event moves —
/// the leading edge (<c>Start</c>) is unchanged — which is why this context carries
/// <see cref="NewEnd"/> but no <c>NewStart</c>. The library has already applied the
/// resize optimistically when this callback fires; the consumer rejects by setting
/// <see cref="Cancel"/> to <see langword="true"/>.
/// </summary>
/// <remarks>
/// Class (not record) for the same reason as <see cref="EventMoveContext"/>: <see cref="Cancel"/>
/// is an in/out flag read after the awaited handler completes. See ADR-0006.
/// </remarks>
public sealed class EventResizeContext
{
    /// <summary>The event being resized, in its pre-drop form.</summary>
    public required ICalendarEvent Event { get; init; }

    /// <summary>
    /// New end instant after the resize. Only the trailing edge moves — start is unchanged,
    /// which is why there is no <c>NewStart</c> field.
    /// </summary>
    public required DateTimeOffset NewEnd { get; init; }

    /// <summary>
    /// Set to <see langword="true"/> in the consumer's handler to reject the resize.
    /// See ADR-0006.
    /// </summary>
    public bool Cancel { get; set; }
}
