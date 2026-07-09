#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Calee.Scheduler.Components;

/// <summary>
/// Toolbar for the Calee.Scheduler root component. Renders Today / Previous / Next
/// navigation, a range label, and a view switcher. Implements FR-40, FR-41, and the
/// enabling half of FR-42 (the actual <c>ShowToolbar</c> visibility toggle lives on
/// the root <c>CaleeScheduler&lt;TEvent&gt;</c> component shipped in Task 11).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Non-generic.</strong> The toolbar does not carry a <c>TEvent</c> type
/// parameter — it never touches consumer event objects.
/// </para>
/// <para>
/// <strong>Mode resolution.</strong> Two modes:
/// <list type="bullet">
///   <item><description>
///     <em>Cascaded</em> — when the toolbar is rendered under a
///     <see cref="SchedulerStateContainer"/> cascading value (the root scheduler does
///     this in Task 11). All rendering values are read from the container; clicks
///     route through <see cref="SchedulerStateContainer.RequestDateChange"/> /
///     <see cref="SchedulerStateContainer.RequestViewChange"/>. Direct parameters
///     are ignored (and warned about, once per parameter set, when a logger is
///     available).
///   </description></item>
///   <item><description>
///     <em>Standalone</em> — when no container is present. Direct parameters drive
///     rendering; <see cref="DateChanged"/> / <see cref="ViewChanged"/> fire on
///     user activation. This is the path tested in this task; the cascaded path is
///     exercised end-to-end in Task 11.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Validation.</strong> In standalone mode a null <see cref="TimeZone"/>
/// throws <see cref="ArgumentNullException"/> from <see cref="OnParametersSet"/>
/// (PRD §4.6 hard-fail). In cascaded mode <see cref="TimeZone"/> is supplied via
/// the container — it's the root scheduler's responsibility to enforce.
/// </para>
/// </remarks>
public partial class CaleeSchedulerToolbar : ComponentBase
{
    /// <summary>
    /// Default view-switcher options when neither cascaded nor explicit values are set.
    /// Includes the five unconditionally-renderable views (Day / Week / Month / Year /
    /// Agenda); Timeline is intentionally omitted because a Timeline entry would mislead
    /// a bare-toolbar consumer who has no <c>Lanes</c> / <c>LaneKey</c> wiring to fall
    /// back on. The composed-root path continues to gate Timeline via
    /// <c>CaleeScheduler&lt;TEvent&gt;.TimelineViewAvailable</c> against the lane binding.
    /// </summary>
    private static readonly IReadOnlyList<SchedulerView> DefaultAvailableViews = new[]
    {
        SchedulerView.Day,
        SchedulerView.Week,
        SchedulerView.Month,
        SchedulerView.Year,
        SchedulerView.Agenda,
    };

    /// <summary>
    /// Cascaded state container supplied by the root <c>CaleeScheduler&lt;TEvent&gt;</c>
    /// (Task 11). When non-null the toolbar runs in cascaded mode and ignores its
    /// direct parameters.
    /// </summary>
    [CascadingParameter]
    internal SchedulerStateContainer? State { get; set; }

    /// <summary>
    /// The currently-active view. Honored only in standalone mode. Bindable —
    /// pair with <see cref="ViewChanged"/> via <c>@bind-View</c>.
    /// </summary>
    [Parameter]
    public SchedulerView View { get; set; } = SchedulerView.Week;

    /// <summary>
    /// Fired when the user picks a different view from the switcher (standalone mode).
    /// Bindable via <c>@bind-View</c>.
    /// </summary>
    [Parameter]
    public EventCallback<SchedulerView> ViewChanged { get; set; }

    /// <summary>
    /// The anchor date of the currently-displayed view. Honored only in standalone mode.
    /// Bindable — pair with <see cref="DateChanged"/> via <c>@bind-Date</c>.
    /// </summary>
    [Parameter]
    public DateTimeOffset Date { get; set; }

    /// <summary>
    /// Fired when the user activates Today / Previous / Next (standalone mode).
    /// Bindable via <c>@bind-Date</c>.
    /// </summary>
    [Parameter]
    public EventCallback<DateTimeOffset> DateChanged { get; set; }

    /// <summary>
    /// Time zone used to compute "today" (for the Today button) and as the basis for
    /// month/week boundary math in the range label. Required in standalone mode
    /// (PRD §4.6). In cascaded mode this value is read from
    /// <see cref="SchedulerStateContainer.TimeZone"/>.
    /// </summary>
    [Parameter, EditorRequired]
    public TimeZoneInfo TimeZone { get; set; } = default!;

    /// <summary>
    /// First day of the week (FR-04) used to compute the Week range label's bounds.
    /// </summary>
    [Parameter]
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>
    /// View-switcher entries. When <see langword="null"/> the toolbar shows the default
    /// trio (Day / Week / Month). The root scheduler appends
    /// <see cref="SchedulerView.Timeline"/> to this list when wired with lane
    /// parameters (FR-09c).
    /// </summary>
    [Parameter]
    public IReadOnlyList<SchedulerView>? AvailableViews { get; set; }

