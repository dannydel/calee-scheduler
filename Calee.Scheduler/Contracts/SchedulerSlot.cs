namespace Calee.Scheduler.Contracts;

/// <summary>
/// A clickable time slot in the grid. Passed to <c>OnSlotClicked</c> and to
/// <see cref="EventCreateContext"/>.
/// </summary>
/// <param name="Start">Slot start instant in the view's configured <c>TimeZone</c>.</param>
/// <param name="End">Slot end instant; <c>End - Start</c> equals the slot duration.</param>
/// <param name="LaneId">
/// Identifier of the lane row the slot belongs to. Always populated when the slot
/// originates from <c>CaleeSchedulerTimelineView</c>; <see langword="null"/> for slots
/// from Day, Week, or Month views. Consumers should check for <see langword="null"/>
/// before routing on lane. See ADR-0011.
/// </param>
public sealed record SchedulerSlot(
    DateTimeOffset Start,
    DateTimeOffset End,
    string? LaneId = null);
