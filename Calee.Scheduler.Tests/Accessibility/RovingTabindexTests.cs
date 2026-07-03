using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests.Accessibility;

/// <summary>
/// Roving-tabindex correctness audit for every Phase 1 view (NFR-06, PRD Task 14).
///
/// The roving-tabindex pattern requires that <em>exactly one</em> focusable cell in
/// each navigable grid carries <c>tabindex="0"</c> at a time — every other cell
/// carries <c>tabindex="-1"</c>. Arrow keys move focus between cells without taking
/// the user out of the grid; Tab moves focus past the grid as a single tab stop.
///
/// These tests guard against accidental regressions in the per-view markup. They
/// complement the existing keyboard-nav tests scattered across the per-view files
/// by asserting the invariant directly.
/// </summary>
public class RovingTabindexTests
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

    /// <summary>Count of role="gridcell" elements that participate in the roving-tabindex
    /// rotation (i.e., have an explicit tabindex attribute). This filters out non-slot
    /// gridcells like the event-wrapper cells, which carry no tabindex.</summary>
    private static IReadOnlyList<IElement> RovingCells<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        return cut.FindAll("[role='gridcell'][tabindex]");
    }

    [Fact]
    public void DayView_Has_Exactly_One_Tabbable_Slot_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var rovingCells = RovingCells(cut);
        Assert.NotEmpty(rovingCells);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
        // Every other cell is tabindex="-1".
        foreach (var cell in rovingCells.Where(c => c.GetAttribute("tabindex") != "0"))
        {
            Assert.Equal("-1", cell.GetAttribute("tabindex"));
        }
    }

    [Fact]
    public async Task DayView_Arrow_Keys_Move_Tabbable_Cell_But_Preserve_Invariant()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var grid = cut.Find("[role='grid']");
        // Walk down a few cells.
        for (var i = 0; i < 3; i++)
        {
            await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        }

        // Still exactly one tabbable.
        var rovingCells = RovingCells(cut);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
    }

    [Fact]
    public void WeekView_Has_Exactly_One_Tabbable_Slot_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var rovingCells = RovingCells(cut);
        Assert.NotEmpty(rovingCells);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
        foreach (var cell in rovingCells.Where(c => c.GetAttribute("tabindex") != "0"))
        {
            Assert.Equal("-1", cell.GetAttribute("tabindex"));
        }
    }

    [Fact]
    public async Task WeekView_Cross_Column_Arrow_Preserves_Single_Tabbable_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));

        var rovingCells = RovingCells(cut);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
    }

    [Fact]
    public void MonthView_Has_Exactly_One_Tabbable_Day_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var rovingCells = RovingCells(cut);
        Assert.Equal(42, rovingCells.Count);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
        foreach (var cell in rovingCells.Where(c => c.GetAttribute("tabindex") != "0"))
        {
            Assert.Equal("-1", cell.GetAttribute("tabindex"));
        }
    }

    [Fact]
    public async Task MonthView_Arrow_Keys_Preserve_Single_Tabbable_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        var rovingCells = RovingCells(cut);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
    }

    [Fact]
    public void TimelineView_Has_Exactly_One_Tabbable_Slot_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new ILane[]
            {
                new Lane("r1", "Alpha"),
                new Lane("r2", "Beta"),
            })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        var rovingCells = RovingCells(cut);
        Assert.NotEmpty(rovingCells);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
        foreach (var cell in rovingCells.Where(c => c.GetAttribute("tabindex") != "0"))
        {
            Assert.Equal("-1", cell.GetAttribute("tabindex"));
        }
    }

    [Fact]
    public async Task TimelineView_Arrow_Keys_Preserve_Single_Tabbable_Cell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new ILane[]
            {
                new Lane("r1", "Alpha"),
                new Lane("r2", "Beta"),
            })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        var rovingCells = RovingCells(cut);
        Assert.Single(rovingCells, c => c.GetAttribute("tabindex") == "0");
    }

    [Theory]
    [InlineData("CaleeSchedulerDayView")]
    [InlineData("CaleeSchedulerWeekView")]
    [InlineData("CaleeSchedulerMonthView")]
    [InlineData("CaleeSchedulerTimelineView")]
    public void Grid_Is_Single_Tab_Stop_From_Outside(string viewName)
    {
        // Every view's role="grid" wrapper carries tabindex="-1" so it doesn't itself
        // become a tab stop; focus enters through the single tabindex="0" cell. This is
        // the WAI-ARIA roving-tabindex contract.
        using var ctx = NewContext();
        var grid = viewName switch
        {
            "CaleeSchedulerDayView" => ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor))
                .Find("[role='grid']"),
            "CaleeSchedulerWeekView" => ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor))
                .Find("[role='grid']"),
            "CaleeSchedulerMonthView" => ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor))
                .Find("[role='grid']"),
            "CaleeSchedulerTimelineView" => ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ).Add(c => c.Date, Anchor)
                .Add(c => c.Lanes, new ILane[] { new Lane("r1", "Alpha") })
                .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
                .Add(c => c.ShowUnassignedRow, false))
                .Find("[role='grid']"),
            _ => throw new ArgumentException(viewName),
        };

        Assert.Equal("-1", grid.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task DayView_Enter_On_Focused_Slot_Fires_OnSlotClicked()
    {
        using var ctx = NewContext();
        SchedulerSlot? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.OnSlotClicked,
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<SchedulerSlot>(
                    this, s => captured = s)));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));

        Assert.NotNull(captured);
        Assert.Equal(8, captured!.Start.Hour);
    }

    [Fact]
    public async Task DayView_Escape_On_Grid_Does_Not_Throw()
    {
        // Escape calls into the JS module via `calee.blurActive`. In tests the JSRuntime is
        // in Loose mode and the import resolves to a stub — we just verify the handler
        // doesn't propagate any exception.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Escape"));
        // Reached here without exception — pass.
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Issue #19 — arrow-key roving moves must invoke the JS focus-transfer helper.
    //
    // bUnit's headless DOM cannot exercise real browser focus (see this class's
    // remarks and README §9.1a), so these tests assert the C# side actually calls
    // calee-scheduler.js's `focusActiveGridCell` helper — with the view's grid/list
    // container — on every roving-tabindex move, and does NOT call it for non-roving
    // keys (Enter, Escape). Live-browser verification of the resulting
    // `document.activeElement` move lives in tools/a11y-audit.
    // ════════════════════════════════════════════════════════════════════════════

    private const string ModulePath = "./_content/Calee.Scheduler/calee-scheduler.js";

    [Fact]
    public async Task DayView_ArrowDown_Invokes_FocusActiveGridCell_WithGridContainer()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18));

        var grid = cut.Find("[role='grid']");
        Assert.Empty(module.Invocations["focusActiveGridCell"]);

        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        var call1 = Assert.Single(module.Invocations["focusActiveGridCell"]);
        var container1 = Assert.IsType<ElementReference>(call1.Arguments[0]);
        Assert.False(string.IsNullOrEmpty(container1.Id));

        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));
        Assert.Equal(2, module.Invocations["focusActiveGridCell"].Count);
        // Every call targets the same grid container (identical ElementReference).
        var container2 = Assert.IsType<ElementReference>(module.Invocations["focusActiveGridCell"][1].Arguments[0]);
        Assert.Equal(container1.Id, container2.Id);
    }

    [Fact]
    public async Task DayView_Enter_And_Escape_Do_Not_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));
        await cut.InvokeAsync(() => grid.KeyDown("Escape"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }

    [Fact]
    public async Task WeekView_ArrowMoves_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        Assert.Equal(2, module.Invocations["focusActiveGridCell"].Count);
        foreach (var call in module.Invocations["focusActiveGridCell"])
        {
            var container = Assert.IsType<ElementReference>(call.Arguments[0]);
            Assert.False(string.IsNullOrEmpty(container.Id));
        }
    }

    [Fact]
    public async Task WeekView_Enter_Does_Not_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }

    [Fact]
    public async Task MonthView_ArrowMoves_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        Assert.Equal(2, module.Invocations["focusActiveGridCell"].Count);
        foreach (var call in module.Invocations["focusActiveGridCell"])
        {
            var container = Assert.IsType<ElementReference>(call.Arguments[0]);
            Assert.False(string.IsNullOrEmpty(container.Id));
        }
    }

    [Fact]
    public async Task MonthView_Enter_Does_Not_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerMonthView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }

    [Fact]
    public async Task TimelineView_ArrowMoves_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new ILane[]
            {
                new Lane("r1", "Alpha"),
                new Lane("r2", "Beta"),
            })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("ArrowRight"));
        await cut.InvokeAsync(() => grid.KeyDown("ArrowDown"));

        Assert.Equal(2, module.Invocations["focusActiveGridCell"].Count);
        foreach (var call in module.Invocations["focusActiveGridCell"])
        {
            var container = Assert.IsType<ElementReference>(call.Arguments[0]);
            Assert.False(string.IsNullOrEmpty(container.Id));
        }
    }

    [Fact]
    public async Task TimelineView_Enter_Does_Not_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var cut = ctx.Render<CaleeSchedulerTimelineView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new ILane[]
            {
                new Lane("r1", "Alpha"),
            })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => null))
            .Add(c => c.ShowUnassignedRow, false));

        var grid = cut.Find("[role='grid']");
        await cut.InvokeAsync(() => grid.KeyDown("Enter"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }

    [Fact]
    public async Task AgendaView_ArrowDown_Invokes_FocusActiveGridCell_WithListContainer()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var events = new[]
        {
            new CalendarEvent("e1", "e1",
                new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.FromHours(-4)),
                new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.FromHours(-4))),
            new CalendarEvent("e2", "e2",
                new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.FromHours(-4)),
                new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.FromHours(-4))),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Equal(2, rows.Count);
        Assert.Empty(module.Invocations["focusActiveGridCell"]);

        await cut.InvokeAsync(() => rows[0].KeyDown("ArrowDown"));

        var call = Assert.Single(module.Invocations["focusActiveGridCell"]);
        var container = Assert.IsType<ElementReference>(call.Arguments[0]);
        Assert.False(string.IsNullOrEmpty(container.Id));
    }

    [Fact]
    public async Task AgendaView_Enter_Does_Not_Invoke_FocusActiveGridCell()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.SetupVoid("focusActiveGridCell", _ => true).SetVoidResult();

        var events = new[]
        {
            new CalendarEvent("e1", "e1",
                new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.FromHours(-4)),
                new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.FromHours(-4))),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var row = cut.Find("[data-calee-region='agenda-row']");
        await cut.InvokeAsync(() => row.KeyDown("Enter"));

        Assert.Empty(module.Invocations["focusActiveGridCell"]);
    }
}
