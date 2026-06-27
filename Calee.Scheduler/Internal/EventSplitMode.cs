namespace Calee.Scheduler.Internal;

/// <summary>
/// How <see cref="VisibleEventSet{TEvent}"/> handles multi-day timed events.
/// </summary>
internal enum EventSplitMode
{
    /// <summary>
    /// Multi-day timed events are split into per-day chunks at midnight in the
    /// configured time zone. Used by Day and Week views, where each day column
    /// is rendered independently and continues-from/continues-to arrows mark
    /// the cut edges.
    /// </summary>
    PerDay,

    /// <summary>
    /// Multi-day timed events remain as a single chunk spanning all the days
    /// they touch. Used by TimelineView, whose X-axis is continuous time across
    /// days; splitting would visually break the continuous block.
    /// </summary>
    Continuous,
}
