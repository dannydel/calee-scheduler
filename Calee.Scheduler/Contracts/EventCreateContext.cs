namespace Calee.Scheduler.Contracts;

/// <summary>
/// Payload for <c>OnEventCreated</c>, fired when the user drags out a new event in
/// an empty slot. The library renders the in-progress ghost; on drop it fires this
/// callback. The consumer typically opens an editor and either persists a real event
/// or sets <see cref="Cancel"/> to <see langword="true"/>.
/// </summary>
/// <remarks>
/// Class (not record) for the same reason as <see cref="EventMoveContext"/>: <see cref="Cancel"/>
/// is an in/out flag read post-await. See ADR-0006.
/// </remarks>
public sealed class EventCreateContext
{
    /// <summary>
    /// The slot the user dragged out. Carries <c>LaneId</c> when the create
    /// originated in <c>CaleeSchedulerTimelineView</c>; <see langword="null"/> otherwise.
    /// </summary>
    public required SchedulerSlot Slot { get; init; }

    /// <summary>
    /// Set to <see langword="true"/> in the consumer's handler to discard the proposed
    /// creation (e.g., when the editor is dismissed without saving). See ADR-0006.
    /// </summary>
    public bool Cancel { get; set; }
}
