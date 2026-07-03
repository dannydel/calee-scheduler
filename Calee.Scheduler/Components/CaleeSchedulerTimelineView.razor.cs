#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Timeline view for Calee.Scheduler (FR-09c). Renders rows = lanes,
/// X-axis = time, with three selectable horizontal time scales: <see cref="TimelineScale.Day"/>,
/// <see cref="TimelineScale.Week"/>, and <see cref="TimelineScale.Month"/>.
/// Reuses <see cref="EventLayoutEngine"/> with horizontal-time interpretation of the
/// engine's direction-agnostic <c>TimeStartPercent</c> / <c>TimeSpanPercent</c> fields
/// (PRD §4.4).
/// </summary>
/// <remarks>
/// <para>
/// Implements FR-09c, FR-09d, FR-09e,
/// FR-13 (via <see cref="EventLayoutEngine"/>, horizontal interpretation), FR-16,
/// FR-17, FR-19, FR-19a (Day mode), FR-19b, FR-20, FR-21, FR-23, FR-30 (Timeline
/// portion), FR-31, FR-32, FR-33, FR-53, FR-54, FR-55, NFR-04, NFR-06
/// (Timeline portion), NFR-08.
/// </para>
/// <para>
/// Parameter validation follows PRD §4.6: null <see cref="Lanes"/> or
/// <see cref="LaneKey"/> hard-fail with <see cref="ArgumentNullException"/>;
/// invalid hours/slot duration (TimeScale=Day) hard-fail with
/// <see cref="ArgumentException"/>; duplicate <see cref="ILane.Id"/> values
/// soft-degrade with an <see cref="ILogger"/> warning.
/// </para>
/// <para>
/// Multi-day timed events render as a single continuous block in TimeScale=Week
/// and TimeScale=Month (FR-09e); the X-axis is already continuous time across days,
/// so no per-day split is needed. All-day events render in the per-lane banner
/// strip on the row label, not in the time grid (FR-09e).
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerTimelineView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// Lanes whose events should be grouped into rows, in the supplied order.
    /// Required (PRD §4.6); null hard-fails. An empty list is acceptable — the view
    /// renders the toolbar + only the unassigned row (if enabled).
    /// </summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<ILane> Lanes { get; set; } = default!;

    /// <summary>
    /// Projection from a consumer event to a lane <c>Id</c>. Required (PRD §4.6).
    /// Returning <see langword="null"/> (or an Id not in <see cref="Lanes"/>) places
    /// the event in the unassigned row (FR-09d).
    /// </summary>
    [Parameter, EditorRequired]
    public Func<TEvent, string?> LaneKey { get; set; } = default!;

    /// <summary>
    /// Horizontal time scale. Controls how wide the visible range is along the X axis
    /// and how tick labels are formatted. Defaults to <see cref="TimelineScale.Day"/>.
    /// </summary>
    [Parameter]
    public TimelineScale TimeScale { get; set; } = TimelineScale.Day;

    /// <summary>
    /// First visible hour of the time area (0..24, inclusive). Applies only when
    /// <see cref="TimeScale"/> is <see cref="TimelineScale.Day"/>; defaults from
    /// <c>SchedulerOptions.Value.DefaultStartHour</c> when null.
    /// </summary>
    [Parameter]
    public int? StartHour { get; set; }

    /// <summary>
    /// Last visible hour of the time area (0..24, exclusive ceiling). Applies only when
    /// <see cref="TimeScale"/> is <see cref="TimelineScale.Day"/>; defaults from
    /// <c>SchedulerOptions.Value.DefaultEndHour</c> when null.
    /// </summary>
    [Parameter]
    public int? EndHour { get; set; }

    /// <summary>
    /// Slot duration in minutes; applies only when <see cref="TimeScale"/> is
    /// <see cref="TimelineScale.Day"/>. Must be one of <c>15</c>, <c>30</c>, or
    /// <c>60</c>. Defaults from <c>SchedulerOptions.Value.DefaultSlotDurationMinutes</c>.
    /// </summary>
    [Parameter]
    public int? SlotDurationMinutes { get; set; }

    /// <summary>
    /// First day of the visible week; applies only when <see cref="TimeScale"/> is
    /// <see cref="TimelineScale.Week"/>. Defaults from
    /// <c>SchedulerOptions.Value.DefaultFirstDayOfWeek</c>.
    /// </summary>
    [Parameter]
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Whether to render the unassigned row (FR-09d) at the bottom of the view.
    /// Defaults to <see langword="true"/>. The row is also auto-hidden when there are
    /// no unassigned events in the visible range, even when this is true.
    /// </summary>
    [Parameter]
    public bool ShowUnassignedRow { get; set; } = true;

    /// <summary>
    /// Whether to render a vertical current-time indicator across all lane rows
    /// when today (in <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>) is in
    /// range. Only applies when <see cref="TimeScale"/> is <see cref="TimelineScale.Day"/>
    /// (FR-07). Defaults to <see langword="true"/>.
    /// </summary>
    [Parameter]
    public bool ShowCurrentTimeIndicator { get; set; } = true;

    /// <summary>
    /// Max side-by-side overlap stacks within a lane before surplus events collapse into a
    /// "+N" block. Defaults to <c>SchedulerOptions.Value.DefaultMaxOverlapColumns</c>. Must be &gt;= 2.
    /// </summary>
    [Parameter]
    public int? MaxOverlapColumns { get; set; }

    /// <summary>
    /// Optional render fragment for the *inside* of each timed event block (FR-17).
    /// See ADR-0002 for the library-owned-rectangle / consumer-owned-inside contract.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventTemplate { get; set; }

    /// <summary>Optional class hook applied to each lane row's label area (FR-54).</summary>
    [Parameter]
    public string? LaneLabelClass { get; set; }

    /// <summary>Optional class hook applied to the time-axis tick row (FR-54).</summary>
    [Parameter]
    public string? TimeGutterClass { get; set; }

    /// <summary>Optional class hook applied to each lane's all-day banner strip (FR-54).</summary>
    [Parameter]
    public string? AllDayRowClass { get; set; }

    /// <summary>Injected JS runtime, used for the FR-09b horizontal scroll helper and Escape blur.</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // ----- Resolved parameters after OnParametersSet ------------------------------------

    private int _resolvedStartHour;
    private int _resolvedEndHour;
    private int _resolvedSlotMinutes;
    private DayOfWeek _resolvedFirstDayOfWeek;
    private int _resolvedMaxOverlapColumns;

    // The view's visible range (X-axis bounds).
    private DateTimeOffset _rangeStart;
    private DateTimeOffset _rangeEndExclusive;

    // Per-day bounds for tick rendering (TimeScale=Week → 7 entries; Month → calendar days
    // in the current month; Day → 1 entry).
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> _dayBounds =
        Array.Empty<(DateTimeOffset, DateTimeOffset)>();

    // Per-row layout (parallel to the row list, including the trailing unassigned row when shown).
    private RowLayout[] _rows = Array.Empty<RowLayout>();

    // Per-row VisibleEventSets own their own Id→TEvent lookups (see RowLayout.Set);
    // click handlers route through TypedFor, which walks rows. Lanes are bounded and
    // small enough (typically &lt; 50) that scanning row sets on each click is cheaper
    // than maintaining a parallel aggregate dictionary on every parameter set.

    // Roving-tabindex anchor for the (lane × time) grid.
    private int _focusedRowIndex;
    private int _focusedTimeIndex;

    // Keyboard move mode state (issue #20 — SC 2.5.7)
    private bool _keyboardMoveMode;
    private string? _keyboardMoveEventId;
    private int _keyboardMovePhantomTimeOffset;
    private int _keyboardMovePhantomLaneOffset;
    private DateTimeOffset _keyboardMoveOriginalStart;
    private DateTimeOffset _keyboardMoveOriginalEnd;
    private string? _keyboardMoveOriginalLaneId;

    // For FR-23.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private ElementReference _timeAreaScrollContainer;
    private bool _scrollPending;
    private IJSObjectReference? _jsModule;

    // Issue #19 — set by HandleGridKeyDownAsync when an arrow key moves the roving
    // tabindex; consumed in OnAfterRenderAsync (after the tabindex swap has actually
    // rendered) to move real browser focus onto the newly-active slot/day cell.
    private bool _focusMovePending;

    /// <summary>
    /// Per-event element refs the drag layer uses as the ghost source (Phase 2 Task 6 — FR-25).
    /// Keyed by event id so a chip whose row position changes between renders (a drag that
    /// projects an event into a new lane re-buckets it; cross-lane drag is the canonical
    /// case) still resolves to its captured DOM element. The previous array-indexed-by-
    /// position pattern mis-aligned ids and refs when Blazor's diff reused a <c>@key</c>-
    /// matched chip at a new row/slot without re-firing the <c>@ref</c> capture.
    /// </summary>
    private readonly Dictionary<string, ElementReference> _eventRefsByEventId = new(StringComparer.Ordinal);

    /// <summary>
    /// Optimistic pins for in-flight or just-completed drag-to-move operations
    /// (ADR-0006). Keyed by the consumer event's authoritative id. Unlike Day/Week
    /// the pin shape carries a third field — <c>LaneId</c> — because TimelineView's
    /// drag can change which lane row the event sits on (FR-25 cross-lane semantics).
    /// A <see langword="null"/> <c>LaneId</c> means the pinned target is the unassigned
    /// row. Cleared in <see cref="OnParametersSet"/> when the consumer's authoritative
    /// times AND <see cref="LaneKey"/>-projected lane id all match the pin.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, string? LaneId)> _optimisticPin =
        new(StringComparer.Ordinal);

    /// <summary>Inclusive start of the visible range along the X axis.</summary>
    internal DateTimeOffset RangeStart => _rangeStart;

    /// <summary>Exclusive end of the visible range along the X axis.</summary>
    internal DateTimeOffset RangeEndExclusive => _rangeEndExclusive;

    /// <summary>The set of lane rows to render (excludes the unassigned row).</summary>
    internal IReadOnlyList<ILane> LaneRows => Lanes;

    /// <summary>Per-day bounds backing the X-axis ticks (Week=7, Month=days-in-month, Day=1).</summary>
    internal IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> DayBounds => _dayBounds;

    /// <summary>Number of rows including the unassigned row (when visible).</summary>
    internal int TotalRowCount => _rows.Length;

    /// <summary>
    /// Accessible name for an hour-grid slot at (rowIndex, slotIdx) in TimeScale=Day:
    /// "&lt;lane&gt;, &lt;time&gt;, empty slot." Used by screen readers when the cell is
    /// focused.
    /// </summary>
    internal string SlotAccessibleName(int rowIndex, int slotIdx)
    {
        var row = _rows[rowIndex];
        var minutes = slotIdx * _resolvedSlotMinutes;
        var time = _rangeStart.Date.AddHours(_resolvedStartHour).AddMinutes(minutes);
        return $"{row.LaneName}, {time:h:mm tt}, empty slot";
    }

    /// <summary>
    /// Accessible name for a day-cell at (rowIndex, dayIdx) in TimeScale=Week or Month:
    /// "&lt;lane&gt;, &lt;weekday&gt;, &lt;date&gt;, empty cell."
    /// </summary>
    internal string DayCellAccessibleName(int rowIndex, int dayIdx)
    {
        var row = _rows[rowIndex];
        var day = _dayBounds[dayIdx].Start;
        return $"{row.LaneName}, {day:dddd, MMM d}, empty cell";
    }

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Hard-fail per PRD §4.6 — required parameters.
        if (Lanes is null)
        {
            throw new ArgumentNullException(nameof(Lanes));
        }
        if (LaneKey is null)
        {
            throw new ArgumentNullException(nameof(LaneKey));
        }

        var opts = SchedulerOptions.Value;
        _resolvedStartHour = StartHour ?? opts.DefaultStartHour;
        _resolvedEndHour = EndHour ?? opts.DefaultEndHour;
        _resolvedSlotMinutes = SlotDurationMinutes ?? opts.DefaultSlotDurationMinutes;
        _resolvedFirstDayOfWeek = FirstDayOfWeek ?? opts.DefaultFirstDayOfWeek;
        _resolvedMaxOverlapColumns = MaxOverlapColumns ?? opts.DefaultMaxOverlapColumns;

        // Hour/slot validation only applies in TimeScale=Day. Week/Month skip the floor/ceiling.
        if (TimeScale == TimelineScale.Day)
        {
            SchedulerViewPrimitives.ValidateHourParameters(
                _resolvedStartHour, _resolvedEndHour, _resolvedSlotMinutes);
        }

        // Warn about duplicate Lane Ids (soft-degradation per PRD §4.6).
        if (Lanes.Count > 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Lanes.Count; i++)
            {
                if (!seen.Add(Lanes[i].Id))
                {
                    Logger?.LogWarning(
                        "Calee.Scheduler: duplicate Lane.Id '{LaneId}' detected; rendering both rows. (PRD §4.6 soft-degradation)",
                        Lanes[i].Id);
                }
            }
        }

        // Compute visible range per TimeScale.
        switch (TimeScale)
        {
            case TimelineScale.Day:
                {
                    var localDate = CurrentDate.Date;
                    var offset = TimeZone.GetUtcOffset(localDate);
                    _rangeStart = new DateTimeOffset(localDate, offset);
                    _rangeEndExclusive = _rangeStart.AddDays(1);
                    _dayBounds = new[] { (_rangeStart, _rangeEndExclusive) };
                    break;
                }
            case TimelineScale.Week:
                {
                    _dayBounds = SchedulerViewPrimitives.ComputeWeekDays(
                        CurrentDate, _resolvedFirstDayOfWeek, TimeZone);
                    _rangeStart = _dayBounds[0].Start;
                    _rangeEndExclusive = _dayBounds[^1].End;
                    break;
                }
            case TimelineScale.Month:
                {
                    var (start, end) = SchedulerViewPrimitives.ComputeMonthRange(CurrentDate, TimeZone);
                    _rangeStart = start;
                    _rangeEndExclusive = end;
                    _dayBounds = SchedulerViewPrimitives.ComputeDayBounds(start, end, TimeZone);
                    break;
                }
            default:
                throw new ArgumentException(
                    $"Unknown TimeScale value: {TimeScale}.", nameof(TimeScale));
        }

        // Optimistic-pin housekeeping (ADR-0006). Drop pins the consumer has caught up
        // on — i.e., the consumer's authoritative Start/End AND projected lane id now
        // match the pin. Performed before layout so the engine sees only still-relevant
        // pins.
        ClearAcknowledgedPins();

        ComputeLayout();

        // FR-23.
        if (_lastRangeStart != _rangeStart || _lastRangeEnd != _rangeEndExclusive)
        {
            _lastRangeStart = _rangeStart;
            _lastRangeEnd = _rangeEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(_rangeStart, _rangeEndExclusive));
        }
    }

    /// <summary>
    /// Build the per-row layout: bucket events by lane (or to the unassigned bucket),
    /// then hand each bucket to a per-row <see cref="VisibleEventSet{TEvent}"/> using
    /// <see cref="EventSplitMode.Continuous"/> (the X-axis is continuous time across days,
    /// so multi-day timed events stay as a single block per FR-09e). The engine runs once
    /// per row using horizontal-time interpretation.
    /// </summary>
    private void ComputeLayout()
    {
        var filtered = GetFilteredEvents();

        // Bucket events by lane Id. Events whose LaneKey is null or unknown go to
        // the unassigned bucket.
        var laneIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Lanes.Count; i++)
        {
            laneIds.Add(Lanes[i].Id);
        }

        var eventsByLane = new Dictionary<string, List<TEvent>>(StringComparer.Ordinal);
        var unassignedEvents = new List<TEvent>();

        foreach (var ev in filtered)
        {
            // Honor optimistic pin's LaneId when present (ADR-0006 + FR-25 cross-lane).
            // Otherwise fall back to the consumer-supplied LaneKey projection.
            // Short-circuiting at the grouping site is the cleanest seam — we don't have
            // to fake a lane-keyed wrapper around TEvent; just override the lookup here.
            string? key;
            if (_optimisticPin.TryGetValue(ev.Id, out var pin))
            {
                key = pin.LaneId;
            }
            else
            {
                key = LaneKey(ev);
            }

            if (key is null || !laneIds.Contains(key))
            {
                unassignedEvents.Add(ev);
            }
            else
            {
                if (!eventsByLane.TryGetValue(key, out var list))
                {
                    list = new List<TEvent>();
                    eventsByLane[key] = list;
                }
                list.Add(ev);
            }
        }

        var rowList = new List<RowLayout>(Lanes.Count + 1);
        var engine = new EventLayoutEngine();
        int? startHour = TimeScale == TimelineScale.Day ? _resolvedStartHour : null;
        int? endHour = TimeScale == TimelineScale.Day ? _resolvedEndHour : null;

        for (var i = 0; i < Lanes.Count; i++)
        {
            var lane = Lanes[i];
            eventsByLane.TryGetValue(lane.Id, out var rowEvents);
            rowList.Add(BuildRow(
                rowEvents ?? (IReadOnlyList<TEvent>)Array.Empty<TEvent>(),
                lane.Id, lane.Name, lane.Color, isUnassigned: false,
                engine, startHour, endHour));
        }

        // Unassigned row, when needed.
        if (ShowUnassignedRow && unassignedEvents.Count > 0)
        {
            rowList.Add(BuildRow(
                unassignedEvents,
                laneId: null, laneName: "Unassigned", laneColor: null,
                isUnassigned: true,
                engine, startHour, endHour));
        }

        _rows = rowList.ToArray();

        // Clamp focus to current row count.
        if (_focusedRowIndex >= _rows.Length) _focusedRowIndex = Math.Max(0, _rows.Length - 1);
    }

    /// <summary>
    /// Build a single lane row by handing the row's bucketed events to a
    /// <see cref="VisibleEventSet{TEvent}"/> with <see cref="EventSplitMode.Continuous"/>,
    /// then running the layout engine over the resulting timed chunks.
    /// </summary>
    private RowLayout BuildRow(
        IReadOnlyList<TEvent> rowEvents,
        string? laneId,
        string laneName,
        string? laneColor,
        bool isUnassigned,
        EventLayoutEngine engine,
        int? startHour,
        int? endHour)
    {
        var rowSet = new VisibleEventSet<TEvent>(
            rowEvents, _rangeStart, _rangeEndExclusive, TimeZone, EventSplitMode.Continuous);

        // Apply any optimistic-pin Start/End overrides (ADR-0006) before layout. The grouping
        // pass above already routed pinned events into the right row's bucket via the pin's
        // LaneId; this in-place chunk rewrite handles the time-axis component of the pin.
        // Continuous mode produces one chunk per event, so a simple per-chunk substitute
        // matches the Day-view pattern (no Week-view chunk-collapse needed).
        IReadOnlyList<EventChunk<TEvent>> chunksForLayout = rowSet.TimedChunks;
        if (_optimisticPin.Count > 0)
        {
            chunksForLayout = ApplyOptimisticPinTimes(rowSet.TimedChunks);
        }

        // Continuous mode produces one chunk per event covering its visible span (FR-09e).
        // IReadOnlyList&lt;EventChunk&lt;TEvent&gt;&gt; flows into the engine via IReadOnlyList covariance.
        var layout = engine.Layout(
            chunksForLayout,
            _rangeStart, _rangeEndExclusive, startHour, endHour, _resolvedMaxOverlapColumns);

        return new RowLayout(
            LaneId: laneId,
            LaneName: laneName,
            LaneColor: laneColor,
            Set: rowSet,
            AllDay: rowSet.AllDay,
            Layout: layout,
            IsUnassigned: isUnassigned);
    }

    /// <summary>
    /// Return a copy of <paramref name="chunks"/> with any pinned events' Start/End replaced
    /// by their pin values. Non-pinned chunks pass through unchanged. The returned list is
    /// freshly allocated only when at least one chunk is rewritten. Mirrors Day view's
    /// per-chunk in-place rewrite — TimelineView's <see cref="EventSplitMode.Continuous"/>
    /// guarantees one chunk per event, so no per-day collapse step is needed.
    /// </summary>
    private IReadOnlyList<EventChunk<TEvent>> ApplyOptimisticPinTimes(IReadOnlyList<EventChunk<TEvent>> chunks)
    {
        List<EventChunk<TEvent>>? rebuilt = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (_optimisticPin.TryGetValue(c.Id, out var pinned))
            {
                rebuilt ??= new List<EventChunk<TEvent>>(chunks);
                rebuilt[i] = c with
                {
                    Start = pinned.Start,
                    End = pinned.End,
                    ClippedAtTimeStart = false,
                    ClippedAtTimeEnd = false,
                };
            }
        }
        return rebuilt is null ? chunks : rebuilt;
    }

    /// <summary>
    /// Drop pin entries whose pinned <c>(Start, End, LaneId)</c> all match the consumer-supplied
    /// authoritative state. The lane check uses <see cref="LaneKey"/> for the authoritative
    /// projection — pins are only dropped when the consumer has reflected both the time AND
    /// the lane assignment back. Pins that aren't yet acknowledged stay (the optimistic state
    /// is still the more up-to-date view of the world).
    /// </summary>
    private void ClearAcknowledgedPins()
    {
        if (_optimisticPin.Count == 0) return;
        var events = Events;
        if (events is null || LaneKey is null) return;

        // Build the lane-id set once so we can normalize "unknown lane id" → null for
        // pin matching (so a pin with LaneId=null matches an event whose LaneKey returns
        // an id absent from Lanes — both visibly route to the unassigned row).
        var laneIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Lanes.Count; i++) laneIds.Add(Lanes[i].Id);

        List<string>? toRemove = null;
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (!_optimisticPin.TryGetValue(ev.Id, out var pin)) continue;
            if (pin.Start != ev.Start || pin.End != ev.End) continue;
            var projected = LaneKey(ev);
            // Normalize: an event whose projected lane id isn't in Lanes is visually
            // identical to "unassigned" (null). The pin matches in either case.
            var projectedNormalized = (projected is not null && laneIds.Contains(projected)) ? projected : null;
            if (pin.LaneId != projectedNormalized) continue;

            toRemove ??= new List<string>();
            toRemove.Add(ev.Id);
        }
        if (toRemove is null) return;
        foreach (var id in toRemove)
        {
            _optimisticPin.Remove(id);
        }
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);
            if (TimeScale == TimelineScale.Day) _scrollPending = true;
        }

        if (_scrollPending && _jsModule is not null && TimeScale == TimelineScale.Day)
        {
            _scrollPending = false;
            try
            {
                var hourOffset = SchedulerViewPrimitives.ComputeInitialScrollHourOffset(
                    Today, _rangeStart, _rangeEndExclusive, _resolvedStartHour, _resolvedEndHour);
                await _jsModule.InvokeVoidAsync("scrollToHourHorizontal", _timeAreaScrollContainer, hourOffset);
            }
            catch (JSException) { /* Non-fatal; no JS environment. */ }
            catch (InvalidOperationException) { /* No JS runtime in tests. */ }
        }

        // Issue #19 — move real browser focus onto the newly-active slot/day cell after
        // an arrow-key roving move. Deferred to here so the query runs after the
        // tabindex swap has rendered to the DOM. Applies regardless of TimeScale (unlike
        // the scroll-into-view block above, which is Day-scale only).
        if (_focusMovePending && _jsModule is not null)
        {
            _focusMovePending = false;
            await SchedulerViewPrimitives.TryFocusActiveGridCellAsync(_jsModule, _timeAreaScrollContainer);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_jsModule is not null)
        {
            try { await _jsModule.DisposeAsync(); }
            catch (JSDisconnectedException) { /* Circuit gone. */ }
            catch (JSException) { /* Best-effort cleanup. */ }
        }
        await base.DisposeAsync();
    }

    // ----- Internal accessors used by the .razor markup -------------------------------

    /// <summary>Number of slots within the visible Day range (only used for TimeScale=Day).</summary>
    internal int SlotCount =>
        TimeScale == TimelineScale.Day
            ? (_resolvedEndHour - _resolvedStartHour) * 60 / Math.Max(1, _resolvedSlotMinutes)
            : 0;

    /// <summary>Visible minutes in the visible band (only used for TimeScale=Day).</summary>
    internal int VisibleMinutes =>
        TimeScale == TimelineScale.Day
            ? (_resolvedEndHour - _resolvedStartHour) * 60
            : 0;

    /// <summary>Hour labels for the X-axis tick row in TimeScale=Day.</summary>
    internal IEnumerable<int> HourLabels()
    {
        if (TimeScale != TimelineScale.Day) yield break;
        for (var h = _resolvedStartHour; h < _resolvedEndHour; h++)
        {
            yield return h;
        }
    }

    /// <summary>Format an hour-of-day for the X-axis tick row (TimeScale=Day).</summary>
    internal static string FormatHour(int hour) => SchedulerViewPrimitives.FormatHour(hour);

    /// <summary>Convert hour-of-day into a percentage of the visible Day band's X axis.</summary>
    internal double HourToPercent(int hour) =>
        VisibleMinutes <= 0 ? 0 : (hour - _resolvedStartHour) * 60.0 / VisibleMinutes * 100.0;

    /// <summary>X-position percentage for the supplied day-index in Week/Month modes.</summary>
    internal double DayLeftPercent(int dayIndex) =>
        _dayBounds.Count == 0 ? 0 : (double)dayIndex / _dayBounds.Count * 100.0;

    /// <summary>Width percentage of a single day-tick in Week/Month modes.</summary>
    internal double DayWidthPercent() =>
        _dayBounds.Count == 0 ? 0 : 100.0 / _dayBounds.Count;

    /// <summary>The row list, including the trailing unassigned row when present.</summary>
    internal IReadOnlyList<RowLayout> Rows => _rows;

    /// <summary>The row at the supplied index — exposed for the .razor markup.</summary>
    internal RowLayout Row(int index) => _rows[index];

    /// <summary>True when today (in TimeZone) falls inside the visible Day range.</summary>
    internal bool IsTodayInDayRange =>
        TimeScale == TimelineScale.Day &&
        Today.Date == CurrentDate.Date &&
        ShowCurrentTimeIndicator;

    /// <summary>Left percentage for the current-time indicator on the Day-mode time area.</summary>
    internal double CurrentTimeIndicatorPercent
    {
        get
        {
            if (TimeScale != TimelineScale.Day) return -1;
            var now = Today;
            var minutesIntoBand = (now.Hour - _resolvedStartHour) * 60 + now.Minute;
            if (minutesIntoBand < 0 || minutesIntoBand > VisibleMinutes) return -1;
            return VisibleMinutes == 0 ? 0 : (double)minutesIntoBand / VisibleMinutes * 100.0;
        }
    }

    /// <summary>True if any row has earlier-overflow events (Day mode only).</summary>
    internal bool ShowEarlierChipFor(int rowIndex) =>
        TimeScale == TimelineScale.Day && _rows[rowIndex].Layout.EarlierOverflow.Count > 0;

    /// <summary>True if any row has later-overflow events (Day mode only).</summary>
    internal bool ShowLaterChipFor(int rowIndex) =>
        TimeScale == TimelineScale.Day && _rows[rowIndex].Layout.LaterOverflow.Count > 0;

    /// <summary>Format an event's start/end as an accessible time range in TimeZone.</summary>
    internal string FormatEventTimeRange(ICalendarEvent ev) =>
        SchedulerViewPrimitives.FormatEventTimeRange(ev, TimeZone);

    /// <summary>Return the consumer's original TEvent for a positioned event (unwraps chunks via Id).
    /// Walks the per-row VisibleEventSets; lanes are bounded and small.</summary>
    internal TEvent? TypedFor(ICalendarEvent ev)
    {
        foreach (var row in _rows)
        {
            var typed = row.Set.FindById(ev.Id);
            if (typed is not null) return typed;
        }
        return default;
    }

    /// <summary>Map overflow chunks back to the consumer's TEvent (deduped by id), dropping any that no longer resolve.</summary>
    private IReadOnlyList<TEvent> MapToTyped(IReadOnlyList<ICalendarEvent> events)
    {
        if (events.Count == 0) return Array.Empty<TEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TEvent>(events.Count);
        foreach (var e in events)
        {
            if (!seen.Add(e.Id)) continue;
            var typed = TypedFor(e);
            if (typed is not null) result.Add(typed);
        }
        return result;
    }

    /// <summary>True when the positioned event has a left-edge clip (either source).</summary>
    internal bool ClippedLeft(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtStart(pe);

    /// <summary>True when the positioned event has a right-edge clip (either source).</summary>
    internal bool ClippedRight(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtEnd(pe);

    /// <summary>Unwrap an <see cref="EventChunk{TEvent}"/> back to its underlying consumer event for formatting.</summary>
    internal ICalendarEvent UnwrapForFormatting(ICalendarEvent ev) =>
        ev is EventChunk<TEvent> c ? c.Event : ev;

    /// <summary>Return the consumer-supplied CSS class for an event (via base helper).</summary>
    internal string? ClassFor(TEvent ev) => GetEventClass(ev);

    // ----- Event handlers -----------------------------------------------------------

    /// <summary>Fire OnEventClicked with the original TEvent reference and update the selection.</summary>
    /// <remarks>
    /// Selection mutation happens only on real pointer clicks (<paramref name="args"/>
    /// non-null) — the Phase 1 keyboard Enter path is unchanged; Task 11 wires the
    /// selection-keyboard surface. Render order for Shift+click range select walks
    /// the rows top-to-bottom, and within each row: all-day banner-strip events first
    /// (lane label area), then the timed events in <c>Layout.Positioned</c> order
    /// (left-to-right by start, then top-to-bottom by stack). This matches DOM order.
    /// Row order is stable across Day / Week / Month timeline scales — the row list
    /// is keyed by <see cref="ILane.Id"/>, not by visual position; consumers re-
    /// bucketing a selected event into a different lane (Task 10 reviewer rec #3)
    /// leaves the selection set unchanged.
    /// </remarks>
    internal Task HandleEventClickAsync(ICalendarEvent ev, MouseEventArgs? args = null)
    {
        var typed = TypedFor(ev);
        if (typed is null)
        {
            return Task.CompletedTask;
        }
        return DispatchClickAsync(typed, ev.Id, args);
    }

    private async Task DispatchClickAsync(TEvent typed, string clickedId, MouseEventArgs? args)
    {
        if (args is not null)
        {
            var ctrlOrMeta = args.CtrlKey || args.MetaKey;
            var shift = args.ShiftKey;
            var renderOrder = ComputeRenderOrderIds();
            var changed = await ApplyClickSelectionAsync(clickedId, ctrlOrMeta, shift, renderOrder);
            // Standalone path needs its own re-render; cascade path is owned by the
            // root's HandleRequestSelectionChangeAsync (see SchedulerComponentBase.IsStandalone).
            if (changed && IsStandalone)
            {
                StateHasChanged();
            }
        }
        await OnEventClicked.InvokeAsync(typed);
    }

    private IReadOnlyList<string> ComputeRenderOrderIds()
    {
        // Walk rows top→bottom; per row, all-day events first then timed events.
        var ids = new List<string>(16);
        for (var r = 0; r < _rows.Length; r++)
        {
            var row = _rows[r];
            for (var i = 0; i < row.AllDay.Count; i++) ids.Add(row.AllDay[i].Id);
            for (var i = 0; i < row.Layout.Positioned.Count; i++)
            {
                ids.Add(row.Layout.Positioned[i].Event.Id);
            }
        }
        return ids;
    }

    /// <summary>Fire OnSlotClicked for the supplied row + time-index (TimeScale=Day).
    /// After the callback resolves, removes focus from any focused event chip so
    /// the "clicking off an event clears its focus ring" mental model holds.</summary>
    internal async Task HandleSlotClickDayAsync(int rowIndex, int slotIndex)
    {
        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var endMinutes = startMinutes + _resolvedSlotMinutes;
        var start = _rangeStart.AddMinutes(startMinutes);
        var end = _rangeStart.AddMinutes(endMinutes);
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(start, end, _rows[rowIndex].LaneId));
        await BlurActiveEventChipAsync();
    }

    /// <summary>Fire OnSlotClicked for the supplied row + day-index (TimeScale=Week or Month).
    /// After the callback resolves, removes focus from any focused event chip.</summary>
    internal async Task HandleSlotClickDayCellAsync(int rowIndex, int dayIndex)
    {
        var (s, e) = _dayBounds[dayIndex];
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(s, e, _rows[rowIndex].LaneId));
        await BlurActiveEventChipAsync();
    }

    /// <summary>Fire OnDayOverflowClicked for a row's "+N earlier" chip (Day mode only).
    /// The row's lane id is carried on <see cref="DayOverflowContext{TEvent}.LaneId"/> so the
    /// consumer can identify which row was clicked; null for the unassigned row.</summary>
    internal Task HandleEarlierChipClickAsync(int rowIndex)
    {
        var day = DateOnly.FromDateTime(_rangeStart.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Earlier, MapToTyped(_rows[rowIndex].Layout.EarlierOverflow),
            LaneId: _rows[rowIndex].LaneId));
    }

    /// <summary>Fire OnDayOverflowClicked for a row's "+N later" chip (Day mode only).
    /// Carries the row's lane id; null for the unassigned row.</summary>
    internal Task HandleLaterChipClickAsync(int rowIndex)
    {
        var day = DateOnly.FromDateTime(_rangeStart.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Later, MapToTyped(_rows[rowIndex].Layout.LaterOverflow),
            LaneId: _rows[rowIndex].LaneId));
    }

    /// <summary>Fire OnDayOverflowClicked for a row's "+N" overlap block. Carries the row's lane id.</summary>
    internal Task HandleOverlapChipClickAsync(int rowIndex, OverlapOverflowBlock block)
    {
        var day = DateOnly.FromDateTime(_rangeStart.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Overlap, MapToTyped(block.Events),
            RegionStart: block.RegionStart, RegionEnd: block.RegionEnd,
            LaneId: _rows[rowIndex].LaneId));
    }

    /// <summary>
    /// Keyboard handler on the grid container. Up/Down moves between lane rows;
    /// Left/Right moves within a row. Enter on a focused cell fires the slot click;
    /// Escape either clears a non-empty selection (Phase 2 Task 11 — FR-34) or
    /// blurs to the parent container (FR-30 fallback for the empty-selection case).
    /// </summary>
    internal async Task HandleGridKeyDownAsync(KeyboardEventArgs e)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch (FR-36). Replaces
        // Task 13's TryDispatchUndoRedoAsync. IsDragActive precedence unchanged.
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null && _rows.Length > 0)
        {
            var moveTimeAxisMax = TimeScale == TimelineScale.Day
                ? Math.Max(0, SlotCount - 1)
                : Math.Max(0, _dayBounds.Count - 1);

            switch (e.Key)
            {
                case "ArrowRight":
                    var origTimeIdxRight = GetKeyboardMoveOriginalTimeIndex();
                    _keyboardMovePhantomTimeOffset = Math.Min(
                        moveTimeAxisMax - origTimeIdxRight,
                        _keyboardMovePhantomTimeOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    var origTimeIdxLeft = GetKeyboardMoveOriginalTimeIndex();
                    _keyboardMovePhantomTimeOffset = Math.Max(
                        -origTimeIdxLeft,
                        _keyboardMovePhantomTimeOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origLaneIdxDown = GetKeyboardMoveOriginalLaneIndex();
                    _keyboardMovePhantomLaneOffset = Math.Min(
                        _rows.Length - 1 - origLaneIdxDown,
                        _keyboardMovePhantomLaneOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowUp":
                    var origLaneIdxUp = GetKeyboardMoveOriginalLaneIndex();
                    _keyboardMovePhantomLaneOffset = Math.Max(
                        -origLaneIdxUp,
                        _keyboardMovePhantomLaneOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "Enter":
                    await CommitKeyboardMoveAsync();
                    return;
                case "Escape":
                    await CancelKeyboardMoveAsync();
                    return;
            }
        }

        if (await TryDispatchShortcutAsync(e, KeystrokeScope.Grid)) return;

        var timeAxisMax = TimeScale == TimelineScale.Day
            ? Math.Max(0, SlotCount - 1)
            : Math.Max(0, _dayBounds.Count - 1);

        switch (e.Key)
        {
            case "ArrowDown":
                if (_rows.Length > 0)
                {
                    _focusedRowIndex = Math.Min(_rows.Length - 1, _focusedRowIndex + 1);
                    _focusMovePending = true;
                    StateHasChanged();
                }
                break;
            case "ArrowUp":
                _focusedRowIndex = Math.Max(0, _focusedRowIndex - 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowRight":
                _focusedTimeIndex = Math.Min(timeAxisMax, _focusedTimeIndex + 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowLeft":
                _focusedTimeIndex = Math.Max(0, _focusedTimeIndex - 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "Enter":
                if (_rows.Length > 0)
                {
                    if (TimeScale == TimelineScale.Day)
                    {
                        await HandleSlotClickDayAsync(_focusedRowIndex, _focusedTimeIndex);
                    }
                    else
                    {
                        await HandleSlotClickDayCellAsync(_focusedRowIndex, _focusedTimeIndex);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Keyboard handler on an event card. Enter fires <c>OnEventClicked</c> via the
    /// existing Phase 1 path (no selection mutation). Space toggles the focused chip
    /// in/out of the selection when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> is enabled
    /// (FR-34 keyboard); when disabled the handler defers to the browser default so
    /// the synthesized click drives a single-id selection (FR-29 fail-closed).
    /// Escape clears a non-empty selection (Task 11) or falls through to FR-30 blur.
    /// </summary>
    internal async Task HandleEventKeyDownAsync(KeyboardEventArgs e, ICalendarEvent ev)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch. See Day view's
        // matching branch for the longer rationale.
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null && _rows.Length > 0)
        {
            var moveTimeAxisMax = TimeScale == TimelineScale.Day
                ? Math.Max(0, SlotCount - 1)
                : Math.Max(0, _dayBounds.Count - 1);

            switch (e.Key)
            {
                case "ArrowRight":
                    var origTimeIdxRight = GetKeyboardMoveOriginalTimeIndex();
                    _keyboardMovePhantomTimeOffset = Math.Min(
                        moveTimeAxisMax - origTimeIdxRight,
                        _keyboardMovePhantomTimeOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    var origTimeIdxLeft = GetKeyboardMoveOriginalTimeIndex();
                    _keyboardMovePhantomTimeOffset = Math.Max(
                        -origTimeIdxLeft,
                        _keyboardMovePhantomTimeOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origLaneIdxDown = GetKeyboardMoveOriginalLaneIndex();
                    _keyboardMovePhantomLaneOffset = Math.Min(
                        _rows.Length - 1 - origLaneIdxDown,
                        _keyboardMovePhantomLaneOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowUp":
                    var origLaneIdxUp = GetKeyboardMoveOriginalLaneIndex();
                    _keyboardMovePhantomLaneOffset = Math.Max(
                        -origLaneIdxUp,
                        _keyboardMovePhantomLaneOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "Enter":
                    await CommitKeyboardMoveAsync();
                    return;
                case "Escape":
                    await CancelKeyboardMoveAsync();
                    return;
            }
        }

        var typed = TypedFor(ev);
        if (await TryDispatchShortcutAsync(e, KeystrokeScope.Chip, typed, ev.Id)) return;

        if (e.Key == "Enter")
        {
            await HandleEventClickAsync(ev);
        }
    }

    /// <summary>
    /// View-specific command dispatch — see Day view for the canonical pattern.
    /// </summary>
    private protected override async Task<bool> DispatchViewCommandAsync(
        string commandId,
        KeyboardEventArgs e,
        KeystrokeScope scope,
        TEvent? focusedEvent,
        string? focusedEventId)
    {
        switch (commandId)
        {
            case SchedulerCommandIds.SelectToggle:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null) return false;
                // Timeline uses TypedFor (which walks lane VisibleEventSets); we don't
                // have a single dictionary lookup here, so we re-resolve via the per-
                // lane sets. The base passed focusedEvent already; route through that.
                if (focusedEvent is null) return false;
                await HandleEventClickAsync((ICalendarEvent)focusedEvent, new MouseEventArgs { CtrlKey = true });
                return true;
            case SchedulerCommandIds.EditDelete:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEvent is null) return false;
                await HandleDeleteAsync((ICalendarEvent)focusedEvent);
                return true;
            case SchedulerCommandIds.Cancel:
                await HandleEscapeAsync();
                return true;
        }
        return false;
    }

    private protected override async Task DispatchKeyboardMoveAsync(TEvent? focusedEvent, string? focusedEventId)
    {
        if (focusedEvent is null || focusedEventId is null) return;

        _keyboardMoveMode = true;
        _keyboardMoveEventId = focusedEventId;
        _keyboardMovePhantomTimeOffset = 0;
        _keyboardMovePhantomLaneOffset = 0;
        _keyboardMoveOriginalStart = focusedEvent.Start;
        _keyboardMoveOriginalEnd = focusedEvent.End;
        _keyboardMoveOriginalLaneId = LaneKey is not null ? LaneKey(focusedEvent) : FindRowIndexFor(focusedEvent) >= 0 && FindRowIndexFor(focusedEvent) < _rows.Length ? _rows[FindRowIndexFor(focusedEvent)].LaneId : null;

        var request = new KeyboardMoveRequest
        {
            Event = focusedEvent,
            CurrentSlotIndex = GetKeyboardMoveOriginalTimeIndex(),
        };
        await OnKeyboardMoveRequested.InvokeAsync(request);

        _optimisticPin[focusedEventId] = (focusedEvent.Start, focusedEvent.End, _keyboardMoveOriginalLaneId);
        StateHasChanged();
    }

    private protected override async Task DispatchKeyboardResizeAsync(TEvent? focusedEvent, string? focusedEventId, KeyboardResizeDirection direction)
    {
        if (focusedEvent is null || focusedEventId is null) return;

        var currentLaneId = _keyboardMoveOriginalLaneId
            ?? (LaneKey is not null ? LaneKey(focusedEvent) : null);
        var rowIndex = FindRowIndexFor(focusedEvent);
        if (rowIndex >= 0 && rowIndex < _rows.Length)
        {
            currentLaneId ??= _rows[rowIndex].LaneId;
        }

        DateTimeOffset newEnd;
        if (TimeScale == TimelineScale.Day)
        {
            var slotMinutes = _resolvedSlotMinutes;
            var deltaMinutes = direction == KeyboardResizeDirection.Extend ? slotMinutes : -slotMinutes;
            newEnd = focusedEvent.End.AddMinutes(deltaMinutes);

            if (newEnd <= focusedEvent.Start)
            {
                newEnd = focusedEvent.Start.AddMinutes(slotMinutes);
            }
        }
        else
        {
            // Week/Month: resize by one day cell
            var deltaDays = direction == KeyboardResizeDirection.Extend ? 1 : -1;
            newEnd = focusedEvent.End.AddDays(deltaDays);
            var minEnd = focusedEvent.Start.AddDays(1);
            if (newEnd < minEnd) newEnd = minEnd;
        }

        var request = new KeyboardResizeRequest
        {
            Event = focusedEvent,
            Direction = direction,
        };
        await OnKeyboardResizeRequested.InvokeAsync(request);

        _optimisticPin[focusedEventId] = (focusedEvent.Start, newEnd, currentLaneId);
        ComputeLayout();
        StateHasChanged();

        var context = new EventResizeContext
        {
            Event = focusedEvent,
            NewEnd = newEnd,
        };
        await OnEventResized.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(focusedEventId);
            ComputeLayout();
            StateHasChanged();
        }
    }

    private int GetKeyboardMoveOriginalTimeIndex()
    {
        if (TimeScale == TimelineScale.Day)
        {
            var minutes = (_keyboardMoveOriginalStart.TimeOfDay.TotalMinutes - _resolvedStartHour * 60);
            return Math.Clamp((int)(minutes / _resolvedSlotMinutes), 0, Math.Max(0, SlotCount - 1));
        }
        else
        {
            for (var i = 0; i < _dayBounds.Count; i++)
            {
                if (_keyboardMoveOriginalStart >= _dayBounds[i].Start && _keyboardMoveOriginalStart < _dayBounds[i].End)
                    return i;
            }
            return 0;
        }
    }

    private int GetKeyboardMoveOriginalLaneIndex()
    {
        var laneId = _keyboardMoveOriginalLaneId;
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].LaneId == laneId)
                return i;
        }
        return 0;
    }

    private async Task UpdateKeyboardMovePhantomPositionAsync()
    {
        if (_keyboardMoveEventId is null || _rows.Length == 0) return;

        var origTimeIdx = GetKeyboardMoveOriginalTimeIndex();
        var origLaneIdx = GetKeyboardMoveOriginalLaneIndex();

        var timeAxisMax = TimeScale == TimelineScale.Day
            ? Math.Max(0, SlotCount - 1)
            : Math.Max(0, _dayBounds.Count - 1);

        var newTimeIdx = Math.Clamp(origTimeIdx + _keyboardMovePhantomTimeOffset, 0, timeAxisMax);
        var newLaneIdx = Math.Clamp(origLaneIdx + _keyboardMovePhantomLaneOffset, 0, _rows.Length - 1);
        var newLaneId = _rows[newLaneIdx].LaneId;

        DateTimeOffset newStart;
        if (TimeScale == TimelineScale.Day)
        {
            var visibleStart = _rangeStart.AddHours(_resolvedStartHour);
            newStart = visibleStart.AddMinutes(newTimeIdx * _resolvedSlotMinutes);
        }
        else
        {
            var targetDayStart = _dayBounds[newTimeIdx].Start;
            // Preserve the time-of-day offset from the original event
            var origDayStart = _dayBounds[origTimeIdx].Start;
            var timeOfDay = _keyboardMoveOriginalStart - origDayStart;
            newStart = targetDayStart + timeOfDay;
        }

        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
        var newEnd = newStart + duration;

        _optimisticPin[_keyboardMoveEventId] = (newStart, newEnd, newLaneId);
        ComputeLayout();
        StateHasChanged();
    }

    private async Task CommitKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null || _rows.Length == 0) return;

        // Resolve the event by walking lane rows (mirrors TypedFor but without
        // needing to construct a placeholder ICalendarEvent).
        TEvent? ev = default;
        for (var i = 0; i < _rows.Length; i++)
        {
            ev = _rows[i].Set.FindById(_keyboardMoveEventId);
            if (ev is not null) break;
        }
        if (ev is null)
        {
            await CancelKeyboardMoveAsync();
            return;
        }

        var origTimeIdx = GetKeyboardMoveOriginalTimeIndex();
        var origLaneIdx = GetKeyboardMoveOriginalLaneIndex();

        var timeAxisMax = TimeScale == TimelineScale.Day
            ? Math.Max(0, SlotCount - 1)
            : Math.Max(0, _dayBounds.Count - 1);

        var newTimeIdx = Math.Clamp(origTimeIdx + _keyboardMovePhantomTimeOffset, 0, timeAxisMax);
        var newLaneIdx = Math.Clamp(origLaneIdx + _keyboardMovePhantomLaneOffset, 0, _rows.Length - 1);
        var newLaneId = _rows[newLaneIdx].LaneId;

        DateTimeOffset newStart;
        if (TimeScale == TimelineScale.Day)
        {
            var visibleStart = _rangeStart.AddHours(_resolvedStartHour);
            newStart = visibleStart.AddMinutes(newTimeIdx * _resolvedSlotMinutes);
        }
        else
        {
            var targetDayStart = _dayBounds[newTimeIdx].Start;
            var origDayStart = _dayBounds[origTimeIdx].Start;
            var timeOfDay = _keyboardMoveOriginalStart - origDayStart;
            newStart = targetDayStart + timeOfDay;
        }

        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
        var newEnd = newStart + duration;

        var context = new EventMoveContext
        {
            Event = ev,
            NewStart = newStart,
            NewEnd = newEnd,
            NewLaneId = newLaneId,
        };
        await OnEventMoved.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(_keyboardMoveEventId);
            ComputeLayout();
            StateHasChanged();
        }

        _keyboardMoveMode = false;
        _keyboardMoveEventId = null;
        _keyboardMovePhantomTimeOffset = 0;
        _keyboardMovePhantomLaneOffset = 0;
        _keyboardMoveOriginalLaneId = null;
    }

    private async Task CancelKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null) return;

        _optimisticPin.Remove(_keyboardMoveEventId);
        ComputeLayout();
        StateHasChanged();

        _keyboardMoveMode = false;
        _keyboardMoveEventId = null;
        _keyboardMovePhantomTimeOffset = 0;
        _keyboardMovePhantomLaneOffset = 0;
        _keyboardMoveOriginalLaneId = null;
    }

    /// <summary>
    /// Shared Delete behavior — mirrors the per-view helper on Day / Week / Month.
    /// Short-circuits on <see cref="SchedulerComponentBase{TEvent}.AllowDelete"/> +
    /// <c>IsDragActive</c>, resolves the focused chip to a typed consumer event via
    /// <see cref="TypedFor"/> (which walks each lane row's <see cref="VisibleEventSet{TEvent}"/>),
    /// dispatches through the base's <see cref="SchedulerComponentBase{TEvent}.TryDeleteFocusedEventAsync"/>
    /// helper.
    /// </summary>
    private async Task HandleDeleteAsync(ICalendarEvent ev)
    {
        if (!AllowDelete) return;
        if (IsDragActive) return;

        var typed = TypedFor(ev);
        if (typed is null) return;

        var changed = await TryDeleteFocusedEventAsync(ev.Id, typed);
        // Standalone path needs its own re-render; cascade path is owned by the
        // root's HandleRequestSelectionChangeAsync (see SchedulerComponentBase.IsStandalone).
        if (changed && IsStandalone)
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Shared Escape behavior — mirrors the per-view helper on Day / Week / Month:
    /// defer to JS mid-drag (ADR-0006), clear non-empty selection (FR-34 keyboard),
    /// otherwise blur (FR-30).
    /// </summary>
    private async Task HandleEscapeAsync()
    {
        if (_keyboardMoveMode)
        {
            await CancelKeyboardMoveAsync();
            return;
        }

        if (IsDragActive)
        {
            return;
        }
        var cleared = await TryClearSelectionViaKeyboardAsync();
        if (cleared)
        {
            StateHasChanged();
            return;
        }
        await BlurActiveAsync();
    }

    /// <summary>True when the supplied (row, time) is the currently-tabbable cell.</summary>
    internal bool IsCellTabbable(int rowIndex, int timeIndex) =>
        rowIndex == _focusedRowIndex && timeIndex == _focusedTimeIndex;

    /// <summary>Build the accessible name for a positioned timed event.</summary>
    internal string EventAccessibleName(ICalendarEvent ev, string laneName) =>
        $"{ev.Title}, {FormatEventTimeRange(ev)}, {laneName}";

    /// <summary>Build the accessible name for an all-day banner chip.</summary>
    internal string AllDayAccessibleName(ICalendarEvent ev, string laneName)
    {
        // Multi-day all-day events use exclusive End (e.g., Vacation Mon–Wed has End=Thu 00:00).
        // AddTicks(-1) normalizes that to the inclusive last day for the spoken accessible name.
        var startDate = TimeZoneInfo.ConvertTime(ev.Start, TimeZone).Date;
        var endDateInclusive = TimeZoneInfo.ConvertTime(ev.End, TimeZone).AddTicks(-1).Date;
        if (startDate == endDateInclusive)
        {
            return $"{ev.Title}, all day on {startDate:MMM d}, {laneName}";
        }
        return $"{ev.Title}, all day from {startDate:MMM d} to {endDateInclusive:MMM d}, {laneName}";
    }

    /// <summary>Accessible name for an overflow chip (Day mode only).</summary>
    internal string OverflowChipAccessibleName(int count, OverflowKind kind) =>
        kind == OverflowKind.Earlier
            ? $"{count} events earlier"
            : $"{count} events later";

    /// <summary>Accessible name for a "+N" overlap block in a lane row.</summary>
    internal string OverlapBlockAccessibleName(RowLayout row, OverlapOverflowBlock block)
    {
        var start = TimeZoneInfo.ConvertTime(block.RegionStart, TimeZone);
        var end = TimeZoneInfo.ConvertTime(block.RegionEnd, TimeZone);
        return $"{block.Events.Count} more events from {start:h:mm tt} to {end:h:mm tt}, {row.LaneName}, activate to choose";
    }

    /// <summary>Format an X-axis tick label for the Week mode.</summary>
    internal string FormatWeekTick(DateTimeOffset dayStart) => $"{dayStart:ddd} {dayStart.Day}";

    /// <summary>Format an X-axis tick label for the Month mode (just the day-of-month).</summary>
    internal string FormatMonthTick(DateTimeOffset dayStart) => dayStart.Day.ToString();

    private async Task BlurActiveAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActive"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    /// <summary>Blur the active element only if it is an event chip
    /// (data-calee-region="event"). Used by the slot-click handlers.</summary>
    private async Task BlurActiveEventChipAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActiveIfEvent"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    // ----- Drag-to-move (Phase 2 Task 6 — FR-25) --------------------------------------

    /// <summary>The dictionary the .razor template binds via
    /// <c>@ref="EventRefsByEventId[eventId]"</c>.</summary>
    internal Dictionary<string, ElementReference> EventRefsByEventId => _eventRefsByEventId;

    /// <summary>
    /// Returns the optimistic-pin <c>(Start, End, LaneId)</c> for the supplied event id
    /// if one is set; otherwise null. The .razor template uses this to display the pinned
    /// time-range label even though the consumer's authoritative TEvent has stale times
    /// until the data round-trip completes.
    /// </summary>
    internal (DateTimeOffset Start, DateTimeOffset End, string? LaneId)? GetOptimisticPin(string id) =>
        _optimisticPin.TryGetValue(id, out var pin) ? pin : null;

    /// <summary>
    /// Locate the row index whose lane id matches the projected lane id of
    /// <paramref name="ev"/>. Returns <c>-1</c> when no matching row exists (the event
    /// is unassigned and the unassigned row isn't shown, in which case the chip wouldn't
    /// have rendered anyway — defensive guard).
    /// </summary>
    private int FindRowIndexFor(TEvent ev)
    {
        // Honor a pre-existing pin's LaneId — the row the event currently *appears* on
        // is what the user grabbed, not the row the consumer's authoritative LaneKey
        // says it belongs to.
        string? key;
        if (_optimisticPin.TryGetValue(ev.Id, out var pin))
        {
            key = pin.LaneId;
        }
        else
        {
            key = LaneKey(ev);
        }

        // Normalize: an unknown lane id is visually equivalent to the unassigned row.
        var normalized = key;
        if (normalized is not null)
        {
            var found = false;
            for (var i = 0; i < Lanes.Count; i++)
            {
                if (string.Equals(Lanes[i].Id, normalized, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found) normalized = null;
        }

        for (var i = 0; i < _rows.Length; i++)
        {
            // Both LaneId values can be null (unassigned row); StringComparer handles that.
            if (string.Equals(_rows[i].LaneId, normalized, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Pointer-down handler attached to each event chip when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is true. Starts a
    /// drag-to-move via the base's <c>BeginDragOnPointerAsync</c>; the drop branch routes
    /// to <see cref="HandleMoveDropAsync"/>. The cancel branch is a no-op per ADR-0006 —
    /// mid-drag cancel never pinned anything, so there's nothing to revert.
    /// </summary>
    internal async Task OnEventPointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToMove) return;
        // Only primary button (left mouse / first touch) starts a drag — PointerEvent
        // spec: Button==0 is primary; touch always reports 0. Filters out right/middle.
        if (e.Button != 0) return;

        var typed = TypedFor(ev);
        if (typed is null) return;

        // Look up the chip's element ref by id. The dict is populated by @ref captures
        // on first mount and survives @key-matched reorders across rows / lanes.
        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        // TimelineView's geometry is rotated relative to Day/Week: X = time, Y = lane row.
        // Both axes snap. snapPixelsX = time-slot width (Day mode = slot pixels; Week/Month
        // = day-cell width). snapPixelsY = row height.
        var gridHeightPx = await GetTimeAreaHeightPxAsync();
        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && _rows.Length > 0) ? gridHeightPx / _rows.Length : 0;
        double snapPixelsX = 0;
        if (gridWidthPx > 0)
        {
            if (TimeScale == TimelineScale.Day && SlotCount > 0)
            {
                snapPixelsX = gridWidthPx / SlotCount;
            }
            else if (TimeScale != TimelineScale.Day && _dayBounds.Count > 0)
            {
                snapPixelsX = gridWidthPx / _dayBounds.Count;
            }
        }

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.Move,
            snapPixelsX: snapPixelsX,
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleMoveDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask);
    }

    /// <summary>
    /// Drop handler. Converts the JS drop delta into a new <c>(Start, End, LaneId)</c>:
    /// X-axis snaps to the time slot (Day mode) or day cell (Week/Month mode); Y-axis
    /// snaps to a row index, which selects the target lane (or unassigned row → null
    /// <see cref="EventMoveContext.NewLaneId"/>). Duration is preserved. The pin is
    /// applied optimistically, <see cref="SchedulerComponentBase{TEvent}.OnEventMoved"/>
    /// fires, and the pin rolls back if the consumer sets <see cref="EventMoveContext.Cancel"/>.
    /// </summary>
    private async Task HandleMoveDropAsync(TEvent ev, DropPayload payload)
    {
        var gridHeightPx = await GetTimeAreaHeightPxAsync();
        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        if (gridHeightPx <= 0)
        {
            // Fallback geometry for test environments without a real DOM. Use a per-row
            // height that mirrors a comfortable lane density (40 px/row) — the *ratio* of
            // DeltaYPx / rowHeight is what determines the row shift, so any positive
            // value gives a deterministic answer in unit tests.
            gridHeightPx = Math.Max(1, _rows.Length) * 40.0;
        }
        if (gridWidthPx <= 0)
        {
            // Width fallback. Day mode covers VisibleMinutes minutes at 56 px/hour
            // (default --calee-scheduler-pixels-per-hour); Week/Month: 100 px/day, which
            // makes test-supplied DeltaXPx easy to reason about.
            if (TimeScale == TimelineScale.Day)
            {
                gridWidthPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
            }
            else
            {
                gridWidthPx = Math.Max(1, _dayBounds.Count) * 100.0;
            }
        }

        // 1) Y-axis (lane row). Find the original row, add the rounded delta-row shift,
        //    clamp to the visible row range. The target row's LaneId is the new lane id —
        //    null when the target is the unassigned row (Per ADR-0011 + FR-25).
        var origRowIndex = FindRowIndexFor(ev);
        if (origRowIndex < 0)
        {
            // Defensive: pointer-down wouldn't have fired for an unrendered event.
            return;
        }
        var rowHeight = gridHeightPx / Math.Max(1, _rows.Length);
        var rowShift = rowHeight > 0
            ? (int)Math.Round(payload.DeltaYPx / rowHeight, MidpointRounding.AwayFromZero)
            : 0;
        var newRowIndex = Math.Clamp(origRowIndex + rowShift, 0, Math.Max(0, _rows.Length - 1));
        var newLaneId = _rows[newRowIndex].LaneId;

        // 2) X-axis (time). The drop math must agree with the same-coordinate OnSlotClicked
        //    result so a drag-and-drop is bit-identical to a click at the drop coordinate.
        //    - Day mode: snap to SlotDurationMinutes via InverseX over [rangeStart, rangeEnd)
        //      restricted to the visible [StartHour, EndHour) band.
        //    - Week/Month mode: snap to a whole-day cell via DeltaXPx / dayWidth rounding,
        //      preserving the time-of-day from the original event so the visible Start
        //      lands on the same hour:minute as before, just on a new day.
        DateTimeOffset newStart;
        if (TimeScale == TimelineScale.Day)
        {
            var visibleStart = _rangeStart.AddHours(_resolvedStartHour);
            var visibleEnd = _rangeStart.AddHours(_resolvedEndHour);
            var origStartMinutes = (ev.Start - visibleStart).TotalMinutes;
            var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridWidthPx;
            var newStartPxInGrid = (origStartMinutes / minutesPerPx) + payload.DeltaXPx;

            newStart = EventLayoutEngine.InverseX(
                pixelX: newStartPxInGrid,
                totalWidthPx: gridWidthPx,
                rangeStart: visibleStart,
                rangeEndExclusive: visibleEnd,
                slotMinutes: _resolvedSlotMinutes);
        }
        else
        {
            // Week / Month: snap to whole-day shifts. Locate the original event's day in
            // _dayBounds, round the X delta into a day-shift, clamp.
            var origDayIndex = -1;
            for (var i = 0; i < _dayBounds.Count; i++)
            {
                if (ev.Start >= _dayBounds[i].Start && ev.Start < _dayBounds[i].End)
                {
                    origDayIndex = i;
                    break;
                }
            }
            if (origDayIndex < 0)
            {
                // Event begins outside the visible day-bounds (e.g., multi-day from earlier).
                // Anchor to the first visible day so the drop math still produces a sensible
                // result; consumers can validate or override server-side.
                origDayIndex = 0;
            }
            var dayWidth = gridWidthPx / Math.Max(1, _dayBounds.Count);
            var dayShift = dayWidth > 0
                ? (int)Math.Round(payload.DeltaXPx / dayWidth, MidpointRounding.AwayFromZero)
                : 0;
            var newDayIndex = Math.Clamp(origDayIndex + dayShift, 0, Math.Max(0, _dayBounds.Count - 1));
            // Preserve the original time-of-day on the new day. The new day's Start carries
            // its own offset (DST-aware via ComputeWeekDays / ComputeDayBounds), so we
            // recompose the date+time from the target day's Start + the original time span
            // relative to its own day start.
            var origDayStart = _dayBounds[origDayIndex].Start;
            var timeOfDay = ev.Start - origDayStart;
            newStart = _dayBounds[newDayIndex].Start + timeOfDay;
        }

        var duration = ev.End - ev.Start;
        var newEnd = newStart + duration;

        // Optimistic pin: apply visually before consumer commits (ADR-0006).
        _optimisticPin[ev.Id] = (newStart, newEnd, newLaneId);
        ComputeLayout();
        StateHasChanged();

        var context = new EventMoveContext
        {
            Event = ev,
            NewStart = newStart,
            NewEnd = newEnd,
            // FR-25: NewLaneId is always populated in TimelineView, even for same-row
            // time-only moves — consumers compare against the event's known lane to
            // detect a reassignment. Null indicates the unassigned row (ADR-0011).
            NewLaneId = newLaneId,
        };
        await OnEventMoved.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(ev.Id);
            ComputeLayout();
            StateHasChanged();
        }
        // If not canceled, the pin remains until the consumer's authoritative event AND
        // lane projection catch up — ClearAcknowledgedPins drops it on the next
        // OnParametersSet.
    }

    /// <summary>
    /// Query the time-area scroll container's real on-screen height via the JS helper.
    /// Returns 0 in test environments without a real DOM; callers fall back to a
    /// default-derived geometry in that case.
    /// </summary>
    private async Task<double> GetTimeAreaHeightPxAsync()
    {
        if (_jsModule is null) return 0;
        try
        {
            return await _jsModule.InvokeAsync<double>("getElementHeight", _timeAreaScrollContainer);
        }
        catch (JSException) { return 0; }
        catch (JSDisconnectedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>
    /// Query the time-area scroll container's real on-screen width via the JS helper.
    /// Returns 0 in test environments without a real DOM; callers fall back to a default
    /// geometry in that case.
    /// </summary>
    private async Task<double> GetTimeAreaWidthPxAsync()
    {
        if (_jsModule is null) return 0;
        try
        {
            return await _jsModule.InvokeAsync<double>("getElementWidth", _timeAreaScrollContainer);
        }
        catch (JSException) { return 0; }
        catch (JSDisconnectedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>
    /// Test-only entry point for the drop-handling pipeline. Lets the test project
    /// exercise the optimistic-pin + callback flow without driving a real pointer-drag
    /// sequence through JS interop (which bUnit's headless DOM cannot produce).
    /// Visibility is <see langword="internal"/>; the test assembly sees it via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal Task InvokeMoveDropForTestAsync(TEvent ev, DropPayload payload) =>
        HandleMoveDropAsync(ev, payload);

    // ----- Drag-to-resize (Phase 2 Task 7 — FR-26) -------------------------------------

    /// <summary>
    /// Returns the centralized <c>aria-roledescription</c> string for the supplied chip,
    /// composed from <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> +
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToResize"/> via
    /// <see cref="SchedulerComponentBase{TEvent}.GetEventChipAriaRoleDescription"/>.
    /// </summary>
    internal string? EventAriaRoleDescription() => GetEventChipAriaRoleDescription();

    /// <summary>
    /// Pointer-down handler attached to the right-edge resize hit-zone of each event
    /// chip when <see cref="SchedulerComponentBase{TEvent}.AllowDragToResize"/> is true.
    /// Starts a resize-end drag with <see cref="ResizeAxis.X"/>; only the right edge
    /// of the ghost moves (left is anchored to the original event Start). The drop
    /// branch routes to <see cref="HandleResizeDropAsync"/>; the cancel branch is a
    /// no-op per ADR-0006.
    /// </summary>
    internal async Task OnEventResizePointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToResize) return;
        if (e.Button != 0) return;

        var typed = TypedFor(ev);
        if (typed is null) return;

        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        // Resize only stretches along the X axis (time). snapPixelsX = time-slot width
        // (Day mode) or day-cell width (Week/Month mode); snapPixelsY = 0 because the
        // lane row doesn't change during a resize. The user can't lane-reassign by
        // dragging the right edge — that's drag-to-move's job.
        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        double snapPixelsX = 0;
        if (gridWidthPx > 0)
        {
            if (TimeScale == TimelineScale.Day && SlotCount > 0)
            {
                snapPixelsX = gridWidthPx / SlotCount;
            }
            else if (TimeScale != TimelineScale.Day && _dayBounds.Count > 0)
            {
                snapPixelsX = gridWidthPx / _dayBounds.Count;
            }
        }

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.ResizeEnd,
            snapPixelsX: snapPixelsX,
            snapPixelsY: 0,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleResizeDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.X);
    }

    /// <summary>
    /// Drop handler for the resize-end drag in TimelineView. Converts DeltaXPx into a
    /// new <c>End</c>: in Day mode it snaps to the slot boundary inside the visible
    /// band; in Week/Month mode it snaps to a whole-day shift preserving the trailing
    /// time-of-day relative to its day's start. Preserves <c>Start</c>. The optimistic
    /// pin shares storage with move-mode pins (FR-26 + ADR-0006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>LaneId in the pin.</strong> The pin's LaneId stays equal to the row the
    /// event is currently rendered on — resize never changes the lane (FR-26).
    /// <see cref="FindRowIndexFor(TEvent)"/> already honors any pre-existing pin, so a
    /// resize after a move correctly stays on the post-move row.
    /// </para>
    /// <para>
    /// <strong>Minimum-duration clamp.</strong> When the user drags the right edge past
    /// (or before) the event's <c>Start</c>, the new End is clamped to
    /// <c>Start + SlotDurationMinutes</c> in Day mode and <c>Start + 24 h</c> in
    /// Week/Month mode (one cell). Resize cannot invert the range.
    /// </para>
    /// <para>
    /// <strong>End-of-band clamp.</strong> Dragging past the visible range's
    /// right edge clamps to the range end. Same behavior as move-mode's clamping.
    /// </para>
    /// </remarks>
    private async Task HandleResizeDropAsync(TEvent ev, DropPayload payload)
    {
        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        if (gridWidthPx <= 0)
        {
            if (TimeScale == TimelineScale.Day)
            {
                gridWidthPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
            }
            else
            {
                gridWidthPx = Math.Max(1, _dayBounds.Count) * 100.0;
            }
        }

        // Compute the lane id the chip currently lives on — resize keeps it unchanged.
        // FindRowIndexFor honors any pre-existing pin so a resize after a move stays
        // on the (pinned) post-move row rather than reverting to LaneKey's projection.
        var rowIndex = FindRowIndexFor(ev);
        if (rowIndex < 0)
        {
            return; // Defensive — pointer-down wouldn't fire for an unrendered chip.
        }
        var laneId = _rows[rowIndex].LaneId;

        DateTimeOffset newEnd;
        if (TimeScale == TimelineScale.Day)
        {
            var visibleStart = _rangeStart.AddHours(_resolvedStartHour);
            var visibleEnd = _rangeStart.AddHours(_resolvedEndHour);
            var origEndMinutes = (ev.End - visibleStart).TotalMinutes;
            var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridWidthPx;
            var newEndPxInGrid = (origEndMinutes / minutesPerPx) + payload.DeltaXPx;

            var totalMinutes = (visibleEnd - visibleStart).TotalMinutes;
            var minutesFromStartUnclamped = newEndPxInGrid / gridWidthPx * totalMinutes;
            var snappedMinutes = Math.Round(
                minutesFromStartUnclamped / _resolvedSlotMinutes,
                MidpointRounding.AwayFromZero) * _resolvedSlotMinutes;
            if (snappedMinutes > totalMinutes) snappedMinutes = totalMinutes;
            if (snappedMinutes < 0) snappedMinutes = 0;
            newEnd = visibleStart.AddMinutes(snappedMinutes);

            var minEnd = ev.Start.AddMinutes(_resolvedSlotMinutes);
            if (newEnd < minEnd) newEnd = minEnd;
        }
        else
        {
            // Week / Month: snap End to whole-day shifts. Locate the original event's
            // End in _dayBounds (using the half-open day cell its End falls into; an
            // event ending exactly on a day boundary lives in the *previous* cell so
            // the End-edge sits at that cell's right wall).
            var origEndDayIndex = -1;
            for (var i = 0; i < _dayBounds.Count; i++)
            {
                // `End > Start && End <= End-exclusive` — End sitting exactly on a
                // boundary (e.g., 00:00 the next day) belongs to the *previous* cell.
                if (ev.End > _dayBounds[i].Start && ev.End <= _dayBounds[i].End)
                {
                    origEndDayIndex = i;
                    break;
                }
            }
            if (origEndDayIndex < 0)
            {
                // Event ends outside the visible range (e.g., multi-day extending past
                // the visible window). Anchor to the last visible cell for a sensible
                // drop result; consumers can override server-side.
                origEndDayIndex = _dayBounds.Count - 1;
            }

            var dayWidth = gridWidthPx / Math.Max(1, _dayBounds.Count);
            var dayShift = dayWidth > 0
                ? (int)Math.Round(payload.DeltaXPx / dayWidth, MidpointRounding.AwayFromZero)
                : 0;
            var newEndDayIndex = Math.Clamp(origEndDayIndex + dayShift, 0, Math.Max(0, _dayBounds.Count - 1));
            // Preserve the trailing time-of-day relative to its day's Start so DST is
            // respected on both sides of the shift.
            var origDayStart = _dayBounds[origEndDayIndex].Start;
            var endTimeOfDay = ev.End - origDayStart;
            newEnd = _dayBounds[newEndDayIndex].Start + endTimeOfDay;

            // Minimum-duration clamp in non-Day mode: one day cell (24 h calendar day,
            // which is the natural snap unit for Week/Month).
            var minEnd = ev.Start + TimeSpan.FromDays(1);
            if (newEnd < minEnd) newEnd = minEnd;
        }

        // Optimistic pin shares storage with move-mode pins. The pin's LaneId equals
        // the chip's current row's lane id (which may itself be a pinned value from a
        // prior move). Resize only mutates End; Start + LaneId are preserved.
        _optimisticPin[ev.Id] = (ev.Start, newEnd, laneId);
        ComputeLayout();
        StateHasChanged();

        var context = new EventResizeContext
        {
            Event = ev,
            NewEnd = newEnd,
        };
        await OnEventResized.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(ev.Id);
            ComputeLayout();
            StateHasChanged();
        }
    }

    /// <summary>
    /// Test-only entry point for the resize drop-handling pipeline. Mirrors
    /// <see cref="InvokeMoveDropForTestAsync"/>.
    /// </summary>
    internal Task InvokeResizeDropForTestAsync(TEvent ev, DropPayload payload) =>
        HandleResizeDropAsync(ev, payload);

    /// <summary>
    /// Test-only entry point for keyboard move dispatch (issue #20).
    /// </summary>
    internal Task InvokeKeyboardMoveForTestAsync(TEvent ev) =>
        DispatchKeyboardMoveAsync(ev, ev.Id);

    /// <summary>
    /// Test-only entry point for keyboard resize dispatch (issue #20).
    /// </summary>
    internal Task InvokeKeyboardResizeForTestAsync(TEvent ev, KeyboardResizeDirection direction) =>
        DispatchKeyboardResizeAsync(ev, ev.Id, direction);

    /// <summary>
    /// Test-only entry point for committing keyboard move (issue #20).
    /// </summary>
    internal Task InvokeKeyboardMoveCommitForTestAsync() =>
        CommitKeyboardMoveAsync();

    /// <summary>
    /// Test-only entry point for cancelling keyboard move (issue #20).
    /// </summary>
    internal Task InvokeKeyboardMoveCancelForTestAsync() =>
        CancelKeyboardMoveAsync();

    /// <summary>
    /// Test-only flag: whether keyboard move mode is active (issue #20).
    /// </summary>
    internal bool IsKeyboardMoveModeForTest => _keyboardMoveMode;

    /// <summary>
    /// Test-only access to phantom offsets (issue #20).
    /// </summary>
    internal (int timeOffset, int laneOffset) KeyboardMovePhantomOffsetsForTest =>
        (_keyboardMovePhantomTimeOffset, _keyboardMovePhantomLaneOffset);

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) -------------------------------------

    /// <summary>
    /// Pointer-down handler bound to each row's slot/day cells when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToCreate"/> is true. Starts a
    /// <see cref="DragMode.CreateRegion"/> drag anchored at the cell's (rowIndex, timeIndex),
    /// growing horizontally as the cursor moves. The drag stays within the anchor row
    /// (lane axis is locked); the resulting <see cref="EventCreateContext.Slot"/> carries
    /// the anchor row's lane id (null for the unassigned row). Below the 5 px threshold
    /// the JS module fires <c>onCancel</c> and the slot's own <c>@onclick</c> drives
    /// <c>OnSlotClicked</c>.
    /// </summary>
    /// <param name="e">The originating pointer event (viewport coords + button info).</param>
    /// <param name="anchorRowIndex">The row the user pressed on — locks the lane axis.</param>
    /// <param name="anchorTimeIndex">The slot index (Day) or day-cell index (Week/Month).</param>
    internal async Task OnGridPointerDownAsync(PointerEventArgs e, int anchorRowIndex, int anchorTimeIndex)
    {
        if (!AllowDragToCreate) return;
        if (e.Button != 0) return;

        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        double snapPixelsX = 0;
        if (gridWidthPx > 0)
        {
            if (TimeScale == TimelineScale.Day && SlotCount > 0)
            {
                snapPixelsX = gridWidthPx / SlotCount;
            }
            else if (TimeScale != TimelineScale.Day && _dayBounds.Count > 0)
            {
                snapPixelsX = gridWidthPx / _dayBounds.Count;
            }
        }

        await BeginDragOnPointerAsync(
            e,
            _timeAreaScrollContainer,
            DragMode.CreateRegion,
            snapPixelsX: snapPixelsX,
            snapPixelsY: 0,                       // Lane axis locked.
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleCreateDropAsync(anchorRowIndex, anchorTimeIndex, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.X,
            anchorViewportX: e.ClientX,
            anchorViewportY: e.ClientY,
            thresholdPx: 5,
            // Bound the ghost to the anchor lane row. _timeAreaScrollContainer
            // spans all rows; without this slice the ghost would draw across the
            // full lane stack height.
            crossAxisIndex: anchorRowIndex,
            crossAxisDivisions: _rows.Length);
    }

    /// <summary>
    /// Drop handler for a drag-to-create on TimelineView. Computes the spanned
    /// <c>(Start, End)</c> along the X axis (lane axis is locked to the anchor row),
    /// populates <see cref="SchedulerSlot.LaneId"/> from the anchor row, fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/>, and exits. Below
    /// the threshold the path never reaches here (JS fires onCancel instead). No
    /// optimistic phantom event — Option A per the Task 8 lifecycle decision (see
    /// commit body).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Anchor row locked.</strong> Vertical cursor movement does not change the
    /// resulting lane assignment — the spanned region stays on the anchor's row.
    /// Cross-lane creates require a separate gesture (consumer can multi-create after
    /// the fact). <see cref="SchedulerSlot.LaneId"/> is the anchor row's lane id,
    /// which is <see langword="null"/> for an unassigned-row anchor when
    /// <see cref="ShowUnassignedRow"/> is true.
    /// </para>
    /// <para>
    /// <strong>Bidirectional drag.</strong> Normalized via <c>min(anchor, final)</c>
    /// / <c>max(anchor, final)</c>; the resulting Start is always before End regardless
    /// of cursor direction (left or right).
    /// </para>
    /// </remarks>
    private async Task HandleCreateDropAsync(int anchorRowIndex, int anchorTimeIndex, DropPayload payload)
    {
        if (anchorRowIndex < 0 || anchorRowIndex >= _rows.Length) return;

        var gridWidthPx = await GetTimeAreaWidthPxAsync();
        if (gridWidthPx <= 0)
        {
            // Same fallback geometry as the move/resize drop handlers.
            if (TimeScale == TimelineScale.Day)
            {
                gridWidthPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
            }
            else
            {
                gridWidthPx = Math.Max(1, _dayBounds.Count) * 100.0;
            }
        }

        DateTimeOffset start;
        DateTimeOffset end;

        if (TimeScale == TimelineScale.Day)
        {
            var slotWidthPx = gridWidthPx / SlotCount;
            var slotShift = slotWidthPx > 0
                ? (int)Math.Round(payload.DeltaXPx / slotWidthPx, MidpointRounding.AwayFromZero)
                : 0;
            var finalSlotIndex = Math.Clamp(anchorTimeIndex + slotShift, 0, SlotCount - 1);

            var startSlot = Math.Min(anchorTimeIndex, finalSlotIndex);
            var endSlot = Math.Max(anchorTimeIndex, finalSlotIndex) + 1;
            if (endSlot > SlotCount) endSlot = SlotCount;

            var startMinutes = _resolvedStartHour * 60 + startSlot * _resolvedSlotMinutes;
            var endMinutes = _resolvedStartHour * 60 + endSlot * _resolvedSlotMinutes;
            start = _rangeStart.AddMinutes(startMinutes);
            end = _rangeStart.AddMinutes(endMinutes);
        }
        else
        {
            // Week/Month: time-axis cell is one whole day.
            var dayWidthPx = gridWidthPx / Math.Max(1, _dayBounds.Count);
            var dayShift = dayWidthPx > 0
                ? (int)Math.Round(payload.DeltaXPx / dayWidthPx, MidpointRounding.AwayFromZero)
                : 0;
            var finalDayIndex = Math.Clamp(anchorTimeIndex + dayShift, 0, _dayBounds.Count - 1);

            var startDay = Math.Min(anchorTimeIndex, finalDayIndex);
            var endDay = Math.Max(anchorTimeIndex, finalDayIndex);

            start = _dayBounds[startDay].Start;
            end = _dayBounds[endDay].End;
        }

        var anchorRowLaneId = _rows[anchorRowIndex].LaneId;

        var context = new EventCreateContext
        {
            Slot = new SchedulerSlot(start, end, anchorRowLaneId),
        };
        await OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the create drop-handling pipeline. Lets the test project
    /// exercise the callback flow without driving a real pointer-drag sequence. Mirrors
    /// <see cref="InvokeMoveDropForTestAsync"/> / <see cref="InvokeResizeDropForTestAsync"/>.
    /// </summary>
    /// <param name="anchorRowIndex">The row the synthetic create anchors on.</param>
    /// <param name="anchorTimeIndex">Slot index (Day) or day-cell index (Week/Month).</param>
    /// <param name="payload">Synthetic <see cref="DropPayload"/>; only <c>DeltaXPx</c> is consumed.</param>
    internal Task InvokeCreateDropForTestAsync(int anchorRowIndex, int anchorTimeIndex, DropPayload payload) =>
        HandleCreateDropAsync(anchorRowIndex, anchorTimeIndex, payload);

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -----------------------------

    /// <summary>
    /// Double-click handler bound to each lane slot cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDoubleClickToCreate"/> is true.
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/> with a
    /// <see cref="SchedulerSlot"/> spanning <c>(cellStart, cellStart + defaultDuration)</c>;
    /// <see cref="SchedulerSlot.LaneId"/> is the clicked row's lane id (null for an
    /// unassigned-row anchor when <see cref="ShowUnassignedRow"/> is true). Same
    /// lifecycle (no optimistic phantom event) as drag-to-create per ADR-0006.
    /// </summary>
    /// <remarks>
    /// Duration resolution honors <see cref="Extensions.CaleeSchedulerOptions.DefaultCreateDurationMinutes"/>
    /// when set; otherwise the per-scale default kicks in. At
    /// <see cref="TimelineScale.Day"/> the default is one <c>SlotDurationMinutes</c>;
    /// at <see cref="TimelineScale.Week"/> / <see cref="TimelineScale.Month"/> the
    /// default is 1440 minutes (one day). A Week/Month-scale slot is itself a whole
    /// day, so the dbl-clicked cell's Start/End is already a day-aligned range — at
    /// those scales the default-duration branch returns the cell's natural bounds and
    /// the consumer-explicit option overrides them.
    /// </remarks>
    /// <param name="rowIndex">The lane row that was double-clicked.</param>
    /// <param name="timeIndex">
    /// The slot index (Day scale) or day-cell index (Week/Month scale) inside the row.
    /// </param>
    internal Task HandleDoubleClickCreateAsync(int rowIndex, int timeIndex)
    {
        if (!AllowDoubleClickToCreate) return Task.CompletedTask;
        if (rowIndex < 0 || rowIndex >= _rows.Length) return Task.CompletedTask;

        var useWholeDayDefault = TimeScale != TimelineScale.Day;
        var durationMinutes = ResolveDefaultCreateDurationMinutes(
            slotDurationMinutes: _resolvedSlotMinutes,
            useWholeDayDefault: useWholeDayDefault);

        DateTimeOffset start;
        DateTimeOffset end;

        if (TimeScale == TimelineScale.Day)
        {
            var startMinutes = _resolvedStartHour * 60 + timeIndex * _resolvedSlotMinutes;
            var bandEndMinutes = _resolvedEndHour * 60;
            var endMinutes = Math.Min(startMinutes + durationMinutes, bandEndMinutes);

            start = _rangeStart.AddMinutes(startMinutes);
            end = _rangeStart.AddMinutes(endMinutes);
        }
        else
        {
            // Week/Month: a "time slot" is one whole day cell. Start is the cell's
            // midnight; End is computed from the option (when set) or defaults to the
            // cell's exclusive end (one-day cell + 1440 min default = identical result).
            if (timeIndex < 0 || timeIndex >= _dayBounds.Count) return Task.CompletedTask;
            var (cellStart, _) = _dayBounds[timeIndex];
            start = cellStart;
            end = cellStart.AddMinutes(durationMinutes);

            // Clamp to the visible Week/Month window — a consumer-supplied 90-minute
            // duration on a Month-scale view would otherwise extend past the cell.
            var windowEnd = _dayBounds[^1].End;
            if (end > windowEnd) end = windowEnd;
        }

        var laneId = _rows[rowIndex].LaneId;

        var context = new EventCreateContext
        {
            Slot = new SchedulerSlot(start, end, laneId),
        };
        return OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the double-click-create pipeline. Mirrors Day /
    /// Week's <c>InvokeDoubleClickCreateForTestAsync</c>.
    /// </summary>
    /// <param name="rowIndex">Synthetically double-clicked row.</param>
    /// <param name="timeIndex">Synthetically double-clicked time-axis cell.</param>
    internal Task InvokeDoubleClickCreateForTestAsync(int rowIndex, int timeIndex) =>
        HandleDoubleClickCreateAsync(rowIndex, timeIndex);

    /// <summary>
    /// Per-row materialized state: the row's identity (lane Id + name + color, or
    /// unassigned marker), the all-day events to render in the banner strip, and the
    /// engine's layout result for the timed events on this row.
    /// </summary>
    /// <param name="LaneId">
    /// The lane's <see cref="ILane.Id"/>, or <see langword="null"/> for the
    /// unassigned row (used as the <see cref="SchedulerSlot.LaneId"/> for slot clicks).
    /// </param>
    /// <param name="LaneName">Display name shown on the row label.</param>
    /// <param name="LaneColor">Optional tint color from <see cref="ILane.Color"/>.</param>
    /// <param name="Set">The row-local VisibleEventSet; owns the Id→TEvent lookup so click handlers can route through it.</param>
    /// <param name="AllDay">All-day events for this lane that touch the visible range.</param>
    /// <param name="Layout">The engine's layout for this row's timed events.</param>
    /// <param name="IsUnassigned">True for the trailing unassigned row.</param>
    internal sealed record RowLayout(
        string? LaneId,
        string LaneName,
        string? LaneColor,
        VisibleEventSet<TEvent> Set,
        IReadOnlyList<TEvent> AllDay,
        LayoutResult Layout,
        bool IsUnassigned);
}
