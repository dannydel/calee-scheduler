using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="EventLayoutEngine"/> covering PRD §6.1 exit-criteria scenarios:
/// single, non-overlapping pair, fully overlapping pair, the A/B/C three-way partial
/// overlap (ADR-0003 keystone), zero-duration, fully-out-of-range, partial overflow, and
/// the null-hour-clip case used by Timeline Week/Month modes.
/// </summary>
public class EventLayoutEngineTests
{
    private static readonly DateTimeOffset Day = new(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hour, int minute = 0) =>
        Day.AddHours(hour).AddMinutes(minute);

    private static CalendarEvent Event(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isAllDay = false) =>
        new(id, id, start, end, isAllDay);

    private static LayoutResult Layout(
        IEnumerable<ICalendarEvent> events,
        DateTimeOffset? rangeStart = null,
        DateTimeOffset? rangeEnd = null,
        int? rangeStartHour = null,
        int? rangeEndHour = null) =>
        new EventLayoutEngine().Layout(
            events.ToList(),
            rangeStart ?? Day,
            rangeEnd ?? Day.AddDays(1),
            rangeStartHour,
            rangeEndHour);

    private static PositionedEvent Find(LayoutResult result, string id) =>
        result.Positioned.Single(p => p.Event.Id == id);

    [Fact]
    public void Layout_EmptyInput_ReturnsEmptyResult()
    {
        var result = Layout(Array.Empty<ICalendarEvent>());

        Assert.Empty(result.Positioned);
        Assert.Empty(result.EarlierOverflow);
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_SingleEvent_RendersFullWidthInStackZero()
    {
        // 24-hour band; one event 10:00–11:00 → TimeStart = 10/24, Span = 1/24.
        var e = Event("A", At(10), At(11));
        var result = Layout(new[] { e });

        var p = Assert.Single(result.Positioned);
        Assert.Equal(0, p.StackIndex);
        Assert.Equal(1, p.StackCount);
        Assert.Equal(10.0 / 24.0 * 100.0, p.TimeStartPercent, 6);
        Assert.Equal(1.0 / 24.0 * 100.0, p.TimeSpanPercent, 6);
        Assert.False(p.ClippedAtTimeStart);
        Assert.False(p.ClippedAtTimeEnd);
        Assert.Empty(result.EarlierOverflow);
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_TwoNonOverlappingEvents_BothStackZeroStackCountOne()
    {
        var a = Event("A", At(9), At(10));
        var b = Event("B", At(11), At(12));

        var result = Layout(new[] { a, b });

        Assert.Equal(2, result.Positioned.Count);
        Assert.All(result.Positioned, p =>
        {
            Assert.Equal(0, p.StackIndex);
            Assert.Equal(1, p.StackCount);
        });
    }

    [Fact]
    public void Layout_TwoFullyOverlappingEvents_StackIndexZeroAndOneStackCountTwo()
    {
        var a = Event("A", At(9), At(10));
        var b = Event("B", At(9), At(10));

        var result = Layout(new[] { a, b });

        var pa = Find(result, "A");
        var pb = Find(result, "B");

        // Sort tiebreaker is Id-ordinal — A wins stack 0, B takes stack 1.
        Assert.Equal(0, pa.StackIndex);
        Assert.Equal(1, pb.StackIndex);
        Assert.Equal(2, pa.StackCount);
        Assert.Equal(2, pb.StackCount);
    }

    /// <summary>
    /// The ADR-0003 keystone case: A (9–10), B (9:30–10:30), C (10–11). A and C never
    /// directly overlap, so the algorithm must reuse A's stack slot for C — yielding two
    /// stacks total with <see cref="PositionedEvent.StackCount"/> = 2 for all three events.
    /// If any event reports <c>StackCount = 3</c>, the algorithm collapsed into
    /// transitive-closure grouping and is wrong.
    /// </summary>
    [Fact]
    public void Layout_ThreeWayPartialOverlap_AllEventsStackCountTwo()
    {
        var a = Event("A", At(9), At(10));
        var b = Event("B", At(9, 30), At(10, 30));
        var c = Event("C", At(10), At(11));

        var result = Layout(new[] { a, b, c });

        var pa = Find(result, "A");
        var pb = Find(result, "B");
        var pc = Find(result, "C");

        Assert.Equal(0, pa.StackIndex);
        Assert.Equal(1, pb.StackIndex);
        // C reuses A's stack slot (slot 0) — the whole point of sweep-line with stack reuse.
        Assert.Equal(0, pc.StackIndex);

        // The whole point of ADR-0003: all three render at 50% width.
        Assert.Equal(2, pa.StackCount);
        Assert.Equal(2, pb.StackCount);
        Assert.Equal(2, pc.StackCount);
    }

    [Fact]
    public void Layout_ZeroDurationEvent_GetsMinimumHeightTreatment()
    {
        // Engine reports raw TimeSpanPercent = 0 for a zero-duration event; the renderer
        // applies any minimum-height treatment (PRD §5.2).
        var e = Event("A", At(10), At(10));
        var result = Layout(new[] { e });

        var p = Assert.Single(result.Positioned);
        Assert.Equal(0.0, p.TimeSpanPercent);
        Assert.Equal(10.0 / 24.0 * 100.0, p.TimeStartPercent, 6);
        Assert.Equal(0, p.StackIndex);
        Assert.Equal(1, p.StackCount);
    }

    [Fact]
    public void Layout_EventEntirelyBeforeStartHour_GoesToEarlierOverflow()
    {
        var e = Event("A", At(6), At(7));
        var result = Layout(new[] { e }, rangeStartHour: 8);

        Assert.Empty(result.Positioned);
        Assert.Equal(e, Assert.Single(result.EarlierOverflow));
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_EventEntirelyAfterEndHour_GoesToLaterOverflow()
    {
        var e = Event("A", At(19), At(20));
        var result = Layout(new[] { e }, rangeEndHour: 18);

        Assert.Empty(result.Positioned);
        Assert.Equal(e, Assert.Single(result.LaterOverflow));
        Assert.Empty(result.EarlierOverflow);
    }

    [Fact]
    public void Layout_EventPartiallyBeforeStartHour_RendersClippedAtTimeStart()
    {
        // Event 7:30–9:00 with rangeStartHour=8 → clipped at start, render 8:00–9:00.
        // Visible band = 16h (08:00–24:00). Span = 1h / 16h.
        var e = Event("A", At(7, 30), At(9));
        var result = Layout(new[] { e }, rangeStartHour: 8);

        var p = Assert.Single(result.Positioned);
        Assert.True(p.ClippedAtTimeStart);
        Assert.False(p.ClippedAtTimeEnd);
        Assert.Equal(0.0, p.TimeStartPercent, 6);
        Assert.Equal(1.0 / 16.0 * 100.0, p.TimeSpanPercent, 6);
        Assert.Empty(result.EarlierOverflow);
    }

    [Fact]
    public void Layout_EventPartiallyAfterEndHour_RendersClippedAtTimeEnd()
    {
        // Event 17:30–19:00 with rangeEndHour=18 → clipped at end, render 17:30–18:00.
        // Visible band = 18h (00:00–18:00). TimeStart = 17.5/18. Span = 0.5/18.
        var e = Event("A", At(17, 30), At(19));
        var result = Layout(new[] { e }, rangeEndHour: 18);

        var p = Assert.Single(result.Positioned);
        Assert.False(p.ClippedAtTimeStart);
        Assert.True(p.ClippedAtTimeEnd);
        Assert.Equal(17.5 / 18.0 * 100.0, p.TimeStartPercent, 6);
        Assert.Equal(0.5 / 18.0 * 100.0, p.TimeSpanPercent, 6);
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_EventOutsideRange_NotInResult()
    {
        // Event 15:00–16:00 lies after [10:00, 14:00]. With NO hour clip configured, the
        // event is excluded outright — it belongs to a different time band entirely. The
        // engine never speculates about which day's overflow bucket a stray event
        // should fall into.
        var rangeStart = At(10);
        var rangeEnd = At(14);
        var e = Event("A", At(15), At(16));

        var result = Layout(new[] { e }, rangeStart, rangeEnd);

        Assert.Empty(result.Positioned);
        Assert.Empty(result.EarlierOverflow);
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_EventStartingExactlyAtRangeEnd_IsExcluded()
    {
        // Locks in the half-open semantic: an event starting at exactly rangeEnd is OUTSIDE
        // the visible band (End is exclusive), so it does not render.
        var rangeStart = At(10);
        var rangeEnd = At(14);
        var e = Event("BoundaryStart", At(14), At(15));

        var result = Layout(new[] { e }, rangeStart, rangeEnd);

        Assert.Empty(result.Positioned);
        Assert.Empty(result.EarlierOverflow);
        Assert.Empty(result.LaterOverflow);
    }

    [Fact]
    public void Layout_NullClippingHours_NoOverflow()
    {
        // Timeline Week/Month: no hour-of-day floor or ceiling. Every event in [rangeStart,
        // rangeEnd] renders in Positioned, regardless of where in the band it sits.
        var a = Event("Early", At(2), At(3));
        var b = Event("Mid", At(10), At(11));
        var c = Event("Late", At(22), At(23));

        var result = Layout(new[] { a, b, c }, rangeStartHour: null, rangeEndHour: null);

        Assert.Equal(3, result.Positioned.Count);
        Assert.Empty(result.EarlierOverflow);
        Assert.Empty(result.LaterOverflow);
    }

    // -------- Additional clarifying tests --------

    [Fact]
    public void Layout_FourWayCascade_StackCountReflectsLocalConcurrency()
    {
        // A staircase that demonstrates per-event StackCount: A and B overlap (count 2),
        // B and C overlap (count 2), C and D overlap (count 2). At no instant are three
        // events concurrent. Every event must report StackCount=2, not 4.
        var a = Event("A", At(9), At(10));
        var b = Event("B", At(9, 30), At(10, 30));
        var c = Event("C", At(10), At(11));
        var d = Event("D", At(10, 30), At(11, 30));

        var result = Layout(new[] { a, b, c, d });

        Assert.All(result.Positioned, p => Assert.Equal(2, p.StackCount));
    }

    [Fact]
    public void Layout_DeterministicReruns_ProduceIdenticalOutput()
    {
        // Re-running on the same input must produce byte-identical output. Catches any
        // accidental hash-based ordering inside the engine.
        var events = new[]
        {
            Event("B", At(9, 30), At(10, 30)),
            Event("A", At(9), At(10)),
            Event("C", At(10), At(11)),
        };

        var r1 = Layout(events);
        var r2 = Layout(events);

        Assert.Equal(r1.Positioned.Count, r2.Positioned.Count);
        for (var i = 0; i < r1.Positioned.Count; i++)
        {
            Assert.Equal(r1.Positioned[i].Event.Id, r2.Positioned[i].Event.Id);
            Assert.Equal(r1.Positioned[i].StackIndex, r2.Positioned[i].StackIndex);
            Assert.Equal(r1.Positioned[i].StackCount, r2.Positioned[i].StackCount);
        }
    }

    [Fact]
    public void Layout_OptimizedSweep_MatchesNaiveReferenceAcrossRandomizedInputs()
    {
        var random = new Random(0xCA1EE);
        for (var scenario = 0; scenario < 200; scenario++)
        {
            var count = random.Next(1, 25);
            var events = new List<ICalendarEvent>(count);
            for (var i = 0; i < count; i++)
            {
                var startMinutes = random.Next(0, 20 * 60);
                var durationMinutes = random.Next(0, 240);
                events.Add(Event(
                    $"{scenario:D3}-{i:D2}",
                    Day.AddMinutes(startMinutes),
                    Day.AddMinutes(startMinutes + durationMinutes)));
            }

            var expected = NaiveStackGeometry(events);
            var actual = Layout(events).Positioned.ToDictionary(p => p.Event.Id, StringComparer.Ordinal);

            Assert.Equal(expected.Count, actual.Count);
            foreach (var (id, geometry) in expected)
            {
                Assert.Equal(geometry.StackIndex, actual[id].StackIndex);
                Assert.Equal(geometry.StackCount, actual[id].StackCount);
            }
        }
    }

    [Fact]
    public void Layout_EventSpanningEntireVisibleBand_BothClipFlagsSet()
    {
        // An event that starts before rangeStartHour and ends after rangeEndHour should
        // render full-bleed across the visible band with both clip flags set.
        var e = Event("A", At(5), At(22));
        var result = Layout(new[] { e }, rangeStartHour: 8, rangeEndHour: 18);

        var p = Assert.Single(result.Positioned);
        Assert.True(p.ClippedAtTimeStart);
        Assert.True(p.ClippedAtTimeEnd);
        Assert.Equal(0.0, p.TimeStartPercent, 6);
        Assert.Equal(100.0, p.TimeSpanPercent, 6);
    }

    // ---------------------------------------------------------------------------
    // Column-cap + overlap-overflow tests (Task 2).
    // ---------------------------------------------------------------------------

    private static readonly DateTimeOffset OvlDay =
        new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset OvlAt(int h, int m = 0) => OvlDay.AddHours(h).AddMinutes(m);

    private static CalendarEvent Ovl(string id, int startH, int endH) =>
        new(id, id, OvlAt(startH), OvlAt(endH));

    [Fact]
    public void Layout_NoMaxColumns_Unchanged_FiveConcurrent_AllPositioned()
    {
        var events = new ICalendarEvent[]
        {
            Ovl("a", 9, 10), Ovl("b", 9, 10), Ovl("c", 9, 10),
            Ovl("d", 9, 10), Ovl("e", 9, 10),
        };
        var result = new EventLayoutEngine().Layout(events, OvlDay, OvlDay.AddDays(1));

        Assert.Equal(5, result.Positioned.Count);
        Assert.Empty(result.OverlapOverflow);
        Assert.All(result.Positioned, pe => Assert.Equal(5, pe.StackCount));
    }

    [Fact]
    public void Layout_ThreeConcurrent_MaxColumns3_NoBlock_AllVisible()
    {
        var events = new ICalendarEvent[] { Ovl("a", 9, 10), Ovl("b", 9, 10), Ovl("c", 9, 10) };
        var result = new EventLayoutEngine().Layout(events, OvlDay, OvlDay.AddDays(1), maxColumns: 3);

        Assert.Equal(3, result.Positioned.Count);
        Assert.Empty(result.OverlapOverflow);
        Assert.All(result.Positioned, pe => Assert.Equal(3, pe.StackCount)); // width 1/3
    }

    [Fact]
    public void Layout_FourConcurrent_MaxColumns3_TwoVisible_OneBlockOfTwo()
    {
        var events = new ICalendarEvent[]
        {
            Ovl("a", 9, 10), Ovl("b", 9, 10), Ovl("c", 9, 10), Ovl("d", 9, 10),
        };
        var result = new EventLayoutEngine().Layout(events, OvlDay, OvlDay.AddDays(1), maxColumns: 3);

        Assert.Equal(2, result.Positioned.Count);
        Assert.All(result.Positioned, pe => Assert.Equal(3, pe.StackCount));
        var block = Assert.Single(result.OverlapOverflow);
        Assert.Equal(2, block.Events.Count);          // never "+1"
        Assert.Equal(2, block.StackIndex);            // reserved last column (cap-1)
        Assert.Equal(3, block.StackCount);
        Assert.Equal(OvlAt(9), block.RegionStart);
        Assert.Equal(OvlAt(10), block.RegionEnd);
    }

    [Fact]
    public void Layout_FiveConcurrent_MaxColumns3_BlockHidesThree()
    {
        var events = new ICalendarEvent[]
        {
            Ovl("a", 9, 10), Ovl("b", 9, 10), Ovl("c", 9, 10),
            Ovl("d", 9, 10), Ovl("e", 9, 10),
        };
        var result = new EventLayoutEngine().Layout(events, OvlDay, OvlDay.AddDays(1), maxColumns: 3);

        Assert.Equal(2, result.Positioned.Count);
        var block = Assert.Single(result.OverlapOverflow);
        Assert.Equal(3, block.Events.Count);
        Assert.Equal(2, block.StackIndex);
        Assert.Equal(3, block.StackCount);
        Assert.Equal(OvlAt(9), block.RegionStart);
        Assert.Equal(OvlAt(10), block.RegionEnd);
    }

    [Fact]
    public void Layout_TwoCrowdedRegions_MaxColumns3_ProducesTwoBlocks()
    {
        var events = new ICalendarEvent[]
        {
            Ovl("a", 9, 10), Ovl("b", 9, 10), Ovl("c", 9, 10), Ovl("d", 9, 10),     // 9-10 crowded
            Ovl("e", 11, 12), Ovl("f", 11, 12), Ovl("g", 11, 12), Ovl("h", 11, 12), // 11-12 crowded
        };
        var result = new EventLayoutEngine().Layout(events, OvlDay, OvlDay.AddDays(1), maxColumns: 3);

        Assert.Equal(4, result.Positioned.Count);
        Assert.All(result.Positioned, pe => Assert.Equal(3, pe.StackCount));
        Assert.Equal(2, result.OverlapOverflow.Count);
        Assert.All(result.OverlapOverflow, b => Assert.Equal(2, b.Events.Count));
        Assert.All(result.OverlapOverflow, b => Assert.Equal(2, b.StackIndex));
        Assert.All(result.OverlapOverflow, b => Assert.Equal(3, b.StackCount));
        Assert.Contains(result.OverlapOverflow, b => b.RegionStart == OvlAt(9));
        Assert.Contains(result.OverlapOverflow, b => b.RegionStart == OvlAt(11));
    }

    // ---------------------------------------------------------------------------
    // Inverse-mapping helpers (Phase 2 Task 3). Coverage of the boundary semantics
    // documented on the helper methods themselves:
    //   - Y=0 → rangeStart.
    //   - Y at exactly total height → last legal slot (rangeEndExclusive - slotMinutes).
    //   - Y < 0 / Y > total → clamp to first/last slot.
    //   - Y exactly at slot boundary → next slot (Math.Round half-up).
    //   - Zero-duration / zero-extent → safe defaults.
    //   - InverseX is the axis-swapped analog of InverseY.
    //   - InverseDayCell / InverseLaneRow clamp to [0, count-1]; lane returns -1
    //     when there are zero lanes.
    // ---------------------------------------------------------------------------

    // 24-hour band [00:00, 24:00) covering Day. Total height 480px → 20px / hour.
    private const double TotalHeight24h = 480;
    private const double TotalWidth24h = 480;

    [Fact]
    public void InverseY_AtZero_ReturnsRangeStart()
    {
        var t = EventLayoutEngine.InverseY(0, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day, t);
    }

    [Fact]
    public void InverseY_AtHalf_Returns_Midpoint_Snapped()
    {
        // Half of a 24h range is 12:00 exactly — already on a 30-minute slot.
        var t = EventLayoutEngine.InverseY(TotalHeight24h / 2, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day.AddHours(12), t);
    }

    [Fact]
    public void InverseY_AtTotal_Returns_LastLegalSlot()
    {
        // A drop at exactly the bottom edge must be clamped to the last legal slot,
        // not past the end. For a 24h range with 30-minute slots → 23:30.
        var t = EventLayoutEngine.InverseY(TotalHeight24h, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day.AddHours(23).AddMinutes(30), t);
    }

    [Fact]
    public void InverseY_Negative_ClampsToZero()
    {
        var t = EventLayoutEngine.InverseY(-50, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day, t);
    }

    [Fact]
    public void InverseY_BeyondTotal_ClampsToLastSlot()
    {
        var t = EventLayoutEngine.InverseY(TotalHeight24h * 2, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day.AddHours(23).AddMinutes(30), t);
    }

    [Fact]
    public void InverseY_SnapToNearest_BoundaryTipsToNext()
    {
        // For 24h / 480px: a pixel at 15 minutes into the band is 5px. The boundary
        // between 0 and 30 (slotMinutes=30) sits at 15 min, which Math.Round half-up
        // tips to 30 → 00:30.
        // 15 min = 5px in a 480px / 24h grid.
        var t = EventLayoutEngine.InverseY(5, TotalHeight24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day.AddMinutes(30), t);
    }

    [Fact]
    public void InverseY_ZeroDurationRange_ReturnsRangeStart()
    {
        // A zero-duration band (start == end) has no slots; helper must return rangeStart
        // rather than NaN-ing on division by zero.
        var t = EventLayoutEngine.InverseY(50, TotalHeight24h, Day, Day, 30);
        Assert.Equal(Day, t);
    }

    [Fact]
    public void InverseY_ZeroTotalHeight_ReturnsRangeStart()
    {
        // Layout hasn't measured yet (totalHeightPx=0); avoid division by zero by
        // returning the start of the band.
        var t = EventLayoutEngine.InverseY(50, 0, Day, Day.AddDays(1), 30);
        Assert.Equal(Day, t);
    }

    [Theory]
    [InlineData(0, 0)]           // Y=0 → 00:00
    [InlineData(240, 12 * 60)]   // Y=mid → 12:00
    [InlineData(480, 23 * 60 + 30)]  // Y=total → 23:30 (last legal slot)
    [InlineData(-50, 0)]         // negative → 00:00
    [InlineData(960, 23 * 60 + 30)]  // past total → 23:30
    public void InverseX_Behaves_Identically_To_InverseY_With_Axes_Swapped(double pixel, int expectedMinutes)
    {
        // The two helpers must produce identical results for the same logical input —
        // InverseX is just InverseY with the axis swapped. Verifying matched outputs
        // also pins InverseX's behavior to the same contract.
        var ty = EventLayoutEngine.InverseY(pixel, TotalHeight24h, Day, Day.AddDays(1), 30);
        var tx = EventLayoutEngine.InverseX(pixel, TotalWidth24h, Day, Day.AddDays(1), 30);
        Assert.Equal(Day.AddMinutes(expectedMinutes), tx);
        Assert.Equal(ty, tx);
    }

    [Fact]
    public void InverseDayCell_AtZero_ReturnsZero()
    {
        Assert.Equal(0, EventLayoutEngine.InverseDayCell(0, 700, 7));
    }

    [Fact]
    public void InverseDayCell_BeyondLast_ClampsToLastIndex()
    {
        // X at exactly total width → last cell (clamped). And way past → still last.
        Assert.Equal(6, EventLayoutEngine.InverseDayCell(700, 700, 7));
        Assert.Equal(6, EventLayoutEngine.InverseDayCell(10_000, 700, 7));
    }

    [Fact]
    public void InverseDayCell_EmptyCount_ReturnsZero()
    {
        // Zero days → degenerate result; helper returns 0 rather than throwing.
        Assert.Equal(0, EventLayoutEngine.InverseDayCell(50, 700, 0));
        // Zero width also returns 0 (layout hasn't measured yet).
        Assert.Equal(0, EventLayoutEngine.InverseDayCell(50, 0, 7));
    }

    [Fact]
    public void InverseLaneRow_NoLanes_ReturnsNegativeOne()
    {
        // No lanes to hit-test against — caller can branch on -1.
        Assert.Equal(-1, EventLayoutEngine.InverseLaneRow(100, 400, 0));
    }

    [Fact]
    public void InverseLaneRow_AtZero_ReturnsFirstLane()
    {
        Assert.Equal(0, EventLayoutEngine.InverseLaneRow(0, 400, 4));
    }

    [Fact]
    public void InverseLaneRow_BeyondLast_ClampsToLastIndex()
    {
        Assert.Equal(3, EventLayoutEngine.InverseLaneRow(400, 400, 4));
        Assert.Equal(3, EventLayoutEngine.InverseLaneRow(10_000, 400, 4));
        // Negative Y clamps to lane 0.
        Assert.Equal(0, EventLayoutEngine.InverseLaneRow(-50, 400, 4));
    }

    private static Dictionary<string, (int StackIndex, int StackCount)> NaiveStackGeometry(
        IReadOnlyList<ICalendarEvent> input)
    {
        var events = input
            .OrderBy(e => e.Start)
            .ThenBy(e => e.End)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        var stackEnds = new List<DateTimeOffset>();
        var stackIndices = new int[events.Length];

        for (var i = 0; i < events.Length; i++)
        {
            var assigned = -1;
            for (var slot = 0; slot < stackEnds.Count; slot++)
            {
                if (stackEnds[slot] > events[i].Start) continue;
                assigned = slot;
                stackEnds[slot] = events[i].End;
                break;
            }
            if (assigned < 0)
            {
                assigned = stackEnds.Count;
                stackEnds.Add(events[i].End);
            }
            stackIndices[i] = assigned;
        }

        var result = new Dictionary<string, (int, int)>(events.Length, StringComparer.Ordinal);
        for (var i = 0; i < events.Length; i++)
        {
            var current = events[i];
            var instants = events
                .Where(other => other.Start == current.Start
                    || (other.Start > current.Start && other.Start < current.End))
                .Select(other => other.Start)
                .Distinct();
            var maxConcurrency = 1;
            foreach (var instant in instants)
            {
                var concurrency = events.Count(other =>
                    other.Start <= instant
                    && (instant < other.End || (other.Start == other.End && other.Start == instant)));
                maxConcurrency = Math.Max(maxConcurrency, concurrency);
            }
            result[current.Id] = (stackIndices[i], maxConcurrency);
        }
        return result;
    }
}
