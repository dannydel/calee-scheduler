using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerYearView{TEvent}"/> covering FR-38, FR-23,
/// FR-30 (Year portion), NFR-06 (Year portion), and the locked design decisions in
/// phase-2-plan §5.3 Q13/Q14/Q15.
/// </summary>
public class CaleeSchedulerYearViewTests
{
    // Fixed time zone and anchor for determinism. Year: 2026. Mid-year anchor.
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 15, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    /// <summary>Build a midnight DateTimeOffset on the supplied date in -04:00 (EDT) offset.</summary>
    private static DateTimeOffset Edt(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));

    private static CalendarEvent Timed(
        string id, DateTimeOffset start, DateTimeOffset end, string? color = null) =>
        new(id, id, start, end, IsAllDay: false, Color: color);

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    // ────────────────────────────────────────────────────────────────────────
    // SchedulerView.Year enum + ShortcutMap / Commands pinning
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SchedulerView_Year_Enum_Value_Exists()
    {
        // Pinned in the contract — Task 16 widened the enum, and Task 18 will wire it
        // into the root scheduler's @switch. Until then this is the canonical signal
        // that Year is a first-class view mode.
        var values = Enum.GetNames(typeof(SchedulerView));
        Assert.Contains(nameof(SchedulerView.Year), values);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Render shape
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_Twelve_Month_Sub_Grids()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var months = cut.FindAll("[data-calee-region='year-month']");
        Assert.Equal(12, months.Count);

        // 12 months × 42 day cells = 504 cells.
        var cells = cut.FindAll("[data-calee-region='year-day-cell']");
        Assert.Equal(12 * 42, cells.Count);
    }

    [Fact]
    public void Renders_12_Month_Sub_Grids_For_A_Different_Year()
    {
        using var ctx = NewContext();
        // Leap year 2024.
        var anchor = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, anchor));

        Assert.Equal(12, cut.FindAll("[data-calee-region='year-month']").Count);

        // The header attribute on each month carries the 1..12 number.
        var months = cut.FindAll("[data-calee-region='year-month']");
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal((i + 1).ToString(), months[i].GetAttribute("data-calee-month-number"));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Layout variants (Q15)
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(YearViewLayout.Grid4x3, "calee-scheduler-year-layout--grid-4x3")]
    [InlineData(YearViewLayout.Grid3x4, "calee-scheduler-year-layout--grid-3x4")]
    [InlineData(YearViewLayout.Grid2x6, "calee-scheduler-year-layout--grid-2x6")]
    [InlineData(YearViewLayout.Grid6x2, "calee-scheduler-year-layout--grid-6x2")]
    [InlineData(YearViewLayout.Column, "calee-scheduler-year-layout--column")]
    public void Layout_Variant_Applies_Correct_Class(YearViewLayout layout, string expectedClass)
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Layout, layout));

        var root = cut.Find("[data-calee-region='year']");
        Assert.Contains(expectedClass, root.GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public void Default_Layout_Is_Grid4x3()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var root = cut.Find("[data-calee-region='year']");
        Assert.Contains("calee-scheduler-year-layout--grid-4x3", root.GetAttribute("class") ?? string.Empty);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Style variants (Q13)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MiniMonths_Style_Shows_Day_Numbers()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Style, YearViewStyle.MiniMonths));

        // Day-number text is present in every cell.
        var dateLabels = cut.FindAll(".calee-scheduler-year-cell-date");
        Assert.Equal(12 * 42, dateLabels.Count);

        // No heatmap fill spans rendered in mini mode.
        Assert.Empty(cut.FindAll(".calee-scheduler-year-cell-heatmap-fill"));
    }

    [Fact]
    public void Heatmap_Style_Shows_Colored_Squares_Only()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Style, YearViewStyle.Heatmap));

        // Heatmap fill spans present (one per cell).
        var fills = cut.FindAll(".calee-scheduler-year-cell-heatmap-fill");
        Assert.Equal(12 * 42, fills.Count);

        // The .calee-scheduler-year-cell-date glyph is NOT emitted in heatmap mode
        // (heatmap suppresses the per-cell day number — see razor template + CSS).
        Assert.Empty(cut.FindAll(".calee-scheduler-year-cell-date"));

        // Root carries the heatmap style class.
        var root = cut.Find("[data-calee-region='year']");
        Assert.Contains("calee-scheduler-year-style--heatmap", root.GetAttribute("class") ?? string.Empty);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Density computation
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Density_Single_Timed_Event_Counts_Exactly_One_Day()
    {
        using var ctx = NewContext();
        var ev = Timed("e1", Edt(2026, 5, 15, 9), Edt(2026, 5, 15, 10));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 15)));
        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2026, 5, 14)));
        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2026, 5, 16)));
    }

    [Fact]
    public void Density_AllDay_MultiDay_Event_Counts_On_Each_Date_In_Span()
    {
        using var ctx = NewContext();
        // All-day Wed May 13 → end-of-Fri May 15 (exclusive Sat May 16). Three days.
        var ev = AllDay("conf", Edt(2026, 5, 13), Edt(2026, 5, 16));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        // Each touched day carries a count of 1.
        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 13)));
        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 14)));
        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 15)));
        // The next-midnight exclusive end → not touched.
        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2026, 5, 16)));
        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2026, 5, 12)));
    }

    [Fact]
    public void Density_Timed_MultiDay_Event_Counts_On_Each_Date_It_Crosses()
    {
        using var ctx = NewContext();
        // Timed Wed May 13 8 AM → Fri May 15 10 AM. Crosses three calendar dates.
        var ev = Timed("training", Edt(2026, 5, 13, 8), Edt(2026, 5, 15, 10));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 13)));
        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 14)));
        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 15)));
    }

    [Fact]
    public void Density_EventFilter_Drops_Events_Before_Counting()
    {
        using var ctx = NewContext();
        var fri15 = Edt(2026, 5, 15);
        var keep = Timed("keep", fri15.AddHours(9), fri15.AddHours(10));
        var hide = Timed("hide", fri15.AddHours(11), fri15.AddHours(12));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { keep, hide })
            .Add(c => c.EventFilter, e => e.Id != "hide"));

        Assert.Equal(1, cut.Instance.DensityCount(new DateOnly(2026, 5, 15)));
    }

    [Fact]
    public void Density_Bucket_Boundaries_Match_Documented_Rule()
    {
        // Bucket map: 0→0; 1→1; 2..4→2; 5+→3 (per YearViewStyle XML doc).
        using var ctx = NewContext();

        // Stack five overlapping events on May 15. Each event counts as +1 for May 15.
        var fri15 = Edt(2026, 5, 15);
        var events = new[]
        {
            Timed("e1", fri15.AddHours(9), fri15.AddHours(10)),
            Timed("e2", fri15.AddHours(10), fri15.AddHours(11)),
            Timed("e3", fri15.AddHours(11), fri15.AddHours(12)),
            Timed("e4", fri15.AddHours(12), fri15.AddHours(13)),
            Timed("e5", fri15.AddHours(13), fri15.AddHours(14)),
        };

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        Assert.Equal(5, cut.Instance.DensityCount(new DateOnly(2026, 5, 15)));
        Assert.Equal(3, cut.Instance.DensityBucket(new DateOnly(2026, 5, 15)));

        // Verify the boundary day-by-day.
        Assert.Equal(0, cut.Instance.DensityBucket(new DateOnly(2026, 5, 14)));
    }

    [Fact]
    public void Density_Buckets_Use_Documented_Boundaries()
    {
        // Test edges of the bucket map directly via DensityBucket.
        using var ctx = NewContext();

        var fri15 = Edt(2026, 5, 15);
        // Two events on the 15th → bucket 2; one event on the 14th → bucket 1.
        var events = new[]
        {
            Timed("a", fri15.AddHours(9), fri15.AddHours(10)),
            Timed("b", fri15.AddHours(11), fri15.AddHours(12)),
            Timed("c", Edt(2026, 5, 14, 9), Edt(2026, 5, 14, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        Assert.Equal(1, cut.Instance.DensityBucket(new DateOnly(2026, 5, 14)));
        Assert.Equal(2, cut.Instance.DensityBucket(new DateOnly(2026, 5, 15)));
    }

    [Fact]
    public void Density_Skips_Events_Outside_Visible_Year()
    {
        using var ctx = NewContext();
        var beforeYear = Timed("before", Edt(2025, 12, 31, 9), Edt(2025, 12, 31, 10));
        var afterYear = Timed("after", Edt(2027, 1, 1, 9), Edt(2027, 1, 1, 10));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { beforeYear, afterYear }));

        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2025, 12, 31)));
        Assert.Equal(0, cut.Instance.DensityCount(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Density_Cell_Carries_Bucket_Attribute_For_The_Markup()
    {
        // A cell's density bucket is also surfaced via the data-calee-density attribute
        // for selector-friendly consumer CSS hooks.
        using var ctx = NewContext();
        var fri15 = Edt(2026, 5, 15);
        var ev = Timed("e1", fri15.AddHours(9), fri15.AddHours(10));

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var cell = cut.Find("[data-calee-date='2026-05-15']");
        Assert.Equal("1", cell.GetAttribute("data-calee-density"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Drill-down callbacks (Q14)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Cell_Click_Fires_OnSlotClicked_With_Correct_Bounds()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var cell = cut.Find("[data-calee-date='2026-05-15']");
        await cut.InvokeAsync(() => cell.Click());

        Assert.NotNull(captured);
        // Bounds: May 15 midnight EDT → May 16 midnight EDT, in the configured TimeZone.
        Assert.Equal(2026, captured!.Start.Year);
        Assert.Equal(5, captured.Start.Month);
        Assert.Equal(15, captured.Start.Day);
        Assert.Equal(0, captured.Start.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), captured.Start.Offset);
        Assert.Equal(captured.Start.AddDays(1), captured.End);
        // Year view has no lanes — LaneId stays null.
        Assert.Null(captured.LaneId);
    }

    [Fact]
    public async Task Month_Header_Click_Fires_OnMonthClicked_With_First_Of_Month()
    {
        using var ctx = NewContext();
        DateOnly? captured = null;

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnMonthClicked,
                EventCallback.Factory.Create<DateOnly>(this, d => captured = d)));

        // Click the May header (index 4 = May).
        var headers = cut.FindAll("[data-calee-region='year-month-header']");
        Assert.Equal(12, headers.Count);
        await cut.InvokeAsync(() => headers[4].Click());

        Assert.NotNull(captured);
        Assert.Equal(new DateOnly(2026, 5, 1), captured!.Value);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Toolbar prev/next stepping (year boundary unit)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AdvanceAnchor_Year_Moves_By_One_Year()
    {
        // The toolbar's Prev/Next chevrons route through SchedulerViewPrimitives.AdvanceAnchor.
        // Year view must step by exactly one calendar year per +/-1.
        var anchor = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.FromHours(-4));

        var next = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Year, anchor, +1, TZ);
        Assert.Equal(2027, next.Year);
        Assert.Equal(5, next.Month);
        Assert.Equal(15, next.Day);

        var prev = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Year, anchor, -1, TZ);
        Assert.Equal(2025, prev.Year);
    }

    [Fact]
    public void FormatRangeLabel_Year_Returns_Just_The_Year()
    {
        // The toolbar's center-region range label for Year is the bare year.
        var anchor = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.FromHours(-4));
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Year, anchor, TZ, DayOfWeek.Sunday);
        Assert.Equal("2026", label);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ARIA structure (ADR-0009 / NFR-06)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Aria_Grid_Roles_Present()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.NotNull(cut.Find("[role='grid']"));

        // Twelve role="rowheader" elements — one per month, on the clickable header.
        Assert.Equal(12, cut.FindAll("[role='rowheader']").Count);

        // 12 month rows × 42 day cells = 504 gridcells.
        Assert.Equal(12 * 42, cut.FindAll("[role='gridcell']").Count);

        // Weekday-header columnheaders: 12 months × 7 = 84.
        Assert.Equal(12 * 7, cut.FindAll("[role='columnheader']").Count);
    }

    [Fact]
    public void Roving_Tabindex_Exactly_One_Cell_Is_Tabbable()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var cells = cut.FindAll("[data-calee-region='year-day-cell']");
        var tabbable = cells.Count(c => c.GetAttribute("tabindex") == "0");
        Assert.Equal(1, tabbable);
    }

    [Fact]
    public void Cell_Aria_Selected_Reflects_Focus_State()
    {
        // aria-selected="true" on exactly the focused cell.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var cells = cut.FindAll("[data-calee-region='year-day-cell']");
        var selected = cells.Count(c => c.GetAttribute("aria-selected") == "true");
        Assert.Equal(1, selected);
    }

    [Fact]
    public void Today_Cell_Has_Aria_Current_Date()
    {
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz));

        var todayCells = cut.FindAll("[data-calee-region='year-day-cell'][aria-current='date']");
        // The displayed year contains exactly one cell whose date == "today in TZ" in
        // the displayed month's grid AND in the prev/next month's leading/trailing
        // grid blocks, BUT the in-month vs muted distinction does not affect aria-
        // current. We assert that at least one cell carries it (today exists once per
        // year in the in-month rendering, may appear additionally in adjacent muted
        // rows when "today" happens to fall on a leading/trailing edge).
        Assert.True(todayCells.Count >= 1, "Today cell should be present with aria-current='date'");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Keyboard navigation
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArrowRight_Moves_Focus_To_Next_Cell_Within_Month()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var (initialMonth, initialCell) = cut.Instance.FocusedCellForTest;

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeCellKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "ArrowRight" }, initialMonth, initialCell));

        var (newMonth, newCell) = cut.Instance.FocusedCellForTest;
        Assert.Equal(initialMonth, newMonth);
        Assert.Equal(initialCell + 1, newCell);
    }

    [Fact]
    public async Task ArrowDown_Moves_Focus_Down_A_Week_Within_Month()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var (initialMonth, initialCell) = cut.Instance.FocusedCellForTest;

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeCellKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "ArrowDown" }, initialMonth, initialCell));

        var (newMonth, newCell) = cut.Instance.FocusedCellForTest;
        Assert.Equal(initialMonth, newMonth);
        Assert.Equal(initialCell + 7, newCell);
    }

    [Fact]
    public async Task PageDown_Moves_Focus_To_Next_Month()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var (initialMonth, initialCell) = cut.Instance.FocusedCellForTest;
        Assert.Equal(0, initialMonth);

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeCellKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "PageDown" }, initialMonth, initialCell));

        var (newMonth, _) = cut.Instance.FocusedCellForTest;
        Assert.Equal(1, newMonth);
    }

    [Fact]
    public async Task Enter_On_Focused_Cell_Fires_OnSlotClicked()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var cut = ctx.Render<CaleeSchedulerYearView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var (initialMonth, initialCell) = cut.Instance.FocusedCellForTest;

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeCellKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "Enter" }, initialMonth, initialCell));

        Assert.NotNull(captured);
        Assert.Equal(captured!.Start.AddDays(1), captured.End);
    }
}
