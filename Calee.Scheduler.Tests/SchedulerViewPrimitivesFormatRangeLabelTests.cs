using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Pure-static tests for <see cref="SchedulerViewPrimitives.FormatRangeLabel"/> and
/// <see cref="SchedulerViewPrimitives.AdvanceAnchor"/> per FR-41 / FR-40. No bUnit.
/// </summary>
public class SchedulerViewPrimitivesFormatRangeLabelTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Useful anchors per FR-41:
    //   2026-03-18 (Wed) — Week sample falls Sun 3/15 … Sat 3/21 (Sunday-first)
    //   2026-04-01 (Wed) — Week sample crosses month boundary (3/29 … 4/4 Sunday-first)
    //   2025-12-31 (Wed) — Week sample crosses year boundary (12/28/2025 … 1/3/2026 Sunday-first)
    //   2026-03-17 (Tue) — Day sample matches PRD literal "Tue, Mar 18, 2026" no wait — let's use the literal: 2026-03-18 (Wed). Adjust string accordingly.

    private static DateTimeOffset On(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TZ.GetUtcOffset(new DateTime(year, month, day)));

    [Fact]
    public void Format_Day_Returns_Weekday_Month_Day_Year()
    {
        // 2026-03-18 is a Wednesday in en-US. Use March 17, 2026 (Tuesday) for the literal in PRD.
        var date = On(2026, 3, 17);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Day, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("Tue, Mar 17, 2026", label);
    }

    [Fact]
    public void Format_Week_Same_Month()
    {
        // Anchor inside the week 2026-03-15 (Sun) .. 2026-03-21 (Sat).
        // FR-41 PRD literal: "Mar 18 – 24, 2026". Use a Sunday-first week that lands those bounds:
        // pick Wednesday 2026-03-18; with FirstDayOfWeek=Wednesday week is 3/18..3/24.
        var date = On(2026, 3, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Wednesday);
        Assert.Equal("Mar 18 – 24, 2026", label);
    }

    [Fact]
    public void Format_Week_Cross_Month()
    {
        // FR-41 literal: "Mar 29 – Apr 4, 2026"  (Sunday 3/29 .. Saturday 4/4).
        // Anchor Tuesday 2026-03-31 with FirstDayOfWeek=Sunday → 3/29..4/4.
        var date = On(2026, 3, 31);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("Mar 29 – Apr 4, 2026", label);
    }

    [Fact]
    public void Format_Week_Cross_Year()
    {
        // FR-41 literal: "Dec 29, 2025 – Jan 4, 2026"  (Monday-first: 12/29 .. 1/4).
        // Anchor Wednesday 2025-12-31 with FirstDayOfWeek=Monday → 12/29..1/4.
        var date = On(2025, 12, 31);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Monday);
        Assert.Equal("Dec 29, 2025 – Jan 4, 2026", label);
    }

    [Fact]
    public void Format_Month_Returns_Long_Month_And_Year()
    {
        var date = On(2026, 3, 17);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Month, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("March 2026", label);
    }

    [Fact]
    public void Format_Timeline_Day_Matches_Day()
    {
        var date = On(2026, 3, 17);
        var timeline = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Timeline, date, TZ, DayOfWeek.Sunday, TimelineScale.Day);
        var day = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Day, date, TZ, DayOfWeek.Sunday);
        Assert.Equal(day, timeline);
    }

    [Fact]
    public void Format_Timeline_Week_Matches_Week()
    {
        var date = On(2026, 3, 18);
        var timeline = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Timeline, date, TZ, DayOfWeek.Wednesday, TimelineScale.Week);
        var week = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Wednesday);
        Assert.Equal(week, timeline);
    }

    [Fact]
    public void Format_Timeline_Month_Matches_Month()
    {
        var date = On(2026, 3, 17);
        var timeline = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Timeline, date, TZ, DayOfWeek.Sunday, TimelineScale.Month);
        var month = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Month, date, TZ, DayOfWeek.Sunday);
        Assert.Equal(month, timeline);
    }

    [Fact]
    public void Format_Uses_EnDash_Not_Hyphen()
    {
        var date = On(2026, 3, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Wednesday);
        Assert.Contains("–", label);     // U+2013 EN DASH
        Assert.DoesNotContain("-", label);    // hyphen-minus must not be used in the range separator
    }

    [Fact]
    public void Format_Honors_FirstDayOfWeek_For_Week_Range()
    {
        // Anchor Wed 2026-03-18 with FirstDayOfWeek=Sunday should yield 3/15..3/21
        // (same-month → "Mar 15 – 21, 2026").
        var date = On(2026, 3, 18);
        var sun = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("Mar 15 – 21, 2026", sun);

        // Same anchor with FirstDayOfWeek=Monday → 3/16..3/22.
        var mon = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Week, date, TZ, DayOfWeek.Monday);
        Assert.Equal("Mar 16 – 22, 2026", mon);
    }

    // ----- WorkWeek coverage (issue #7) ---------------------------------------------

    [Fact]
    public void Format_WorkWeek_Default_MonToFri()
    {
        // Anchor Wed 2026-03-18, FirstDayOfWeek=Sunday → week 3/15..3/21. Default
        // WorkWeekDays (null → Mon-Fri) → 3/16..3/20 → "Mar 16 – 20, 2026".
        var date = On(2026, 3, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.WorkWeek, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("Mar 16 – 20, 2026", label);
    }

    [Fact]
    public void Format_WorkWeek_Honors_Explicit_WorkWeekDays()
    {
        var date = On(2026, 3, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.WorkWeek, date, TZ, DayOfWeek.Sunday,
            workWeekDays: new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday });
        Assert.Equal("Mar 17 – 19, 2026", label);
    }

    [Fact]
    public void Format_WorkWeek_CrossMonth()
    {
        // Anchor Tue 2026-03-31 with FirstDayOfWeek=Sunday → week 3/29..4/4.
        // Mon-Fri subset → 3/30..4/3 → "Mar 30 – Apr 3, 2026".
        var date = On(2026, 3, 31);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.WorkWeek, date, TZ, DayOfWeek.Sunday);
        Assert.Equal("Mar 30 – Apr 3, 2026", label);
    }

    [Fact]
    public void Format_WorkWeek_EmptySubset_SoftDegrades_To_AllSevenDays_Span()
    {
        // PRD §4.6 soft-degradation, mirrored silently by the pure formatter: an empty
        // (or no-match) WorkWeekDays list falls back to spanning all seven days.
        var date = On(2026, 3, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.WorkWeek, date, TZ, DayOfWeek.Sunday,
            workWeekDays: Array.Empty<DayOfWeek>());
        Assert.Equal("Mar 15 – 21, 2026", label);
    }

    [Fact]
    public void Advance_WorkWeek_Adds_Seven_Days()
    {
        var date = On(2026, 3, 17);
        var next = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.WorkWeek, date, +1, TZ);
        var prev = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.WorkWeek, date, -1, TZ);
        Assert.Equal(date.AddDays(7), next);
        Assert.Equal(date.AddDays(-7), prev);
    }

    // ----- FilterVisibleDays coverage (shared with CaleeSchedulerWeekView) ---------

    [Fact]
    public void FilterVisibleDays_ReturnsOnlyRequestedWeekdays_PreservingInputOrder()
    {
        var allDays = SchedulerViewPrimitives.ComputeWeekDays(On(2026, 3, 18), DayOfWeek.Monday, TZ);
        var filtered = SchedulerViewPrimitives.FilterVisibleDays(
            allDays, new[] { DayOfWeek.Wednesday, DayOfWeek.Monday });

        Assert.Equal(2, filtered.Count);
        Assert.Equal(DayOfWeek.Monday, filtered[0].Start.DayOfWeek);
        Assert.Equal(DayOfWeek.Wednesday, filtered[1].Start.DayOfWeek);
    }

    [Fact]
    public void FilterVisibleDays_NoMatch_ReturnsEmpty_CallerDecidesFallback()
    {
        // Pure helper — it does NOT itself soft-degrade; callers (CaleeSchedulerWeekView,
        // the root's WorkWeek ComputeRange/FormatRangeLabel) decide the fallback.
        var allDays = SchedulerViewPrimitives.ComputeWeekDays(On(2026, 3, 18), DayOfWeek.Sunday, TZ);
        var filtered = SchedulerViewPrimitives.FilterVisibleDays(allDays, Array.Empty<DayOfWeek>());
        Assert.Empty(filtered);
    }

    // ----- AdvanceAnchor coverage --------------------------------------------------

    [Fact]
    public void Advance_Day_Adds_One_Day()
    {
        var date = On(2026, 3, 17);
        var next = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.Day, date, +1, TZ);
        var prev = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.Day, date, -1, TZ);
        Assert.Equal(date.AddDays(1), next);
        Assert.Equal(date.AddDays(-1), prev);
    }

    [Fact]
    public void Advance_Week_Adds_Seven_Days()
    {
        var date = On(2026, 3, 17);
        var next = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.Week, date, +1, TZ);
        var prev = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.Week, date, -1, TZ);
        Assert.Equal(date.AddDays(7), next);
        Assert.Equal(date.AddDays(-7), prev);
    }

    [Fact]
    public void Advance_Month_Adds_One_Month_With_TimeZone_Aware_Offset()
    {
        var date = On(2026, 3, 17);
        var next = SchedulerViewPrimitives.AdvanceAnchor(SchedulerView.Month, date, +1, TZ);
        Assert.Equal(new DateTime(2026, 4, 17), next.Date);
        // The new offset comes from TZ at the new date.
        Assert.Equal(TZ.GetUtcOffset(new DateTime(2026, 4, 17)), next.Offset);
    }

    [Fact]
    public void Advance_Timeline_Delegates_To_TimeScale()
    {
        var date = On(2026, 3, 17);
        var timelineDay = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Timeline, date, +1, TZ, TimelineScale.Day);
        Assert.Equal(date.AddDays(1), timelineDay);

        var timelineWeek = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Timeline, date, +1, TZ, TimelineScale.Week);
        Assert.Equal(date.AddDays(7), timelineWeek);

        var timelineMonth = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Timeline, date, -1, TZ, TimelineScale.Month);
        Assert.Equal(new DateTime(2026, 2, 17), timelineMonth.Date);
    }
}