    /// <summary>
    /// Current TimelineView time scale; drives the range-label format when
    /// <see cref="View"/> is <see cref="SchedulerView.Timeline"/> in standalone mode.
    /// </summary>
    [Parameter]
    public TimelineScale TimelineScale { get; set; } = TimelineScale.Day;

    /// <summary>
    /// Rolling-window length (in days) for Agenda view; drives the range-label format
    /// and the prev/next stepping when <see cref="View"/> is
    /// <see cref="SchedulerView.Agenda"/> in standalone mode (Phase 2 Task 17). In
    /// cascaded mode this value is read from
    /// <see cref="SchedulerStateContainer.AgendaDays"/>. Defaults to <c>7</c> to match
    /// <c>CaleeSchedulerAgendaView.AgendaDays</c>'s default.
    /// </summary>
    [Parameter]
    public int AgendaDays { get; set; } = 7;

    /// <summary>
    /// Day subset for WorkWeek view; drives the range-label format and the composed
    /// child's rendered columns when <see cref="View"/> is
    /// <see cref="SchedulerView.WorkWeek"/> in standalone mode (issue #7). In cascaded
    /// mode this value is read from <see cref="SchedulerStateContainer.WorkWeekDays"/>.
    /// <see langword="null"/> resolves to Monday–Friday, matching the root scheduler's
    /// <c>WorkWeekDays</c> parameter default.
    /// </summary>
    [Parameter]
    public IReadOnlyList<DayOfWeek>? WorkWeekDays { get; set; }

    /// <summary>
    /// Unmatched HTML attributes splatted onto the outermost toolbar element (FR-53).
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Optional consumer-supplied class applied alongside the library's own classes
    /// on the toolbar root element (FR-54).
    /// </summary>
    [Parameter]
    public string? ToolbarClass { get; set; }

    /// <summary>
    /// Optional consumer content rendered at the start of the toolbar, before the
    /// Today / Previous / Next navigation group. Takes its natural position in the
    /// DOM and tab order (first tab stop in the toolbar when it contains interactive
    /// controls). When <see langword="null"/> no wrapper element is emitted and the
    /// toolbar markup is byte-identical to the no-slot layout. The library owns the
    /// toolbar shell, spacing, and wrap behavior; the consumer owns the injected
    /// content, including its target size (WCAG 2.2 SC 2.5.8) and accessible labels.
    /// </summary>
    [Parameter] public RenderFragment? ToolbarStart { get; set; }

    /// <summary>
    /// Optional consumer content rendered at the end of the toolbar, after the view
    /// switcher. Takes its natural position in the DOM and tab order (last tab stop
    /// in the toolbar when it contains interactive controls). When
    /// <see langword="null"/> no wrapper element is emitted and the toolbar markup is
    /// byte-identical to the no-slot layout. Same ownership split as
    /// <see cref="ToolbarStart"/>.
    /// </summary>
    [Parameter] public RenderFragment? ToolbarEnd { get; set; }

