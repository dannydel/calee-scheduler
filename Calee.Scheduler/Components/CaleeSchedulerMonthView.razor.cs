#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Month view for Calee.Scheduler (FR-03). Renders a 6-week × 7-day calendar
/// grid covering the calendar month containing <c>CurrentDate</c>, with single-day events
/// as compact chips, multi-day events as continuous bars overlaid across cells, and a
/// "+N more" overflow chip per cell once <c>MaxEventsPerDay</c> is exceeded.
/// Supports drag-to-move (FR-25) and keyboard move mode (WCAG 2.2 SC 2.5.7) when
/// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is true.
/// </summary>
/// <remarks>
/// <para>
/// Implements FR-03, FR-04, FR-09a (via base),
/// FR-16, FR-17 (chip variant via <see cref="EventChipTemplate"/>), FR-18, FR-19, FR-20,
/// FR-21, FR-23, FR-30 (Month portion), FR-32, FR-53, FR-54, FR-55 (extended with the
/// <c>month-cell</c> region), NFR-06 (Month portion), NFR-08.
/// </para>
/// <para>
/// <strong>Layout model.</strong> Month view does <em>not</em> use the
/// <see cref="EventLayoutEngine"/>. Its layout is cell-based, not overlap-based: events
/// occupy "lanes" (vertical bands) within each cell. Multi-day events claim the topmost
/// lanes across the cells they cover so the bars line up horizontally; single-day chips
/// fill the remaining lanes below them. Per-week-row, lane assignment is computed greedily
/// so that overlapping bars do not share a lane.
/// </para>
/// <para>
/// Parameter validation follows PRD §4.6: <see cref="MaxEventsPerDay"/> less than 1
/// hard-fails with <see cref="ArgumentException"/>. Null events soft-degrade via the base.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerMonthView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// First day of the visible week (FR-04). Defaults to
    /// <c>SchedulerOptions.Value.DefaultFirstDayOfWeek</c> when null.
    /// </summary>
    [Parameter]
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Maximum number of events rendered inside a single day cell before the "+N more"
    /// overflow chip takes over (FR-18). Must be <c>&gt;= 1</c> when explicitly set.
    /// Defaults to <c>SchedulerOptions.Value.DefaultMaxEventsPerDay</c>.
    /// </summary>
    [Parameter]
    public int? MaxEventsPerDay { get; set; }

    /// <summary>
    /// Optional render fragment for the *inside* of each single-day event chip (FR-17).
    /// Distinct from the time-grid views' <c>EventTemplate</c>: chips in Month view are
    /// visually different from the time-grid event blocks, so the parameter name is
    /// <c>EventChipTemplate</c> to make the visual contract obvious at the call site.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventChipTemplate { get; set; }

    /// <summary>Optional class hook applied to the weekday-label header row (FR-54).</summary>
    [Parameter]
    public string? DayHeaderClass { get; set; }

    /// <summary>Injected JS runtime — used for the Escape blur helper (FR-30).</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // Resolved parameter values after OnParametersSet.
    private DayOfWeek _resolvedFirstDayOfWeek;
    private int _resolvedMaxEventsPerDay;

    // The 42 day cells in display order, each as a midnight-midnight bound in TimeZone.
    // Index = week-row * 7 + column.
    private (DateTimeOffset Start, DateTimeOffset End)[] _gridCells = Array.Empty<(DateTimeOffset, DateTimeOffset)>();

    // Cached DayModifier results (issue #8), one entry per _gridCells index — evaluated
    // once per parameter set, not per render frame, for every rendered cell (including
    // in/out-of-month cells — Month always renders 42 real dates).
    private SchedulerDayState?[] _cellDayStates = Array.Empty<SchedulerDayState?>();

    // The month being displayed, used to detect "outside the month" cells.
    private int _displayedMonth;
    private int _displayedYear;

    // Per-cell render data — what's painted inside each cell.
    private CellLayout[] _cellLayouts = Array.Empty<CellLayout>();

    // Per-week-row bar segments. Index = week row (0..5).
    private List<BarSegment>[] _barsPerWeekRow = Array.Empty<List<BarSegment>>();

    // Cache the consumer's TEvent by Id so click handlers can fire with the original reference.
    private Dictionary<string, TEvent> _eventLookup = new();

    /// <summary>
    /// Per-event element refs the drag layer uses as the ghost source. Keyed by event id
    /// so chips and bars whose position changes between renders still resolve to their
    /// captured DOM element.
    /// </summary>
    private readonly Dictionary<string, ElementReference> _eventRefsByEventId = new(StringComparer.Ordinal);

    /// <summary>
    /// Optimistic pins for in-flight or just-completed drag-to-move and keyboard-move
    /// operations (ADR-0006). The rendering pipeline substitutes (Start, End) in this
    /// dictionary for the consumer-supplied event's authoritative times so the new position
    /// is visible before the consumer's data round-trip completes.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End)> _optimisticPin =
        new(StringComparer.Ordinal);

    // Keyboard move mode state
    private bool _keyboardMoveMode;
    private string? _keyboardMoveEventId;
    private int _keyboardMovePhantomCellOffset;
    private DateTimeOffset _keyboardMoveOriginalStart;
    private DateTimeOffset _keyboardMoveOriginalEnd;

    // Roving-tabindex anchor for the grid: linear index (0..41).
    private int _focusedCellIndex;

    // FR-23 — fire OnRangeChanged only when the visible range actually changes.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private IJSObjectReference? _jsModule;

    // The grid's outer wrapper (role="grid") — queried by focusActiveGridCell (issue #19)
    // to find the currently-tabbable day cell.
    private ElementReference _monthGridRef;

    // Issue #19 — set by HandleGridKeyDownAsync when an arrow key moves the roving
    // tabindex; consumed in OnAfterRenderAsync (after the tabindex swap has actually
    // rendered) to move real browser focus onto the newly-active day cell.
    private bool _focusMovePending;

    /// <summary>Inclusive start of the visible 6-week range.</summary>
    private DateTimeOffset GridStart => _gridCells.Length > 0 ? _gridCells[0].Start : default;

    /// <summary>Exclusive end of the visible 6-week range.</summary>
    private DateTimeOffset GridEndExclusive => _gridCells.Length > 0 ? _gridCells[^1].End : default;

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        var opts = SchedulerOptions.Value;
        _resolvedFirstDayOfWeek = FirstDayOfWeek ?? opts.DefaultFirstDayOfWeek;
        _resolvedMaxEventsPerDay = MaxEventsPerDay ?? opts.DefaultMaxEventsPerDay;

        if (_resolvedMaxEventsPerDay < 1)
        {
            throw new ArgumentException(
                $"MaxEventsPerDay must be >= 1; got {_resolvedMaxEventsPerDay}.",
                nameof(MaxEventsPerDay));
        }

        ComputeGrid();

        // Issue #8 — evaluate the per-day state hook once per rendered cell, in the
        // grid time zone (_gridCells' midnight boundaries), not per render frame.
        _cellDayStates = new SchedulerDayState?[_gridCells.Length];
        if (DayModifier is not null)
        {
            for (var i = 0; i < _gridCells.Length; i++)
            {
                _cellDayStates[i] = GetDayState(_gridCells[i].Start);
            }
        }

        // Optimistic-pin housekeeping (ADR-0006). Drop entries the consumer has caught
        // up on — i.e., the consumer's authoritative Start/End for the event now matches
        // the pinned values, so the pin is redundant. Performed before computing layout
        // so the engine sees only still-relevant pins.
        ClearAcknowledgedPins();

        ComputeLayout();

        if (_lastRangeStart != GridStart || _lastRangeEnd != GridEndExclusive)
        {
            _lastRangeStart = GridStart;
            _lastRangeEnd = GridEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(GridStart, GridEndExclusive));
        }
    }

    /// <summary>
    /// Compute the 42-cell visible grid covering the calendar month containing
    /// <c>CurrentDate</c>, anchored to the configured first-day-of-week.
    /// </summary>
    private void ComputeGrid()
    {
        var anchor = CurrentDate.Date;
        _displayedMonth = anchor.Month;
        _displayedYear = anchor.Year;

        var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
        var dayOffset = ((int)firstOfMonth.DayOfWeek - (int)_resolvedFirstDayOfWeek + 7) % 7;
        var gridStartDate = firstOfMonth.AddDays(-dayOffset);

        _gridCells = new (DateTimeOffset, DateTimeOffset)[42];
        for (var i = 0; i < 42; i++)
        {
            var d = gridStartDate.AddDays(i);
            var offset = TimeZone.GetUtcOffset(d);
            var start = new DateTimeOffset(d, offset);
            var end = start.AddDays(1);
            _gridCells[i] = (start, end);
        }
    }

    /// <summary>
    /// Classify each filtered event as a single-day chip or multi-day bar segment,
    /// then assign cell-lane positions so bars line up across cells and chips fill
    /// below them. Computes overflow counts per cell.
    /// </summary>
    private void ComputeLayout()
    {
        var filtered = GetFilteredEvents();
        _eventLookup = new Dictionary<string, TEvent>(filtered.Count);

        // Populate event lookup from the consumer's original list so click handlers
        // always return the consumer's reference, never a pinned wrapper.
        foreach (var ev in filtered)
        {
            _eventLookup[ev.Id] = ev;
        }

        // Apply optimistic-pin overrides (ADR-0006). Pinned events are wrapped in
        // EventChunk<TEvent> with substituted Start/End so the classification loop
        // below sees the pinned times.
        IReadOnlyList<ICalendarEvent> eventsForLayout = _optimisticPin.Count > 0
            ? ApplyOptimisticPins(filtered)
            : filtered.Select(e => (ICalendarEvent)e).ToList();

        // (1) Partition into single-day chips (per cell index) and multi-day bar segments
        // (per week row). "Single-day" = first/last day match in TimeZone AND the event
        // fits inside one cell's midnight–midnight bound.
        var chipsPerCell = new List<ICalendarEvent>[42];
        for (var i = 0; i < 42; i++) chipsPerCell[i] = new List<ICalendarEvent>();

        _barsPerWeekRow = new List<BarSegment>[6];
        for (var r = 0; r < 6; r++) _barsPerWeekRow[r] = new List<BarSegment>();

        foreach (var ev in eventsForLayout)
        {
            // Skip events entirely outside the visible 6-week window.
            if (ev.End <= GridStart || ev.Start >= GridEndExclusive) continue;

            if (IsSingleCellEvent(ev, out var cellIndex))
            {
                chipsPerCell[cellIndex].Add(ev);
                continue;
            }

            // Multi-cell event: find every cell index it touches, then split into
            // contiguous per-week-row spans.
            AppendBarSegments(ev);
        }

        // (2) Lane-assign bars within each week row. Greedy: scan in start order,
        // place each bar in the lowest lane index that has no overlap.
        for (var r = 0; r < 6; r++)
        {
            AssignBarLanes(_barsPerWeekRow[r]);
        }

        // (3) Build per-cell render data: for each cell, accumulate the bar segments that
        // touch it (recorded as a virtual "this cell occupies lane L" marker), then place
        // chips into the lanes above MaxEventsPerDay until the limit is reached. Anything
        // beyond becomes the "+N more" tail.
        _cellLayouts = new CellLayout[42];
        for (var i = 0; i < 42; i++)
        {
            _cellLayouts[i] = BuildCellLayout(i, chipsPerCell[i]);
        }
    }

    /// <summary>
    /// True when the event is contained within a single grid cell (either an all-day event
    /// that spans exactly one date or a timed event whose start and end share the same
    /// date in <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>).
    /// </summary>
    private bool IsSingleCellEvent(ICalendarEvent ev, out int cellIndex)
    {
        cellIndex = -1;
        // Find the cell whose midnight–midnight bound fully contains the event.
        for (var i = 0; i < _gridCells.Length; i++)
        {
            var (cs, ce) = _gridCells[i];
            if (ev.Start >= cs && ev.End <= ce)
            {
                cellIndex = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Decompose a multi-cell event into one bar segment per week row it touches.
    /// Each segment carries its first/last column indices within that row and per-edge
    /// "extends past grid" clip flags.
    /// </summary>
    private void AppendBarSegments(ICalendarEvent ev)
    {
        // Find the contiguous run of cell indices the event covers.
        int firstCell = -1, lastCell = -1;
        for (var i = 0; i < _gridCells.Length; i++)
        {
            var (cs, ce) = _gridCells[i];
            if (ev.End > cs && ev.Start < ce)
            {
                if (firstCell < 0) firstCell = i;
                lastCell = i;
            }
        }
        if (firstCell < 0) return; // Shouldn't happen — pre-filtered.

        var clipsBeforeGrid = ev.Start < GridStart;
        var clipsAfterGrid = ev.End > GridEndExclusive;

        // Walk the covered cells, splitting at week-row boundaries (every 7 cells).
        var cursor = firstCell;
        while (cursor <= lastCell)
        {
            var row = cursor / 7;
            var rowEnd = (row + 1) * 7 - 1;
            var segEnd = Math.Min(rowEnd, lastCell);

            var leftCol = cursor % 7;
            var rightCol = segEnd % 7;

            var clipLeft = cursor == firstCell ? clipsBeforeGrid : true; // continuing-from-previous row
            var clipRight = segEnd == lastCell ? clipsAfterGrid : true;  // continuing-into-next row

            _barsPerWeekRow[row].Add(new BarSegment(
                Event: ev,
                LeftColIndex: leftCol,
                RightColIndex: rightCol,
                ClipLeft: clipLeft,
                ClipRight: clipRight,
                LaneIndex: 0)); // assigned in AssignBarLanes

            cursor = segEnd + 1;
        }
    }

    /// <summary>
    /// Greedy lane assignment within a single week row. Bars that overlap horizontally
    /// land on distinct lanes. The list is mutated in place.
    /// </summary>
    private static void AssignBarLanes(List<BarSegment> rowBars)
    {
        if (rowBars.Count == 0) return;

        // Sort by leftmost column so the lane assignment is deterministic.
        rowBars.Sort((a, b) =>
        {
            var c = a.LeftColIndex.CompareTo(b.LeftColIndex);
            return c != 0 ? c : string.CompareOrdinal(a.Event.Id, b.Event.Id);
        });

        // For each lane, track the rightmost column it currently extends to (-1 = unused).
        var laneRights = new List<int>();
        for (var i = 0; i < rowBars.Count; i++)
        {
            var bar = rowBars[i];
            var placed = false;
            for (var lane = 0; lane < laneRights.Count; lane++)
            {
                if (laneRights[lane] < bar.LeftColIndex)
                {
                    laneRights[lane] = bar.RightColIndex;
                    rowBars[i] = bar with { LaneIndex = lane };
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                rowBars[i] = bar with { LaneIndex = laneRights.Count };
                laneRights.Add(bar.RightColIndex);
            }
        }
    }

    /// <summary>
    /// Build the render-time layout for a single cell: which bar lanes are occupied,
    /// which chips fit, and the overflow count.
    /// </summary>
    private CellLayout BuildCellLayout(int cellIndex, List<ICalendarEvent> chips)
    {
        var row = cellIndex / 7;
        var col = cellIndex % 7;

        // Determine which bar lanes touch this cell (those bars whose [LeftColIndex,RightColIndex]
        // include `col`).
        var occupiedLanes = new HashSet<int>();
        foreach (var bar in _barsPerWeekRow[row])
        {
            if (bar.LeftColIndex <= col && col <= bar.RightColIndex)
            {
                occupiedLanes.Add(bar.LaneIndex);
            }
        }

        var barCount = occupiedLanes.Count;
        var chipCount = chips.Count;

        // FR-18: at most MaxEventsPerDay items visible per cell. Bars always render (they're
        // cross-cell — clipping one cell would leave a hole in a continuous bar), so bars
        // claim slots first; chips fill the remaining budget. The "+N more" count reflects
        // only the chips that overflow — bars are always visible and out of the overflow sum.
        var chipBudget = Math.Max(0, _resolvedMaxEventsPerDay - barCount);
        var visibleChipCount = Math.Min(chipCount, chipBudget);
        var overflow = chipCount - visibleChipCount;

        var visibleChips = chipCount == visibleChipCount
            ? chips
            : chips.Take(visibleChipCount).ToList();

        return new CellLayout(
            CellIndex: cellIndex,
            OccupiedLanes: occupiedLanes,
            VisibleChips: visibleChips,
            OverflowCount: overflow);
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);
        }

        // Issue #19 — move real browser focus onto the newly-active day cell after an
        // arrow-key roving move. Deferred to here so the query runs after the tabindex
        // swap has rendered to the DOM.
        if (_focusMovePending && _jsModule is not null)
        {
            _focusMovePending = false;
            await SchedulerViewPrimitives.TryFocusActiveGridCellAsync(_jsModule, _monthGridRef);
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

    /// <summary>Cell count — always 42 (6 weeks × 7 days).</summary>
    internal int CellCount => _gridCells.Length;

    /// <summary>Number of week rows — always 6.</summary>
    internal int WeekRowCount => 6;

    /// <summary>Number of day columns — always 7.</summary>
    internal int ColumnCount => 7;

    /// <summary>The 7 weekday labels in display order, starting at the configured first-day-of-week.</summary>
    internal IEnumerable<string> WeekdayLabels()
    {
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_resolvedFirstDayOfWeek + i) % 7);
            yield return day.ToString().Substring(0, 3); // Sun, Mon, Tue, ...
        }
    }

    /// <summary>True when the cell at <paramref name="cellIndex"/> represents the displayed month.</summary>
    internal bool IsInDisplayedMonth(int cellIndex) =>
        _gridCells[cellIndex].Start.Year == _displayedYear
        && _gridCells[cellIndex].Start.Month == _displayedMonth;

    /// <summary>True when the cell at <paramref name="cellIndex"/> matches "today in TimeZone".</summary>
    internal bool IsTodayCell(int cellIndex) =>
        _gridCells[cellIndex].Start.Date == Today.Date;

    /// <summary>The date displayed in the corner of the cell.</summary>
    internal int CellDayNumber(int cellIndex) => _gridCells[cellIndex].Start.Day;

    /// <summary>
    /// Accessible name for the cell: "Wednesday, May 20, 2026" — or the blocked-day
    /// label (issue #8) when the cell's day is blocked.
    /// </summary>
    internal string CellAccessibleName(int cellIndex)
    {
        var d = _gridCells[cellIndex].Start;
        return IsCellBlocked(cellIndex)
            ? SchedulerViewPrimitives.BlockedDayAccessibleLabel(d, _cellDayStates[cellIndex])
            : d.ToString("dddd, MMMM d, yyyy");
    }

    /// <summary>Returns true when the supplied cell index is the currently-tabbable cell.</summary>
    internal bool IsCellTabbable(int cellIndex) => cellIndex == _focusedCellIndex;

    // ----- Blocked days (issue #8) -----------------------------------------------------

    /// <summary>True when the cell's day is blocked per <see cref="SchedulerComponentBase{TEvent}.DayModifier"/>.</summary>
    internal bool IsCellBlocked(int cellIndex) => _cellDayStates[cellIndex]?.IsBlocked ?? false;

    /// <summary>Consumer-supplied per-day class hook for the cell's day, or null.</summary>
    internal string? DayBlockedClassFor(int cellIndex) => _cellDayStates[cellIndex]?.Class;

    /// <summary>
    /// Issue #8 — Month's grid-focus concept is the roving cell index; the
    /// create-at-focus suppression check looks at the focused cell's day. Month has
    /// no drag-to-create, so this only gates the create-at-focus keystroke — the
    /// double-click path is gated directly in <see cref="HandleDoubleClickCreateAsync"/>.
    /// </summary>
    private protected override bool IsFocusedGridDayBlocked() =>
        _focusedCellIndex >= 0 && _focusedCellIndex < _cellDayStates.Length && IsCellBlocked(_focusedCellIndex);

    /// <summary>Bars to render on the supplied week row.</summary>
    internal IReadOnlyList<BarSegment> BarsForWeekRow(int rowIndex) => _barsPerWeekRow[rowIndex];

    /// <summary>The cell layout for the supplied cell index.</summary>
    internal CellLayout LayoutForCell(int cellIndex) => _cellLayouts[cellIndex];

    /// <summary>
    /// Bar-lane count to reserve in a single cell's chip area — counts only the
    /// bars whose <c>[LeftColIndex, RightColIndex]</c> span actually covers
    /// this cell's column. Cells in the same row but outside any bar's span
    /// return 0 so their chip area can use the full cell height instead of
    /// reserving phantom space for bars that visually end one column earlier.
    /// Replaces the prior <c>BarLaneCountForRow</c> which reserved the same
    /// space in every cell of the row, wasting vertical room past a bar's
    /// right edge (e.g., a Mon–Thu bar still pushed Fri/Sat chips down).
    /// </summary>
    /// <param name="rowIndex">Zero-based week-row index (0..5).</param>
    /// <param name="colIndex">Zero-based column index within the row (0..6).</param>
    internal int BarLaneCountForCell(int rowIndex, int colIndex)
    {
        var bars = _barsPerWeekRow[rowIndex];
        if (bars.Count == 0) return 0;
        var maxLane = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            if (colIndex >= b.LeftColIndex && colIndex <= b.RightColIndex)
            {
                if (b.LaneIndex > maxLane) maxLane = b.LaneIndex;
            }
        }
        // -1 means no covering bar → 0 lanes reserved.
        return maxLane + 1;
    }

    /// <summary>Returns the underlying TEvent for an ICalendarEvent (so click handlers receive the consumer's reference).</summary>
    internal TEvent? TypedForId(string id) =>
        _eventLookup.TryGetValue(id, out var t) ? t : default;

    /// <summary>Returns the consumer-supplied CSS class for an event (via base helper).</summary>
    internal string? ClassFor(TEvent ev) => GetEventClass(ev);

    /// <summary>Format an event's start time in the configured zone (e.g., "9:00 AM").</summary>
    internal string FormatStartTime(ICalendarEvent ev)
    {
        var startLocal = TimeZoneInfo.ConvertTime(ev.Start, TimeZone);
        return startLocal.ToString("h:mm tt");
    }

    /// <summary>Build the accessible name for a single-day chip.</summary>
    internal string ChipAccessibleName(ICalendarEvent ev) =>
        ev.IsAllDay ? ev.Title : $"{ev.Title}, {FormatStartTime(ev)}";

    /// <summary>Build the accessible name for a multi-day bar segment.</summary>
    internal string BarAccessibleName(BarSegment bar)
    {
        var ev = bar.Event;
        var start = TimeZoneInfo.ConvertTime(ev.Start, TimeZone);
        var end = TimeZoneInfo.ConvertTime(ev.End, TimeZone);
        if (ev.IsAllDay)
        {
            // All-day events end at the *next day*'s midnight; subtract a day for the readable
            // "to <date>" phrase.
            var lastDay = end.AddTicks(-1);
            return $"{ev.Title}, all day from {start:MMM d} to {lastDay:MMM d}";
        }
        return $"{ev.Title}, from {start:MMM d h:mm tt} to {end:MMM d h:mm tt}";
    }

    /// <summary>Build the accessible name for a "+N more" overflow chip.</summary>
    internal string OverflowChipAccessibleName(int cellIndex, int count)
    {
        var d = _gridCells[cellIndex].Start;
        return $"{count} more events on {d:MMMM d}";
    }

    // ----- Event handlers ------------------------------------------------------------

    /// <summary>Fire OnEventClicked with the consumer's original TEvent and update the selection.</summary>
    /// <remarks>
    /// Selection mutation happens only on real pointer clicks (<paramref name="args"/>
    /// non-null) — the Phase 1 keyboard Enter path is unchanged; Task 11 wires the
    /// selection-keyboard surface. Render order for Shift+click range select is:
    /// per-week-row bars (left-to-right within their week), then per-cell chips in
    /// grid-cell order. Matches the .razor DOM order so Shift+click across cells
    /// follows visual expectation.
    /// </remarks>
    internal Task HandleEventClickAsync(ICalendarEvent ev, MouseEventArgs? args = null)
    {
        if (!_eventLookup.TryGetValue(ev.Id, out var typed))
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
        var ids = new List<string>(_eventLookup.Count);
        // Bars for each week row, in week-row order then bar order.
        for (var r = 0; r < WeekRowCount; r++)
        {
            var bars = BarsForWeekRow(r);
            for (var i = 0; i < bars.Count; i++) ids.Add(bars[i].Event.Id);
        }
        // Per-cell chips in cell order (0..41), each cell's visible chips top to bottom.
        for (var c = 0; c < _gridCells.Length; c++)
        {
            var layout = LayoutForCell(c);
            for (var i = 0; i < layout.VisibleChips.Count; i++)
            {
                ids.Add(layout.VisibleChips[i].Id);
            }
        }
        return ids;
    }

    /// <summary>Fire OnSlotClicked for a cell with the day's [midnight, next midnight) bounds.
    /// After the callback resolves, removes focus from any focused event chip / bar so the
    /// "clicking off an event clears its focus ring" mental model holds.</summary>
    internal async Task HandleCellSlotClickAsync(int cellIndex)
    {
        var (start, end) = _gridCells[cellIndex];
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(start, end));
        await BlurActiveEventChipAsync();
    }

    /// <summary>Fire OnDayOverflowClicked with the cell's date and <see cref="OverflowKind.Month"/>.</summary>
    internal Task HandleOverflowChipClickAsync(int cellIndex)
    {
        var day = DateOnly.FromDateTime(_gridCells[cellIndex].Start.Date);
        // ponytail: Month passes empty Events — its consumer re-derives the day's events from
        // the date, as before. Populating requires CellLayout to retain its overflow events;
        // add that only if a Month chooser actually needs them.
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Month, Array.Empty<TEvent>()));
    }

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -----------------------------

    /// <summary>
    /// Double-click handler bound to each month cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDoubleClickToCreate"/> is true.
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/> with a
    /// <see cref="SchedulerSlot"/> on the clicked date. Same lifecycle (no optimistic
    /// phantom event) as drag-to-create per ADR-0006.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>All-day shape.</strong> Month cells are whole-day, so the proposed event
    /// is an all-day event by convention: <c>Start = midnight in TimeZone</c>,
    /// <c>End = Start + 1 day</c>. The duration defaults to 1440 minutes (one day) per
    /// the per-view rule, but the consumer can override via
    /// <see cref="Extensions.CaleeSchedulerOptions.DefaultCreateDurationMinutes"/>. A non-1440
    /// override produces a Start-anchored timed event in the clicked date's hour band
    /// — useful for consumers that want "double-click → 30-minute meeting at 9 AM" on
    /// every view. <see cref="EventCreateContext"/> does not carry an
    /// <c>IsAllDay</c> flag; the all-day intent is conveyed via the 24-hour-from-
    /// midnight Start/End shape.
    /// </para>
    /// <para>
    /// <strong>LaneId.</strong> Always <see langword="null"/> — Month view has no
    /// lanes.
    /// </para>
    /// </remarks>
    /// <param name="cellIndex">The 0..41 grid cell that was double-clicked.</param>
    internal Task HandleDoubleClickCreateAsync(int cellIndex)
    {
        if (!AllowDoubleClickToCreate) return Task.CompletedTask;
        if (cellIndex < 0 || cellIndex >= _gridCells.Length) return Task.CompletedTask;

        var durationMinutes = ResolveDefaultCreateDurationMinutes(
            slotDurationMinutes: 1440, // Unused — Month is a whole-day view.
            useWholeDayDefault: true);

        var (cellStart, cellEnd) = _gridCells[cellIndex];
        var start = cellStart;
        var end = start.AddMinutes(durationMinutes);

        // For the default 1440-minute case, end will equal cellEnd exactly. A consumer-
        // supplied non-1440 override (e.g., 60 minutes for "default new event = 1 hour")
        // produces a timed event anchored at the cell's midnight; we don't clamp because
        // the consumer asked for that exact duration, and Month view's caller is free to
        // interpret the resulting Start/End however suits their domain.
        _ = cellEnd; // Keep the destructuring symmetric for readers.

        // Issue #8 — fail-closed: no-op on a blocked day (no phantom, no OnEventCreated).
        // Uses the general span check (not just IsCellBlocked(cellIndex)) so a consumer
        // override of DefaultCreateDurationMinutes that pushes End past the cell's
        // midnight still catches a blocked day it crosses into.
        if (CreateSpanTouchesBlockedDay(start, end)) return Task.CompletedTask;

        var context = new EventCreateContext
        {
            // LaneId stays null — Month view has no lanes.
            Slot = new SchedulerSlot(start, end),
        };
        return OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the double-click-create pipeline. Mirrors the other
    /// views' <c>InvokeDoubleClickCreateForTestAsync</c>.
    /// </summary>
    /// <param name="cellIndex">Synthetically double-clicked cell (0..41).</param>
    internal Task InvokeDoubleClickCreateForTestAsync(int cellIndex) =>
        HandleDoubleClickCreateAsync(cellIndex);

    /// <summary>
    /// Keyboard handler for the grid: arrows move the focused cell; Enter on a focused
    /// cell fires <c>OnSlotClicked</c> with the day's bounds; Escape either clears a
    /// non-empty selection (Phase 2 Task 11 — FR-34) or blurs (FR-30 fallback for the
    /// empty-selection case).
    /// </summary>
    internal async Task HandleGridKeyDownAsync(KeyboardEventArgs e)
    {
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomCellOffset = Math.Max(
                        -GetKeyboardMoveOriginalCellIndex(),
                        _keyboardMovePhantomCellOffset - 7);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var maxDown = _gridCells.Length - 1 - GetKeyboardMoveOriginalCellIndex();
                    _keyboardMovePhantomCellOffset = Math.Min(
                        maxDown,
                        _keyboardMovePhantomCellOffset + 7);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowRight":
                    var maxRight = _gridCells.Length - 1 - GetKeyboardMoveOriginalCellIndex();
                    _keyboardMovePhantomCellOffset = Math.Min(
                        maxRight,
                        _keyboardMovePhantomCellOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    _keyboardMovePhantomCellOffset = Math.Max(
                        -GetKeyboardMoveOriginalCellIndex(),
                        _keyboardMovePhantomCellOffset - 1);
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

        switch (e.Key)
        {
            case "ArrowDown":
                _focusedCellIndex = Math.Min(_gridCells.Length - 1, _focusedCellIndex + 7);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowUp":
                _focusedCellIndex = Math.Max(0, _focusedCellIndex - 7);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowRight":
                _focusedCellIndex = Math.Min(_gridCells.Length - 1, _focusedCellIndex + 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowLeft":
                _focusedCellIndex = Math.Max(0, _focusedCellIndex - 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "Enter":
                await HandleCellSlotClickAsync(_focusedCellIndex);
                break;
        }
    }

    /// <summary>
    /// Keyboard handler on a chip/bar. Enter fires <c>OnEventClicked</c> via the
    /// existing Phase 1 path (no selection mutation). Space toggles the focused chip
    /// in/out of the selection when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> is enabled
    /// (FR-34 keyboard); when disabled the handler defers to the browser's default
    /// Space-activates-button so the synthesized click drives a single-id selection
    /// through the existing click path (FR-29 fail-closed). Escape clears a
    /// non-empty selection (Task 11) or falls through to FR-30 blur when empty.
    /// </summary>
    internal async Task HandleEventKeyDownAsync(KeyboardEventArgs e, ICalendarEvent ev)
    {
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomCellOffset = Math.Max(
                        -GetKeyboardMoveOriginalCellIndex(),
                        _keyboardMovePhantomCellOffset - 7);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var maxDown = _gridCells.Length - 1 - GetKeyboardMoveOriginalCellIndex();
                    _keyboardMovePhantomCellOffset = Math.Min(
                        maxDown,
                        _keyboardMovePhantomCellOffset + 7);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowRight":
                    var maxRight = _gridCells.Length - 1 - GetKeyboardMoveOriginalCellIndex();
                    _keyboardMovePhantomCellOffset = Math.Min(
                        maxRight,
                        _keyboardMovePhantomCellOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    _keyboardMovePhantomCellOffset = Math.Max(
                        -GetKeyboardMoveOriginalCellIndex(),
                        _keyboardMovePhantomCellOffset - 1);
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

        _eventLookup.TryGetValue(ev.Id, out var typed);
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
            case SchedulerCommandIds.EditMove:
                if (scope != KeystrokeScope.Chip) return false;
                await DispatchKeyboardMoveAsync(focusedEvent, focusedEventId);
                return true;
            case SchedulerCommandIds.SelectToggle:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null) return false;
                if (!_eventLookup.TryGetValue(focusedEventId, out var underlyingTyped)) return false;
                await HandleEventClickAsync((ICalendarEvent)underlyingTyped, new MouseEventArgs { CtrlKey = true });
                return true;
            case SchedulerCommandIds.EditDelete:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null || focusedEvent is null) return false;
                if (!_eventLookup.TryGetValue(focusedEventId, out var ev)) return false;
                await HandleDeleteAsync((ICalendarEvent)ev);
                return true;
            case SchedulerCommandIds.Cancel:
                await HandleEscapeAsync();
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shared Delete behavior — mirrors the per-view helper on Day / Week / Timeline.
    /// Short-circuits on <see cref="SchedulerComponentBase{TEvent}.AllowDelete"/> +
    /// <c>IsDragActive</c>, resolves the focused chip to a typed consumer event
    /// (Month uses <c>_eventLookup</c> rather than a <see cref="VisibleEventSet{TEvent}"/>
    /// since it doesn't need timed/all-day split), dispatches through the base's
    /// <see cref="SchedulerComponentBase{TEvent}.TryDeleteFocusedEventAsync"/> helper.
    /// </summary>
    private async Task HandleDeleteAsync(ICalendarEvent ev)
    {
        if (!AllowDelete) return;
        if (IsDragActive) return;

        if (!_eventLookup.TryGetValue(ev.Id, out var typed)) return;

        var changed = await TryDeleteFocusedEventAsync(ev.Id, typed);
        // Standalone path needs its own re-render; cascade path is owned by the
        // root's HandleRequestSelectionChangeAsync (see SchedulerComponentBase.IsStandalone).
        if (changed && IsStandalone)
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Shared Escape behavior — mirrors the per-view helper on Day / Week / Timeline:
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

    private async Task BlurActiveAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActive"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    /// <summary>Blur the active element only if it is an event chip / bar
    /// (data-calee-region="event"). Used by the slot-click handler.</summary>
    private async Task BlurActiveEventChipAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActiveIfEvent"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    // ----- Optimistic pins (ADR-0006) ----------------------------------------------------

    /// <summary>
    /// Drop pin entries whose pinned (Start, End) matches the consumer-supplied
    /// authoritative times — i.e., the consumer has accepted the move and pushed
    /// the new times back through <see cref="SchedulerComponentBase{TEvent}.Events"/>.
    /// </summary>
    private void ClearAcknowledgedPins()
    {
        if (_optimisticPin.Count == 0) return;
        var events = Events;
        if (events is null) return;

        List<string>? toRemove = null;
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (_optimisticPin.TryGetValue(ev.Id, out var pin)
                && pin.Start == ev.Start
                && pin.End == ev.End)
            {
                toRemove ??= new List<string>();
                toRemove.Add(ev.Id);
            }
        }
        if (toRemove is null) return;
        foreach (var id in toRemove)
        {
            _optimisticPin.Remove(id);
        }
    }

    /// <summary>
    /// Return the filtered event list with pinned events wrapped in
    /// <see cref="EventChunk{TEvent}"/> to substitute their Start/End with the pin
    /// values. Non-pinned events pass through as-is.
    /// </summary>
    private IReadOnlyList<ICalendarEvent> ApplyOptimisticPins(IReadOnlyList<TEvent> filtered)
    {
        var result = new List<ICalendarEvent>(filtered.Count);
        for (var i = 0; i < filtered.Count; i++)
        {
            var ev = filtered[i];
            if (_optimisticPin.TryGetValue(ev.Id, out var pinned))
            {
                result.Add(new EventChunk<TEvent>(ev, pinned.Start, pinned.End, ClippedAtTimeStart: false, ClippedAtTimeEnd: false));
            }
            else
            {
                result.Add(ev);
            }
        }
        return result;
    }

    /// <summary>Return the optimistic-pin (Start, End) for the supplied event id if set.</summary>
    internal (DateTimeOffset Start, DateTimeOffset End)? GetOptimisticPin(string id) =>
        _optimisticPin.TryGetValue(id, out var pin) ? pin : null;

    /// <summary>The dictionary the .razor template binds for element refs.</summary>
    internal Dictionary<string, ElementReference> EventRefsByEventId => _eventRefsByEventId;

    // ----- Drag-to-move ------------------------------------------------------------------

    /// <summary>
    /// Pointer-down handler attached to each single-day chip when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is true.
    /// </summary>
    internal async Task OnEventPointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToMove) return;
        if (e.Button != 0) return;

        if (!_eventLookup.TryGetValue(ev.Id, out var typed)) return;
        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef)) return;

        var (cellWidth, cellHeight) = await GetMonthGridCellSizePxAsync();
        var snapPixelsX = cellWidth > 0 ? cellWidth : 0;
        var snapPixelsY = cellHeight > 0 ? cellHeight : 0;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.Move,
            snapPixelsX: snapPixelsX,
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleMoveDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            highlightContainer: _monthGridRef,
            highlightMode: "day-cell",
            eventDurationDays: 1,
            columnCount: ColumnCount,
            rowCount: WeekRowCount);
    }

    /// <summary>
    /// Pointer-down handler attached to each multi-day bar when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is true.
    /// </summary>
    internal async Task OnBarPointerDownAsync(PointerEventArgs e, BarSegment bar)
    {
        if (!AllowDragToMove) return;
        if (e.Button != 0) return;

        var ev = bar.Event;
        if (!_eventLookup.TryGetValue(ev.Id, out var typed)) return;
        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef)) return;

        var (cellWidth, cellHeight) = await GetMonthGridCellSizePxAsync();
        var snapPixelsX = cellWidth > 0 ? cellWidth : 0;
        var snapPixelsY = cellHeight > 0 ? cellHeight : 0;
        var eventDurationDays = bar.RightColIndex - bar.LeftColIndex + 1;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.Move,
            snapPixelsX: snapPixelsX,
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleMoveDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            highlightContainer: _monthGridRef,
            highlightMode: "day-cell",
            eventDurationDays: eventDurationDays,
            columnCount: ColumnCount,
            rowCount: WeekRowCount);
    }

    /// <summary>
    /// Drop handler. Converts the JS drop delta (X for column shift, Y for row shift)
    /// into a new (Start, End) preserving the event's duration, optimistically pins the
    /// new position, fires <see cref="SchedulerComponentBase{TEvent}.OnEventMoved"/>, and
    /// rolls the pin back if the consumer set <see cref="EventMoveContext.Cancel"/>.
    /// </summary>
    private async Task HandleMoveDropAsync(TEvent ev, DropPayload payload)
    {
        var (cellWidth, cellHeight) = await GetMonthGridCellSizePxAsync();
        if (cellWidth <= 0) cellWidth = 100.0;
        if (cellHeight <= 0) cellHeight = 100.0;

        var originalCellIndex = FindCellIndexForEvent(ev);
        if (originalCellIndex < 0) return;

        var colShift = cellWidth > 0
            ? (int)Math.Round(payload.DeltaXPx / cellWidth, MidpointRounding.AwayFromZero)
            : 0;
        var rowShift = cellHeight > 0
            ? (int)Math.Round(payload.DeltaYPx / cellHeight, MidpointRounding.AwayFromZero)
            : 0;

        if (colShift == 0 && rowShift == 0) return;

        var rawTarget = originalCellIndex + colShift + rowShift * 7;
        var newCellIndex = Math.Clamp(rawTarget, 0, _gridCells.Length - 1);

        var originalCellStart = _gridCells[originalCellIndex].Start;
        var timeOfDayOffset = ev.Start - originalCellStart;
        var duration = ev.End - ev.Start;

        var newStart = _gridCells[newCellIndex].Start + timeOfDayOffset;
        var newEnd = newStart + duration;

        _optimisticPin[ev.Id] = (newStart, newEnd);
        ComputeLayout();
        StateHasChanged();

        var context = new EventMoveContext
        {
            Event = ev,
            NewStart = newStart,
            NewEnd = newEnd,
        };
        await OnEventMoved.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(ev.Id);
            ComputeLayout();
            StateHasChanged();
        }
    }

    /// <summary>
    /// Query the month grid container's real on-screen dimensions via the JS helper.
    /// Returns zeros in test environments without a real DOM; callers supply fallbacks.
    /// </summary>
    private async Task<(double width, double height)> GetMonthGridCellSizePxAsync()
    {
        if (_jsModule is null) return (0, 0);
        try
        {
            var width = await _jsModule.InvokeAsync<double>("getElementWidth", _monthGridRef);
            var height = await _jsModule.InvokeAsync<double>("getElementHeight", _monthGridRef);
            var cellWidth = width > 0 ? width / ColumnCount : 0;
            var cellHeight = height > 0 ? height / WeekRowCount : 0;
            return (cellWidth, cellHeight);
        }
        catch (JSException) { return (0, 0); }
        catch (JSDisconnectedException) { return (0, 0); }
        catch (InvalidOperationException) { return (0, 0); }
    }

    /// <summary>
    /// Locate the grid cell index whose [midnight, midnight+1d) bounds contain the
    /// event's Start. Returns -1 when no cell contains it.
    /// </summary>
    internal int FindCellIndexForEvent(ICalendarEvent ev)
    {
        for (var i = 0; i < _gridCells.Length; i++)
        {
            var (cs, ce) = _gridCells[i];
            if (ev.Start >= cs && ev.Start < ce) return i;
        }
        return -1;
    }

    /// <summary>
    /// Test-only entry point for the drop-handling pipeline.
    /// </summary>
    internal Task InvokeMoveDropForTestAsync(TEvent ev, DropPayload payload) =>
        HandleMoveDropAsync(ev, payload);

    // ----- Keyboard move -----------------------------------------------------------------

    /// <inheritdoc/>
    private protected override async Task DispatchKeyboardMoveAsync(TEvent? focusedEvent, string? focusedEventId)
    {
        if (focusedEvent is null || focusedEventId is null) return;

        _keyboardMoveMode = true;
        _keyboardMoveEventId = focusedEventId;
        _keyboardMovePhantomCellOffset = 0;
        _keyboardMoveOriginalStart = focusedEvent.Start;
        _keyboardMoveOriginalEnd = focusedEvent.End;

        var currentCellIndex = FindCellIndexForEvent(focusedEvent);
        if (currentCellIndex < 0) currentCellIndex = 0;

        var request = new KeyboardMoveRequest
        {
            Event = (ICalendarEvent)focusedEvent,
            CurrentSlotIndex = currentCellIndex,
        };
        await OnKeyboardMoveRequested.InvokeAsync(request);

        _optimisticPin[focusedEventId] = (focusedEvent.Start, focusedEvent.End);
        StateHasChanged();
    }

    private int GetKeyboardMoveOriginalCellIndex()
    {
        return FindCellForDate(_keyboardMoveOriginalStart);
    }

    private int FindCellForDate(DateTimeOffset value)
    {
        for (var i = 0; i < _gridCells.Length; i++)
        {
            if (value >= _gridCells[i].Start && value < _gridCells[i].End) return i;
        }
        return 0;
    }

    private Task UpdateKeyboardMovePhantomPositionAsync()
    {
        if (_keyboardMoveEventId is null) return Task.CompletedTask;

        var origCellIndex = GetKeyboardMoveOriginalCellIndex();
        var newCellIndex = Math.Clamp(origCellIndex + _keyboardMovePhantomCellOffset, 0, _gridCells.Length - 1);

        var origCellStart = _gridCells[origCellIndex].Start;
        var timeOfDayOffset = _keyboardMoveOriginalStart - origCellStart;
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;

        var newStart = _gridCells[newCellIndex].Start + timeOfDayOffset;
        var newEnd = newStart + duration;

        _optimisticPin[_keyboardMoveEventId] = (newStart, newEnd);
        ComputeLayout();
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task CommitKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null) return;

        var origCellIndex = GetKeyboardMoveOriginalCellIndex();
        var newCellIndex = Math.Clamp(origCellIndex + _keyboardMovePhantomCellOffset, 0, _gridCells.Length - 1);

        var origCellStart = _gridCells[origCellIndex].Start;
        var timeOfDayOffset = _keyboardMoveOriginalStart - origCellStart;
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;

        var newStart = _gridCells[newCellIndex].Start + timeOfDayOffset;
        var newEnd = newStart + duration;

        if (!_eventLookup.TryGetValue(_keyboardMoveEventId, out var ev))
        {
            await CancelKeyboardMoveAsync();
            return;
        }

        var context = new EventMoveContext
        {
            Event = ev,
            NewStart = newStart,
            NewEnd = newEnd,
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
        _keyboardMovePhantomCellOffset = 0;
    }

    private async Task CancelKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null) return;

        _optimisticPin.Remove(_keyboardMoveEventId);
        ComputeLayout();
        StateHasChanged();

        _keyboardMoveMode = false;
        _keyboardMoveEventId = null;
        _keyboardMovePhantomCellOffset = 0;
    }

    /// <summary>Test-only entry point for keyboard move dispatch.</summary>
    internal Task InvokeKeyboardMoveForTestAsync(TEvent ev) =>
        DispatchKeyboardMoveAsync(ev, ev.Id);

    /// <summary>Test-only entry point for committing keyboard move.</summary>
    internal Task InvokeKeyboardMoveCommitForTestAsync() =>
        CommitKeyboardMoveAsync();

    /// <summary>Test-only entry point for cancelling keyboard move.</summary>
    internal Task InvokeKeyboardMoveCancelForTestAsync() =>
        CancelKeyboardMoveAsync();

    /// <summary>Test-only flag: whether keyboard move mode is active.</summary>
    internal bool IsKeyboardMoveModeForTest => _keyboardMoveMode;

    /// <summary>Test-only access to the phantom cell offset.</summary>
    internal int KeyboardMovePhantomCellOffsetForTest => _keyboardMovePhantomCellOffset;

    /// <summary>
    /// A multi-day event's bar segment within a single week row of the visible grid.
    /// </summary>
    /// <param name="Event">The original consumer event reference (used for click handling).</param>
    /// <param name="LeftColIndex">Inclusive leftmost column index this segment covers (0..6).</param>
    /// <param name="RightColIndex">Inclusive rightmost column index this segment covers (0..6).</param>
    /// <param name="ClipLeft">True when the event extends past the segment's left edge
    /// (either before the visible grid, or continuing from the previous week row).</param>
    /// <param name="ClipRight">True when the event extends past the segment's right edge
    /// (either after the visible grid, or continuing into the next week row).</param>
    /// <param name="LaneIndex">Assigned lane index within the week row (0-based, lower = topmost).</param>
    internal sealed record BarSegment(
        ICalendarEvent Event,
        int LeftColIndex,
        int RightColIndex,
        bool ClipLeft,
        bool ClipRight,
        int LaneIndex);

    /// <summary>
    /// Per-cell render-time layout: which bar lanes pass through, which chips fit, and the
    /// "+N more" overflow count when the cell's total exceeds <c>MaxEventsPerDay</c>.
    /// </summary>
    /// <param name="CellIndex">Linear cell index (0..41).</param>
    /// <param name="OccupiedLanes">Lane indices used by bars that pass through this cell.</param>
    /// <param name="VisibleChips">Single-day chips to render in this cell (already truncated by FR-18).</param>
    /// <param name="OverflowCount">"+N" count for the overflow chip; <c>0</c> when no chip is shown.</param>
    internal sealed record CellLayout(
        int CellIndex,
        HashSet<int> OccupiedLanes,
        IReadOnlyList<ICalendarEvent> VisibleChips,
        int OverflowCount);
}
