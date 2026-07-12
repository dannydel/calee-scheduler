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
/// Tests for <see cref="CaleeSchedulerTimelineView{TEvent}"/> covering FR-09c, FR-09d,
/// FR-09e, FR-13 (engine reuse with horizontal-time interpretation), FR-16, FR-17,
/// FR-19, FR-19a (Day mode), FR-19b, FR-20, FR-21, FR-23, FR-30 (Timeline portion),
/// FR-53, FR-54, FR-55, NFR-06 (Timeline portion), and PRD §4.6 parameter validation.
/// </summary>
public class CaleeSchedulerTimelineViewTests
{
    // Deterministic timezone + anchor: Tuesday, 2026-05-19 in America/New_York (EDT -04:00).
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static ILane LaneOf(string id, string name, string? color = null) =>
        new Lane(id, name, color);

    private static CalendarEvent Timed(
        string id,
        int startHour,
        int endHour,
        int startMin = 0,
        int endMin = 0,
        string? color = null,
        DateTimeOffset? on = null)
    {
        var date = on ?? Anchor;
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, startHour, startMin, 0, date.Offset);
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, endHour, endMin, 0, date.Offset);
        return new CalendarEvent(id, id, start, end, IsAllDay: false, Color: color);
    }

    private static CalendarEvent TimedSpan(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: false);

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    /// <summary>EventLaneMap with the same callable shape consumers use.</summary>
    private static Func<CalendarEvent, string?> KeyMap(params (string id, string laneId)[] mappings)
    {
        var dict = mappings.ToDictionary(m => m.id, m => (string?)m.laneId);
        return ev => dict.TryGetValue(ev.Id, out var r) ? r : null;
    }

    [Fact]
    public void Renders_Without_Errors_With_Minimal_Params()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        Assert.NotNull(cut.Find(".calee-scheduler-timeline"));
        Assert.NotNull(cut.Find("[role='grid']"));
        Assert.NotNull(cut.Find("[data-calee-region='time-gutter']"));
        Assert.NotNull(cut.Find("[data-calee-region='lane-label']"));
    }

    [Fact]
    public void Renders_One_Row_Per_Lane_In_Order()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] {
                LaneOf("a", "Alpha"),
                LaneOf("b", "Beta"),
                LaneOf("c", "Charlie")
            })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        var rows = cut.FindAll("[data-calee-region='lane-row']");
        Assert.Equal(3, rows.Count);
        Assert.Contains("Alpha", rows[0].TextContent);
        Assert.Contains("Beta", rows[1].TextContent);
        Assert.Contains("Charlie", rows[2].TextContent);
    }

    [Fact]
    public void Lane_Virtualization_Is_Opt_In_And_Exposes_Logical_Row_Metadata()
    {
        using var ctx = NewContext();
        var lanes = Enumerable.Range(0, 25)
            .Select(i => (ILane)LaneOf($"lane-{i}", $"Lane {i}"))
            .ToArray();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.EnableLaneVirtualization, true));

        Assert.Equal("26", cut.Find("[role='grid']").GetAttribute("aria-rowcount"));
        Assert.Equal(20, cut.FindAll("[data-calee-region='lane-row']").Count);
        Assert.Equal("2", cut.Find("[data-lane-id='lane-0']").GetAttribute("aria-rowindex"));
        Assert.NotNull(cut.Find(".calee-scheduler-timeline-virtual-spacer"));
    }

    [Fact]
    public void Lane_Virtualization_Reenables_With_Initial_Window_After_Drag_Gating()
    {
        using var ctx = NewContext();
        var lanes = Enumerable.Range(0, 25)
            .Select(i => (ILane)LaneOf($"lane-{i}", $"Lane {i}"))
            .ToArray();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.EnableLaneVirtualization, true));

        cut.Render(p => p.Add(c => c.AllowDragToMove, true));
        Assert.Equal(25, cut.FindAll("[data-calee-region='lane-row']").Count);

        cut.Render(p => p.Add(c => c.AllowDragToMove, false));
        Assert.Equal(20, cut.FindAll("[data-calee-region='lane-row']").Count);
    }

    [Fact]
    public void LaneKey_Returning_Null_Goes_To_Unassigned_Row()
    {
        using var ctx = NewContext();

        var ev = Timed("e1", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.Events, new[] { ev }));

        var unassignedRow = cut.Find("[data-calee-region='unassigned-row']");
        Assert.NotNull(unassignedRow);
        var unassignedEvents = unassignedRow.QuerySelectorAll(".calee-scheduler-timeline-event");
        Assert.Single(unassignedEvents);
    }

    [Fact]
    public void LaneKey_Returning_Unknown_Id_Goes_To_Unassigned_Row()
    {
        using var ctx = NewContext();

        var ev = Timed("e1", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "ghost-lane"))
            .Add(c => c.Events, new[] { ev }));

        var unassignedRow = cut.Find("[data-calee-region='unassigned-row']");
        Assert.NotNull(unassignedRow);
        Assert.Single(unassignedRow.QuerySelectorAll(".calee-scheduler-timeline-event"));

        // Named lane row should be empty.
        var r1Row = cut.Find("[data-lane-id='r1']");
        Assert.Empty(r1Row.QuerySelectorAll(".calee-scheduler-timeline-event"));
    }

    [Fact]
    public void Unassigned_Row_Hidden_When_ShowUnassignedRow_False()
    {
        using var ctx = NewContext();

        var ev = Timed("e1", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll("[data-calee-region='unassigned-row']"));
    }

    [Fact]
    public void Unassigned_Row_Hidden_When_No_Unassigned_Events()
    {
        using var ctx = NewContext();

        // Event goes to the named lane — no unassigned events.
        var ev = Timed("e1", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll("[data-calee-region='unassigned-row']"));
    }

    [Fact]
    public void AllDay_Event_Renders_In_Banner_Strip_Not_Time_Grid()
    {
        using var ctx = NewContext();

        var allDay = AllDay("celebration",
            new DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.FromHours(-4)));

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.Events, new[] { allDay }));

        var bannerRow = cut.Find("[data-calee-region='all-day']");
        var allDayChips = bannerRow.QuerySelectorAll(".calee-scheduler-timeline-all-day-chip");
        Assert.Single(allDayChips);
        Assert.Contains("celebration", allDayChips[0].TextContent);

        // Hour grid should NOT contain the all-day event.
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        Assert.Empty(hourGrid.QuerySelectorAll(".calee-scheduler-timeline-event"));
    }

    [Fact]
    public void Multi_Day_Timed_Event_Renders_As_Single_Continuous_Block()
    {
        using var ctx = NewContext();

        // Week mode: visible Sun 17 to Sun 24 (Sunday start). Event spans Tue 10am to Thu 3pm.
        var start = new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 5, 21, 15, 0, 0, TimeSpan.FromHours(-4));
        var ev = TimedSpan("multi", start, end);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.Events, new[] { ev }));

        // FR-09e: exactly one event element — no per-day splitting in TimelineView.
        var events = cut.FindAll(".calee-scheduler-timeline-event");
        Assert.Single(events);
    }

    [Fact]
    public void Overlapping_Events_Within_Same_Lane_Get_Sub_Row_Stacking()
    {
        using var ctx = NewContext();

        var a = Timed("a", 9, 11);
        var b = Timed("b", 10, 12);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b }));

        var events = cut.FindAll(".calee-scheduler-timeline-event");
        Assert.Equal(2, events.Count);
        // Each event has its stack slot mapped to top/height with 50% height (2 stacks).
        foreach (var e in events)
        {
            var style = e.GetAttribute("style") ?? "";
            Assert.Contains("height: 50", style);
        }
        // One event at top: 0, the other at top: 50.
        var tops = events.Select(e => e.GetAttribute("style") ?? "").ToList();
        Assert.Contains(tops, s => s.Contains("top: 0.0000%"));
        Assert.Contains(tops, s => s.Contains("top: 50.0000%"));
    }

    [Fact]
    public void Cross_Lane_Overlap_Does_Not_Affect_Stack_Count()
    {
        using var ctx = NewContext();

        // Two events on the same hour but different lanes — each row has its own stack=1.
        var a = Timed("a", 10, 11);
        var b = Timed("b", 10, 11);
        var key = KeyMap(("a", "r1"), ("b", "r2"));

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, key)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b }));

        var events = cut.FindAll(".calee-scheduler-timeline-event");
        Assert.Equal(2, events.Count);
        // Each event should render at full 100% height since it's the only one in its own row.
        foreach (var e in events)
        {
            var style = e.GetAttribute("style") ?? "";
            Assert.Contains("height: 100", style);
        }
    }

    [Fact]
    public void TimeScale_Day_Renders_Hour_Ticks()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        var ticks = cut.FindAll(".calee-scheduler-timeline-tick-label");
        // 10 hours visible (8..17 inclusive); ticks are spaced one per hour.
        Assert.Equal(10, ticks.Count);
        Assert.Contains("8 AM", ticks[0].TextContent);
    }

    [Fact]
    public void TimeScale_Week_Renders_7_Day_Ticks_From_FirstDayOfWeek()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        var ticks = cut.FindAll(".calee-scheduler-timeline-tick-label");
        Assert.Equal(7, ticks.Count);
        // Anchor Tue 2026-05-19. With Monday start, first day = Mon 2026-05-18.
        Assert.Contains("Mon 18", ticks[0].TextContent);
    }

    [Fact]
    public void TimeScale_Month_Renders_All_Days_In_CurrentDate_Month()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Month)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        var ticks = cut.FindAll(".calee-scheduler-timeline-tick-label");
        // May has 31 days.
        Assert.Equal(31, ticks.Count);
        Assert.Equal("1", ticks[0].TextContent.Trim());
        Assert.Equal("31", ticks[^1].TextContent.Trim());
    }

    [Fact]
    public void TimeScale_Day_With_Narrowed_Hours_Shows_Per_Row_Earlier_Chip()
    {
        using var ctx = NewContext();

        // 6am event with StartHour=8 → before visible band → earlier overflow on the lane's row.
        var ev = Timed("early", 6, 7);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var chips = cut.FindAll("[data-calee-region='overflow-chip']");
        Assert.Single(chips);
        Assert.Contains("+1 earlier", chips[0].TextContent);
    }

    [Fact]
    public async Task Per_Row_Overflow_Chip_Click_Carries_LaneId_For_That_Row()
    {
        using var ctx = NewContext();
        DayOverflowContext<CalendarEvent>? captured = null;

        // Two lanes. Out-of-range event 6am on r2 only → chip appears on r2's row.
        // Click → the consumer should receive LaneId="r2".
        var ev = Timed("early", 6, 7);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r2"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnDayOverflowClicked, EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var chips = cut.FindAll("[data-calee-region='overflow-chip']");
        Assert.Single(chips);
        await chips[0].ClickAsync(new MouseEventArgs());

        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Earlier, captured!.Kind);
        Assert.Equal("r2", captured.LaneId);
    }

    [Fact]
    public void TimeScale_Week_Or_Month_Does_Not_Show_Overflow_Chips()
    {
        using var ctx = NewContext();

        var ev = Timed("early", 6, 7);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll("[data-calee-region='overflow-chip']"));
    }

    [Fact]
    public async Task Event_Click_Fires_OnEventClicked_With_Original_TEvent()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;

        var ev = Timed("e1", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var eventEl = cut.Find(".calee-scheduler-timeline-event");
        await cut.InvokeAsync(() => eventEl.Click());

        Assert.NotNull(captured);
        Assert.Same(ev, captured);
    }

    [Fact]
    public async Task Slot_Click_Carries_LaneId_In_SchedulerSlot()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("alpha", "Alpha"), LaneOf("beta", "Beta") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Click first slot of the second lane (beta).
        var betaRow = cut.Find("[data-lane-id='beta']");
        var slot = betaRow.QuerySelector(".calee-scheduler-timeline-slot");
        Assert.NotNull(slot);
        await cut.InvokeAsync(() => slot!.Click());

        Assert.NotNull(captured);
        Assert.Equal("beta", captured!.LaneId);
        Assert.Equal(8, captured.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
        Assert.Equal(8, captured.End.Hour);
        Assert.Equal(30, captured.End.Minute);
    }

    [Fact]
    public void EventFilter_Applies_Before_Lane_Grouping()
    {
        using var ctx = NewContext();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.EventFilter, (Func<CalendarEvent, bool>)(e => e.Id != "b")));

        var events = cut.FindAll(".calee-scheduler-timeline-event");
        Assert.Single(events);
        Assert.Contains("a", events[0].TextContent);
    }

    [Fact]
    public void Current_Time_Indicator_Only_When_TimeScale_Day_And_Today_In_Range()
    {
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        // Today, Day mode → indicator visible (when current hour is in the band 0..24).
        var cut1 = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotEmpty(cut1.FindAll(".calee-scheduler-timeline-current-time-indicator"));

        // Today, Week mode → indicator NOT shown.
        var cut2 = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.Empty(cut2.FindAll(".calee-scheduler-timeline-current-time-indicator"));

        // Day mode but a non-today anchor → indicator not shown.
        var farFuture = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5));
        var cut3 = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, farFuture)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 24)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.Empty(cut3.FindAll(".calee-scheduler-timeline-current-time-indicator"));
    }

    [Fact]
    public void Aria_Grid_Roles_Present_With_Rowheaders_And_Gridcells()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        Assert.NotNull(cut.Find("[role='grid']"));
        // Two rowheaders (one per lane row).
        Assert.Equal(2, cut.FindAll("[role='rowheader']").Count);
        // Multiple columnheaders (one per hour tick).
        Assert.NotEmpty(cut.FindAll("[role='columnheader']"));
        // Gridcells per slot in each row.
        Assert.NotEmpty(cut.FindAll("[role='gridcell']"));
    }

    [Fact]
    public async Task Cross_Lane_Keyboard_Nav_Down_Arrow_Moves_To_Next_Lane_Row()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("alpha", "Alpha"), LaneOf("beta", "Beta") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var grid = cut.Find("[role='grid']");
        // ArrowDown: focused row goes 0 → 1 (beta).
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        // Enter should fire OnSlotClicked at row 1's slot 0.
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));

        Assert.NotNull(captured);
        Assert.Equal("beta", captured!.LaneId);
        Assert.Equal(8, captured.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
    }

    [Fact]
    public void Missing_Lanes_Throws()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.Lanes, default(IReadOnlyList<ILane>)!)
                .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))));
        Assert.Equal("Lanes", ex.ParamName);
    }

    [Fact]
    public void Missing_LaneKey_Throws()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
                .Add(c => c.LaneKey, default(Func<CalendarEvent, string?>)!)));
        Assert.Equal("LaneKey", ex.ParamName);
    }

    [Fact]
    public void Multi_Day_Event_Extending_Past_Visible_Range_Has_Clip_Indicator()
    {
        using var ctx = NewContext();

        // Week mode: visible Sun 17 to Sun 24. Event spans Sat 16 to Wed 20 — left clip.
        var start = new DateTimeOffset(2026, 5, 16, 9, 0, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 5, 20, 17, 0, 0, TimeSpan.FromHours(-4));
        var ev = TimedSpan("spans-past", start, end);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.Events, new[] { ev }));

        var events = cut.FindAll(".calee-scheduler-timeline-event");
        Assert.Single(events);
        var classes = events[0].GetAttribute("class") ?? "";
        Assert.Contains("calee-scheduler-timeline-event--clip-left", classes);
        Assert.DoesNotContain("calee-scheduler-timeline-event--clip-right", classes);
    }

    // ----- Drag-to-move (Phase 2 Task 6 — FR-25 cross-lane → NewLaneId) -----------------

    [Fact]
    public void TimelineDragToMove_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDragToMove must be false and the
        // rendered event chip must NOT carry the drag affordances (data attribute, ARIA).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        Assert.Null(chip.GetAttribute("data-calee-drag-handle"));
        Assert.Null(chip.GetAttribute("aria-roledescription"));
    }

    [Fact]
    public void TimelineDragToMove_Enabled_AttachesDragAffordances()
    {
        // When AllowDragToMove=true every visible chip gains the drag-handle data attribute
        // and the aria-roledescription per plan §5.1 #2/#3.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        Assert.Equal("move", chip.GetAttribute("data-calee-drag-handle"));
        Assert.Equal("draggable event", chip.GetAttribute("aria-roledescription"));
    }

    [Fact]
    public async Task TimelineDragToMove_HorizontalDrop_SameLane_NewStartEndSnapped_DayScale()
    {
        // Drop the r1 10:00–11:00 event one slot (28 px @ default 56 px/hour × 30 min) to
        // the right with zero vertical movement. New start snaps to 10:30 (same lane);
        // NewLaneId carries the row's lane id even for same-row time-only moves (FR-25).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Day mode fallback width = 56 * (18-8) = 560 px. SlotCount=20 → 28 px/slot.
        // DeltaX=28 → +1 slot horizontally. DeltaY=0 → no row shift.
        var payload = new DropPayload(0, 0, 28, 0, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewStart.Hour);
        Assert.Equal(30, captured.NewStart.Minute);
        Assert.Equal(11, captured.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.Equal("r1", captured.NewLaneId);    // FR-25: always populated in TimelineView.
    }

    [Fact]
    public async Task TimelineDragToMove_VerticalDrop_CrossLane_NewLaneIdPopulated()
    {
        // Two lanes, event lives on r1. Drop one row down → r2. No horizontal movement,
        // so Start/End are unchanged from the original 10:00–11:00.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Fallback row height = 40 px (2 rows × 40 = 80 px). DeltaY=40 → +1 row → r2.
        var payload = new DropPayload(0, 0, 0, 40, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal("r2", captured!.NewLaneId);
        Assert.Equal(10, captured.NewStart.Hour);
        Assert.Equal(0, captured.NewStart.Minute);
    }

    [Fact]
    public async Task TimelineDragToMove_DropOnUnassignedRow_NewLaneIdIsNull()
    {
        // ShowUnassignedRow=true with at least one unassigned event so the row renders.
        // Drag the r1 event into the unassigned row → NewLaneId=null per ADR-0011 + FR-25.
        using var ctx = NewContext();

        var r1Event = Timed("on-r1", 10, 11);
        var unassignedEvent = new CalendarEvent("uev", "uev",
            new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 5, 19, 13, 0, 0, TimeSpan.FromHours(-4)),
            IsAllDay: false);

        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, KeyMap(("on-r1", "r1")))   // "uev" projects to null → unassigned.
            .Add(c => c.ShowUnassignedRow, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { r1Event, unassignedEvent })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Two rows render (r1 + unassigned). Row height = 40 px (80 / 2). DeltaY=40 → +1 row → unassigned.
        var payload = new DropPayload(0, 0, 0, 40, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(r1Event, payload));

        Assert.NotNull(captured);
        Assert.Null(captured!.NewLaneId);   // ADR-0011: null = explicitly unassigned.
    }

    [Fact]
    public async Task TimelineDragToMove_DropFromUnassignedToLane_NewLaneIdPopulated()
    {
        // Inverse of the previous test: drag an unassigned event UP into the r1 row.
        using var ctx = NewContext();

        var r1Event = Timed("on-r1", 10, 11);
        var unassignedEvent = new CalendarEvent("uev", "uev",
            new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 5, 19, 13, 0, 0, TimeSpan.FromHours(-4)),
            IsAllDay: false);

        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, KeyMap(("on-r1", "r1")))
            .Add(c => c.ShowUnassignedRow, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { r1Event, unassignedEvent })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // unassignedEvent is on row index 1. DeltaY=-40 (drag upward) → row 0 = r1.
        var payload = new DropPayload(0, 0, 0, -40, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(unassignedEvent, payload));

        Assert.NotNull(captured);
        Assert.Equal("r1", captured!.NewLaneId);
    }

    [Fact]
    public async Task TimelineDragToMove_PreservesEventDuration()
    {
        // 30-minute event dropped with arbitrary deltas must yield NewEnd - NewStart == 30 min.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 10, endMin: 30);   // 10:00–10:30
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 56, 0, "move");   // 2 slots right
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromMinutes(30), captured!.NewEnd - captured.NewStart);
    }

    [Fact]
    public async Task TimelineDragToMove_SnapsToSlot_OnNonAlignedDrop_TimeAxis()
    {
        // X-axis snap: DeltaX=20 with 28 px/slot is closer to 1 slot (28 px) than 0 — snap
        // to 10:30 via Math.Round(20/28) = 1 with AwayFromZero.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 20, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // Result must be a 30-minute slot boundary.
        Assert.Equal(0, captured!.NewStart.Minute % 30);
    }

    [Fact]
    public async Task TimelineDragToMove_SnapsToLaneRow_OnNonAlignedDrop_LaneAxis()
    {
        // Y-axis snap: 3 rows × 40 px = 120 px total. DeltaY=28 is closer to one row (40 px)
        // than zero rows — round-to-nearest moves the row index by +1.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2"), LaneOf("r3", "R3") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal("r2", captured!.NewLaneId);
    }

    [Fact]
    public async Task TimelineDragToMove_Cancel_True_Reverts_OptimisticPin_AllThreeFields()
    {
        // Consumer sets Cancel=true → the pin clears and the event renders back at its
        // original lane and time. Verify all three fields (Start, End, LaneId) roll back.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cancelHandler = EventCallback.Factory.Create<EventMoveContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, cancelHandler));

        // Pre-drop: chip is in r1's row, at 10:00 → left=20% (10:00 within 8–18 band).
        var r1RowBefore = cut.Find("[data-lane-id='r1']");
        Assert.Single(r1RowBefore.QuerySelectorAll(".calee-scheduler-timeline-event"));

        // Drop +1 row down (r2) AND +1 slot right (10:30).
        var payload = new DropPayload(0, 0, 28, 40, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Pin cleared after Cancel=true.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));

        // Chip is back on r1 (not r2) at top: 0 / left: 20% (10:00 within 8–18 band).
        var r1RowAfter = cut.Find("[data-lane-id='r1']");
        var r2RowAfter = cut.Find("[data-lane-id='r2']");
        Assert.Single(r1RowAfter.QuerySelectorAll(".calee-scheduler-timeline-event"));
        Assert.Empty(r2RowAfter.QuerySelectorAll(".calee-scheduler-timeline-event"));
        var style = r1RowAfter.QuerySelector(".calee-scheduler-timeline-event")!.GetAttribute("style") ?? "";
        Assert.Contains("left: 20", style);    // 10:00 → 20% (2h into 10h band).
    }

    [Fact]
    public async Task TimelineDragToMove_PinAppliedVisually_BeforeConsumerCatchUp()
    {
        // After a successful drop the chip must immediately render in the pinned lane row
        // at the pinned time — the consumer's data hasn't caught up yet.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // Drop +1 row down (r2), +1 slot right (10:30).
        var payload = new DropPayload(0, 0, 28, 40, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Pin set with all three fields.
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(10, pin!.Value.Start.Hour);
        Assert.Equal(30, pin.Value.Start.Minute);
        Assert.Equal("r2", pin.Value.LaneId);

        // Chip moved from r1's row to r2's row.
        var r1Row = cut.Find("[data-lane-id='r1']");
        var r2Row = cut.Find("[data-lane-id='r2']");
        Assert.Empty(r1Row.QuerySelectorAll(".calee-scheduler-timeline-event"));
        var r2Chips = r2Row.QuerySelectorAll(".calee-scheduler-timeline-event");
        Assert.Single(r2Chips);
        var style = r2Chips[0].GetAttribute("style") ?? "";
        Assert.Contains("left: 25", style);    // 10:30 → 25% (2.5h into 10h band).
    }

    [Fact]
    public async Task TimelineOptimisticPin_ClearedOnConsumerDataCatchup()
    {
        // After the consumer accepts the move and pushes a new Events list AND a new
        // LaneKey projection reflecting the new lane, the pin is redundant — OnParametersSet
        // drops it. Verifies the three-field acknowledge condition.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // Drop r1 → r2 + 30 minutes later.
        var payload = new DropPayload(0, 0, 28, 40, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        var pinBefore = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pinBefore);

        // Consumer catches up partially: Events updated to new times, but LaneKey STILL
        // returns "r1" → pin must NOT clear (the lane component hasn't been acknowledged).
        var movedSameLane = new CalendarEvent("e", "e",
            pinBefore!.Value.Start, pinBefore.Value.End, IsAllDay: false);
        cut.Render(p => p.Add(c => c.Events, new[] { movedSameLane }));
        Assert.NotNull(cut.Instance.GetOptimisticPin("e"));

        // Now the consumer also pushes the new LaneKey projection (→ r2). All three fields
        // now agree with the pin → the pin is dropped on this OnParametersSet.
        cut.Render(p => p
            .Add(c => c.Events, new[] { movedSameLane })
            .Add(c => c.LaneKey, KeyMap(("e", "r2"))));
        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    [Fact]
    public async Task TimelineDragToMove_DropOutsideGrid_ClampsToNearestValidRow()
    {
        // A drop dragged far past the last row should clamp to the bottom row rather than
        // firing a no-op. Same shape as Day/Week's "clamp at edges" behavior.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Wildly excessive DeltaY (10 000 px down) should clamp to the bottom row (r2).
        var payload = new DropPayload(0, 0, 0, 10000, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal("r2", captured!.NewLaneId);

        // Same upward direction — clamp to the topmost row (r1, unchanged).
        captured = null;
        var upPayload = new DropPayload(0, 0, 0, -10000, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, upPayload));
        Assert.NotNull(captured);
        Assert.Equal("r1", captured!.NewLaneId);
    }

    [Fact]
    public async Task TimelineDragToMove_WeekScale_HorizontalDropCrossesDayColumns()
    {
        // TimeScale=Week: 7 day cells across 700 px (fallback width) → 100 px/cell.
        // Drag the Tuesday 10:00 event 2 columns right → Thursday 10:00 (time-of-day
        // preserved across the day shift).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);   // Anchored on Tue 2026-05-19.
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Sunday)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // DeltaX=200 with 100 px/cell → +2 days → Thursday May 21.
        var payload = new DropPayload(0, 0, 200, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(21, captured!.NewStart.Day);
        // Time-of-day preserved: 10:00 → 11:00.
        Assert.Equal(10, captured.NewStart.Hour);
        Assert.Equal(0, captured.NewStart.Minute);
        Assert.Equal(11, captured.NewEnd.Hour);
        Assert.Equal("r1", captured.NewLaneId);
    }

    [Fact]
    public async Task TimelineDragToMove_MonthScale_SmokeTest()
    {
        // TimeScale=Month: ~31 day cells across 3100 px (fallback width). One column right
        // is +1 day. Time-of-day preserved across the day shift.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);   // Anchored on Tue 2026-05-19.
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Month)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // 100 px/day in Month-mode fallback. DeltaX=100 → +1 day → May 20.
        var payload = new DropPayload(0, 0, 100, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(20, captured!.NewStart.Day);
        Assert.Equal(10, captured.NewStart.Hour);
        Assert.Equal("r1", captured.NewLaneId);
    }

    // ----- Drag-to-resize (Phase 2 Task 7 — FR-26) ---------------------------------

    [Fact]
    public void TimelineDragToResize_Disabled_By_Default()
    {
        // FR-29 fail-closed.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        Assert.Null(chip.GetAttribute("aria-roledescription"));
        Assert.Empty(chip.QuerySelectorAll("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public void TimelineDragToResize_Enabled_AttachesResizeAffordances()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        Assert.Equal("resizable event", chip.GetAttribute("aria-roledescription"));
        Assert.NotNull(chip.QuerySelector("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public void TimelineDragToResize_Both_AllowDragToMove_And_AllowDragToResize_Combines_AriaRoleDescription()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        Assert.Equal("draggable resizable event", chip.GetAttribute("aria-roledescription"));
        Assert.Equal("move", chip.GetAttribute("data-calee-drag-handle"));
        Assert.NotNull(chip.QuerySelector("[data-calee-drag-handle='resize-end']"));
    }

    [Fact]
    public async Task TimelineDragToResize_Drop_NoCancel_FiresOnEventResized_WithSnappedNewEnd_DayScale()
    {
        // Drop the right edge of the r1 10:00–11:00 event one slot right.
        // Day mode fallback width = 560 px (56*(18-8)); SlotCount=20 → 28 px/slot.
        // DeltaX=28 → +1 slot horizontally → 11:30.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 28, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(11, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
    }

    [Fact]
    public async Task TimelineDragToResize_PreservesStart()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 56, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(10, pin!.Value.Start.Hour);
        Assert.Equal(0, pin.Value.Start.Minute);
    }

    [Fact]
    public async Task TimelineDragToResize_SnapsToSlot_OnNonAlignedDrop()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 20, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(0, captured!.NewEnd.Minute % 30);
    }

    [Fact]
    public async Task TimelineDragToResize_Cancel_True_Reverts_OptimisticPin()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cancelHandler = EventCallback.Factory.Create<EventResizeContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized, cancelHandler));

        var payload = new DropPayload(0, 0, 28, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    [Fact]
    public async Task TimelineDragToResize_ClampsToMinimumOneSlotDuration_WhenDraggedPastStart()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, -10000, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.True(captured.NewEnd > ev.Start);
    }

    [Fact]
    public async Task TimelineDragToResize_ClampsToEndHour_WhenDraggedPastViewRight()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 10000, 0, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(18, captured!.NewEnd.Hour);
        Assert.Equal(0, captured.NewEnd.Minute);
    }

    [Fact]
    public async Task TimelineDragToResize_DoesNotChangeLane()
    {
        // Even with DeltaYPx supplied (which would be a lane shift for drag-to-move),
        // resize keeps LaneId stable. The chip stays on r1 and the pin's LaneId stays r1.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "R1"), LaneOf("r2", "R2") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // DeltaY=10000 would push down past r2 in move-mode; for resize it's irrelevant —
        // the lane row stays r1.
        var payload = new DropPayload(0, 0, 28, 10000, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // The resize fired; the pin's LaneId is still r1.
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal("r1", pin!.Value.LaneId);

        // Visually, the chip is on the r1 row's time-area, not r2's.
        var laneRows = cut.FindAll("[data-calee-region='lane-row']");
        Assert.Equal(2, laneRows.Count);
        Assert.Single(laneRows[0].QuerySelectorAll(".calee-scheduler-timeline-event"));
        Assert.Empty(laneRows[1].QuerySelectorAll(".calee-scheduler-timeline-event"));
    }

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) ---------------------------------

    [Fact]
    public void TimelineDragToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed.
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var slot = cut.Find(".calee-scheduler-timeline-slot");
        var classAttr = slot.GetAttribute("class") ?? string.Empty;
        Assert.DoesNotContain("calee-scheduler-timeline-slot--create-affordance", classAttr);
    }

    [Fact]
    public void TimelineDragToCreate_Enabled_AttachesGridBackgroundHandler()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToCreate, true));

        var slots = cut.FindAll(".calee-scheduler-timeline-slot");
        Assert.NotEmpty(slots);
        foreach (var s in slots)
        {
            var classAttr = s.GetAttribute("class") ?? string.Empty;
            Assert.Contains("calee-scheduler-timeline-slot--create-affordance", classAttr);
        }
    }

    [Fact]
    public async Task TimelineDragToCreate_Drop_NoCancel_FiresOnEventCreated_WithSnappedStartEnd()
    {
        // Day mode: 10 hours visible (8..18), slot=30 min → 20 slots per row.
        // Anchor row=0 (Lane r1), anchor slot=4 (10:00). DeltaX = 28 (slotWidthPx = 560/20 = 28 → +1 slot).
        // Wait — gridWidthPx fallback is 56*(EndHour-StartHour) = 560 → slotWidthPx = 28.
        // Expected: 10:00 → 11:00, LaneId=r1.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 28, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(0, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
        Assert.Equal("r1", captured.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDragToCreate_BidirectionalDrag_NormalizesStartLessThanEnd()
    {
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Anchor slot 6 (11:00), DeltaX=-56 (-2 slots) → final slot 4 (10:00).
        // Normalized: Start = 10:00, End = 11:30 (covers slots 4..6).
        var payload = new DropPayload(0, 0, -56, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(0, 6, payload));

        Assert.NotNull(captured);
        Assert.True(captured!.Slot.Start < captured.Slot.End);
        Assert.Equal(10, captured.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task TimelineDragToCreate_SnapsToSlot_OnNonAlignedHorizontalDrop()
    {
        // DeltaX=20 (between 0 and 28) → AwayFromZero rounds to +1 slot.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 20, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(0, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal(0, captured!.Slot.Start.Minute % 30);
        Assert.Equal(0, captured.Slot.End.Minute % 30);
    }

    [Fact]
    public async Task TimelineDragToCreate_LaneIdMatchesAnchorRow()
    {
        // Anchor row = r2 → LaneId on the produced slot = "r2".
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1"), LaneOf("r2", "Lane 2") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 28, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(1, 4, payload));

        Assert.NotNull(captured);
        Assert.Equal("r2", captured!.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDragToCreate_AnchorInUnassignedRow_LaneIdIsNull()
    {
        // Anchor on the unassigned row → LaneId = null. We provide one unassigned event
        // so the unassigned row is auto-shown.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var unassignedEvent = Timed("u", 9, 10);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            // KeyMap with no mappings → every event returns null → unassigned.
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.Events, new[] { unassignedEvent })
            .Add(c => c.ShowUnassignedRow, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // The unassigned row is the second row (index 1) when ShowUnassignedRow=true
        // and there's an unassigned event.
        var payload = new DropPayload(0, 0, 28, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(1, 4, payload));

        Assert.NotNull(captured);
        Assert.Null(captured!.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDragToCreate_Cancel_True_NoPersistedState()
    {
        using var ctx = NewContext();

        var cancelHandler = EventCallback.Factory.Create<EventCreateContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated, cancelHandler));

        var payload = new DropPayload(0, 0, 28, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(0, 4, payload));

        // Option A: no library-rendered chip exists for a create.
        Assert.Empty(cut.FindAll(".calee-scheduler-timeline-event"));
    }

    [Fact]
    public async Task TimelineDragToCreate_Disallows_Start_On_ExistingEventChip()
    {
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var eventClickFired = false;
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, _ => eventClickFired = true))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        await cut.InvokeAsync(() => chip.Click());

        Assert.True(eventClickFired);
        Assert.False(createFired);
    }

    [Fact]
    public async Task TimelineDragToCreate_WeekScale_HorizontalDropCoversWholeDays()
    {
        // Week mode: time axis = whole days. Anchor day-cell=2 (Tuesday May 19 in the
        // Sun-start week); DeltaX = 100 (one dayWidthPx fallback) → +1 day → final
        // day=3 (Wednesday). Spanned: Tuesday Start → Wednesday End (i.e., 2 days).
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Week mode fallback gridWidth = 7 * 100 = 700px; dayWidthPx = 100. DeltaX=100 → +1 day.
        var payload = new DropPayload(0, 0, 100, 0, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(0, 2, payload));

        Assert.NotNull(captured);
        // Start = Tuesday May 19 00:00, End = Thursday May 21 00:00 (exclusive end of
        // day cell index 3 = Wednesday → next-day midnight is the canonical exclusive End).
        Assert.Equal(19, captured!.Slot.Start.Day);
        Assert.Equal(0, captured.Slot.Start.Hour);
        Assert.Equal(21, captured.Slot.End.Day);
        Assert.Equal(0, captured.Slot.End.Hour);
        Assert.Equal("r1", captured.Slot.LaneId);
    }

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -------------------------

    [Fact]
    public async Task TimelineDoubleClickToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed. bUnit raises MissingEventHandlerException when the
        // ondblclick binding is absent — that's the proof.
        using var ctx = NewContext();

        var fired = false;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        var slot = cut.Find(".calee-scheduler-timeline-slot");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => slot.DoubleClick()));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0, 0));
        Assert.False(fired);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_DayScale_UsesSlotMinutesWhenOptionNull()
    {
        // Day scale, 30-min slot → null option resolves to 30-min default. Anchor row=0,
        // slot 4 = 10:00 → End = 10:30.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0, 4));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(10, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
        Assert.Equal("r1", captured.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_LaneIdMatchesClickedRow()
    {
        // Two lanes — row 1 is "r2". The Slot.LaneId on the created event must be "r2".
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1"), LaneOf("r2", "Lane 2") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(1, 0));

        Assert.NotNull(captured);
        Assert.Equal("r2", captured!.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_AnchorInUnassignedRow_LaneIdIsNull()
    {
        // ShowUnassignedRow=true with at least one unassigned event → the unassigned
        // row materializes at the bottom. The double-click anchored there carries
        // LaneId=null on the produced Slot.
        using var ctx = NewContext();

        // An event whose LaneKey returns null lands in the unassigned bucket and
        // causes the unassigned row to render. Without it, ShowUnassignedRow=true is
        // a no-op (see CaleeSchedulerTimelineView's per-row materialization).
        var orphanEv = Timed("orphan", 9, 10);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, _ => null) // every event → unassigned.
            .Add(c => c.ShowUnassignedRow, true)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { orphanEv })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Row 1 is the unassigned row (rows: [r1, unassigned]).
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(1, 0));

        Assert.NotNull(captured);
        Assert.Null(captured!.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_WeekScale_UsesDefault1440MinutesWhenOptionNull()
    {
        // Week scale → null option resolves to 1440 min (one day). Day-cell index 2 in
        // a Sunday-start week containing Tue May 19 = Tue May 19 itself.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Week)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Day-cell index 2 → Tuesday May 19.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0, 2));

        Assert.NotNull(captured);
        Assert.Equal(19, captured!.Slot.Start.Day);
        Assert.Equal(0, captured.Slot.Start.Hour);
        Assert.Equal(captured.Slot.Start.AddDays(1), captured.Slot.End);
        Assert.Equal("r1", captured.Slot.LaneId);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_MonthScale_UsesDefault1440MinutesWhenOptionNull()
    {
        // Month scale → null option resolves to 1440 min.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Month)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Cell 0 of May 2026 = May 1.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0, 0));

        Assert.NotNull(captured);
        Assert.Equal(captured!.Slot.Start.AddDays(1), captured.Slot.End);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_RespectsExplicit_DefaultCreateDurationMinutes()
    {
        // Explicit option = 90 min overrides the Day-scale slot default and the
        // Week/Month-scale 1440 default uniformly.
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultCreateDurationMinutes = 90);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap())
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(0, 4));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task TimelineDoubleClickToCreate_Disabled_OnExistingEventChip()
    {
        // Event chips render in their own grouping cell (a sibling layer to the slot
        // cells). The chip carries no @ondblclick handler — bUnit raises
        // MissingEventHandlerException on the dispatch, which is exactly the proof.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, KeyMap(("e", "r1")))
            .Add(c => c.ShowUnassignedRow, false)
            .Add(c => c.TimeScale, TimelineScale.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => chip.DoubleClick()));

        Assert.False(createFired);
    }

    [Fact]
    public void TimelineView_FourOverlappingInLane_RendersOverlapBlock_AndFiresOverlapContext()
    {
        DayOverflowContext<CalendarEvent>? captured = null;
        var day = Anchor;
        ICalendarEvent Mk(string id) => new CalendarEvent(id, id, day.AddHours(9), day.AddHours(10));
        var evs = new[] { (CalendarEvent)Mk("a"), (CalendarEvent)Mk("b"), (CalendarEvent)Mk("c"), (CalendarEvent)Mk("d") };
        var lanes = new ILane[] { LaneOf("L1", "Lane 1") };

        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, day)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, evs)
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var block = cut.Find(".calee-scheduler-overlap-block");
        Assert.Equal("+2", block.TextContent.Trim());
        block.Click();
        Assert.Equal(OverflowKind.Overlap, captured!.Kind);
        Assert.Equal(2, captured.Events.Count);
        Assert.Equal("L1", captured.LaneId);
    }
}
