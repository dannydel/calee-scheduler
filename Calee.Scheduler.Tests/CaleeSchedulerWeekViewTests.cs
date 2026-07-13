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
/// Tests for <see cref="CaleeSchedulerWeekView{TEvent}"/> covering FR-02, FR-04, FR-05,
/// FR-06, FR-07, FR-12, FR-13, FR-14, FR-15, FR-19, FR-19a, FR-19b, FR-20, FR-21, FR-23,
/// FR-30 (Week portion), and PRD §4.6 parameter validation.
/// </summary>
public class CaleeSchedulerWeekViewTests
{
    // Fixed time zone and anchor for determinism. Anchor: Tuesday, 2026-05-19 EDT.
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static CalendarEvent Timed(
        string id, DateTimeOffset start, DateTimeOffset end, string? color = null) =>
        new(id, id, start, end, IsAllDay: false, Color: color);

    private static CalendarEvent Timed(
        string id,
        DateTimeOffset day,
        int startHour, int endHour,
        int startMin = 0, int endMin = 0,
        string? color = null)
    {
        var s = new DateTimeOffset(day.Year, day.Month, day.Day, startHour, startMin, 0, day.Offset);
        var e = new DateTimeOffset(day.Year, day.Month, day.Day, endHour, endMin, 0, day.Offset);
        return new CalendarEvent(id, id, s, e, IsAllDay: false, Color: color);
    }

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    /// <summary>Build a midnight DateTimeOffset on the supplied date in -04:00 (EDT) offset.</summary>
    private static DateTimeOffset Edt(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Renders_Seven_Day_Headers()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(7, headers.Count);
    }

    [Fact]
    public void First_Day_Of_Week_Honored_From_Parameter()
    {
        // Anchor is Tuesday 2026-05-19. With FirstDayOfWeek=Monday, the week starts on
        // Monday 2026-05-18.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(7, headers.Count);
        // First column's date should be 18 (Monday May 18).
        var firstDateCell = headers[0].QuerySelector(".calee-scheduler-day-header-date");
        Assert.NotNull(firstDateCell);
        Assert.Equal("18", firstDateCell!.TextContent.Trim());
    }

