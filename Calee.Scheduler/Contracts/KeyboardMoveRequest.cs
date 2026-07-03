namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnKeyboardMoveRequested</c>. Fired when the user presses the move-mode
/// binding (default <c>m</c>) on a focused event chip. Identifies the focused event so
/// the consumer can show a visual cue or log the action. The library implements the
/// phantom movement logic internally; the consumer doesn't need to track focus.
/// </summary>
/// <remarks>
/// Class (not record) for consistency with <see cref="EventMoveContext"/> and
/// <see cref="EventResizeContext"/>. Immutable payload — no mutable properties.
/// </remarks>
public sealed class KeyboardMoveRequest
{
    /// <summary>The focused event the user is moving.</summary>
    public required ICalendarEvent Event { get; init; }

    /// <summary>
    /// Current slot index of the event's start time (relative to the view's StartHour).
    /// Useful for consumers who want to show a visual cue at the current position.
    /// </summary>
    public required int CurrentSlotIndex { get; init; }
}
