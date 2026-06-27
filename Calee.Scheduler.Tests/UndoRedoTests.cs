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
/// Tests for the Phase 2 Task 13 undo/redo trigger surface (FR-35 keyboard triggers;
/// FR-29 AllowUndoRedo fail-closed gating). Covers per-view Cmd+Z / Ctrl+Z fires
/// OnUndoRequested, Cmd+Shift+Z / Ctrl+Y fires OnRedoRequested, AllowUndoRedo=false
/// is a no-op, undo/redo dispatch doesn't mutate selection or fire OnEventClicked,
/// modifier discrimination (plain Z / Shift+Z don't fire), and fire-count
/// discipline (no coalescing). Companion to <see cref="SelectionKeyboardTests"/>
/// (Task 11's Space/Esc) and <see cref="SelectionDeleteTests"/> (Task 12's Delete).
/// </summary>
public class UndoRedoTests
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

    private static CalendarEvent Timed(string id, int startHour, int endHour, DateTimeOffset? on = null)
    {
        var date = on ?? Anchor;
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, startHour, 0, 0, date.Offset);
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, endHour, 0, 0, date.Offset);
        return new CalendarEvent(id, id, start, end, IsAllDay: false);
    }

    private static ILane LaneOf(string id, string name) => new Lane(id, name);

    private static KeyboardEventArgs CtrlZ() => new() { Key = "z", CtrlKey = true };
    private static KeyboardEventArgs CmdZ() => new() { Key = "z", MetaKey = true };
    private static KeyboardEventArgs CtrlShiftZ() => new() { Key = "Z", CtrlKey = true, ShiftKey = true };
    private static KeyboardEventArgs CmdShiftZ() => new() { Key = "Z", MetaKey = true, ShiftKey = true };
    private static KeyboardEventArgs CtrlY() => new() { Key = "y", CtrlKey = true };
    private static KeyboardEventArgs CmdY() => new() { Key = "y", MetaKey = true };

    // ───────────────────────────────────────────────────────────────────────────
    // Per-view single binding fires: Cmd+Z / Ctrl+Z on a focused chip fires
    // OnUndoRequested. Cmd+Shift+Z and Ctrl+Y fire OnRedoRequested.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_CmdZ_On_Chip_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task Day_CtrlZ_On_Chip_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlZ()));

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task Day_CmdShiftZ_On_Chip_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdShiftZ()));

        Assert.Equal(1, redoCount);
    }

    [Fact]
    public async Task Day_CtrlY_On_Chip_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlY()));

        Assert.Equal(1, redoCount);
    }

    [Fact]
    public async Task Week_CmdZ_On_Chip_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task Week_CtrlShiftZ_On_Chip_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlShiftZ()));

        Assert.Equal(1, redoCount);
    }

    [Fact]
    public async Task Month_CmdZ_On_Chip_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task Month_CtrlY_On_Chip_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown(CtrlY()));

        Assert.Equal(1, redoCount);
    }

    [Fact]
    public async Task Timeline_CmdZ_On_Chip_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task Timeline_CmdShiftZ_On_Chip_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown(CmdShiftZ()));

        Assert.Equal(1, redoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Grid binding — Cmd+Z on the grid container (not a chip) also fires
    // OnUndoRequested. Day view is sufficient — the routing logic is the same
    // shape across interactive views (TryDispatchUndoRedoAsync is called from
    // each view's HandleGridKeyDownAsync near the top).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_CmdZ_On_Grid_Container_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        // Find the role="grid" container (the hour-grid wrapper).
        await cut.InvokeAsync(() => cut.Find("[role='grid']").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // FR-29 fail-closed: AllowUndoRedo defaults false; Cmd+Z / Ctrl+Y on a
    // focused chip do NOT fire either callback. Pin gate ordering — the
    // AllowUndoRedo gate is checked inside TryDispatchUndoRedoAsync, before
    // anything else; IsDragActive precedes it at the call site (mirrors
    // Task 11 / Task 12 precedent).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_UndoRedo_With_AllowUndoRedo_False_Is_NoOp_Even_Without_Drag()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            // AllowUndoRedo intentionally omitted — defaults to false.
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        // Cmd+Z, Ctrl+Z, Cmd+Shift+Z, Ctrl+Y — none fire.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlZ()));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdShiftZ()));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlY()));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    [Fact]
    public async Task Week_UndoRedo_With_AllowUndoRedo_False_Is_NoOp()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlY()));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    [Fact]
    public async Task Month_UndoRedo_With_AllowUndoRedo_False_Is_NoOp()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown(CmdZ()));
        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown(CtrlY()));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    [Fact]
    public async Task Timeline_UndoRedo_With_AllowUndoRedo_False_Is_NoOp()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

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
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown(CmdZ()));
        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown(CtrlY()));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // AllowUndoRedo defaults false on every view + on the root scheduler
    // (FR-29 inventory; mirrors the AllowDelete / AllowMultiSelect default-
    // false coverage in SelectionDeleteTests).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllowUndoRedo_Defaults_False_On_All_Views_And_Root()
    {
        using var ctx = NewContext();
        var lanes = new[] { LaneOf("L1", "Lane 1") };

        var day = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));
        Assert.False(day.Instance.AllowUndoRedo);

        var week = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));
        Assert.False(week.Instance.AllowUndoRedo);

        var month = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));
        Assert.False(month.Instance.AllowUndoRedo);

        var timeline = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1")));
        Assert.False(timeline.Instance.AllowUndoRedo);

        var root = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ));
        Assert.False(root.Instance.AllowUndoRedo);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Modifier discrimination: plain Z (no modifiers) does NOT fire; Shift+Z
    // (no Cmd/Ctrl) does NOT fire. The modifier check inside
    // TryDispatchUndoRedoAsync requires CtrlKey || MetaKey.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_PlainZ_Without_Modifiers_Does_Not_Fire()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        // Plain "z" — no Ctrl, no Meta, no Shift.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "z" }));
        // Shift+Z — no Ctrl, no Meta. Should not fire either.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "Z", ShiftKey = true }));
        // Plain "y" — no Ctrl, no Meta.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "y" }));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    [Fact]
    public async Task Day_CmdZ_Fires_Undo_Only_Not_Redo()
    {
        // Cmd+Z fires exactly one callback (undo). The redo callback must NOT
        // also fire from the same keystroke.
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
        Assert.Equal(0, redoCount);
    }

    [Fact]
    public async Task Day_CmdShiftZ_Fires_Redo_Only_Not_Undo()
    {
        // Cmd+Shift+Z fires exactly one callback (redo). The undo callback must
        // NOT also fire — the Shift modifier distinguishes the two bindings.
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdShiftZ()));

        Assert.Equal(0, undoCount);
        Assert.Equal(1, redoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Cmd+Y is NOT bound — ADR-0013's canonical map specifies Ctrl+Y only.
    // Cmd+Y on macOS is a system gesture (Yank / Show Downloads / app-
    // specific) the library deliberately does not intercept. Pin this so
    // a future change to TryDispatchUndoRedoAsync that broadens the Y
    // binding accidentally trips the test.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_CmdY_Does_Not_Fire_OnRedoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdY()));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Selection survives — undo/redo dispatch does NOT mutate the selection
    // set or fire OnSelectionChanged. The library is purely emitting a trigger;
    // selection state is orthogonal.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_CmdZ_Does_Not_Mutate_Selection_Or_Fire_OnSelectionChanged()
    {
        using var ctx = NewContext();
        var selectionFires = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selectionFires.Add(s))));

        // Build a selection of [a, b] via Ctrl+click — establishes a non-empty
        // baseline. Then press Cmd+Z on a focused chip and assert the selection
        // is byte-identical AND OnSelectionChanged did not re-fire.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        var preSelection = cut.Instance.EffectiveSelection.ToOrderedList();
        Assert.Equal(new[] { "a", "b" }, preSelection);
        var preFireCount = selectionFires.Count;

        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0].KeyDown(CmdZ()));

        var postSelection = cut.Instance.EffectiveSelection.ToOrderedList();
        Assert.Equal(preSelection, postSelection);
        Assert.Equal(preFireCount, selectionFires.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // OnEventClicked does NOT fire — undo/redo on a focused chip is a different
    // gesture from click. The keydown handler returns early after dispatching
    // the trigger; the regular Enter / Space arms do not run.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_CmdZ_Does_Not_Fire_OnEventClicked()
    {
        using var ctx = NewContext();
        var clicked = new List<CalendarEvent>();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => clicked.Add(e)))
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));

        Assert.Equal(1, undoCount);
        Assert.Empty(clicked);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Fire count discipline — pressing Cmd+Z N times fires OnUndoRequested
    // N times. The library does not coalesce or rate-limit triggers; the
    // consumer's stack walker is the canonical place to decide whether N
    // undos pop N entries.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Three_CmdZ_Presses_Fire_OnUndoRequested_Three_Times()
    {
        using var ctx = NewContext();
        var undoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        for (var i = 0; i < 3; i++)
        {
            await cut.InvokeAsync(() =>
                cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));
        }

        Assert.Equal(3, undoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Root-level forwarding — AllowUndoRedo + OnUndoRequested + OnRedoRequested
    // cascade to the active child view through CaleeScheduler. Press Cmd+Z on
    // the focused chip in the cascaded Day view; the callback fires once on
    // the root.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_CmdZ_On_Active_View_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CmdZ()));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(CtrlY()));

        Assert.Equal(1, undoCount);
        Assert.Equal(1, redoCount);
    }
}
