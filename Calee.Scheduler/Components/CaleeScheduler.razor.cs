#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;

namespace Calee.Scheduler.Components;

/// <summary>
/// Root scheduler component composing the toolbar with the currently-active view
/// (Day / Week / Month / Year / Agenda / Timeline). Implements FR-08 (root composition), FR-22
/// (<see cref="OnViewChanged"/>), FR-23 (<see cref="SchedulerComponentBase{TEvent}.OnRangeChanged"/>),
/// FR-31 (bindable <see cref="View"/>), FR-42 (<see cref="ShowToolbar"/>), the
/// timeline-availability rule in FR-09c, and the Phase 2 interaction/power-user
/// parameters inherited from <see cref="SchedulerComponentBase{TEvent}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bindable parameters.</strong> Two parameters use the controllable pattern:
/// <list type="bullet">
///   <item><description><see cref="SchedulerComponentBase{TEvent}.Events"/> is plain — no bindable. </description></item>
///   <item><description><c>Date</c> is bindable via <see cref="SchedulerStatefulComponentBase{TEvent}"/>.</description></item>
///   <item><description><see cref="View"/> is bindable here (FR-31): supply <c>@bind-View</c>
///     for controlled mode; omit to let the root track view state internally seeded from
///     <see cref="CaleeSchedulerOptions.DefaultView"/>.</description></item>
///   <item><description><see cref="TimelineScale"/> is bindable here for the same reasons —
///     consumers wiring the Timeline view often want to react to the toolbar's scale toggle.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Range-event reconciliation.</strong> The root computes its own canonical
/// <see cref="SchedulerRange"/> in <c>OnParametersSet</c> and fires
/// <see cref="SchedulerComponentBase{TEvent}.OnRangeChanged"/> exactly when that range
/// changes. The child views' <c>OnRangeChanged</c> events are intentionally absorbed
/// (wired to a no-op handler) so consumers receive a single range event per render
/// rather than one from the root plus one from the child — and so the source of truth
/// for "what range am I looking at?" is the root's parameter-set logic, not whichever
/// view happens to be active.
/// </para>
/// <para>
/// <strong>Cascading state.</strong> A single <see cref="SchedulerStateContainer"/>
/// instance is allocated in <see cref="OnInitialized"/> and mutated in place on each
/// <see cref="OnParametersSet"/>. That stable instance is supplied to descendants via
/// <c>&lt;CascadingValue Value="_state" IsFixed="false"&gt;</c>. The non-fixed value lets the
/// toolbar re-render when fields it reads change without forcing the entire view subtree
/// to re-render on every unrelated parameter set.
/// </para>
/// <para>
/// <strong>Validation.</strong> Inherits the layered <c>TimeZone</c> resolution
/// (issue #34) from the base — see <see cref="SchedulerComponentBase{TEvent}.ResolveTimeZone"/>.
/// Additionally throws <see cref="InvalidOperationException"/> when <see cref="View"/>
/// resolves to <see cref="SchedulerView.Timeline"/> but the timeline binding is incomplete
/// (PRD §4.6 — the timeline view shape requires both <see cref="Lanes"/> and
/// <see cref="LaneKey"/>).
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeScheduler<TEvent> : SchedulerStatefulComponentBase<TEvent>
    where TEvent : ICalendarEvent
{
    // ----- Bindable View ------------------------------------------------------------

    /// <summary>
    /// Bindable active view. When <see langword="null"/>, the root manages view state
    /// internally seeded from <see cref="CaleeSchedulerOptions.DefaultView"/> per FR-31.
    /// </summary>
    [Parameter]
    public SchedulerView? View { get; set; }

    /// <summary>
    /// Fires when the active view changes — the bindable's setter, used by
    /// <c>@bind-View</c>. See <see cref="OnViewChanged"/> for the general "switching views"
    /// notification (FR-22).
    /// </summary>
    [Parameter]
    public EventCallback<SchedulerView> ViewChanged { get; set; }

    /// <summary>
    /// Fires whenever the user switches views via the toolbar or by setting <see cref="View"/>
    /// externally. This is FR-22's general callback — it fires alongside
    /// <see cref="ViewChanged"/> (which exists solely to satisfy <c>@bind-View</c>).
    /// </summary>
    [Parameter]
    public EventCallback<SchedulerView> OnViewChanged { get; set; }

    // ----- Bindable TimelineScale ------------------------------------------------

    /// <summary>
    /// Bindable horizontal time scale for the Timeline view. When <see langword="null"/>,
    /// the root manages this internally and seeds it to <see cref="TimelineScale.Day"/>.
    /// Ignored when <see cref="EffectiveView"/> is not <see cref="SchedulerView.Timeline"/>.
    /// </summary>
    [Parameter]
    public TimelineScale? TimelineScale { get; set; }

    /// <summary>
    /// Fires when <see cref="TimelineScale"/> changes (the bindable setter). Note: Phase 1
    /// shipped without a scale-switching UI; the bindable shape is preserved so consumers
    /// can own scale controls externally without a contract change.
    /// </summary>
    [Parameter]
    public EventCallback<TimelineScale> TimelineScaleChanged { get; set; }

    /// <summary>
    /// Enables opt-in vertical lane-row virtualization when the Timeline view is active.
    /// Disabled by default for 1.x rendering compatibility.
    /// </summary>
    [Parameter]
    public bool EnableTimelineVirtualization { get; set; }

    // ----- Toolbar control -----------------------------------------------------------

    /// <summary>
    /// Whether to render the toolbar at the top of the scheduler. Defaults to
    /// <see langword="true"/>; set <see langword="false"/> to host the toolbar elsewhere
    /// or replace navigation entirely (FR-42).
    /// </summary>
    [Parameter]
    public bool ShowToolbar { get; set; } = true;

    // ----- View-config defaults (forwarded to active view when applicable) -----------

    /// <summary>
    /// Forwarded to the active view's <c>StartHour</c> parameter when applicable
    /// (Day, Week, Timeline[Day]). When <see langword="null"/>, the child view falls
    /// back to <see cref="CaleeSchedulerOptions.DefaultStartHour"/>.
    /// </summary>
    [Parameter]
    public int? StartHour { get; set; }

    /// <summary>
    /// Forwarded to the active view's <c>EndHour</c> parameter when applicable
    /// (Day, Week, Timeline[Day]). When <see langword="null"/>, the child view falls
    /// back to <see cref="CaleeSchedulerOptions.DefaultEndHour"/>.
    /// </summary>
    [Parameter]
    public int? EndHour { get; set; }

    /// <summary>
    /// Forwarded to the active view's <c>SlotDurationMinutes</c> parameter when applicable
    /// (Day, Week, Timeline[Day]). Must be one of <c>15</c>, <c>30</c>, or <c>60</c>.
    /// When <see langword="null"/>, the child view falls back to
    /// <see cref="CaleeSchedulerOptions.DefaultSlotDurationMinutes"/>.
    /// </summary>
    [Parameter]
    public int? SlotDurationMinutes { get; set; }

    /// <summary>
    /// Forwarded to the active view's <c>FirstDayOfWeek</c> parameter when applicable
    /// (Week, Month, Timeline[Week]). When <see langword="null"/>, the child view falls
    /// back to <see cref="CaleeSchedulerOptions.DefaultFirstDayOfWeek"/>.
    /// </summary>
    [Parameter]
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Opt-in day subset forwarded to the composed Week view's <c>VisibleDays</c>
    /// parameter when <see cref="EffectiveView"/> is <see cref="SchedulerView.WorkWeek"/>
    /// (issue #7). When <see langword="null"/> (the default), resolves to Monday–Friday.
    /// Ignored for every other view — in particular, <see cref="SchedulerView.Week"/>
    /// always renders all seven days from the root; this parameter never forwards to it.
    /// </summary>
    /// <remarks>
    /// Same soft-degradation rule as <c>CaleeSchedulerWeekView.VisibleDays</c> (PRD §4.6):
    /// an empty list, or one whose values match none of the week's seven days, degrades
    /// to "all seven days" — the composed child view logs the warning once; the root's
    /// own range/label computation mirrors the same fallback silently so the two never
    /// disagree about which days are "in view."
    /// </remarks>
    [Parameter]
    public IReadOnlyList<DayOfWeek>? WorkWeekDays { get; set; }

    /// <summary>
    /// Forwarded to time-grid views and the Timeline[Day] view. Defaults to
    /// <see langword="true"/> (FR-07).
    /// </summary>
    [Parameter]
    public bool ShowCurrentTimeIndicator { get; set; } = true;

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerMonthView{TEvent}"/>'s <c>MaxEventsPerDay</c>
    /// when the Month view is active. See FR-18.
    /// </summary>
    [Parameter]
    public int? MaxEventsPerDay { get; set; }

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerTimelineView{TEvent}"/>. Controls whether the
    /// trailing "Unassigned" row renders (FR-09d). Defaults to <see langword="true"/>.
    /// </summary>
    [Parameter]
    public bool ShowUnassignedRow { get; set; } = true;

    // ----- Year + Agenda view config (Phase 2 Task 18) -------------------------------

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerYearView{TEvent}"/> when the Year view is
    /// active. Default <see cref="Contracts.YearViewStyle.MiniMonths"/>; see
    /// <see cref="Contracts.YearViewStyle"/> for the heatmap alternative (FR-38).
    /// </summary>
    [Parameter]
    public YearViewStyle YearStyle { get; set; } = YearViewStyle.MiniMonths;

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerYearView{TEvent}"/> when the Year view is
    /// active. Default <see cref="Contracts.YearViewLayout.Grid4x3"/> (the
    /// "calendar wall" arrangement); see <see cref="Contracts.YearViewLayout"/> for the
    /// alternative grid shapes (FR-38).
    /// </summary>
    [Parameter]
    public YearViewLayout YearLayout { get; set; } = YearViewLayout.Grid4x3;

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerAgendaView{TEvent}"/> when the Agenda view
    /// is active. Default <c>7</c>; clamped to the inclusive range <c>[1, 90]</c> per
    /// <see cref="CaleeSchedulerAgendaView{TEvent}.MaxAgendaDays"/> (FR-39). The
    /// resolved value also drives the toolbar's prev/next stepping when
    /// <see cref="View"/> is <see cref="SchedulerView.Agenda"/> — the toolbar reads the
    /// cascaded <see cref="SchedulerStateContainer.AgendaDays"/> populated here.
    /// </summary>
    /// <remarks>
    /// Clamping matches <c>CaleeSchedulerAgendaView</c>'s own clamp shape — out-of-range
    /// values are silently snapped to <c>1</c> or <c>90</c>; the library does not throw
    /// (PRD §4.6 soft-degradation for non-required parameters). The clamp lives in
    /// <c>OnParametersSet</c> on the root so the cascaded <see cref="SchedulerStateContainer.AgendaDays"/>
    /// carries the clamped value to descendants — keeps the Agenda view's resolved
    /// window length and the toolbar's prev/next step in lockstep.
    /// </remarks>
    [Parameter]
    public int AgendaDays { get; set; } = 7;

    /// <summary>
    /// Enables measured date-group virtualization while the Agenda view is active.
    /// Disabled by default for source- and rendering-compatible 1.x behavior.
    /// </summary>
    [Parameter]
    public bool EnableAgendaVirtualization { get; set; }

    /// <summary>
    /// Whether the Year entry appears in the toolbar's view switcher. Defaults to
    /// <see langword="true"/>; set <see langword="false"/> to hide the Year button
    /// (and keep <c>view.year</c> out of the toolbar UI). The
    /// <see cref="SchedulerCommandIds.ViewYear"/> command and the default <c>4</c>
    /// keystroke still fire when the consumer has not disabled them via
    /// <see cref="SchedulerComponentBase{TEvent}.DisabledShortcuts"/> — the toggle is
    /// strictly about the toolbar's surface (FR-38).
    /// </summary>
    [Parameter]
    public bool ShowYearButton { get; set; } = true;

    /// <summary>
    /// Whether the Agenda entry appears in the toolbar's view switcher. Defaults to
    /// <see langword="true"/>; set <see langword="false"/> to hide the Agenda button.
    /// Same separation-of-concerns as <see cref="ShowYearButton"/> — keystroke +
    /// palette dispatch are independent of the toolbar's visibility (FR-39).
    /// </summary>
    [Parameter]
    public bool ShowAgendaButton { get; set; } = true;

    // ----- Lane binding --------------------------------------------------------------

    /// <summary>
    /// Lanes used when the Timeline view is active. The presence of both this and
    /// <see cref="LaneKey"/> enables the Timeline entry in the toolbar's view switcher
    /// (FR-09c). When omitted, the Timeline view is unavailable from the toolbar; setting
    /// <see cref="View"/> to <see cref="SchedulerView.Timeline"/> without these throws.
    /// </summary>
    [Parameter]
    public IReadOnlyList<ILane>? Lanes { get; set; }

    /// <summary>
    /// Projection from a consumer event to a lane <c>Id</c>. Pair with
    /// <see cref="Lanes"/> to enable the Timeline view.
    /// </summary>
    [Parameter]
    public Func<TEvent, string?>? LaneKey { get; set; }

    // ----- Templates -----------------------------------------------------------------

    /// <summary>
    /// Forwarded to the time-grid views (Day, Week, Timeline). See ADR-0002 for the
    /// library-owned-rectangle / consumer-owned-inside contract.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventTemplate { get; set; }

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerMonthView{TEvent}"/>'s <c>EventChipTemplate</c>.
    /// Distinct from <see cref="EventTemplate"/> because chips in Month view are visually
    /// different from time-grid event blocks.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventChipTemplate { get; set; }

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerAgendaView{TEvent}"/>'s
    /// <see cref="CaleeSchedulerAgendaView{TEvent}.EventRowTemplate"/> when the Agenda
    /// view is active. The agenda row is a list-row shape (no positioning), distinct from
    /// the time-grid event block (<see cref="EventTemplate"/>) and the Month chip
    /// (<see cref="EventChipTemplate"/>) — same TEvent context, different surface. See
    /// the Agenda view's parameter for the row container the consumer fragment renders
    /// inside.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventRowTemplate { get; set; }

    // ----- Class hooks (FR-54) -------------------------------------------------------

    /// <summary>Optional consumer-supplied class applied to the toolbar root (FR-54).</summary>
    [Parameter]
    public string? ToolbarClass { get; set; }

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerToolbar.ToolbarStart"/>. Consumer content
    /// rendered at the start of the toolbar, before the Today / Previous / Next group.
    /// Only rendered when <see cref="ShowToolbar"/> is <see langword="true"/>. See the
    /// toolbar parameter for the DOM/tab-order and ownership contract.
    /// </summary>
    [Parameter] public RenderFragment? ToolbarStart { get; set; }

    /// <summary>
    /// Forwarded to <see cref="CaleeSchedulerToolbar.ToolbarEnd"/>. Consumer content
    /// rendered at the end of the toolbar, after the view switcher. Only rendered when
    /// <see cref="ShowToolbar"/> is <see langword="true"/>. See the toolbar parameter
    /// for the DOM/tab-order and ownership contract.
    /// </summary>
    [Parameter] public RenderFragment? ToolbarEnd { get; set; }

    /// <summary>Optional class hook for day-header cells (Day / Week / Month) (FR-54).</summary>
    [Parameter]
    public string? DayHeaderClass { get; set; }

    /// <summary>Optional class hook for the time-gutter column (Day / Week / Timeline[Day]) (FR-54).</summary>
    [Parameter]
    public string? TimeGutterClass { get; set; }

    /// <summary>Optional class hook for the all-day row (Day / Week / Timeline) (FR-54).</summary>
    [Parameter]
    public string? AllDayRowClass { get; set; }

    /// <summary>Optional class hook for lane row labels (Timeline view only) (FR-54).</summary>
    [Parameter]
    public string? LaneLabelClass { get; set; }

    // (AllowMultiSelect / OnSelectionChanged are inherited from
    // SchedulerComponentBase{TEvent} so every view exposes the same flags. The root
    // scheduler additionally wires its own selection persistence — see
    // HandleRequestSelectionChangeAsync below — so cross-view persistence works
    // (FR-34 / Phase 2 Task 10).)

    // ----- Internal state ------------------------------------------------------------

    /// <summary>
    /// Internal view anchor used when the consumer does not supply <see cref="View"/>.
    /// Exposed to the test project for the controlled-vs-uncontrolled mode contract.
    /// </summary>
    internal SchedulerView _internalView;

    /// <summary>
    /// Internal time-scale anchor used when the consumer does not supply
    /// <see cref="TimelineScale"/>.
    /// </summary>
    internal TimelineScale _internalTimelineScale;

    /// <summary>
    /// The cascading-value carrier. Allocated once in <see cref="OnInitialized"/>,
    /// mutated in place each <see cref="OnParametersSet"/>. Internal so toolbar tests
    /// can assert on the same instance.
    /// </summary>
    private SchedulerStateContainer _state = default!;

    /// <summary>The last range the root fired <see cref="SchedulerComponentBase{TEvent}.OnRangeChanged"/> for (FR-23 dirty flag).</summary>
    private SchedulerRange? _lastRange;

    /// <summary>
    /// Cached input identity for the resolved shortcut map (Phase 2 Task 14). Used by
    /// <see cref="SyncStateContainer"/> to avoid re-parsing the consumer's overrides
    /// on every parameter set when the references haven't changed.
    /// </summary>
    private (IReadOnlyList<string>? Disabled, IReadOnlyList<ShortcutBinding>? Map) _lastShortcutInputs;

    /// <summary>
    /// First-render gate for the root's resolved shortcut map. The cache key uses
    /// reference equality on the input lists, and at construction time both are null —
    /// without an explicit "have we initialized?" flag the dirty check would skip
    /// the first resolve and the cascade would carry <see cref="ResolvedShortcutMap.Empty"/>
    /// on the first render. This flips on the first <see cref="SyncStateContainer"/>.
    /// </summary>
    private bool _shortcutInputsInitialized;

    /// <summary>Internal accessor for the cascading container — used by tests to assert on state propagation.</summary>
    internal SchedulerStateContainer State => _state;

    // ----- Computed accessors --------------------------------------------------------

    /// <summary>The view in effect for this render — supplied value wins over internal state.</summary>
    protected SchedulerView EffectiveView => View ?? _internalView;

    /// <summary>The timeline time scale in effect for this render.</summary>
    protected TimelineScale EffectiveTimelineScale =>
        TimelineScale ?? _internalTimelineScale;

    /// <summary>The first-day-of-week in effect for this render (falls back to options).</summary>
    protected DayOfWeek EffectiveFirstDayOfWeek =>
        FirstDayOfWeek ?? SchedulerOptions.Value.DefaultFirstDayOfWeek;

    /// <summary>True when the consumer wired both <see cref="Lanes"/> and <see cref="LaneKey"/> (FR-09c).</summary>
    protected bool TimelineViewAvailable => Lanes is not null && LaneKey is not null;

    /// <summary>
    /// The set of view-switcher entries surfaced by the toolbar. Always includes
    /// Day / WorkWeek / Week / Month — WorkWeek is unconditionally renderable, the same
    /// as Day/Week/Month (no Timeline-style gate; issue #7). Year and Agenda are
    /// included by default and gated on <see cref="ShowYearButton"/> /
    /// <see cref="ShowAgendaButton"/> respectively (Phase 2 Task 18 — FR-38 / FR-39).
    /// Timeline appears only when both <see cref="Lanes"/> and <see cref="LaneKey"/> are
    /// wired (FR-09c).
    /// </summary>
    /// <remarks>
    /// Built once per <see cref="SyncStateContainer"/> and cached on the cascading
    /// <see cref="SchedulerStateContainer.AvailableViews"/> field. The toolbar's order is
    /// constructed explicitly here — <c>Day, WorkWeek, Week, Month, Year, Agenda,
    /// Timeline</c> — and is intentionally decoupled from the <see cref="SchedulerView"/>
    /// enum's declaration order (WorkWeek is appended at the end of the enum per the 1.x
    /// source-stable promise, but slots in right after Day in the toolbar).
    /// </remarks>
    protected IReadOnlyList<SchedulerView> AvailableViews => BuildAvailableViews();

    private IReadOnlyList<SchedulerView>? _availableViewsCache;
    private (bool ShowYear, bool ShowAgenda, bool TimelineAvailable) _availableViewsKey;

    private IReadOnlyList<SchedulerView> BuildAvailableViews()
    {
        var key = (ShowYearButton, ShowAgendaButton, TimelineViewAvailable);
        if (_availableViewsCache is not null && _availableViewsKey == key)
        {
            return _availableViewsCache;
        }

        // Day / WorkWeek / Week / Month are always present. Year and Agenda follow the
        // per-view visibility toggles; Timeline is gated on the lane binding (FR-09c).
        var list = new List<SchedulerView>(7)
        {
            SchedulerView.Day,
            SchedulerView.WorkWeek,
            SchedulerView.Week,
            SchedulerView.Month,
        };
        if (ShowYearButton) list.Add(SchedulerView.Year);
        if (ShowAgendaButton) list.Add(SchedulerView.Agenda);
        if (TimelineViewAvailable) list.Add(SchedulerView.Timeline);

        _availableViewsCache = list;
        _availableViewsKey = key;
        return _availableViewsCache;
    }

    // ----- Lifecycle -----------------------------------------------------------------

    /// <inheritdoc/>
    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Seed view-state fallbacks from options. The actual cascading container is
        // allocated here once and mutated in place thereafter so its reference identity
        // is stable across renders (see <see cref="SchedulerStateContainer"/> remarks).
        _internalView = SchedulerOptions.Value.DefaultView;
        _internalTimelineScale = Contracts.TimelineScale.Day;
        _state = new SchedulerStateContainer
        {
            RequestViewChange = HandleRequestViewChangeAsync,
            RequestDateChange = HandleRequestDateChangeAsync,
            RequestSelectionChange = HandleRequestSelectionChangeAsync,
        };
    }

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet(); // resolves ResolvedTimeZone (issue #34)

        // PRD §4.6: View=Timeline requires both Lanes and LaneKey to be wired.
        if (EffectiveView == SchedulerView.Timeline && !TimelineViewAvailable)
        {
            throw new InvalidOperationException(
                "CaleeScheduler.View is Timeline but Lanes or LaneKey is null.");
        }

        // Phase 2 Task 18 — clamp AgendaDays to [1, 90] on the root so the cascaded
        // container's AgendaDays carries the resolved value and the toolbar's
        // prev/next step matches what the Agenda view sees. Mirrors
        // CaleeSchedulerAgendaView.OnParametersSet — same shape, single resolved
        // value across the cascade.
        if (AgendaDays < 1)
        {
            _resolvedAgendaDays = 1;
        }
        else if (AgendaDays > CaleeSchedulerAgendaView<TEvent>.MaxAgendaDays)
        {
            _resolvedAgendaDays = CaleeSchedulerAgendaView<TEvent>.MaxAgendaDays;
        }
        else
        {
            _resolvedAgendaDays = AgendaDays;
        }

        // Issue #7 — resolve WorkWeekDays to Monday–Friday when the consumer hasn't
        // overridden it. Mirrors the AgendaDays resolution shape above so the cascaded
        // state, ComputeRange, and the composed Week view's VisibleDays parameter all
        // agree on the same resolved list within a single render.
        _resolvedWorkWeekDays = WorkWeekDays ?? SchedulerViewPrimitives.DefaultWorkWeekDays;

        SyncStateContainer();
    }

    /// <summary>
    /// Post-clamp resolved value of <see cref="AgendaDays"/> after OnParametersSet —
    /// always in <c>[1, 90]</c>. Used for the cascaded value and forwarded to the
    /// Agenda view directly (the view re-clamps for the standalone path; the root's
    /// clamp here keeps the toolbar's prev/next aligned with the rendered window).
    /// </summary>
    private int _resolvedAgendaDays = 7;

    /// <summary>Internal accessor for tests covering the clamp behavior.</summary>
    internal int ResolvedAgendaDays => _resolvedAgendaDays;

    /// <summary>
    /// Post-resolution value of <see cref="WorkWeekDays"/> after <c>OnParametersSet</c> —
    /// never <see langword="null"/>; defaults to Monday–Friday (issue #7). Used for the
    /// cascaded value, <see cref="ComputeRange"/>, and forwarded to the composed Week
    /// view's <c>VisibleDays</c> parameter when <see cref="EffectiveView"/> is
    /// <see cref="SchedulerView.WorkWeek"/>.
    /// </summary>
    private IReadOnlyList<DayOfWeek> _resolvedWorkWeekDays = SchedulerViewPrimitives.DefaultWorkWeekDays;

    /// <summary>Internal accessor for tests covering the WorkWeekDays default/override resolution.</summary>
    internal IReadOnlyList<DayOfWeek> ResolvedWorkWeekDays => _resolvedWorkWeekDays;

    /// <summary>
    /// Recompute the canonical visible range and update every field on the cascading
    /// <see cref="_state"/> container in place. Called from <see cref="OnParametersSet"/>
    /// AND from the toolbar-initiated change handlers (<see cref="HandleRequestViewChangeAsync"/>,
    /// <see cref="HandleRequestDateChangeAsync"/>) because Blazor's <c>StateHasChanged</c>
    /// does NOT re-run <c>OnParametersSet</c> — without an explicit sync after an internal
    /// state mutation, the cascading container's <c>CurrentView</c> / <c>CurrentDate</c> /
    /// <c>CurrentRange</c> stay stale and the toolbar keeps highlighting the previous view.
    /// </summary>
    private void SyncStateContainer()
    {
        // Compute the canonical visible range for the current effective state. This is
        // the source of truth for OnRangeChanged — child view range events are absorbed
        // and re-derived here so consumers receive a single notification per render.
        var range = ComputeRange(EffectiveView, CurrentDate, EffectiveFirstDayOfWeek, ResolvedTimeZone, EffectiveTimelineScale, _resolvedAgendaDays, _resolvedWorkWeekDays);

        // Mutate the cascading container in place. The toolbar reads these values on
        // every render; the stable reference identity prevents wholesale subtree
        // re-renders under <CascadingValue>.
        _state.CurrentView = EffectiveView;
        _state.CurrentDate = CurrentDate;
        _state.CurrentRange = range;
        _state.TimeZone = ResolvedTimeZone;
        _state.FirstDayOfWeek = EffectiveFirstDayOfWeek;
        _state.AvailableViews = AvailableViews;
        _state.TimelineScale = EffectiveView == SchedulerView.Timeline
            ? EffectiveTimelineScale
            : null;
        // Phase 2 Task 18 — propagate the resolved Agenda window length down through
        // the cascade so the toolbar's prev/next stepping and range label match the
        // active Agenda view's window. The view re-clamps; the root's clamp is the
        // single source the toolbar reads.
        _state.AgendaDays = _resolvedAgendaDays;
        // Issue #7 — propagate the resolved WorkWeek day subset down through the cascade
        // so the toolbar's range-label computation matches the composed Week view's
        // VisibleDays parameter.
        _state.WorkWeekDays = _resolvedWorkWeekDays;
        // Propagate the multi-select opt-in to descendants via the cascade. Views read
        // this from the cascaded container (so their per-click handler ignores Ctrl/
        // Shift when the consumer has not opted in — FR-29 fail-closed default).
        _state.AllowMultiSelect = AllowMultiSelect;
        // Phase 2 Task 14 — recompute the resolved shortcut map when the consumer's
        // DisabledShortcuts / ShortcutMap parameters change. Reference equality on the
        // inputs is sufficient: if the consumer rebuilds the lists on every render we
        // pay one re-resolve per render, which is microseconds. The "initialized" gate
        // is necessary because both inputs start null and ReferenceEquals(null, null)
        // is true — without it the first resolve would be skipped and the cascade
        // would carry the empty sentinel on the first render.
        if (!_shortcutInputsInitialized
            || !ReferenceEquals(_lastShortcutInputs.Disabled, DisabledShortcuts)
            || !ReferenceEquals(_lastShortcutInputs.Map, ShortcutMap))
        {
            _lastShortcutInputs = (DisabledShortcuts, ShortcutMap);
            _state.ResolvedShortcuts = ResolvedShortcutMap.Resolve(DisabledShortcuts, ShortcutMap);
            _shortcutInputsInitialized = true;
        }
        // (RequestViewChange / RequestDateChange / RequestSelectionChange were set
        // once in OnInitialized.)

        // FR-23: fire OnRangeChanged exactly when the canonical range changes.
        // SchedulerRange is a record, so reference inequality + record-equality both work;
        // we use `!= range` which exercises record's value-equality.
        if (_lastRange is null || _lastRange != range)
        {
            _lastRange = range;
            _ = OnRangeChanged.InvokeAsync(range);
        }
    }

    /// <summary>
    /// Compute the canonical visible range for the supplied (view, date, firstDayOfWeek, tz,
    /// timelineScale, agendaDays, workWeekDays). Mirrors what the active child view will
    /// internally derive — reusing the shared primitives keeps the root and the child in
    /// lockstep on which days are "in view".
    /// </summary>
    private static SchedulerRange ComputeRange(
        SchedulerView view,
        DateTimeOffset date,
        DayOfWeek firstDayOfWeek,
        TimeZoneInfo tz,
        TimelineScale timelineScale,
        int agendaDays,
        IReadOnlyList<DayOfWeek> workWeekDays)
    {
        switch (view)
        {
            case SchedulerView.Day:
                {
                    var local = date.Date;
                    var start = SchedulerViewPrimitives.MidnightInZone(local, tz);
                    var end = SchedulerViewPrimitives.MidnightInZone(local.AddDays(1), tz);
                    return new SchedulerRange(start, end);
                }
            case SchedulerView.Week:
                {
                    var days = SchedulerViewPrimitives.ComputeWeekDays(date, firstDayOfWeek, tz);
                    return new SchedulerRange(days[0].Start, days[^1].End);
                }
            case SchedulerView.WorkWeek:
                {
                    // Issue #7 — mirrors what CaleeSchedulerWeekView.VisibleDays derives:
                    // first visible day start → last visible day end. Reuses the same
                    // FilterVisibleDays primitive the child's ResolveVisibleWeekDays calls
                    // so root and child never disagree on which days are "in view." Silent
                    // soft-degradation here (no logging) — the composed child already logs
                    // the PRD §4.6 warning once when the subset is empty/no-match.
                    var allDays = SchedulerViewPrimitives.ComputeWeekDays(date, firstDayOfWeek, tz);
                    var filtered = SchedulerViewPrimitives.FilterVisibleDays(allDays, workWeekDays);
                    if (filtered.Count == 0)
                    {
                        filtered = allDays;
                    }
                    return new SchedulerRange(filtered[0].Start, filtered[^1].End);
                }
            case SchedulerView.Month:
                {
                    // Month view's grid is 42 cells anchored at firstDayOfWeek — its range
                    // matches the child's <c>GridStart</c>/<c>GridEndExclusive</c>.
                    var anchor = date.Date;
                    var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
                    var dayOffset = ((int)firstOfMonth.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
                    var gridStartDate = firstOfMonth.AddDays(-dayOffset);
                    var startOffset = tz.GetUtcOffset(gridStartDate);
                    var start = new DateTimeOffset(gridStartDate, startOffset);
                    var endDate = gridStartDate.AddDays(42);
                    var endOffset = tz.GetUtcOffset(endDate);
                    return new SchedulerRange(start, new DateTimeOffset(endDate, endOffset));
                }
            case SchedulerView.Year:
                {
                    // Year view's range is Jan 1 → Jan 1 of next year (matches
                    // CaleeSchedulerYearView.ComputeYearRange).
                    var anchor = date.Date;
                    var first = new DateTime(anchor.Year, 1, 1);
                    var next = new DateTime(anchor.Year + 1, 1, 1);
                    return new SchedulerRange(
                        new DateTimeOffset(first, tz.GetUtcOffset(first)),
                        new DateTimeOffset(next, tz.GetUtcOffset(next)));
                }
            case SchedulerView.Agenda:
                {
                    // Agenda's window is [anchor-midnight, anchor-midnight + agendaDays).
                    // Matches CaleeSchedulerAgendaView.ComputeWindow.
                    var firstDate = date.Date;
                    var startOffset = tz.GetUtcOffset(firstDate);
                    var endDate = firstDate.AddDays(agendaDays);
                    var endOffset = tz.GetUtcOffset(endDate);
                    return new SchedulerRange(
                        new DateTimeOffset(firstDate, startOffset),
                        new DateTimeOffset(endDate, endOffset));
                }
            case SchedulerView.Timeline:
                {
                    switch (timelineScale)
                    {
                        case Contracts.TimelineScale.Day:
                            return ComputeRange(SchedulerView.Day, date, firstDayOfWeek, tz, timelineScale, agendaDays, workWeekDays);
                        case Contracts.TimelineScale.Week:
                            return ComputeRange(SchedulerView.Week, date, firstDayOfWeek, tz, timelineScale, agendaDays, workWeekDays);
                        case Contracts.TimelineScale.Month:
                            {
                                // Timeline[Month] spans the natural calendar month (not the 42-cell
                                // Month-view grid), matching <c>CaleeSchedulerTimelineView.ComputeMonthRange</c>.
                                var (s, e) = SchedulerViewPrimitives.ComputeMonthRange(date, tz);
                                return new SchedulerRange(s, e);
                            }
                        default:
                            return ComputeRange(SchedulerView.Day, date, firstDayOfWeek, tz, timelineScale, agendaDays, workWeekDays);
                    }
                }
            default:
                return ComputeRange(SchedulerView.Day, date, firstDayOfWeek, tz, timelineScale, agendaDays, workWeekDays);
        }
    }

    // ----- Cascade callbacks ---------------------------------------------------------

    /// <summary>
    /// Handle a view-change request from the toolbar (or any descendant). Implements the
    /// controllable pattern: in controlled mode the consumer is expected to push a new
    /// <see cref="View"/> in via <c>@bind-View</c>; in uncontrolled mode the root mutates
    /// its internal anchor. Both modes fire <see cref="ViewChanged"/> and
    /// <see cref="OnViewChanged"/> (FR-22, FR-31).
    /// </summary>
    private async Task HandleRequestViewChangeAsync(SchedulerView newView)
    {
        if (View.HasValue)
        {
            // Controlled mode: defer to the consumer's binding. OnParametersSet will
            // re-fire when the consumer pushes the new View back in, re-syncing _state.
            await ViewChanged.InvokeAsync(newView);
            await OnViewChanged.InvokeAsync(newView);
            return;
        }

        // Uncontrolled mode: own the state, sync the cascading container immediately,
        // then notify and re-render. The explicit SyncStateContainer call is load-bearing —
        // without it, the toolbar reads stale `_state.CurrentView` and keeps the previous
        // view highlighted, because StateHasChanged() doesn't re-run OnParametersSet.
        _internalView = newView;
        SyncStateContainer();
        await ViewChanged.InvokeAsync(newView);
        await OnViewChanged.InvokeAsync(newView);
        StateHasChanged();
    }

    /// <summary>
    /// Handle a date-change request from the toolbar. Delegates to
    /// <see cref="SchedulerStatefulComponentBase{TEvent}.SetCurrentDateAsync"/> on the
    /// base which already handles the controlled/uncontrolled fork.
    /// </summary>
    private async Task HandleRequestDateChangeAsync(DateTimeOffset newDate)
    {
        await SetCurrentDateAsync(newDate);
        // Mirror the view-change handler: sync the cascading container after the base
        // updates internal date state, so the toolbar's range label reflects the new
        // date on the very next render rather than waiting for the next OnParametersSet.
        SyncStateContainer();
    }

    /// <summary>
    /// Absorb child-view <see cref="SchedulerComponentBase{TEvent}.OnRangeChanged"/> events.
    /// The root computes the canonical range itself in <see cref="OnParametersSet"/> and is
    /// responsible for firing the consumer-facing event; double-firing from the child would
    /// cause downstream consumers (e.g., fetch-on-range-change hooks) to issue duplicate
    /// requests on every parameter set. The unused parameter is intentional — do NOT pipe
    /// it through to <see cref="SchedulerComponentBase{TEvent}.OnRangeChanged"/>.
    /// </summary>
    private Task OnChildRangeChangedAsync(SchedulerRange _) => Task.CompletedTask;

    /// <summary>
    /// Hook called when a child view's bindable <c>Date</c> fires from below. Defensive:
    /// the root drives <c>Date</c> downward in normal use, so this path is rarely hit.
    /// Delegating to <see cref="SchedulerStatefulComponentBase{TEvent}.SetCurrentDateAsync"/>
    /// ensures any change initiated by the child still flows through the same controlled/
    /// uncontrolled fork the root uses for its own state.
    /// </summary>
    private Task OnDateChangedFromChildAsync(DateTimeOffset newDate) =>
        SetCurrentDateAsync(newDate);

    /// <summary>
    /// Handle a selection-change request from a descendant view. Updates the cascaded
    /// <see cref="SchedulerStateContainer.Selection"/> in place (its reference identity
    /// stays stable across view swaps, which is what gives selection its cross-view
    /// persistence), re-syncs the cascading container so consumers re-render, and fires
    /// the consumer-visible <see cref="SchedulerComponentBase{TEvent}.OnSelectionChanged"/>
    /// callback exactly once per real change. A no-op replace (same ids in the same
    /// order) suppresses both the re-render and the callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>State-sync discipline.</strong> Selection lives on the cascaded
    /// container so the toolbar and any other descendants reading from the cascade
    /// see the new set on the very next render. <see cref="ComponentBase.StateHasChanged"/>
    /// alone does not re-run <c>OnParametersSet</c> — without the explicit
    /// <see cref="SyncStateContainer"/> call here the toolbar's range label and any
    /// other state-container-derived UI would lag by one render. See the comment in
    /// <see cref="HandleRequestViewChangeAsync"/> for the longer rationale.
    /// </para>
    /// <para>
    /// <strong>Why we fire <c>OnSelectionChanged</c> from the root, not the child.</strong>
    /// The child view computed the new set; if it also fired the callback we'd have
    /// two write sites for the same observable, and the child's TEvent resolution
    /// would diverge from the root's whenever the consumer pushed an updated
    /// <c>Events</c> list mid-handler. One write site, one fire.
    /// </para>
    /// </remarks>
    private async Task HandleRequestSelectionChangeAsync(IReadOnlyList<string> newOrderedIds)
    {
        var changed = _state.Selection.Replace(newOrderedIds);
        if (!changed)
        {
            return;
        }
        SyncStateContainer();
        await FireOnSelectionChangedFromContainerAsync();
        StateHasChanged();
    }

    /// <summary>
    /// Phase 2 Task 15 — route the palette's view-switch Invoke through the same
    /// interception the keystroke path uses (<see cref="HandleChildViewSwitchRequestedAsync"/>),
    /// so the root's uncontrolled-mode auto-flip applies whether the consumer triggers
    /// the switch from the palette or the keyboard.
    /// </summary>
    private protected override void InvokeViewSwitchFromCommand(SchedulerView view)
    {
        _ = HandleChildViewSwitchRequestedAsync(view);
    }

    /// <summary>
    /// Phase 2 Task 15 — route the palette's navigate-today Invoke through the same
    /// interception the keystroke path uses (<see cref="HandleChildTodayRequestedAsync"/>).
    /// </summary>
    private protected override void InvokeTodayFromCommand()
    {
        _ = HandleChildTodayRequestedAsync();
    }

    /// <summary>
    /// Intercept the child view's <see cref="SchedulerComponentBase{TEvent}.OnViewSwitchRequested"/>
    /// fire so the root scheduler can apply its own view-flip behavior (Phase 2
    /// Task 14, FR-36 / ADR-0013): when the consumer has NOT wired the public
    /// <see cref="SchedulerComponentBase{TEvent}.OnViewSwitchRequested"/> callback AND
    /// the root is in uncontrolled <see cref="View"/> mode, the root flips its own
    /// active view to the requested one. When the consumer HAS wired the callback OR
    /// the view-switch target is unavailable (Timeline without lanes), only the
    /// consumer callback fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wired into the child via the cascade in <c>CaleeScheduler.razor</c>; the child
    /// never sees the consumer's callback directly. This keeps the auto-flip behavior
    /// on the root (where the view-state lives) without duplicating the decision
    /// across six child views.
    /// </para>
    /// <para>
    /// <strong>Available-views guard.</strong> Timeline is only switchable when both
    /// <see cref="Lanes"/> and <see cref="LaneKey"/> are wired (FR-09c). Year and
    /// Agenda are always reachable as of Phase 2 Task 18 — the root's @switch in
    /// <c>CaleeScheduler.razor</c> composes both. When the requested view is
    /// unavailable (Timeline without lanes) the root does NOT auto-flip — it would
    /// throw at the OnParametersSet validation step — but the consumer callback still
    /// fires so apps that handle the view externally get the signal.
    /// </para>
    /// </remarks>
    private async Task HandleChildViewSwitchRequestedAsync(SchedulerView requested)
    {
        // Always fire the consumer's callback first so consumer handlers see the intent
        // even if the root can't auto-flip. The HasDelegate check elides the awaitless
        // path when nothing is wired.
        if (OnViewSwitchRequested.HasDelegate)
        {
            await OnViewSwitchRequested.InvokeAsync(requested);
            // Consumer handled the switch — they own the View binding. Skip the auto-flip
            // to avoid double-changing the view (consumer's @bind-View push will arrive
            // on the next render).
            return;
        }

        // Consumer didn't wire the callback. In uncontrolled View mode, flip the root's
        // own active view as long as the target is supported and reachable. The
        // controlled-mode case (consumer supplied `View`) doesn't auto-flip — the
        // consumer's binding is the source of truth and flipping internally would
        // diverge from it on the next render.
        if (View.HasValue)
        {
            return;
        }

        // Guard against switching to a view the root can't actually render. Timeline
        // requires the lane binding; Day / WorkWeek / Week / Month / Year / Agenda are
        // all unconditionally renderable — WorkWeek gets no Timeline-style gate (issue #7).
        if (requested == SchedulerView.Timeline && !TimelineViewAvailable)
        {
            return;
        }
        if (requested != SchedulerView.Day
            && requested != SchedulerView.WorkWeek
            && requested != SchedulerView.Week
            && requested != SchedulerView.Month
            && requested != SchedulerView.Year
            && requested != SchedulerView.Agenda
            && requested != SchedulerView.Timeline)
        {
            // Defensive — unknown enum value. Not reached for the seven declared views.
            return;
        }

        await HandleRequestViewChangeAsync(requested);
    }

    /// <summary>
    /// Intercept the child view's <see cref="SchedulerComponentBase{TEvent}.OnTodayRequested"/>
    /// fire so the root applies the same hybrid behavior as
    /// <see cref="HandleChildViewSwitchRequestedAsync"/>: consumer-wired callback wins;
    /// otherwise the root flips its own anchor to today.
    /// </summary>
    private async Task HandleChildTodayRequestedAsync()
    {
        if (OnTodayRequested.HasDelegate)
        {
            await OnTodayRequested.InvokeAsync();
            return;
        }
        // Uncontrolled-anchor convenience: snap to today in ResolvedTimeZone.
        // Controlled mode (Date.HasValue) defers to the consumer's binding push.
        if (Date.HasValue)
        {
            return;
        }
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolvedTimeZone);
        await HandleRequestDateChangeAsync(today);
    }

    /// <summary>
    /// Fire <see cref="SchedulerComponentBase{TEvent}.OnSelectionChanged"/> resolving
    /// ids from the cascaded <see cref="_state"/>'s Selection rather than the base's
    /// own <c>EffectiveSelection</c>. The root has no cascading parent, so its base
    /// <c>EffectiveSelection</c> returns the empty local-fallback set — this helper
    /// substitutes the right source. Mirrors
    /// <see cref="SchedulerComponentBase{TEvent}.InvokeOnSelectionChangedAsync"/>'s
    /// id→TEvent projection.
    /// </summary>
    private Task FireOnSelectionChangedFromContainerAsync()
    {
        var selection = _state.Selection;
        if (selection.Count == 0)
        {
            return OnSelectionChanged.InvokeAsync(Array.Empty<TEvent>());
        }
        var events = Events;
        if (events is null || events.Count == 0)
        {
            return OnSelectionChanged.InvokeAsync(Array.Empty<TEvent>());
        }
        var lookup = new Dictionary<string, TEvent>(events.Count, StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            lookup[ev.Id] = ev;
        }
        var resolved = new List<TEvent>(selection.Count);
        foreach (var id in selection)
        {
            if (lookup.TryGetValue(id, out var ev))
            {
                resolved.Add(ev);
            }
        }
        return OnSelectionChanged.InvokeAsync(resolved);
    }
}
