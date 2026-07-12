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
/// Tests for the Phase 2 Task 12 delete keyboard surface (FR-33 single delete;
/// FR-34 batch delete; FR-29 fail-closed gating). Covers per-view Delete-fires-
/// callback, per-view AllowDelete=false-is-no-op, Backspace alias, single-vs-
/// batch routing, consumer-cancel-keeps-selection, prune-on-accept, and the
/// drag-active precedence guard. Companion to <see cref="SelectionKeyboardTests"/>
/// (Task 11's Space/Esc keyboard surface).
/// </summary>
public class SelectionDeleteTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const string ModulePath = "./_content/Calee.Scheduler/calee-scheduler.js";
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

    private static ILane LaneOf(string id, string name) => new Lane(id, name);

    // ───────────────────────────────────────────────────────────────────────────
    // Single-event delete: Delete on a focused chip with AllowDelete=true and
    // no multi-event selection fires OnEventDeleted with the focused event.
    // Per-view coverage so the routing in each view's keyboard handler is
    // exercised against its own DOM shape.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Delete_With_AllowDelete_True_Fires_OnEventDeleted_With_Focused_Event()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        // Delete on the second chip (event b) → single-event callback with b.
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown("Delete"));

        Assert.Single(deletes);
        Assert.Equal("b", deletes[0].Event.Id);
    }

    [Fact]
    public async Task Week_Delete_With_AllowDelete_True_Fires_OnEventDeleted_With_Focused_Event()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Single(deletes);
        Assert.Equal("a", deletes[0].Event.Id);
    }

    [Fact]
    public async Task Month_Delete_With_AllowDelete_True_Fires_OnEventDeleted_With_Focused_Event()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown("Delete"));

        Assert.Single(deletes);
        Assert.Equal("a", deletes[0].Event.Id);
    }

    [Fact]
    public async Task Timeline_Delete_With_AllowDelete_True_Fires_OnEventDeleted_With_Focused_Event()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown("Delete"));

        Assert.Single(deletes);
        Assert.Equal("a", deletes[0].Event.Id);
    }

    [Fact]
    public async Task Day_Accepted_Delete_Restores_Grid_Focus_And_Undo_Remains_Dispatchable()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();
        var undoRequests = 0;
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, _ => { }))
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoRequests++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        var focusCall = Assert.Single(module.Invocations["focusActiveGridCell"]);
        var container = Assert.IsType<ElementReference>(focusCall.Arguments[0]);
        Assert.False(string.IsNullOrEmpty(container.Id));

        await cut.InvokeAsync(() => cut.Find("[role='grid']").KeyDown(
            new KeyboardEventArgs { Key = "z", MetaKey = true }));

        Assert.Equal(1, undoRequests);
    }

    [Fact]
    public async Task Day_Canceled_Delete_Does_Not_Restore_Grid_Focus()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => ctxArg.Cancel = true)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // FR-29 fail-closed: Delete on a focused chip with AllowDelete=false is a
    // no-op — no OnEventDeleted fire, no OnEventsDeleted fire, selection
    // unchanged. Per-view coverage so each view's gate is exercised.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Delete_With_AllowDelete_False_Does_Not_Fire_OnEventDeleted()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            // AllowDelete not set (defaults false).
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg)))
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg)))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Build a one-event selection first.
        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());
        Assert.Single(selections);

        // Delete on the focused chip — must not fire either callback.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Empty(deletes);
        Assert.Empty(batchDeletes);
        // Selection unchanged: still [a].
        Assert.Equal(new[] { "a" }, cut.Instance.EffectiveSelection.ToOrderedList());
        Assert.Single(selections);
    }

    [Fact]
    public async Task Week_Delete_With_AllowDelete_False_Does_Not_Fire_OnEventDeleted()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Empty(deletes);
    }

    [Fact]
    public async Task Month_Delete_With_AllowDelete_False_Does_Not_Fire_OnEventDeleted()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown("Delete"));

        Assert.Empty(deletes);
    }

    [Fact]
    public async Task Timeline_Delete_With_AllowDelete_False_Does_Not_Fire_OnEventDeleted()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

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
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown("Delete"));

        Assert.Empty(deletes);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // FR-29 default — AllowDelete defaults false on every view and on the root.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllowDelete_Defaults_False_On_Every_View()
    {
        using var ctx = NewContext();
        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };

        var day = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a }));
        Assert.False(day.Instance.AllowDelete);

        var week = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a }));
        Assert.False(week.Instance.AllowDelete);

        var month = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a }));
        Assert.False(month.Instance.AllowDelete);

        var timeline = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a }));
        Assert.False(timeline.Instance.AllowDelete);

        var root = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a }));
        Assert.False(root.Instance.AllowDelete);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Backspace alias (macOS): pressing Backspace on a focused chip with
    // AllowDelete=true triggers the same delete dispatch as Delete. We cover
    // Day view here; the routing in the other three views matches by shape.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Backspace_With_AllowDelete_True_Fires_OnEventDeleted()
    {
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Backspace"));

        Assert.Single(deletes);
        Assert.Equal("a", deletes[0].Event.Id);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Batch delete (FR-34): selection set of two-or-more events + focused chip
    // is in the set → OnEventsDeleted fires with the full set; OnEventDeleted
    // does NOT fire (mutually exclusive).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Batch_Delete_Fires_OnEventsDeleted_With_All_Selected()
    {
        using var ctx = NewContext();
        var singleDeletes = new List<EventDeleteContext>();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => singleDeletes.Add(ctxArg)))
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg))));

        // Build a three-event selection via Ctrl+click. Re-find on each line —
        // bUnit's render diff replaces event-handler ids between dispatches, so
        // a cached IRefreshableElementCollection from one FindAll won't survive
        // the post-click re-render.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        Assert.Equal(new[] { "a", "b", "c" }, cut.Instance.EffectiveSelection.ToOrderedList());

        // Delete on the focused chip (c, the most recent Ctrl+click) → batch path.
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2].KeyDown("Delete"));

        Assert.Empty(singleDeletes);
        Assert.Single(batchDeletes);
        Assert.Equal(new[] { "a", "b", "c" }, batchDeletes[0].Events.Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Mixed case: selection set holds [a, b], focused chip is c (NOT in
    // selection). Single-event callback fires with c; batch callback does
    // NOT fire; selection [a, b] is preserved.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Delete_Focused_Chip_Not_In_Selection_Fires_Single_Not_Batch()
    {
        using var ctx = NewContext();
        var singleDeletes = new List<EventDeleteContext>();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => singleDeletes.Add(ctxArg)))
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg)))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Selection = [a, b]; focused chip = c (not in selection).
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        Assert.Equal(new[] { "a", "b" }, cut.Instance.EffectiveSelection.ToOrderedList());
        var selectionFiresBefore = selections.Count;

        // Delete on chip c (the focused chip outside the selection).
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2].KeyDown("Delete"));

        Assert.Single(singleDeletes);
        Assert.Equal("c", singleDeletes[0].Event.Id);
        Assert.Empty(batchDeletes);
        // Selection [a, b] preserved — the focused-outside-selection rule means the
        // delete touched only c (which wasn't selected), so the set is unchanged.
        Assert.Equal(new[] { "a", "b" }, cut.Instance.EffectiveSelection.ToOrderedList());
        // No OnSelectionChanged fire — the selection set is byte-identical.
        Assert.Equal(selectionFiresBefore, selections.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Single-event delete with selection of exactly one: the focused chip IS
    // in the selection, but the selection has only one entry, so per the
    // routing rule the single callback fires (batch needs N>=2).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Delete_With_Single_Selection_Fires_Single_Callback()
    {
        using var ctx = NewContext();
        var singleDeletes = new List<EventDeleteContext>();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => singleDeletes.Add(ctxArg)))
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg))));

        // Build a one-event selection.
        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());
        Assert.Equal(new[] { "a" }, cut.Instance.EffectiveSelection.ToOrderedList());

        // Delete on the focused (only) chip → single callback (N<2 means no batch).
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Single(singleDeletes);
        Assert.Equal("a", singleDeletes[0].Event.Id);
        Assert.Empty(batchDeletes);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Consumer cancels: setting ctx.Cancel = true leaves the selection intact.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Batch_Delete_When_Consumer_Cancels_Leaves_Selection_Intact()
    {
        using var ctx = NewContext();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg =>
                {
                    batchDeletes.Add(ctxArg);
                    ctxArg.Cancel = true; // Consumer rejects.
                }))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Build a two-event selection.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        Assert.Equal(new[] { "a", "b" }, cut.Instance.EffectiveSelection.ToOrderedList());
        var selectionFiresBefore = selections.Count;

        // Delete on focused chip (b, in selection) → batch dispatch → consumer cancels.
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown("Delete"));

        Assert.Single(batchDeletes);
        Assert.True(batchDeletes[0].Cancel);
        // Selection intact — the consumer rejected the delete, so the library must
        // not prune anything.
        Assert.Equal(new[] { "a", "b" }, cut.Instance.EffectiveSelection.ToOrderedList());
        Assert.Equal(selectionFiresBefore, selections.Count);
    }

    [Fact]
    public async Task Day_Single_Delete_When_Consumer_Cancels_Leaves_Selection_Intact()
    {
        using var ctx = NewContext();
        var singleDeletes = new List<EventDeleteContext>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg =>
                {
                    singleDeletes.Add(ctxArg);
                    ctxArg.Cancel = true;
                }))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Build a one-event selection (single, in selection → single callback).
        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());
        var selectionFiresBefore = selections.Count;

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Single(singleDeletes);
        Assert.True(singleDeletes[0].Cancel);
        Assert.Equal(new[] { "a" }, cut.Instance.EffectiveSelection.ToOrderedList());
        Assert.Equal(selectionFiresBefore, selections.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Selection prune on accept (FR-34): a successful batch delete empties the
    // selection set and fires OnSelectionChanged exactly once with an empty list.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Batch_Delete_Accept_Empties_Selection_And_Fires_OnSelectionChanged_Once()
    {
        using var ctx = NewContext();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);
        var c = Timed("c", 13, 14);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg)))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Build a three-event selection.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        // 3 fires building the selection.
        Assert.Equal(3, selections.Count);

        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[2].KeyDown("Delete"));

        Assert.Single(batchDeletes);
        // Exactly one additional OnSelectionChanged fire — empty list.
        Assert.Equal(4, selections.Count);
        Assert.Empty(selections[3]);
        // Selection is now empty.
        Assert.Empty(cut.Instance.EffectiveSelection);
    }

    [Fact]
    public async Task Day_Single_Delete_On_Selected_Chip_Prunes_From_Selection()
    {
        // Selection = [a, b]; focused chip = a (in selection); single-callback
        // path because the focused chip is in selection but the SET is of size
        // 2 — wait, that's the batch case. To exercise the "single delete prunes
        // from selection" branch we use AllowMultiSelect=false (or selection
        // size 1). Use single-id selection here.
        using var ctx = NewContext();
        var singleDeletes = new List<EventDeleteContext>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => singleDeletes.Add(ctxArg)))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());
        Assert.Single(selections);
        Assert.Equal(new[] { "a" }, cut.Instance.EffectiveSelection.ToOrderedList());

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Single(singleDeletes);
        // Single delete on a chip that WAS in selection: prune fires the empty
        // OnSelectionChanged exactly once.
        Assert.Equal(2, selections.Count);
        Assert.Empty(selections[1]);
        Assert.Empty(cut.Instance.EffectiveSelection);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Drag-active precedence: when a drag is in flight the C# Delete handler
    // must not fire (the JS module owns the keystroke). Documenting why this
    // is hard to test from bUnit: the JS module is loaded lazily on the first
    // pointerdown that initiates a drag, and bUnit's headless DOM cannot
    // produce real pointer-events. The internal IsDragActive bool is read
    // from PointerDragInterop.IsActive (also internal). The view-level guard
    // is the same `IsDragActive` check Task 11 uses for Esc; the regression
    // pin below targets the keydown handler shape rather than the JS state.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Delete_Handler_Short_Circuits_When_AllowDelete_False_Even_Without_Drag()
    {
        // Sanity-pin the gate ordering: AllowDelete=false short-circuits before
        // the handler ever reaches the IsDragActive check, so toggling drag
        // state can't matter when delete is disabled. Combined with the
        // Day_Delete_With_AllowDelete_False test above, the gate is correctly
        // before the rest of the dispatch.
        using var ctx = NewContext();
        var deletes = new List<EventDeleteContext>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            // AllowDelete defaults false.
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, ctxArg => deletes.Add(ctxArg))));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));

        Assert.Empty(deletes);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Cross-view persistence ripple: a batch delete that empties the selection
    // through the root scheduler survives a view switch — the state container
    // holds an empty selection across Day → Week → Month, with no events
    // re-appearing in the consumer-visible callback list.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_Batch_Delete_Empty_Survives_View_Switch()
    {
        using var ctx = NewContext();
        var batchDeletes = new List<EventsDeletedContext<CalendarEvent>>();
        var selections = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 11, 12);

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnEventsDeleted,
                EventCallback.Factory.Create<EventsDeletedContext<CalendarEvent>>(this, ctxArg => batchDeletes.Add(ctxArg)))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selections.Add(s))));

        // Build a two-event selection via Ctrl+click in Day view.
        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });
        Assert.Equal(new[] { "a", "b" }, cut.Instance.State.Selection.ToOrderedList());

        // Delete on focused chip (b, in selection) → batch path → consumer accepts.
        await cut.InvokeAsync(() =>
            cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[1].KeyDown("Delete"));

        Assert.Single(batchDeletes);
        Assert.Empty(cut.Instance.State.Selection);

        // Switch the view → state container's empty selection should survive.
        cut.Render(p => p.Add(c => c.View, SchedulerView.Week));
        Assert.Empty(cut.Instance.State.Selection);

        cut.Render(p => p.Add(c => c.View, SchedulerView.Month));
        Assert.Empty(cut.Instance.State.Selection);

        // No spurious OnSelectionChanged fires from the view swaps — the cascade's
        // empty selection is byte-identical across views, so the root's
        // HandleRequestSelectionChangeAsync isn't even involved.
        // (Builds were: 2 click-fires + 1 batch-delete-fire = 3.)
        Assert.Equal(3, selections.Count);
    }
}
