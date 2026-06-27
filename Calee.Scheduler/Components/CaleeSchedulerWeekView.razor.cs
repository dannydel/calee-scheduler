#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Week view for Calee.Scheduler (FR-02). Renders seven consecutive days
/// starting at the first-day-of-week boundary that contains <c>CurrentDate</c>, with
/// a shared day-header row, a shared all-day row (multi-day events render as a single
/// continuous bar across the columns they cover), per-day "+N earlier"/"+N later"
/// overflow chips, and a shared scrollable hour grid in which timed multi-day events
/// are split into per-day chunks.
/// </summary>
/// <remarks>
/// <para>
/// Implements FR-02, FR-04, FR-05, FR-06,
/// FR-07, FR-09b, FR-12, FR-13 (via <see cref="EventLayoutEngine"/>), FR-14, FR-15,
/// FR-16, FR-17, FR-19, FR-19a, FR-19b, FR-20, FR-21, FR-23, FR-30 (Week portion),
/// FR-31, FR-32, FR-33, FR-53, FR-54, FR-55, NFR-04, NFR-06 (Week portion), NFR-08.
/// </para>
/// <para>
/// Parameter validation follows PRD §4.6: invalid <see cref="StartHour"/>,
/// <see cref="EndHour"/>, or <see cref="SlotDurationMinutes"/> hard-fails with
/// <see cref="ArgumentException"/>; null events soft-degrade through the base.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerWeekView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// First visible hour of the time grid (0..24, inclusive). Defaults to
    /// <c>SchedulerOptions.Value.DefaultStartHour</c> when null. Must satisfy
    /// <c>0 &lt;= StartHour &lt;= EndHour &lt;= 24</c>.
    /// </summary>
    [Parameter]
    public int? StartHour { get; set; }

    /// <summary>
    /// Last visible hour of the time grid (0..24, exclusive ceiling). Defaults to
    /// <c>SchedulerOptions.Value.DefaultEndHour</c> when null.
    /// </summary>
    [Parameter]
    public int? EndHour { get; set; }

    /// <summary>
    /// Slot duration in minutes; must be one of <c>15</c>, <c>30</c>, or <c>60</c>.
    /// Defaults to <c>SchedulerOptions.Value.DefaultSlotDurationMinutes</c>.
    /// </summary>
    [Parameter]
    public int? SlotDurationMinutes { get; set; }

    /// <summary>
    /// First day of the visible week (FR-04). Defaults to
    /// <c>SchedulerOptions.Value.DefaultFirstDayOfWeek</c> when null.
    /// </summary>
    [Parameter]
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Whether to render a horizontal current-time indicator on today's column when
    /// today (in <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>) is within the
    /// visible week (FR-07). Defaults to <see langword="true"/>.
    /// </summary>
    [Parameter]
    public bool ShowCurrentTimeIndicator { get; set; } = true;

    /// <summary>
    /// Max side-by-side overlap columns before surplus events collapse into a "+N" block.
    /// Defaults to <c>SchedulerOptions.Value.DefaultMaxOverlapColumns</c> when null. Must be &gt;= 2.
    /// </summary>
    [Parameter]
    public int? MaxOverlapColumns { get; set; }

    /// <summary>
    /// Optional render fragment for the *inside* of each timed event card (FR-17).
    /// See ADR-0002 for the library-owned-rectangle/consumer-owned-inside contract.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventTemplate { get; set; }

    /// <summary>Optional class hook applied to each day-header cell (FR-54).</summary>
    [Parameter]
    public string? DayHeaderClass { get; set; }

    /// <summary>Optional class hook applied to the time gutter column (FR-54).</summary>
    [Parameter]
    public string? TimeGutterClass { get; set; }

    /// <summary>Optional class hook applied to the all-day row (FR-54).</summary>
    [Parameter]
    public string? AllDayRowClass { get; set; }

    /// <summary>Injected JS runtime, used for the FR-09b scroll-into-view helper and Escape blur.</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // Resolved values after OnParametersSet.
    private int _resolvedStartHour;
    private int _resolvedEndHour;
    private int _resolvedSlotMinutes;
    private DayOfWeek _resolvedFirstDayOfWeek;
    private int _resolvedMaxOverlapColumns;

    // The 7 visible days, each as a midnight–midnight bound in TimeZone.
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> _weekDays = Array.Empty<(DateTimeOffset, DateTimeOffset)>();

    // Per-day layout result (parallel to _weekDays).
    private LayoutResult[] _layoutPerDay = Array.Empty<LayoutResult>();

    // All-day "bars" — each spans 1+ contiguous day columns. Computed once per render.
    private List<AllDayBar> _allDayBars = new();

    // Frozen-by-construction pre-processed view of the filtered events:
    // owns all-day classification, multi-day per-day chunk splitting, and Id→TEvent lookup.
    private VisibleEventSet<TEvent> _visibleEvents = VisibleEventSet<TEvent>.Empty;

    // Roving-tabindex anchor for the slot grid: column and row coordinates. The grid is a
    // single tab stop from the consumer's perspective (NFR-06).
    private int _focusedColumnIndex;
    private int _focusedRowIndex;

    // For FR-23 — fire OnRangeChanged only when the visible range actually changes.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private ElementReference _hourGridRef;
    private bool _scrollPending;
    private IJSObjectReference? _jsModule;

    /// <summary>
    /// Per-event element refs the drag layer uses as the ghost source (Phase 2 Task 5 — FR-25).
    /// Keyed by event id so a chip whose position in the foreach changes between renders
    /// still resolves to its captured DOM element. The previous array-indexed-by-position
    /// pattern mis-aligned <c>_eventIds</c> and <c>_eventRefs</c> when Blazor's diff reused
    /// a <c>@key</c>-matched chip at a new position without re-firing the <c>@ref</c>
    /// capture for the new slot. A multi-day event chunked across columns shares one
    /// entry — chunks all carry the same id and overwrite the dict slot during render,
    /// which is fine because the drag source is the chunk the user pressed down on.
    /// </summary>
    private readonly Dictionary<string, ElementReference> _eventRefsByEventId = new(StringComparer.Ordinal);

    /// <summary>
    /// Optimistic pins for in-flight or just-completed drag-to-move operations
    /// (ADR-0006). Keyed by the consumer event's authoritative id. The rendering
    /// pipeline replaces all chunks of a pinned event with one synthetic chunk
    /// at the pinned <c>(Start, End)</c> so the new position is visible before
    /// the consumer's data round-trip completes. Cleared in
    /// <see cref="OnParametersSet"/> when the consumer's authoritative times catch up.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End)> _optimisticPin =
        new(StringComparer.Ordinal);

    /// <summary>Number of slots per day between StartHour and EndHour.</summary>
    private int SlotCount => (_resolvedEndHour - _resolvedStartHour) * 60 / _resolvedSlotMinutes;

    /// <summary>Total visible minutes per day in the hour grid (derived).</summary>
    private int VisibleMinutes => (_resolvedEndHour - _resolvedStartHour) * 60;

    /// <summary>Inclusive start of the visible week.</summary>
    private DateTimeOffset WeekStart => _weekDays.Count > 0 ? _weekDays[0].Start : default;

    /// <summary>Exclusive end of the visible week.</summary>
    private DateTimeOffset WeekEndExclusive => _weekDays.Count > 0 ? _weekDays[^1].End : default;

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        var opts = SchedulerOptions.Value;
        _resolvedStartHour = StartHour ?? opts.DefaultStartHour;
        _resolvedEndHour = EndHour ?? opts.DefaultEndHour;
        _resolvedSlotMinutes = SlotDurationMinutes ?? opts.DefaultSlotDurationMinutes;
        _resolvedFirstDayOfWeek = FirstDayOfWeek ?? opts.DefaultFirstDayOfWeek;
        _resolvedMaxOverlapColumns = MaxOverlapColumns ?? opts.DefaultMaxOverlapColumns;

        // PRD §4.6 hard-fail validation. Shared helper throws with the same messages
        // Day view does — keeps consumer-facing diagnostics uniform.
        SchedulerViewPrimitives.ValidateHourParameters(_resolvedStartHour, _resolvedEndHour, _resolvedSlotMinutes);

        _weekDays = SchedulerViewPrimitives.ComputeWeekDays(CurrentDate, _resolvedFirstDayOfWeek, TimeZone);

        // Optimistic-pin housekeeping (ADR-0006). Drop entries the consumer has caught
        // up on — i.e., the consumer's authoritative Start/End for the event now matches
        // the pinned values, so the pin is redundant. Performed before laying out so the
        // engine sees only still-relevant pins.
        ClearAcknowledgedPins();

        ComputeLayout();

        // FR-23: fire OnRangeChanged when the visible range changes.
        if (_lastRangeStart != WeekStart || _lastRangeEnd != WeekEndExclusive)
        {
            _lastRangeStart = WeekStart;
            _lastRangeEnd = WeekEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(WeekStart, WeekEndExclusive));
        }
    }

    /// <summary>
    /// Recompute the all-day bars, per-day timed chunks, and the per-day layout for the
    /// current parameter set. Materialized here so the render path is read-only.
    /// </summary>
    private void ComputeLayout()
    {
        // VisibleEventSet owns the filter→classify→split→lookup pipeline. PerDay split mode
        // gives us one chunk per visible day a multi-day timed event touches.
        _visibleEvents = new VisibleEventSet<TEvent>(
            GetFilteredEvents(),
            WeekStart,
            WeekEndExclusive,
            TimeZone,
            EventSplitMode.PerDay);

        // All-day bars: one bar per event, spanning the contiguous run of visible day
        // columns it covers. Compute the bar's left/right column indices and the
        // continues-past-week clip flags here so the render is pure.
        _allDayBars = new List<AllDayBar>(_visibleEvents.AllDay.Count);
        foreach (var ev in _visibleEvents.AllDay)
        {
            int firstCol = -1, lastCol = -1;
            for (var i = 0; i < _weekDays.Count; i++)
            {
                var (ds, de) = _weekDays[i];
                if (ev.End > ds && ev.Start < de)
                {
                    if (firstCol < 0) firstCol = i;
                    lastCol = i;
                }
            }
            if (firstCol < 0) continue; // Shouldn't happen — VisibleEventSet pre-filters by range overlap.

            var clipLeft = ev.Start < _weekDays[firstCol].Start;
            var clipRight = ev.End > _weekDays[lastCol].End;
            _allDayBars.Add(new AllDayBar(ev, firstCol, lastCol, clipLeft, clipRight));
        }

        // Apply any optimistic-pin overrides (ADR-0006) before per-day bucketing so a
        // pinned event's chunks rebucket to its new day column. ApplyOptimisticPins
        // collapses all chunks of a pinned event into a single synthetic chunk with
        // the pinned Start/End; under PerDay split the consumer's catch-up will
        // re-split the event correctly once it acknowledges the move.
        IReadOnlyList<EventChunk<TEvent>> chunksForLayout = _visibleEvents.TimedChunks;
        if (_optimisticPin.Count > 0)
        {
            chunksForLayout = ApplyOptimisticPins(_visibleEvents.TimedChunks);
        }

        // Per-day buckets for the timed chunks. VisibleEventSet has already split multi-day
        // events into per-day chunks; group them by which week-day column their Start falls in.
        _layoutPerDay = new LayoutResult[_weekDays.Count];
        var perDayChunks = new List<ICalendarEvent>[_weekDays.Count];
        for (var i = 0; i < _weekDays.Count; i++) perDayChunks[i] = new List<ICalendarEvent>();

        foreach (var chunk in chunksForLayout)
        {
            // Locate the chunk's day column. We linear-scan since 7 is tiny.
            for (var i = 0; i < _weekDays.Count; i++)
            {
                if (chunk.Start >= _weekDays[i].Start && chunk.Start < _weekDays[i].End)
                {
                    perDayChunks[i].Add(chunk);
                    break;
                }
            }
        }

        var engine = new EventLayoutEngine();
        for (var i = 0; i < _weekDays.Count; i++)
        {
            // The engine accepts ICalendarEvent — pass the chunks directly.
            _layoutPerDay[i] = engine.Layout(
                perDayChunks[i],
                _weekDays[i].Start,
                _weekDays[i].End,
                _resolvedStartHour,
                _resolvedEndHour,
                _resolvedMaxOverlapColumns);
        }
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);
            _scrollPending = true;
        }

        if (_scrollPending && _jsModule is not null)
        {
            _scrollPending = false;
            try
            {
                var hourOffset = SchedulerViewPrimitives.ComputeInitialScrollHourOffset(
                    Today, WeekStart, WeekEndExclusive, _resolvedStartHour, _resolvedEndHour);
                await _jsModule.InvokeVoidAsync("scrollToHour", _hourGridRef, hourOffset);
            }
            catch (JSException) { /* Non-fatal — see Day view note. */ }
            catch (InvalidOperationException) { /* No JS runtime in tests. */ }
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

    /// <summary>Hour labels (inclusive of StartHour, exclusive of EndHour).</summary>
    internal IEnumerable<int> HourLabels()
    {
        for (var h = _resolvedStartHour; h < _resolvedEndHour; h++)
        {
            yield return h;
        }
    }

    /// <summary>Slot rows per column.</summary>
    internal int SlotCountForRender => SlotCount;

    /// <summary>Column count (always 7 — week view).</summary>
    internal int ColumnCount => _weekDays.Count;

    /// <summary>Convert hour-of-day into a percentage of the visible vertical band.</summary>
    internal double HourToPercent(int hour) =>
        VisibleMinutes <= 0 ? 0 : (hour - _resolvedStartHour) * 60.0 / VisibleMinutes * 100.0;

    /// <summary>Visible days (date-only for headers).</summary>
    internal IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> WeekDays => _weekDays;

    /// <summary>
    /// Accessible name for the slot at (col, row): "&lt;weekday&gt;, &lt;time&gt;, empty slot" in
    /// the configured time zone. Lets screen-reader users tabbing to a slot hear which day +
    /// hour they're on instead of just "gridcell."
    /// </summary>
    internal string SlotAccessibleName(int col, int row)
    {
        var day = _weekDays[col].Start;
        var minutes = row * _resolvedSlotMinutes;
        var time = day.Date.AddHours(_resolvedStartHour).AddMinutes(minutes);
        return $"{day:dddd}, {time:h:mm tt}, empty slot";
    }

    /// <summary>Format an hour-of-day for the time gutter.</summary>
    internal static string FormatHour(int hour) => SchedulerViewPrimitives.FormatHour(hour);

    /// <summary>Format an event's start/end as an accessible time range in TimeZone.</summary>
    internal string FormatEventTimeRange(ICalendarEvent ev) =>
        SchedulerViewPrimitives.FormatEventTimeRange(ev, TimeZone);

    /// <summary>True when the supplied column index is "today in TimeZone".</summary>
    internal bool IsTodayColumn(int colIndex) =>
        colIndex >= 0 && colIndex < _weekDays.Count
        && Today.Date == _weekDays[colIndex].Start.Date;

    /// <summary>True when the current-time indicator should render on the supplied column.</summary>
    internal bool ShouldRenderCurrentTimeIndicator(int colIndex) =>
        ShowCurrentTimeIndicator && IsTodayColumn(colIndex);

    /// <summary>Top percentage for the current-time indicator on today's column.</summary>
    internal double CurrentTimeIndicatorPercent
    {
        get
        {
            var now = Today;
            var minutesIntoBand = (now.Hour - _resolvedStartHour) * 60 + now.Minute;
            if (minutesIntoBand < 0 || minutesIntoBand > VisibleMinutes) return -1;
            return VisibleMinutes == 0 ? 0 : (double)minutesIntoBand / VisibleMinutes * 100.0;
        }
    }

    /// <summary>The all-day bars to render across the all-day row.</summary>
    internal IReadOnlyList<AllDayBar> AllDayBars => _allDayBars;

    /// <summary>Positioned timed events for the supplied column.</summary>
    internal IReadOnlyList<PositionedEvent> PositionedEventsFor(int colIndex) =>
        _layoutPerDay[colIndex].Positioned;

    /// <summary>Overlap-overflow blocks for the supplied column.</summary>
    internal IReadOnlyList<OverlapOverflowBlock> OverlapBlocksFor(int colIndex) =>
        _layoutPerDay[colIndex].OverlapOverflow;

    /// <summary>Count of events entirely earlier than StartHour for the supplied column.</summary>
    internal int EarlierOverflowCountFor(int colIndex) =>
        _layoutPerDay[colIndex].EarlierOverflow.Count;

    /// <summary>Count of events entirely later than EndHour for the supplied column.</summary>
    internal int LaterOverflowCountFor(int colIndex) =>
        _layoutPerDay[colIndex].LaterOverflow.Count;

    /// <summary>True when any column has an earlier-overflow chip.</summary>
    internal bool AnyEarlierOverflow
    {
        get
        {
            for (var i = 0; i < _layoutPerDay.Length; i++)
            {
                if (_layoutPerDay[i].EarlierOverflow.Count > 0) return true;
            }
            return false;
        }
    }

    /// <summary>True when any column has a later-overflow chip.</summary>
    internal bool AnyLaterOverflow
    {
        get
        {
            for (var i = 0; i < _layoutPerDay.Length; i++)
            {
                if (_layoutPerDay[i].LaterOverflow.Count > 0) return true;
            }
            return false;
        }
    }

    /// <summary>True when the positioned event was clipped at the top (either source).</summary>
    internal bool ClippedTop(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtStart(pe);

    /// <summary>True when the positioned event was clipped at the bottom (either source).</summary>
    internal bool ClippedBottom(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtEnd(pe);

    /// <summary>Returns the underlying TEvent for a positioned event (unwraps EventChunk).</summary>
    internal TEvent? TypedFor(PositionedEvent pe) => _visibleEvents.FindById(pe.Event.Id);

    /// <summary>Map overflow chunks back to the consumer's TEvent (deduped by id), dropping any that no longer resolve.</summary>
    private IReadOnlyList<TEvent> MapToTyped(IReadOnlyList<ICalendarEvent> events)
    {
        if (events.Count == 0) return Array.Empty<TEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TEvent>(events.Count);
        foreach (var e in events)
        {
            if (!seen.Add(e.Id)) continue;
            var typed = _visibleEvents.FindById(e.Id);
            if (typed is not null) result.Add(typed);
        }
        return result;
    }

    /// <summary>Returns the underlying TEvent by Id, or default if not found.</summary>
    internal TEvent? TypedForId(string id) => _visibleEvents.FindById(id);

    /// <summary>Returns the underlying consumer event (unwraps an <see cref="EventChunk{TEvent}"/>) for formatting.</summary>
    internal ICalendarEvent UnwrapForFormatting(PositionedEvent pe) =>
        pe.Event is EventChunk<TEvent> c ? c.Event : pe.Event;

    /// <summary>Returns the consumer-supplied CSS class for an event (via base helper).</summary>
    internal string? ClassFor(TEvent ev) => GetEventClass(ev);

    /// <summary>The weekday of the column (used to build accessible names for events).</summary>
    internal string WeekdayOf(int colIndex) => _weekDays[colIndex].Start.ToString("dddd");

    // ----- Event handlers ------------------------------------------------------------

    /// <summary>Fire OnEventClicked with the original consumer TEvent (unwrapping any chunk) and update the selection.</summary>
    /// <remarks>
    /// Selection mutation happens only on real pointer clicks (<paramref name="args"/>
    /// non-null) so the Phase 1 keyboard Enter path is unchanged here — Space-toggle
    /// and the other selection-keyboard bindings wire up in Task 11 (FR-34 keyboard).
    /// Render order for Shift+click range select is: all-day bars (left-to-right,
    /// top-to-bottom by lane index) first, then per-column timed events column-major
    /// (col 0 top→bottom, col 1 top→bottom, …) — matches DOM order in the .razor
    /// markup, which is what users perceive when Shift+clicking across columns.
    /// </remarks>
    internal Task HandleEventClickAsync(ICalendarEvent ev, MouseEventArgs? args = null)
    {
        // EventChunk<TEvent>.Id forwards to its underlying consumer event, so FindById
        // resolves both wrapped chunks and unwrapped events uniformly.
        var typed = _visibleEvents.FindById(ev.Id);
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
        // All-day bars (multi-day events appear once at their start column) followed by
        // per-column timed events in column-major (col 0 events, col 1 events, …).
        // ApplyClickSelectionAsync dedupes on first occurrence, so a multi-day chunk
        // that appears across columns is counted by its earliest visible position.
        var allDayBars = _allDayBars;
        var ids = new List<string>(allDayBars.Count + 16);
        for (var i = 0; i < allDayBars.Count; i++) ids.Add(allDayBars[i].Event.Id);
        for (var c = 0; c < ColumnCount; c++)
        {
            var positioned = PositionedEventsFor(c);
            for (var i = 0; i < positioned.Count; i++) ids.Add(positioned[i].Event.Id);
        }
        return ids;
    }

    /// <summary>Fire OnDayOverflowClicked for a "+N earlier" chip on the supplied column.</summary>
    internal Task HandleEarlierChipClickAsync(int colIndex)
    {
        var day = DateOnly.FromDateTime(_weekDays[colIndex].Start.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Earlier, MapToTyped(_layoutPerDay[colIndex].EarlierOverflow)));
    }

    /// <summary>Fire OnDayOverflowClicked for a "+N later" chip on the supplied column.</summary>
    internal Task HandleLaterChipClickAsync(int colIndex)
    {
        var day = DateOnly.FromDateTime(_weekDays[colIndex].Start.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Later, MapToTyped(_layoutPerDay[colIndex].LaterOverflow)));
    }

    /// <summary>Fire OnDayOverflowClicked for a "+N" overlap block on the supplied column.</summary>
    internal Task HandleOverlapChipClickAsync(int colIndex, OverlapOverflowBlock block)
    {
        var day = DateOnly.FromDateTime(_weekDays[colIndex].Start.Date);
        return OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            day, OverflowKind.Overlap, MapToTyped(block.Events),
            RegionStart: block.RegionStart, RegionEnd: block.RegionEnd));
    }

    /// <summary>Fire OnSlotClicked for a clicked slot in a specific day column (FR-21).
    /// After the callback resolves, removes focus from any focused event chip so
    /// the "clicking off an event clears its focus ring" mental model holds even
    /// when the clicked slot has tabindex=-1.</summary>
    internal async Task HandleSlotClickAsync(int colIndex, int slotIndex)
    {
        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var endMinutes = startMinutes + _resolvedSlotMinutes;
        var start = _weekDays[colIndex].Start.AddMinutes(startMinutes);
        var end = _weekDays[colIndex].Start.AddMinutes(endMinutes);
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(start, end));
        await BlurActiveEventChipAsync();
    }

    /// <summary>
    /// Keyboard handler on the hour-grid. Arrows move the focused (col, row); Enter
    /// fires the slot click; Escape either clears a non-empty selection (Phase 2
    /// Task 11 — FR-34) or blurs (FR-30 fallback for the empty-selection case).
    /// </summary>
    internal async Task HandleGridKeyDownAsync(KeyboardEventArgs e)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch (FR-36). Replaces
        // Task 13's TryDispatchUndoRedoAsync — the new dispatch covers undo/redo plus
        // the rest of the resolved (DefaultMap + DisabledShortcuts + ShortcutMap) map.
        // IsDragActive precedence unchanged.
        if (IsDragActive) return;
        if (await TryDispatchShortcutAsync(e, KeystrokeScope.Grid)) return;

        switch (e.Key)
        {
            case "ArrowDown":
                _focusedRowIndex = Math.Min(SlotCount - 1, _focusedRowIndex + 1);
                StateHasChanged();
                break;
            case "ArrowUp":
                _focusedRowIndex = Math.Max(0, _focusedRowIndex - 1);
                StateHasChanged();
                break;
            case "ArrowRight":
                _focusedColumnIndex = Math.Min(ColumnCount - 1, _focusedColumnIndex + 1);
                StateHasChanged();
                break;
            case "ArrowLeft":
                _focusedColumnIndex = Math.Max(0, _focusedColumnIndex - 1);
                StateHasChanged();
                break;
            case "Enter":
                await HandleSlotClickAsync(_focusedColumnIndex, _focusedRowIndex);
                break;
        }
    }

    /// <summary>
    /// Keyboard handler on an event card. Enter fires <c>OnEventClicked</c> via the
    /// existing keyboard-Enter path (no selection mutation — matches the Phase 1
    /// contract). Space toggles the focused chip in/out of the selection when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> is enabled
    /// (FR-34 keyboard); when disabled it returns without preventing the browser
    /// default so the synthesized click drives a single-id selection through the
    /// existing click path (FR-29 fail-closed). Escape clears a non-empty selection
    /// (Task 11) or falls through to the FR-30 blur behavior when empty. See the
    /// Day view's <c>HandleEventKeyDownAsync</c> remarks for the precedence rules
    /// (Esc-mid-drag belongs to the JS drag module; Space's browser-default
    /// suppression is wired via <c>@onkeydown:preventDefault</c> bound to
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/>).
    /// </summary>
    internal async Task HandleEventKeyDownAsync(KeyboardEventArgs e, ICalendarEvent ev)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch. See Day view's
        // matching branch for the longer rationale.
        if (IsDragActive) return;
        var typed = _visibleEvents.FindById(ev.Id);
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
                var underlying = _visibleEvents.FindById(focusedEventId) as ICalendarEvent;
                if (underlying is null) return false;
                await HandleEventClickAsync(underlying, new MouseEventArgs { CtrlKey = true });
                return true;
            case SchedulerCommandIds.EditDelete:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null || focusedEvent is null) return false;
                var ev = _visibleEvents.FindById(focusedEventId) as ICalendarEvent;
                if (ev is null) return false;
                await HandleDeleteAsync(ev);
                return true;
            case SchedulerCommandIds.Cancel:
                await HandleEscapeAsync();
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shared Delete behavior — mirrors the per-view helper on Day: short-circuits
    /// on <see cref="SchedulerComponentBase{TEvent}.AllowDelete"/> + <c>IsDragActive</c>,
    /// resolves the focused chip to a typed consumer event, dispatches through the
    /// base's <see cref="SchedulerComponentBase{TEvent}.TryDeleteFocusedEventAsync"/>
    /// helper.
    /// </summary>
    private async Task HandleDeleteAsync(ICalendarEvent ev)
    {
        if (!AllowDelete) return;
        if (IsDragActive) return;

        var typed = _visibleEvents.FindById(ev.Id);
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
    /// Shared Escape behavior — mirrors the per-view helper on Day: defer to JS
    /// mid-drag (ADR-0006), clear non-empty selection (FR-34 keyboard), otherwise
    /// blur (FR-30).
    /// </summary>
    private async Task HandleEscapeAsync()
    {
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

    /// <summary>Returns true when the supplied (column, row) is the currently-tabbable cell.</summary>
    internal bool IsSlotTabbable(int colIndex, int rowIndex) =>
        colIndex == _focusedColumnIndex && rowIndex == _focusedRowIndex;

    /// <summary>Build the accessible name for a positioned event in the supplied column.</summary>
    internal string EventAccessibleName(PositionedEvent pe, int colIndex)
    {
        var ev = UnwrapForFormatting(pe);
        var weekday = WeekdayOf(colIndex);
        return $"{ev.Title}, {FormatEventTimeRange(ev)}, {weekday}";
    }

    /// <summary>Build the accessible name for an all-day bar.</summary>
    internal string AllDayAccessibleName(AllDayBar bar)
    {
        if (bar.FirstColIndex == bar.LastColIndex)
        {
            var d = _weekDays[bar.FirstColIndex].Start;
            return $"{bar.Event.Title}, all day on {d:MMM d}";
        }
        var s = _weekDays[bar.FirstColIndex].Start;
        var e = _weekDays[bar.LastColIndex].Start;
        return $"{bar.Event.Title}, all day from {s:MMM d} to {e:MMM d}";
    }

    /// <summary>Build the accessible name for an overflow chip.</summary>
    internal string OverflowChipAccessibleName(int colIndex, int count, OverflowKind kind)
    {
        var weekday = WeekdayOf(colIndex);
        var when = kind == OverflowKind.Earlier ? "earlier" : "later";
        return $"{count} events {when} on {weekday}";
    }

    /// <summary>Accessible name for a "+N" overlap block.</summary>
    internal string OverlapBlockAccessibleName(int colIndex, OverlapOverflowBlock block)
    {
        var weekday = WeekdayOf(colIndex);
        var start = TimeZoneInfo.ConvertTime(block.RegionStart, TimeZone);
        var end = TimeZoneInfo.ConvertTime(block.RegionEnd, TimeZone);
        return $"{block.Events.Count} more events from {start:h:mm tt} to {end:h:mm tt} on {weekday}, activate to choose";
    }

    private async Task BlurActiveAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActive"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    /// <summary>Blur the active element only if it is an event chip
    /// (data-calee-region="event"). Used by the slot-click handler.</summary>
    private async Task BlurActiveEventChipAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActiveIfEvent"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    // ----- Drag-to-move (Phase 2 Task 5 — FR-25) --------------------------------------

    /// <summary>
    /// Return a copy of <paramref name="chunks"/> where every chunk belonging to a pinned
    /// event is removed and replaced with one synthetic chunk at the pinned <c>(Start, End)</c>.
    /// Non-pinned chunks pass through unchanged. The returned list is freshly allocated only
    /// when at least one pin matches; otherwise the input list is returned as-is.
    /// </summary>
    /// <remarks>
    /// Collapsing all chunks of a pinned event into a single chunk is the right shape for
    /// the common single-day-move case (the typical Week-view drag). A multi-day event
    /// being moved temporarily appears as a single chunk at the pinned times; the
    /// consumer's catch-up restores correct PerDay splitting once it acknowledges the move.
    /// </remarks>
    private IReadOnlyList<EventChunk<TEvent>> ApplyOptimisticPins(IReadOnlyList<EventChunk<TEvent>> chunks)
    {
        // First sweep: detect whether any chunk is pinned (cheap path: most renders nothing).
        var pinnedIds = (HashSet<string>?)null;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (_optimisticPin.ContainsKey(chunks[i].Id))
            {
                pinnedIds ??= new HashSet<string>(StringComparer.Ordinal);
                pinnedIds.Add(chunks[i].Id);
            }
        }
        if (pinnedIds is null)
        {
            return chunks;
        }

        var rebuilt = new List<EventChunk<TEvent>>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (!pinnedIds.Contains(c.Id))
            {
                rebuilt.Add(c);
            }
        }
        // Emit one synthetic chunk per pinned id, with the pinned Start/End. Clip flags
        // are reset — the pinned event is treated as a fresh single-day chunk until the
        // consumer acknowledges and proper PerDay splitting reasserts itself.
        foreach (var id in pinnedIds)
        {
            var typed = _visibleEvents.FindById(id);
            if (typed is null) continue;
            var pin = _optimisticPin[id];
            rebuilt.Add(new EventChunk<TEvent>(typed, pin.Start, pin.End, ClippedAtTimeStart: false, ClippedAtTimeEnd: false));
        }
        return rebuilt;
    }

    /// <summary>
    /// Drop pin entries whose pinned (Start, End) matches the consumer-supplied
    /// authoritative times — i.e., the consumer has accepted the move and pushed
    /// the new times back through <see cref="SchedulerComponentBase{TEvent}.Events"/>.
    /// Pins that haven't been acknowledged remain (the optimistic state is still
    /// the more up-to-date view of the world).
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

    /// <summary>The dictionary the .razor template binds via
    /// <c>@ref="EventRefsByEventId[eventId]"</c>.</summary>
    internal Dictionary<string, ElementReference> EventRefsByEventId => _eventRefsByEventId;

    /// <summary>
    /// Returns the optimistic-pin (Start, End) for the supplied event id if one is
    /// set; otherwise null. The .razor template uses this to display the pinned
    /// time-range label even though the consumer's authoritative TEvent has stale
    /// times until the data round-trip completes.
    /// </summary>
    internal (DateTimeOffset Start, DateTimeOffset End)? GetOptimisticPin(string id) =>
        _optimisticPin.TryGetValue(id, out var pin) ? pin : null;

    /// <summary>
    /// Pointer-down handler attached to each event chip when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is true.
    /// Starts a drag-to-move via the base's <c>BeginDragOnPointerAsync</c>;
    /// the drop branch routes to <see cref="HandleMoveDropAsync"/>. The cancel
    /// branch is a no-op per ADR-0006 — mid-drag cancel never pinned anything.
    /// </summary>
    internal async Task OnEventPointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToMove) return;
        // Only primary button (left mouse / first touch) starts a drag (PointerEvent spec:
        // Button==0 is primary; touch always reports 0). Filters out right/middle/auxiliary.
        if (e.Button != 0) return;

        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null) return;

        // Look up the chip's element ref by event id. Multi-day events with multiple chunks
        // share one dict entry (whichever chunk renders last overwrites the slot, which is
        // fine — JS only needs *some* element to clone for the ghost).
        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var gridWidthPx = await GetHourGridWidthPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;
        var snapPixelsX = (gridWidthPx > 0 && ColumnCount > 0) ? gridWidthPx / ColumnCount : 0;

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
    /// Drop handler. Converts the JS drop delta (X for day-column crossing, Y for
    /// time-of-day) into a new (Start, End) preserving the event's duration,
    /// optimistically pins the new position, fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnEventMoved"/>, and rolls the
    /// pin back if the consumer set <see cref="EventMoveContext.Cancel"/>.
    /// </summary>
    private async Task HandleMoveDropAsync(TEvent ev, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        var gridWidthPx = await GetHourGridWidthPxAsync();
        if (gridHeightPx <= 0)
        {
            // Fallback geometry for test environments without a real DOM. 56 px/hour matches
            // the default --calee-scheduler-pixels-per-hour CSS variable; total height covers
            // the visible band. Production always hits the JS-measured path.
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }
        if (gridWidthPx <= 0)
        {
            // Synthesize a column width that makes one column = one slot wide vertically.
            // The actual value doesn't matter for tests — only the *ratio* of DeltaXPx /
            // (gridWidthPx / ColumnCount) determines the day-shift. 700px (100/col) is a
            // reasonable default and keeps test-supplied DeltaXPx easy to reason about.
            gridWidthPx = 700.0;
        }

        // 1) Time-of-day axis: same delta-Y trick as Day view. Compute the chunk's pre-drop
        //    top inside the grid from its start time, add DeltaYPx, then InverseY against
        //    a *single* day's visible band (the grid is one day tall vertically — the
        //    7 columns are just X-axis days, not vertically-stacked days).
        var origDayIndex = FindColumnIndex(ev.Start);
        if (origDayIndex < 0)
        {
            // Drag was started before the consumer pushed the event into the visible week
            // (defensive guard — pointer-down wouldn't have fired otherwise, but be safe).
            return;
        }

        var origDayStart = _weekDays[origDayIndex].Start;
        var visibleDayStart = origDayStart.AddHours(_resolvedStartHour);
        var visibleDayEnd = origDayStart.AddHours(_resolvedEndHour);

        var origStartMinutes = (ev.Start - visibleDayStart).TotalMinutes;
        var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridHeightPx;
        var newStartPxInGrid = (origStartMinutes / minutesPerPx) + payload.DeltaYPx;

        // 2) Day-column axis: round DeltaXPx to whole-column shifts, clamped to [0, ColumnCount-1].
        //    The Y inverse maps to a time-of-day on the *new* day column.
        var colWidth = gridWidthPx / ColumnCount;
        var dayShift = colWidth > 0
            ? (int)Math.Round(payload.DeltaXPx / colWidth, MidpointRounding.AwayFromZero)
            : 0;
        var newDayIndex = Math.Clamp(origDayIndex + dayShift, 0, ColumnCount - 1);
        var targetDayStart = _weekDays[newDayIndex].Start;
        var targetVisibleStart = targetDayStart.AddHours(_resolvedStartHour);
        var targetVisibleEnd = targetDayStart.AddHours(_resolvedEndHour);

        var snappedStart = EventLayoutEngine.InverseY(
            pixelY: newStartPxInGrid,
            totalHeightPx: gridHeightPx,
            rangeStart: targetVisibleStart,
            rangeEndExclusive: targetVisibleEnd,
            slotMinutes: _resolvedSlotMinutes);

        var duration = ev.End - ev.Start;
        var newStart = snappedStart;
        var newEnd = newStart + duration;

        // Optimistic pin: apply visually before consumer commits (ADR-0006).
        _optimisticPin[ev.Id] = (newStart, newEnd);
        ComputeLayout();
        StateHasChanged();

        var context = new EventMoveContext
        {
            Event = ev,
            NewStart = newStart,
            NewEnd = newEnd,
            // NewLaneId stays null — Week view has no lanes (ADR-0011).
        };
        await OnEventMoved.InvokeAsync(context);

        if (context.Cancel)
        {
            _optimisticPin.Remove(ev.Id);
            ComputeLayout();
            StateHasChanged();
        }
        // If not canceled, the pin remains until the consumer's authoritative event
        // catches up — ClearAcknowledgedPins drops it on the next OnParametersSet.
    }

    /// <summary>
    /// Locate the day-column index whose visible-window bounds contain the supplied
    /// <see cref="DateTimeOffset"/>. Returns <c>-1</c> when the value falls outside
    /// the visible week (which shouldn't happen for a chip the user can press, but
    /// the drop handler guards against this defensively).
    /// </summary>
    private int FindColumnIndex(DateTimeOffset value)
    {
        for (var i = 0; i < _weekDays.Count; i++)
        {
            if (value >= _weekDays[i].Start && value < _weekDays[i].End)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Query the hour-grid container's real on-screen height via the JS helper.
    /// Returns 0 in test environments without a real DOM; callers fall back to a
    /// default-derived geometry in that case.
    /// </summary>
    private async Task<double> GetHourGridHeightPxAsync()
    {
        if (_jsModule is null) return 0;
        try
        {
            return await _jsModule.InvokeAsync<double>("getElementHeight", _hourGridRef);
        }
        catch (JSException) { return 0; }
        catch (JSDisconnectedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>
    /// Query the hour-grid container's real on-screen width via the JS helper.
    /// Returns 0 in test environments without a real DOM; callers fall back to a
    /// default geometry in that case.
    /// </summary>
    private async Task<double> GetHourGridWidthPxAsync()
    {
        if (_jsModule is null) return 0;
        try
        {
            return await _jsModule.InvokeAsync<double>("getElementWidth", _hourGridRef);
        }
        catch (JSException) { return 0; }
        catch (JSDisconnectedException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>
    /// Test-only entry point for the drop-handling pipeline. Lets the test project
    /// exercise the optimistic-pin + callback flow without driving a real
    /// pointer-drag sequence through JS interop (which bUnit's headless DOM cannot
    /// produce). Visibility is <see langword="internal"/>; the test assembly sees
    /// it via <c>InternalsVisibleTo</c>.
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
    /// Pointer-down handler attached to the bottom-edge resize hit-zone of each event
    /// chip when <see cref="SchedulerComponentBase{TEvent}.AllowDragToResize"/> is true.
    /// Starts a resize-end drag with <see cref="ResizeAxis.Y"/>; only the bottom edge
    /// of the ghost moves (top is anchored to the original event Start). The drop
    /// branch routes to <see cref="HandleResizeDropAsync"/>; the cancel branch is a
    /// no-op per ADR-0006.
    /// </summary>
    internal async Task OnEventResizePointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToResize) return;
        if (e.Button != 0) return;

        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null) return;

        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.ResizeEnd,
            snapPixelsX: 0,                      // Week view doesn't resize across columns.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleResizeDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y);
    }

    /// <summary>
    /// Drop handler for the resize-end drag. Same shape as Day view: convert DeltaYPx
    /// into a new <c>End</c> snapped to the slot boundary, preserve <c>Start</c>,
    /// pin optimistically, fire <see cref="SchedulerComponentBase{TEvent}.OnEventResized"/>,
    /// roll back on cancel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Multi-day events under PerDay split appear as multiple chunks across the visible
    /// week. The resize handle on any chunk resizes the *underlying event's* End time
    /// (lookup is by id via <see cref="VisibleEventSet{TEvent}.FindById"/>); the pin keys
    /// by the underlying event id, identical to drag-to-move. The Week view's
    /// <c>ApplyOptimisticPins</c> collapses all chunks of a pinned event into a single
    /// chunk at the pinned <c>(Start, End)</c> for as long as the pin is active — the
    /// consumer's catch-up restores proper PerDay splitting via <see cref="VisibleEventSet{TEvent}"/>.
    /// </para>
    /// <para>
    /// <strong>Minimum-duration clamp.</strong> When the user drags past <c>Start</c>,
    /// the new End is clamped to <c>Start + SlotDurationMinutes</c>. Resize cannot
    /// invert the event's range — the library guarantees <c>NewEnd &gt; Start</c>.
    /// </para>
    /// <para>
    /// <strong>End-of-band clamp.</strong> Dragging past the visible band's
    /// <c>EndHour</c> clamps to the band end (which on a single day equals the bottom of
    /// the visible band). The resize stays inside the original day column — Week view's
    /// resize is single-axis (vertical-time only); cross-day-column resize is not in
    /// scope for FR-26.
    /// </para>
    /// </remarks>
    private async Task HandleResizeDropAsync(TEvent ev, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }

        // Resize stays in the original day column — the bottom-edge drag is purely
        // vertical (snapPixelsX = 0 above so DeltaXPx is unsnapped, but we don't use
        // it). Recover the day index from the event's own Start so we have the right
        // band bounds for the End clamp.
        var dayIndex = FindColumnIndex(ev.Start);
        if (dayIndex < 0)
        {
            return; // Defensive — pointer-down wouldn't fire for an unrendered chip.
        }
        var origDayStart = _weekDays[dayIndex].Start;
        var visibleStart = origDayStart.AddHours(_resolvedStartHour);
        var visibleEnd = origDayStart.AddHours(_resolvedEndHour);

        var origEndMinutes = (ev.End - visibleStart).TotalMinutes;
        var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridHeightPx;
        var newEndPxInGrid = (origEndMinutes / minutesPerPx) + payload.DeltaYPx;

        var totalMinutes = (visibleEnd - visibleStart).TotalMinutes;
        var minutesFromStartUnclamped = newEndPxInGrid / gridHeightPx * totalMinutes;
        var snappedMinutes = Math.Round(
            minutesFromStartUnclamped / _resolvedSlotMinutes,
            MidpointRounding.AwayFromZero) * _resolvedSlotMinutes;
        if (snappedMinutes > totalMinutes) snappedMinutes = totalMinutes;
        if (snappedMinutes < 0) snappedMinutes = 0;

        var newEnd = visibleStart.AddMinutes(snappedMinutes);
        var minEnd = ev.Start.AddMinutes(_resolvedSlotMinutes);
        if (newEnd < minEnd)
        {
            // Minimum-duration clamp: NewEnd > Start by exactly one slot.
            newEnd = minEnd;
        }

        // Share the move-mode pin slot — whichever drag last touched a per-event pin
        // wins, with ClearAcknowledgedPins handling acknowledge.
        _optimisticPin[ev.Id] = (ev.Start, newEnd);
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

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) -------------------------------------

    /// <summary>
    /// Pointer-down handler bound to each slot cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToCreate"/> is true. Starts a
    /// <see cref="DragMode.CreateRegion"/> drag anchored at the slot's (col, row),
    /// growing vertically as the cursor moves. The drag stays within the anchor's day
    /// column (lane axis is locked — even if the cursor wanders into the adjacent
    /// column, the resulting Start/End live on the anchor's date). Below the 5 px
    /// threshold the JS module fires <c>onCancel</c> and the slot's own <c>@onclick</c>
    /// continues to drive <c>OnSlotClicked</c>.
    /// </summary>
    internal async Task OnGridPointerDownAsync(PointerEventArgs e, int anchorColIndex, int anchorSlotIndex)
    {
        if (!AllowDragToCreate) return;
        if (e.Button != 0) return;

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;

        await BeginDragOnPointerAsync(
            e,
            _hourGridRef,
            DragMode.CreateRegion,
            snapPixelsX: 0,                      // Lane axis locked to anchor column.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleCreateDropAsync(anchorColIndex, anchorSlotIndex, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            anchorViewportX: e.ClientX,
            anchorViewportY: e.ClientY,
            thresholdPx: 5,
            // Bound the ghost to the anchor day column. _hourGridRef spans all 7 days;
            // without this slice the ghost would draw across the entire week width.
            crossAxisIndex: anchorColIndex,
            crossAxisDivisions: ColumnCount);
    }

    /// <summary>
    /// Drop handler for a drag-to-create on Week view. Computes the spanned
    /// <c>(Start, End)</c> on the anchor's day column (lane axis is locked — DeltaXPx
    /// is intentionally ignored), fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/>, then exits.
    /// Bidirectional drag normalized via <c>min(anchor, final)</c> /
    /// <c>max(anchor, final)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Anchor column locked.</strong> Even if the cursor crosses into the
    /// adjacent day column mid-drag, the result stays on the anchor's date. This
    /// matches the Google Calendar week-view UX where dragging out a new event
    /// stays in one day; cross-day creates require a separate gesture.
    /// </para>
    /// <para>
    /// <strong>No optimistic phantom.</strong> Option A per the Task 8 lifecycle
    /// decision (see commit body); consumer-owned visual.
    /// </para>
    /// </remarks>
    private async Task HandleCreateDropAsync(int anchorColIndex, int anchorSlotIndex, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }
        var slotHeightPx = gridHeightPx / SlotCount;

        var slotShift = slotHeightPx > 0
            ? (int)Math.Round(payload.DeltaYPx / slotHeightPx, MidpointRounding.AwayFromZero)
            : 0;
        var finalSlotIndex = Math.Clamp(anchorSlotIndex + slotShift, 0, SlotCount - 1);

        var startSlot = Math.Min(anchorSlotIndex, finalSlotIndex);
        var endSlot = Math.Max(anchorSlotIndex, finalSlotIndex) + 1;
        if (endSlot > SlotCount) endSlot = SlotCount;

        var dayStart = _weekDays[anchorColIndex].Start;
        var startMinutes = _resolvedStartHour * 60 + startSlot * _resolvedSlotMinutes;
        var endMinutes = _resolvedStartHour * 60 + endSlot * _resolvedSlotMinutes;
        var start = dayStart.AddMinutes(startMinutes);
        var end = dayStart.AddMinutes(endMinutes);

        var context = new EventCreateContext
        {
            // LaneId stays null — Week view has no lanes (ADR-0011).
            Slot = new SchedulerSlot(start, end),
        };
        await OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the create drop-handling pipeline. Lets the test project
    /// exercise the callback flow without driving a real pointer-drag sequence. Mirrors
    /// <see cref="InvokeMoveDropForTestAsync"/> / <see cref="InvokeResizeDropForTestAsync"/>.
    /// </summary>
    internal Task InvokeCreateDropForTestAsync(int anchorColIndex, int anchorSlotIndex, DropPayload payload) =>
        HandleCreateDropAsync(anchorColIndex, anchorSlotIndex, payload);

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -----------------------------

    /// <summary>
    /// Double-click handler bound to each slot cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDoubleClickToCreate"/> is true.
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/> with a
    /// <see cref="SchedulerSlot"/> spanning <c>(slotStart, slotStart + defaultDuration)</c>
    /// in the clicked day column; <c>defaultDuration</c> resolves per
    /// <see cref="Extensions.CaleeSchedulerOptions.DefaultCreateDurationMinutes"/> — defaulting to
    /// one <c>SlotDurationMinutes</c> for Week view (a time-grid view). The proposed End
    /// is clamped to the visible band end (<c>EndHour</c>). Same lifecycle (no optimistic
    /// phantom event) as drag-to-create per ADR-0006.
    /// </summary>
    /// <param name="colIndex">The day-column index that was double-clicked.</param>
    /// <param name="slotIndex">The slot index within that column.</param>
    internal Task HandleDoubleClickCreateAsync(int colIndex, int slotIndex)
    {
        if (!AllowDoubleClickToCreate) return Task.CompletedTask;

        var durationMinutes = ResolveDefaultCreateDurationMinutes(
            slotDurationMinutes: _resolvedSlotMinutes,
            useWholeDayDefault: false);

        var dayStart = _weekDays[colIndex].Start;
        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var bandEndMinutes = _resolvedEndHour * 60;
        var endMinutes = Math.Min(startMinutes + durationMinutes, bandEndMinutes);

        var start = dayStart.AddMinutes(startMinutes);
        var end = dayStart.AddMinutes(endMinutes);

        var context = new EventCreateContext
        {
            // LaneId stays null — Week view has no lanes.
            Slot = new SchedulerSlot(start, end),
        };
        return OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the double-click-create pipeline. Mirrors Day view's
    /// <c>InvokeDoubleClickCreateForTestAsync</c>.
    /// </summary>
    /// <param name="colIndex">Synthetically double-clicked day column.</param>
    /// <param name="slotIndex">Synthetically double-clicked slot within the column.</param>
    internal Task InvokeDoubleClickCreateForTestAsync(int colIndex, int slotIndex) =>
        HandleDoubleClickCreateAsync(colIndex, slotIndex);

    /// <summary>
    /// A multi-day all-day event mapped onto the visible week's columns: which contiguous
    /// run of day columns it spans plus per-edge "continues past the visible week" flags.
    /// </summary>
    /// <param name="Event">The original consumer event reference (used for click + key lookups).</param>
    /// <param name="FirstColIndex">Inclusive leftmost column index this bar covers (0..6).</param>
    /// <param name="LastColIndex">Inclusive rightmost column index this bar covers (0..6).</param>
    /// <param name="ClipLeft">True when the event begins before the visible week (left-edge clip).</param>
    /// <param name="ClipRight">True when the event extends past the visible week (right-edge clip).</param>
    internal sealed record AllDayBar(
        ICalendarEvent Event,
        int FirstColIndex,
        int LastColIndex,
        bool ClipLeft,
        bool ClipRight);
}
