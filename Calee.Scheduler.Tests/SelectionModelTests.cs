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
/// Tests for the Phase 2 Task 10 selection model (FR-29 fail-closed flag, FR-34
/// selection foundation). Covers per-view click semantics across Day/Week/Month/
/// Timeline, cross-view persistence via <see cref="CaleeScheduler{TEvent}"/>, the
/// re-bucketing persistence requirement called out by the reviewer (selection
/// survives a <c>LaneKey</c> change mid-render), and the <c>OnSelectionChanged</c>
/// fire-vs-no-fire policy.
/// </summary>
public class SelectionModelTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Tuesday 2026-05-19 in America/New_York (EDT -04:00).
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static CalendarEvent Timed(
        string id,
        int startHour,
        int endHour,
        DateTimeOffset? on = null)
    {
        var date = on ?? Anchor;
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, startHour, 0, 0, date.Offset);
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, endHour, 0, 0, date.Offset);
        return new CalendarEvent(id, id, start, end, IsAllDay: false);
    }

    private static CalendarEvent AllDay(string id, DateTimeOffset day)
    {
        var start = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset);
        var end = start.AddDays(1);
        return new CalendarEvent(id, id, start, end, IsAllDay: true);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Day view
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_PlainClick_Sets_SingleSelection()
    {
        using var ctx = NewContext();
        IReadOnlyList<CalendarEvent>? captured = null;

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured = s)));

        var chips = cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal(2, chips.Count);

        // Click chip "a" (first in render order, ordered by start time).
        await chips[0].ClickAsync(new MouseEventArgs());

        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("a", captured[0].Id);

        // The clicked chip is marked selected, the other is not.
        chips = cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Contains("calee-scheduler-event--selected", chips[0].ClassName);
        Assert.Equal("true", chips[0].GetAttribute("aria-selected"));
        Assert.DoesNotContain("calee-scheduler-event--selected", chips[1].ClassName ?? string.Empty);
        Assert.Null(chips[1].GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task Day_CtrlClick_Toggles_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        // Plain-click a → selection = [a]
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        // Ctrl-click b → selection = [a, b]. Re-find: the click above triggered a render
        // and handler IDs are stale on the old chip references.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));

        // Ctrl-click a → toggle off → selection = [b]
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "b" }, events[2].Select(e => e.Id));
    }

    [Fact]
    public async Task Day_MetaClick_Also_Toggles()
    {
        // Cmd on macOS — modeled as MetaKey. Equivalent to Ctrl on Windows.
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { MetaKey = true });

        Assert.Equal(new[] { "a", "b" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Day_ShiftClick_Selects_Range_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var d = Timed("d", 15, 16);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(p2 => p2.TimeZone, TZ)
            .Add(p2 => p2.Date, Anchor)
            .Add(p2 => p2.StartHour, 8)
            .Add(p2 => p2.EndHour, 18)
            .Add(p2 => p2.AllowMultiSelect, true)
            .Add(p2 => p2.Events, new[] { a, b, c, d })
            .Add(p2 => p2.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        // Plain click on b → anchor = b.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs());
        // Shift-click d → selection = [b, c, d]. Re-find: handler IDs are stale.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[3]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "b" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "b", "c", "d" }, events[1].Select(e => e.Id));
    }

    [Fact]
    public async Task Day_AllowMultiSelect_False_Ignores_Ctrl_And_Shift_Modifiers()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            // AllowMultiSelect omitted — defaults false (FR-29 fail-closed).
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        // Each modifier-key click should land as a single-id selection on the clicked event.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "b" }, events[1].Select(e => e.Id));
    }

    [Fact]
    public async Task Day_PlainReClick_Of_Sole_Selected_Event_Does_Not_Fire_OnSelectionChanged()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await chip.ClickAsync(new MouseEventArgs());
        Assert.Single(events);

        // Re-click the sole selected event — no-op, no callback.
        chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await chip.ClickAsync(new MouseEventArgs());
        Assert.Single(events);
    }

    [Fact]
    public async Task Day_PlainClick_Collapses_Multi_To_Single_And_Fires()
    {
        // Multi-selection collapses to single on plain click (different event). FR-34
        // policy: the set changed → fire OnSelectionChanged.
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        // Now selection = [a, b]. Plain click a → collapse to [a].
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());

        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "a" }, events[2].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Week view
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Week_PlainClick_Sets_SingleSelection()
    {
        using var ctx = NewContext();
        IReadOnlyList<CalendarEvent>? captured = null;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured = s)));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await chip.ClickAsync(new MouseEventArgs());

        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("a", captured[0].Id);
    }

    [Fact]
    public async Task Week_CtrlClick_Toggles_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        Assert.Equal(2, cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event").Count);
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(new[] { "a", "b" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Week_ShiftClick_Selects_Range_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(p2 => p2.TimeZone, TZ)
            .Add(p2 => p2.Date, Anchor)
            .Add(p2 => p2.StartHour, 8)
            .Add(p2 => p2.EndHour, 18)
            .Add(p2 => p2.AllowMultiSelect, true)
            .Add(p2 => p2.Events, new[] { a, b, c })
            .Add(p2 => p2.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(new[] { "a", "b", "c" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Week_AllowMultiSelect_False_Ignores_Modifiers()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "b" }, events[1].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Month view
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Month_PlainClick_Sets_SingleSelection()
    {
        using var ctx = NewContext();
        IReadOnlyList<CalendarEvent>? captured = null;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured = s)));

        var chip = cut.Find(".calee-scheduler-month-chip");
        await chip.ClickAsync(new MouseEventArgs());

        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("a", captured[0].Id);
    }

    [Fact]
    public async Task Month_CtrlClick_Toggles_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        Assert.Equal(2, cut.FindAll(".calee-scheduler-month-chip").Count);
        await cut.FindAll(".calee-scheduler-month-chip")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-month-chip")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(new[] { "a", "b" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Month_ShiftClick_Selects_Range_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(p2 => p2.TimeZone, TZ)
            .Add(p2 => p2.Date, Anchor)
            .Add(p2 => p2.AllowMultiSelect, true)
            .Add(p2 => p2.Events, new[] { a, b, c })
            .Add(p2 => p2.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll(".calee-scheduler-month-chip")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-month-chip")[2]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(new[] { "a", "b", "c" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Month_AllowMultiSelect_False_Ignores_Modifiers()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll(".calee-scheduler-month-chip")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll(".calee-scheduler-month-chip")[1]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "b" }, events[1].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Timeline view
    // ───────────────────────────────────────────────────────────────────────────

    private static ILane LaneOf(string id, string name) => new Lane(id, name);

    [Fact]
    public async Task Timeline_PlainClick_Sets_SingleSelection()
    {
        using var ctx = NewContext();
        IReadOnlyList<CalendarEvent>? captured = null;

        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured = s)));

        var chip = cut.Find(".calee-scheduler-timeline-event");
        await chip.ClickAsync(new MouseEventArgs());

        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("a", captured[0].Id);
    }

    [Fact]
    public async Task Timeline_CtrlClick_Toggles_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        Assert.Equal(2, cut.FindAll(".calee-scheduler-timeline-event").Count);
        await cut.FindAll(".calee-scheduler-timeline-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-timeline-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(new[] { "a", "b" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Timeline_ShiftClick_Selects_Range_When_AllowMultiSelect_True()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(p2 => p2.TimeZone, TZ)
            .Add(p2 => p2.Date, Anchor)
            .Add(p2 => p2.StartHour, 8)
            .Add(p2 => p2.EndHour, 18)
            .Add(p2 => p2.Lanes, lanes)
            .Add(p2 => p2.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(p2 => p2.AllowMultiSelect, true)
            .Add(p2 => p2.Events, new[] { a, b, c })
            .Add(p2 => p2.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll(".calee-scheduler-timeline-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-timeline-event")[2]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(new[] { "a", "b", "c" }, events[^1].Select(e => e.Id));
    }

    [Fact]
    public async Task Timeline_AllowMultiSelect_False_Ignores_Modifiers()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.FindAll(".calee-scheduler-timeline-event")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll(".calee-scheduler-timeline-event")[1]
            .ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "b" }, events[1].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Reviewer rec #3 — re-bucketing persistence
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeline_SelectedEvent_LaneId_Mutation_Preserves_Selection_And_Order()
    {
        // Reviewer focus row preservation concern (commit kickoff). The focus row clamp at
        // CaleeSchedulerTimelineView.razor.cs:416 runs every OnParametersSet via
        // ComputeLayout — confirm that re-bucketing a selected event (its LaneKey
        // projection moves to another lane) leaves the selection set unchanged AND
        // preserves anchor order. Selection lives by id, not by row.
        using var ctx = NewContext();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);

        var lanes = new[] { LaneOf("L1", "Lane 1"), LaneOf("L2", "Lane 2") };

        // Initial: a → L1, b → L1, c → L2. Select a then b (ctrl) → selection = [a, b].
        var laneMap = new Dictionary<string, string> { { "a", "L1" }, { "b", "L1" }, { "c", "L2" } };
        Func<CalendarEvent, string?> laneKey = e => laneMap[e.Id];
        var captured = new List<IReadOnlyList<CalendarEvent>>();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, laneKey)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured.Add(s))));

        // Select a (anchor) then ctrl-click b.
        Assert.Equal(3, cut.FindAll(".calee-scheduler-timeline-event").Count);
        // The chip at DOM index 0 is a (Lane 1 row, earliest start).
        await cut.FindAll(".calee-scheduler-timeline-event")[0]
            .ClickAsync(new MouseEventArgs());
        // After re-render, the chip at DOM index 1 is b (Lane 1 row, 2nd in row).
        await cut.FindAll(".calee-scheduler-timeline-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(2, captured.Count);
        Assert.Equal(new[] { "a", "b" }, captured[1].Select(e => e.Id));

        // Capture the view's selection contents directly (internal accessor).
        var preSelection = cut.Instance.EffectiveSelection.ToOrderedList();
        Assert.Equal(new[] { "a", "b" }, preSelection);

        // Now mutate b's lane projection. Consumer's data update: b → L2. Push a new
        // event list (parameter set) so OnParametersSet runs end-to-end, ComputeLayout
        // re-buckets, and the focus-row clamp fires.
        laneMap["b"] = "L2";
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, laneKey)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => captured.Add(s))));

        // Selection unchanged after re-bucket.
        var postSelection = cut.Instance.EffectiveSelection.ToOrderedList();
        Assert.Equal(new[] { "a", "b" }, postSelection);
        // Anchor order preserved (b last).
        Assert.Equal("b", cut.Instance.EffectiveSelection.Anchor);

        // OnSelectionChanged should NOT have fired again as a result of the lane move
        // (the selection set didn't change; lane re-bucket is a layout concern only).
        Assert.Equal(2, captured.Count);

        // Both selected events still render with the selected visual class — even
        // though b is now in Lane 2's row.
        var selectedChips = cut.FindAll(".calee-scheduler-timeline-event--selected");
        Assert.Equal(2, selectedChips.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Cross-view persistence — selection survives a View switch on the root
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Selection_Persists_Across_View_Switch_On_Root_Scheduler()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        // Select a + b in Day view.
        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a", "b" }, events[^1].Select(e => e.Id));

        // Switch to Week — same events should remain selected. The cascaded state
        // container survives view swaps (its reference identity is stable), so the
        // new view reads the same Selection and renders the selected chips with the
        // visual marker.
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        var weekSelected = cut.FindAll(".calee-scheduler-week .calee-scheduler-event--selected");
        Assert.Equal(2, weekSelected.Count);

        // The state container holds the same set.
        Assert.Equal(new[] { "a", "b" },
            cut.Instance.State.Selection.ToOrderedList());

        // Switching to Month and back continues to round-trip the selection.
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        var monthSelected = cut.FindAll(".calee-scheduler-month .calee-scheduler-month-chip--selected");
        Assert.Equal(2, monthSelected.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // FR-29 fail-closed default
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllowMultiSelect_Defaults_False_On_Root_Scheduler()
    {
        // FR-29: every drag/selection opt-in defaults to false. The root scheduler
        // must reflect that on construction without the consumer specifying anything.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.False(cut.Instance.AllowMultiSelect);
    }

    [Fact]
    public void AllowMultiSelect_Defaults_False_On_Every_View()
    {
        // FR-29 fail-closed propagation: every view's parameter defaults to false too.
        using var ctx = NewContext();
        var dayCut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor));
        var weekCut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor));
        var monthCut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor));
        var timelineCut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("L1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1")));

        Assert.False(dayCut.Instance.AllowMultiSelect);
        Assert.False(weekCut.Instance.AllowMultiSelect);
        Assert.False(monthCut.Instance.AllowMultiSelect);
        Assert.False(timelineCut.Instance.AllowMultiSelect);
    }

    [Fact]
    public async Task Empty_Selection_Fires_With_Empty_List_When_Anchor_And_Click_Match_Anchor()
    {
        // Edge case: ctrl-click the sole selected item → toggles off → selection
        // becomes empty → OnSelectionChanged should fire with an empty list (the set
        // changed from {a} to {}).
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await chip.ClickAsync(new MouseEventArgs());
        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Empty(events[1]);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // OnEventClicked still fires alongside selection mutation
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Click_Fires_Both_OnEventClicked_And_OnSelectionChanged()
    {
        // The Phase 1 OnEventClicked contract must not regress: it fires on every click
        // alongside the new OnSelectionChanged callback. Consumers using only
        // OnEventClicked see no difference; consumers wiring both see both fire.
        using var ctx = NewContext();
        CalendarEvent? clicked = null;
        IReadOnlyList<CalendarEvent>? selected = null;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => clicked = e))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selected = s)));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await chip.ClickAsync(new MouseEventArgs());

        Assert.NotNull(clicked);
        Assert.Equal("a", clicked!.Id);
        Assert.NotNull(selected);
        Assert.Equal(new[] { "a" }, selected!.Select(e => e.Id));
    }
}
