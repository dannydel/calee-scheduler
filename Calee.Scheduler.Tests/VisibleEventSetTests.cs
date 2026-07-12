using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="VisibleEventSet{TEvent}"/> — the frozen-by-construction pre-processed
/// view of events shared by Day, Week, and TimelineView. Validates classification, multi-day
/// splitting (PerDay vs. Continuous), clip flags, lookup behavior, and determinism.
/// </summary>
public class VisibleEventSetTests
{
    // Anchor TZ at America/New_York so DST math is exercised by realistic timestamps.
    // The scenarios are intentionally outside DST transitions; New_York is just a stable
    // non-UTC zone for the timezone-conversion paths.
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Range: Mon May 18 2026 00:00 ET → Thu May 21 2026 00:00 ET (3 days).
    private static readonly DateTimeOffset MonMidnight =
        new(2026, 5, 18, 0, 0, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset TueMidnight =
        new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset WedMidnight =
        new(2026, 5, 20, 0, 0, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset ThuMidnight =
        new(2026, 5, 21, 0, 0, 0, TimeSpan.FromHours(-4));

    private static CalendarEvent Timed(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: false);

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    private static VisibleEventSet<CalendarEvent> Build(
        IEnumerable<CalendarEvent> events,
        EventSplitMode splitMode,
        DateTimeOffset? rangeStart = null,
        DateTimeOffset? rangeEnd = null) =>
        new(events, rangeStart ?? MonMidnight, rangeEnd ?? ThuMidnight, Tz, splitMode);

    [Fact]
    public void Empty_Input_Produces_Empty_Result()
    {
        var set = Build(Array.Empty<CalendarEvent>(), EventSplitMode.PerDay);

        Assert.Empty(set.AllDay);
        Assert.Empty(set.TimedChunks);
    }

    [Fact]
    public void AllDay_Event_In_Range_Goes_To_AllDay_List()
    {
        var ev = AllDay("vacation", MonMidnight, TueMidnight);

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        var only = Assert.Single(set.AllDay);
        Assert.Same(ev, only);
        Assert.Empty(set.TimedChunks);
    }

    [Fact]
    public void AllDay_Event_Outside_Range_Excluded()
    {
        // Sunday — entirely before the Mon..Thu range.
        var sunStart = MonMidnight.AddDays(-1);
        var sunEnd = MonMidnight;
        var ev = AllDay("yesterday", sunStart, sunEnd);

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Empty(set.AllDay);
        Assert.Empty(set.TimedChunks);
    }

    [Fact]
    public void Multi_Day_AllDay_Event_Spanning_Range_Included_Once()
    {
        // Sun..Fri: starts before the range, ends after it. Should appear once in AllDay.
        var ev = AllDay("conference", MonMidnight.AddDays(-1), ThuMidnight.AddDays(1));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Single(set.AllDay);
        Assert.Same(ev, set.AllDay[0]);
    }

    [Fact]
    public void Timed_Event_Inside_Range_Becomes_One_Chunk_With_No_Clip_Flags()
    {
        // Single-day timed event 09:00–10:00 on Tuesday.
        var ev = Timed("morning", TueMidnight.AddHours(9), TueMidnight.AddHours(10));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        var chunk = Assert.Single(set.TimedChunks);
        Assert.Same(ev, chunk.Event);
        Assert.Equal(ev.Start, chunk.Start);
        Assert.Equal(ev.End, chunk.End);
        Assert.False(chunk.ClippedAtTimeStart);
        Assert.False(chunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void Timed_Event_Outside_Range_Excluded()
    {
        // Friday — entirely after the Mon..Thu range.
        var ev = Timed("future", ThuMidnight.AddHours(10), ThuMidnight.AddHours(11));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Empty(set.TimedChunks);
    }

    [Fact]
    public void PerDay_Splits_MidnightCrossing_Into_Three_Chunks()
    {
        // Mon 23:00 → Wed 02:00. PerDay should produce 3 chunks: Mon 23:00–00:00,
        // Tue 00:00–00:00 (full day), Wed 00:00–02:00.
        var ev = Timed("overnight", MonMidnight.AddHours(23), WedMidnight.AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Equal(3, set.TimedChunks.Count);

        var monChunk = set.TimedChunks[0];
        var tueChunk = set.TimedChunks[1];
        var wedChunk = set.TimedChunks[2];

        Assert.Equal(MonMidnight.AddHours(23), monChunk.Start);
        Assert.Equal(TueMidnight, monChunk.End);

        Assert.Equal(TueMidnight, tueChunk.Start);
        Assert.Equal(WedMidnight, tueChunk.End);

        Assert.Equal(WedMidnight, wedChunk.Start);
        Assert.Equal(WedMidnight.AddHours(2), wedChunk.End);
    }

    [Fact]
    public void PerDay_First_Chunk_Has_ClippedAtTimeEnd_True_Middle_Has_Both_Last_Has_ClippedAtTimeStart()
    {
        var ev = Timed("overnight", MonMidnight.AddHours(23), WedMidnight.AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        var monChunk = set.TimedChunks[0];
        var tueChunk = set.TimedChunks[1];
        var wedChunk = set.TimedChunks[2];

        // First chunk: original starts on Mon (no clip-start), extends past Mon midnight (clip-end).
        Assert.False(monChunk.ClippedAtTimeStart);
        Assert.True(monChunk.ClippedAtTimeEnd);

        // Middle chunk: original extends past both Tue midnight edges.
        Assert.True(tueChunk.ClippedAtTimeStart);
        Assert.True(tueChunk.ClippedAtTimeEnd);

        // Last chunk: original extends before Wed midnight (clip-start), ends on Wed (no clip-end).
        Assert.True(wedChunk.ClippedAtTimeStart);
        Assert.False(wedChunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void Continuous_Multi_Day_Stays_One_Chunk_With_Span_Equal_To_Original()
    {
        // Same overnight event in Continuous mode — one chunk covering the full span.
        var ev = Timed("overnight", MonMidnight.AddHours(23), WedMidnight.AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.Continuous);

        var chunk = Assert.Single(set.TimedChunks);
        Assert.Equal(ev.Start, chunk.Start);
        Assert.Equal(ev.End, chunk.End);
        Assert.False(chunk.ClippedAtTimeStart);
        Assert.False(chunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void Continuous_Multi_Day_Extending_Past_RangeStart_Has_ClippedAtTimeStart_True()
    {
        // Event Sun 22:00 → Mon 02:00. RangeStart is Mon midnight.
        var ev = Timed("late-sunday", MonMidnight.AddHours(-2), MonMidnight.AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.Continuous);

        var chunk = Assert.Single(set.TimedChunks);
        Assert.Equal(MonMidnight, chunk.Start); // pre-clipped to rangeStart
        Assert.Equal(MonMidnight.AddHours(2), chunk.End);
        Assert.True(chunk.ClippedAtTimeStart);
        Assert.False(chunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void Continuous_Multi_Day_Extending_Past_RangeEnd_Has_ClippedAtTimeEnd_True()
    {
        // Event Wed 22:00 → Fri 02:00. RangeEnd is Thu midnight.
        var ev = Timed("late-wednesday", WedMidnight.AddHours(22), ThuMidnight.AddDays(1).AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.Continuous);

        var chunk = Assert.Single(set.TimedChunks);
        Assert.Equal(WedMidnight.AddHours(22), chunk.Start);
        Assert.Equal(ThuMidnight, chunk.End); // pre-clipped to rangeEnd
        Assert.False(chunk.ClippedAtTimeStart);
        Assert.True(chunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void Zero_Duration_Event_Produces_One_Zero_Width_Chunk()
    {
        // A reminder at Tue 09:00 with Start == End.
        var instant = TueMidnight.AddHours(9);
        var ev = Timed("reminder", instant, instant);

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        var chunk = Assert.Single(set.TimedChunks);
        Assert.Equal(instant, chunk.Start);
        Assert.Equal(instant, chunk.End);
        Assert.False(chunk.ClippedAtTimeStart);
        Assert.False(chunk.ClippedAtTimeEnd);
    }

    [Fact]
    public void FindById_Returns_Original_Consumer_Event()
    {
        var ev = Timed("e1", TueMidnight.AddHours(9), TueMidnight.AddHours(10));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        var found = set.FindById("e1");
        Assert.NotNull(found);
        Assert.Same(ev, found);
    }

    [Fact]
    public void FindById_Returns_Null_For_Unknown_Id()
    {
        var ev = Timed("e1", TueMidnight.AddHours(9), TueMidnight.AddHours(10));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Null(set.FindById("nope"));
    }

    [Fact]
    public void Duplicate_Id_Throws_Actionable_ArgumentException()
    {
        var first = Timed("dup", TueMidnight.AddHours(9), TueMidnight.AddHours(10));
        var second = Timed("dup", TueMidnight.AddHours(11), TueMidnight.AddHours(12));

        var ex = Assert.Throws<ArgumentException>(() =>
            Build(new[] { first, second }, EventSplitMode.PerDay));

        Assert.Equal("events", ex.ParamName);
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedChunks_Sorted_By_Start_Ascending()
    {
        // Intentionally pass events in reverse order; expect chunks sorted by Start.
        var late = Timed("late", TueMidnight.AddHours(15), TueMidnight.AddHours(16));
        var early = Timed("early", TueMidnight.AddHours(9), TueMidnight.AddHours(10));
        var mid = Timed("mid", TueMidnight.AddHours(12), TueMidnight.AddHours(13));

        var set = Build(new[] { late, mid, early }, EventSplitMode.PerDay);

        Assert.Equal(3, set.TimedChunks.Count);
        Assert.Equal("early", set.TimedChunks[0].Id);
        Assert.Equal("mid", set.TimedChunks[1].Id);
        Assert.Equal("late", set.TimedChunks[2].Id);
    }

    [Fact]
    public void Per_Day_Chunks_Of_Multi_Day_Event_All_Resolve_To_Same_Original_Via_FindById()
    {
        // Confirm the lookup-after-split contract: clicking ANY chunk of a multi-day event
        // resolves back to the consumer's authoritative reference.
        var ev = Timed("overnight", MonMidnight.AddHours(23), WedMidnight.AddHours(2));

        var set = Build(new[] { ev }, EventSplitMode.PerDay);

        Assert.Equal(3, set.TimedChunks.Count);
        foreach (var chunk in set.TimedChunks)
        {
            Assert.Same(ev, set.FindById(chunk.Id));
        }
    }
}
