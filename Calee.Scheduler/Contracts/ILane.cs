namespace Calee.Scheduler.Contracts;

/// <summary>
/// Identity of a row in <c>CaleeSchedulerTimelineView</c> — a driver, vehicle, room,
/// project, status, or any other schedulable axis the consumer groups events by.
/// Used only by TimelineView; <see cref="ICalendarEvent"/> is deliberately not
/// widened to know about lanes.
/// </summary>
/// <remarks>
/// Consumers bind events to lanes via the <c>LaneKey</c> projection on the view,
/// not via a property on the event itself. This keeps the event contract minimal and
/// lets the same event type drive multiple lane axes if needed. See ADR-0011.
/// </remarks>
public interface ILane
{
    /// <summary>Stable identifier matched against the value returned by the view's <c>LaneKey</c>.</summary>
    string Id { get; }

    /// <summary>Display name shown in the lane row header.</summary>
    string Name { get; }

    /// <summary>
    /// Optional CSS color used to tint the lane row and its events when the event
    /// itself does not specify a color. <see langword="null"/> falls back to the theme default.
    /// </summary>
    string? Color { get; }
}
