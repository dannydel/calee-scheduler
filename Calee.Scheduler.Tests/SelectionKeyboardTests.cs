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
/// Tests for the Phase 2 Task 11 multi-select keyboard surface (FR-34 keyboard
/// equivalents; FR-29 fail-closed gating). Covers per-view Space-toggles-selection,
/// per-view Esc-clears-selection, and the focus / drag / non-Esc-key precedence
/// guards. Companion to <see cref="SelectionModelTests"/> (Task 10's mouse-click
/// surface).
/// </summary>
public class SelectionKeyboardTests
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

    private static ILane LaneOf(string id, string name) => new Lane(id, name);

    // ───────────────────────────────────────────────────────────────────────────
    // Space → toggle in/out of selection (FR-34, AllowMultiSelect=true)
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Space_Toggles_Focused_Event_When_AllowMultiSelect_True()
    {
        // Two timed chips. Space on chip A → selection = [a]. Space on chip B →
        // selection = [a, b] (toggle on moves anchor to b — same semantics as
        // Ctrl+click). Space on b again → toggle off → selection = [a].
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

        // Space on chip a → selection = [a] (toggle-on; was empty).
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0].KeyDown(" "));

        Assert.Single(events);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));

        // Space on chip b → selection = [a, b] (anchor moved to b).
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown(" "));

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));

        // Space on chip b again → toggle off → selection = [a].
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown(" "));

        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "a" }, events[2].Select(e => e.Id));
    }

    [Fact]
    public async Task Week_Space_Toggles_Focused_Event_When_AllowMultiSelect_True()
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

        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0].KeyDown(" "));
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown(" "));

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));
    }

    [Fact]
    public async Task Month_Space_Toggles_Focused_Event_When_AllowMultiSelect_True()
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

        await cut.InvokeAsync(() => cut.FindAll(".calee-scheduler-month-chip")[0].KeyDown(" "));
        await cut.InvokeAsync(() => cut.FindAll(".calee-scheduler-month-chip")[1].KeyDown(" "));

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));

        // Toggle off — anchor remains 'a' (the older insertion).
        await cut.InvokeAsync(() => cut.FindAll(".calee-scheduler-month-chip")[1].KeyDown(" "));
        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "a" }, events[2].Select(e => e.Id));
    }

    [Fact]
    public async Task Timeline_Space_Toggles_Focused_Event_When_AllowMultiSelect_True()
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
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.InvokeAsync(() => cut.FindAll(".calee-scheduler-timeline-event")[0].KeyDown(" "));
        await cut.InvokeAsync(() => cut.FindAll(".calee-scheduler-timeline-event")[1].KeyDown(" "));

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Space with AllowMultiSelect=false — keydown handler no-ops; the browser's
    // default Space-activates-button fires the synthesized click which lands in
    // the existing click path (single-id selection). bUnit's KeyDown helper does
    // not simulate the browser's default-activate step (it only triggers the
    // @onkeydown handler), so we assert here that the keydown handler itself
    // does NOT fire OnSelectionChanged when the consumer hasn't opted in — i.e.,
    // the in-handler short-circuit holds. The browser-default-then-click path
    // is covered by SelectionModelTests's plain-click coverage.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Space_With_AllowMultiSelect_False_Does_Not_Mutate_Selection_In_Keydown_Handler()
    {
        // FR-29 fail-closed gate: ApplySpaceToggleSelectionAsync returns early when
        // AllowMultiSelect is false, leaving selection untouched. The browser then
        // dispatches the synthesized click on key-up which hits @onclick →
        // HandleEventClickAsync → single-id selection. bUnit can't simulate the
        // browser-default click step from KeyDown alone, so this test asserts the
        // pre-default state: the keydown handler itself didn't mutate selection.
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            // AllowMultiSelect not set (defaults false).
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(" "));

        // Keydown handler short-circuited: no OnSelectionChanged fire.
        Assert.Empty(events);
        Assert.Empty(cut.Instance.EffectiveSelection);

        // Sanity: a real click (the browser-default-then-click path) WOULD produce
        // a single-id selection. This branch is what consumers without
        // AllowMultiSelect rely on for Space-to-activate.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .ClickAsync(new MouseEventArgs()));
        Assert.Single(events);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Esc → clear selection (FR-34)
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Esc_Clears_NonEmpty_Selection_From_Event_Chip()
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

        // Build a two-event selection via Ctrl+click.
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a", "b" }, events[1].Select(e => e.Id));

        // Esc on the focused chip clears the selection.
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[1].KeyDown("Escape"));

        Assert.Equal(3, events.Count);
        Assert.Empty(events[2]);
        Assert.Empty(cut.Instance.EffectiveSelection);
    }

    [Fact]
    public async Task Day_Esc_From_Grid_Clears_NonEmpty_Selection()
    {
        // Esc can also be received at the grid level when the user has tabbed off
        // an event chip back onto the slot grid. The same selection-clear rule
        // applies — the cascaded selection lives one level above the active focus
        // target so it doesn't matter which descendant fired the keydown.
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

        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());

        Assert.Equal(new[] { "a" }, events[^1].Select(e => e.Id));

        // Esc at the grid (role='grid' wrapper around the hour grid).
        await cut.InvokeAsync(() => cut.Find("[role='grid']").KeyDown("Escape"));

        Assert.Equal(2, events.Count);
        Assert.Empty(events[1]);
    }

    [Fact]
    public async Task Day_Esc_With_Empty_Selection_Does_Not_Fire_OnSelectionChanged()
    {
        // FR-30 fallback: when no selection is held, Esc falls through to the
        // existing blur behavior — no selection change, no callback fire. The
        // browser doesn't have a default Esc action on a button so there's
        // nothing to preventDefault either.
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

        // No prior click → selection is empty.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Escape"));
        await cut.InvokeAsync(() => cut.Find("[role='grid']").KeyDown("Escape"));

        Assert.Empty(events);
        Assert.Empty(cut.Instance.EffectiveSelection);
    }

    [Fact]
    public async Task Week_Esc_Clears_NonEmpty_Selection()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());

        Assert.Equal(new[] { "a" }, events[^1].Select(e => e.Id));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Escape"));

        Assert.Empty(events[^1]);
    }

    [Fact]
    public async Task Month_Esc_Clears_NonEmpty_Selection()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.Find(".calee-scheduler-month-chip").ClickAsync(new MouseEventArgs());
        Assert.Equal(new[] { "a" }, events[^1].Select(e => e.Id));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-month-chip").KeyDown("Escape"));
        Assert.Empty(events[^1]);
    }

    [Fact]
    public async Task Timeline_Esc_Clears_NonEmpty_Selection()
    {
        using var ctx = NewContext();
        var events = new List<IReadOnlyList<CalendarEvent>>();

        var a = Timed("a", 9, 10);
        var lanes = new[] { LaneOf("L1", "Lane 1") };
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Lanes, lanes)
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "L1"))
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => events.Add(s))));

        await cut.Find(".calee-scheduler-timeline-event").ClickAsync(new MouseEventArgs());
        Assert.Equal(new[] { "a" }, events[^1].Select(e => e.Id));

        await cut.InvokeAsync(() => cut.Find(".calee-scheduler-timeline-event").KeyDown("Escape"));
        Assert.Empty(events[^1]);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Esc precedence — handler must not swallow other keys (Delete, Tab, etc.)
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_NonEsc_Keys_Do_Not_Clear_Selection()
    {
        // Defensive: the Esc handler must only fire on "Escape" — Delete (Task 12),
        // Tab, ArrowRight, and other distinct keys must not invoke the
        // clear-selection branch. Selection remains intact across these keys.
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

        await cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .ClickAsync(new MouseEventArgs());

        Assert.Equal(new[] { "a" }, events[^1].Select(e => e.Id));
        var preCount = events.Count;

        // Delete on a focused chip — Task 12 will wire this to OnEventDeleted; for
        // now it must be a no-op from the Esc handler's perspective (selection
        // unchanged, no fire).
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Delete"));
        // Tab — passes through; no selection change.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Tab"));
        // ArrowRight — handled by the grid for cell navigation, but on the chip
        // it's not bound; selection unchanged.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("ArrowRight"));

        Assert.Equal(preCount, events.Count);
        Assert.Equal(new[] { "a" }, cut.Instance.EffectiveSelection.ToOrderedList());
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Fire-count discipline: Space toggle-on then toggle-off fires twice;
    // Esc-on-empty fires zero times; Esc-on-non-empty fires once.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Space_Toggle_OnOff_Fires_Twice_And_Esc_On_Empty_Fires_Zero()
    {
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

        // Space on, Space off — two fires.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(" "));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(" "));

        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "a" }, events[0].Select(e => e.Id));
        Assert.Empty(events[1]);

        // Esc on the now-empty selection — zero additional fires.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Escape"));
        await cut.InvokeAsync(() => cut.Find("[role='grid']").KeyDown("Escape"));

        Assert.Equal(2, events.Count);

        // Build a new selection then Esc — exactly one additional fire.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(" "));
        Assert.Equal(3, events.Count);
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown("Escape"));
        Assert.Equal(4, events.Count);
        Assert.Empty(events[3]);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Shift+Arrow regression — §5.2 Q10 locks Shift+ArrowUp/Down to "resize"
    // (Task 14). Until then, the binding does nothing selection-related — this
    // test guards against any future drift that would silently start mutating
    // selection on Shift+Arrow.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Shift_Arrow_Does_Not_Mutate_Selection_Today()
    {
        // Per §5.2 Q10 + ADR-0013, Shift+ArrowUp/Down is the future resize binding.
        // The phase-2-plan §6.3 mention of "Shift+Arrow" pre-dates that decision;
        // Task 11 deliberately skips the binding. Selection must not move on
        // Shift+Arrow today — both on the chip and at the grid.
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
        var preCount = events.Count;
        var preIds = cut.Instance.EffectiveSelection.ToOrderedList();

        // Shift+ArrowDown/Up — must not extend / mutate selection.
        var args = new KeyboardEventArgs { Key = "ArrowDown", ShiftKey = true };
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0].KeyDown(args));
        args = new KeyboardEventArgs { Key = "ArrowUp", ShiftKey = true };
        await cut.InvokeAsync(() =>
            cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event")[0].KeyDown(args));

        Assert.Equal(preCount, events.Count);
        Assert.Equal(preIds, cut.Instance.EffectiveSelection.ToOrderedList());
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Space matches Ctrl+click semantics — fires OnEventClicked alongside the
    // selection mutation. The keyboard equivalent must not drop the per-event
    // click callback that the Phase 1 Enter/Space path also exposed.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_Space_Fires_Both_OnEventClicked_And_OnSelectionChanged()
    {
        // Ctrl+click currently fires OnEventClicked AND OnSelectionChanged from the
        // same dispatch (see SelectionModelTests.Click_Fires_Both_*). Space, as
        // the keyboard equivalent, must do the same — consumers wiring only
        // OnEventClicked see no regression vs. the Phase 1 Space-activate path;
        // consumers wiring OnSelectionChanged additionally see the typed list.
        using var ctx = NewContext();
        CalendarEvent? clicked = null;
        IReadOnlyList<CalendarEvent>? selected = null;

        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowMultiSelect, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => clicked = e))
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => selected = s)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event").KeyDown(" "));

        Assert.NotNull(clicked);
        Assert.Equal("a", clicked!.Id);
        Assert.NotNull(selected);
        Assert.Equal(new[] { "a" }, selected!.Select(e => e.Id));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Cross-view persistence via the cascaded selection — Esc on the root
    // scheduler still routes through the state container.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_Esc_Clears_Cascaded_Selection_From_Active_View()
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

        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[0]
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[1]
            .ClickAsync(new MouseEventArgs { CtrlKey = true });

        Assert.Equal(new[] { "a", "b" },
            cut.Instance.State.Selection.ToOrderedList());

        // Esc on the focused chip — clears via the cascaded RequestSelectionChange
        // path; the root's single write site fires the consumer callback.
        await cut.InvokeAsync(() =>
            cut.FindAll(".calee-scheduler-day [data-calee-region='event']")[1].KeyDown("Escape"));

        Assert.Empty(cut.Instance.State.Selection);
        Assert.Empty(events[^1]);
    }
}
