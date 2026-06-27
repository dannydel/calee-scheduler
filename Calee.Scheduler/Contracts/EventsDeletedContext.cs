namespace Calee.Scheduler.Contracts;

/// <summary>
/// Batch variant of <see cref="EventDeleteContext"/> — fired when the user presses the
/// delete keystroke with a multi-event selection set held (FR-34 batch). The library
/// chooses between <c>OnEventDeleted</c> and <c>OnEventsDeleted</c> based on selection
/// size + focused-chip-in-set membership: when the focused chip is in the selection
/// AND the selection holds two or more events, the batch callback fires with the full
/// set; otherwise the single-event callback fires with just the focused chip (the
/// "focused outside selection" rule prevents surprise deletes of unfocused events).
/// </summary>
/// <typeparam name="TEvent">
/// The consumer's event type — matches the view's generic argument so the batch list
/// arrives strongly typed (consumers iterate without casting). Mirrors
/// <c>OnSelectionChanged</c>'s generic shape; <c>OnEventDeleted</c> (single) stays on
/// <see cref="ICalendarEvent"/> because a single event is trivial to cast and matches
/// the existing move/resize/create context precedent.
/// </typeparam>
/// <remarks>
/// <para>
/// Class (not record) — see <see cref="EventDeleteContext"/>. <see cref="Cancel"/>
/// rejects ALL deletions atomically; there is no partial-success path. Consumers that
/// need finer control should wire their own per-event confirmation and route the
/// batch through individual mutations on their side.
/// </para>
/// <para>
/// Same Option A semantics as <see cref="EventDeleteContext"/> — no optimistic
/// phantom-removed state. On accept (no cancel) the library prunes the deleted ids
/// from the selection set and fires <c>OnSelectionChanged</c> once with the post-
/// delete set; the consumer's next <c>Events</c> parameter set removes the chips
/// from the DOM.
/// </para>
/// </remarks>
public sealed class EventsDeletedContext<TEvent>
    where TEvent : ICalendarEvent
{
    /// <summary>The events the user requested to delete (the held selection set at keystroke time).</summary>
    public required IReadOnlyList<TEvent> Events { get; init; }

    /// <summary>
    /// Set to <see langword="true"/> in the consumer's handler to reject ALL deletions
    /// atomically. The selection set is unchanged on reject. See ADR-0006 / ADR-0010.
    /// </summary>
    public bool Cancel { get; set; }
}
