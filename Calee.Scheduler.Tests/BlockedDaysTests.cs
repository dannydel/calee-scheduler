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
/// Tests for the <c>DayModifier</c> per-day state hook (issue #8) across Day, Week
/// (including a <c>VisibleDays</c> subset — the WorkWeek engine primitive), and Month.
/// Covers: blocked-cell markup (class + aria + label), fail-closed create suppression
/// (double-click, drag-to-create, create-at-focus), the create-vs-move split
/// (drag-to-move/resize onto a blocked day still fires; <c>OnSlotClicked</c> is
/// untouched), the null-hook default path, and the roving-tabindex contract.
/// </summary>
public class BlockedDaysTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Anchor: Tuesday, 2026-05-19 in America/New_York (matches the other per-view test files).
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    /// <summary>DayModifier that blocks exactly the supplied date (date-only compare).</summary>
    private static Func<DateTimeOffset, SchedulerDayState?> BlockOn(DateOnly date, string? label = null) =>
        day => DateOnly.FromDateTime(day.Date) == date
            ? new SchedulerDayState(IsBlocked: true, Label: label)
            : null;

    // ════════════════════════════════════════════════════════════════════════════
    // Day view
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Day_BlockedDay_HeaderAndSlots_CarryClassAriaAndLabel()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 10)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date), "Blocked — holiday")));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.Contains("calee-scheduler-day-blocked", header.GetAttribute("class") ?? string.Empty);
        Assert.Equal("true", header.GetAttribute("aria-disabled"));
        Assert.Contains("Blocked — holiday", header.GetAttribute("aria-label") ?? string.Empty);

        foreach (var slot in cut.FindAll(".calee-scheduler-slot"))
        {
            Assert.Contains("calee-scheduler-day-blocked", slot.GetAttribute("class") ?? string.Empty);
            Assert.Equal("true", slot.GetAttribute("aria-disabled"));
            Assert.Contains("Blocked — holiday", slot.GetAttribute("aria-label") ?? string.Empty);
        }
    }

    [Fact]
    public void Day_BlockedDay_NoLabel_FallsBackToGenericBlockedAnnouncement()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date))));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.Contains("blocked", header.GetAttribute("aria-label") ?? string.Empty);
    }

    [Fact]
    public void Day_BlockedDay_SuppressesCreateAffordanceClass_EvenWhenAllowDragToCreate()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date))));

        var slot = cut.Find(".calee-scheduler-slot");
        Assert.DoesNotContain("calee-scheduler-slot--create-affordance", slot.GetAttribute("class") ?? string.Empty);
    }

    [Fact]
    public async Task Day_DoubleClickToCreate_NoOp_OnBlockedDay()
    {
        using var ctx = NewContext();
        var fired = false;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date)))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        // Both the markup-level gate (no @ondblclick binding on a blocked slot) and the
        // handler-level fail-closed check are exercised: DOM double-click throws because
        // no handler is wired, and the test-seam entry point confirms the handler itself
        // also refuses.
        var slot = cut.Find(".calee-scheduler-slot");
        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => slot.DoubleClick()));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4));
        Assert.False(fired);
    }

    [Fact]
    public async Task Day_DragToCreate_NoOp_WhenSweptRegionTouchesBlockedDay()
    {
        using var ctx = NewContext();
        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date)))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(4, payload));

        Assert.Null(captured);
    }

    [Fact]
    public async Task Day_CreateAtFocusKeystroke_NoOp_OnBlockedDay()
    {
        using var ctx = NewContext();
        var createCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date)))
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']").KeyDown(new KeyboardEventArgs { Key = "n" }));

        Assert.Equal(0, createCount);
    }

    [Fact]
    public async Task Day_OnSlotClicked_StillFires_OnBlockedDay()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date)))
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var slot = cut.Find(".calee-scheduler-slot");
        await cut.InvokeAsync(() => slot.Click());

        Assert.NotNull(captured);
    }

    [Fact]
    public void Day_NullDayModifier_ZeroBehaviorChange()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.DoesNotContain("calee-scheduler-day-blocked", header.GetAttribute("class") ?? string.Empty);
        Assert.Null(header.GetAttribute("aria-disabled"));

        foreach (var slot in cut.FindAll(".calee-scheduler-slot"))
        {
            Assert.DoesNotContain("calee-scheduler-day-blocked", slot.GetAttribute("class") ?? string.Empty);
            Assert.Null(slot.GetAttribute("aria-disabled"));
        }
    }

    [Fact]
    public void Day_BlockedDay_TabbableSlot_StaysFocusable()
    {
        // Roving-tabindex contract: a blocked day's slots stay reachable/focusable
        // (inert to create, not to focus) — exactly one carries tabindex="0", and it
        // also carries aria-disabled="true" simultaneously.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(DateOnly.FromDateTime(Anchor.Date))));

        var rovingCells = cut.FindAll("[role='gridcell'][tabindex]");
        Assert.NotEmpty(rovingCells);
        var tabbable = Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
        Assert.Equal("true", tabbable.GetAttribute("aria-disabled"));
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Week view (incl. VisibleDays / WorkWeek subset)
    // ════════════════════════════════════════════════════════════════════════════

    // Anchor's week: Sun 5/17, Mon 5/18, Tue 5/19 (anchor, col 2), Wed 5/20, Thu 5/21,
    // Fri 5/22, Sat 5/23 (FirstDayOfWeek defaults to Sunday).
    private static readonly DateOnly Thursday = new(2026, 5, 21);

    [Fact]
    public void Week_BlockedColumn_HeaderAndSlots_CarryClassAriaAndLabel_AdjacentColumnUnaffected()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 10)
            .Add(c => c.SlotDurationMinutes, 60)
            .Add(c => c.DayModifier, BlockOn(Thursday, "Blocked — no route")));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        // Col 4 = Thursday 5/21.
        Assert.Contains("calee-scheduler-day-blocked", headers[4].GetAttribute("class") ?? string.Empty);
        Assert.Equal("true", headers[4].GetAttribute("aria-disabled"));
        Assert.Contains("Blocked — no route", headers[4].GetAttribute("aria-label") ?? string.Empty);

        // Anchor's own column (Tuesday, col 2) is untouched.
        Assert.DoesNotContain("calee-scheduler-day-blocked", headers[2].GetAttribute("class") ?? string.Empty);
        Assert.Null(headers[2].GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Week_DoubleClickToCreate_NoOp_OnBlockedColumn_ButFires_OnAdjacentColumn()
    {
        using var ctx = NewContext();
        var fired = new List<EventCreateContext>();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.DayModifier, BlockOn(Thursday))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => fired.Add(m))));

        // Col 4 = Thursday (blocked) — no-op.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(4, 2));
        Assert.Empty(fired);

        // Col 2 = Tuesday (normal) — fires.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(2, 2));
        Assert.Single(fired);
    }

    [Fact]
    public async Task Week_DragToCreate_NoOp_WhenSweptRegionTouchesBlockedColumn()
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
            .Add(c => c.DayModifier, BlockOn(Thursday))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => captured = m)));

        var payload = new DropPayload(0, 0, 0, 28, "create-region");
        await cut.InvokeAsync(() => cut.Instance.InvokeCreateDropForTestAsync(4, 4, payload));

        Assert.Null(captured);
    }

    [Fact]
    public async Task Week_CreateAtFocusKeystroke_NoOp_WhenFocusedColumnBlocked_ButFires_OnAdjacentColumn()
    {
        using var ctx = NewContext();
        var createCount = 0;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(Thursday))
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        var grid = cut.Find("[role='grid']");
        // Focus starts at column 0 (Sunday). Move right to column 4 (Thursday, blocked).
        for (var i = 0; i < 4; i++)
        {
            await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        }
        await cut.InvokeAsync(() => grid.KeyDown(new KeyboardEventArgs { Key = "n" }));
        Assert.Equal(0, createCount);

        // Move back one column (Wednesday, col 3 — normal) and retry.
        await cut.InvokeAsync(() => grid.KeyDown("ArrowLeft"));
        await cut.InvokeAsync(() => grid.KeyDown(new KeyboardEventArgs { Key = "n" }));
        Assert.Equal(1, createCount);
    }

    [Fact]
    public async Task Week_DragToMove_OntoBlockedDay_StillFiresOnEventMoved()
    {
        // Move the Tuesday 10:00–11:00 event two columns right onto Thursday, which is
        // blocked. Create-only suppression must not touch move — OnEventMoved still
        // fires; the consumer decides whether to reject via EventMoveContext.Cancel.
        using var ctx = NewContext();
        var ev = new CalendarEvent(
            "e", "e",
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, Anchor.Offset),
            new DateTimeOffset(2026, 5, 19, 11, 0, 0, Anchor.Offset),
            IsAllDay: false);
        EventMoveContext? captured = null;

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.SlotDurationMinutes, 30)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.DayModifier, BlockOn(Thursday))
            .Add(c => c.OnEventMoved,
                EventCallback.Factory.Create<EventMoveContext>(this, m => captured = m)));

        // Fallback grid width = 700px → 100px/column. DeltaX=200 = 2 columns right (Tue → Thu).
        var payload = new DropPayload(0, 0, 200, 0, "move");
        await cut.InvokeAsync(() => cut.Instance.InvokeMoveDropForTestAsync(ev, payload));

        Assert.NotNull(captured);
        Assert.Equal(21, captured!.NewStart.Day); // Landed on Thursday 5/21.
    }

    [Fact]
    public async Task Week_OnSlotClicked_StillFires_OnBlockedColumn()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.DayModifier, BlockOn(Thursday))
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        await cut.InvokeAsync(() => cut.Instance.HandleSlotClickAsync(4, 0));

        Assert.NotNull(captured);
        Assert.Equal(21, captured!.Start.Day);
    }

    [Fact]
    public void Week_NullDayModifier_ZeroBehaviorChange()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        foreach (var header in cut.FindAll("[data-calee-region='day-header']"))
        {
            Assert.DoesNotContain("calee-scheduler-day-blocked", header.GetAttribute("class") ?? string.Empty);
        }
    }

    [Fact]
    public void Week_VisibleDays_Subset_NeverEvaluatesHookForHiddenDays()
    {
        // WorkWeek engine primitive (issue #7) — a Mon-Fri VisibleDays subset must never
        // pass a hidden (Sat/Sun) day to DayModifier.
        using var ctx = NewContext();
        var seenDays = new List<DayOfWeek>();
        Func<DateTimeOffset, SchedulerDayState?> recordingHook = day =>
        {
            seenDays.Add(day.DayOfWeek);
            return null;
        };

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.DayModifier, recordingHook));

        Assert.Equal(5, seenDays.Count);
        Assert.DoesNotContain(DayOfWeek.Saturday, seenDays);
        Assert.DoesNotContain(DayOfWeek.Sunday, seenDays);
        Assert.Equal(5, cut.FindAll("[data-calee-region='day-header']").Count);
    }

    [Fact]
    public async Task Week_VisibleDays_Subset_BlockedWeekdayStillSuppressesCreate()
    {
        using var ctx = NewContext();
        var fired = false;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday,
            })
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.DayModifier, BlockOn(Thursday)) // Thursday is column 3 in a Mon-Fri subset.
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, _ => fired = true)));

        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(3, 0));

        Assert.False(fired);
    }

    [Fact]
    public void Week_BlockedColumn_TabbableSlot_StaysFocusable()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(new DateOnly(2026, 5, 17)))); // Sunday, col 0 — default focus.

        var rovingCells = cut.FindAll("[role='gridcell'][tabindex]");
        var tabbable = Assert.Single(rovingCells, cell => cell.GetAttribute("tabindex") == "0");
        Assert.Equal("true", tabbable.GetAttribute("aria-disabled"));
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Month view
    // ════════════════════════════════════════════════════════════════════════════

    // Anchor month is May 2026, Sunday-start grid. Cell 19 = Fri May 15 (matches the
    // constant used throughout CaleeSchedulerMonthViewTests.cs).
    private static readonly DateOnly FriMay15 = new(2026, 5, 15);

    [Fact]
    public void Month_BlockedCell_CarriesClassAriaAndLabel_AdjacentCellUnaffected()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(FriMay15, "Blocked — depot closed")));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        var blocked = cells[19];
        Assert.Contains("calee-scheduler-day-blocked", blocked.GetAttribute("class") ?? string.Empty);
        Assert.Equal("true", blocked.GetAttribute("aria-disabled"));
        Assert.Contains("Blocked — depot closed", blocked.GetAttribute("aria-label") ?? string.Empty);

        var adjacent = cells[18]; // Thu May 14 — untouched.
        Assert.DoesNotContain("calee-scheduler-day-blocked", adjacent.GetAttribute("class") ?? string.Empty);
        Assert.Null(adjacent.GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task Month_DoubleClickToCreate_NoOp_OnBlockedCell_ButFires_OnAdjacentCell()
    {
        using var ctx = NewContext();
        var fired = new List<EventCreateContext>();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.DayModifier, BlockOn(FriMay15))
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, m => fired.Add(m))));

        // Cell 19 = Fri May 15 (blocked) — no-op.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(19));
        Assert.Empty(fired);

        // Cell 18 = Thu May 14 (normal) — fires.
        await cut.InvokeAsync(() => cut.Instance.InvokeDoubleClickCreateForTestAsync(18));
        Assert.Single(fired);
    }

    [Fact]
    public async Task Month_CreateAtFocusKeystroke_NoOp_WhenFocusedCellBlocked()
    {
        using var ctx = NewContext();
        var createCount = 0;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(FriMay15))
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        var grid = cut.Find("[role='grid']");
        // Default focus is cell 0. Cell 19 = row 2, col 5 (19 = 2*7 + 5).
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        for (var i = 0; i < 5; i++)
        {
            await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        }
        await cut.InvokeAsync(() => grid.KeyDown(new KeyboardEventArgs { Key = "n" }));

        Assert.Equal(0, createCount);
    }

    [Fact]
    public async Task Month_OnSlotClicked_StillFires_OnBlockedCell()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(FriMay15))
            .Add(c => c.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, s => captured = s)));

        var cell = cut.FindAll("[data-calee-region='month-cell']")[19];
        await cut.InvokeAsync(() => cell.Click());

        Assert.NotNull(captured);
        Assert.Equal(15, captured!.Start.Day);
    }

    [Fact]
    public void Month_NullDayModifier_ZeroBehaviorChange()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        foreach (var cell in cut.FindAll("[data-calee-region='month-cell']"))
        {
            Assert.DoesNotContain("calee-scheduler-day-blocked", cell.GetAttribute("class") ?? string.Empty);
            Assert.Null(cell.GetAttribute("aria-disabled"));
        }
    }

    [Fact]
    public void Month_BlockedCell_StaysInRovingTabindexRotation()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayModifier, BlockOn(FriMay15)));

        var rovingCells = cut.FindAll("[role='gridcell'][tabindex]");
        Assert.Equal(42, rovingCells.Count);
        // The blocked cell itself carries an explicit tabindex (either 0 or -1,
        // depending on current focus) — it is never removed from the rotation.
        var blockedCell = rovingCells[19];
        Assert.True(blockedCell.GetAttribute("tabindex") is "0" or "-1");
        Assert.Equal("true", blockedCell.GetAttribute("aria-disabled"));
    }
}
