namespace Calee.Scheduler.Contracts;

/// <summary>
/// The view modes exposed by the root <c>CaleeScheduler</c> component.
/// </summary>
/// <remarks>
/// TimelineView is a first-class view alongside Day, Week, and Month — not a Phase 2
/// add-on. See ADR-0011 (and superseded ADR-0008 for the prior Resource framing).
/// </remarks>
public enum SchedulerView
{
    /// <summary>Single-day time grid.</summary>
    Day,

    /// <summary>Seven-day time grid starting on the configured first day of week.</summary>
    Week,

    /// <summary>Month grid with one cell per day.</summary>
    Month,

    /// <summary>
    /// Year overview: twelve mini-months (or heatmap squares) arranged in a configurable
    /// grid. Per-day event-density indicator only — no per-event layout (Phase 2 Task 16).
    /// </summary>
    Year,

    /// <summary>
    /// Agenda overview: a flat date-grouped list spanning a rolling N-day window from the
    /// anchor. Screen-reader friendly + narrow-viewport friendly — uses <c>role="list"</c>
    /// rather than <c>role="grid"</c> (Phase 2 Task 17).
    /// </summary>
    Agenda,

    /// <summary>Timeline grid: one row per <see cref="ILane"/>, time on the horizontal axis.</summary>
    Timeline,

    /// <summary>
    /// Work-week time grid: the same seven-day engine as <see cref="Week"/>, narrowed to
    /// a configurable day subset — Monday–Friday by default. Composed on the root
    /// scheduler from <c>CaleeSchedulerWeekView&lt;TEvent&gt;</c>'s <c>VisibleDays</c>
    /// parameter (see issue #7, which builds on the engine primitive added for issue #6).
    /// Appended at the end of the enum — additive per the 1.x source-stable promise;
    /// existing switch statements and any serialized <see cref="SchedulerView"/> values
    /// are unaffected by this member's addition.
    /// </summary>
    WorkWeek,
}
