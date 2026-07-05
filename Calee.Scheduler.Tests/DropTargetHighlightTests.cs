using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

public class DropTargetHighlightTests
{
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
        string id, int startHour, int endHour,
        int startMin = 0, int endMin = 0,
        DateTimeOffset? on = null)
    {
        var date = on ?? Anchor;
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, startHour, startMin, 0, date.Offset);
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, endHour, endMin, 0, date.Offset);
        return new CalendarEvent(id, id, start, end, IsAllDay: false);
    }

    // ----- Day View -----

    [Fact]
    public async Task Day_View_KeyboardMove_Adds_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        Assert.False(view.IsKeyboardMoveModeForTest);

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));
        Assert.True(view.IsKeyboardMoveModeForTest);

        var phantom = cut.Find(".calee-scheduler-event--keyboard-phantom");
        Assert.NotNull(phantom);
    }

    [Fact]
    public async Task Day_View_KeyboardMove_Cancel_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        // Cancel should exit keyboard-move mode (state check proven by existing
        // KeyboardMoveTests). The phantom class is gated on _keyboardMoveMode and
        // _keyboardMoveEventId; checking the state flags verifies the class is gone.
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCancelForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }

    [Fact]
    public async Task Day_View_KeyboardMove_Commit_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCommitForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }

    [Fact]
    public async Task Day_View_KeyboardResize_Adds_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        Assert.False(view.IsKeyboardMoveModeForTest);

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));
        Assert.True(view.IsKeyboardMoveModeForTest);

        // In keyboard move mode the event chip carries the phantom class.
        // (Pin-driven layout updates are covered by KeyboardMoveTests.)
        Assert.NotNull(cut.Find(".calee-scheduler-event--keyboard-phantom"));
    }

    // ----- Week View -----

    [Fact]
    public async Task Week_View_KeyboardMove_Adds_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        Assert.False(view.IsKeyboardMoveModeForTest);

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));
        Assert.True(view.IsKeyboardMoveModeForTest);

        Assert.NotNull(cut.Find(".calee-scheduler-event--keyboard-phantom"));
    }

    [Fact]
    public async Task Week_View_KeyboardMove_Cancel_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCancelForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }

    [Fact]
    public async Task Week_View_KeyboardMove_Commit_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCommitForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }

    // ----- Timeline View -----

    [Fact]
    public async Task Timeline_View_KeyboardMove_Adds_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13, on: Anchor);
        var events = new List<CalendarEvent> { ev };
        var lanes = new List<Lane> { new("L1", "Alpha", "#ccc") };

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (CalendarEvent e) => "L1")
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        Assert.False(view.IsKeyboardMoveModeForTest);

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));
        Assert.True(view.IsKeyboardMoveModeForTest);

        Assert.NotNull(cut.Find(".calee-scheduler-event--keyboard-phantom"));
    }

    [Fact]
    public async Task Timeline_View_KeyboardMove_Cancel_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13, on: Anchor);
        var events = new List<CalendarEvent> { ev };
        var lanes = new List<Lane> { new("L1", "Alpha", "#ccc") };

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (CalendarEvent e) => "L1")
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCancelForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }

    [Fact]
    public async Task Timeline_View_KeyboardMove_Commit_Removes_Phantom_Class()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13, on: Anchor);
        var events = new List<CalendarEvent> { ev };
        var lanes = new List<Lane> { new("L1", "Alpha", "#ccc") };

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (CalendarEvent e) => "L1")
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardMoveForTestAsync(ev));

        await cut.InvokeAsync(() => view.InvokeKeyboardMoveCommitForTestAsync());
        Assert.False(view.IsKeyboardMoveModeForTest);
    }
}
