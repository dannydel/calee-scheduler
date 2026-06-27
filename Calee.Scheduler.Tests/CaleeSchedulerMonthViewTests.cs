using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerMonthView{TEvent}"/> covering FR-03, FR-04, FR-16,
/// FR-17 (chip variant), FR-18, FR-19, FR-20, FR-21, FR-23, FR-30 (Month portion), and
/// PRD §4.6 parameter validation.
/// </summary>
public class CaleeSchedulerMonthViewTests
{
    // Fixed time zone and anchor for determinism. Anchor: 2026-05-15 EDT (mid-May).
    // May 2026 starts on Friday; Sunday-anchored grid begins Sunday April 26 2026.
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

    [Fact]
    public void Renders_42_Day_Cells()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        Assert.Equal(42, cells.Count);
    }

    [Fact]
    public void First_Day_Of_Week_From_Parameter_Honored()
    {
        // Anchor May 2026, FirstDayOfWeek=Monday. May 1 is a Friday, so the grid begins
        // Monday April 27 2026.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday));

        // Header row labels — first should be "Mon".
        var headers = cut.FindAll("[role='columnheader']");
        Assert.Equal(7, headers.Count);
        Assert.Equal("Mon", headers[0].TextContent.Trim());

        // First cell's date number should be 27 (Apr 27).
        var cells = cut.FindAll("[data-calee-region='month-cell']");
        var firstCellDate = cells[0].QuerySelector(".calee-scheduler-month-cell-date");
        Assert.NotNull(firstCellDate);
        Assert.Equal("27", firstCellDate!.TextContent.Trim());
    }

    [Fact]
    public void First_Day_Of_Week_Defaults_From_Options()
    {
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultFirstDayOfWeek = DayOfWeek.Monday);

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var headers = cut.FindAll("[role='columnheader']");
        Assert.Equal("Mon", headers[0].TextContent.Trim());
        var cells = cut.FindAll("[data-calee-region='month-cell']");
        var firstCellDate = cells[0].QuerySelector(".calee-scheduler-month-cell-date");
        Assert.Equal("27", firstCellDate!.TextContent.Trim());
    }

    [Fact]
    public void Today_Cell_Highlighted_With_AriaCurrent()
    {
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz));

        var todayCells = cut.FindAll("[data-calee-region='month-cell'][aria-current='date']");
        Assert.Single(todayCells);
        Assert.Contains("calee-scheduler-month-cell--today", todayCells[0].GetAttribute("class") ?? "");
    }

    [Fact]
    public void Cells_Outside_Month_Are_Muted()
    {
        // Anchor May 2026, FirstDayOfWeek=Sunday → first cell = Apr 26 (Sunday), which is
        // outside the displayed month and must carry the muted class.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        // First cell — Apr 26, outside May.
        Assert.Contains("calee-scheduler-month-cell--muted", cells[0].GetAttribute("class") ?? "");
        // A May-cell (e.g., index 5 = Fri May 1) must NOT be muted.
        Assert.DoesNotContain("calee-scheduler-month-cell--muted", cells[5].GetAttribute("class") ?? "");
    }

    [Fact]
    public void Single_Day_Event_Renders_As_Chip_In_Correct_Cell()
    {
        using var ctx = NewContext();

        // Anchor May 15. With Sunday start, May 15 = row 2 column 5 (Fri) → index 19.
        var fri15 = Edt(2026, 5, 15);
        var ev = Timed("standup", fri15.AddHours(9), fri15.AddHours(10));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        var chipsInTarget = cells[19].QuerySelectorAll(".calee-scheduler-month-chip");
        Assert.Single(chipsInTarget);

        // No chips in adjacent cells.
        Assert.Empty(cells[18].QuerySelectorAll(".calee-scheduler-month-chip"));
        Assert.Empty(cells[20].QuerySelectorAll(".calee-scheduler-month-chip"));
    }

    [Fact]
    public void Multi_Day_Event_Renders_As_Continuous_Bar_Across_Cells()
    {
        using var ctx = NewContext();

        // Anchor May 15. All-day Wed May 13 → end-of-Fri May 15 (exclusive Sat May 16).
        // Wed/Thu/Fri are all on row 2.
        var ev = AllDay("conf", Edt(2026, 5, 13), Edt(2026, 5, 16));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        // One bar overall, spanning 3 columns on row 2.
        var bars = cut.FindAll(".calee-scheduler-month-bar");
        Assert.Single(bars);
        var style = bars[0].GetAttribute("style") ?? "";
        // 3 columns = 3/7 ≈ 42.8571%, left at col 3 (Wed) = 3/7 ≈ 42.8571%.
        Assert.Contains("width: 42.8571", style);
        Assert.Contains("left: 42.8571", style);
        Assert.DoesNotContain("clip-left", bars[0].GetAttribute("class") ?? "");
        Assert.DoesNotContain("clip-right", bars[0].GetAttribute("class") ?? "");
    }

    [Fact]
    public void Multi_Day_Event_Crossing_Week_Boundary_Renders_Separate_Bars_Per_Week_Row()
    {
        using var ctx = NewContext();

        // Anchor May 15, Sunday start. Event: Thu May 14 → end-of-Tue May 19
        // (exclusive Wed May 20). Spans Thu/Fri/Sat on row 2 and Sun/Mon/Tue on row 3.
        var ev = AllDay("trip", Edt(2026, 5, 14), Edt(2026, 5, 20));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var bars = cut.FindAll(".calee-scheduler-month-bar");
        Assert.Equal(2, bars.Count);

        // The bars live in their respective rows; the first found is row 2 (Thu/Fri/Sat),
        // the second is row 3 (Sun/Mon/Tue). Both span 3 columns.
        foreach (var b in bars)
        {
            var style = b.GetAttribute("style") ?? "";
            Assert.Contains("width: 42.8571", style);
        }

        // Row 2 segment starts at col 4 (Thu) = 4/7 ≈ 57.1429%, has clip-right
        // (continuing to next row).
        var row2Bar = bars.First(b => (b.GetAttribute("style") ?? "").Contains("left: 57.142"));
        Assert.Contains("calee-scheduler-month-bar--clip-right", row2Bar.GetAttribute("class") ?? "");
        Assert.DoesNotContain("calee-scheduler-month-bar--clip-left", row2Bar.GetAttribute("class") ?? "");

        // Row 3 segment starts at col 0 (Sun) = 0%, has clip-left (continuing from prev row).
        var row3Bar = bars.First(b => (b.GetAttribute("style") ?? "").Contains("left: 0.0000"));
        Assert.Contains("calee-scheduler-month-bar--clip-left", row3Bar.GetAttribute("class") ?? "");
        Assert.DoesNotContain("calee-scheduler-month-bar--clip-right", row3Bar.GetAttribute("class") ?? "");
    }

    [Fact]
    public void Multi_Day_Event_Extending_Past_Grid_Has_Clip_Indicator()
    {
        using var ctx = NewContext();

        // Anchor May 15. Grid starts Apr 26 (Sunday). An event that started before Apr 26
        // (e.g. Apr 20 → Apr 28) should show a left clip on the first row.
        var ev = AllDay("long-trip", Edt(2026, 4, 20), Edt(2026, 4, 29));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var bars = cut.FindAll(".calee-scheduler-month-bar");
        Assert.NotEmpty(bars);
        // The first row's bar should carry clip-left (event extends past grid start).
        var firstRowBar = bars[0];
        Assert.Contains("calee-scheduler-month-bar--clip-left", firstRowBar.GetAttribute("class") ?? "");
    }

    [Fact]
    public void MaxEventsPerDay_Limits_Cell_To_3_With_Plus_N_More()
    {
        using var ctx = NewContext();

        // 5 single-day events on Fri May 15 with MaxEventsPerDay=3 → 3 chips + "+2 more".
        var fri15 = Edt(2026, 5, 15);
        var evs = new[]
        {
            Timed("e1", fri15.AddHours(8), fri15.AddHours(9)),
            Timed("e2", fri15.AddHours(10), fri15.AddHours(11)),
            Timed("e3", fri15.AddHours(12), fri15.AddHours(13)),
            Timed("e4", fri15.AddHours(14), fri15.AddHours(15)),
            Timed("e5", fri15.AddHours(16), fri15.AddHours(17)),
        };

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, evs)
            .Add(c => c.MaxEventsPerDay, 3));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        var targetCell = cells[19]; // Fri May 15.
        var chips = targetCell.QuerySelectorAll(".calee-scheduler-month-chip");
        Assert.Equal(3, chips.Length);

        var overflow = targetCell.QuerySelectorAll("[data-calee-region='overflow-chip']");
        Assert.Single(overflow);
        Assert.Contains("+2 more", overflow[0].TextContent);
    }

    [Fact]
    public async Task Plus_N_More_Click_Fires_OnDayOverflowClicked_With_Month_Kind()
    {
        using var ctx = NewContext();
        DayOverflowContext<CalendarEvent>? captured = null;

        var fri15 = Edt(2026, 5, 15);
        var evs = new[]
        {
            Timed("e1", fri15.AddHours(8), fri15.AddHours(9)),
            Timed("e2", fri15.AddHours(10), fri15.AddHours(11)),
            Timed("e3", fri15.AddHours(12), fri15.AddHours(13)),
            Timed("e4", fri15.AddHours(14), fri15.AddHours(15)),
            Timed("e5", fri15.AddHours(16), fri15.AddHours(17)),
        };

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, evs)
            .Add(c => c.MaxEventsPerDay, 3)
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, d => captured = d)));

        var overflow = cut.Find("[data-calee-region='overflow-chip']");
        await cut.InvokeAsync(() => overflow.Click());

        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Month, captured!.Kind);
        Assert.Equal(new DateOnly(2026, 5, 15), captured.Date);
    }

    [Fact]
    public async Task Chip_Click_Fires_OnEventClicked_With_Original_Event_And_Stops_Slot_Propagation()
    {
        using var ctx = NewContext();
        CalendarEvent? capturedEvent = null;
        SchedulerSlot? capturedSlot = null;

        var fri15 = Edt(2026, 5, 15);
        var ev = Timed("standup", fri15.AddHours(9), fri15.AddHours(10));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => capturedEvent = e))
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => capturedSlot = s)));

        var chip = cut.Find(".calee-scheduler-month-chip");
        await cut.InvokeAsync(() => chip.Click());

        Assert.NotNull(capturedEvent);
        Assert.Same(ev, capturedEvent);
        Assert.Null(capturedSlot);
    }

    [Fact]
    public async Task Slot_Click_On_Empty_Cell_Fires_OnSlotClicked_With_Day_Bounds()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Click Fri May 15 cell (index 19).
        var cells = cut.FindAll("[data-calee-region='month-cell']");
        await cut.InvokeAsync(() => cells[19].Click());

        Assert.NotNull(captured);
        // Day bounds: midnight May 15 to midnight May 16 in the configured TZ.
        Assert.Equal(15, captured!.Start.Day);
        Assert.Equal(0, captured.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
        Assert.Equal(16, captured.End.Day);
        Assert.Equal(0, captured.End.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), captured.Start.Offset);
    }

    [Fact]
    public void EventFilter_Excludes_Matching_Events()
    {
        using var ctx = NewContext();

        var fri15 = Edt(2026, 5, 15);
        var a = Timed("keep", fri15.AddHours(9), fri15.AddHours(10));
        var b = Timed("hide", fri15.AddHours(11), fri15.AddHours(12));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.EventFilter, e => e.Id != "hide"));

        var chips = cut.FindAll(".calee-scheduler-month-chip");
        Assert.Single(chips);
        Assert.Contains("keep", chips[0].TextContent);
    }

    [Fact]
    public void EventChipTemplate_Honored_For_Single_Day_Chip()
    {
        using var ctx = NewContext();

        var fri15 = Edt(2026, 5, 15);
        var ev = Timed("standup", fri15.AddHours(9), fri15.AddHours(10));

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.EventChipTemplate,
                (RenderFragment<CalendarEvent>)(e =>
                    builder => builder.AddMarkupContent(0, $"<span class='custom-chip-content'>{e.Title}</span>"))));

        var custom = cut.Find(".custom-chip-content");
        Assert.Equal("standup", custom.TextContent);
        // The default dot/title should NOT appear because the template replaced the chip's content.
        Assert.Empty(cut.FindAll(".calee-scheduler-month-chip-dot"));
    }

    [Fact]
    public void MaxEventsPerDay_LessThan_1_Throws_ArgumentException()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<ArgumentException>(() =>
            ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.MaxEventsPerDay, 0)));
        Assert.Contains("MaxEventsPerDay", ex.Message);
    }

    [Fact]
    public void Aria_Grid_Roles_Present()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.NotNull(cut.Find("[role='grid']"));
        Assert.Equal(7, cut.FindAll("[role='columnheader']").Count);

        // 1 header row + 6 week rows = 7 role="row" elements.
        Assert.Equal(7, cut.FindAll("[role='row']").Count);

        // 42 day cells.
        Assert.Equal(42, cut.FindAll("[role='gridcell']").Count);
    }

    [Fact]
    public async Task Cross_Cell_Keyboard_Nav_Right_Arrow_Moves_Focus_To_Next_Cell()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Initial focused cell is index 0 (Sunday April 26). Right arrow → index 1 (April 27).
        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        Assert.Equal("0", cells[1].GetAttribute("tabindex"));
        Assert.Equal("-1", cells[0].GetAttribute("tabindex"));

        // Enter on the focused cell fires OnSlotClicked with Apr 27 midnight bounds.
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(captured);
        Assert.Equal(27, captured!.Start.Day);
        Assert.Equal(4, captured.Start.Month);
    }

    [Fact]
    public async Task Down_Arrow_At_Bottom_Edge_Does_Not_Crash()
    {
        // Behavior chosen: Down-arrow at the bottom edge no-ops (clamps to the bottom row).
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");

        // Press Down 10 times to land deep into the bottom row.
        for (var i = 0; i < 10; i++)
        {
            await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        }

        // One more Down — must not throw.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        // The currently-tabbable cell should be in the last row (indices 35..41).
        var cells = cut.FindAll("[data-calee-region='month-cell']").ToList();
        var tabbable = cells.Single(c => c.GetAttribute("tabindex") == "0");
        var tabbableIndex = cells.IndexOf(tabbable);
        Assert.InRange(tabbableIndex, 35, 41);
    }

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -------------------------

    [Fact]
    public async Task DoubleClickToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed: a double-click on a cell does not fire OnEventCreated.
        // bUnit raises MissingEventHandlerException on the dispatch when the cell
        // has no ondblclick binding — that's the proof.
        using var ctx = NewContext();

        var fired = false;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        var cell = cut.FindAll("[data-calee-region='month-cell']")[19]; // Fri May 15.
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => cell.DoubleClick()));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(19));
        Assert.False(fired);
    }

    [Fact]
    public async Task DoubleClickToCreate_Enabled_OnEmptyCell_FiresOnEventCreated_WithDefaultDuration()
    {
        // Anchor May 2026 Sunday-start. Cell 19 = Fri May 15. Default duration null
        // resolves to 1440 minutes (one day) on Month → Start = May 15 00:00, End = May 16 00:00.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var cell = cut.FindAll("[data-calee-region='month-cell']")[19];
        await cut.InvokeAsync(() => cell.DoubleClick());

        Assert.NotNull(captured);
        Assert.Equal(15, captured!.Slot.Start.Day);
        Assert.Equal(0, captured.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(16, captured.Slot.End.Day);
        Assert.Equal(0, captured.Slot.End.Hour);
        // All-day-shape contract: Start.TimeOfDay == 0 and End == Start + 1 day.
        Assert.Equal(TimeSpan.Zero, captured.Slot.Start.TimeOfDay);
        Assert.Equal(captured.Slot.Start.AddDays(1), captured.Slot.End);
        Assert.Null(captured.Slot.LaneId);
    }

    [Fact]
    public async Task DoubleClickToCreate_Disabled_OnExistingEventChip()
    {
        // Double-clicking a chip must NOT bubble up to the cell. The chip carries
        // @ondblclick:stopPropagation="true" but no @ondblclick handler. Two
        // observable facts together prove the propagation discipline:
        //   (1) bUnit raises MissingEventHandlerException on the dispatch — the
        //       chip's handler list includes "ondblclick:stoppropagation" but not
        //       "ondblclick", proving the stopPropagation directive is bound.
        //   (2) The cell-level OnEventCreated callback never fires.
        using var ctx = NewContext();

        var fri15 = Edt(2026, 5, 15);
        var ev = Timed("standup", fri15.AddHours(9), fri15.AddHours(10));
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find(".calee-scheduler-month-chip");
        var ex = await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => chip.DoubleClick()));
        Assert.Contains("ondblclick:stoppropagation", ex.Message);

        Assert.False(createFired);
    }

    [Fact]
    public async Task DoubleClickToCreate_Disabled_OnOverflowChip()
    {
        // The "+N more" overflow chip carries the same propagation discipline as the
        // regular chip — @ondblclick:stopPropagation="true" with no @ondblclick
        // handler. Asserted in the same shape as the chip test.
        using var ctx = NewContext();

        // Create enough events on Fri May 15 to overflow with MaxEventsPerDay=1.
        var fri15 = Edt(2026, 5, 15);
        var ev1 = Timed("a", fri15.AddHours(9), fri15.AddHours(10));
        var ev2 = Timed("b", fri15.AddHours(11), fri15.AddHours(12));
        var ev3 = Timed("c", fri15.AddHours(13), fri15.AddHours(14));
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.MaxEventsPerDay, 1)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { ev1, ev2, ev3 })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var overflowChip = cut.Find(".calee-scheduler-month-overflow-chip");
        var ex = await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => overflowChip.DoubleClick()));
        Assert.Contains("ondblclick:stoppropagation", ex.Message);

        Assert.False(createFired);
    }

    [Fact]
    public async Task DoubleClickToCreate_RespectsExplicit_DefaultCreateDurationMinutes()
    {
        // Explicit option = 60 minutes overrides the per-view 1440 default. The
        // proposed event is then a 60-minute window anchored at the clicked cell's
        // midnight (Start = May 15 00:00, End = May 15 01:00).
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultCreateDurationMinutes = 60);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var cell = cut.FindAll("[data-calee-region='month-cell']")[19];
        await cut.InvokeAsync(() => cell.DoubleClick());

        Assert.NotNull(captured);
        Assert.Equal(15, captured!.Slot.Start.Day);
        Assert.Equal(0, captured.Slot.Start.Hour);
        Assert.Equal(15, captured.Slot.End.Day);
        Assert.Equal(1, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DoubleClickToCreate_NullOption_FallsBackTo_OneDay()
    {
        // Month view is a whole-day-cell view → null option resolves to 1440 minutes.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(19));

        Assert.NotNull(captured);
        Assert.Equal(captured!.Slot.Start.AddDays(1), captured.Slot.End);
    }

    [Fact]
    public async Task DoubleClickToCreate_ClickedDateMatchesClickedCell()
    {
        // Verify the date routing: cell index → clicked-cell's midnight Start.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Cell 0 = Sunday April 26 2026 (Sunday-start, May 1 was a Friday).
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0));

        Assert.NotNull(captured);
        Assert.Equal(2026, captured!.Slot.Start.Year);
        Assert.Equal(4, captured.Slot.Start.Month);
        Assert.Equal(26, captured.Slot.Start.Day);
        Assert.Equal(TimeSpan.FromHours(-4), captured.Slot.Start.Offset);
    }
}
