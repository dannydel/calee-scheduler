namespace Calee.Scheduler.Contracts;

/// <summary>
/// The visual mode for <c>CaleeSchedulerYearView&lt;TEvent&gt;</c> (Phase 2 Task 16,
/// FR-38). Picks between the day-number grid (<see cref="MiniMonths"/>) and the
/// GitHub-contribution-style heatmap (<see cref="Heatmap"/>). Per phase-2-plan §5.3 Q13
/// the per-day event-density count is computed once and shared between the two modes;
/// only the visual rendering differs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Density bucketing.</strong> Both modes use the same four buckets so a
/// heatmap and a mini-month rendering of the same data agree on which days are "busy":
/// <list type="number">
///   <item><description><c>0</c> events — no indicator (mini-months) / empty cell (heatmap).</description></item>
///   <item><description><c>1</c> event — level 1 (lowest opacity dot / lightest tile).</description></item>
///   <item><description><c>2–4</c> events — level 2 (medium opacity / medium tile).</description></item>
///   <item><description><c>5+</c> events — level 3 (full opacity / darkest tile).</description></item>
/// </list>
/// Bucket boundaries are hardcoded in the view; reskinning is done by overriding the
/// <c>--calee-scheduler-density-color</c> CSS custom property (the four levels are derived
/// from one color with built-in opacity steps).
/// </para>
/// </remarks>
public enum YearViewStyle
{
    /// <summary>
    /// Day-number grid: each month renders a Sun–Sat 7-column grid of date numbers with
    /// a small colored dot below the number indicating event density (see the
    /// <see cref="YearViewStyle"/> remarks for the bucketing). The default mode.
    /// </summary>
    MiniMonths,

    /// <summary>
    /// GitHub-contribution-style heatmap: each day is a colored square only (no day
    /// numbers), shaded by the same density buckets documented on <see cref="YearViewStyle"/>.
    /// Useful for a denser at-a-glance overview when individual date numbers aren't
    /// needed.
    /// </summary>
    Heatmap,
}
