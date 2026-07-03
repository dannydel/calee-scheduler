#nullable enable
using System.Globalization;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Read-only Agenda view for Calee.Scheduler (Phase 2 Task 17 — FR-39). Renders a flat
/// date-grouped list spanning a rolling <see cref="AgendaDays"/>-day window starting at
/// <c>CurrentDate</c>. Empty days are hidden entirely (no "(no events)" placeholder);
/// multi-day events appear once on their start date with a range label. Uses the
/// ARIA-list pattern (<c>role="list"</c> + <c>role="listitem"</c>) rather than the
/// ARIA-grid pattern used by Day/Week/Month/Year — agenda is a flow of items, not a
/// 2D matrix.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout model.</strong> Agenda view does NOT use the
/// <see cref="EventLayoutEngine"/> or <see cref="VisibleEventSet{TEvent}"/> — both are
/// per-event-positioning primitives designed for 2D grid views. Agenda's per-event
/// layout is just a list: one row per (date-group × event), ordered by event start
/// within each group, group headers in calendar order. The view precomputes the
/// grouped row data once per <see cref="OnParametersSet"/> and reads it from markup.
/// </para>
/// <para>
/// <strong>Per-day grouping rule.</strong> An event belongs to the date group of its
/// <c>Start</c> in the configured <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>
/// — except for events whose start is before the window's first day but whose end
/// extends into the window. Those events are <em>pinned</em> to the window's first
/// day (so the user has an anchor row for them; without the pin a multi-day event
/// crossing the leading edge would be invisible).
/// </para>
/// <para>
/// <strong>Multi-day event handling.</strong> A multi-day event renders <em>once</em>,
/// attached to its start-date group (or the pinned first-day group for leading-edge
/// pinning), with a range label like "Mar 12 → Mar 16" (all-day) or "Mar 12 10:00 →
/// Mar 16 17:00" (timed). It is NOT replicated across every touched date — that's a
/// Day/Week chunking convention; Agenda's whole point is one row per event.
/// </para>
/// <para>
/// <strong>Empty days hidden.</strong> A date group is rendered only when it has at
/// least one event row. Per phase-2-plan §1.3 / §5.3 Q16 this is the canonical
/// screen-reader-friendly + narrow-viewport-friendly framing — the user sees a flow
/// of "what's happening" without padding for "what's not."
/// </para>
/// <para>
/// <strong>Out-of-window events.</strong> Events entirely before or entirely after
/// the window are not rendered. Agenda does NOT ship "+N earlier" / "+N later"
/// overflow chips (different shape from Day/Week's hour-band overflows).
/// </para>
/// <para>
/// <strong>Sticky headers.</strong> Each date-group header uses CSS
/// <c>position: sticky</c> so it stays at the top of the scroll viewport as the user
/// scrolls through that group. The scroll container is the view's own wrapper
/// (<c>max-height</c> + <c>overflow: auto</c>) so the sticky behavior works without
/// depending on the consumer's host layout. Consumers can re-theme the height via the
/// <c>--calee-scheduler-agenda-max-height</c> CSS custom property.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerAgendaView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    private const int MinAgendaDays = 1;

    /// <summary>
    /// Documented upper bound for <see cref="AgendaDays"/> per phase-2-plan §5.3 Q16 —
    /// values above this clamp down to <c>90</c>. The cap exists to keep sticky-header
    /// + long-window scrolling performant (see phase-2-plan §9's perf risk row).
    /// </summary>
    public const int MaxAgendaDays = 90;

    /// <summary>
    /// Rolling window length in days. Default <c>7</c>; clamped to the inclusive range
    /// <c>[1, 90]</c> per phase-2-plan §5.3 Q16 (out-of-range values are silently
    /// snapped to the nearest bound; the resolved value is exposed via
    /// <see cref="ResolvedAgendaDays"/> for test/diagnostic introspection). The window
    /// starts at <c>CurrentDate</c>'s date and extends <c>AgendaDays</c> calendar days
    /// forward in the configured <see cref="SchedulerComponentBase{TEvent}.TimeZone"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a cap.</strong> Sticky date headers + per-event-row rendering on a
    /// 365-day window with thousands of events can stutter on narrow devices. Capping
    /// at 90 keeps the worst case bounded; consumers needing a longer window should
    /// page through it (the toolbar prev/next steps by <c>AgendaDays</c>, so a
    /// <c>AgendaDays=90</c> view tiles three calendar months per page).
    /// </para>
    /// <para>
    /// <strong>Clamping is silent.</strong> Setting <c>AgendaDays = 365</c> resolves to
    /// <c>90</c>; setting <c>AgendaDays = 0</c> or any negative value resolves to
    /// <c>1</c>. The library does not throw — fail-soft is consistent with the rest of
    /// the Phase 2 surface (PRD §4.6 soft-degradation for non-required parameters).
    /// Clamping happens in <c>OnParametersSet</c>; the parameter property is a plain
    /// auto-property per the Blazor analyzer's <c>BL0007</c> rule.
    /// </para>
    /// </remarks>
    [Parameter]
    public int AgendaDays { get; set; } = 7;

    /// <summary>
    /// The post-clamp resolved value of <see cref="AgendaDays"/> after
    /// <c>OnParametersSet</c>. Always in <c>[1, 90]</c>.
    /// </summary>
    private int _agendaDays = 7;

    /// <summary>
    /// Optional render fragment for the inside of an event row. Mirrors
    /// <see cref="CaleeSchedulerMonthView{TEvent}.EventChipTemplate"/>'s shape — chips in
    /// Agenda view are visually different from the time-grid blocks (no positioning,
    /// just a list row with a time + title), so the parameter name surfaces that.
    /// </summary>
    [Parameter]
    public RenderFragment<TEvent>? EventRowTemplate { get; set; }

    /// <summary>
    /// Fired when the user activates a date-group header (click or Enter/Space). Carries
    /// the first day of the group as a <see cref="DateOnly"/> — typically the consumer
    /// responds by switching the root scheduler's view to Day with the supplied date.
    /// Mirrors <see cref="CaleeSchedulerYearView{TEvent}.OnMonthClicked"/>'s drill-down
    /// shape so consumers compose the two views consistently.
    /// </summary>
    [Parameter]
    public EventCallback<DateOnly> OnDateClicked { get; set; }

    /// <summary>Injected JS runtime — used for the Escape blur helper (FR-30).</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // Resolved inputs after OnParametersSet.
    private DateTimeOffset _windowStart;            // Inclusive start (midnight in TimeZone).
    private DateTimeOffset _windowEndExclusive;     // Exclusive end (midnight in TimeZone).
    private DateOnly _windowFirstDate;              // The first calendar date in the window.

    // The grouped agenda rows. Top-level is one DateGroup per visible date; each
    // DateGroup carries the events anchored on that date in render order. Empty days
    // are not included (the locked design decision from §5.3 Q16).
    private DateGroup[] _groups = Array.Empty<DateGroup>();

    // Roving-tabindex anchor: a flat row index into the per-render visible-rows list
    // (skipping headers — headers are non-interactive landmarks per ADR-0009). -1 means
    // "no row focused yet"; first render seeds to 0 when at least one row exists.
    private int _focusedRowIndex = -1;

    // Range tracking for FR-23 — fire OnRangeChanged when the window changes.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private IJSObjectReference? _jsModule;

    // The scrollable list wrapper (role="group") — queried by focusActiveGridCell
    // (issue #19) to find the currently-tabbable row.
    private ElementReference _agendaListRef;

    // Issue #19 — set by HandleRowKeyDownAsync when a key moves the roving tabindex
    // (arrows, Home/End, PageUp/PageDown); consumed in OnAfterRenderAsync (after the
    // tabindex swap has actually rendered) to move real browser focus onto the
    // newly-active row.
    private bool _focusMovePending;

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Clamp AgendaDays into [1, 90] silently per phase-2-plan §5.3 Q16. The
        // public parameter remains a plain auto-property (Blazor's BL0007 analyzer
        // requires that); the clamp lives here so a degenerate consumer-supplied
        // value can't drive the layout into a negative-length window.
        if (AgendaDays < MinAgendaDays) _agendaDays = MinAgendaDays;
        else if (AgendaDays > MaxAgendaDays) _agendaDays = MaxAgendaDays;
        else _agendaDays = AgendaDays;

        ComputeWindow();
        ComputeGroups();
        ClampFocus();

        if (_lastRangeStart != _windowStart || _lastRangeEnd != _windowEndExclusive)
        {
            _lastRangeStart = _windowStart;
            _lastRangeEnd = _windowEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(_windowStart, _windowEndExclusive));
        }
    }

    /// <summary>
    /// Resolve the window bounds. The window starts at <c>CurrentDate</c>'s date in
    /// the configured time zone and extends <see cref="AgendaDays"/> calendar days
    /// forward (i.e., <c>[anchor-midnight, anchor-midnight + N days)</c>). DST is
    /// honored — each day boundary's offset is taken at that day's date.
    /// </summary>
    private void ComputeWindow()
    {
        var firstDate = CurrentDate.Date;
        var firstOffset = TimeZone.GetUtcOffset(firstDate);
        _windowStart = new DateTimeOffset(firstDate, firstOffset);
        _windowFirstDate = DateOnly.FromDateTime(firstDate);

        var endDate = firstDate.AddDays(_agendaDays);
        var endOffset = TimeZone.GetUtcOffset(endDate);
        _windowEndExclusive = new DateTimeOffset(endDate, endOffset);
    }

    /// <summary>
    /// Walk the filtered events once; bucket each visible event into its anchor date
    /// group; sort each group's events by start time; drop empty groups; expose the
    /// resulting <see cref="DateGroup"/>[] in calendar order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Anchor-date rule.</strong> The default anchor date for an event is its
    /// <c>Start.Date</c> in the configured time zone. The exception: an event whose
    /// start is before <c>_windowFirstDate</c> but whose end is after
    /// <c>_windowStart</c> (i.e., it extends INTO the window from before) pins to
    /// <c>_windowFirstDate</c> so the user has an anchor row for it. Without the pin
    /// the multi-day event would be invisible — its actual start is below the window's
    /// first day; we don't repeat events across days, so there's no other place to
    /// surface it.
    /// </para>
    /// <para>
    /// <strong>Out-of-window events.</strong> Events entirely before <c>_windowStart</c>
    /// (end &lt;= window start) and events entirely on/after <c>_windowEndExclusive</c>
    /// (start &gt;= window end) are dropped before bucketing — they have no anchor row.
    /// Agenda does not surface "+N earlier" / "+N later" chips for them.
    /// </para>
    /// <para>
    /// <strong>Sort order within a group.</strong> All-day events first (so the
    /// banner-style rows lead each day), then timed events ascending by Start. Within
    /// each sub-bucket events are stable in input order — the consumer's
    /// <see cref="SchedulerComponentBase{TEvent}.Events"/> order is the tiebreaker, so
    /// re-ordering the supplied list re-orders the rows visibly.
    /// </para>
    /// </remarks>
    private void ComputeGroups()
    {
        var filtered = GetFilteredEvents();
        if (filtered.Count == 0)
        {
            _groups = Array.Empty<DateGroup>();
            return;
        }

        var windowStart = _windowStart;
        var windowEnd = _windowEndExclusive;
        var windowFirstDate = _windowFirstDate;
        var windowLastDateInclusive = _windowFirstDate.AddDays(_agendaDays - 1);

        // Materialize each TEvent into a row carrying its anchor date and the typed
        // reference for click callbacks. Pinned-from-before events anchor to the
        // window's first day rather than their actual start date.
        var rows = new List<EventRow>(filtered.Count);
        for (var i = 0; i < filtered.Count; i++)
        {
            var ev = filtered[i];

            // Out-of-window guards. Use the half-open [Start, End) overlap check — same
            // shape as Year view's density bucketing (CONTEXT.md says all-day events
            // run to next-midnight, so an all-day event ending on the window's first
            // date at midnight does not touch the window).
            if (ev.End <= windowStart) continue;
            if (ev.Start >= windowEnd) continue;

            // Anchor date: usually Start.Date in TimeZone; pinned to the window's first
            // day when the event began before the window.
            var startInZone = TimeZoneInfo.ConvertTime(ev.Start, TimeZone);
            var startDate = DateOnly.FromDateTime(startInZone.Date);
            var anchorDate = startDate < windowFirstDate ? windowFirstDate : startDate;
            // If the calculated anchor falls outside the visible window (can happen
            // when the start date itself is after window-end but End hasn't crossed),
            // skip — defensive, the half-open check above already covered it but the
            // explicit clamp is documentation.
            if (anchorDate > windowLastDateInclusive) continue;

            // Order key inside a group: all-day events first (sort key 0), then timed
            // events keyed by their start instant. Within the same key, input order is
            // preserved by carrying the iteration index as a secondary key.
            var sortKey = ev.IsAllDay ? long.MinValue : ev.Start.UtcTicks;
            rows.Add(new EventRow(
                EventRef: ev,
                Id: ev.Id,
                AnchorDate: anchorDate,
                SortKey: sortKey,
                InputOrder: i,
                IsAllDay: ev.IsAllDay,
                IsPinnedFromBefore: anchorDate != startDate));
        }

        if (rows.Count == 0)
        {
            _groups = Array.Empty<DateGroup>();
            return;
        }

        // Bucket into per-date lists; preserve insertion order via the rows iteration.
        var bucketsByDate = new Dictionary<DateOnly, List<EventRow>>(capacity: rows.Count);
        foreach (var row in rows)
        {
            if (!bucketsByDate.TryGetValue(row.AnchorDate, out var bucket))
            {
                bucket = new List<EventRow>(capacity: 4);
                bucketsByDate[row.AnchorDate] = bucket;
            }
            bucket.Add(row);
        }

        // Sort dates ascending; sort each bucket's rows by (sortKey, inputOrder).
        var orderedDates = new List<DateOnly>(bucketsByDate.Keys);
        orderedDates.Sort();

        var groups = new DateGroup[orderedDates.Count];
        for (var g = 0; g < orderedDates.Count; g++)
        {
            var date = orderedDates[g];
            var bucket = bucketsByDate[date];
            bucket.Sort(static (a, b) =>
            {
                var byKey = a.SortKey.CompareTo(b.SortKey);
                if (byKey != 0) return byKey;
                return a.InputOrder.CompareTo(b.InputOrder);
            });
            // Compute the day's [midnight, next-midnight) bounds in TimeZone for the
            // header's drill-down callback.
            var dayDate = date.ToDateTime(TimeOnly.MinValue);
            var dayOffset = TimeZone.GetUtcOffset(dayDate);
            var dayStart = new DateTimeOffset(dayDate, dayOffset);
            var dayEnd = dayStart.AddDays(1);
            groups[g] = new DateGroup(date, dayStart, dayEnd, bucket.ToArray());
        }

        _groups = groups;
    }

    /// <summary>
    /// Re-clamp the focused row index against the new groups. Without this a window
    /// change (prev/next year, AgendaDays delta) can leave focus pointing at a now-
    /// nonexistent row index.
    /// </summary>
    private void ClampFocus()
    {
        var totalRows = TotalRowCount();
        if (totalRows == 0)
        {
            _focusedRowIndex = -1;
        }
        else if (_focusedRowIndex < 0 || _focusedRowIndex >= totalRows)
        {
            _focusedRowIndex = 0;
        }
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);
        }

        // Issue #19 — move real browser focus onto the newly-active row after a
        // roving-tabindex move. Deferred to here so the query runs after the tabindex
        // swap has rendered to the DOM.
        if (_focusMovePending && _jsModule is not null)
        {
            _focusMovePending = false;
            await SchedulerViewPrimitives.TryFocusActiveGridCellAsync(_jsModule, _agendaListRef);
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

    // ----- Accessors used by the .razor markup -------------------------------------

    /// <summary>Inclusive start of the visible range (test-facing).</summary>
    internal DateTimeOffset WindowStart => _windowStart;

    /// <summary>Exclusive end of the visible range (test-facing).</summary>
    internal DateTimeOffset WindowEndExclusive => _windowEndExclusive;

    /// <summary>The grouped, ordered render data (test-facing).</summary>
    internal IReadOnlyList<DateGroup> Groups => _groups;

    /// <summary>The currently-focused flat row index (-1 if no row).</summary>
    internal int FocusedRowIndexForTest => _focusedRowIndex;

    /// <summary>Test-only accessor for the resolved AgendaDays after clamping.</summary>
    internal int ResolvedAgendaDays => _agendaDays;

    /// <summary>Total row count across every visible group — drives ARIA aria-setsize.</summary>
    private int TotalRowCount()
    {
        var total = 0;
        for (var i = 0; i < _groups.Length; i++) total += _groups[i].Rows.Length;
        return total;
    }

    /// <summary>Flat row index → (groupIndex, rowIndexInGroup) — used by keyboard nav.</summary>
    private (int Group, int RowInGroup) ResolveFlatIndex(int flatIndex)
    {
        var cursor = 0;
        for (var g = 0; g < _groups.Length; g++)
        {
            var len = _groups[g].Rows.Length;
            if (flatIndex < cursor + len) return (g, flatIndex - cursor);
            cursor += len;
        }
        return (-1, -1);
    }

    /// <summary>(groupIndex, rowIndexInGroup) → flat row index — inverse of <see cref="ResolveFlatIndex"/>.</summary>
    private int Flatten(int groupIndex, int rowInGroup)
    {
        var cursor = 0;
        for (var g = 0; g < groupIndex; g++) cursor += _groups[g].Rows.Length;
        return cursor + rowInGroup;
    }

    /// <summary>True when the supplied flat row index is the roving-tabindex anchor.</summary>
    internal bool IsRowTabbable(int flatIndex) => flatIndex == _focusedRowIndex;

    /// <summary>Today expressed as a <see cref="DateOnly"/> in the configured time zone.</summary>
    internal DateOnly TodayInZone =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone).Date);

    /// <summary>Format the group's header label (e.g., "Tuesday, March 17").</summary>
    internal string FormatGroupHeader(DateGroup group)
    {
        var date = group.Date.ToDateTime(TimeOnly.MinValue);
        return date.ToString("dddd, MMMM d", CultureInfo.GetCultureInfo("en-US"));
    }

    /// <summary>Accessible name for a group header (announces today / today's relation).</summary>
    internal string GroupHeaderAccessibleName(DateGroup group)
    {
        var label = FormatGroupHeader(group);
        if (group.Date == TodayInZone) return label + ", today";
        return label;
    }

    /// <summary>
    /// Format the time portion of an event row. All-day single-day → "All day"; timed
    /// single-day → "9:00 AM – 10:00 AM"; multi-day (all-day or timed) → range label
    /// spanning the event's actual start to end (NOT clamped to the window — the user
    /// wants the real event duration, not the visible slice).
    /// </summary>
    internal string FormatRowTime(EventRow row)
    {
        var ev = row.EventRef;
        var tz = TimeZone;
        var startInZone = TimeZoneInfo.ConvertTime(ev.Start, tz);
        var endInZone = TimeZoneInfo.ConvertTime(ev.End, tz);

        if (ev.IsAllDay)
        {
            var lastInclusive = endInZone.AddTicks(-1);
            if (startInZone.Date == lastInclusive.Date)
            {
                return "All day";
            }
            return $"{startInZone:MMM d} → {lastInclusive:MMM d}";
        }

        if (startInZone.Date == endInZone.Date)
        {
            return $"{startInZone:h:mm tt} – {endInZone:h:mm tt}";
        }
        return $"{startInZone:MMM d h:mm tt} → {endInZone:MMM d h:mm tt}";
    }

    /// <summary>Accessible name for an event row (title + formatted time).</summary>
    internal string RowAccessibleName(EventRow row)
    {
        var time = FormatRowTime(row);
        return $"{row.EventRef.Title}, {time}";
    }

    /// <summary>Consumer-supplied CSS class for an event (via base helper).</summary>
    internal string? ClassFor(TEvent ev) => GetEventClass(ev);

    // ----- Event handlers ----------------------------------------------------------

    /// <summary>Drill-down to a specific date.</summary>
    internal Task HandleDateClickAsync(DateGroup group)
    {
        return OnDateClicked.InvokeAsync(group.Date);
    }

    /// <summary>
    /// Click handler for an event row. Fires <see cref="SchedulerComponentBase{TEvent}.OnEventClicked"/>
    /// with the consumer's TEvent + updates the selection through the shared base
    /// helper. Mirrors Month view's <c>DispatchClickAsync</c> pattern — render order
    /// for Shift+click range select is "every visible row in document order, skipping
    /// headers."
    /// </summary>
    internal async Task HandleRowClickAsync(EventRow row, MouseEventArgs? args = null)
    {
        if (row.EventRef is not TEvent typed) return;

        if (args is not null)
        {
            var ctrlOrMeta = args.CtrlKey || args.MetaKey;
            var shift = args.ShiftKey;
            var renderOrder = ComputeRenderOrderIds();
            var changed = await ApplyClickSelectionAsync(row.Id, ctrlOrMeta, shift, renderOrder);
            if (changed && IsStandalone)
            {
                StateHasChanged();
            }
        }

        await OnEventClicked.InvokeAsync(typed);
    }

    /// <summary>Render-order id list — every visible row in flat order; headers are skipped.</summary>
    private IReadOnlyList<string> ComputeRenderOrderIds()
    {
        var total = TotalRowCount();
        if (total == 0) return Array.Empty<string>();
        var ids = new List<string>(total);
        for (var g = 0; g < _groups.Length; g++)
        {
            var rows = _groups[g].Rows;
            for (var r = 0; r < rows.Length; r++)
            {
                ids.Add(rows[r].Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// Keyboard handler for an event row. Routes through the shared shortcut-map
    /// dispatch (FR-36) so global commands (view-switch, palette, undo/redo, etc.)
    /// fire from Agenda as well. View-specific bindings: Enter / Space activate the
    /// row (Enter fires <see cref="SchedulerComponentBase{TEvent}.OnEventClicked"/>;
    /// Space toggles selection via the chip-scope select.toggle binding when
    /// <see cref="SchedulerComponentBase{TEvent}.AllowMultiSelect"/> is on); arrow
    /// keys move between rows; Home / End jump to first / last; PageUp / PageDown
    /// move between date groups.
    /// </summary>
    internal async Task HandleRowKeyDownAsync(KeyboardEventArgs e, EventRow row, int flatIndex)
    {
        // Drag precedence — same shape as the other views; Esc-mid-drag belongs to JS.
        if (IsDragActive) return;

        if (row.EventRef is not TEvent typed) return;

        if (await TryDispatchShortcutAsync(e, KeystrokeScope.Chip, typed, row.Id)) return;

        switch (e.Key)
        {
            case "ArrowDown":
                {
                    var total = TotalRowCount();
                    if (total == 0) return;
                    _focusedRowIndex = Math.Min(total - 1, flatIndex + 1);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "ArrowUp":
                {
                    _focusedRowIndex = Math.Max(0, flatIndex - 1);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "Home":
                {
                    _focusedRowIndex = 0;
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "End":
                {
                    var total = TotalRowCount();
                    if (total == 0) return;
                    _focusedRowIndex = total - 1;
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "PageDown":
                {
                    // Jump to the first row of the next date group (or end of last group).
                    var (g, _) = ResolveFlatIndex(flatIndex);
                    if (g < 0) return;
                    if (g + 1 < _groups.Length)
                    {
                        _focusedRowIndex = Flatten(g + 1, 0);
                    }
                    else
                    {
                        var total = TotalRowCount();
                        _focusedRowIndex = total - 1;
                    }
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "PageUp":
                {
                    // Jump to the first row of the previous date group (or first row).
                    var (g, _) = ResolveFlatIndex(flatIndex);
                    if (g < 0) return;
                    if (g > 0)
                    {
                        _focusedRowIndex = Flatten(g - 1, 0);
                    }
                    else
                    {
                        _focusedRowIndex = 0;
                    }
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "Enter":
                {
                    await HandleRowClickAsync(row);
                    break;
                }
            case " ":
                {
                    // When AllowMultiSelect is off Space is dispatched by the browser default
                    // (button activates → click → single selection); when on, the chip-scope
                    // select.toggle dispatch above already handled it. The fall-through here
                    // covers the edge case where the shortcut map disabled select.toggle —
                    // we keep Space as a row-activator so accessibility's "Space = activate
                    // button" expectation still holds.
                    if (!AllowMultiSelect)
                    {
                        await HandleRowClickAsync(row);
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// Test-only entry point for the keyboard handler. Lets bUnit drive arrow nav
    /// without having to materialize a synthetic <see cref="KeyboardEventArgs"/>
    /// through DOM.
    /// </summary>
    internal Task InvokeRowKeyDownForTestAsync(KeyboardEventArgs e, EventRow row, int flatIndex) =>
        HandleRowKeyDownAsync(e, row, flatIndex);

    /// <summary>
    /// View-specific command dispatch — Agenda mirrors Month/Year for SelectToggle and
    /// Cancel. Delete on a focused row routes through the shared
    /// <see cref="SchedulerComponentBase{TEvent}.TryDeleteFocusedEventAsync"/> helper.
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
                if (focusedEventId is null || focusedEvent is null) return false;
                // Route through the click handler with a synthetic Ctrl-modifier so the
                // toggle semantics match the Ctrl+click path exactly. Matches Month view
                // (line 725 in CaleeSchedulerMonthView.razor.cs).
                var rowMatch = FindRowById(focusedEventId);
                if (rowMatch is null) return false;
                await HandleRowClickAsync(rowMatch, new MouseEventArgs { CtrlKey = true });
                return true;
            case SchedulerCommandIds.EditDelete:
                if (scope != KeystrokeScope.Chip) return false;
                if (focusedEventId is null || focusedEvent is null) return false;
                if (!AllowDelete) return false;
                if (IsDragActive) return false;
                var changed = await TryDeleteFocusedEventAsync(focusedEventId, focusedEvent);
                if (changed && IsStandalone) StateHasChanged();
                return true;
            case SchedulerCommandIds.Cancel:
                await HandleEscapeAsync();
                return true;
        }
        return false;
    }

    private EventRow? FindRowById(string id)
    {
        for (var g = 0; g < _groups.Length; g++)
        {
            var rows = _groups[g].Rows;
            for (var r = 0; r < rows.Length; r++)
            {
                if (string.Equals(rows[r].Id, id, StringComparison.Ordinal))
                {
                    return rows[r];
                }
            }
        }
        return null;
    }

    private async Task HandleEscapeAsync()
    {
        if (IsDragActive) return;
        var cleared = await TryClearSelectionViaKeyboardAsync();
        if (cleared)
        {
            if (IsStandalone) StateHasChanged();
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

    // ----- Render-data record types ------------------------------------------------

    /// <summary>
    /// Render-time data for one date group inside the agenda. Carries the date, the
    /// day's [midnight, next-midnight) bounds for drill-down callbacks, and the
    /// ordered event rows anchored on that date.
    /// </summary>
    /// <param name="Date">The calendar date this group represents in the configured zone.</param>
    /// <param name="Start">Inclusive midnight start of the day.</param>
    /// <param name="End">Exclusive midnight end of the day (= next midnight).</param>
    /// <param name="Rows">Event rows anchored on this date, sorted (all-day first, then by start).</param>
    internal sealed record DateGroup(DateOnly Date, DateTimeOffset Start, DateTimeOffset End, EventRow[] Rows);

    /// <summary>
    /// Render-time data for a single event row in the agenda. Carries the underlying
    /// consumer event reference (typed as <see cref="ICalendarEvent"/> in the contract
    /// signature, but the actual instance is the consumer's <typeparamref name="TEvent"/>),
    /// the id for selection lookup, the anchor date the row appears under, and a sort
    /// key + input order for stable ordering inside its group.
    /// </summary>
    /// <param name="EventRef">The consumer event this row represents.</param>
    /// <param name="Id">The event's id (cached for selection lookup hot-path).</param>
    /// <param name="AnchorDate">The date group this row belongs to.</param>
    /// <param name="SortKey">Sort key inside the group (all-day = <see cref="long.MinValue"/>; timed = <c>Start.UtcTicks</c>).</param>
    /// <param name="InputOrder">Secondary sort key — preserves consumer input order on ties.</param>
    /// <param name="IsAllDay">True when the underlying event has <c>IsAllDay = true</c>.</param>
    /// <param name="IsPinnedFromBefore">
    /// True when the event's actual start is before the window's first day and the row
    /// is pinned to the window's first day instead. Used by the markup to render an
    /// "earlier" indicator on the row.
    /// </param>
    internal sealed record EventRow(
        ICalendarEvent EventRef,
        string Id,
        DateOnly AnchorDate,
        long SortKey,
        int InputOrder,
        bool IsAllDay,
        bool IsPinnedFromBefore);
}