    [Fact]
    public void First_Day_Of_Week_Defaults_From_Options()
    {
        using var ctx = NewContext();
        // Override the registered options to default to Monday.
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultFirstDayOfWeek = DayOfWeek.Monday);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        var firstDateCell = headers[0].QuerySelector(".calee-scheduler-day-header-date");
        Assert.NotNull(firstDateCell);
        Assert.Equal("18", firstDateCell!.TextContent.Trim());
    }

    [Fact]
    public void Today_Column_Has_Today_Highlight_And_AriaCurrent()
    {
        // Anchor the view to "today in TZ" so one column is today.
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        var todayHeaders = headers.Where(h => h.GetAttribute("aria-current") == "date").ToList();
        Assert.Single(todayHeaders);
        Assert.Contains("calee-scheduler-day-header--today", todayHeaders[0].GetAttribute("class") ?? "");
    }

    [Fact]
    public void Current_Time_Indicator_Renders_Only_On_Today_Column()
    {
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz)
            // Widen the hour range so the indicator's vertical position lands inside it
            // regardless of when in the day the test runs.
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24));

        var indicators = cut.FindAll(".calee-scheduler-current-time-indicator");
        Assert.Single(indicators);
    }

    [Fact]
    public void AllDay_Single_Day_Renders_In_One_Column()
    {
        using var ctx = NewContext();

        // Single-day all-day on Wednesday (2026-05-20).
        var wed = Edt(2026, 5, 20);
        var allDay = AllDay("party", wed, wed.AddDays(1));

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { allDay }));

        var allDayRow = cut.Find("[data-calee-region='all-day']");
        var bars = allDayRow.QuerySelectorAll(".calee-scheduler-all-day-event");
        Assert.Single(bars);
        // Bar should span 1/7 of the all-day-grid width.
        var style = bars[0].GetAttribute("style") ?? "";
        Assert.Contains("width: 14.2857", style); // 1/7 = ~14.2857%
    }

    [Fact]
    public void AllDay_Multi_Day_Renders_As_Continuous_Bar_Spanning_Three_Columns()
    {
        using var ctx = NewContext();

        // Anchor: Tuesday 2026-05-19. Default first-day-of-week = Sunday → visible week
        // is Sun 17 … Sat 23.
        // Vacation: Wed 20 to end-of-Fri 22 (exclusive Sat 23 midnight). Spans 3 columns
        // (Wed, Thu, Fri).
        var wed = Edt(2026, 5, 20);
        var satMidnight = Edt(2026, 5, 23);
        var vacation = AllDay("vacation", wed, satMidnight);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { vacation }));

        var bars = cut.FindAll(".calee-scheduler-all-day-event");
        Assert.Single(bars);
        var style = bars[0].GetAttribute("style") ?? "";
        // 3 columns = 3/7 ≈ 42.8571%
        Assert.Contains("width: 42.8571", style);
        // First col is index 3 (Wed) → left = 3/7 ≈ 42.8571%.
        Assert.Contains("left: 42.8571", style);
        // No clip indicators expected — event is fully contained in the visible week.
        Assert.DoesNotContain("clip-left", bars[0].GetAttribute("class") ?? "");
        Assert.DoesNotContain("clip-right", bars[0].GetAttribute("class") ?? "");
    }

    [Fact]
    public void EventClass_Applied_To_AllDay_Bar()
    {
        using var ctx = NewContext();

        // Lock in that the per-event EventClass hook (FR-54) flows onto the all-day bar's
        // outer button, alongside any consumer-supplied class via AdditionalAttributes.
        var wed = Edt(2026, 5, 20);
        var thu = Edt(2026, 5, 21);
        var vacation = AllDay("vacation", wed, thu);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { vacation })
            .Add(c => c.EventClass, _ => "consumer-event-class"));

        var bars = cut.FindAll(".calee-scheduler-all-day-event");
        Assert.Single(bars);
        Assert.Contains("consumer-event-class", bars[0].GetAttribute("class") ?? "");
    }

    [Fact]
    public void AllDay_Multi_Day_Extending_Past_Week_Has_Right_Edge_Clip()
    {
        using var ctx = NewContext();

        // Visible week: Sun 17 … Sat 23. Event: Thu 21 → next Tue 26 (exclusive Wed 27).
        var thu = Edt(2026, 5, 21);
        var nextWedMidnight = Edt(2026, 5, 27);
        var trip = AllDay("trip", thu, nextWedMidnight);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { trip }));

        var bars = cut.FindAll(".calee-scheduler-all-day-event");
        Assert.Single(bars);
        // Should span 3 columns (Thu, Fri, Sat) with right-edge clip.
        var classes = bars[0].GetAttribute("class") ?? "";
        Assert.Contains("calee-scheduler-all-day-event--clip-right", classes);
        Assert.DoesNotContain("calee-scheduler-all-day-event--clip-left", classes);
        // 3 columns = 3/7 ≈ 42.8571%.
        var style = bars[0].GetAttribute("style") ?? "";
        Assert.Contains("width: 42.8571", style);
    }

    [Fact]
    public void Timed_Multi_Day_Event_Splits_Per_Day_With_Clip_Indicators()
    {
        using var ctx = NewContext();

        // Anchor: Tuesday May 19. Visible week with Sunday start = Sun 17 … Sat 23.
        // Event: Mon May 18 23:00 → Wed May 20 02:00.
        var monday11pm = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.FromHours(-4));
        var wed2am = new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("overnight", monday11pm, wed2am);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // Widen hours so the 11pm and 0–2am chunks are inside the visible band.
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.Events, new[] { ev }));

        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Equal(7, columns.Length);

        // Monday is column index 1 (Sunday is 0).
        var monEvents = columns[1].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(monEvents);
        Assert.Contains("calee-scheduler-event--clip-bottom", monEvents[0].GetAttribute("class") ?? "");
        Assert.DoesNotContain("calee-scheduler-event--clip-top", monEvents[0].GetAttribute("class") ?? "");

        // Tuesday is column index 2 — full-day chunk with top AND bottom clip.
        var tueEvents = columns[2].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(tueEvents);
        var tueClasses = tueEvents[0].GetAttribute("class") ?? "";
        Assert.Contains("calee-scheduler-event--clip-top", tueClasses);
        Assert.Contains("calee-scheduler-event--clip-bottom", tueClasses);

        // Wednesday is column index 3 — top clip only.
        var wedEvents = columns[3].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(wedEvents);
        var wedClasses = wedEvents[0].GetAttribute("class") ?? "";
        Assert.Contains("calee-scheduler-event--clip-top", wedClasses);
        Assert.DoesNotContain("calee-scheduler-event--clip-bottom", wedClasses);
    }

    [Fact]
    public void Per_Day_Earlier_Chip_Appears_On_Correct_Column()
    {
        using var ctx = NewContext();

        // Tuesday May 19, 6 AM event. StartHour=8 → before visible band → goes to earlier-chip.
        var ev = Timed("early", Anchor, 6, 7);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        // The earlier-row exists, and the chip appears in the Tuesday column (index 2).
        var overflowRow = cut.FindAll(".calee-scheduler-week-overflow-row");
        Assert.NotEmpty(overflowRow);
        var chips = cut.FindAll("[data-calee-region='overflow-chip']");
        Assert.Single(chips);
        Assert.Contains("+1 earlier", chips[0].TextContent);

        // Verify the chip is in the Tuesday cell — the third cell in the overflow row.
        var cells = overflowRow[0].QuerySelectorAll(".calee-scheduler-week-overflow-cell");
        Assert.Equal(7, cells.Length);
        Assert.NotEmpty(cells[2].QuerySelectorAll("[data-calee-region='overflow-chip']"));
        // Other cells must not contain a chip.
        for (var i = 0; i < cells.Length; i++)
        {
            if (i == 2) continue;
            Assert.Empty(cells[i].QuerySelectorAll("[data-calee-region='overflow-chip']"));
        }
    }

    [Fact]
    public async Task WeekView_EarlierChip_FiresContextWithPopulatedEvents()
    {
        using var ctx = NewContext();
        DayOverflowContext<CalendarEvent>? captured = null;

        // Tuesday May 19, 6 AM event. StartHour=8 → before visible band → goes to earlier-chip.
        var ev = Timed("early", Anchor, 6, 7);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var chip = cut.Find("[data-calee-region='overflow-chip']");
        Assert.Contains("+1 earlier", chip.TextContent);

        await cut.InvokeAsync(() => chip.Click());

        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Earlier, captured!.Kind);
        Assert.NotEmpty(captured.Events);
        Assert.Same(ev, captured.Events[0]);
    }

    [Fact]
    public async Task Event_Click_Fires_OnEventClicked_With_Original_Event_Not_Synthetic_Chunk()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;

        // A multi-day event so a chunk is created.
        var monday11pm = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.FromHours(-4));
        var wed2am = new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("overnight", monday11pm, wed2am);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        // Click the Tuesday-column chunk (column index 2 with Sunday start).
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        var tueChunk = columns[2].QuerySelector(".calee-scheduler-event");
        Assert.NotNull(tueChunk);
        await cut.InvokeAsync(() => tueChunk!.Click());

        Assert.NotNull(captured);
        // The captured event must be the consumer's original record reference.
        Assert.Same(ev, captured);
    }

    [Fact]
    public async Task Slot_Click_Fires_OnSlotClicked_With_Correct_Day()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Visible week: Sun 17 … Sat 23. Thursday is column index 4 (Sun=0, Mon=1, Tue=2, Wed=3, Thu=4).
        // Click the first slot (8:00 AM) on Thursday.
        // Slot rows are organized as: each role="row" contains 7 gridcells. First row,
        // index-4 cell within it = first slot on Thursday.
        var rows = cut.FindAll("[role='row']");
        // The first "row" is the header row; the all-day row is NOT a row. So we filter
        // to the rows inside the hour grid.
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var slotRows = hourGrid.QuerySelectorAll(".calee-scheduler-slot-row");
        Assert.NotEmpty(slotRows);
        var thuCell = slotRows[0].QuerySelectorAll("[role='gridcell']")[4];
        await cut.InvokeAsync(() => thuCell.Click());

        Assert.NotNull(captured);
        Assert.Equal(8, captured!.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
        Assert.Equal(8, captured.End.Hour);
        Assert.Equal(30, captured.End.Minute);
        Assert.Equal(21, captured.Start.Day); // Thursday May 21 2026.
        Assert.Equal(TimeSpan.FromHours(-4), captured.Start.Offset);
    }

    [Fact]
    public async Task Occupied_Slots_Are_Not_Interactive_Targets()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var busy = Timed("busy", Edt(2026, 5, 17), 8, 9);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 10)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.Events, new[] { busy })
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var slotRows = cut.Find("[data-calee-region='hour-grid']")
            .QuerySelectorAll(".calee-scheduler-slot-row");
        var occupied = slotRows[0].QuerySelectorAll(".calee-scheduler-slot")[0];

        Assert.Contains("calee-scheduler-slot--occupied", occupied.ClassList);
        Assert.DoesNotContain("calee-scheduler-slot--create-affordance", occupied.ClassList);
        Assert.False(occupied.HasAttribute("tabindex"));
        Assert.False(occupied.HasAttribute("aria-label"));
        Assert.Equal("true", occupied.GetAttribute("aria-disabled"));

        Assert.Throws<MissingEventHandlerException>(() => occupied.Click());
        await cut.InvokeAsync(() => cut.Instance.HandleSlotClickAsync(0, 0));
        Assert.Null(captured);

        var tabbable = slotRows[2].QuerySelectorAll(".calee-scheduler-slot")[0];
        Assert.Equal("0", tabbable.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task Keyboard_Navigation_Skips_Occupied_Slots()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var busy = Timed("busy", Edt(2026, 5, 17), 8, 9, startMin: 30);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 10)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.Events, new[] { busy })
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        var slotRows = cut.Find("[data-calee-region='hour-grid']")
            .QuerySelectorAll(".calee-scheduler-slot-row");
        var tabbable = slotRows[2].QuerySelectorAll(".calee-scheduler-slot")[0];
        Assert.Equal("0", tabbable.GetAttribute("tabindex"));

        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(captured);
        Assert.Equal(9, captured!.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
    }

    [Fact]
    public void EventFilter_Applies_Across_All_Days()
    {
        using var ctx = NewContext();

        var a = Timed("a", Anchor, 9, 10);
        var b = Timed("b", Edt(2026, 5, 20), 10, 11);
        var c = Timed("c", Edt(2026, 5, 21), 11, 12);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.EventFilter, e => e.Id != "b"));

        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var events = hourGrid.QuerySelectorAll(".calee-scheduler-event");
        Assert.Equal(2, events.Length);
        Assert.DoesNotContain(events, e => e.TextContent.Contains("b"));
    }

    [Fact]
    public void Aria_Grid_Role_Present()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.NotNull(cut.Find("[role='grid']"));
        // Columnheaders: 7. Rows: at least the header row + slot rows.
        Assert.Equal(7, cut.FindAll("[data-calee-region='day-header']").Count);
        Assert.NotEmpty(cut.FindAll("[role='row']"));
        Assert.NotEmpty(cut.FindAll("[role='gridcell']"));
    }

    [Fact]
    public async Task Cross_Column_Keyboard_Nav_Right_Arrow_Moves_Focus_To_Next_Day()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Initial focused cell is (col=0, row=0). Sunday May 17.
        var grid = cut.Find("[role='grid']");

        // Right arrow → col=1 (Monday). Down arrow → row=1.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        // Verify the tabbable cell moved. The tabbable cell has tabindex="0".
        // It should be the second cell of the second slot row.
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var slotRows = hourGrid.QuerySelectorAll(".calee-scheduler-slot-row");
        var tabbable = slotRows[1].QuerySelectorAll("[role='gridcell']")[1];
        Assert.Equal("0", tabbable.GetAttribute("tabindex"));

        // Enter on the focused cell should fire OnSlotClicked with Mon May 18 at 8:30.
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(captured);
        Assert.Equal(18, captured!.Start.Day);   // Monday May 18.
        Assert.Equal(8, captured.Start.Hour);
        Assert.Equal(30, captured.Start.Minute);
    }

    [Fact]
    public void Invalid_StartHour_GreaterThan_EndHour_Throws()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<ArgumentException>(() =>
            ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.StartHour, 18)
                .Add(c => c.EndHour, 8)));
        Assert.Contains("StartHour", ex.Message);
    }

    // ----- Drag-to-move (Phase 2 Task 5 — FR-25) ----------------------------------

    [Fact]
    public void WeekDragToMove_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDragToMove must be false and
        // the rendered event chip must NOT carry the drag affordances.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Null(chip.GetAttribute("data-calee-drag-handle"));
        Assert.Null(chip.GetAttribute("aria-roledescription"));
    }

    [Fact]
    public void WeekDragToMove_Enabled_AttachesDragAffordances()
    {
        // When AllowDragToMove=true every visible chip gains the drag-handle data
        // attribute and the aria-roledescription per plan §5.1 #2/#3.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal("move", chip.GetAttribute("data-calee-drag-handle"));
        Assert.Equal("draggable event", chip.GetAttribute("aria-roledescription"));
    }

    [Fact]
    public async Task WeekDragToMove_VerticalDrop_NoCancel_SnappedNewStartEnd_SameDay()
    {
        // Drop the 10:00–11:00 event one slot (28px @ default 56px/hr × 30min) down,
        // no horizontal movement. New start snaps to 10:30 on the same day, duration
        // preserved → 11:30 end. NewLaneId is null in Week view (ADR-0011).
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);   // Tuesday May 19, 10:00–11:00 EDT.
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Fallback geometry: 56px/hour × 10 hours = 560px tall × 700px wide (100/col).
        // One slot at SlotDurationMinutes=30 → 28px vertical. DeltaX=0 → no day shift.
        var payload = new DropPayload(0, 0, 0, 28, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewStart.Hour);
        Assert.Equal(30, captured.NewStart.Minute);
        Assert.Equal(11, captured.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.Equal(19, captured.NewStart.Day);   // Tuesday May 19 — unchanged.
        Assert.Null(captured.NewLaneId);
    }

    [Fact]
    public async Task WeekDragToMove_CrossDayDrop_NewStartFallsOnTargetDay()
    {
        // Drop the Tuesday 10:00–11:00 event two day-columns to the right (Thursday)
        // and one vertical slot down (10:30). Start.Date must shift from May 19 → May 21
        // and time-of-day must snap to 10:30.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Fallback grid width = 700px → 100px/column. DeltaX=200 = 2 columns right.
        // DeltaY=28 = 1 slot down (snap to 10:30).
        var payload = new DropPayload(0, 0, 200, 28, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(21, captured!.NewStart.Day);   // Tuesday + 2 days = Thursday May 21.
        Assert.Equal(10, captured.NewStart.Hour);
        Assert.Equal(30, captured.NewStart.Minute);
        Assert.Equal(21, captured.NewEnd.Day);
        Assert.Equal(11, captured.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.Null(captured.NewLaneId);
    }

    [Fact]
    public async Task WeekDragToMove_PreservesEventDuration()
    {
        // 30-minute event dropped with arbitrary deltas must yield NewEnd - NewStart == 30 min.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 10, startMin: 0, endMin: 30);  // 10:00–10:30
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // One column right, two slots down.
        var payload = new DropPayload(0, 0, 100, 56, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromMinutes(30), captured!.NewEnd - captured.NewStart);
    }

    [Fact]
    public async Task WeekDragToMove_SnapsToSlot_OnNonAlignedDrop()
    {
        // Vertical snap: DeltaY=20 is closer to 28 (one slot @ 30min) than 0 — snap to 10:30.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 20, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // Snapped to a 30-minute slot boundary.
        Assert.Equal(0, captured!.NewStart.Minute % 30);
    }

    [Fact]
    public async Task WeekDragToMove_SnapsToDayColumn_OnNonAlignedDropX()
    {
        // Horizontal snap: DeltaX=70 with 100px/column is closer to one column (100px) than
        // zero columns — round-to-nearest moves the day index by +1. Verifies the column-shift
        // rounds via AwayFromZero rather than truncating toward zero.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);   // Tuesday May 19.
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 70, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // Tuesday + 1 = Wednesday May 20.
        Assert.Equal(20, captured!.NewStart.Day);
        // Time-of-day unchanged.
        Assert.Equal(10, captured.NewStart.Hour);
        Assert.Equal(0, captured.NewStart.Minute);
    }

    [Fact]
    public async Task WeekDragToMove_Cancel_True_Reverts_OptimisticPin()
    {
        // Consumer sets context.Cancel = true → the pin clears and the event renders
        // at its original column (Tuesday) again.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);   // Tuesday May 19, 10:00.
        var cancelHandler = EventCallback.Factory.Create<EventMoveContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, cancelHandler));

        // Pre-drop: chip is in the Tuesday column (index 2 with Sunday start).
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Single(columns[2].QuerySelectorAll(".calee-scheduler-event"));

        // Drop one column right (Wednesday) and one slot down (10:30).
        var payload = new DropPayload(0, 0, 100, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Cancel cleared the pin.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));

        // And the chip is back in the Tuesday column at its original position.
        hourGrid = cut.Find("[data-calee-region='hour-grid']");
        columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Single(columns[2].QuerySelectorAll(".calee-scheduler-event"));
        Assert.Empty(columns[3].QuerySelectorAll(".calee-scheduler-event"));
        var style = columns[2].QuerySelector(".calee-scheduler-event")!.GetAttribute("style") ?? "";
        Assert.Contains("top: 20", style);  // 10:00 within an 8–18 band → 20%.
    }

    [Fact]
    public async Task WeekDragToMove_PinAppliedVisually_BeforeConsumerCatchUp()
    {
        // After a successful drop the chip immediately moves to the new column + time —
        // the consumer's data hasn't caught up yet. Drop Tuesday 10:00 → Thursday 10:30.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // 2 columns right (Thursday), 1 slot down.
        var payload = new DropPayload(0, 0, 200, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Pin set: Thursday May 21, 10:30.
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(21, pin!.Value.Start.Day);
        Assert.Equal(10, pin.Value.Start.Hour);
        Assert.Equal(30, pin.Value.Start.Minute);

        // Chip moved off Tuesday (index 2), onto Thursday (index 4). top=25% (10:30 in 8–18 band).
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Empty(columns[2].QuerySelectorAll(".calee-scheduler-event"));
        var thuChips = columns[4].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(thuChips);
        var style = thuChips[0].GetAttribute("style") ?? "";
        Assert.Contains("top: 25", style);
    }

    [Fact]
    public async Task WeekOptimisticPin_ClearedOnConsumerDataCatchup()
    {
        // After the consumer accepts the move and pushes a new Events list with the
        // pinned times, the pin is redundant — OnParametersSet drops it.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // Drop one slot down on the same column.
        var payload = new DropPayload(0, 0, 0, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        var pinBefore = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pinBefore);

        // Consumer "catches up" — pushes the new Events list with the pinned times.
        var moved = new CalendarEvent("e", "e",
            pinBefore!.Value.Start, pinBefore.Value.End, IsAllDay: false);
        cut.Render(p => p.Add(c => c.Events, new[] { moved }));

        // Pin dropped on the next OnParametersSet.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    [Fact]
    public async Task WeekDragToMove_MultiDayEvent_PinCollapsesToSingleChunk_DuringOptimisticWindow()
    {
        // Regression test for the multi-day pin-collapse path in ApplyOptimisticPins
        // (Task 9 reviewer rec #1). Under PerDay split a multi-day event renders as
        // N chunks (one per visible day). After a drag drop, the optimistic pin
        // collapses ALL chunks of the pinned event into ONE synthetic chunk at the
        // pinned (Start, End). This test pins down the collapse — distinct from a
        // hypothetical rewrite-in-place — by verifying chunk count post-drop.
        using var ctx = NewContext();

        // Multi-day event: Mon May 18 23:00 → Wed May 20 02:00. With StartHour=0 /
        // EndHour=24, the read-only render produces three chunks across columns 1/2/3
        // (the same fixture as Timed_Multi_Day_Event_Splits_Per_Day_With_Clip_Indicators).
        var monday11pm = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.FromHours(-4));
        var wed2am = new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("trip", monday11pm, wed2am);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // Sanity: pre-drag, the multi-day event renders as three chunks (Mon/Tue/Wed).
        var hourGridBefore = cut.Find("[data-calee-region='hour-grid']");
        var columnsBefore = hourGridBefore.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Single(columnsBefore[1].QuerySelectorAll(".calee-scheduler-event")); // Mon
        Assert.Single(columnsBefore[2].QuerySelectorAll(".calee-scheduler-event")); // Tue
        Assert.Single(columnsBefore[3].QuerySelectorAll(".calee-scheduler-event")); // Wed

        // Drag forward two day-columns (DeltaX=200px at 100px/col fallback geometry),
        // no vertical change. Same convention as WeekDragToMove_CrossDayDrop_*.
        var payload = new DropPayload(0, 0, 200, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Pin shifted by +2 days: Wed May 20 23:00 → Fri May 22 02:00.
        var pin = cut.Instance.GetOptimisticPin("trip");
        Assert.NotNull(pin);
        Assert.Equal(20, pin!.Value.Start.Day);
        Assert.Equal(23, pin.Value.Start.Hour);
        Assert.Equal(22, pin.Value.End.Day);
        Assert.Equal(2, pin.Value.End.Hour);

        // Collapse assertion: during the optimistic window the pinned event renders
        // as exactly ONE chunk, bucketed into the column where the pinned Start.Day
        // falls (Wed = column 3). Columns 4 (Thu) and 5 (Fri) stay empty until the
        // consumer's catch-up re-splits per-day via VisibleEventSet. A rewrite-in-
        // place implementation would have produced three chunks across cols 3/4/5.
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Single(columns[3].QuerySelectorAll(".calee-scheduler-event"));  // Wed
        Assert.Empty(columns[4].QuerySelectorAll(".calee-scheduler-event"));   // Thu
        Assert.Empty(columns[5].QuerySelectorAll(".calee-scheduler-event"));   // Fri

        // The Wed chunk has clip-bottom (event continues past midnight) and no
        // clip-top (the chunk's Start at 23:00 sits inside the visible band).
        var wedChip = columns[3].QuerySelector(".calee-scheduler-event")!;
        var wedClasses = wedChip.GetAttribute("class") ?? string.Empty;
        Assert.Contains("calee-scheduler-event--clip-bottom", wedClasses);
        Assert.DoesNotContain("calee-scheduler-event--clip-top", wedClasses);
    }

    // ----- Drag-to-resize (Phase 2 Task 7 — FR-26) ---------------------------------

    [Fact]
    public void WeekDragToResize_Disabled_By_Default()
    {
        // FR-29 fail-closed: AllowDragToResize defaults false; chip carries no resize
        // affordances.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Null(chip.GetAttribute("aria-roledescription"));
        Assert.Empty(chip.QuerySelectorAll("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public void WeekDragToResize_Enabled_AttachesResizeAffordances()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal("resizable event", chip.GetAttribute("aria-roledescription"));
        Assert.NotNull(chip.QuerySelector("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public void WeekDragToResize_Both_AllowDragToMove_And_AllowDragToResize_Combines_AriaRoleDescription()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal("draggable resizable event", chip.GetAttribute("aria-roledescription"));
        Assert.Equal("move", chip.GetAttribute("data-calee-drag-handle"));
        Assert.NotNull(chip.QuerySelector("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public async Task WeekDragToResize_Drop_NoCancel_FiresOnEventResized_WithSnappedNewEnd()
    {
        // Drop bottom edge of Tuesday 10:00–11:00 one slot down → 11:30 same day.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 28, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(11, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.Equal(Anchor.Day, captured.NewEnd.Day);   // Same day — resize doesn't cross columns.
    }

    [Fact]
    public async Task WeekDragToResize_PreservesStart()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 56, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(10, pin!.Value.Start.Hour);
        Assert.Equal(0, pin.Value.Start.Minute);
    }

    [Fact]
    public async Task WeekDragToResize_SnapsToSlot_OnNonAlignedDrop()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 20, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(0, captured!.NewEnd.Minute % 30);
    }

    [Fact]
    public async Task WeekDragToResize_Cancel_True_Reverts_OptimisticPin()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var cancelHandler = EventCallback.Factory.Create<EventResizeContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized, cancelHandler));

        var payload = new DropPayload(0, 0, 0, 28, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    [Fact]
    public async Task WeekDragToResize_ClampsToMinimumOneSlotDuration_WhenDraggedPastStart()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, -10000, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.True(captured.NewEnd > ev.Start);
    }

    [Fact]
    public async Task WeekDragToResize_ClampsToEndHour_WhenDraggedPastViewBottom()
    {
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 10000, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(18, captured!.NewEnd.Hour);
        Assert.Equal(0, captured.NewEnd.Minute);
        // Same day as the original event — resize stays in the original column.
        Assert.Equal(Anchor.Day, captured.NewEnd.Day);
    }

    [Fact]
    public async Task WeekDragToResize_EventEndingAtMidnight_ZeroDelta_RoundTripsExactly()
    {
        // Regression (code review on commit 0cfee2f): the resize End-anchor formula must
        // measure elapsed time since visibleStart, not ev.End.TimeOfDay — TimeOfDay is
        // ambiguous for an event ending exactly at midnight (semantically the end of the
        // *current* day / 24:00, but TimeOfDay reports 00:00, indistinguishable from the
        // top of the band). A buggy TimeOfDay-based formula collapsed this down to the
        // minimum-duration clamp on drop instead of leaving End unchanged.
        using var ctx = NewContext();

        var start = new DateTimeOffset(2026, 5, 19, 22, 0, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.FromHours(-4)); // Midnight.
        var ev = Timed("late-night", start, end);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(end, captured!.NewEnd);
    }

    [Fact]
    public async Task WeekDragToResize_ChunkOfMultiDayEvent_EndPastOriginDay_ClampsToOriginDayBandEnd()
    {
        // Companion regression: grabbing an *early* chunk's resize handle on a multi-day
        // event whose true End is on a later day must clamp to that chunk's own band end
        // (existing End-of-band clamp semantics, FR-26), not mis-map ev.End's wall-clock
        // time-of-day onto the grabbed chunk's own date the way the buggy TimeOfDay-only
        // formula did.
        using var ctx = NewContext();

        var start = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.FromHours(-4));  // Monday.
        var end = new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.FromHours(-4));    // Wednesday.
        var ev = Timed("multi-day-trip", start, end);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // Grab the Monday chunk's resize handle — ev.Start's own day, and the *first* of
        // the three Monday/Tuesday/Wednesday chunks this event renders.
        var payload = new DropPayload(0, 0, 0, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // Clamped to Monday's own band end (midnight Tuesday) — not the wrong "Monday
        // 14:00" a wall-clock-time-of-day-only formula would mis-map ev.End's true 14:00
        // onto, and not the true (out-of-scope for single-axis resize) Wednesday 14:00.
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4)), captured!.NewEnd);
    }

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) ---------------------------------

    [Fact]
    public void WeekDragToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed.
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var slot = cut.Find(".calee-scheduler-slot");
        var classAttr = slot.GetAttribute("class") ?? string.Empty;
        Assert.DoesNotContain("calee-scheduler-slot--create-affordance", classAttr);
    }

    [Fact]
    public void WeekDragToCreate_Enabled_AttachesGridBackgroundHandler()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToCreate, true));

        var slots = cut.FindAll(".calee-scheduler-slot");
        Assert.NotEmpty(slots);
        foreach (var s in slots)
        {
            var classAttr = s.GetAttribute("class") ?? string.Empty;
            Assert.Contains("calee-scheduler-slot--create-affordance", classAttr);
        }
    }

    [Fact]
    public async Task WeekDragToCreate_Drop_NoCancel_FiresOnEventCreated_WithSnappedStartEnd()
    {
        // Anchor is Tuesday 2026-05-19, default FirstDayOfWeek=Sunday → Tuesday is col 2.
        // Slot 4 at 30-min granularity past 8:00 = 10:00. DeltaY=28 → one slot down.
        // Expected: Tuesday 10:00 → 11:00.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Anchor col = 2 (Tuesday), slot = 4 (10:00).
        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(2, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
        // Tuesday May 19.
        Assert.Equal(19, captured.Slot.Start.Day);
        Assert.Equal(19, captured.Slot.End.Day);
        Assert.Null(captured.Slot.LaneId);
    }

    [Fact]
    public async Task WeekDragToCreate_BidirectionalDrag_NormalizesStartLessThanEnd()
    {
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Anchor col=2 (Tue), slot=6 (11:00). DeltaY=-56 → -2 slots → final slot 4 (10:00).
        // Start = 10:00, End = 11:30 (covers slots 4..6 inclusive).
        var payload = new DropPayload(0, 0, 0, -56, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(2, 6, payload));

        Assert.NotNull(captured);
        Assert.True(captured!.Slot.Start < captured.Slot.End);
        Assert.Equal(10, captured.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task WeekDragToCreate_SnapsToSlot_OnNonAlignedVerticalDrop()
    {
        // DeltaY=20 → rounds AwayFromZero to +1 slot. Start at 10:00 → End at 11:00.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 20, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(2, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(0, captured!.Slot.Start.Minute % 30);
        Assert.Equal(0, captured.Slot.End.Minute % 30);
    }

    [Fact]
    public async Task WeekDragToCreate_StaysWithinAnchorDayColumn()
    {
        // Even an enormous +DeltaX (cursor wandered into adjacent column) must not
        // change the anchor column — Week view's drag-to-create is single-axis (vertical
        // time only). The resulting Start/End must live on Tuesday May 19.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Massive +DeltaX, normal DeltaY. The handler ignores DeltaXPx entirely.
        var payload = new DropPayload(0, 0, 99999, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(2, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(19, captured!.Slot.Start.Day); // Still Tuesday May 19.
        Assert.Equal(19, captured.Slot.End.Day);
    }

    [Fact]
    public async Task WeekDragToCreate_Cancel_True_NoPersistedState()
    {
        using var ctx = NewContext();

        var cancelHandler = EventCallback.Factory.Create<EventCreateContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated, cancelHandler));

        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(2, 4, payload));

        // Option A: no library-rendered chip exists for a create. Even with Cancel=true
        // there's nothing to "revert."
        Assert.Empty(cut.FindAll(".calee-scheduler-week-grid .calee-scheduler-event"));
    }

    [Fact]
    public async Task WeekDragToCreate_Disallows_Start_On_ExistingEventChip()
    {
        // Pressing on an event chip falls through to event-click, not a create.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var eventClickFired = false;
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, _ => eventClickFired = true))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find(".calee-scheduler-week-grid .calee-scheduler-event");
        await cut.InvokeAsync(() => chip.Click());

        Assert.True(eventClickFired);
        Assert.False(createFired);
    }

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -------------------------

    [Fact]
    public async Task DoubleClickToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed. bUnit's dispatch raises MissingEventHandlerException
        // on an ondblclick with no bound handler, which is the proof the binding
        // is absent. The test-seam also short-circuits via the AllowDoubleClickToCreate
        // guard.
        using var ctx = NewContext();

        var fired = false;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        var slot = cut.Find(".calee-scheduler-slot");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => slot.DoubleClick()));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 4));
        Assert.False(fired);
    }

    [Fact]
    public async Task DoubleClickToCreate_Enabled_OnEmptySlot_FiresOnEventCreated_WithDefaultDuration()
    {
        // Anchor week starts Sun May 17 2026. Column 2 = Tue May 19 (the Anchor).
        // Slot 4 (30-min granularity past 8:00) = 10:00 → End = 10:30.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 4));

        Assert.NotNull(captured);
        Assert.Equal(19, captured!.Slot.Start.Day);
        Assert.Equal(10, captured.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(10, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
        Assert.Null(captured.Slot.LaneId);
    }

    [Fact]
    public async Task DoubleClickToCreate_Disabled_OnExistingEventChip()
    {
        // Same DOM-ordering rationale as Day: event chips sit in a separate events-row
        // div sibling-to the slot cells. The chip itself has no @ondblclick handler,
        // so bUnit raises MissingEventHandlerException on dispatch — which is the
        // proof that double-click create cannot start from a chip.
        using var ctx = NewContext();

        var ev = Timed("e", Anchor, 10, 11);
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find(".calee-scheduler-week-grid .calee-scheduler-event");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => chip.DoubleClick()));

        Assert.False(createFired);
    }

    [Fact]
    public async Task DoubleClickToCreate_RespectsExplicit_DefaultCreateDurationMinutes()
    {
        // Explicit option = 90 minutes. From 10:00 → 11:30.
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultCreateDurationMinutes = 90);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 4));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DoubleClickToCreate_NullOption_FallsBackToSlotDurationMinutes()
    {
        // Week view is a time-grid view → null option resolves to SlotDurationMinutes.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Slot 2 at 60-min granularity past 8:00 = 10:00. Default duration = 60 → End = 11:00.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 2));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(11, captured.Slot.End.Hour);
    }

    // ----- Overlap-block rendering (Task 4) ----------------------------------------

    [Fact]
    public void WeekView_FourOverlapping_RendersOverlapBlock_AndFiresOverlapContext()
    {
        DayOverflowContext<CalendarEvent>? captured = null;
        var day = Anchor; // Tuesday 2026-05-19, inside the rendered week.

        // Four events overlapping 9:00–10:00 on the anchor day. With DefaultMaxOverlapColumns=3,
        // the engine places 2 events in positioned columns 0 and 1, plus 1 block covering
        // the 2 surplus events. Block text should be "+2".
        var evs = new[]
        {
            Timed("a", day, 9, 10),
            Timed("b", day, 9, 10),
            Timed("c", day, 9, 10),
            Timed("d", day, 9, 10),
        };

        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, evs)
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var block = cut.Find(".calee-scheduler-overlap-block");
        Assert.Equal("+2", block.TextContent.Trim());

        block.Click();
        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Overlap, captured!.Kind);
        Assert.Equal(2, captured.Events.Count);
        Assert.NotNull(captured.RegionStart);
    }

    // ----- VisibleDays (issue #6 — Work Week engine primitive) --------------------

    [Fact]
    public void VisibleDays_Null_Default_RendersAllSevenDays_Unchanged()
    {
        // Explicit null (the default) must be byte-for-byte identical to omitting the
        // parameter entirely — no behavior change for existing consumers.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, (IReadOnlyList<DayOfWeek>?)null));

        Assert.Equal(7, cut.FindAll("[data-calee-region='day-header']").Count);
        var columns = cut.Find("[data-calee-region='hour-grid']").QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Equal(7, columns.Length);
    }

    [Fact]
    public void VisibleDays_MonToFri_RendersFiveColumns_OrderedByFirstDayOfWeek()
    {
        // FirstDayOfWeek=Monday → week is Mon 18 .. Sun 24 May 2026. Supplying the
        // subset out of order and interleaved with a non-visible day must not affect
        // the rendered order — that always derives from FirstDayOfWeek.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Tuesday, DayOfWeek.Thursday,
            }));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        var dates = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-date")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "18", "19", "20", "21", "22" }, dates);
    }

    [Fact]
    public void VisibleDays_NonContiguous_MonWedFri_RendersThreeColumns_InOrder()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(3, headers.Count);
        var dates = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-date")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "18", "20", "22" }, dates);
    }

    [Fact]
    public void VisibleDays_EmptyList_SoftDegrades_To_AllSevenDays()
    {
        // PRD §4.6 soft-degradation: an empty VisibleDays renders "all seven days"
        // rather than a zero-column grid, mirroring the null-Events idiom.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, Array.Empty<DayOfWeek>()));

        Assert.Equal(7, cut.FindAll("[data-calee-region='day-header']").Count);
    }

    [Fact]
    public void VisibleDays_ValuesMatchingNoDayOfWeek_SoftDegrades_To_AllSevenDays()
    {
        // Effectively-empty case: a non-empty list whose values don't intersect the
        // week's seven days must fall back the same way as a literal empty list.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, new[] { (DayOfWeek)99 }));

        Assert.Equal(7, cut.FindAll("[data-calee-region='day-header']").Count);
    }

    [Fact]
    public void VisibleDays_Duplicates_Deduped()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Wednesday, DayOfWeek.Wednesday,
            }));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(2, headers.Count);
        var dates = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-date")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "18", "20" }, dates);
    }

    [Fact]
    public void VisibleDays_MultiDayEvent_ClipsAcrossHiddenDay_HiddenDayNotRendered()
    {
        // Mon/Wed/Fri subset (Tuesday hidden). Event spans Monday 23:00 → Wednesday
        // 02:00 — continues straight through the hidden Tuesday. The Monday chunk must
        // still show the continues-to-later indicator and the Wednesday chunk the
        // continues-from-earlier indicator, exactly as if Tuesday were visible — the
        // per-day chunk split is calendar-day-based, not visible-column-based.
        using var ctx = NewContext();

        var monday11pm = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.FromHours(-4));
        var wed2am = new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("overnight", monday11pm, wed2am);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.Events, new[] { ev }));

        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var columns = hourGrid.QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Equal(3, columns.Length); // Mon, Wed, Fri only — no hidden Tuesday column.

        var monEvents = columns[0].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(monEvents);
        Assert.Contains("calee-scheduler-event--clip-bottom", monEvents[0].GetAttribute("class") ?? "");
        Assert.DoesNotContain("calee-scheduler-event--clip-top", monEvents[0].GetAttribute("class") ?? "");

        var wedEvents = columns[1].QuerySelectorAll(".calee-scheduler-event");
        Assert.Single(wedEvents);
        Assert.Contains("calee-scheduler-event--clip-top", wedEvents[0].GetAttribute("class") ?? "");
        Assert.DoesNotContain("calee-scheduler-event--clip-bottom", wedEvents[0].GetAttribute("class") ?? "");

        var friEvents = columns[2].QuerySelectorAll(".calee-scheduler-event");
        Assert.Empty(friEvents);

        // Exactly two rendered chunks total — the would-be Tuesday chunk is dropped.
        Assert.Equal(2, hourGrid.QuerySelectorAll(".calee-scheduler-event").Length);
    }

    [Fact]
    public void VisibleDays_TimedEvent_EntirelyOnHiddenDay_Excluded()
    {
        using var ctx = NewContext();

        // Tuesday May 19, 10:00–11:00 — hidden in the Mon/Wed/Fri subset.
        var ev = Timed("hidden-day", Anchor, 10, 11);

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll(".calee-scheduler-event"));
    }

    [Fact]
    public void VisibleDays_AllDayEvent_EntirelyOnHiddenDay_Excluded()
    {
        using var ctx = NewContext();

        // All-day event on Tuesday May 19 — hidden in the Mon/Wed/Fri subset.
        var ev = AllDay("hidden-allday", Edt(2026, 5, 19), Edt(2026, 5, 20));

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll(".calee-scheduler-all-day-event"));
    }

    [Fact]
    public void VisibleDays_OnRangeChanged_SpansFirstVisibleDayStart_To_LastVisibleDayEnd()
    {
        using var ctx = NewContext();
        SchedulerRange? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => captured = r)));

        Assert.NotNull(captured);
        Assert.Equal(Edt(2026, 5, 18), captured!.Start);  // Monday 00:00 — first visible day.
        Assert.Equal(Edt(2026, 5, 23), captured.End);     // Saturday 00:00 — Friday's day end.
    }

    [Fact]
    public async Task VisibleDays_KeyboardNav_ArrowRight_MovesOnlyAcrossVisibleColumns()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var grid = cut.Find("[role='grid']");

        // Initial focus col=0 (Monday). One ArrowRight → col=1, which must be Wednesday
        // (not the hidden Tuesday — there is no column for it at all).
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(captured);
        Assert.Equal(20, captured!.Start.Day); // Wednesday May 20.

        // Second ArrowRight → col=2 (Friday).
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(22, captured!.Start.Day); // Friday May 22.

        // A third ArrowRight has nowhere further to go — clamps at the last visible
        // column (Friday), never wandering into a hidden day.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(22, captured!.Start.Day); // Still Friday May 22.
    }

    [Fact]
    public async Task VisibleDays_DragToMove_ClampsToLastVisibleColumn_NotHiddenDay()
    {
        // Mon/Wed/Fri subset (ColumnCount=3). A drop with an enormous rightward
        // DeltaXPx must clamp to the last *visible* column (Friday, index 2) — not to
        // some index that would land on a hidden Tuesday/Thursday if they were still
        // counted.
        using var ctx = NewContext();

        var monday10am = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("mon-event", monday10am, monday10am.AddHours(1));
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 99999, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(22, captured!.NewStart.Day); // Friday May 22 — last visible column.
    }

    [Fact]
    public async Task VisibleDays_DragToCreate_AnchorColumnIndex_MapsToVisibleDay()
    {
        // Column index 1 in a Mon/Wed/Fri subset must resolve to Wednesday — the hidden
        // Tuesday never occupies a column index at all.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(1, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(20, captured!.Slot.Start.Day); // Wednesday May 20.
    }

    [Fact]
    public async Task VisibleDays_DoubleClickToCreate_AnchorColumnIndex_MapsToVisibleDay()
    {
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Column index 2 must be Friday — the third and last visible column.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 4));

        Assert.NotNull(captured);
        Assert.Equal(22, captured!.Slot.Start.Day); // Friday May 22.
    }

    // ----- VisibleDays code-review fixes (blocking + should-fix regression tests) --

    [Fact]
    public async Task VisibleDays_DragToMove_MultiDayEvent_StartOnHiddenDay_ZeroDelta_RoundTripsExactly()
    {
        // BLOCKING bug from code review: a multi-day event whose true Start falls on a
        // day VisibleDays hides only renders (and is only draggable from) its later,
        // clipped-at-start chunk. FirstDayOfWeek=Saturday puts Sat/Sun and Mon in the
        // *same* rendered week (Sat, Sun, Mon, Tue, Wed, Thu, Fri) so VisibleDays=Mon-Fri
        // hides Sat+Sun while Monday's chunk of a Sat->Mon event still renders.
        // The origin column must be derived from the chunk actually grabbed (Monday,
        // column 0 of the Mon-Fri subset) rather than by re-resolving ev.Start's own
        // (hidden) day — a zero-delta drop must round-trip to the exact original
        // Start/End, proving the event's true (hidden-day) Start isn't truncated to
        // the visible chunk.
        using var ctx = NewContext();

        var satMay16_9am = new DateTimeOffset(2026, 5, 16, 9, 0, 0, TimeSpan.FromHours(-4));
        var monMay18_5pm = new DateTimeOffset(2026, 5, 18, 17, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("weekend-trip", satMay16_9am, monMay18_5pm);

        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Saturday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Sanity: exactly one chunk renders — Saturday and Sunday are hidden.
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        Assert.Equal(1, hourGrid.QuerySelectorAll(".calee-scheduler-event").Length);

        // Column 0 = Monday, the only visible (and only draggable) chunk. No drag
        // distance at all (DeltaX=0, DeltaY=0).
        var payload = new DropPayload(0, 0, 0, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, 0, payload));

        Assert.NotNull(captured);
        Assert.Equal(satMay16_9am, captured!.NewStart);
        Assert.Equal(monMay18_5pm, captured.NewEnd);
    }

    [Fact]
    public async Task VisibleDays_DragToMove_MultiDayEvent_StartOnHiddenDay_ColumnShift_MovesEntireEventByCalendarDays()
    {
        // Same repro as the zero-delta test, but with an actual +1-visible-column drag
        // (Monday -> Tuesday). The whole event — including its hidden-day Start — must
        // shift by the same +1 calendar day the column shift represents, preserving
        // duration; it must not get clamped/truncated to the visible chunk's own day.
        using var ctx = NewContext();

        var satMay16_9am = new DateTimeOffset(2026, 5, 16, 9, 0, 0, TimeSpan.FromHours(-4));
        var monMay18_5pm = new DateTimeOffset(2026, 5, 18, 17, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("weekend-trip", satMay16_9am, monMay18_5pm);

        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Saturday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Drag the Monday chunk (col 0) one visible column right, to Tuesday (col 1).
        // Fallback grid width = 700px / 5 columns = 140px/column.
        var payload = new DropPayload(0, 0, 140, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, 0, payload));

        Assert.NotNull(captured);
        Assert.Equal(new DateTimeOffset(2026, 5, 17, 9, 0, 0, TimeSpan.FromHours(-4)), captured!.NewStart);
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 17, 0, 0, TimeSpan.FromHours(-4)), captured.NewEnd);
    }

    [Fact]
    public async Task VisibleDays_DragToResize_MultiDayEvent_StartOnHiddenDay_FiresCallback_WithCorrectMath()
    {
        // BLOCKING bug from code review, resize side — same Sat->Mon repro as the move
        // tests above. The resize handle on the one visible (clipped-at-start) Monday
        // chunk must still fire OnEventResized: Start stays the true (hidden-day) Start,
        // and NewEnd is correctly snapped on Monday's own band.
        using var ctx = NewContext();

        var satMay16_9am = new DateTimeOffset(2026, 5, 16, 9, 0, 0, TimeSpan.FromHours(-4));
        var monMay18_5pm = new DateTimeOffset(2026, 5, 18, 17, 0, 0, TimeSpan.FromHours(-4));
        var ev = Timed("weekend-trip", satMay16_9am, monMay18_5pm);

        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Saturday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // Drag the Monday (col 0) resize handle down by 1 hour (56 px/hour fallback).
        var payload = new DropPayload(0, 0, 0, 56, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, 0, payload));

        Assert.NotNull(captured);
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 18, 0, 0, TimeSpan.FromHours(-4)), captured!.NewEnd);
    }

    [Fact]
    public async Task VisibleDays_SingleDaySubset_Monday_LayoutRangeAndKeyboardClamp()
    {
        // VisibleDays with exactly one day — the degenerate-but-legal minimum subset.
        using var ctx = NewContext();
        SchedulerRange? rangeCaptured = null;
        SchedulerSlot? slotCaptured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => rangeCaptured = r))
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => slotCaptured = s)));

        // Layout: exactly one column.
        Assert.Single(cut.FindAll("[data-calee-region='day-header']"));
        var columns = cut.Find("[data-calee-region='hour-grid']").QuerySelectorAll(".calee-scheduler-week-column");
        Assert.Equal(1, columns.Length);

        // Range: the single visible day's own midnight-to-midnight bounds.
        Assert.NotNull(rangeCaptured);
        Assert.Equal(Edt(2026, 5, 18), rangeCaptured!.Start);
        Assert.Equal(Edt(2026, 5, 19), rangeCaptured.End);

        // Keyboard clamp: with only one column, ArrowRight/ArrowLeft never move focus
        // off it in either direction.
        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(slotCaptured);
        Assert.Equal(18, slotCaptured!.Start.Day); // Still Monday May 18.

        await cut.InvokeAsync(() => grid.KeyDown("ArrowLeft"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(18, slotCaptured!.Start.Day); // Still Monday — never moved off it.
    }

    [Fact]
    public void VisibleDays_FirstDayOfWeekNotInSubset_StillOrdersAndRangesCorrectly()
    {
        // FirstDayOfWeek=Sunday, but VisibleDays hides Sunday (and Saturday) entirely —
        // the week's own "day 0" anchor is never itself a rendered column. Column order
        // must still follow FirstDayOfWeek's rotation (Sun, Mon, ... Sat) filtered down
        // to the Mon-Fri subset, and OnRangeChanged must span the first *visible* day
        // (Monday), not the hidden FirstDayOfWeek anchor (Sunday).
        using var ctx = NewContext();
        SchedulerRange? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Sunday)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => captured = r)));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        var dates = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-date")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "18", "19", "20", "21", "22" }, dates);

        Assert.NotNull(captured);
        Assert.Equal(Edt(2026, 5, 18), captured!.Start); // Monday — first *visible* day, not Sunday.
        Assert.Equal(Edt(2026, 5, 23), captured.End);    // Saturday 00:00 — Friday's day end.
    }

    [Fact]
    public async Task VisibleDays_KeyboardNav_ArrowLeft_MovesOnlyAcrossVisibleColumns_AcrossNonContiguousGap()
    {
        // Mirrors the existing ArrowRight non-contiguous-gap test, but exercising
        // ArrowLeft — previously uncovered.
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var grid = cut.Find("[role='grid']");

        // Move to the last column (Friday, col 2) first.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.NotNull(captured);
        Assert.Equal(22, captured!.Start.Day); // Friday May 22.

        // ArrowLeft from Friday (col 2) -> Wednesday (col 1) — there is no column for
        // the hidden Thursday to land on or pass through.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowLeft"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(20, captured!.Start.Day); // Wednesday May 20.

        // ArrowLeft again -> Monday (col 0), skipping the hidden Tuesday the same way.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowLeft"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(18, captured!.Start.Day); // Monday May 18.

        // A further ArrowLeft has nowhere further to go — clamps at column 0 (Monday),
        // never wandering into a negative index or a hidden day.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowLeft"));
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        Assert.Equal(18, captured!.Start.Day); // Still Monday May 18.
    }
}
