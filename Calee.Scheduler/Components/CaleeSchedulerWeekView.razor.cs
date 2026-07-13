#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Week view for Calee.Scheduler (FR-02). Renders seven consecutive days
/// starting at the first-day-of-week boundary that contains <c>CurrentDate</c> — or,
/// when <see cref="VisibleDays"/> is supplied, only the requested subset of those seven
/// days — with a shared day-header row, a shared all-day row (multi-day events render
/// as a single continuous bar across the columns they cover), per-day "+N earlier"/"+N
/// later" overflow chips, and a shared scrollable hour grid in which timed multi-day
/// events are split into per-day chunks.
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
/// See <see cref="VisibleDays"/> for its own soft-degradation case.
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
    /// Opt-in subset of days to render as columns — e.g. Monday–Friday for a work week.
    /// Order in the supplied list is irrelevant and duplicates are ignored; the rendered
    /// column order always follows <see cref="FirstDayOfWeek"/>, and the subset need not
    /// be contiguous (e.g. Monday/Wednesday/Friday).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> (the default) renders all seven days — existing consumers
    /// see no behavior change (additive per the 1.x source-stable promise). An empty
    /// collection, or one whose values match none of the week's seven days, is a
    /// soft-degradation case per PRD §4.6: treated as "all seven days," with a Warning
    /// logged via <see cref="SchedulerComponentBase{TEvent}.Logger"/> when one is
    /// available. This differs from the Debug level the base uses for a null
    /// <see cref="SchedulerComponentBase{TEvent}.Events"/> list — an empty
    /// <see cref="VisibleDays"/> is far more likely to indicate a consumer bug (e.g. an
    /// unfiltered day-picker selection) than an intentionally omitted optional parameter.
    /// </para>
    /// <para>
    /// This is the engine primitive behind a future Work Week view: layout, the all-day
    /// row, multi-day event chunking, overflow chips, and keyboard/drag navigation all
    /// operate over the resolved <see cref="ColumnCount"/> visible columns rather than a
    /// hardcoded seven, so any subset renders correctly without further changes. Events
    /// falling entirely on a hidden day are excluded from the view. A multi-day timed
    /// event that continues into a hidden day is unaffected by that day's visibility —
    /// <see cref="VisibleEventSet{TEvent}"/>'s per-day chunk split walks actual calendar
    /// days, not visible columns, so the adjacent visible chunk still carries the
    /// existing clip-edge arrow indicator (see <see cref="EventChunk{TEvent}"/>).
    /// </para>
    /// </remarks>
    [Parameter]
    public IReadOnlyList<DayOfWeek>? VisibleDays { get; set; }

    /// <summary>
    /// Whether to render a horizontal current-time indicator on today's column when
    /// today (in <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>) is within the
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

    // The 7 visible days, each as a midnight–midnight bound in ResolvedTimeZone.
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> _weekDays = Array.Empty<(DateTimeOffset, DateTimeOffset)>();

    // Cached DayModifier results (issue #8), one entry per _weekDays column — evaluated
    // once per parameter set, not per slot, and only for the visible column subset
    // (VisibleDays-hidden days are never passed to the hook).
    private SchedulerDayState?[] _dayStates = Array.Empty<SchedulerDayState?>();

    // Per-day layout result (parallel to _weekDays).
    private LayoutResult[] _layoutPerDay = Array.Empty<LayoutResult>();

    // All-day "bars" — each spans 1+ contiguous day columns. Computed once per render.
    private List<AllDayBar> _allDayBars = new();
    private int _allDayLaneCount = 1;

    // Frozen-by-construction pre-processed view of the filtered events:
    // owns all-day classification, multi-day per-day chunk splitting, and Id→TEvent lookup.
    private VisibleEventSet<TEvent> _visibleEvents = VisibleEventSet<TEvent>.Empty;
    private EventGeometrySnapshot<TEvent>? _layoutEventSnapshot;
    private (int StartHour, int EndHour, int SlotMinutes, int MaxOverlapColumns,
        TimeZoneInfo TimeZone)? _lastLayoutInputs;
    private (DateTimeOffset Start, DateTimeOffset End)[] _lastLayoutDays =
        Array.Empty<(DateTimeOffset, DateTimeOffset)>();

    // Roving-tabindex anchor for the slot grid: column and row coordinates. The grid is a
    // single tab stop from the consumer's perspective (NFR-06).
    private int _focusedColumnIndex;
    private int _focusedRowIndex;

    // Keyboard move mode state (issue #20 — SC 2.5.7)
    private bool _keyboardMoveMode;
    private string? _keyboardMoveEventId;
    private int _keyboardMovePhantomSlotOffset;
    private int _keyboardMovePhantomDayOffset;
    private DateTimeOffset _keyboardMoveOriginalStart;
    private DateTimeOffset _keyboardMoveOriginalEnd;

    // For FR-23 — fire OnRangeChanged only when the visible range actually changes.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private ElementReference _hourGridRef;
    private bool _scrollPending;
    private IJSObjectReference? _jsModule;

    // Issue #19 — set by HandleGridKeyDownAsync when an arrow key moves the roving
    // tabindex; consumed in OnAfterRenderAsync (after the tabindex swap has actually
    // rendered) to move real browser focus onto the newly-active slot cell. Mirrors
    // _scrollPending's set-then-consume-post-render shape.
    private bool _focusMovePending;

    // Handle for the JS module's day-header Space-key guard (issue #9) — non-null
    // exactly while OnDayHeaderClicked has a delegate wired and the guard is
    // registered. Synced every render in OnAfterRenderAsync so wiring/unwiring the
    // callback after first render registers/unregisters the guard accordingly.
    private string? _dayHeaderKeyGuardHandle;

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

        _weekDays = ResolveVisibleWeekDays();

        // Issue #8 — evaluate the per-day state hook once per visible column, in the
        // grid time zone (_weekDays' midnight boundaries). Hidden VisibleDays columns
        // are never in _weekDays, so they're never evaluated.
        _dayStates = new SchedulerDayState?[_weekDays.Count];
        if (DayModifier is not null)
        {
            for (var i = 0; i < _weekDays.Count; i++)
            {
                _dayStates[i] = GetDayState(_weekDays[i].Start);
            }
        }

        // Optimistic-pin housekeeping (ADR-0006). Drop entries the consumer has caught
        // up on — i.e., the consumer's authoritative Start/End for the event now matches
        // the pinned values, so the pin is redundant. Performed before laying out so the
        // engine sees only still-relevant pins.
        ClearAcknowledgedPins();

        var filtered = GetFilteredEvents();
        if (WeekLayoutInputsChanged()
            || _layoutEventSnapshot is null
            || !_layoutEventSnapshot.Matches(filtered))
        {
            ComputeLayout(filtered);
            _layoutEventSnapshot = EventGeometrySnapshot<TEvent>.Capture(filtered);
            CaptureWeekLayoutInputs();
        }

        // FR-23: fire OnRangeChanged when the visible range changes.
        if (_lastRangeStart != WeekStart || _lastRangeEnd != WeekEndExclusive)
        {
            _lastRangeStart = WeekStart;
            _lastRangeEnd = WeekEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(WeekStart, WeekEndExclusive));
        }
    }

    /// <summary>
    /// Compute the full seven-day week, then narrow it to the days requested by
    /// <see cref="VisibleDays"/>, preserving <see cref="FirstDayOfWeek"/> order. See
    /// <see cref="VisibleDays"/>'s remarks for the soft-degradation rule applied when
    /// the requested subset is empty or matches none of the seven days.
    /// </summary>
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ResolveVisibleWeekDays()
    {
        var allDays = SchedulerViewPrimitives.ComputeWeekDays(CurrentDate, _resolvedFirstDayOfWeek, ResolvedTimeZone);

        if (VisibleDays is null)
        {
            return allDays;
        }

        // Shared with the root scheduler's WorkWeek range/label computation (issue #7)
        // so both stay in lockstep on which days are "in view" — see
        // SchedulerViewPrimitives.FilterVisibleDays remarks.
        var filtered = SchedulerViewPrimitives.FilterVisibleDays(allDays, VisibleDays);

        if (filtered.Count == 0)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: VisibleDays parameter was empty or matched no day of the week; treating as all seven days (PRD §4.6 soft-degradation).");
            return allDays;
        }

        return filtered;
    }

    /// <summary>
    /// Recompute the all-day bars, per-day timed chunks, and the per-day layout for the
    /// current parameter set. Materialized here so the render path is read-only.
    /// </summary>
    private void ComputeLayout(IReadOnlyList<TEvent>? filtered = null)
    {
        // VisibleEventSet owns the filter→classify→split→lookup pipeline. PerDay split mode
        // gives us one chunk per visible day a multi-day timed event touches.
        _visibleEvents = new VisibleEventSet<TEvent>(
            filtered ?? GetFilteredEvents(),
            WeekStart,
            WeekEndExclusive,
            ResolvedTimeZone,
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
            // No overlap with any *visible* day column. Under the all-7-days default this
            // genuinely shouldn't happen (VisibleEventSet pre-filters by range overlap
            // against the same bounds _weekDays spans). Under a VisibleDays subset, though,
            // this is the expected — and frequently hit — path for an all-day event that
            // falls entirely on a hidden day: VisibleEventSet's range filter still admits
            // it (the range spans first-visible-day-start..last-visible-day-end, which can
            // include hidden days in between), but no entry in _weekDays overlaps it, so
            // the bar is correctly dropped here rather than rendered. Do not "clean up" this
            // branch as dead code.
            if (firstCol < 0) continue;

            var clipLeft = ev.Start < _weekDays[firstCol].Start;
            var clipRight = ev.End > _weekDays[lastCol].End;
            _allDayBars.Add(new AllDayBar(ev, firstCol, lastCol, clipLeft, clipRight, LaneIndex: 0));
        }
        _allDayLaneCount = AssignAllDayLanes(_allDayBars);

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
                _resolvedMaxOverlapColumns,
                ResolvedTimeZone);
        }

        EnsureFocusedSlotIsAvailable();
        PruneEventRefs();
    }

    /// <summary>
    /// Greedily assigns horizontally overlapping all-day bars to separate vertical lanes.
    /// Bars are ordered by their first column so the lowest available lane produces the
    /// minimum lane count for interval data; event id makes equal starts deterministic.
    /// </summary>
    private static int AssignAllDayLanes(List<AllDayBar> bars)
    {
        bars.Sort((a, b) =>
        {
            var byStart = a.FirstColIndex.CompareTo(b.FirstColIndex);
            return byStart != 0 ? byStart : string.CompareOrdinal(a.Event.Id, b.Event.Id);
        });

        var laneRights = new List<int>();
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var laneIndex = 0;
            while (laneIndex < laneRights.Count && laneRights[laneIndex] >= bar.FirstColIndex)
            {
                laneIndex++;
            }

            if (laneIndex == laneRights.Count)
            {
                laneRights.Add(bar.LastColIndex);
            }
            else
            {
                laneRights[laneIndex] = bar.LastColIndex;
            }

            bars[i] = bar with { LaneIndex = laneIndex };
        }

        return Math.Max(1, laneRights.Count);
    }

    private bool WeekLayoutInputsChanged()
    {
        var current = (_resolvedStartHour, _resolvedEndHour, _resolvedSlotMinutes,
            _resolvedMaxOverlapColumns, ResolvedTimeZone);
        if (_lastLayoutInputs != current || _lastLayoutDays.Length != _weekDays.Count) return true;

        for (var i = 0; i < _weekDays.Count; i++)
        {
            if (_lastLayoutDays[i] != _weekDays[i]) return true;
        }
        return false;
    }

    private void CaptureWeekLayoutInputs()
    {
        _lastLayoutInputs = (_resolvedStartHour, _resolvedEndHour, _resolvedSlotMinutes,
            _resolvedMaxOverlapColumns, ResolvedTimeZone);
        _lastLayoutDays = _weekDays.ToArray();
    }

    private void PruneEventRefs()
    {
        if (_eventRefsByEventId.Count == 0) return;

        var renderedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dayLayout in _layoutPerDay)
        {
            foreach (var positioned in dayLayout.Positioned) renderedIds.Add(positioned.Event.Id);
        }

        List<string>? stale = null;
        foreach (var id in _eventRefsByEventId.Keys)
        {
            if (renderedIds.Contains(id)) continue;
            stale ??= new List<string>();
            stale.Add(id);
        }
        if (stale is null) return;
        foreach (var id in stale) _eventRefsByEventId.Remove(id);
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

        // Issue #19 — move real browser focus onto the newly-active slot cell after an
        // arrow-key roving move. Deferred to here so the query runs after the tabindex
        // swap has rendered to the DOM.
        if (_focusMovePending && _jsModule is not null)
        {
            _focusMovePending = false;
            await SchedulerViewPrimitives.TryFocusActiveGridCellAsync(_jsModule, _hourGridRef);
        }

        // Issue #9 — keep the JS module's day-header Space-key guard in sync with
        // OnDayHeaderClicked's wiring. Checked every render (not just firstRender) so
        // a consumer that wires or unwires the callback after mount still gets the
        // guard registered/unregistered accordingly. See registerDayHeaderKeyGuard's
        // doc comment in calee-scheduler.js for why this can't be a Blazor
        // @onkeydown:preventDefault directive.
        if (_jsModule is not null)
        {
            if (OnDayHeaderClicked.HasDelegate && _dayHeaderKeyGuardHandle is null)
            {
                _dayHeaderKeyGuardHandle = await SchedulerViewPrimitives.TryRegisterDayHeaderKeyGuardAsync(_jsModule);
            }
            else if (!OnDayHeaderClicked.HasDelegate && _dayHeaderKeyGuardHandle is not null)
            {
                await SchedulerViewPrimitives.TryUnregisterDayHeaderKeyGuardAsync(_jsModule, _dayHeaderKeyGuardHandle);
                _dayHeaderKeyGuardHandle = null;
            }
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_jsModule is not null)
        {
            if (_dayHeaderKeyGuardHandle is not null)
            {
                await SchedulerViewPrimitives.TryUnregisterDayHeaderKeyGuardAsync(_jsModule, _dayHeaderKeyGuardHandle);
                _dayHeaderKeyGuardHandle = null;
            }
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

    /// <summary>
    /// Rendered day-column count — 7 by default, or the size of the resolved
    /// <see cref="VisibleDays"/> subset (1..7) when supplied.
    /// </summary>
    internal int ColumnCount => _weekDays.Count;

    /// <summary>
    /// Minimum shared canvas width: the default 4rem gutter plus 80px per visible day.
    /// Keeping every row on the same canvas preserves alignment while the root scrolls.
    /// </summary>
    internal int MinimumCanvasWidthPixels => 64 + ColumnCount * 80;

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
        if (IsColumnBlocked(col))
        {
            var label = _dayStates[col]?.Label;
            return string.IsNullOrEmpty(label)
                ? $"{day:dddd}, {time:h:mm tt}, blocked"
                : $"{day:dddd}, {time:h:mm tt}, {label}";
        }
        return $"{day:dddd}, {time:h:mm tt}, empty slot";
    }

    // ----- Blocked days (issue #8) -----------------------------------------------------

    /// <summary>True when the column's day is blocked per <see cref="SchedulerComponentBase{TEvent}.DayModifier"/>.</summary>
    internal bool IsColumnBlocked(int col) => _dayStates[col]?.IsBlocked ?? false;

    /// <summary>Consumer-supplied per-day class hook for the column's day, or null.</summary>
    internal string? DayBlockedClassFor(int col) => _dayStates[col]?.Class;

    /// <summary>Accessible label announced on the column's day header when it is blocked.</summary>
    internal string BlockedDayHeaderLabel(int col) =>
        SchedulerViewPrimitives.BlockedDayAccessibleLabel(_weekDays[col].Start, _dayStates[col]);

    // ----- Day header template + click (issue #9) -------------------------------------

    /// <summary>
    /// True when <see cref="SchedulerComponentBase{TEvent}.OnDayHeaderClicked"/> has a
    /// delegate wired. Gates whether every column's header cell is rendered as
    /// focusable/interactive at all — fail-closed default (see the parameter's
    /// remarks). Not per-column: either every visible column's header is interactive
    /// or none are.
    /// </summary>
    internal bool IsDayHeaderInteractive => OnDayHeaderClicked.HasDelegate;

    /// <summary>Accessible name for the column's day header when it is interactive and not blocked.</summary>
    internal string DayHeaderAccessibleName(int col) =>
        SchedulerViewPrimitives.DayHeaderAccessibleName(_weekDays[col].Start);

    /// <summary>
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnDayHeaderClicked"/> with the
    /// column's midnight boundary in <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>.
    /// No-op while a drag is active (ADR-0006 precedence — matches every other click
    /// handler in the view).
    /// </summary>
    internal Task HandleDayHeaderClickAsync(int col)
    {
        if (IsDragActive) return Task.CompletedTask;
        return OnDayHeaderClicked.InvokeAsync(_weekDays[col].Start);
    }

    /// <summary>
    /// Keyboard handler bound to a day header when <see cref="IsDayHeaderInteractive"/>.
    /// Enter and Space activate the header; every other key is a no-op (the header row
    /// is not part of the hour grid's roving-tabindex flow).
    /// </summary>
    /// <remarks>
    /// This handler does not (and must not) carry a Blazor
    /// <c>@onkeydown:preventDefault</c> directive — that directive is element-wide, so
    /// binding it here to suppress Space's default "scroll the viewport" behavior
    /// would also swallow Tab's default focus-move off the header, a keyboard trap.
    /// The Space-scroll suppression instead lives in the JS module's
    /// <c>registerDayHeaderKeyGuard</c> (<see cref="OnAfterRenderAsync"/> registers it
    /// while <see cref="IsDayHeaderInteractive"/>), which is scoped to exactly the
    /// Space key on an interactive day-header target and leaves every other key,
    /// including Tab, untouched.
    /// </remarks>
    internal Task HandleDayHeaderKeyDownAsync(KeyboardEventArgs e, int col)
    {
        if (e.Key != "Enter" && e.Key != " ") return Task.CompletedTask;
        return HandleDayHeaderClickAsync(col);
    }

    /// <summary>
    /// Issue #8 — the grid-focus concept for Week is the roving (column, row) pair;
    /// the create-at-focus suppression check looks at the focused column's day.
    /// </summary>
    private protected override bool IsFocusedGridDayBlocked() =>
        _focusedColumnIndex >= 0 && _focusedColumnIndex < _dayStates.Length && IsColumnBlocked(_focusedColumnIndex);

    /// <summary>Format an hour-of-day for the time gutter.</summary>
    internal static string FormatHour(int hour) => SchedulerViewPrimitives.FormatHour(hour);

    /// <summary>Format an event's start/end as an accessible time range in ResolvedTimeZone.</summary>
    internal string FormatEventTimeRange(ICalendarEvent ev) =>
        SchedulerViewPrimitives.FormatEventTimeRange(ev, ResolvedTimeZone);

    /// <summary>True when the supplied column index is "today in ResolvedTimeZone".</summary>
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

    /// <summary>Number of vertical lanes required by the all-day bars, with one lane retained when empty.</summary>
    internal int AllDayLaneCount => _allDayLaneCount;

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
        if (IsSlotOccupied(colIndex, slotIndex)) return;

        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var endMinutes = startMinutes + _resolvedSlotMinutes;
        var start = SchedulerViewPrimitives.TimeInZone(_weekDays[colIndex].Start.Date, startMinutes, ResolvedTimeZone);
        var end = SchedulerViewPrimitives.TimeInZone(_weekDays[colIndex].Start.Date, endMinutes, ResolvedTimeZone);
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

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null && _weekDays.Count > 0)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomSlotOffset = Math.Max(
                        -_focusedRowIndex,
                        _keyboardMovePhantomSlotOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origSlotIdx = GetKeyboardMoveOriginalSlotIndex();
                    _keyboardMovePhantomSlotOffset = Math.Min(
                        SlotCount - 1 - origSlotIdx,
                        _keyboardMovePhantomSlotOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowRight":
                    var origDayIdx = GetKeyboardMoveOriginalDayIndex();
                    _keyboardMovePhantomDayOffset = Math.Min(
                        _weekDays.Count - 1 - origDayIdx,
                        _keyboardMovePhantomDayOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    var leftDayIdx = GetKeyboardMoveOriginalDayIndex();
                    _keyboardMovePhantomDayOffset = Math.Max(
                        -leftDayIdx,
                        _keyboardMovePhantomDayOffset - 1);
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
                MoveFocusedRow(1);
                break;
            case "ArrowUp":
                MoveFocusedRow(-1);
                break;
            case "ArrowRight":
                MoveFocusedColumn(1);
                break;
            case "ArrowLeft":
                MoveFocusedColumn(-1);
                break;
            case "Enter":
                if (!IsSlotOccupied(_focusedColumnIndex, _focusedRowIndex))
                {
                    await HandleSlotClickAsync(_focusedColumnIndex, _focusedRowIndex);
                }
                break;
        }
    }

    private void MoveFocusedRow(int direction)
    {
        for (var row = _focusedRowIndex + direction; row >= 0 && row < SlotCount; row += direction)
        {
            if (IsSlotOccupied(_focusedColumnIndex, row)) continue;

            _focusedRowIndex = row;
            _focusMovePending = true;
            StateHasChanged();
            return;
        }
    }

    private void MoveFocusedColumn(int direction)
    {
        for (var col = _focusedColumnIndex + direction; col >= 0 && col < ColumnCount; col += direction)
        {
            if (IsSlotOccupied(col, _focusedRowIndex)) continue;

            _focusedColumnIndex = col;
            _focusMovePending = true;
            StateHasChanged();
            return;
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

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null && _weekDays.Count > 0)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomSlotOffset = Math.Max(
                        -_focusedRowIndex,
                        _keyboardMovePhantomSlotOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origSlotIdx = GetKeyboardMoveOriginalSlotIndex();
                    _keyboardMovePhantomSlotOffset = Math.Min(
                        SlotCount - 1 - origSlotIdx,
                        _keyboardMovePhantomSlotOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowRight":
                    var origDayIdx = GetKeyboardMoveOriginalDayIndex();
                    _keyboardMovePhantomDayOffset = Math.Min(
                        _weekDays.Count - 1 - origDayIdx,
                        _keyboardMovePhantomDayOffset + 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowLeft":
                    var leftDayIdx = GetKeyboardMoveOriginalDayIndex();
                    _keyboardMovePhantomDayOffset = Math.Max(
                        -leftDayIdx,
                        _keyboardMovePhantomDayOffset - 1);
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

    private protected override async Task DispatchKeyboardMoveAsync(TEvent? focusedEvent, string? focusedEventId)
    {
        if (focusedEvent is null || focusedEventId is null) return;

        _keyboardMoveMode = true;
        _keyboardMoveEventId = focusedEventId;
        _keyboardMovePhantomSlotOffset = 0;
        _keyboardMovePhantomDayOffset = 0;
        _keyboardMoveOriginalStart = focusedEvent.Start;
        _keyboardMoveOriginalEnd = focusedEvent.End;

        // The focused chip's Start is the reference for time-of-day; its day column
        // determines the origin column for cross-day movement. Use the chunk's Start
        // (which is clipped to the visible day) as the reference, per the handover note.
        var chunk = _visibleEvents.FindById(focusedEventId) as ICalendarEvent;
        var chipSlotIndex = chunk is not null
            ? Math.Clamp(
                (int)((chunk.Start.TimeOfDay.TotalMinutes - _resolvedStartHour * 60) / _resolvedSlotMinutes),
                0, SlotCount - 1)
            : 0;

        var request = new KeyboardMoveRequest
        {
            Event = (ICalendarEvent)focusedEvent,
            CurrentSlotIndex = chipSlotIndex,
        };
        await OnKeyboardMoveRequested.InvokeAsync(request);

        _optimisticPin[focusedEventId] = (focusedEvent.Start, focusedEvent.End);
        StateHasChanged();
    }

    private protected override async Task DispatchKeyboardResizeAsync(TEvent? focusedEvent, string? focusedEventId, KeyboardResizeDirection direction)
    {
        if (focusedEvent is null || focusedEventId is null) return;

        var slotMinutes = _resolvedSlotMinutes;
        var deltaMinutes = direction == KeyboardResizeDirection.Extend ? slotMinutes : -slotMinutes;
        var newEnd = focusedEvent.End.AddMinutes(deltaMinutes);

        if (newEnd <= focusedEvent.Start)
        {
            newEnd = focusedEvent.Start.AddMinutes(slotMinutes);
        }

        var request = new KeyboardResizeRequest
        {
            Event = focusedEvent,
            Direction = direction,
        };
        await OnKeyboardResizeRequested.InvokeAsync(request);

        _optimisticPin[focusedEventId] = (focusedEvent.Start, newEnd);
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

    private int GetKeyboardMoveOriginalSlotIndex()
    {
        var startHour = _resolvedStartHour;
        var slotMinutes = _resolvedSlotMinutes;
        var minutes = _keyboardMoveOriginalStart.TimeOfDay.TotalMinutes - startHour * 60;
        return Math.Clamp((int)(minutes / slotMinutes), 0, SlotCount - 1);
    }

    private int GetKeyboardMoveOriginalDayIndex()
    {
        var date = _keyboardMoveOriginalStart.Date;
        for (var i = 0; i < _weekDays.Count; i++)
        {
            if (_weekDays[i].Start.Date == date)
                return i;
        }
        // Multi-day event whose Start falls on a hidden/out-of-range day for the
        // chunk the user focused — clamp to the closest visible day.
        if (_weekDays.Count > 0)
        {
            var startDate = _weekDays[0].Start.Date;
            var endDate = _weekDays[^1].Start.Date;
            if (date < startDate) return 0;
            if (date > endDate) return _weekDays.Count - 1;
        }
        return 0;
    }

    private async Task UpdateKeyboardMovePhantomPositionAsync()
    {
        if (_keyboardMoveEventId is null || _weekDays.Count == 0) return;

        var slotMinutes = _resolvedSlotMinutes;
        var origSlotIdx = GetKeyboardMoveOriginalSlotIndex();
        var origDayIdx = GetKeyboardMoveOriginalDayIndex();
        var newDayIdx = Math.Clamp(origDayIdx + _keyboardMovePhantomDayOffset, 0, _weekDays.Count - 1);
        var newSlotIdx = Math.Clamp(origSlotIdx + _keyboardMovePhantomSlotOffset, 0, SlotCount - 1);

        var targetDayStart = _weekDays[newDayIdx].Start;
        var visibleStart = SchedulerViewPrimitives.TimeInZone(
            targetDayStart.Date, _resolvedStartHour * 60, ResolvedTimeZone);
        var newStart = visibleStart.AddMinutes(newSlotIdx * slotMinutes);
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
        var newEnd = newStart + duration;

        _optimisticPin[_keyboardMoveEventId] = (newStart, newEnd);
        ComputeLayout();
        StateHasChanged();
    }

    private async Task CommitKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null || _weekDays.Count == 0) return;

        var slotMinutes = _resolvedSlotMinutes;
        var origSlotIdx = GetKeyboardMoveOriginalSlotIndex();
        var origDayIdx = GetKeyboardMoveOriginalDayIndex();
        var newDayIdx = Math.Clamp(origDayIdx + _keyboardMovePhantomDayOffset, 0, _weekDays.Count - 1);
        var newSlotIdx = Math.Clamp(origSlotIdx + _keyboardMovePhantomSlotOffset, 0, SlotCount - 1);

        var targetDayStart = _weekDays[newDayIdx].Start;
        var visibleStart = SchedulerViewPrimitives.TimeInZone(
            targetDayStart.Date, _resolvedStartHour * 60, ResolvedTimeZone);
        var newStartLocal = visibleStart.AddMinutes(newSlotIdx * slotMinutes);
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
        var newEndLocal = newStartLocal + duration;

        // Apply date shift to the original event's Start date (honors the event's
        // actual date, not the chunk's clipped date). The day offset is measured
        // in visible-day steps (not calendar days), so compute the calendar-day
        // delta between the origin and target _weekDays entries.
        var originDayDate = _weekDays[origDayIdx].Start.Date;
        var targetDayDate = _weekDays[newDayIdx].Start.Date;
        var daysMoved = (targetDayDate - originDayDate).Days;

        var newStartDate = _keyboardMoveOriginalStart.Date.AddDays(daysMoved);
        var newDayBase = new DateTimeOffset(newStartDate, ResolvedTimeZone.GetUtcOffset(newStartDate));
        var snappedTimeOfDay = newStartLocal - targetDayStart;
        var newStart = newDayBase + snappedTimeOfDay;
        var newEnd = newStart + duration;

        var ev = _visibleEvents.FindById(_keyboardMoveEventId);
        if (ev is null)
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
        _keyboardMovePhantomSlotOffset = 0;
        _keyboardMovePhantomDayOffset = 0;
    }

    private async Task CancelKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null) return;

        _optimisticPin.Remove(_keyboardMoveEventId);
        ComputeLayout();
        StateHasChanged();

        _keyboardMoveMode = false;
        _keyboardMoveEventId = null;
        _keyboardMovePhantomSlotOffset = 0;
        _keyboardMovePhantomDayOffset = 0;
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
        if (changed)
        {
            _focusMovePending = true;
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

    /// <summary>Returns true when a positioned event or overlap block intersects the supplied slot.</summary>
    internal bool IsSlotOccupied(int colIndex, int rowIndex)
    {
        if (colIndex < 0 || colIndex >= _layoutPerDay.Length || rowIndex < 0 || rowIndex >= SlotCount)
        {
            return false;
        }

        var slotStart = (double)rowIndex / SlotCount * 100.0;
        var slotEnd = (double)(rowIndex + 1) / SlotCount * 100.0;
        var layout = _layoutPerDay[colIndex];

        foreach (var positionedEvent in layout.Positioned)
        {
            if (RangesOverlap(
                slotStart,
                slotEnd,
                positionedEvent.TimeStartPercent,
                positionedEvent.TimeStartPercent + positionedEvent.TimeSpanPercent))
            {
                return true;
            }
        }

        foreach (var block in layout.OverlapOverflow)
        {
            if (RangesOverlap(
                slotStart,
                slotEnd,
                block.TimeStartPercent,
                block.TimeStartPercent + block.TimeSpanPercent))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RangesOverlap(double firstStart, double firstEnd, double secondStart, double secondEnd) =>
        firstStart < secondEnd && secondStart < firstEnd;

    private void EnsureFocusedSlotIsAvailable()
    {
        if (ColumnCount == 0 || SlotCount == 0) return;

        _focusedColumnIndex = Math.Clamp(_focusedColumnIndex, 0, ColumnCount - 1);
        _focusedRowIndex = Math.Clamp(_focusedRowIndex, 0, SlotCount - 1);
        if (!IsSlotOccupied(_focusedColumnIndex, _focusedRowIndex)) return;

        for (var row = _focusedRowIndex + 1; row < SlotCount; row++)
        {
            if (IsSlotOccupied(_focusedColumnIndex, row)) continue;

            _focusedRowIndex = row;
            return;
        }

        for (var col = 0; col < ColumnCount; col++)
        {
            for (var row = 0; row < SlotCount; row++)
            {
                if (IsSlotOccupied(col, row)) continue;

                _focusedColumnIndex = col;
                _focusedRowIndex = row;
                return;
            }
        }
    }

    /// <summary>Returns true when the supplied empty (column, row) is the currently-tabbable cell.</summary>
    internal bool IsSlotTabbable(int colIndex, int rowIndex) =>
        colIndex == _focusedColumnIndex
        && rowIndex == _focusedRowIndex
        && !IsSlotOccupied(colIndex, rowIndex);

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
        var start = TimeZoneInfo.ConvertTime(block.RegionStart, ResolvedTimeZone);
        var end = TimeZoneInfo.ConvertTime(block.RegionEnd, ResolvedTimeZone);
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
    /// <param name="e">The pointer-down event.</param>
    /// <param name="ev">The chunk (or plain event) rendered at the pressed chip.</param>
    /// <param name="colIndex">
    /// The visible day-column index the pressed chip is actually rendered in. Threaded
    /// through to <see cref="HandleMoveDropAsync"/> as the drag's origin column instead
    /// of re-deriving it from the underlying event's <c>Start</c> — a multi-day event
    /// can have its true <c>Start</c> on a day <see cref="VisibleDays"/> hides, in which
    /// case only a later chunk (this one) renders at all, and <c>Start</c>'s own day
    /// isn't addressable in <c>_weekDays</c>. See the code-review fix on issue #6.
    /// </param>
    internal async Task OnEventPointerDownAsync(PointerEventArgs e, ICalendarEvent ev, int colIndex)
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

        var durationMinutes = (ev.End - ev.Start).TotalMinutes;
        var eventDurationSlots = (int)Math.Ceiling(durationMinutes / _resolvedSlotMinutes);
        var slotHeightPx = gridHeightPx > 0 ? gridHeightPx / Math.Max(1, SlotCount) : 0;
        var eventDurationPixels = eventDurationSlots * slotHeightPx;
        var eventDurationDays = (ev.End.Date - ev.Start.Date).Days + 1;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.Move,
            snapPixelsX: snapPixelsX,
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleMoveDropAsync(typed, colIndex, payload),
            onCancel: static () => Task.CompletedTask,
            highlightContainer: _hourGridRef,
            highlightMode: "slot-band",
            eventDurationPixels: eventDurationPixels,
            eventDurationSlots: eventDurationSlots,
            eventDurationDays: eventDurationDays,
            columnCount: ColumnCount,
            slotCount: SlotCount);
    }

    /// <summary>
    /// Drop handler. Converts the JS drop delta (X for day-column crossing, Y for
    /// time-of-day) into a new (Start, End) preserving the event's duration,
    /// optimistically pins the new position, fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnEventMoved"/>, and rolls the
    /// pin back if the consumer set <see cref="EventMoveContext.Cancel"/>.
    /// </summary>
    /// <param name="ev">The full, underlying consumer event (never a chunk).</param>
    /// <param name="originColIndex">
    /// The visible day-column index of the chunk the drag actually started from (see
    /// <see cref="OnEventPointerDownAsync"/>). This drives the day-shift math directly —
    /// it does <em>not</em> need to equal the column <c>ev.Start</c> would resolve to,
    /// which matters when <c>ev.Start</c> falls on a day <see cref="VisibleDays"/> hides.
    /// </param>
    /// <param name="payload">The JS-reported drop delta.</param>
    private async Task HandleMoveDropAsync(TEvent ev, int originColIndex, DropPayload payload)
    {
        if (originColIndex < 0 || originColIndex >= ColumnCount)
        {
            // Defensive only — pointer-down always captures a column index for a chunk
            // that's currently rendered, so this shouldn't happen outside of a stale
            // closure surviving a parameter change mid-drag. Log rather than fail
            // silently, per code review on issue #6.
            Logger?.LogWarning(
                "Calee.Scheduler: drag-to-move drop could not resolve a valid origin day column ({OriginColIndex}, ColumnCount={ColumnCount}); ignoring drop.",
                originColIndex, ColumnCount);
            return;
        }

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

        // 1) Time-of-day axis: derive the pre-drop pixel offset from ev.Start's wall-clock
        //    time-of-day alone — never from its calendar date. The date is handled
        //    separately in step 2 via daysMoved. Using time-of-day only means this math
        //    is correct even when ev.Start's own date isn't the origin chunk's date (the
        //    hidden-day-start case above) — the drag always starts from the pixel row the
        //    grabbed chunk actually renders at, which corresponds to ev.Start's time, not
        //    to a lookup of which column ev.Start's date happens to be in.
        var origStartMinutes = ev.Start.TimeOfDay.TotalMinutes - _resolvedStartHour * 60;
        var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridHeightPx;
        var newStartPxInGrid = (origStartMinutes / minutesPerPx) + payload.DeltaYPx;

        // 2) Day-column axis: round DeltaXPx to whole visible-column shifts *from the
        //    origin chunk's own column*, clamped to [0, ColumnCount-1]. daysMoved is the
        //    calendar-day delta between the origin and target *visible* days — under a
        //    non-contiguous VisibleDays subset a single column step can span more than
        //    one calendar day (e.g. Monday -> Wednesday is +2 days for a 1-column drag).
        //    Applying daysMoved to ev.Start's own date (not the origin chunk's clipped
        //    Start) is what makes a hidden-day-start event's true Start shift correctly
        //    instead of getting truncated to the visible chunk.
        var colWidth = gridWidthPx / ColumnCount;
        var dayShift = colWidth > 0
            ? (int)Math.Round(payload.DeltaXPx / colWidth, MidpointRounding.AwayFromZero)
            : 0;
        var newColIndex = Math.Clamp(originColIndex + dayShift, 0, ColumnCount - 1);
        var originDayDate = _weekDays[originColIndex].Start.Date;
        var targetDayStart = _weekDays[newColIndex].Start;
        var daysMoved = (targetDayStart.Date - originDayDate).Days;

        var targetVisibleStart = SchedulerViewPrimitives.TimeInZone(
            targetDayStart.Date, _resolvedStartHour * 60, ResolvedTimeZone);
        var targetVisibleEnd = SchedulerViewPrimitives.TimeInZone(
            targetDayStart.Date, _resolvedEndHour * 60, ResolvedTimeZone);

        var snappedOnTargetDay = EventLayoutEngine.InverseY(
            pixelY: newStartPxInGrid,
            totalHeightPx: gridHeightPx,
            rangeStart: targetVisibleStart,
            rangeEndExclusive: targetVisibleEnd,
            slotMinutes: _resolvedSlotMinutes);

        // Re-anchor the snapped time-of-day (currently expressed against targetDayStart's
        // date, which is irrelevant here — only the offset-from-midnight matters) onto
        // ev.Start's own date shifted by daysMoved, in the grid time zone (ADR-0001).
        var snappedTimeOfDay = snappedOnTargetDay - targetDayStart;
        var newStartDate = ev.Start.Date.AddDays(daysMoved);
        var newStartDayBase = new DateTimeOffset(newStartDate, ResolvedTimeZone.GetUtcOffset(newStartDate));
        var newStart = newStartDayBase + snappedTimeOfDay;

        var duration = ev.End - ev.Start;
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
    /// <see cref="DateTimeOffset"/>. Returns <c>-1</c> when the value falls outside the
    /// visible week — notably including the case where <paramref name="value"/>'s date
    /// falls on a day <see cref="VisibleDays"/> hides. Kept only as the origin-column
    /// default for the test-only <c>Invoke*DropForTestAsync</c> overloads that don't
    /// specify an explicit column; the production pointer-down path threads the actual
    /// rendered chunk's column through directly instead of calling this (see the
    /// code-review fix on issue #6 — the whole point is that a chunk's own render column
    /// can legitimately differ from where this method would resolve its event's Start).
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
    /// <remarks>
    /// Resolves the origin column via <see cref="FindColumnIndex"/> against <c>ev.Start</c>
    /// — correct for every pre-existing test (none of them hide the event's own Start
    /// day), but not representative of a multi-day event whose Start falls on a day
    /// <see cref="VisibleDays"/> hides. Tests covering that scenario must use the
    /// <see cref="InvokeMoveDropForTestAsync(TEvent, int, DropPayload)"/> overload and
    /// pass the origin column explicitly, exactly as production's pointer-down handler
    /// does.
    /// </remarks>
    internal Task InvokeMoveDropForTestAsync(TEvent ev, DropPayload payload)
    {
        var originColIndex = FindColumnIndex(ev.Start);
        if (originColIndex < 0)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: InvokeMoveDropForTestAsync could not resolve ev.Start's day column; " +
                "use the (TEvent, int, DropPayload) overload with an explicit origin column instead.");
            return Task.CompletedTask;
        }
        return HandleMoveDropAsync(ev, originColIndex, payload);
    }

    /// <summary>
    /// Test-only entry point mirroring <see cref="InvokeMoveDropForTestAsync(TEvent, DropPayload)"/>,
    /// but with the origin day-column supplied explicitly instead of derived from
    /// <c>ev.Start</c> — exercises the same path production's pointer-down handler takes,
    /// including the case where <c>ev.Start</c>'s own day isn't in the visible subset.
    /// </summary>
    internal Task InvokeMoveDropForTestAsync(TEvent ev, int originColIndex, DropPayload payload) =>
        HandleMoveDropAsync(ev, originColIndex, payload);

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
    /// <param name="e">The pointer-down event.</param>
    /// <param name="ev">The chunk (or plain event) rendered at the pressed chip.</param>
    /// <param name="colIndex">
    /// The visible day-column index the pressed chip is actually rendered in — threaded
    /// through to <see cref="HandleResizeDropAsync"/> for the same reason as
    /// <see cref="OnEventPointerDownAsync"/>.
    /// </param>
    internal async Task OnEventResizePointerDownAsync(PointerEventArgs e, ICalendarEvent ev, int colIndex)
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

        var slotHeightPx = gridHeightPx > 0 ? gridHeightPx / Math.Max(1, SlotCount) : 0;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.ResizeEnd,
            snapPixelsX: 0,                      // Week view doesn't resize across columns.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleResizeDropAsync(typed, colIndex, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            highlightContainer: _hourGridRef,
            highlightMode: "slot-band",
            eventDurationPixels: slotHeightPx,
            eventDurationSlots: 1,
            columnCount: ColumnCount,
            slotCount: SlotCount);
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
    /// <para>
    /// <strong>Hidden-day Start.</strong> Like <see cref="HandleMoveDropAsync"/>, the band
    /// bounds come from <paramref name="originColIndex"/> (the chunk actually grabbed),
    /// not from re-deriving a column via <c>ev.Start</c> — a multi-day event whose true
    /// <c>Start</c> falls on a day <see cref="VisibleDays"/> hides still resizes
    /// correctly from its one visible (clipped-at-start) chunk. <c>ev.End</c>'s
    /// contribution is measured as elapsed time since <c>visibleStart</c> (itself
    /// anchored on <paramref name="originColIndex"/>) — deliberately <em>not</em> via
    /// <c>ev.End.TimeOfDay</c>, which is ambiguous for an End exactly at midnight
    /// (00:00 is indistinguishable from the top of the band; see the code-review fix
    /// that reverted this from a wall-clock-only formula after it collapsed
    /// midnight-ending events on drop).
    /// </para>
    /// </remarks>
    /// <param name="ev">The full, underlying consumer event (never a chunk).</param>
    /// <param name="originColIndex">
    /// The visible day-column index of the chunk the resize handle was pressed on (see
    /// <see cref="OnEventResizePointerDownAsync"/>).
    /// </param>
    /// <param name="payload">The JS-reported drop delta.</param>
    private async Task HandleResizeDropAsync(TEvent ev, int originColIndex, DropPayload payload)
    {
        if (originColIndex < 0 || originColIndex >= ColumnCount)
        {
            // Defensive only — see the matching guard in HandleMoveDropAsync.
            Logger?.LogWarning(
                "Calee.Scheduler: drag-to-resize drop could not resolve a valid origin day column ({OriginColIndex}, ColumnCount={ColumnCount}); ignoring drop.",
                originColIndex, ColumnCount);
            return;
        }

        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }

        // Resize stays in the origin chunk's own day column — the bottom-edge drag is
        // purely vertical (snapPixelsX = 0 above so DeltaXPx is unsnapped, but we don't
        // use it).
        var origDayStart = _weekDays[originColIndex].Start;
        var visibleStart = SchedulerViewPrimitives.TimeInZone(
            origDayStart.Date, _resolvedStartHour * 60, ResolvedTimeZone);
        var visibleEnd = SchedulerViewPrimitives.TimeInZone(
            origDayStart.Date, _resolvedEndHour * 60, ResolvedTimeZone);

        // Minutes-from-band-start, measured as elapsed time from the origin chunk's own
        // visibleStart — NOT via ev.End.TimeOfDay. Unlike ev.Start (used in
        // HandleMoveDropAsync), ev.End has a 24:00 ambiguity: an event ending exactly at
        // midnight has TimeOfDay == 00:00, which is indistinguishable from an event
        // starting the visible band at the top. Elapsed-time-since-visibleStart resolves
        // this correctly (a midnight End is a full day past visibleStart, not zero
        // minutes past it) and still works for the hidden-day-start repro, because
        // visibleStart above is already anchored on originColIndex — the chunk actually
        // grabbed — not on a re-derived lookup of ev.End's own date.
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
    /// <see cref="InvokeMoveDropForTestAsync(TEvent, DropPayload)"/> — resolves the origin
    /// column via <see cref="FindColumnIndex"/> against <c>ev.Start</c>. Not representative
    /// of a hidden-Start multi-day event; use the explicit-column overload for that.
    /// </summary>
    internal Task InvokeResizeDropForTestAsync(TEvent ev, DropPayload payload)
    {
        var originColIndex = FindColumnIndex(ev.Start);
        if (originColIndex < 0)
        {
            Logger?.LogWarning(
                "Calee.Scheduler: InvokeResizeDropForTestAsync could not resolve ev.Start's day column; " +
                "use the (TEvent, int, DropPayload) overload with an explicit origin column instead.");
            return Task.CompletedTask;
        }
        return HandleResizeDropAsync(ev, originColIndex, payload);
    }

    /// <summary>
    /// Test-only entry point mirroring <see cref="InvokeResizeDropForTestAsync(TEvent, DropPayload)"/>,
    /// but with the origin day-column supplied explicitly — see
    /// <see cref="InvokeMoveDropForTestAsync(TEvent, int, DropPayload)"/>.
    /// </summary>
    internal Task InvokeResizeDropForTestAsync(TEvent ev, int originColIndex, DropPayload payload) =>
        HandleResizeDropAsync(ev, originColIndex, payload);

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
    internal (int slotOffset, int dayOffset) KeyboardMovePhantomOffsetsForTest =>
        (_keyboardMovePhantomSlotOffset, _keyboardMovePhantomDayOffset);

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
        // Issue #8 — fail-closed: don't start the drag on a blocked day so no ghost
        // rectangle is ever drawn over it.
        if (IsColumnBlocked(anchorColIndex)) return;

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
        var start = SchedulerViewPrimitives.TimeInZone(dayStart.Date, startMinutes, ResolvedTimeZone);
        var end = SchedulerViewPrimitives.TimeInZone(dayStart.Date, endMinutes, ResolvedTimeZone);

        // Issue #8 — fail-closed: if the swept region touches a blocked day, the
        // create does not fire. Week's drag is column-locked (lane axis locked to the
        // anchor), so this reduces to the anchor column, but the shared helper keeps
        // Day/Week/Month consistent.
        if (CreateSpanTouchesBlockedDay(start, end)) return;

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
    /// <see cref="InvokeMoveDropForTestAsync(TEvent, DropPayload)"/> /
    /// <see cref="InvokeResizeDropForTestAsync(TEvent, DropPayload)"/>.
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

        var start = SchedulerViewPrimitives.TimeInZone(dayStart.Date, startMinutes, ResolvedTimeZone);
        var end = SchedulerViewPrimitives.TimeInZone(dayStart.Date, endMinutes, ResolvedTimeZone);

        // Issue #8 — fail-closed: no-op on a blocked day (no phantom, no OnEventCreated).
        if (CreateSpanTouchesBlockedDay(start, end)) return Task.CompletedTask;

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
    /// <param name="LaneIndex">Zero-based vertical lane assigned to avoid overlapping bars.</param>
    internal sealed record AllDayBar(
        ICalendarEvent Event,
        int FirstColIndex,
        int LastColIndex,
        bool ClipLeft,
        bool ClipRight,
        int LaneIndex);
}
