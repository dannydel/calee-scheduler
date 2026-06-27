using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Lays out timed events inside a single time band (one day for Day/Week views, one
/// lane-row's time-window for TimelineView). Produces a <see cref="LayoutResult"/>
/// whose <see cref="PositionedEvent"/>s carry direction-agnostic time/stack coordinates
/// that the view renderer maps onto CSS dimensions.
/// </summary>
/// <remarks>
/// <para>
/// The engine has no Blazor dependency and is fully unit-testable (NFR-05). It assumes the
/// caller has already filtered out all-day events and split multi-day timed events into
/// per-band chunks (PRD §4.4 "Input shape").
/// </para>
/// <para>
/// Stack assignment is a sweep-line with stack reuse: each event takes the lowest-numbered
/// stack slot whose previous occupant has already ended at the new event's start. The
/// <see cref="PositionedEvent.StackCount"/> reported for an event is the maximum concurrent
/// overlap observed during <em>that event's</em> lifetime — not the size of a transitive
/// overlap group. See ADR-0003 for the verbatim A/B/C rationale. The "stack" terminology
/// (versus the public "lane" that names a TimelineView row) is per ADR-0011.
/// </para>
/// </remarks>
internal sealed class EventLayoutEngine
{
    /// <summary>
    /// Lay out the supplied events inside the band <c>[rangeStart, rangeEnd]</c>, optionally
    /// clipped to the visible hour window <c>[rangeStartHour, rangeEndHour]</c>.
    /// </summary>
    /// <param name="events">
    /// Pre-filtered events to lay out. The caller must have removed all-day events and split
    /// any multi-day timed events into per-band chunks (PRD §4.4).
    /// </param>
    /// <param name="rangeStart">Inclusive start instant of the band.</param>
    /// <param name="rangeEnd">Exclusive end instant of the band. Must be &gt; <paramref name="rangeStart"/>.</param>
    /// <param name="rangeStartHour">
    /// Optional visible-time floor as an hour-of-day (0..24). When supplied, events lying
    /// entirely before this hour within the band flow into <see cref="LayoutResult.EarlierOverflow"/>
    /// instead of being positioned. <see langword="null"/> disables the floor — used by
    /// TimelineView's Week/Month modes where there is no hour-of-day clip.
    /// </param>
    /// <param name="rangeEndHour">
    /// Optional visible-time ceiling as an hour-of-day (0..24). Symmetric counterpart to
    /// <paramref name="rangeStartHour"/>.
    /// </param>
    /// <param name="maxColumns">
    /// Optional column cap (must be &gt;= 2 to take effect). When the number of concurrent
    /// events at any instant exceeds this cap, events that would occupy stack slots at index
    /// <c>cap - 1</c> or beyond are collapsed into <see cref="OverlapOverflowBlock"/>s on
    /// <see cref="LayoutResult.OverlapOverflow"/>. Kept events report a
    /// <see cref="PositionedEvent.StackCount"/> capped at <paramref name="maxColumns"/> so their
    /// width aligns with the reserved block slot. <see langword="null"/> (the default) disables
    /// the cap entirely, preserving pre-cap behavior.
    /// </param>
    public LayoutResult Layout(
        IReadOnlyList<ICalendarEvent> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        int? rangeStartHour = null,
        int? rangeEndHour = null,
        int? maxColumns = null)
    {
        if (events is null || events.Count == 0)
        {
            return new LayoutResult(
                Array.Empty<PositionedEvent>(),
                Array.Empty<ICalendarEvent>(),
                Array.Empty<ICalendarEvent>(),
                Array.Empty<OverlapOverflowBlock>());
        }

        if (rangeEnd <= rangeStart)
        {
            throw new ArgumentException(
                $"rangeEnd ({rangeEnd:O}) must be greater than rangeStart ({rangeStart:O}).",
                nameof(rangeEnd));
        }

        // Visible band: the time window we actually render into. When hour clips are supplied
        // we narrow [rangeStart, rangeEnd] down by hours-of-day; otherwise the visible band
        // equals the full range. Percentages are computed relative to this visible band.
        //
        // Precondition for the hour-clipped path: rangeStart is anchored at midnight (local
        // to the configured TimeZone) of the day being laid out — so AddHours(rangeStartHour)
        // lands on the hour-of-day floor for that day. Callers feeding a non-midnight-anchored
        // band MUST pass null for both rangeStartHour and rangeEndHour, or the visible ceiling
        // will silently drift. Day/Week views always pass midnight; TimelineView Week/Month
        // passes nulls; TimelineView Day passes midnight + optional hour clip.
        var visibleStart = rangeStartHour.HasValue
            ? rangeStart.AddHours(rangeStartHour.Value)
            : rangeStart;
        var visibleEnd = rangeEndHour.HasValue
            ? rangeStart.AddHours(rangeEndHour.Value)
            : rangeEnd;

        if (visibleEnd <= visibleStart)
        {
            throw new ArgumentException(
                "Visible band collapses to zero or negative width; check rangeStartHour/rangeEndHour.",
                nameof(rangeEndHour));
        }

        var visibleTicks = (visibleEnd - visibleStart).Ticks;

        var earlierOverflow = new List<ICalendarEvent>();
        var laterOverflow = new List<ICalendarEvent>();
        var toLayOut = new List<ICalendarEvent>();

        foreach (var ev in events)
        {
            // Events entirely outside the full [rangeStart, rangeEnd] band are excluded
            // outright — they belong to a different band and should never have been passed in.
            if (ev.End <= rangeStart || ev.Start >= rangeEnd)
            {
                continue;
            }

            // Overflow classification uses the visible band (which collapses to the full
            // range when no hour clip is configured, making overflow lists empty).
            if (rangeStartHour.HasValue && ev.End <= visibleStart)
            {
                earlierOverflow.Add(ev);
                continue;
            }

            if (rangeEndHour.HasValue && ev.Start >= visibleEnd)
            {
                laterOverflow.Add(ev);
                continue;
            }

            toLayOut.Add(ev);
        }

        // Deterministic input order: Start ascending, then End ascending, then Id for stable
        // tie-breaks. Stack assignment depends on order, and the renderer reads back a
        // deterministic list.
        toLayOut.Sort(static (a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            if (cmp != 0) return cmp;
            cmp = a.End.CompareTo(b.End);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Id, b.Id);
        });

        // Sweep-line stack assignment with stack reuse. `stackEnds[i]` is the End of the
        // event currently parked in stack slot i; a slot is reusable once its End <= current.Start.
        var stackEnds = new List<DateTimeOffset>();
        var stackIndices = new int[toLayOut.Count];

        for (var i = 0; i < toLayOut.Count; i++)
        {
            var ev = toLayOut[i];
            var assigned = -1;
            for (var slot = 0; slot < stackEnds.Count; slot++)
            {
                if (stackEnds[slot] <= ev.Start)
                {
                    assigned = slot;
                    stackEnds[slot] = ev.End;
                    break;
                }
            }
            if (assigned < 0)
            {
                assigned = stackEnds.Count;
                stackEnds.Add(ev.End);
            }
            stackIndices[i] = assigned;
        }

        // StackCount per event: max concurrent count during [E.Start, E.End). Concurrency
        // changes only at event boundaries, so we sample at E.Start plus every other event's
        // Start that falls inside E's window.
        var stackCounts = new int[toLayOut.Count];
        for (var i = 0; i < toLayOut.Count; i++)
        {
            var e = toLayOut[i];
            var instants = new List<DateTimeOffset> { e.Start };
            for (var j = 0; j < toLayOut.Count; j++)
            {
                if (j == i) continue;
                var other = toLayOut[j];
                if (other.Start > e.Start && other.Start < e.End)
                {
                    instants.Add(other.Start);
                }
            }

            var max = 1;
            foreach (var t in instants)
            {
                var count = 0;
                for (var k = 0; k < toLayOut.Count; k++)
                {
                    var o = toLayOut[k];
                    // Active at instant t means Start <= t < End. Zero-duration events
                    // (Start == End) count as active at their Start only — they still
                    // need a stack slot.
                    if (o.Start <= t && (t < o.End || (o.Start == o.End && o.Start == t)))
                    {
                        count++;
                    }
                }
                if (count > max) max = count;
            }
            stackCounts[i] = max;
        }

        // Column cap. maxColumns null or < 2 disables the cap entirely — cap == int.MaxValue
        // means no event ever overflows and widths stay uncapped, preserving pre-cap behavior
        // exactly for callers that don't opt in.
        var cap = (maxColumns.HasValue && maxColumns.Value >= 2) ? maxColumns.Value : int.MaxValue;

        var positioned = new List<PositionedEvent>(toLayOut.Count);
        // Overflow events, collected in start-sorted order (toLayOut is start-sorted) so the
        // interval-merge below can run in one pass.
        var overflowEvents = new List<ICalendarEvent>();

        for (var i = 0; i < toLayOut.Count; i++)
        {
            var ev = toLayOut[i];

            // An event overflows iff it sits in the squeezed slots (index >= cap-1) AND its
            // local concurrency actually exceeds the cap. The concurrency guard keeps "exactly
            // `cap` concurrent" fully visible (no block) and guarantees a block hides >= 2:
            // reaching index cap-1 needs cap concurrent at the event's start; exceeding the cap
            // forces an (cap+1)th event into index >= cap-1 too, so overflow runs always pair up.
            if (stackIndices[i] >= cap - 1 && stackCounts[i] > cap)
            {
                overflowEvents.Add(ev);
                continue;
            }

            var clippedAtStart = ev.Start < visibleStart;
            var clippedAtEnd = ev.End > visibleEnd;

            var renderStart = clippedAtStart ? visibleStart : ev.Start;
            var renderEnd = clippedAtEnd ? visibleEnd : ev.End;
            // Guard: a zero-duration event at exactly the visible boundary keeps renderEnd at
            // renderStart so TimeSpanPercent is 0 and the renderer applies its minimum-height
            // treatment (PRD §5.2).
            if (renderEnd < renderStart) renderEnd = renderStart;

            var startTicks = (renderStart - visibleStart).Ticks;
            var spanTicks = (renderEnd - renderStart).Ticks;

            positioned.Add(new PositionedEvent(
                Event: ev,
                TimeStartPercent: (double)startTicks / visibleTicks * 100.0,
                TimeSpanPercent: (double)spanTicks / visibleTicks * 100.0,
                StackIndex: stackIndices[i],
                // Cap the width denominator so kept events sit at 1/cap, aligned with the
                // block in the reserved last column, instead of 1/concurrency.
                StackCount: Math.Min(stackCounts[i], cap),
                ClippedAtTimeStart: clippedAtStart,
                ClippedAtTimeEnd: clippedAtEnd));
        }

        // Group overflow events into connected time-overlap runs — one block per run.
        var blocks = new List<OverlapOverflowBlock>();
        if (overflowEvents.Count > 0)
        {
            var runStartIdx = 0;
            var runEnd = overflowEvents[0].End;
            for (var i = 1; i <= overflowEvents.Count; i++)
            {
                var startsNewRun = i == overflowEvents.Count || overflowEvents[i].Start >= runEnd;
                if (startsNewRun)
                {
                    var run = overflowEvents.GetRange(runStartIdx, i - runStartIdx);
                    AppendOverflowRun(run, visibleStart, visibleEnd, visibleTicks, cap, positioned, blocks);
                    if (i < overflowEvents.Count)
                    {
                        runStartIdx = i;
                        runEnd = overflowEvents[i].End;
                    }
                }
                else if (overflowEvents[i].End > runEnd)
                {
                    runEnd = overflowEvents[i].End;
                }
            }
        }

        // Final deterministic output order: by StackIndex, then TimeStartPercent.
        positioned.Sort(static (a, b) =>
        {
            var cmp = a.StackIndex.CompareTo(b.StackIndex);
            if (cmp != 0) return cmp;
            cmp = a.TimeStartPercent.CompareTo(b.TimeStartPercent);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Event.Id, b.Event.Id);
        });

        return new LayoutResult(positioned, earlierOverflow, laterOverflow, blocks);
    }

    /// <summary>
    /// Turn one connected run of overflow events into either a "+N" block (run of 2+) or a
    /// single promoted capped chip (run of 1 — defensive guard so a block never hides just one
    /// event). The block spans the run's [min start, max end] clipped to the visible band and
    /// sits in the reserved last stack slot.
    /// </summary>
    private static void AppendOverflowRun(
        List<ICalendarEvent> run,
        DateTimeOffset visibleStart,
        DateTimeOffset visibleEnd,
        long visibleTicks,
        int cap,
        List<PositionedEvent> positioned,
        List<OverlapOverflowBlock> blocks)
    {
        if (run.Count == 1)
        {
            var ev = run[0];
            var clippedAtStart = ev.Start < visibleStart;
            var clippedAtEnd = ev.End > visibleEnd;
            var rs = clippedAtStart ? visibleStart : ev.Start;
            var re = clippedAtEnd ? visibleEnd : ev.End;
            // Guard: a zero-duration event at exactly the visible boundary keeps re at
            // rs so TimeSpanPercent is 0 and the renderer applies its minimum-height
            // treatment (PRD §5.2).
            if (re < rs) re = rs;
            positioned.Add(new PositionedEvent(
                Event: ev,
                TimeStartPercent: (double)(rs - visibleStart).Ticks / visibleTicks * 100.0,
                TimeSpanPercent: (double)(re - rs).Ticks / visibleTicks * 100.0,
                StackIndex: cap - 1,
                StackCount: cap,
                ClippedAtTimeStart: clippedAtStart,
                ClippedAtTimeEnd: clippedAtEnd));
            return;
        }

        // run is start-sorted, so run[0].Start is the min start; scan for the max end.
        var runStart = run[0].Start;
        var runEnd = run[0].End;
        for (var i = 1; i < run.Count; i++)
        {
            if (run[i].End > runEnd) runEnd = run[i].End;
        }
        var blockStart = runStart < visibleStart ? visibleStart : runStart;
        var blockEnd = runEnd > visibleEnd ? visibleEnd : runEnd;
        if (blockEnd < blockStart) blockEnd = blockStart;

        blocks.Add(new OverlapOverflowBlock(
            TimeStartPercent: (double)(blockStart - visibleStart).Ticks / visibleTicks * 100.0,
            TimeSpanPercent: (double)(blockEnd - blockStart).Ticks / visibleTicks * 100.0,
            StackIndex: cap - 1,
            StackCount: cap,
            RegionStart: blockStart,
            RegionEnd: blockEnd,
            Events: run));
    }

    // ---------------------------------------------------------------------------
    // Inverse-mapping helpers (Phase 2 Task 3).
    //
    // Forward layout (above) computes pixel/percent coordinates from event times.
    // Drag operations need the opposite: a drop point in viewport pixels has to
    // become a (date, time, lane) tuple. These helpers are static + pure so any
    // view code-behind can call them at drop time without engine-instance state.
    //
    // Snap + clamp policy (consistent across all four helpers):
    //   - Pixels < 0  → first slot (clamped).
    //   - Pixels at exactly 0 → first slot.
    //   - Pixels at exactly the total → last legal slot (NOT past the end).
    //   - Pixels past the total → clamped to last legal slot.
    //   - Pixels at the boundary between two slots → next slot wins (Math.Round
    //     rounds half-up).
    //   - Zero-duration / zero-extent inputs → safe defaults (first slot / 0 /
    //     -1 for "no lanes").
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Inverse of the vertical-time layout: given a Y pixel offset within a hour-grid
    /// of <paramref name="totalHeightPx"/> height, covering <c>[rangeStart, rangeEndExclusive]</c>,
    /// return the corresponding <see cref="DateTimeOffset"/> snapped to the nearest slot
    /// of <paramref name="slotMinutes"/>. Pixel coords are clamped to the range bounds
    /// (a Y past the end returns <c>rangeEndExclusive - slotMinutes</c>).
    /// </summary>
    /// <param name="pixelY">Y offset (pixels) into the time grid.</param>
    /// <param name="totalHeightPx">Total height of the time grid in pixels.</param>
    /// <param name="rangeStart">Inclusive start of the time range.</param>
    /// <param name="rangeEndExclusive">Exclusive end of the time range.</param>
    /// <param name="slotMinutes">Snap granularity in minutes.</param>
    internal static DateTimeOffset InverseY(
        double pixelY,
        double totalHeightPx,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        int slotMinutes)
    {
        if (totalHeightPx <= 0) return rangeStart;
        var totalMinutes = (rangeEndExclusive - rangeStart).TotalMinutes;
        if (totalMinutes <= 0) return rangeStart;
        var clampedY = Math.Clamp(pixelY, 0, totalHeightPx);
        var minutesFromStart = clampedY / totalHeightPx * totalMinutes;
        // MidpointRounding.AwayFromZero: a pixel sitting exactly between two slots
        // tips to the *next* slot (e.g., between 00:00 and 00:30 → 00:30). The
        // default banker's-rounding would tip to the *closer-even-multiple*,
        // surprising users dropping just past a slot boundary.
        var snapped = Math.Round(minutesFromStart / slotMinutes, MidpointRounding.AwayFromZero) * slotMinutes;
        var maxSnapped = totalMinutes - slotMinutes;  // last legal slot start
        if (snapped > maxSnapped) snapped = maxSnapped;
        if (snapped < 0) snapped = 0;
        return rangeStart.AddMinutes(snapped);
    }

    /// <summary>
    /// Inverse of the horizontal-time layout (TimelineView in Day mode): given an X
    /// pixel offset within a <paramref name="totalWidthPx"/>-wide lane covering
    /// <c>[rangeStart, rangeEndExclusive]</c>, return the corresponding
    /// <see cref="DateTimeOffset"/> snapped to <paramref name="slotMinutes"/>. Same
    /// semantics as <see cref="InverseY"/> with the axis swapped.
    /// </summary>
    /// <param name="pixelX">X offset (pixels) into the time lane.</param>
    /// <param name="totalWidthPx">Total width of the time lane in pixels.</param>
    /// <param name="rangeStart">Inclusive start of the time range.</param>
    /// <param name="rangeEndExclusive">Exclusive end of the time range.</param>
    /// <param name="slotMinutes">Snap granularity in minutes.</param>
    internal static DateTimeOffset InverseX(
        double pixelX,
        double totalWidthPx,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        int slotMinutes)
    {
        if (totalWidthPx <= 0) return rangeStart;
        var totalMinutes = (rangeEndExclusive - rangeStart).TotalMinutes;
        if (totalMinutes <= 0) return rangeStart;
        var clampedX = Math.Clamp(pixelX, 0, totalWidthPx);
        var minutesFromStart = clampedX / totalWidthPx * totalMinutes;
        // Same midpoint policy as InverseY (see comment there).
        var snapped = Math.Round(minutesFromStart / slotMinutes, MidpointRounding.AwayFromZero) * slotMinutes;
        var maxSnapped = totalMinutes - slotMinutes;
        if (snapped > maxSnapped) snapped = maxSnapped;
        if (snapped < 0) snapped = 0;
        return rangeStart.AddMinutes(snapped);
    }

    /// <summary>
    /// Inverse of the day-cell layout (TimelineView Week/Month, Month view): given an
    /// X pixel offset within a <paramref name="totalWidthPx"/>-wide row of <paramref name="dayCount"/>
    /// day cells, return the 0-based index of the cell the pixel falls in (clamped to
    /// <c>[0, dayCount - 1]</c>). Returns 0 for non-positive <paramref name="dayCount"/>
    /// or <paramref name="totalWidthPx"/>.
    /// </summary>
    /// <param name="pixelX">X offset (pixels) into the day row.</param>
    /// <param name="totalWidthPx">Total width of the day row in pixels.</param>
    /// <param name="dayCount">Number of day cells in the row.</param>
    internal static int InverseDayCell(
        double pixelX,
        double totalWidthPx,
        int dayCount)
    {
        if (dayCount <= 0) return 0;
        if (totalWidthPx <= 0) return 0;
        var clampedX = Math.Clamp(pixelX, 0, totalWidthPx);
        var idx = (int)(clampedX / (totalWidthPx / dayCount));
        if (idx >= dayCount) idx = dayCount - 1;
        return idx;
    }

    /// <summary>
    /// Inverse of the lane layout (TimelineView): given a Y pixel offset within a
    /// <paramref name="totalHeightPx"/>-tall stack of <paramref name="laneCount"/> lane
    /// rows, return the 0-based lane index. When the pixel is past the last lane,
    /// returns <c>laneCount - 1</c> (clamped). When <paramref name="laneCount"/> is 0,
    /// returns -1 (no lanes exist to hit-test against).
    /// </summary>
    /// <param name="pixelY">Y offset (pixels) into the lane stack.</param>
    /// <param name="totalHeightPx">Total height of the lane stack in pixels.</param>
    /// <param name="laneCount">Number of lane rows in the stack (including any unassigned row).</param>
    internal static int InverseLaneRow(
        double pixelY,
        double totalHeightPx,
        int laneCount)
    {
        if (laneCount <= 0) return -1;
        if (totalHeightPx <= 0) return 0;
        var clampedY = Math.Clamp(pixelY, 0, totalHeightPx);
        var idx = (int)(clampedY / (totalHeightPx / laneCount));
        if (idx >= laneCount) idx = laneCount - 1;
        return idx;
    }
}
