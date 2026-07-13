#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Components;

/// <summary>
/// Read-only Year view for Calee.Scheduler (Phase 2 Task 16 — FR-38). Renders the twelve
/// months of <c>CurrentDate.Year</c> as mini-month grids (default
/// <see cref="YearViewStyle.MiniMonths"/>) or as colored heatmap squares
/// (<see cref="YearViewStyle.Heatmap"/>). Per-day event density drives the dot opacity /
/// tile fill; click-to-drill emits <see cref="SchedulerComponentBase{TEvent}.OnSlotClicked"/>
/// (with the clicked day's midnight–midnight bounds) on a day cell and
/// <see cref="OnMonthClicked"/> on a month header.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout model.</strong> Year view does NOT use the
/// <see cref="EventLayoutEngine"/> or <see cref="VisibleEventSet{TEvent}"/> — both are
/// per-event layout primitives, and per phase-2-plan §9 Year view's per-day density count
/// is the only signal that drives rendering. The view precomputes a
/// <see cref="Dictionary{DateOnly, Int32}"/> of event counts once per render and reads it
/// from the markup. An all-day event (or a timed multi-day event) contributes a count of
/// 1 to every day it touches; a single-day timed event contributes to exactly one day.
/// </para>
/// <para>
/// <strong>Per-day count rule (matches CONTEXT.md).</strong> A day is "touched" by an
/// event when the event's <c>[Start, End)</c> half-open range overlaps the day's
/// <c>[midnight, next-midnight)</c> bound in the configured
/// <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>. The
/// rule is consistent across all-day and timed events — an all-day event whose
/// <c>End</c> is the next midnight after its last day counts on every date in its span
/// (matching how Day/Week/Month treat all-day events), and a timed multi-day event
/// counts on each calendar date it visibly intersects.
/// </para>
/// <para>
/// <strong>Density bucketing.</strong> The four levels documented on
/// <see cref="YearViewStyle"/> drive both modes:
/// <list type="number">
///   <item><description>0 events — no indicator / empty cell.</description></item>
///   <item><description>1 event — level 1.</description></item>
///   <item><description>2–4 events — level 2.</description></item>
///   <item><description>5+ events — level 3.</description></item>
/// </list>
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
public partial class CaleeSchedulerYearView<TEvent> : SchedulerStatefulComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// First day of each mini-month's week (FR-04). Defaults to
    /// <c>SchedulerOptions.Value.DefaultFirstDayOfWeek</c> when null. Drives the column
    /// ordering inside every mini-month grid.
    /// </summary>
    [Parameter]
    public DayOfWeek? FirstDayOfWeek { get; set; }

    /// <summary>
    /// Visual rendering mode for the per-day density indicator. Defaults to
    /// <see cref="YearViewStyle.MiniMonths"/> (day numbers + dots). See
    /// <see cref="YearViewStyle"/> for the bucketing rule.
    /// </summary>
    [Parameter]
    public YearViewStyle Style { get; set; } = YearViewStyle.MiniMonths;

    /// <summary>
    /// Arrangement of the twelve mini-months inside the view. Defaults to
    /// <see cref="YearViewLayout.Grid4x3"/> (the "calendar wall" layout).
    /// </summary>
    [Parameter]
    public YearViewLayout Layout { get; set; } = YearViewLayout.Grid4x3;

    /// <summary>
    /// Fired when the user activates a month header (click on the header strip or
    /// <c>Enter</c>/<c>Space</c> on it). Payload is the first day of that month as a
    /// <see cref="DateOnly"/> per phase-2-plan §5.3 Q14.
    /// </summary>
    /// <remarks>
    /// Consumers typically respond by switching the root scheduler's view to
    /// <see cref="SchedulerView.Month"/> with the supplied date as the anchor. The
    /// library does not auto-switch — the year-to-month drill-down is the consumer's
    /// decision (matches the FR-38 contract: trigger only, consumer renders the next
    /// step).
    /// </remarks>
    [Parameter]
    public EventCallback<DateOnly> OnMonthClicked { get; set; }

    /// <summary>Injected JS runtime — used for the Escape blur helper (FR-30).</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    // Resolved parameter values after OnParametersSet.
    private DayOfWeek _resolvedFirstDayOfWeek;
    private int _displayedYear;

    // Per-day event-density count for the visible year. Built once per OnParametersSet
    // (see remarks on the class) and read from the markup.
    private Dictionary<DateOnly, int> _densityByDate = new();

    // The twelve months of the displayed year (1..12). Computed once and reused.
    private MonthLayout[] _months = Array.Empty<MonthLayout>();
    private EventGeometrySnapshot<TEvent>? _densityEventSnapshot;
    private (int Year, DayOfWeek FirstDay, TimeZoneInfo TimeZone)? _densityInputs;

    // Roving-tabindex anchor for the grid. Stored as (monthIndex, cellIndex within month's
    // 6-week × 7-day matrix). monthIndex is 0..11; cellIndex is 0..41. -1/-1 = no focus yet.
    private int _focusedMonthIndex;
    private int _focusedCellIndex;

    // Range tracking for FR-23 — fire OnRangeChanged when the year changes.
    private DateTimeOffset? _lastRangeStart;
    private DateTimeOffset? _lastRangeEnd;

    private IJSObjectReference? _jsModule;

    // The grid's outer wrapper (role="grid") — queried by focusActiveGridCell (issue #19)
    // to find the currently-tabbable day cell.
    private ElementReference _yearGridRef;

    // Issue #19 — set by HandleCellKeyDownAsync when a key moves the roving tabindex
    // (arrows, Home/End, PageUp/PageDown); consumed in OnAfterRenderAsync (after the
    // tabindex swap has actually rendered) to move real browser focus onto the
    // newly-active day cell.
    private bool _focusMovePending;

    /// <summary>Inclusive start of the visible range (Jan 1 of the displayed year at midnight in <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>).</summary>
    internal DateTimeOffset YearStart { get; private set; }

    /// <summary>Exclusive end of the visible range (Jan 1 of the next year at midnight).</summary>
    internal DateTimeOffset YearEndExclusive { get; private set; }

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        var opts = SchedulerOptions.Value;
        _resolvedFirstDayOfWeek = FirstDayOfWeek ?? opts.DefaultFirstDayOfWeek;

        _displayedYear = CurrentDate.Year;
        var filtered = GetFilteredEvents();
        var inputs = (_displayedYear, _resolvedFirstDayOfWeek, ResolvedTimeZone);
        if (EventFilter is not null
            || _densityInputs != inputs
            || _densityEventSnapshot is null
            || !_densityEventSnapshot.Matches(filtered))
        {
            ComputeYearRange();
            ComputeMonths();
            ComputeDensity(filtered);
            _densityInputs = inputs;
            _densityEventSnapshot = EventFilter is null
                ? EventGeometrySnapshot<TEvent>.Capture(filtered)
                : null;
        }
        ClampFocus();

        if (_lastRangeStart != YearStart || _lastRangeEnd != YearEndExclusive)
        {
            _lastRangeStart = YearStart;
            _lastRangeEnd = YearEndExclusive;
            _ = OnRangeChanged.InvokeAsync(new SchedulerRange(YearStart, YearEndExclusive));
        }
    }

    /// <summary>
    /// Compute the inclusive Jan 1 / exclusive Jan 1-of-next-year bounds for the displayed
    /// year, in the configured time zone. Mirrors <see cref="SchedulerViewPrimitives.ComputeMonthRange"/>'s
    /// shape so the per-zone DST offset is taken at the year's own start (FR-09a).
    /// </summary>
    private void ComputeYearRange()
    {
        var first = new DateTime(_displayedYear, 1, 1);
        var next = new DateTime(_displayedYear + 1, 1, 1);
        YearStart = new DateTimeOffset(first, ResolvedTimeZone.GetUtcOffset(first));
        YearEndExclusive = new DateTimeOffset(next, ResolvedTimeZone.GetUtcOffset(next));
    }

    /// <summary>
    /// Build the twelve <see cref="MonthLayout"/> records — one per month, each carrying
    /// its 42-cell day grid with date numbers + in-month flags.
    /// </summary>
    private void ComputeMonths()
    {
        _months = new MonthLayout[12];
        for (var m = 0; m < 12; m++)
        {
            var monthNumber = m + 1;
            var firstOfMonth = new DateTime(_displayedYear, monthNumber, 1);
            var dayOffset = ((int)firstOfMonth.DayOfWeek - (int)_resolvedFirstDayOfWeek + 7) % 7;
            var gridStartDate = firstOfMonth.AddDays(-dayOffset);

            var cells = new DayCell[42];
            for (var i = 0; i < 42; i++)
            {
                var d = gridStartDate.AddDays(i);
                var date = DateOnly.FromDateTime(d);
                var startOfDay = SchedulerViewPrimitives.MidnightInZone(d, ResolvedTimeZone);
                cells[i] = new DayCell(
                    Date: date,
                    Start: startOfDay,
                    End: SchedulerViewPrimitives.MidnightInZone(d.AddDays(1), ResolvedTimeZone),
                    InMonth: d.Year == _displayedYear && d.Month == monthNumber);
            }
            _months[m] = new MonthLayout(monthNumber, firstOfMonth, cells);
        }
    }

    /// <summary>
    /// Walk the filtered events once and count touched-days into the
    /// <see cref="_densityByDate"/> dictionary. An event contributes 1 per touched day
    /// regardless of how many distinct hours it covers within that day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a Dictionary and not a fixed-size array.</strong> A year spans 365–366
    /// days; the dictionary's lookup-by-DateOnly matches the cell markup's read shape
    /// (the cell already knows its date) without forcing the view to compute a linear
    /// day-of-year index in the hot path. The allocation is bounded — at most one entry
    /// per touched day — and the count fits a small Int32 even for thousands of overlapping
    /// events.
    /// </para>
    /// <para>
    /// <strong>Out-of-range events</strong> (entirely before Jan 1 or on/after Jan 1 of
    /// next year) are skipped at the top of the loop; the engine never sees them.
    /// </para>
    /// </remarks>
    private void ComputeDensity(IReadOnlyList<TEvent> filtered)
    {
        _densityByDate = new Dictionary<DateOnly, int>(filtered.Count == 0 ? 0 : 64);

        if (filtered.Count == 0) return;

        var yearStart = YearStart;
        var yearEnd = YearEndExclusive;

        foreach (var ev in filtered)
        {
            // Skip events entirely outside the visible year.
            if (ev.End <= yearStart || ev.Start >= yearEnd) continue;

            // Clamp the event's span to the year window, then iterate per touched day.
            var spanStart = ev.Start < yearStart ? yearStart : ev.Start;
            var spanEnd = ev.End > yearEnd ? yearEnd : ev.End;

            // Convert clamped bounds to the configured zone to derive touched dates.
            var startInZone = TimeZoneInfo.ConvertTime(spanStart, ResolvedTimeZone);
            var endInZone = TimeZoneInfo.ConvertTime(spanEnd, ResolvedTimeZone);

            var firstDay = DateOnly.FromDateTime(startInZone.Date);
            // The last touched day is the day whose midnight is < end. If end is exactly
            // midnight (typical all-day shape where End = next-midnight) the last touched
            // day is one calendar day earlier; we compute lastDay as end - 1 tick rounded
            // down to date so the half-open [Start, End) shape is honored.
            var lastDay = DateOnly.FromDateTime(endInZone.AddTicks(-1).Date);

            if (lastDay < firstDay) lastDay = firstDay;

            var cursor = firstDay;
            while (cursor <= lastDay)
            {
                if (_densityByDate.TryGetValue(cursor, out var existing))
                {
                    _densityByDate[cursor] = existing + 1;
                }
                else
                {
                    _densityByDate[cursor] = 1;
                }
                cursor = cursor.AddDays(1);
            }
        }
    }

    /// <summary>
    /// Reset focus when the displayed year changes — without this the previously-focused
    /// cell index might land on a muted (other-year) cell after a prev/next-year click.
    /// </summary>
    private void ClampFocus()
    {
        if (_focusedMonthIndex < 0 || _focusedMonthIndex >= 12) _focusedMonthIndex = 0;
        if (_focusedCellIndex < 0 || _focusedCellIndex >= 42) _focusedCellIndex = FirstInMonthCellIndex(_focusedMonthIndex);
    }

    /// <summary>Returns the first in-month cell index of the supplied month (skips muted leading cells).</summary>
    private int FirstInMonthCellIndex(int monthIndex)
    {
        var cells = _months[monthIndex].Cells;
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].InMonth) return i;
        }
        return 0;
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await SchedulerViewPrimitives.TryLoadJsModuleAsync(JSRuntime);
        }

        // Issue #19 — move real browser focus onto the newly-active day cell after a
        // roving-tabindex move. Deferred to here so the query runs after the tabindex
        // swap has rendered to the DOM.
        if (_focusMovePending && _jsModule is not null)
        {
            _focusMovePending = false;
            await SchedulerViewPrimitives.TryFocusActiveGridCellAsync(_jsModule, _yearGridRef);
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

    /// <summary>The 12 months of the displayed year (in display order).</summary>
    internal IReadOnlyList<MonthLayout> Months => _months;

    /// <summary>The displayed year.</summary>
    internal int DisplayedYear => _displayedYear;

    /// <summary>The configured time zone (test access — base property is protected).</summary>
    internal TimeZoneInfo EffectiveTimeZone => ResolvedTimeZone;

    /// <summary>The configured first-day-of-week (test access — base property is protected).</summary>
    internal DayOfWeek EffectiveFirstDayOfWeek => _resolvedFirstDayOfWeek;

    /// <summary>
    /// CSS class for the outer grid wrapper. One of five fixed shapes mapped 1:1 from
    /// <see cref="YearViewLayout"/>. Used by the markup to pick the CSS grid-template.
    /// </summary>
    internal string LayoutClassName => Layout switch
    {
        YearViewLayout.Grid4x3 => "calee-scheduler-year-layout--grid-4x3",
        YearViewLayout.Grid3x4 => "calee-scheduler-year-layout--grid-3x4",
        YearViewLayout.Grid2x6 => "calee-scheduler-year-layout--grid-2x6",
        YearViewLayout.Grid6x2 => "calee-scheduler-year-layout--grid-6x2",
        YearViewLayout.Column => "calee-scheduler-year-layout--column",
        _ => "calee-scheduler-year-layout--grid-4x3",
    };

    /// <summary>CSS class for the style variant (mini-months vs heatmap).</summary>
    internal string StyleClassName => Style switch
    {
        YearViewStyle.MiniMonths => "calee-scheduler-year-style--mini",
        YearViewStyle.Heatmap => "calee-scheduler-year-style--heatmap",
        _ => "calee-scheduler-year-style--mini",
    };

    /// <summary>
    /// Density bucket for a date — 0 (empty) / 1 / 2 / 3. Drives the per-cell visual
    /// (dot opacity for MiniMonths, tile fill for Heatmap). Bucket boundaries are
    /// documented on <see cref="YearViewStyle"/>.
    /// </summary>
    internal int DensityBucket(DateOnly date)
    {
        if (!_densityByDate.TryGetValue(date, out var count) || count <= 0) return 0;
        if (count == 1) return 1;
        if (count <= 4) return 2;
        return 3;
    }

    /// <summary>Raw density count for a date (test-facing).</summary>
    internal int DensityCount(DateOnly date) =>
        _densityByDate.TryGetValue(date, out var count) ? count : 0;

    /// <summary>Returns true when the cell is the roving-tabindex anchor.</summary>
    internal bool IsCellTabbable(int monthIndex, int cellIndex) =>
        monthIndex == _focusedMonthIndex && cellIndex == _focusedCellIndex;

    /// <summary>True when the supplied cell is "today" in the configured time zone.</summary>
    internal bool IsTodayCell(DateOnly date)
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolvedTimeZone);
        return DateOnly.FromDateTime(today.Date) == date;
    }

    /// <summary>The 7 weekday-header labels in display order (Sun..Sat starting at FirstDayOfWeek).</summary>
    internal IEnumerable<string> WeekdayLabels()
    {
        // Single-letter labels keep the mini-month header narrow ("S M T W T F S").
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_resolvedFirstDayOfWeek + i) % 7);
            yield return day.ToString().Substring(0, 1);
        }
    }

    /// <summary>Accessible name for a day cell ("Wednesday, May 20, 2026, 3 events").</summary>
    internal string CellAccessibleName(DayCell cell)
    {
        var count = DensityCount(cell.Date);
        var date = cell.Date.ToString("dddd, MMMM d, yyyy");
        if (count == 0) return date;
        if (count == 1) return $"{date}, 1 event";
        return $"{date}, {count} events";
    }

    /// <summary>Accessible name for a month header ("January 2026, 5 events").</summary>
    internal string MonthAccessibleName(MonthLayout month)
    {
        var name = month.FirstOfMonth.ToString("MMMM yyyy");
        // Sum the in-month density counts so the screen-reader announcement matches the
        // visible per-day dots (out-of-month cells in the leading/trailing weeks are not
        // counted — they belong to a different month).
        var total = 0;
        foreach (var cell in month.Cells)
        {
            if (cell.InMonth)
            {
                total += DensityCount(cell.Date);
            }
        }
        if (total == 0) return name;
        if (total == 1) return $"{name}, 1 event";
        return $"{name}, {total} events";
    }

    /// <summary>Visible (display-format) month name for the header strip ("January").</summary>
    internal string MonthName(MonthLayout month) =>
        month.FirstOfMonth.ToString("MMMM");

    // ----- Event handlers ------------------------------------------------------------

    /// <summary>
    /// Drill-down to a specific day per phase-2-plan §5.3 Q14. Fires
    /// <see cref="SchedulerComponentBase{TEvent}.OnSlotClicked"/> with the day's
    /// <c>[midnight, next-midnight)</c> bounds in the configured time zone, then blurs
    /// any focused event chip (matches Month view's slot-click behavior).
    /// </summary>
    internal async Task HandleDayClickAsync(DayCell cell)
    {
        await OnSlotClicked.InvokeAsync(new SchedulerSlot(cell.Start, cell.End));
        await BlurActiveEventChipAsync();
    }

    /// <summary>Drill-down to a month per phase-2-plan §5.3 Q14.</summary>
    internal Task HandleMonthClickAsync(MonthLayout month)
    {
        var firstOfMonth = DateOnly.FromDateTime(month.FirstOfMonth);
        return OnMonthClicked.InvokeAsync(firstOfMonth);
    }

    /// <summary>
    /// Keyboard handler for a day cell. Arrows move focus within the month (Up/Down by
    /// week, Left/Right by day); Home/End jump to start/end of the focused week within
    /// the month; PageUp/PageDown navigate between months; Enter/Space fires the
    /// slot-click drill-down.
    /// </summary>
    internal async Task HandleCellKeyDownAsync(KeyboardEventArgs e, int monthIndex, int cellIndex)
    {
        // Phase 2 Task 14 — route through the shared shortcut-map dispatch (FR-36) so
        // global commands (view-switch, palette, undo/redo, etc.) fire from the year
        // view as well. The Year-specific keys (Enter on a cell, arrow nav between
        // months) fall through if no command matched.
        if (await TryDispatchShortcutAsync(e, KeystrokeScope.Grid)) return;

        var month = _months[monthIndex];
        var cells = month.Cells;
        switch (e.Key)
        {
            case "ArrowDown":
                {
                    var next = Math.Min(cells.Length - 1, cellIndex + 7);
                    _focusedCellIndex = next;
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "ArrowUp":
                {
                    var next = Math.Max(0, cellIndex - 7);
                    _focusedCellIndex = next;
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "ArrowRight":
                {
                    _focusedCellIndex = Math.Min(cells.Length - 1, cellIndex + 1);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "ArrowLeft":
                {
                    _focusedCellIndex = Math.Max(0, cellIndex - 1);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "Home":
                {
                    // Jump to the first in-month cell.
                    _focusedCellIndex = FirstInMonthCellIndex(monthIndex);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "End":
                {
                    // Jump to the last in-month cell.
                    var last = FirstInMonthCellIndex(monthIndex);
                    for (var i = 0; i < cells.Length; i++)
                    {
                        if (cells[i].InMonth) last = i;
                    }
                    _focusedCellIndex = last;
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "PageDown":
                {
                    // Next month — wrap at the year boundary (the toolbar prev/next-year is
                    // the canonical inter-year nav).
                    _focusedMonthIndex = Math.Min(11, monthIndex + 1);
                    _focusedCellIndex = FirstInMonthCellIndex(_focusedMonthIndex);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "PageUp":
                {
                    _focusedMonthIndex = Math.Max(0, monthIndex - 1);
                    _focusedCellIndex = FirstInMonthCellIndex(_focusedMonthIndex);
                    _focusMovePending = true;
                    StateHasChanged();
                    break;
                }
            case "Enter":
            case " ":
                {
                    await HandleDayClickAsync(cells[cellIndex]);
                    break;
                }
        }
    }

    /// <summary>
    /// Test-only entry point for the keyboard handler, mirroring Day view's
    /// <c>InvokeGridKeyDownForTestAsync</c>. Lets bUnit drive arrow navigation without
    /// having to materialize a synthetic <see cref="KeyboardEventArgs"/> through DOM.
    /// </summary>
    internal Task InvokeCellKeyDownForTestAsync(KeyboardEventArgs e, int monthIndex, int cellIndex) =>
        HandleCellKeyDownAsync(e, monthIndex, cellIndex);

    /// <summary>Test-only accessor for the focused-cell anchor.</summary>
    internal (int MonthIndex, int CellIndex) FocusedCellForTest => (_focusedMonthIndex, _focusedCellIndex);

    /// <summary>
    /// View-specific shortcut hook — Year view has no chip-scope work to dispatch (no
    /// per-event affordances), so this only handles the grid-scope Cancel command
    /// (Escape → blur the active element, matching Day/Month/Week).
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
            case SchedulerCommandIds.Cancel:
                await HandleEscapeAsync();
                return true;
        }
        return false;
    }

    private async Task HandleEscapeAsync()
    {
        // No drag in Year view (no chips to drag), so no IsDragActive precedence check.
        // Selection is unused (no events render as chips), so no selection-clear path.
        // Just blur whatever happens to be focused — mirrors the other views' FR-30
        // Escape fallback.
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

    private async Task BlurActiveEventChipAsync()
    {
        if (_jsModule is null) return;
        try { await _jsModule.InvokeVoidAsync("blurActiveIfEvent"); }
        catch (JSException) { /* Non-fatal. */ }
        catch (JSDisconnectedException) { /* Circuit gone. */ }
        catch (InvalidOperationException) { /* No JS runtime in tests. */ }
    }

    // ----- Render-data record types --------------------------------------------------

    /// <summary>
    /// Render-time data for a single mini-month: its number (1..12), the first-of-month
    /// <see cref="DateTime"/> (used by label formatting), and the 42-cell day grid.
    /// </summary>
    /// <param name="MonthNumber">1-based month number (1 = January, 12 = December).</param>
    /// <param name="FirstOfMonth">The first day of this month, used for label formatting.</param>
    /// <param name="Cells">The 42-cell day grid (6 weeks × 7 days).</param>
    internal sealed record MonthLayout(int MonthNumber, DateTime FirstOfMonth, DayCell[] Cells);

    /// <summary>
    /// Render-time data for a single day cell inside a mini-month grid.
    /// </summary>
    /// <param name="Date">The calendar date this cell represents (in the configured zone).</param>
    /// <param name="Start">Inclusive midnight start of the day.</param>
    /// <param name="End">Exclusive midnight end of the day (= next midnight).</param>
    /// <param name="InMonth">True when this cell belongs to the rendered month (false for leading / trailing rows that bleed into the prev / next month).</param>
    internal sealed record DayCell(DateOnly Date, DateTimeOffset Start, DateTimeOffset End, bool InMonth);
}
