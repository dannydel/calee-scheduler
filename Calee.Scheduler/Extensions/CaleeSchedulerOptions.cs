using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Extensions;

/// <summary>
/// Service-level defaults for Calee.Scheduler views. Registered via
/// <see cref="ServiceCollectionExtensions.AddCaleeScheduler"/> and injected as
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> wherever the
/// library needs to fall back to a global default when a component parameter is omitted.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DefaultTimeZone"/> is the last rung of the grid time zone's layered
/// resolution (issue #34). The grid time zone (used to compute "today", day
/// boundaries, and the offset stamped on emitted <see cref="SchedulerSlot"/>
/// values — PRD §4.3 / ADR-0001) is resolved per component in this precedence,
/// first non-null wins:
/// <list type="number">
///   <item><description>The component's own explicit <c>TimeZone</c> parameter.</description></item>
///   <item><description>An ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c>.</description></item>
///   <item><description><see cref="DefaultTimeZone"/> on this options type.</description></item>
/// </list>
/// When none of the three supply a value, the component throws
/// <see cref="InvalidOperationException"/> naming all three options rather than
/// silently falling back to <see cref="TimeZoneInfo.Local"/> or
/// <see cref="TimeZoneInfo.Utc"/> — a silent local/UTC fallback is exactly the
/// footgun this layered design avoids: a consumer that scaffolded against the
/// developer's machine local time could otherwise ship to production against UTC
/// server time, with "today" highlights and slot offsets disagreeing page-by-page
/// and no error to surface the drift.
/// </para>
/// <para>
/// Hard-fail validation (see <see cref="ServiceCollectionExtensions.AddCaleeScheduler"/>)
/// enforces the same contract violations the views themselves enforce per PRD §4.6:
/// <c>StartHour &gt; EndHour</c>, <c>StartHour &lt; 0</c>, <c>EndHour &gt; 24</c>,
/// <c>SlotDurationMinutes</c> not in <c>{15, 30, 60}</c>, and <c>MaxEventsPerDay &lt; 1</c>
/// all raise <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
/// on first <c>IOptions&lt;CaleeSchedulerOptions&gt;.Value</c> access.
/// </para>
/// </remarks>
public sealed class CaleeSchedulerOptions
{
    /// <summary>
    /// Default <see cref="SchedulerView"/> for the root <c>CaleeScheduler</c> component
    /// when the consumer does not supply a <c>View</c> parameter. Defaults to
    /// <see cref="SchedulerView.Week"/>.
    /// </summary>
    public SchedulerView DefaultView { get; set; } = SchedulerView.Week;

    /// <summary>
    /// Default first visible hour (0–23) for Day, Week, and TimelineView (TimeScale=Day).
    /// Must satisfy <c>0 &lt;= DefaultStartHour &lt;= DefaultEndHour &lt;= 24</c>.
    /// Defaults to <c>8</c>.
    /// </summary>
    public int DefaultStartHour { get; set; } = 8;

    /// <summary>
    /// Default last visible hour (1–24, exclusive ceiling) for Day, Week, and
    /// TimelineView (TimeScale=Day). Must satisfy
    /// <c>0 &lt;= DefaultStartHour &lt;= DefaultEndHour &lt;= 24</c>. Defaults to <c>18</c>.
    /// </summary>
    public int DefaultEndHour { get; set; } = 18;

    /// <summary>
    /// Default slot duration in minutes for time-grid views. Must be one of
    /// <c>15</c>, <c>30</c>, or <c>60</c> (PRD §4.6). Defaults to <c>30</c>.
    /// </summary>
    public int DefaultSlotDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Default first day of the week for the Week and Month views. Defaults to
    /// <see cref="System.DayOfWeek.Sunday"/>.
    /// </summary>
    public DayOfWeek DefaultFirstDayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>
    /// Default cap on the number of event chips rendered per cell in the Month view
    /// before the "+N more" overflow chip takes over. Must be <c>&gt;= 1</c>.
    /// Defaults to <c>3</c>.
    /// </summary>
    public int DefaultMaxEventsPerDay { get; set; } = 3;

    /// <summary>
    /// Default cap on the number of side-by-side overlap columns rendered in the
    /// time-grid views (Day, Week, TimelineView) before the surplus collapses into a
    /// "+N" overlap block in the reserved last column. Must be <c>&gt;= 2</c>.
    /// Defaults to <c>3</c>.
    /// </summary>
    public int DefaultMaxOverlapColumns { get; set; } = 3;

    /// <summary>
    /// Default duration (in minutes) for events proposed by the double-click-to-create
    /// affordance (FR-32). When <see langword="null"/> (the default), the duration
    /// resolves per view:
    /// <list type="bullet">
    ///   <item><description>Day / Week views and <c>CaleeSchedulerTimelineView</c> at
    ///   <c>TimelineScale.Day</c> use one <c>SlotDurationMinutes</c>.</description></item>
    ///   <item><description>Month view and <c>CaleeSchedulerTimelineView</c> at
    ///   <c>TimelineScale.Week</c> / <c>TimelineScale.Month</c> use <c>1440</c>
    ///   (one day).</description></item>
    /// </list>
    /// When set to a non-null value, that value applies across every view — the
    /// consumer's explicit choice wins.
    /// </summary>
    /// <remarks>
    /// Drag-to-create (FR-24) does <em>not</em> consult this option — the user picks
    /// the duration explicitly by dragging out a region. The option exists solely so
    /// double-click-to-create has a sensible default to lean on.
    /// </remarks>
    public int? DefaultCreateDurationMinutes { get; set; }

    /// <summary>
    /// Service-level fallback grid time zone, consulted last in the layered
    /// <c>TimeZone</c> resolution (issue #34): explicit <c>TimeZone</c> parameter →
    /// ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c> → this option → throw. Defaults
    /// to <see langword="null"/>, meaning "no service-level default" — a consumer that
    /// relies solely on this option must set it explicitly; there is no implicit
    /// <see cref="TimeZoneInfo.Local"/> or <see cref="TimeZoneInfo.Utc"/> substitution.
    /// </summary>
    public TimeZoneInfo? DefaultTimeZone { get; set; }
}
