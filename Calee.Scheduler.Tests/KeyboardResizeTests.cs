using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

public class KeyboardResizeTests
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
    public async Task Day_View_KeyboardResize_Extend_IncreasesEnd()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 12);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        Assert.NotNull(captured);
        Assert.Equal(ev.End.AddMinutes(30), captured!.NewEnd);
    }

    [Fact]
    public async Task Day_View_KeyboardResize_Shrink_DecreasesEnd()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 14);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Shrink));

        Assert.NotNull(captured);
        Assert.Equal(ev.End.AddMinutes(-30), captured!.NewEnd);
    }

    [Fact]
    public async Task Day_View_KeyboardResize_MinimumDuration_OneSlot()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 11);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Shrink));
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Shrink));

        Assert.NotNull(captured);
        Assert.True(captured!.NewEnd > ev.Start);
        Assert.Equal(ev.Start.AddMinutes(30), captured.NewEnd);
    }

    [Fact]
    public async Task Day_View_KeyboardResize_Cancel_Flag_Reverts()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 12);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => ctx2.Cancel = true)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        var pin = view.GetOptimisticPin(ev.Id);
        Assert.Null(pin);
    }

    [Fact]
    public async Task Day_View_KeyboardResize_Payload_IdentifiesFocusedEventAndDirection()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 12);
        var events = new List<CalendarEvent> { ev };
        KeyboardResizeRequest? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnKeyboardResizeRequested, EventCallback.Factory.Create<KeyboardResizeRequest>(this, r => captured = r)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        Assert.NotNull(captured);
        Assert.Equal("one", captured!.Event.Id);
        Assert.Equal(KeyboardResizeDirection.Extend, captured.Direction);
    }

    // ----- Week View -----

    [Fact]
    public async Task Week_View_KeyboardResize_Extend_IncreasesEnd()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 12);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        Assert.NotNull(captured);
        Assert.Equal(ev.End.AddMinutes(30), captured!.NewEnd);
    }

    [Fact]
    public async Task Week_View_KeyboardResize_Shrink_DecreasesEnd()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 14);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Shrink));

        Assert.NotNull(captured);
        Assert.Equal(ev.End.AddMinutes(-30), captured!.NewEnd);
    }

    [Fact]
    public async Task Week_View_KeyboardResize_Cancel_Flag_Reverts()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 12);
        var events = new List<CalendarEvent> { ev };

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => ctx2.Cancel = true)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        var pin = view.GetOptimisticPin(ev.Id);
        Assert.Null(pin);
    }

    // ----- Timeline View -----

    [Fact]
    public async Task Timeline_View_KeyboardResize_Extend_IncreasesEnd()
    {
        using var ctx = NewContext();
        var ev = Timed("one", 10, 13);
        var events = new List<CalendarEvent> { ev };
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new List<ILane> { new Lane("lane-one", "Lane One") })
            .Add(c => c.LaneKey, e => e.Id == "one" ? "lane-one" : null)
            .Add(c => c.Events, events)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, ctx2 => captured = ctx2)));

        var view = cut.Instance;
        await cut.InvokeAsync(() => view.InvokeKeyboardResizeForTestAsync(ev, KeyboardResizeDirection.Extend));

        Assert.NotNull(captured);
        Assert.Equal(ev.End.AddMinutes(30), captured!.NewEnd);
    }
}
