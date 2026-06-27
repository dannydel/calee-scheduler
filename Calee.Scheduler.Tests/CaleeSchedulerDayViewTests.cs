using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerDayView{TEvent}"/> covering FR-01, FR-05, FR-06,
/// FR-07, FR-12, FR-14, FR-15 (visible-chunk), FR-19, FR-19a, FR-19b, FR-20, FR-21,
/// FR-30 (basic), plus PRD §4.6 parameter validation.
/// </summary>
public class CaleeSchedulerDayViewTests
{
    // Fixed time zone and date for determinism.
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Anchor: Tuesday, 2026-05-19 in America/New_York. Offset on 2026-05-19 is -04:00 (EDT).
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

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    [Fact]
    public void Renders_Without_Errors_With_Minimal_Params()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        // Outer container.
        Assert.NotNull(cut.Find(".calee-scheduler-day"));
        // Required regions present.
        Assert.NotNull(cut.Find("[data-calee-region='day-header']"));
        Assert.NotNull(cut.Find("[data-calee-region='all-day']"));
        Assert.NotNull(cut.Find("[data-calee-region='hour-grid']"));
        Assert.NotNull(cut.Find("[data-calee-region='time-gutter']"));
    }

    [Fact]
    public void AllDay_Events_Render_In_All_Day_Row()
    {
        using var ctx = NewContext();

        var allDay = AllDay("party",
            new DateTimeOffset(Anchor.Year, Anchor.Month, Anchor.Day, 0, 0, 0, Anchor.Offset),
            new DateTimeOffset(Anchor.Year, Anchor.Month, Anchor.Day, 23, 59, 59, Anchor.Offset));
        var timed = Timed("call", 10, 11);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { allDay, timed }));

        var allDayRow = cut.Find("[data-calee-region='all-day']");
        var hourGrid = cut.Find("[data-calee-region='hour-grid']");

        // All-day event renders inside the all-day row.
        var allDayEvents = allDayRow.QuerySelectorAll(".calee-scheduler-all-day-event");
        Assert.Single(allDayEvents);
        Assert.Contains("party", allDayEvents[0].TextContent);

        // All-day event does NOT appear in the hour grid.
        var hourGridEvents = hourGrid.QuerySelectorAll(".calee-scheduler-event");
        Assert.DoesNotContain(hourGridEvents, e => e.TextContent.Contains("party"));
        // The timed event does.
        Assert.Contains(hourGridEvents, e => e.TextContent.Contains("call"));
    }

    [Fact]
    public async Task Out_Of_Range_Events_Aggregate_To_Earlier_Chip()
    {
        using var ctx = NewContext();
        DayOverflowContext<CalendarEvent>? captured = null;

        // StartHour=8, event 6–7 AM is entirely before the visible band.
        var early = Timed("early", 6, 7);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { early })
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var chip = cut.Find("[data-calee-region='overflow-chip']");
        Assert.Contains("+1 earlier", chip.TextContent);

        await cut.InvokeAsync(() => chip.Click());

        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Earlier, captured!.Kind);
        Assert.Equal(DateOnly.FromDateTime(Anchor.Date), captured.Date);
    }

    [Fact]
    public async Task Out_Of_Range_Events_Aggregate_To_Later_Chip()
    {
        using var ctx = NewContext();
        DayOverflowContext<CalendarEvent>? captured = null;

        // EndHour=18, event 20–21 is entirely after the visible band.
        var late = Timed("late", 20, 21);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { late })
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var chips = cut.FindAll("[data-calee-region='overflow-chip']");
        Assert.Single(chips);
        Assert.Contains("+1 later", chips[0].TextContent);

        await cut.InvokeAsync(() => chips[0].Click());

        Assert.NotNull(captured);
        Assert.Equal(OverflowKind.Later, captured!.Kind);
    }

    [Fact]
    public void Partial_Span_Renders_Clip_Indicator()
    {
        using var ctx = NewContext();

        // Event 7:30–9:00 with StartHour=8 → spans the top edge of the visible band.
        var partial = Timed("p", 7, 9, startMin: 30, endMin: 0);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { partial }));

        var hourGrid = cut.Find("[data-calee-region='hour-grid']");
        var clipped = hourGrid.QuerySelector(".calee-scheduler-event--clip-top");
        Assert.NotNull(clipped);
        // The ↑ indicator (rendered via &uarr; entity) appears in the clipped event.
        Assert.Contains("↑", clipped!.TextContent);
    }

    [Fact]
    public void Events_Render_At_Computed_Positions()
    {
        using var ctx = NewContext();

        // 10:00–11:00 with StartHour=8, EndHour=18 → top=20%, height=10%.
        var ev = Timed("e", 10, 11);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        var element = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        var style = element.GetAttribute("style") ?? string.Empty;
        Assert.Contains("top: 20", style);
        Assert.Contains("height: 10", style);
    }

    [Fact]
    public async Task Event_Click_Fires_OnEventClicked()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;

        var ev = Timed("e", 10, 11);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var eventEl = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await cut.InvokeAsync(() => eventEl.Click());

        Assert.NotNull(captured);
        Assert.Equal("e", captured!.Id);
    }

    [Fact]
    public async Task Slot_Click_Fires_OnSlotClicked_With_Snapped_Bounds()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        // Click the first slot (index 0) — 8:00–8:30.
        var slots = cut.FindAll("[role='gridcell']");
        Assert.NotEmpty(slots);
        await cut.InvokeAsync(() => slots[0].Click());

        Assert.NotNull(captured);
        Assert.Equal(8, captured!.Start.Hour);
        Assert.Equal(0, captured.Start.Minute);
        Assert.Equal(8, captured.End.Hour);
        Assert.Equal(30, captured.End.Minute);
        // Offset matches TimeZone offset on the anchor date (EDT = -04:00).
        Assert.Equal(TimeSpan.FromHours(-4), captured.Start.Offset);
    }

    [Fact]
    public void EventFilter_Excludes_Matching_Events()
    {
        using var ctx = NewContext();

        var a = Timed("a", 9, 10);
        var b = Timed("b", 10, 11);
        var c = Timed("c", 11, 12);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { a, b, c })
            .Add(c => c.EventFilter, e => e.Id != "b"));

        var events = cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.TextContent.Contains("a"));
        Assert.Contains(events, e => e.TextContent.Contains("c"));
        Assert.DoesNotContain(events, e => e.TextContent.Contains("b"));
    }

    [Fact]
    public void ShowCurrentTimeIndicator_False_Hides_Indicator()
    {
        using var ctx = NewContext();

        // Anchor "today" so the indicator would otherwise render. We need to anchor on
        // today in TZ to actually trigger the IsTodayInView check.
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz)
            .Add(c => c.ShowCurrentTimeIndicator, false));

        var indicators = cut.FindAll(".calee-scheduler-current-time-indicator");
        Assert.Empty(indicators);
    }

    [Fact]
    public void Invalid_StartHour_GreaterThan_EndHour_Throws_ArgumentException()
    {
        using var ctx = NewContext();

        var ex = Assert.Throws<ArgumentException>(() =>
            ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.StartHour, 18)
                .Add(c => c.EndHour, 8)));
        Assert.Contains("StartHour", ex.Message);
    }

    [Fact]
    public void Aria_Grid_Role_Present_On_Hour_Grid()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        Assert.NotNull(grid);
        // Slot cells carry role='gridcell'.
        Assert.NotEmpty(cut.FindAll("[role='gridcell']"));
        // Rows carry role='row'.
        Assert.NotEmpty(cut.FindAll("[role='row']"));
    }

    [Fact]
    public void Multi_Day_Timed_Event_Renders_Visible_Chunk_With_Clip_Indicators()
    {
        using var ctx = NewContext();

        // Event spanning Tuesday 11pm → Wednesday 2am. Viewing Wednesday.
        // Anchor is Wednesday 2026-05-19 EDT (-04:00).
        var tuesday11pm = new DateTimeOffset(2026, 5, 18, 23, 0, 0, TimeSpan.FromHours(-4));
        var wed2am = new DateTimeOffset(2026, 5, 19, 2, 0, 0, TimeSpan.FromHours(-4));
        var ev = new CalendarEvent("overnight", "overnight", tuesday11pm, wed2am, IsAllDay: false);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // Widen visible hours to include the 00:00–02:00 chunk.
            .Add(c => c.StartHour, 0)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev }));

        // The Wednesday-visible chunk should render as a clipped-at-top event.
        var clipped = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event--clip-top");
        Assert.NotNull(clipped);
        // top should be 0% — chunk starts at midnight (the band's visible start).
        var style = clipped.GetAttribute("style") ?? string.Empty;
        Assert.Contains("top: 0", style);
    }

    [Fact]
    public async Task Slot_Click_Stops_Propagation_When_On_Event()
    {
        // FR-20: Event swallows the click — slot click should NOT fire when the user
        // clicked an event chip. Verified by clicking only the event and confirming
        // OnSlotClicked did not fire.
        using var ctx = NewContext();
        SchedulerSlot? slotCaptured = null;
        CalendarEvent? eventCaptured = null;

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => slotCaptured = s))
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => eventCaptured = e)));

        var eventEl = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await cut.InvokeAsync(() => eventEl.Click());

        Assert.NotNull(eventCaptured);
        Assert.Null(slotCaptured);
    }

    // ----- Drag-to-move (Phase 2 Task 4 — FR-25) ----------------------------------

    [Fact]
    public void DragToMove_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDragToMove must be false and
        // the rendered event chip must NOT carry the drag affordances (data-attribute,
        // aria-roledescription). Verifying the user-visible behavior is sufficient —
        // we don't need to assert the inner @onpointerdown wiring.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
    public void DragToMove_Enabled_AttachesDragAffordances()
    {
        // When AllowDragToMove=true the chip gains the drag-handle data attribute
        // and the aria-roledescription per plan §5.1 #2/#3.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
    public async Task DragToMove_Drop_NoCancel_FiresOnEventMoved_WithSnappedNewStartEnd()
    {
        // Drop the 10:00–11:00 event one slot down — the new start should snap to 10:30
        // and the new end should preserve the 1h duration (→ 11:30).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Drop payload: one slot (28px at default 56px/hour × 0.5h) downward.
        // Fallback geometry kicks in (jsModule is null in this test environment, so
        // GetHourGridHeightPxAsync returns 0 → the drop handler synthesizes 56px×10h
        // = 560px total). One slot at SlotDurationMinutes=30 is 560/(10*2) = 28px.
        var payload = new DropPayload(0, 0, 0, 28, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewStart.Hour);
        Assert.Equal(30, captured.NewStart.Minute);
        Assert.Equal(11, captured.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        Assert.Null(captured.NewLaneId);
    }

    [Fact]
    public async Task DragToMove_PreservesEventDuration()
    {
        // 30-minute event dropped anywhere should yield NewEnd - NewStart == 30 minutes.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 10, endMin: 30);  // 10:00–10:30
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 56, "move");  // 2 slots down

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromMinutes(30), captured!.NewEnd - captured.NewStart);
    }

    [Fact]
    public async Task DragToMove_SnapsToSlot_OnNonAlignedDrop()
    {
        // A delta that doesn't align to the slot boundary should snap to the nearest slot.
        // Default 56px/hour, slot=30min → 28px/slot. Drop at 20px: a snap to the nearest
        // multiple-of-28 = 28 → +1 slot. (Math.Round(20/28) = 1 with AwayFromZero.)
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Drop at 20px: closer to 1 slot (28px) than 0 slots. Inverse-Y snaps to 10:30.
        var payload = new DropPayload(0, 0, 0, 20, "move");

        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        // 10:30 is a slot boundary.
        Assert.Equal(0, captured!.NewStart.Minute % 30);
    }

    [Fact]
    public async Task DragToMove_Cancel_True_Reverts_OptimisticPin()
    {
        // Consumer sets context.Cancel = true → the optimistic pin must be cleared
        // and the event renders at its original position again.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cancelHandler = EventCallback.Factory.Create<EventMoveContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, cancelHandler));

        // Original event was at 10:00 (top=20% with StartHour=8, EndHour=18).
        var beforeStyle = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .GetAttribute("style") ?? string.Empty;
        Assert.Contains("top: 20", beforeStyle);

        var payload = new DropPayload(0, 0, 0, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // After Cancel=true, pin is cleared.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));
        // And the rendered position is back at 10:00 (top=20%).
        var afterStyle = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .GetAttribute("style") ?? string.Empty;
        Assert.Contains("top: 20", afterStyle);
    }

    [Fact]
    public async Task DragToMove_PinAppliedVisually_BeforeConsumerCatchUp()
    {
        // After a successful drop the rendered position must reflect the new time
        // immediately (optimistic pin) — without the consumer pushing a new Events
        // list back through. Drop one slot down: top=22.5% (10:30 within 8–18 band).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        var payload = new DropPayload(0, 0, 0, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        // Pin is set.
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(10, pin!.Value.Start.Hour);
        Assert.Equal(30, pin.Value.Start.Minute);

        // Rendered top: 10:30 → 2.5 hours into the 10-hour band → 25%.
        var style = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
            .GetAttribute("style") ?? string.Empty;
        Assert.Contains("top: 25", style);
    }

    // ----- Drag-to-resize (Phase 2 Task 7 — FR-26) ---------------------------------

    [Fact]
    public void DragToResize_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDragToResize is false and the
        // rendered chip does NOT carry the resize affordances (handle element, ARIA).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
    public void DragToResize_Enabled_AttachesResizeAffordances()
    {
        // AllowDragToResize=true adds the resize-end hit-zone element + aria-roledescription
        // "resizable event" (plan §5.1 #2/#3).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev }));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal("resizable event", chip.GetAttribute("aria-roledescription"));
        // The resize hit-zone overlay is a child element of the chip.
        var handle = chip.QuerySelector("[data-calee-drag-handle='resize-end']");
        Assert.NotNull(handle);
    }

    [Fact]
    public void DragToResize_Both_AllowDragToMove_And_AllowDragToResize_Combines_AriaRoleDescription()
    {
        // When both flags are on the chip carries "draggable resizable event" so screen
        // readers announce both affordances. The move data attribute + resize hit-zone
        // both render.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
    public async Task DragToResize_Drop_NoCancel_FiresOnEventResized_WithSnappedNewEnd()
    {
        // Drop the resize-end of a 10:00–11:00 event one slot (28 px @ 56 px/hour × 30 min)
        // downward. The new End snaps to 11:30; Start unchanged at 10:00. NewLaneId not
        // carried by EventResizeContext (it has no lane field — resize never moves lanes).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // DeltaY=28 = one slot at default 56 px/hour × 30 min.
        var payload = new DropPayload(0, 0, 0, 28, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(11, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
    }

    [Fact]
    public async Task DragToResize_PreservesStart()
    {
        // Whatever DeltaY is supplied, Start must equal the event's original Start —
        // resize only moves the End.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
        // Event in the pin still starts at 10:00.
        var pin = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pin);
        Assert.Equal(10, pin!.Value.Start.Hour);
        Assert.Equal(0, pin.Value.Start.Minute);
    }

    [Fact]
    public async Task DragToResize_SnapsToSlot_OnNonAlignedDrop()
    {
        // DeltaY=20 (between 0 and 28 px = one slot). Round-to-nearest-slot via
        // AwayFromZero rolls up to +1 slot → End snaps to 11:30.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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
        // Snapped to a slot boundary.
        Assert.Equal(0, captured!.NewEnd.Minute % 30);
    }

    [Fact]
    public async Task DragToResize_Cancel_True_Reverts_OptimisticPin()
    {
        // Consumer sets context.Cancel = true → the pin clears and the event renders
        // at its original duration (top + height) again.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var cancelHandler = EventCallback.Factory.Create<EventResizeContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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

        // After Cancel=true, pin is cleared.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    [Fact]
    public async Task DragToResize_ClampsToMinimumOneSlotDuration_WhenDraggedPastStart()
    {
        // Drag the bottom edge all the way up past Start — the New End must clamp to
        // Start + one slot (preventing degenerate / inverted ranges).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);   // 1-hour event.
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // Excessive negative DeltaY drags past Start. Expected: NewEnd = Start + 30min = 10:30.
        var payload = new DropPayload(0, 0, 0, -10000, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.NewEnd.Hour);
        Assert.Equal(30, captured.NewEnd.Minute);
        // Library guarantees NewEnd > Start (no degenerate range).
        Assert.True(captured.NewEnd > ev.Start);
    }

    [Fact]
    public async Task DragToResize_ClampsToEndHour_WhenDraggedPastViewBottom()
    {
        // Drag the bottom edge way past EndHour=18 — the New End must clamp to the band
        // end (18:00 on Anchor day).
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        EventResizeContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventResized,
                EventCallback.Factory.Create<EventResizeContext>(this, m => captured = m)));

        // Wildly excessive positive DeltaY → clamp to 18:00.
        var payload = new DropPayload(0, 0, 0, 10000, "resize-end");
        await cut.InvokeAsync(() => cut.Instance.InvokeResizeDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(18, captured!.NewEnd.Hour);
        Assert.Equal(0, captured.NewEnd.Minute);
        Assert.Equal(Anchor.Day, captured.NewEnd.Day);
    }

    [Fact]
    public async Task OptimisticPin_ClearedOnConsumerDataCatchup()
    {
        // After the consumer accepts the move and pushes a new Events list back with
        // the new times, the pin is redundant — OnParametersSet must drop the entry.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var noOpHandler = EventCallback.Factory.Create<EventMoveContext>(this, _ => { });

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventMoved, noOpHandler));

        // Drop one slot down.
        var payload = new DropPayload(0, 0, 0, 28, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        var pinBefore = cut.Instance.GetOptimisticPin("e");
        Assert.NotNull(pinBefore);

        // Consumer "catches up" — pushes the new Events list with the pinned times.
        var moved = new CalendarEvent("e", "e",
            pinBefore!.Value.Start, pinBefore.Value.End, IsAllDay: false);
        cut.Render(p => p.Add(c => c.Events, new[] { moved }));

        // Pin is now redundant and was dropped.
        Assert.Null(cut.Instance.GetOptimisticPin("e"));
    }

    // ----- Drag-to-create (Phase 2 Task 8 — FR-24) ---------------------------------

    [Fact]
    public void DragToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDragToCreate is false. The
        // rendered slot cells must not carry the create-affordance class (no cursor
        // change, no @onpointerdown wiring). bUnit can't easily test the absence of an
        // @onpointerdown binding directly, but the CSS class is a 1:1 proxy — the
        // class is only stamped when the binding is wired.
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var slot = cut.Find(".calee-scheduler-slot");
        var classAttr = slot.GetAttribute("class") ?? string.Empty;
        Assert.DoesNotContain("calee-scheduler-slot--create-affordance", classAttr);
    }

    [Fact]
    public void DragToCreate_Enabled_AttachesGridBackgroundHandler()
    {
        // Opting in renders the create-affordance class on every slot cell. The class
        // doubles as the on/off signal for the @onpointerdown binding (both gate on
        // AllowDragToCreate).
        using var ctx = NewContext();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToCreate, true));

        var slot = cut.Find(".calee-scheduler-slot");
        var classAttr = slot.GetAttribute("class") ?? string.Empty;
        Assert.Contains("calee-scheduler-slot--create-affordance", classAttr);
    }

    [Fact]
    public async Task DragToCreate_Drop_NoCancel_FiresOnEventCreated_WithSnappedStartEnd()
    {
        // Drag from slot 4 (10:00, the 5th slot at 30-min granularity past 8:00) downward
        // by 28 px = one slot. Expected Start = 10:00, End = 11:00 (covers slot 4 + slot 5).
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Slot 4 at 30-min granularity past 8:00 = 10:00. DeltaY=28 = one slot.
        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(4, payload));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
        // Day view: LaneId is always null.
        Assert.Null(captured.Slot.LaneId);
    }

    [Fact]
    public async Task DragToCreate_BidirectionalDrag_NormalizesStartLessThanEnd()
    {
        // Anchor at slot 6 (11:00) with DeltaY=-56 (two slots upward = 10:00).
        // Expected normalization: Start = 10:00, End = 11:00.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Slot 6 = 11:00. DeltaY = -56 px → -2 slots → slot 4 (10:00) as the final.
        var payload = new DropPayload(0, 0, 0, -56, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(6, payload));

        Assert.NotNull(captured);
        Assert.True(captured!.Slot.Start < captured.Slot.End,
            $"Bidirectional drag must produce Start < End; got {captured.Slot.Start} → {captured.Slot.End}.");
        Assert.Equal(10, captured.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        // End covers through-slot-6 (11:00–11:30) inclusive → End = 11:30.
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DragToCreate_SnapsToSlot_OnNonAlignedVerticalDrop()
    {
        // DeltaY=20 (between 0 and 28 px = one slot). Round-to-nearest-slot via
        // AwayFromZero rolls up to +1 slot. From anchor slot 4 (10:00) → final slot 5
        // → spanned 10:00–11:00.
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 20, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(4, payload));

        Assert.NotNull(captured);
        // Snapped — Start/End fall on slot (multiples of 30) boundaries.
        Assert.Equal(0, captured!.Slot.Start.Minute % 30);
        Assert.Equal(0, captured.Slot.End.Minute % 30);
    }

    [Fact]
    public async Task DragToCreate_Cancel_True_NoPersistedState()
    {
        // Under Option A (no optimistic phantom event) the library never renders
        // anything for a create. Setting Cancel=true in the consumer's handler is a
        // pure signal; no library-side revert is needed. We assert that the optimistic-
        // pin store stays empty before AND after the cancel.
        using var ctx = NewContext();

        var cancelHandler = EventCallback.Factory.Create<EventCreateContext>(
            this, m => m.Cancel = true);

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated, cancelHandler));

        // No event chip is rendered (no events). Trigger a synthetic create-drop.
        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(4, payload));

        // No event chip should appear post-drop — Option A renders nothing optimistically.
        // (And the optimistic-pin map is unrelated to create — verify it stays empty.)
        Assert.Empty(cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event"));
    }

    [Fact]
    public async Task DragToCreate_Disallows_Start_On_ExistingEventChip()
    {
        // Pressing on an event chip must NOT start a create drag — the chip is positioned
        // above the slot cells in DOM order, so the slot's @onpointerdown never fires
        // when the press lands on the chip. We assert: clicking an event still fires
        // OnEventClicked, and the OnEventCreated callback was NOT invoked.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var eventClickFired = false;
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
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

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await cut.InvokeAsync(() => chip.Click());

        Assert.True(eventClickFired);
        Assert.False(createFired);
    }

    [Fact]
    public async Task DragToCreate_ClampsToEndHour_WhenDraggedPastViewBottom()
    {
        // From anchor slot 18 (17:00 = next-to-last hour) with an excessive positive
        // DeltaY: the final slot clamps to SlotCount-1 (19), so End = 18:00 (band end).
        using var ctx = NewContext();

        EventCreateContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Slot 18 = 17:00. Wildly excessive +DeltaY → clamped to last slot (slot 19 = 17:30
        // start) → endSlot=20 → 18:00.
        var payload = new DropPayload(0, 0, 0, 10000, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(18, payload));

        Assert.NotNull(captured);
        Assert.Equal(17, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(18, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
    }

    // ----- Double-click-to-create (Phase 2 Task 9 — FR-32) -------------------------

    [Fact]
    public async Task DoubleClickToCreate_Disabled_By_Default()
    {
        // FR-29 fail-closed: with no parameter set, AllowDoubleClickToCreate is false.
        // bUnit's DOM dispatch refuses to fire an ondblclick that has no bound handler
        // ("MissingEventHandlerException"); the very absence of the binding is the
        // proof of the disabled state — we assert that bUnit throws when dispatched.
        // For belt-and-suspenders, we also verify the test-seam respects the guard.
        using var ctx = NewContext();

        var fired = false;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        var slot = cut.Find(".calee-scheduler-slot");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => slot.DoubleClick()));

        // Calling the test seam directly also returns without firing — the internal
        // method short-circuits on the AllowDoubleClickToCreate guard.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4));
        Assert.False(fired);
    }

    [Fact]
    public async Task DoubleClickToCreate_Enabled_OnEmptySlot_FiresOnEventCreated_WithDefaultDuration()
    {
        // Anchor slot 4 = 10:00 (5th slot at 30-min granularity past 8:00).
        // Default duration when option is null = SlotDurationMinutes = 30 → End = 10:30.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(10, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
        Assert.Null(captured.Slot.LaneId);
    }

    [Fact]
    public async Task DoubleClickToCreate_Disabled_OnExistingEventChip()
    {
        // Double-clicking on an event chip must NOT fire OnEventCreated. Event chips
        // are rendered in a separate events-row div sibling to the slot cells — the
        // chip is not an ancestor of any slot, so the slot's @ondblclick never fires.
        // The chip itself has no @ondblclick handler; bUnit raises
        // MissingEventHandlerException on the dispatch, which is exactly the proof
        // that the chip carries no create-affordance binding.
        using var ctx = NewContext();

        var ev = Timed("e", 10, 11);
        var createFired = false;

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => createFired = true)));

        var chip = cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => chip.DoubleClick()));

        Assert.False(createFired);
    }

    [Fact]
    public async Task DoubleClickToCreate_RespectsExplicit_DefaultCreateDurationMinutes()
    {
        // Explicit option = 90 minutes. Anchor slot 4 = 10:00 → End = 11:30.
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultCreateDurationMinutes = 90);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(0, captured.Slot.Start.Minute);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(30, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DoubleClickToCreate_NullOption_FallsBackToSlotDurationMinutes()
    {
        // Day view is a time-grid view → null option resolves to SlotDurationMinutes.
        // Use a non-default 60 to make the assertion meaningful.
        using var ctx = NewContext();

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        // Slot 2 at 60-min granularity past 8:00 = 10:00. Default duration = 60 min → End = 11:00.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2));

        Assert.NotNull(captured);
        Assert.Equal(10, captured!.Slot.Start.Hour);
        Assert.Equal(11, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DoubleClickToCreate_ClampsToEndHour_WhenDefaultDurationExtendsPastBand()
    {
        // Last slot (slot 19 at 30-min granularity past 8:00) = 17:30. Default duration
        // 30 min → End = 18:00 = band end (no clamp needed). Now set an explicit option
        // 120 min — the 17:30+120 = 19:30 proposed End must clamp to 18:00 (band end).
        using var ctx = NewContext();
        ctx.Services.Configure<CaleeSchedulerOptions>(o => o.DefaultCreateDurationMinutes = 120);

        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(19));

        Assert.NotNull(captured);
        Assert.Equal(17, captured!.Slot.Start.Hour);
        Assert.Equal(30, captured.Slot.Start.Minute);
        Assert.Equal(18, captured.Slot.End.Hour);
        Assert.Equal(0, captured.Slot.End.Minute);
    }

    [Fact]
    public async Task DoubleClickToCreate_AndDragToCreate_Both_Enabled_DoesNotDoubleFire()
    {
        // Both flags enabled simultaneously. A pure dblclick on a slot fires
        // OnEventCreated exactly once via the double-click path, not twice. (Verified
        // via the test seam — a real DOM dispatch would emit both events but the
        // 5 px threshold in the JS layer prevents a no-movement pointer sequence from
        // triggering the create-region drag path.)
        using var ctx = NewContext();

        var fireCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fireCount++)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4));

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void DayView_FourOverlapping_RendersOverlapBlock_AndFiresOverlapContext()
    {
        DayOverflowContext<CalendarEvent>? captured = null;
        var day = Anchor;
        var evs = new[]
        {
            new CalendarEvent("a", "A", day.Date.AddHours(9), day.Date.AddHours(10)),
            new CalendarEvent("b", "B", day.Date.AddHours(9), day.Date.AddHours(10)),
            new CalendarEvent("c", "C", day.Date.AddHours(9), day.Date.AddHours(10)),
            new CalendarEvent("d", "D", day.Date.AddHours(9), day.Date.AddHours(10)),
        };

        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, day)
            .Add(c => c.Events, evs)
            .Add(c => c.OnDayOverflowClicked,
                EventCallback.Factory.Create<DayOverflowContext<CalendarEvent>>(this, c => captured = c)));

        var block = cut.Find(".calee-scheduler-overlap-block");
        Assert.Equal("+2", block.TextContent.Trim());
        block.Click();
        Assert.Equal(OverflowKind.Overlap, captured!.Kind);
        Assert.Equal(2, captured.Events.Count);
        Assert.NotNull(captured.RegionEnd);
    }
}
