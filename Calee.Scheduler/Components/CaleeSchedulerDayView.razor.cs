#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Day view for Calee.Scheduler (FR-01). Renders a single calendar day
/// with a day header, all-day row, "+N earlier"/"+N later" overflow chips, and a
/// scrollable hour grid with overlap-laid-out timed events.
/// </summary>
/// <remarks>
/// <para>
/// Implements FR-01, FR-05, FR-06, FR-07, FR-09b, FR-12, FR-13
/// (via <see cref="EventLayoutEngine"/>), FR-14, FR-15 (visible-chunk path),
/// FR-16, FR-17, FR-19, FR-19a, FR-19b, FR-20, FR-21, FR-23, FR-30 (Day portion),
/// FR-31, FR-32, FR-33, FR-53, FR-54, FR-55, NFR-04, NFR-05,
/// NFR-06 (Day portion), NFR-08.
/// </para>
/// <para>
/// Parameter validation follows PRD §4.6: invalid <see cref="StartHour"/>,
/// <see cref="EndHour"/>, or <see cref="SlotDurationMinutes"/> hard-fails with
/// <see cref="ArgumentException"/>; null events soft-degrade through the base.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerDayView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
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
    /// Whether to render a horizontal current-time indicator when "today in
    /// <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>" matches <c>CurrentDate</c>
    /// (FR-07). Defaults to <see langword="true"/>.
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
    /// The library owns the outer positioned rectangle, colored border, and focus
    /// outline; this template fills the content area. See ADR-0002.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventTemplate { get; set; }

    /// <summary>
    /// Optional class hook applied to the day header element (FR-54).
    /// </summary>
    [Parameter]
    public string? DayHeaderClass { get; set; }

    /// <summary>
    /// Optional class hook applied to the time gutter column (FR-54).
    /// </summary>
    [Parameter]
    public string? TimeGutterClass { get; set; }

    /// <summary>
    /// Optional class hook applied to the all-day row (FR-54).
    /// </summary>
    [Parameter]
    public string? AllDayRowClass { get; set; }

    /// <summary>Injected JS runtime, used for the FR-09b scroll-into-view helper and Escape blur.</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // Resolved values after OnParametersSet.
    private int _resolvedStartHour;
    private int _resolvedEndHour;
    private int _resolvedSlotMinutes;
    private int _resolvedMaxOverlapColumns;

    // The layout result for the current render.
    private LayoutResult _layout = new(
        Array.Empty<PositionedEvent>(),
        Array.Empty<ICalendarEvent>(),
        Array.Empty<ICalendarEvent>(),
        Array.Empty<OverlapOverflowBlock>());

    // Frozen-by-construction pre-processed view of the filtered events:
    // owns all-day classification, multi-day splitting, and Id→TEvent lookup.
    private VisibleEventSet<TEvent> _visibleEvents = VisibleEventSet<TEvent>.Empty;

    // Roving-tabindex anchor for the slot grid: the single slot index that is
    // currently tabbable. Arrows move focus between slots; the grid itself is
    // a single tab stop from the consumer's perspective (NFR-06).
    private int _focusedGridSlotIndex;

    // Keyboard move mode state (issue #20 — SC 2.5.7)
    private bool _keyboardMoveMode;
    private string? _keyboardMoveEventId;
    private int _keyboardMovePhantomSlotOffset;
    private DateTimeOffset _keyboardMoveOriginalStart;
    private DateTimeOffset _keyboardMoveOriginalEnd;

    // Day boundary in TimeZone — used by the layout engine and by slot snapping.
    private DateTimeOffset _dayStartLocal;
    private DateTimeOffset _dayEndLocal;

    // Cached DayModifier result for the rendered day (issue #8) — evaluated once per
    // parameter set, not per slot. Null when no hook is configured or the hook returns
    // null for this day ("normal day" in both cases).
    private SchedulerDayState? _dayState;

    // For FR-23 — fire OnRangeChanged only when the visible range actually changes
    // between renders, not on every parameter set.
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
    /// Per-event element refs the drag layer uses as the ghost source (Phase 2 Task 4 — FR-25).
    /// Keyed by event id so a chip whose position in the foreach changes between renders
    /// (e.g., after a drag mutates Start and the layout engine re-sorts the event earlier
    /// in the visible list) still resolves to its captured DOM element. The previous
    /// array-indexed-by-position pattern silently mis-aligned <c>_eventIds</c> and
    /// <c>_eventRefs</c> when Blazor's diff reused a <c>@key</c>-matched chip at a new
    /// position without re-firing the <c>@ref</c> capture for the new slot. Dictionary
    /// entries persist for the lifetime of the chip's mount; if a chip later unmounts
    /// the stale entry is harmless because no foreach iteration will produce its id.
    /// </summary>
    private readonly Dictionary<string, ElementReference> _eventRefsByEventId = new(StringComparer.Ordinal);

    /// <summary>
    /// Optimistic pins for in-flight or just-completed drag-to-move operations
    /// (ADR-0006). The rendering pipeline substitutes <c>(Start, End)</c> in this
    /// dictionary for the consumer-supplied event's authoritative times so the
    /// new position is visible before the consumer's data round-trip completes.
    /// Cleared in <see cref="OnParametersSet"/> when the consumer's authoritative
    /// times have caught up.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End)> _optimisticPin =
        new(StringComparer.Ordinal);

    /// <summary>Number of slots between StartHour and EndHour, derived from <see cref="SlotDurationMinutes"/>.</summary>
    private int SlotCount => (_resolvedEndHour - _resolvedStartHour) * 60 / _resolvedSlotMinutes;

    /// <summary>Total visible minutes in the hour grid (derived).</summary>
    private int VisibleMinutes => (_resolvedEndHour - _resolvedStartHour) * 60;

    /// <summary>The date being rendered (date-only projection of <c>CurrentDate</c>).</summary>
    private DateOnly DayDate => DateOnly.FromDateTime(CurrentDate.Date);

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        var opts = SchedulerOptions.Value;
        _resolvedStartHour = StartHour ?? opts.DefaultStartHour;
        _resolvedEndHour = EndHour ?? opts.DefaultEndHour;
        _resolvedSlotMinutes = SlotDurationMinutes ?? opts.DefaultSlotDurationMinutes;
        _resolvedMaxOverlapColumns = MaxOverlapColumns ?? opts.DefaultMaxOverlapColumns;

        // PRD §4.6 hard-fail validation. The shared helper raises ArgumentException citing
        // the parameter name; rethrow as-is — tests assert on the .Message containing "StartHour".
        SchedulerViewPrimitives.ValidateHourParameters(_resolvedStartHour, _resolvedEndHour, _resolvedSlotMinutes);

        // Compute the day boundary in TimeZone. We treat the date portion of CurrentDate
        // as the local-to-TimeZone day; the offset on CurrentDate itself is irrelevant
        // for choosing which calendar day to render (per PRD §4.6 and FR-09a).
        var localDate = CurrentDate.Date;
        var offsetAtMidnight = TimeZone.GetUtcOffset(localDate);
        _dayStartLocal = new DateTimeOffset(localDate, offsetAtMidnight);
        _dayEndLocal = _dayStartLocal.AddDays(1);

        // Issue #8 — evaluate the per-day state hook once for the rendered day, in the
        // grid time zone (the midnight boundary just computed above), not per slot.
        _dayState = GetDayState(_dayStartLocal);

        // Optimistic-pin housekeeping (ADR-0006). Drop entries the consumer has caught
        // up on — i.e., the consumer's authoritative Start/End for the event now matches
        // the pinned values, so the pin is redundant. We perform this before computing
        // the layout so the engine sees only still-relevant pins.
        ClearAcknowledgedPins();

        // Split filtered events into all-day vs. timed and lay out the timed ones.
        ComputeLayout();

        // FR-23: fire OnRangeChanged when the visible range changes.
        if (_lastRangeStart != _dayStartLocal || _lastRangeEnd != _dayEndLocal)
        {
            _lastRangeStart = _dayStartLocal;
            _lastRangeEnd = _dayEndLocal;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(_dayStartLocal, _dayEndLocal));
        }
    }

    /// <summary>Recompute the all-day list and the timed layout for the current parameter set.</summary>
    private void ComputeLayout()
    {
        // VisibleEventSet owns the filter→classify→split→lookup pipeline. Day view uses
        // PerDay split mode for symmetry with Week view; since the visible range is a single
        // day, multi-day events still produce one chunk per day they touch (Day view sees
        // only the chunk(s) inside its one-day range).
        _visibleEvents = new VisibleEventSet<TEvent>(
            GetFilteredEvents(),
            _dayStartLocal,
            _dayEndLocal,
            TimeZone,
            EventSplitMode.PerDay);

        // Apply any optimistic-pin overrides (ADR-0006). When an event has a pinned
        // (Start, End), we substitute a chunk with the pinned times so the engine
        // (and downstream display) renders the new position. Pinned events stay
        // within the day's bounds — the drop handler clamps via InverseY before
        // pinning, so this transform doesn't accidentally drop the event past the
        // engine's [rangeStart, rangeEnd) filter.
        IReadOnlyList<EventChunk<TEvent>> chunksForLayout = _visibleEvents.TimedChunks;
        if (_optimisticPin.Count > 0)
        {
            chunksForLayout = ApplyOptimisticPins(_visibleEvents.TimedChunks);
        }

        // The engine accepts ICalendarEvent; EventChunk<TEvent> implements it and the chunk's
        // ClippedAtTimeStart/End flags are independent of the engine's hour-clip flags — both
        // can fire (a multi-day chunk that's also clipped by StartHour/EndHour).
        // IReadOnlyList&lt;EventChunk&lt;TEvent&gt;&gt; is covariantly an IReadOnlyList&lt;ICalendarEvent&gt;
        // (EventChunk implements the interface and IReadOnlyList&lt;out T&gt; is covariant) so the
        // chunks flow into the engine directly — no Cast/ToList allocation per render.
        _layout = new EventLayoutEngine().Layout(
            chunksForLayout,
            _dayStartLocal,
            _dayEndLocal,
            _resolvedStartHour,
            _resolvedEndHour,
            _resolvedMaxOverlapColumns);
    }

    /// <summary>
    /// Return a copy of <paramref name="chunks"/> with any pinned events' Start/End
    /// replaced by their pin values. Non-pinned chunks pass through unchanged. The
    /// returned list is freshly allocated only when at least one chunk is rewritten.
    /// </summary>
    private IReadOnlyList<EventChunk<TEvent>> ApplyOptimisticPins(IReadOnlyList<EventChunk<TEvent>> chunks)
    {
        List<EventChunk<TEvent>>? rebuilt = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (_optimisticPin.TryGetValue(c.Id, out var pinned))
            {
                rebuilt ??= new List<EventChunk<TEvent>>(chunks);
                rebuilt[i] = c with { Start = pinned.Start, End = pinned.End };
            }
        }
        return rebuilt is null ? chunks : rebuilt;
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

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await TryLoadModuleAsync();
            _scrollPending = true;
        }

        if (_scrollPending && _jsModule is not null)
        {
            _scrollPending = false;
            try
            {
                // FR-09b: center current-time indicator if today is visible; otherwise
                // scroll to 8 AM clamped to [StartHour, EndHour]. We pass an hour offset
                // (not a pixel offset) so the JS helper reads --calee-scheduler-pixels-per-hour
                // at runtime — consumer overrides of the grid density are respected.
                double hourOffsetFromTop = ComputeInitialScrollHourOffset();
                await _jsModule.InvokeVoidAsync("scrollToHour", _hourGridRef, hourOffsetFromTop);
            }
            catch (JSException)
            {
                // Non-fatal: a missing or broken JS environment must not break rendering.
            }
            catch (InvalidOperationException)
            {
                // Test environment may not expose a real JS runtime.
            }
        }

        // Issue #19 — move real browser focus onto the newly-active slot cell after an
        // arrow-key roving move. Deferred to here (rather than done inline in
        // HandleGridKeyDownAsync) so the query in focusActiveGridCell runs after the
        // tabindex swap has rendered to the DOM — querying before that would find the
        // stale cell.
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

    /// <summary>
    /// Compute the hour-offset from the top of the visible grid for the initial scroll
    /// position. Delegates to <see cref="SchedulerViewPrimitives.ComputeInitialScrollHourOffset"/>
    /// so Day and Week views share one source of truth; pixels-per-hour is read at runtime
    /// by the JS helper from <c>--calee-scheduler-pixels-per-hour</c>.
    /// </summary>
    private double ComputeInitialScrollHourOffset() =>
        SchedulerViewPrimitives.ComputeInitialScrollHourOffset(
            Today,
            _dayStartLocal,
            _dayEndLocal,
            _resolvedStartHour,
            _resolvedEndHour);

    private Task<IJSObjectReference?> TryLoadModuleAsync() =>
        SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);

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
            catch (JSDisconnectedException) { /* Circuit gone; nothing to clean up. */ }
            catch (JSException) { /* Best-effort cleanup. */ }
        }
        await base.DisposeAsync();
    }

    // ----- Internal accessors used by the .razor markup -------------------------------

    /// <summary>Hour labels (inclusive of StartHour, exclusive of EndHour) — e.g., 8 AM, 9 AM, ...</summary>
    internal IEnumerable<int> HourLabels()
    {
        for (var h = _resolvedStartHour; h < _resolvedEndHour; h++)
        {
            yield return h;
        }
    }

    /// <summary>Slot indices used to render gridcells for keyboard nav.</summary>
    internal int SlotCountForRender => SlotCount;

    /// <summary>Convert hour-of-day into a percentage of the visible band.</summary>
    internal double HourToPercent(int hour) =>
        VisibleMinutes <= 0 ? 0 : (hour - _resolvedStartHour) * 60.0 / VisibleMinutes * 100.0;

    /// <summary>Format an hour-of-day for the time gutter (e.g., "8 AM", "12 PM").</summary>
    internal static string FormatHour(int hour) => SchedulerViewPrimitives.FormatHour(hour);

    /// <summary>Format a positioned event's start/end as an accessible time range.</summary>
    internal string FormatEventTimeRange(ICalendarEvent ev) =>
        SchedulerViewPrimitives.FormatEventTimeRange(ev, TimeZone);

    /// <summary>True when the indicator should be rendered (FR-07).</summary>
    internal bool IsTodayInView => Today.Date == CurrentDate.Date && ShowCurrentTimeIndicator;

    /// <summary>Top percentage for the current-time indicator (in the visible band).</summary>
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

    /// <summary>
    /// Accessible name for the slot at the supplied index: "&lt;time&gt;, empty slot" in
    /// the configured time zone. Empty cells need a meaningful announcement so screen-reader
    /// users tabbing into the grid hear "9:00 AM, empty slot" instead of just "gridcell."
    /// When the day is blocked (issue #8) the announcement swaps to the blocked-day
    /// label so a screen-reader user tabbing through the day's slots hears why the day
    /// is inert rather than "empty slot" repeated on every cell.
    /// </summary>
    internal string SlotAccessibleName(int idx)
    {
        var minutes = idx * _resolvedSlotMinutes;
        var time = new DateTime(2000, 1, 1, _resolvedStartHour, 0, 0).AddMinutes(minutes);
        if (IsRenderedDayBlocked)
        {
            var label = _dayState?.Label;
            return string.IsNullOrEmpty(label) ? $"{time:h:mm tt}, blocked" : $"{time:h:mm tt}, {label}";
        }
        return $"{time:h:mm tt}, empty slot";
    }

    // ----- Blocked days (issue #8) -----------------------------------------------------

    /// <summary>True when the rendered day is blocked per <see cref="SchedulerComponentBase{TEvent}.DayModifier"/>.</summary>
    internal bool IsRenderedDayBlocked => _dayState?.IsBlocked ?? false;

    /// <summary>Consumer-supplied per-day class hook for the rendered day, or null.</summary>
    internal string? DayBlockedClass => _dayState?.Class;

    /// <summary>Accessible label announced on the day header when the day is blocked.</summary>
    internal string BlockedDayHeaderLabel => SchedulerViewPrimitives.BlockedDayAccessibleLabel(_dayStartLocal, _dayState);

    // ----- Day header template + click (issue #9) -------------------------------------

    /// <summary>
    /// True when <see cref="SchedulerComponentBase{TEvent}.OnDayHeaderClicked"/> has a
    /// delegate wired. Gates whether the header cell is rendered as focusable/
    /// interactive at all — fail-closed default (see the parameter's remarks).
    /// </summary>
    internal bool IsDayHeaderInteractive => OnDayHeaderClicked.HasDelegate;

    /// <summary>Accessible name for the day header when it is interactive and not blocked.</summary>
    internal string DayHeaderAccessibleName => SchedulerViewPrimitives.DayHeaderAccessibleName(_dayStartLocal);

    /// <summary>
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnDayHeaderClicked"/> with the
    /// rendered day's midnight boundary in <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>.
    /// No-op while a drag is active (ADR-0006 precedence — matches every other click
    /// handler in the view).
    /// </summary>
    internal Task HandleDayHeaderClickAsync()
    {
        if (IsDragActive) return Task.CompletedTask;
        return OnDayHeaderClicked.InvokeAsync(_dayStartLocal);
    }

    /// <summary>
    /// Keyboard handler bound to the day header when <see cref="IsDayHeaderInteractive"/>.
    /// Enter and Space activate the header; every other key is a no-op (the header is
    /// not part of the roving-tabindex grid).
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
    internal Task HandleDayHeaderKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key != "Enter" && e.Key != " ") return Task.CompletedTask;
        return HandleDayHeaderClickAsync();
    }

    /// <summary>
    /// Issue #8 — the Day view's single grid-focus concept is "this whole rendered
    /// day," so the create-at-focus suppression check is just <see cref="IsRenderedDayBlocked"/>.
    /// </summary>
    private protected override bool IsFocusedGridDayBlocked() => IsRenderedDayBlocked;

    /// <summary>Compute whether the all-day event is clipped on the left (continues from previous day).</summary>
    internal bool AllDayClippedLeft(TEvent ev) => ev.Start < _dayStartLocal;

    /// <summary>Compute whether the all-day event is clipped on the right (continues to next day).</summary>
    internal bool AllDayClippedRight(TEvent ev) => ev.End > _dayEndLocal;

    /// <summary>Compute an accessible name for the all-day event.</summary>
    internal string AllDayAccessibleName(TEvent ev) =>
        $"{ev.Title}, all day on {CurrentDate:D}";

    /// <summary>Accessible name for a "+N" overlap block.</summary>
    internal string OverlapBlockAccessibleName(OverlapOverflowBlock block)
    {
        var start = TimeZoneInfo.ConvertTime(block.RegionStart, TimeZone);
        var end = TimeZoneInfo.ConvertTime(block.RegionEnd, TimeZone);
        return $"{block.Events.Count} more events from {start:h:mm tt} to {end:h:mm tt}, activate to choose";
    }

    /// <summary>The positioned timed events to render in the hour grid.</summary>
    internal IReadOnlyList<PositionedEvent> PositionedEvents => _layout.Positioned;

    /// <summary>Overlap-overflow blocks for the day.</summary>
    internal IReadOnlyList<OverlapOverflowBlock> OverlapBlocks => _layout.OverlapOverflow;

    /// <summary>Returns the underlying consumer TEvent for a positioned event (unwraps chunks via Id).</summary>
    internal TEvent? TypedFor(ICalendarEvent ev) => _visibleEvents.FindById(ev.Id);

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

    /// <summary>Returns the underlying consumer event (unwraps an <see cref="EventChunk{TEvent}"/>) for formatting.</summary>
    internal ICalendarEvent UnwrapForFormatting(ICalendarEvent ev) =>
        ev is EventChunk<TEvent> c ? c.Event : ev;

    /// <summary>True when the positioned event was clipped at the top (either source).</summary>
    internal bool ClippedTop(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtStart(pe);

    /// <summary>True when the positioned event was clipped at the bottom (either source).</summary>
    internal bool ClippedBottom(PositionedEvent pe) => EventChunk<TEvent>.IsClippedAtEnd(pe);

    /// <summary>The list of all-day events that fall on the current day.</summary>
    internal IReadOnlyList<TEvent> AllDayEvents => _visibleEvents.AllDay;

    /// <summary>Count of events entirely earlier than StartHour.</summary>
    internal int EarlierOverflowCount => _layout.EarlierOverflow.Count;

    /// <summary>Count of events entirely later than EndHour.</summary>
    internal int LaterOverflowCount => _layout.LaterOverflow.Count;

    // ----- Event handlers ------------------------------------------------------------

    /// <summary>Fire OnEventClicked with the original TEvent and update the selection.</summary>
    /// <remarks>
    /// <para>
    /// Click handling has two side effects: it fires <see cref="SchedulerComponentBase{TEvent}.OnEventClicked"/>
    /// (the original Phase 1 contract — unchanged) AND it mutates the selection via
    /// <see cref="SchedulerComponentBase{TEvent}.ApplyClickSelectionAsync"/> when the
    /// consumer has wired the selection surface (FR-34 / Phase 2 Task 10). Both fires
    /// happen on every click — consumers using only the click callback see no
    /// difference; consumers that also wired <c>OnSelectionChanged</c> see the typed
    /// selection list flow through alongside the per-event click.
    /// </para>
    /// <para>
    /// Render order for Shift+click range select is the engine's positioned-event
    /// order (earliest start first; ties broken by the engine's deterministic stack
    /// assignment), enriched with the in-band all-day events at the front so a
    /// Shift+click that spans the timed/all-day boundary still produces a contiguous
    /// range. <c>EventChunk&lt;TEvent&gt;.Id</c> forwards to the underlying consumer
    /// event, so the same id appears at most once across timed + all-day buckets.
    /// </para>
    /// </remarks>
    internal Task HandleEventClickAsync(ICalendarEvent ev, MouseEventArgs? args = null)
    {
        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null)
        {
            return Task.CompletedTask;
        }
        return DispatchClickAsync(typed, ev.Id, args);
    }

    private async Task DispatchClickAsync(TEvent typed, string clickedId, MouseEventArgs? args)
    {
        // Selection mutation is mouse-only in Task 10 — the keyboard equivalents
        // (Space toggle, Esc clear, Shift+Arrow grow) wire up in Task 11. Enter on a
        // focused event continues to fire OnEventClicked without altering selection
        // so the Phase 1 keyboard path is unchanged when AllowMultiSelect is not in
        // play. `args is null` distinguishes the keyboard-Enter path from a real
        // pointer click.
        if (args is not null)
        {
            var ctrlOrMeta = args.CtrlKey || args.MetaKey;
            var shift = args.ShiftKey;
            var renderOrder = ComputeRenderOrderIds();
            var changed = await ApplyClickSelectionAsync(clickedId, ctrlOrMeta, shift, renderOrder);
            if (changed && IsStandalone)
            {
                // Standalone path: ApplyClickSelectionAsync mutated _localSelection
                // already; flag a re-render so the new visual class shows immediately.
                // The cascade path is owned by the root scheduler's
                // HandleRequestSelectionChangeAsync, which StateHasChanged()s and
                // re-pushes the cascade — calling here too would be redundant.
                StateHasChanged();
            }
        }
        await OnEventClicked.InvokeAsync(typed);
    }

    /// <summary>
    /// Render-order id list used by <see cref="SchedulerComponentBase{TEvent}.ApplyClickSelectionAsync"/>'s
    /// Shift+click range computation. Day view orders all-day events first (the
    /// banner strip sits above the hour grid visually) followed by the engine's
    /// positioned timed events (earliest start first). Ids are deduplicated as the
    /// base walks the list, so emitting them once each is sufficient.
    /// </summary>
    private IReadOnlyList<string> ComputeRenderOrderIds()
    {
        var allDay = _visibleEvents.AllDay;
        var positioned = _layout.Positioned;
        var ids = new List<string>(allDay.Count + positioned.Count);
        for (var i = 0; i < allDay.Count; i++) ids.Add(allDay[i].Id);
        for (var i = 0; i < positioned.Count; i++) ids.Add(positioned[i].Event.Id);
        return ids;
    }

    /// <summary>Fire OnDayOverflowClicked for the "+N earlier" chip.</summary>
    internal Task HandleEarlierChipClickAsync() =>
        OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            DayDate, OverflowKind.Earlier, MapToTyped(_layout.EarlierOverflow)));

    /// <summary>Fire OnDayOverflowClicked for the "+N later" chip.</summary>
    internal Task HandleLaterChipClickAsync() =>
        OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            DayDate, OverflowKind.Later, MapToTyped(_layout.LaterOverflow)));

    /// <summary>Fire OnDayOverflowClicked for a "+N" overlap block.</summary>
    internal Task HandleOverlapChipClickAsync(OverlapOverflowBlock block) =>
        OnDayOverflowClicked.InvokeAsync(new DayOverflowContext<TEvent>(
            DayDate, OverflowKind.Overlap, MapToTyped(block.Events),
            RegionStart: block.RegionStart, RegionEnd: block.RegionEnd));

    /// <summary>
    /// Fire OnSlotClicked for a clicked slot. The supplied slot index is 0..SlotCount-1.
    /// The emitted SchedulerSlot start/end carry TimeZone's offset (FR-21).
    /// After the callback resolves, removes focus from any focused event chip so the
    /// "clicking off an event clears its focus ring" mental model holds even when
    /// the clicked slot itself has tabindex=-1 (would not naturally receive focus).
    /// </summary>
    internal async Task HandleSlotClickAsync(int slotIndex)
    {
        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var endMinutes = startMinutes + _resolvedSlotMinutes;
        var start = _dayStartLocal.AddMinutes(startMinutes);
        var end = _dayStartLocal.AddMinutes(endMinutes);
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(start, end));
        await BlurActiveEventChipAsync();
    }

    /// <summary>
    /// Keyboard handler on the hour-grid. Implements arrow-based navigation between
    /// slot cells (roving tabindex), Enter to fire OnSlotClicked, and Escape to
    /// either clear a non-empty selection (Phase 2 Task 11 — FR-34) or release
    /// focus (FR-30, fallback for the empty-selection case).
    /// </summary>
    internal async Task HandleGridKeyDownAsync(KeyboardEventArgs e)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch (FR-36). The base's
        // TryDispatchShortcutAsync replaces Task 13's TryDispatchUndoRedoAsync: it
        // matches against the resolved (DefaultMap + DisabledShortcuts + ShortcutMap)
        // bindings and dispatches the matched command. Returns true when a command
        // was dispatched (caller short-circuits); false otherwise (caller falls through
        // to the per-key switch). IsDragActive precedence unchanged — the JS pointer
        // module owns cancel keystrokes mid-drag.
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomSlotOffset = Math.Max(
                        -_focusedGridSlotIndex,
                        _keyboardMovePhantomSlotOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origIdx = GetKeyboardMoveOriginalSlotIndex();
                    _keyboardMovePhantomSlotOffset = Math.Min(
                        SlotCount - 1 - origIdx,
                        _keyboardMovePhantomSlotOffset + 1);
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
                _focusedGridSlotIndex = Math.Min(SlotCount - 1, _focusedGridSlotIndex + 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "ArrowUp":
                _focusedGridSlotIndex = Math.Max(0, _focusedGridSlotIndex - 1);
                _focusMovePending = true;
                StateHasChanged();
                break;
            case "Enter":
                await HandleSlotClickAsync(_focusedGridSlotIndex);
                break;
        }
    }

    /// <summary>
    /// Keyboard handler on an event card. Enter fires <c>OnEventClicked</c> via the
    /// existing keyboard-Enter path (no selection mutation — matches the Phase 1
    /// contract). Space toggles the focused chip in/out of the selection when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> is enabled
    /// (FR-34); when disabled it returns without preventing the browser default so
    /// the synthesized click on the focused button drives a single-id selection
    /// through the existing click path (FR-29 fail-closed). Escape clears a
    /// non-empty selection (Task 11) or falls through to the FR-30 blur behavior
    /// when the selection is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Esc-mid-drag precedence.</strong> When a drag is in flight the JS
    /// pointer-events module owns Esc — its window-level <c>keydown</c> listener
    /// calls <c>preventDefault</c> + <c>fireCancel</c>, routing through the C# drop
    /// handler's cancel branch (no-op per ADR-0006). The C# Esc-clears-selection
    /// path is gated on <c>!IsDragActive</c> so the same keystroke never doubles as
    /// "abort the drag AND clear the selection." The selection survives a drag the
    /// user aborts.
    /// </para>
    /// <para>
    /// <strong>Space + browser default.</strong> Space on a focused
    /// <see langword="button"/> defaults to dispatching a synthesized <c>click</c>
    /// on key-up. The component's <c>@onkeydown:preventDefault</c> directive is
    /// bound to <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> so
    /// the default fires when multi-select is off (producing the expected single-
    /// id selection via the click path) and is suppressed when multi-select is on
    /// (so the chip is not also activated immediately after the toggle).
    /// </para>
    /// </remarks>
    internal async Task HandleEventKeyDownAsync(KeyboardEventArgs e, ICalendarEvent ev)
    {
        // Phase 2 Task 14 — route through the shortcut-map dispatch. The base's
        // TryDispatchShortcutAsync matches the keystroke against the resolved map
        // and dispatches the matched command. Chip-scope keys (Space, Delete, Esc,
        // letter shortcuts, modifier shortcuts) flow through; for view-specific
        // commands the base calls back into DispatchViewCommandAsync (below) with
        // the focused event passed through here. IsDragActive precedence unchanged.
        if (IsDragActive) return;

        // Handle arrow keys in keyboard move mode (issue #20 — SC 2.5.7)
        if (_keyboardMoveMode && _keyboardMoveEventId is not null)
        {
            switch (e.Key)
            {
                case "ArrowUp":
                    _keyboardMovePhantomSlotOffset = Math.Max(
                        -_focusedGridSlotIndex,
                        _keyboardMovePhantomSlotOffset - 1);
                    await UpdateKeyboardMovePhantomPositionAsync();
                    return;
                case "ArrowDown":
                    var origIdx = GetKeyboardMoveOriginalSlotIndex();
                    _keyboardMovePhantomSlotOffset = Math.Min(
                        SlotCount - 1 - origIdx,
                        _keyboardMovePhantomSlotOffset + 1);
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

        // Phase 1 contracts that aren't part of the shortcut map remain inline:
        // Enter still fires OnEventClicked without mutating selection.
        if (e.Key == "Enter")
        {
            await HandleEventClickAsync(ev);
        }
    }

    /// <summary>
    /// View-specific command dispatch for the chip-scope shortcuts the base can't
    /// resolve on its own — Space toggle (needs the click-path render-order id list),
    /// Delete (needs the base's <c>TryDeleteFocusedEventAsync</c> with a typed event),
    /// and Escape (needs the view's blur target). Mirrors the per-key switch the chip
    /// handler used pre-Task-14; the dispatch table on the base routes the keystroke
    /// here when a matched command falls into one of these three buckets.
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
                // Resolve back to ICalendarEvent for HandleEventClickAsync; we route
                // through the click path with a synthetic Ctrl-modifier so the
                // selection-toggle + OnEventClicked fires from the single click code
                // path (mirrors Task 11's Space-toggle implementation).
                var underlying = _visibleEvents.FindById(focusedEventId) as ICalendarEvent
                    ?? (ICalendarEvent?)focusedEvent;
                if (underlying is null) return false;
                await HandleEventClickAsync(underlying, new MouseEventArgs { CtrlKey = true });
                return true;
            case SchedulerCommandIds.EditDelete:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null || focusedEvent is null) return false;
                // The base's AllowDelete gate already passed; route through the
                // existing HandleDeleteAsync so the IsStandalone re-render path
                // stays in one place.
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
        _keyboardMoveOriginalStart = focusedEvent.Start;
        _keyboardMoveOriginalEnd = focusedEvent.End;

        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var slotMinutes = _resolvedSlotMinutes;
        var currentSlotIndex = (int)((focusedEvent.Start - visibleStart).TotalMinutes / slotMinutes);

        var request = new KeyboardMoveRequest
        {
            Event = focusedEvent,
            CurrentSlotIndex = currentSlotIndex,
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

    private async Task UpdateKeyboardMovePhantomPositionAsync()
    {
        if (_keyboardMoveEventId is null) return;

        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var slotMinutes = _resolvedSlotMinutes;
        var originalSlotIndex = (int)((_keyboardMoveOriginalStart - visibleStart).TotalMinutes / slotMinutes);
        var newSlotIndex = Math.Clamp(originalSlotIndex + _keyboardMovePhantomSlotOffset, 0, SlotCount - 1);
        var newStart = visibleStart.AddMinutes(newSlotIndex * slotMinutes);
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
        var newEnd = newStart + duration;

        _optimisticPin[_keyboardMoveEventId] = (newStart, newEnd);
        ComputeLayout();
        StateHasChanged();
    }

    private async Task CommitKeyboardMoveAsync()
    {
        if (_keyboardMoveEventId is null) return;

        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var slotMinutes = _resolvedSlotMinutes;
        var originalSlotIndex = (int)((_keyboardMoveOriginalStart - visibleStart).TotalMinutes / slotMinutes);
        var newSlotIndex = Math.Clamp(originalSlotIndex + _keyboardMovePhantomSlotOffset, 0, SlotCount - 1);
        var newStart = visibleStart.AddMinutes(newSlotIndex * slotMinutes);
        var duration = _keyboardMoveOriginalEnd - _keyboardMoveOriginalStart;
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
    }

    private int GetKeyboardMoveOriginalSlotIndex()
    {
        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var slotMinutes = _resolvedSlotMinutes;
        return (int)((_keyboardMoveOriginalStart - visibleStart).TotalMinutes / slotMinutes);
    }

    /// <summary>
    /// Shared Delete behavior — defers to the JS module during a drag (ADR-0006
    /// precedence: the drag layer owns cancel keys), then resolves the focused
    /// chip's id to a typed <typeparamref name="TEvent"/> and dispatches through
    /// the base's <see cref="SchedulerComponentBase{TEvent}.TryDeleteFocusedEventAsync"/>
    /// helper. The base picks single vs batch based on selection-set size + focused-
    /// chip-in-set membership; this view's job is just to gate on
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDelete"/> and resolve the
    /// click target's underlying consumer event.
    /// </summary>
    private async Task HandleDeleteAsync(ICalendarEvent ev)
    {
        if (!AllowDelete) return;
        if (IsDragActive) return;

        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null) return;

        var changed = await TryDeleteFocusedEventAsync(ev.Id, typed);
        if (changed && IsStandalone)
        {
            // Standalone path: ApplyNewSelectionAsync mutated _localSelection
            // already; flag a re-render so the new aria-selected / CSS-class state
            // applies on the next paint. The cascade path is owned by the root
            // scheduler's HandleRequestSelectionChangeAsync, which StateHasChanged()s
            // and re-pushes the cascade — calling here too would be redundant.
            StateHasChanged();
        }
    }

    /// <summary>
    /// Shared Escape behavior used by both <see cref="HandleGridKeyDownAsync"/> and
    /// <see cref="HandleEventKeyDownAsync"/>: when a drag is in flight defer to the
    /// JS module's cancel path (ADR-0006); when a selection is held clear it (FR-34
    /// keyboard); otherwise blur the active element (FR-30).
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

    /// <summary>Returns the slot index that is currently tabbable (roving tabindex).</summary>
    internal int FocusedGridSlotIndex => _focusedGridSlotIndex;

    /// <summary>Returns true when the supplied slot index should be tabbable.</summary>
    internal bool IsSlotTabbable(int slotIndex) => slotIndex == _focusedGridSlotIndex;

    private async Task BlurActiveAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActive"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    /// <summary>
    /// Blur the active element only if it is an event chip (data-calee-region="event").
    /// Used by the slot-click handler — see remarks on <see cref="HandleSlotClickAsync"/>.
    /// </summary>
    private async Task BlurActiveEventChipAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActiveIfEvent"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    // ----- Drag-to-move (Phase 2 Task 4 — FR-25) --------------------------------------

    /// <summary>
    /// Test-only forwarder for the base's <c>private protected IsDragActive</c> —
    /// lets tests confirm a real drag (started via <see cref="OnEventPointerDownAsync"/>)
    /// is active before asserting on drag-active precedence elsewhere (e.g. the day
    /// header's click/keydown guard, issue #9).
    /// </summary>
    internal bool IsDragActiveForTest => IsDragActive;

    /// <summary>The dictionary the .razor template binds via
    /// <c>@ref="EventRefsByEventId[eventId]"</c>. Internal so tests can inspect the
    /// captured set if they need to (no test references it today).</summary>
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
    /// branch is a no-op per ADR-0006 — mid-drag cancel never pinned anything,
    /// so there's nothing to revert. No-op when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToMove"/> is false
    /// (defensive — the handler isn't bound in that case anyway).
    /// </summary>
    internal async Task OnEventPointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToMove) return;
        // Only primary button (left mouse / first touch) starts a drag. Filters out
        // right-click + middle-click + auxiliary buttons. Button==0 is "primary"
        // per the PointerEvent spec; touch always reports 0.
        if (e.Button != 0) return;

        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null) return;

        // Look up the chip's element ref by event id. Captures populate the dict on
        // first mount and stay valid across @key-matched reorders — the array-indexed
        // pattern this replaces silently mis-aligned _eventIds and _eventRefs when a
        // re-render reused a chip at a new position without re-firing its @ref.
        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;

        var durationMinutes = (ev.End - ev.Start).TotalMinutes;
        var eventDurationSlots = (int)Math.Ceiling(durationMinutes / _resolvedSlotMinutes);
        var slotHeightPx = gridHeightPx / Math.Max(1, SlotCount);
        var eventDurationPixels = eventDurationSlots * slotHeightPx;

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.Move,
            snapPixelsX: 0,                       // Day view doesn't cross days horizontally.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleMoveDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            highlightContainer: _hourGridRef,
            highlightMode: "slot-band",
            eventDurationPixels: eventDurationPixels,
            eventDurationSlots: eventDurationSlots,
            slotCount: SlotCount);
    }

    /// <summary>
    /// Drop handler. Converts the JS drop coordinates into a new (Start, End)
    /// preserving the event's duration, optimistically pins the new position,
    /// fires <see cref="SchedulerComponentBase{TEvent}.OnEventMoved"/>, and
    /// rolls the pin back if the consumer set <see cref="EventMoveContext.Cancel"/>.
    /// </summary>
    private async Task HandleMoveDropAsync(TEvent ev, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            // No usable grid geometry (test environment without a real DOM). Fall back
            // to the default 56 pixels/hour from the CSS variable so tests exercising
            // the drop branch get exact, deterministic snap math without having to
            // stub getElementHeight. Production always hits the JS-measured path.
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }

        // Compute new Start. The hour-grid container covers [StartHour, EndHour) of
        // the day, so InverseY's "range" is [_dayStartLocal + StartHour, _dayStartLocal + EndHour).
        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var visibleEnd = _dayStartLocal.AddHours(_resolvedEndHour);

        // payload.FinalTopPx is the ghost's final viewport top; the grid is positioned
        // somewhere else. The delta-based path is more robust: the original chunk's
        // pre-drop top inside the grid plus DeltaYPx is the new top inside the grid.
        // Compute the original event's top-in-grid from its time, then add delta.
        var origStartMinutes = (ev.Start - visibleStart).TotalMinutes;
        var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridHeightPx;
        var newStartPxInGrid = (origStartMinutes / minutesPerPx) + payload.DeltaYPx;

        var newStart = EventLayoutEngine.InverseY(
            pixelY: newStartPxInGrid,
            totalHeightPx: gridHeightPx,
            rangeStart: visibleStart,
            rangeEndExclusive: visibleEnd,
            slotMinutes: _resolvedSlotMinutes);

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
            // NewLaneId stays null — Day view has no lanes (ADR-0011).
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
    /// Returns the <c>aria-roledescription</c> string the .razor template stamps on each
    /// event chip — combines the move + resize affordances per the centralized rule in
    /// <see cref="SchedulerComponentBase{TEvent}.GetEventChipAriaRoleDescription"/>.
    /// </summary>
    internal string? EventAriaRoleDescription() => GetEventChipAriaRoleDescription();

    /// <summary>
    /// Pointer-down handler bound to the bottom-edge resize hit-zone of each event
    /// chip when <see cref="SchedulerComponentBase{TEvent}.AllowDragToResize"/> is true.
    /// Starts a resize-end drag via the base's <c>BeginDragOnPointerAsync</c> with
    /// <see cref="ResizeAxis.Y"/> (the bottom edge of the ghost grows/shrinks while the
    /// top is anchored). The drop branch routes to <see cref="HandleResizeDropAsync"/>;
    /// the cancel branch is a no-op per ADR-0006 — mid-drag cancel never pinned anything.
    /// </summary>
    internal async Task OnEventResizePointerDownAsync(PointerEventArgs e, ICalendarEvent ev)
    {
        if (!AllowDragToResize) return;
        // Only primary button starts a resize. Touch always reports 0.
        if (e.Button != 0) return;

        var typed = _visibleEvents.FindById(ev.Id);
        if (typed is null) return;

        if (!_eventRefsByEventId.TryGetValue(typed.Id, out var sourceRef))
        {
            return;
        }

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;

        var slotHeightPx = gridHeightPx / Math.Max(1, SlotCount);

        await BeginDragOnPointerAsync(
            e,
            sourceRef,
            DragMode.ResizeEnd,
            snapPixelsX: 0,                       // Day view doesn't resize across the X axis.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleResizeDropAsync(typed, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            highlightContainer: _hourGridRef,
            highlightMode: "slot-band",
            eventDurationPixels: slotHeightPx,
            eventDurationSlots: 1,
            slotCount: SlotCount);
    }

    /// <summary>
    /// Drop handler for the resize-end drag. Converts the JS drop's vertical delta into
    /// a new <c>End</c> instant, snapped to the slot boundary; preserves <c>Start</c>;
    /// applies the optimistic pin (shared with drag-to-move); fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnEventResized"/>; rolls the pin back
    /// when the consumer sets <see cref="EventResizeContext.Cancel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Minimum-duration clamp.</strong> When the user drags the bottom edge above
    /// (or past) the event's <c>Start</c>, the new End is clamped to
    /// <c>Start + SlotDurationMinutes</c> (one slot). The library's job is to keep
    /// <c>NewEnd &gt; NewStart</c>; producing a degenerate or inverted range would force
    /// every consumer to defend against it.
    /// </para>
    /// <para>
    /// <strong>End-of-band clamp.</strong> Dragging past the visible <c>EndHour</c>
    /// floor clamps to the band end (the visible grid's last instant). This mirrors the
    /// move-mode clamp behavior.
    /// </para>
    /// </remarks>
    private async Task HandleResizeDropAsync(TEvent ev, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            // Same fallback geometry as the move-drop handler. Production always hits
            // the JS-measured path.
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }

        var visibleStart = _dayStartLocal.AddHours(_resolvedStartHour);
        var visibleEnd = _dayStartLocal.AddHours(_resolvedEndHour);

        // Compute the new End by adding the snapped DeltaYPx to the event's original
        // pre-drop End-pixel-in-grid, then snapping that pixel back to a time. The same
        // delta-based path as drag-to-move keeps the math robust against the grid's
        // viewport offset.
        var origEndMinutes = (ev.End - visibleStart).TotalMinutes;
        var minutesPerPx = (_resolvedEndHour - _resolvedStartHour) * 60.0 / gridHeightPx;
        var newEndPxInGrid = (origEndMinutes / minutesPerPx) + payload.DeltaYPx;

        // Snap the End pixel to a slot boundary. We use a local snap formula rather than
        // InverseY because InverseY's contract is "snap to the nearest slot *start*"
        // (clamped to rangeEnd - slotMinutes), but End is allowed to land *on* the band's
        // exclusive end (e.g., the band is 8:00–18:00 and the event ends at exactly 18:00).
        var totalMinutes = (visibleEnd - visibleStart).TotalMinutes;
        var minutesFromStartUnclamped = newEndPxInGrid / gridHeightPx * totalMinutes;
        var snappedMinutes = Math.Round(
            minutesFromStartUnclamped / _resolvedSlotMinutes,
            MidpointRounding.AwayFromZero) * _resolvedSlotMinutes;
        // Clamp upper bound to the band end (inclusive — a chip can end at 18:00 when
        // the band is 8:00–18:00). Lower bound is set after, via the min-duration rule.
        if (snappedMinutes > totalMinutes) snappedMinutes = totalMinutes;
        if (snappedMinutes < 0) snappedMinutes = 0;

        var newEnd = visibleStart.AddMinutes(snappedMinutes);
        var minEnd = ev.Start.AddMinutes(_resolvedSlotMinutes);
        if (newEnd < minEnd)
        {
            // Minimum-duration clamp: keep NewEnd > Start by exactly one slot.
            newEnd = minEnd;
        }

        // Optimistic pin shares storage with the move-drop pin (FR-26 + ADR-0006):
        // a per-event pin is a per-event pin. Start stays equal to the event's
        // pre-drop Start; only End changes.
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

    /// <summary>
    /// Test-only entry point for keyboard move dispatch (issue #20). Lets the test
    /// project exercise the keyboard move pipeline without driving real keystrokes.
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
    /// Test-only flag: whether keyboard move mode is currently active (issue #20).
    /// </summary>
    internal bool IsKeyboardMoveModeForTest => _keyboardMoveMode;

    /// <summary>
    /// Test-only access to the phantom slot offset (issue #20).
    /// </summary>
    internal int KeyboardMovePhantomSlotOffsetForTest => _keyboardMovePhantomSlotOffset;

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) -------------------------------------

    /// <summary>
    /// Pointer-down handler bound to each slot cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDragToCreate"/> is true. Starts a
    /// <see cref="DragMode.CreateRegion"/> drag anchored at the slot's row, growing
    /// vertically as the cursor moves. Below the 5 px threshold the JS module fires
    /// <c>onCancel</c> and the slot's own <c>@onclick</c> drives <c>OnSlotClicked</c>
    /// unchanged — small jitter is never interpreted as a create. Above the threshold
    /// the JS module fires <c>onDrop</c> and routes here to
    /// <see cref="HandleCreateDropAsync"/>.
    /// </summary>
    /// <param name="e">The originating pointer event (viewport coords + button info).</param>
    /// <param name="anchorSlotIndex">The slot index the user pressed on — used as the
    /// rectangle's anchor row in the inverse mapping. Bidirectional drag is normalized
    /// in the drop handler.</param>
    internal async Task OnGridPointerDownAsync(PointerEventArgs e, int anchorSlotIndex)
    {
        if (!AllowDragToCreate) return;
        // Only primary button starts a create. Touch always reports 0.
        if (e.Button != 0) return;
        // Issue #8 — fail-closed: don't even start the drag on a blocked day, so no
        // ghost rectangle is ever drawn over it.
        if (IsRenderedDayBlocked) return;

        var gridHeightPx = await GetHourGridHeightPxAsync();
        var snapPixelsY = (gridHeightPx > 0 && SlotCount > 0) ? gridHeightPx / SlotCount : 0;

        await BeginDragOnPointerAsync(
            e,
            _hourGridRef,
            DragMode.CreateRegion,
            snapPixelsX: 0,                      // Day view doesn't cross days horizontally.
            snapPixelsY: snapPixelsY,
            ghostClass: "calee-scheduler-event--ghost",
            onDrop: payload => HandleCreateDropAsync(anchorSlotIndex, payload),
            onCancel: static () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            anchorViewportX: e.ClientX,
            anchorViewportY: e.ClientY,
            thresholdPx: 5);
    }

    /// <summary>
    /// Drop handler for a drag-to-create. Computes the spanned (Start, End) in the
    /// view's TimeZone, fires <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/>,
    /// then exits — the library does NOT render an optimistic phantom event (Option A in
    /// the Task 8 lifecycle decision; see the commit body). The consumer typically opens
    /// an editor; on save it pushes the new event back through
    /// <see cref="SchedulerComponentBase{TEvent}.Events"/> and the next render shows the
    /// chip. <see cref="EventCreateContext.Cancel"/> = true means "do not create" — under
    /// Option A there is nothing to revert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Bidirectional drag.</strong> The user may drag downward (positive DeltaY)
    /// or upward (negative DeltaY) from the anchor. The handler normalizes via
    /// <c>min(anchor, final)</c> / <c>max(anchor, final)</c> so the resulting Start is
    /// always before End — C# never assumes the cursor moved in a "positive" direction.
    /// </para>
    /// <para>
    /// <strong>Snap-on-drop.</strong> The JS module already snapped <c>DeltaYPx</c> to a
    /// slot-height multiple, so the computed final slot is exact. The anchor is
    /// already a slot boundary (we use the slot cell's index directly).
    /// </para>
    /// </remarks>
    private async Task HandleCreateDropAsync(int anchorSlotIndex, DropPayload payload)
    {
        var gridHeightPx = await GetHourGridHeightPxAsync();
        if (gridHeightPx <= 0)
        {
            // Same fallback geometry as the move/resize drop handlers. Production
            // always hits the JS-measured path.
            gridHeightPx = 56.0 * (_resolvedEndHour - _resolvedStartHour);
        }
        var slotHeightPx = gridHeightPx / SlotCount;

        // Compute final slot index by adding the rounded delta-Y-row shift. JS already
        // snapped DeltaYPx to slotHeightPx; we re-round defensively in case the test seam
        // passes a non-snapped value or floating-point arithmetic introduced rounding.
        var slotShift = slotHeightPx > 0
            ? (int)Math.Round(payload.DeltaYPx / slotHeightPx, MidpointRounding.AwayFromZero)
            : 0;
        var finalSlotIndex = Math.Clamp(anchorSlotIndex + slotShift, 0, SlotCount - 1);

        // Normalize: Start at the earlier slot's top edge, End at the later slot's
        // bottom edge (i.e., one slot past the last covered slot). This keeps the
        // spanned region inclusive of both endpoints and gives a minimum duration of
        // one slot for a movement of exactly one slot.
        var startSlot = Math.Min(anchorSlotIndex, finalSlotIndex);
        var endSlot = Math.Max(anchorSlotIndex, finalSlotIndex) + 1;
        // endSlot can equal SlotCount when the user drags to the very last slot — the
        // resulting End lands exactly on EndHour, which is the canonical band-end value
        // every other view uses too.
        if (endSlot > SlotCount) endSlot = SlotCount;

        var startMinutes = _resolvedStartHour * 60 + startSlot * _resolvedSlotMinutes;
        var endMinutes = _resolvedStartHour * 60 + endSlot * _resolvedSlotMinutes;
        var start = _dayStartLocal.AddMinutes(startMinutes);
        var end = _dayStartLocal.AddMinutes(endMinutes);

        // Issue #8 — fail-closed: if the swept region touches a blocked day, the
        // create does not fire. Day view's drag never leaves the anchor day, so this
        // reduces to IsRenderedDayBlocked, but the shared helper is used for consistency with
        // Week/Month and to stay correct if that ever changes.
        if (CreateSpanTouchesBlockedDay(start, end)) return;

        var context = new EventCreateContext
        {
            Slot = new SchedulerSlot(start, end),
        };
        await OnEventCreated.InvokeAsync(context);

        // Option A: no library-rendered state to revert when Cancel=true. The consumer's
        // own data flow is responsible for the visual; on Cancel the consumer simply
        // doesn't push a new event back.
    }

    /// <summary>
    /// Test-only entry point for the create drop-handling pipeline. Lets the test project
    /// exercise the callback flow without driving a real pointer-drag sequence through JS
    /// interop (which bUnit's headless DOM cannot produce). Mirrors
    /// <see cref="InvokeMoveDropForTestAsync"/> / <see cref="InvokeResizeDropForTestAsync"/>.
    /// </summary>
    /// <param name="anchorSlotIndex">The slot the synthetic create anchors on (closure-captured by the JS path in production).</param>
    /// <param name="payload">The synthetic <see cref="DropPayload"/>; only <c>DeltaYPx</c> is consumed by Day view.</param>
    internal Task InvokeCreateDropForTestAsync(int anchorSlotIndex, DropPayload payload) =>
        HandleCreateDropAsync(anchorSlotIndex, payload);

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -----------------------------

    /// <summary>
    /// Double-click handler bound to each slot cell when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowDoubleClickToCreate"/> is true.
    /// Fires <see cref="SchedulerComponentBase{TEvent}.OnEventCreated"/> with a
    /// <see cref="SchedulerSlot"/> spanning <c>(slotStart, slotStart + defaultDuration)</c>,
    /// where <c>defaultDuration</c> resolves per
    /// <see cref="Extensions.CaleeSchedulerOptions.DefaultCreateDurationMinutes"/> — defaulting to
    /// one <c>SlotDurationMinutes</c> for Day view (a time-grid view). The proposed End
    /// is clamped to the visible band end (<c>EndHour</c>) so a double-click late in the
    /// day doesn't propose an event past the visible grid. Same lifecycle (no optimistic
    /// phantom event) as drag-to-create per ADR-0006.
    /// </summary>
    /// <param name="slotIndex">The slot index that was double-clicked.</param>
    internal Task HandleDoubleClickCreateAsync(int slotIndex)
    {
        if (!AllowDoubleClickToCreate) return Task.CompletedTask;

        var durationMinutes = ResolveDefaultCreateDurationMinutes(
            slotDurationMinutes: _resolvedSlotMinutes,
            useWholeDayDefault: false);

        var startMinutes = _resolvedStartHour * 60 + slotIndex * _resolvedSlotMinutes;
        var bandEndMinutes = _resolvedEndHour * 60;
        var endMinutes = Math.Min(startMinutes + durationMinutes, bandEndMinutes);

        var start = _dayStartLocal.AddMinutes(startMinutes);
        var end = _dayStartLocal.AddMinutes(endMinutes);

        // Issue #8 — fail-closed: no-op on a blocked day (no phantom, no OnEventCreated).
        if (CreateSpanTouchesBlockedDay(start, end)) return Task.CompletedTask;

        var context = new EventCreateContext
        {
            // LaneId stays null — Day view has no lanes.
            Slot = new SchedulerSlot(start, end),
        };
        return OnEventCreated.InvokeAsync(context);
    }

    /// <summary>
    /// Test-only entry point for the double-click-create pipeline. Lets the test project
    /// exercise the callback flow without driving a synthetic <c>@ondblclick</c> event.
    /// </summary>
    /// <param name="slotIndex">The slot index that was synthetically double-clicked.</param>
    internal Task InvokeDoubleClickCreateForTestAsync(int slotIndex) =>
        HandleDoubleClickCreateAsync(slotIndex);
}