    /// <summary>
    /// Optional logger used to warn about ignored direct parameters when the toolbar
    /// is running in cascaded mode.
    /// </summary>
    [Inject]
    private ILogger<CaleeSchedulerToolbar>? Logger { get; set; }

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        if (State is null)
        {
            // Standalone mode: TimeZone is the toolbar's own contract per PRD §4.6.
            if (TimeZone is null)
            {
                throw new ArgumentNullException(nameof(TimeZone));
            }
        }
        else
        {
            // Cascaded mode: warn once if the consumer is also setting direct params,
            // which would otherwise silently no-op and confuse them.
            WarnIfDirectParamsSetInCascadedMode();
        }
    }

    private void WarnIfDirectParamsSetInCascadedMode()
    {
        // We can only detect "the consumer set this" against the parameter defaults.
        // The four meaningful signals: TimeZone non-null, AvailableViews non-null,
        // DateChanged/ViewChanged having a delegate. (View and Date themselves have
        // defaults that look indistinguishable from "set on purpose" so we don't warn
        // for them.)
        if (TimeZone is not null)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: CaleeSchedulerToolbar.TimeZone was set on a cascaded toolbar; the value is ignored — the root scheduler's TimeZone is used.");
        }
        if (AvailableViews is not null)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: CaleeSchedulerToolbar.AvailableViews was set on a cascaded toolbar; the value is ignored — the root scheduler computes the available views.");
        }
        if (DateChanged.HasDelegate)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: CaleeSchedulerToolbar.DateChanged was wired on a cascaded toolbar; the callback will not fire — use the root scheduler's DateChanged instead.");
        }
        if (ViewChanged.HasDelegate)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: CaleeSchedulerToolbar.ViewChanged was wired on a cascaded toolbar; the callback will not fire — use the root scheduler's OnViewChanged instead.");
        }
    }

    // ----- Effective accessors used by the .razor markup ----------------------------

    /// <summary>The time zone in effect for this render (container's value wins).</summary>
    private TimeZoneInfo EffectiveTimeZone => State?.TimeZone ?? TimeZone;

    /// <summary>The view in effect for this render.</summary>
    private SchedulerView EffectiveView => State?.CurrentView ?? View;

    /// <summary>The timeline time scale in effect for this render.</summary>
    private TimelineScale EffectiveTimelineScale => State?.TimelineScale ?? TimelineScale;

    /// <summary>The Agenda window length in effect for this render.</summary>
    private int EffectiveAgendaDays => State?.AgendaDays ?? AgendaDays;

    /// <summary>The WorkWeek day subset in effect for this render (issue #7).</summary>
    private IReadOnlyList<DayOfWeek>? EffectiveWorkWeekDays => State?.WorkWeekDays ?? WorkWeekDays;

    // ----- Event handlers -----------------------------------------------------------

    /// <summary>Today button: jump to "today" in the configured time zone.</summary>
    private Task HandleTodayClickAsync()
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, EffectiveTimeZone);
        // Snap to date-only at the zone's midnight so the new anchor is unambiguous.
        var dateOnly = today.Date;
        var offset = EffectiveTimeZone.GetUtcOffset(dateOnly);
        var newDate = new DateTimeOffset(dateOnly, offset);
        return DispatchDateChangeAsync(newDate);
    }

    /// <summary>Previous chevron: move one unit backward in the current view.</summary>
    private Task HandlePrevClickAsync()
    {
        var current = State?.CurrentDate ?? Date;
        var newDate = SchedulerViewPrimitives.AdvanceAnchor(
            EffectiveView, current, -1, EffectiveTimeZone, EffectiveTimelineScale, EffectiveAgendaDays);
        return DispatchDateChangeAsync(newDate);
    }

    /// <summary>Next chevron: move one unit forward in the current view.</summary>
    private Task HandleNextClickAsync()
    {
        var current = State?.CurrentDate ?? Date;
        var newDate = SchedulerViewPrimitives.AdvanceAnchor(
            EffectiveView, current, +1, EffectiveTimeZone, EffectiveTimelineScale, EffectiveAgendaDays);
        return DispatchDateChangeAsync(newDate);
    }

    /// <summary>View switcher button click handler.</summary>
    private Task HandleViewChangeAsync(SchedulerView view)
    {
        if (State?.RequestViewChange is not null)
        {
            return State.RequestViewChange(view);
        }
        return ViewChanged.InvokeAsync(view);
    }

    private Task DispatchDateChangeAsync(DateTimeOffset newDate)
    {
        if (State?.RequestDateChange is not null)
        {
            return State.RequestDateChange(newDate);
        }
        return DateChanged.InvokeAsync(newDate);
    }

    // ----- Static label helpers used by the .razor markup ---------------------------

    /// <summary>Aria label for the Previous chevron, varying by current view.</summary>
    private static string PrevAriaLabel(SchedulerView view, TimelineScale scale) =>
        UnitLabel(view, scale) switch
        {
            "day" => "Previous day",
            "week" => "Previous week",
            "month" => "Previous month",
            "year" => "Previous year",
            "agenda" => "Previous agenda window",
            "workweek" => "Previous work week",
            _ => "Previous",
        };

    /// <summary>Aria label for the Next chevron, varying by current view.</summary>
    private static string NextAriaLabel(SchedulerView view, TimelineScale scale) =>
        UnitLabel(view, scale) switch
        {
            "day" => "Next day",
            "week" => "Next week",
            "month" => "Next month",
            "year" => "Next year",
            "agenda" => "Next agenda window",
            "workweek" => "Next work week",
            _ => "Next",
        };

    /// <summary>
    /// Map a view (and timeline scale, when view=Timeline) to the unit-word used in
    /// prev/next aria labels.
    /// </summary>
    private static string UnitLabel(SchedulerView view, TimelineScale scale) => view switch
    {
        SchedulerView.Day => "day",
        SchedulerView.Week => "week",
        SchedulerView.Month => "month",
        SchedulerView.Year => "year",
        // Agenda's unit is the rolling window, not a calendar period; "agenda" reads
        // sensibly in the aria label ("Previous agenda" / "Next agenda").
        SchedulerView.Agenda => "agenda",
        SchedulerView.Timeline => scale switch
        {
            TimelineScale.Day => "day",
            TimelineScale.Week => "week",
            TimelineScale.Month => "month",
            _ => "day",
        },
        // WorkWeek steps ±7 calendar days like Week (issue #7), but gets its own
        // "work week" wording in the aria label so screen-reader users hear which
        // view the Prev/Next buttons are stepping.
        SchedulerView.WorkWeek => "workweek",
        _ => "day",
    };

    /// <summary>Human-readable label for a view-switcher button.</summary>
    private static string ViewLabel(SchedulerView view) => view switch
    {
        SchedulerView.Day => "Day",
        SchedulerView.Week => "Week",
        SchedulerView.Month => "Month",
        SchedulerView.Year => "Year",
        SchedulerView.Agenda => "Agenda",
        SchedulerView.Timeline => "Timeline",
        SchedulerView.WorkWeek => "Work Week",
        _ => view.ToString(),
    };
}
