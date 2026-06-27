namespace Calee.Scheduler.Contracts;

/// <summary>
/// Horizontal time scale for <c>CaleeSchedulerTimelineView</c>. Controls how much
/// time the timeline grid spans along its horizontal axis.
/// </summary>
/// <remarks>See ADR-0011 for the timeline-view design.</remarks>
public enum TimelineScale
{
    /// <summary>One day on the horizontal axis (hour-level granularity).</summary>
    Day,

    /// <summary>One week on the horizontal axis (day-level granularity).</summary>
    Week,

    /// <summary>One month on the horizontal axis (day-level granularity).</summary>
    Month,
}
