namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnKeyboardResizeRequested</c>. Fired when the user presses one of the
/// resize keystrokes (default <c>Shift+ArrowUp</c> / <c>Shift+ArrowDown</c>) on a focused
/// event chip. Identifies the focused event and the resize direction so the consumer can
/// show a visual cue or log the action. The library implements the resize logic internally;
/// the consumer doesn't need to track focus.
/// </summary>
/// <remarks>
/// Class (not record) for consistency with <see cref="EventMoveContext"/> and
/// <see cref="EventResizeContext"/>. Immutable payload — no mutable properties.
/// </remarks>
public sealed class KeyboardResizeRequest
{
    /// <summary>The focused event the user is resizing.</summary>
    public required ICalendarEvent Event { get; init; }

    /// <summary>
    /// Resize direction: <c>Extend</c> (Shift+ArrowUp) increases the event's End by one slot;
    /// <c>Shrink</c> (Shift+ArrowDown) decreases the event's End by one slot.
    /// </summary>
    public required KeyboardResizeDirection Direction { get; init; }
}

/// <summary>
/// Direction of a keyboard resize operation.
/// </summary>
public enum KeyboardResizeDirection
{
    /// <summary>Extend the event's End by one slot (Shift+ArrowUp).</summary>
    Extend,

    /// <summary>Shrink the event's End by one slot (Shift+ArrowDown).</summary>
    Shrink,
}
