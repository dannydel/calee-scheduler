using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeScheduler{TEvent}"/> (root composition). Covers FR-08
/// (composition), FR-22 (<c>OnViewChanged</c>), FR-23 (root <c>OnRangeChanged</c>
/// reconciliation), FR-31 (bindable <c>View</c>), FR-42 (<c>ShowToolbar</c>),
/// FR-09c (Timeline availability rule), FR-53 / FR-54 forwarding, plus PRD §4.6
/// validation of <c>View=Timeline</c> without lane binding.
/// </summary>
public class CaleeSchedulerTests
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

    private static ILane LaneOf(string id, string name) => new Lane(id, name);

    // ----- Default view + bindable View ----------------------------------------------

    [Fact]
    public void Renders_Default_View_From_Options()
    {
        // No View parameter → options.DefaultView=Week → Week view renders.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.NotNull(cut.Find(".calee-scheduler-week"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".calee-scheduler-day"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".calee-scheduler-month"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".calee-scheduler-timeline"));
    }

    [Fact]
    public void DefaultView_WorkWeek_Validates_And_Seeds_Uncontrolled_Mode()
    {
        // Issue #7: CaleeSchedulerOptions.DefaultView = WorkWeek must validate (no
        // OptionsValidationException) and seed the root's uncontrolled-mode internal
        // view, rendering the composed Week view narrowed to Monday–Friday.
        using var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler(o => o.DefaultView = SchedulerView.WorkWeek);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.Equal(SchedulerView.WorkWeek, cut.Instance._internalView);
        Assert.Equal(5, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
        var active = cut.Find("[data-calee-region='toolbar-view-button'][aria-checked='true']");
        Assert.Equal("Work Week", active.TextContent.Trim());
    }

    [Fact]
    public void Bindable_View_Switches_Active_View()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day));

        Assert.NotNull(cut.Find(".calee-scheduler-day"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month));

        Assert.NotNull(cut.Find(".calee-scheduler-month"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".calee-scheduler-day"));
    }

    [Fact]
    public void Toolbar_View_Switcher_Triggers_View_Change()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.ViewChanged, EventCallback.Factory.Create<SchedulerView>(this, v => captured = v)));

        var weekButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Week");
        weekButton.Click();

        Assert.Equal(SchedulerView.Week, captured);
    }

    [Fact]
    public void Toolbar_Uncontrolled_View_Switch_Updates_Active_Highlight_Same_Render()
    {
        // Regression: with no consumer-bound `View` (uncontrolled mode), clicking a
        // different view in the toolbar must update the `aria-checked` / active-button
        // class IMMEDIATELY on the next render — not on the render-after-next when
        // OnParametersSet would re-sync the state container. Earlier code only synced
        // _state.CurrentView in OnParametersSet, which StateHasChanged() doesn't trigger.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));
        // No View parameter bound → uncontrolled mode, defaults to Week per options.

        // Sanity: Week is initially active.
        var initialActive = cut.FindAll("[data-calee-region='toolbar-view-button'][aria-checked='true']");
        Assert.Single(initialActive);
        Assert.Equal("Week", initialActive[0].TextContent.Trim());

        // Click Day.
        var dayButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Day");
        dayButton.Click();

        // After the click, exactly one button should be active — and it should be Day.
        var afterActive = cut.FindAll("[data-calee-region='toolbar-view-button'][aria-checked='true']");
        Assert.Single(afterActive);
        Assert.Equal("Day", afterActive[0].TextContent.Trim());
    }

    [Fact]
    public void Toolbar_Today_Button_Updates_Date()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        cut.Find("[data-calee-region='toolbar-today']").Click();

        Assert.NotNull(captured);
        var todayInZone = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        Assert.Equal(todayInZone.Date, captured!.Value.Date);
    }

    [Fact]
    public void Toolbar_Prev_Button_Updates_Date_By_View_Period()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-1), captured);
    }

    // ----- ShowToolbar ---------------------------------------------------------------

    [Fact]
    public void ShowToolbar_False_Hides_Toolbar()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ShowToolbar, false));

        Assert.Throws<ElementNotFoundException>(
            () => cut.Find("[data-calee-region='toolbar']"));
    }

    // ----- Timeline view availability ------------------------------------------------

    [Fact]
    public void Timeline_View_Excluded_From_AvailableViews_When_Lanes_Missing()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        // Day, WorkWeek, Week, Month, Year, Agenda surface by default (issue #7 adds
        // WorkWeek to the always-present set). Timeline is gated on the lane binding
        // (FR-09c) and absent here.
        var viewButtons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        Assert.Equal(6, viewButtons.Count);
        Assert.DoesNotContain(viewButtons, b => b.TextContent.Trim() == "Timeline");
        Assert.Contains(viewButtons, b => b.TextContent.Trim() == "Work Week");
        Assert.Contains(viewButtons, b => b.TextContent.Trim() == "Year");
        Assert.Contains(viewButtons, b => b.TextContent.Trim() == "Agenda");
    }

    [Fact]
    public void Timeline_View_Available_When_Lanes_And_LaneKey_Wired()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        // Full seven-view surface when the lane binding is in place (issue #7 adds
        // WorkWeek to the always-present set).
        var viewButtons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        Assert.Equal(7, viewButtons.Count);
        Assert.Contains(viewButtons, b => b.TextContent.Trim() == "Timeline");
    }

    [Fact]
    public void View_Timeline_Without_Lanes_Throws_InvalidOperationException()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.View, SchedulerView.Timeline)));

        Assert.Contains("Timeline", ex.Message);
    }

    // ----- Forwarding ----------------------------------------------------------------

    [Fact]
    public void Forwards_Events_To_Active_View()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("a", 9, 10),
            Timed("b", 11, 12),
            Timed("c", 13, 14),
        };
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, events));

        var rendered = cut.FindAll(".calee-scheduler-day [data-calee-region='event']")
            .Where(e => !e.ClassList.Contains("calee-scheduler-all-day-event"))
            .ToList();
        Assert.Equal(3, rendered.Count);
    }

    [Fact]
    public void Forwards_EventFilter_To_Active_View()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("a", 9, 10),
            Timed("b", 11, 12),
            Timed("c", 13, 14),
        };
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, events)
            .Add(c => c.EventFilter, (Func<CalendarEvent, bool>)(e => e.Id == "b")));

        var rendered = cut.FindAll(".calee-scheduler-day [data-calee-region='event']").ToList();
        Assert.Single(rendered);
        Assert.Contains("b", rendered[0].TextContent);
    }

    [Fact]
    public void Forwards_EventTemplate_To_TimeGrid_Views_Not_Month()
    {
        using var ctx = NewContext();
        var events = new[] { Timed("a", 9, 10) };
        RenderFragment<CalendarEvent> template = ev => builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "my-custom-template");
            builder.AddContent(2, $"CUSTOM:{ev.Id}");
            builder.CloseElement();
        };

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, events)
            .Add(c => c.EventTemplate, template));

        var custom = cut.Find(".my-custom-template");
        Assert.Contains("CUSTOM:a", custom.TextContent);
    }

    [Fact]
    public void Forwards_EventChipTemplate_To_Month_View()
    {
        using var ctx = NewContext();
        var events = new[] { Timed("a", 9, 10) };
        RenderFragment<CalendarEvent> chip = ev => builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "my-custom-chip");
            builder.AddContent(2, $"CHIP:{ev.Id}");
            builder.CloseElement();
        };

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.Events, events)
            .Add(c => c.EventChipTemplate, chip));

        var custom = cut.Find(".my-custom-chip");
        Assert.Contains("CHIP:a", custom.TextContent);
    }

    [Fact]
    public async Task OnEventClicked_Bubbles_From_Active_View()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var eventEl = cut.Find(".calee-scheduler-day [data-calee-region='event']");
        await cut.InvokeAsync(() => eventEl.Click());

        Assert.NotNull(captured);
        Assert.Equal("a", captured!.Id);
    }

    [Fact]
    public void Root_Forwards_Drag_Resize_And_Create_Surface_To_Day_View()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.OnEventMoved, EventCallback.Factory.Create<EventMoveContext>(this, _ => { }))
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, _ => { }))
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated, EventCallback.Factory.Create<EventCreateContext>(this, _ => { })));

        var child = cut.FindComponent<CaleeSchedulerDayView<CalendarEvent>>().Instance;

        Assert.True(child.AllowDragToMove);
        Assert.True(child.OnEventMoved.HasDelegate);
        Assert.True(child.AllowDragToResize);
        Assert.True(child.OnEventResized.HasDelegate);
        Assert.True(child.AllowDragToCreate);
        Assert.True(child.AllowDoubleClickToCreate);
        Assert.True(child.OnEventCreated.HasDelegate);
    }

    [Fact]
    public void Root_Forwards_Drag_Resize_And_Create_Surface_To_Week_View()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.OnEventMoved, EventCallback.Factory.Create<EventMoveContext>(this, _ => { }))
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, _ => { }))
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated, EventCallback.Factory.Create<EventCreateContext>(this, _ => { })));

        var child = cut.FindComponent<CaleeSchedulerWeekView<CalendarEvent>>().Instance;

        Assert.True(child.AllowDragToMove);
        Assert.True(child.OnEventMoved.HasDelegate);
        Assert.True(child.AllowDragToResize);
        Assert.True(child.OnEventResized.HasDelegate);
        Assert.True(child.AllowDragToCreate);
        Assert.True(child.AllowDoubleClickToCreate);
        Assert.True(child.OnEventCreated.HasDelegate);
    }

    [Fact]
    public void Root_Forwards_DoubleClick_Create_Surface_To_Month_View()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated, EventCallback.Factory.Create<EventCreateContext>(this, _ => { })));

        var child = cut.FindComponent<CaleeSchedulerMonthView<CalendarEvent>>().Instance;

        Assert.True(child.AllowDoubleClickToCreate);
        Assert.True(child.OnEventCreated.HasDelegate);
    }

    [Fact]
    public async Task Root_Month_DoubleClickCreate_Bubbles_OnEventCreated()
    {
        using var ctx = NewContext();
        EventCreateContext? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated,
                EventCallback.Factory.Create<EventCreateContext>(this, e => captured = e)));

        var cells = cut.FindAll("[data-calee-region='month-cell']");
        await cut.InvokeAsync(() => cells[23].DoubleClick());

        Assert.NotNull(captured);
        Assert.Equal(new DateTime(2026, 5, 19), captured!.Slot.Start.Date);
        Assert.Equal(TimeSpan.FromDays(1), captured.Slot.End - captured.Slot.Start);
    }

    [Fact]
    public void Root_Forwards_Drag_Resize_And_Create_Surface_To_Timeline_View()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Timeline)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.OnEventMoved, EventCallback.Factory.Create<EventMoveContext>(this, _ => { }))
            .Add(c => c.AllowDragToResize, true)
            .Add(c => c.OnEventResized, EventCallback.Factory.Create<EventResizeContext>(this, _ => { }))
            .Add(c => c.AllowDragToCreate, true)
            .Add(c => c.AllowDoubleClickToCreate, true)
            .Add(c => c.OnEventCreated, EventCallback.Factory.Create<EventCreateContext>(this, _ => { })));

        var child = cut.FindComponent<CaleeSchedulerTimelineView<CalendarEvent>>().Instance;

        Assert.True(child.AllowDragToMove);
        Assert.True(child.OnEventMoved.HasDelegate);
        Assert.True(child.AllowDragToResize);
        Assert.True(child.OnEventResized.HasDelegate);
        Assert.True(child.AllowDragToCreate);
        Assert.True(child.AllowDoubleClickToCreate);
        Assert.True(child.OnEventCreated.HasDelegate);
    }

    // ----- OnRangeChanged ------------------------------------------------------------

    [Fact]
    public void OnRangeChanged_Fires_When_View_Changes()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        // Initial render fired once.
        var initialCount = ranges.Count;
        Assert.True(initialCount >= 1);

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week));

        // Switching views changed the range — at least one additional fire.
        Assert.True(ranges.Count > initialCount);
    }

    [Fact]
    public void OnRangeChanged_Fires_When_Date_Changes()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initial = ranges.Count;

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor.AddDays(1))
            .Add(c => c.View, SchedulerView.Day));

        Assert.True(ranges.Count > initial);
    }

    [Fact]
    public void OnRangeChanged_Does_Not_Fire_On_Unrelated_Reparams()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var beforeRepass = ranges.Count;

        // Re-render with identical View+Date but a different unrelated parameter.
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.ShowToolbar, false));

        Assert.Equal(beforeRepass, ranges.Count);
    }

    // ----- Timeline view binding -----------------------------------------------------

    [Fact]
    public void Bindable_TimelineScale_Flows_To_Timeline_View_And_Toolbar()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Timeline)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1"))
            .Add(c => c.TimelineScale, TimelineScale.Week)
            .Add(c => c.DateChanged,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        // Week-scale Timeline view renders 7 X-axis day ticks (one tick label per visible day).
        var ticks = cut.FindAll(".calee-scheduler-timeline-tick-label");
        Assert.Equal(7, ticks.Count);

        // Toolbar Prev uses ±7 days for TimelineScale.Week.
        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-7), captured);
    }

    // ----- AdditionalAttributes / Class hooks ---------------------------------------

    [Fact]
    public void AdditionalAttributes_Splatted_On_Root_Container()
    {
        using var ctx = NewContext();
        var extra = new Dictionary<string, object>
        {
            ["data-test-id"] = "scheduler-root",
            ["title"] = "Main scheduler",
        };
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AdditionalAttributes, extra));

        var root = cut.Find("[data-calee-region='scheduler']");
        Assert.Equal("scheduler-root", root.GetAttribute("data-test-id"));
        Assert.Equal("Main scheduler", root.GetAttribute("title"));
    }

    [Fact]
    public async Task OnViewChanged_Fires_When_View_Switcher_Activated()
    {
        using var ctx = NewContext();
        SchedulerView? capturedOnView = null;
        SchedulerView? capturedBindable = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.ViewChanged,
                EventCallback.Factory.Create<SchedulerView>(this, v => capturedBindable = v))
            .Add(c => c.OnViewChanged,
                EventCallback.Factory.Create<SchedulerView>(this, v => capturedOnView = v)));

        var monthButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Month");
        await cut.InvokeAsync(() => monthButton.Click());

        Assert.Equal(SchedulerView.Month, capturedBindable);
        Assert.Equal(SchedulerView.Month, capturedOnView);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Phase 2 Task 18 — Year + Agenda cascade-mode wiring
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bindable_View_Year_Renders_Year_View()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year));

        Assert.NotNull(cut.Find("[data-calee-region='year']"));
        Assert.Equal(12, cut.FindAll("[data-calee-region='year-month']").Count);
    }

    [Fact]
    public void Bindable_View_Agenda_Renders_Agenda_View()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.Events, new[]
            {
                Timed("a", 9, 10),
                Timed("b", 14, 15),
            }));

        Assert.NotNull(cut.Find("[data-calee-region='agenda']"));
        // Two events anchored on the visible day → two listitem rows.
        Assert.Equal(2, cut.FindAll("[role='listitem']").Count);
    }

    [Fact]
    public void Year_View_Forwards_YearStyle_And_YearLayout()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year)
            .Add(c => c.YearStyle, YearViewStyle.Heatmap)
            .Add(c => c.YearLayout, YearViewLayout.Grid2x6));

        var root = cut.Find("[data-calee-region='year']");
        var classes = root.GetAttribute("class") ?? string.Empty;
        Assert.Contains("calee-scheduler-year-style--heatmap", classes);
        Assert.Contains("calee-scheduler-year-layout--grid-2x6", classes);
    }

    [Fact]
    public void Agenda_View_Forwards_AgendaDays()
    {
        using var ctx = NewContext();
        // AgendaDays=3 with one event 4 days out → that event falls outside the window.
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 3)
            .Add(c => c.Events, new[]
            {
                Timed("inside", 9, 10),
                Timed("outside", 9, 10, on: Anchor.AddDays(5)),
            }));

        var rows = cut.FindAll("[role='listitem']");
        Assert.Single(rows);
        Assert.Contains("inside", rows[0].TextContent);
    }

    [Fact]
    public void Agenda_View_Forwards_EventRowTemplate()
    {
        using var ctx = NewContext();
        var events = new[] { Timed("a", 9, 10) };
        RenderFragment<CalendarEvent> rowTemplate = ev => builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "my-custom-agenda-row");
            builder.AddContent(2, $"ROW:{ev.Id}");
            builder.CloseElement();
        };

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.Events, events)
            .Add(c => c.EventRowTemplate, rowTemplate));

        var custom = cut.Find(".my-custom-agenda-row");
        Assert.Contains("ROW:a", custom.TextContent);
    }

    [Fact]
    public void Agenda_AgendaDays_Clamps_To_Inclusive_Range()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 365));

        // Resolved value clamps to MaxAgendaDays (90).
        Assert.Equal(CaleeSchedulerAgendaView<CalendarEvent>.MaxAgendaDays,
            cut.Instance.ResolvedAgendaDays);

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 0));
        Assert.Equal(1, cut.Instance.ResolvedAgendaDays);
    }

    [Fact]
    public void Year_And_Agenda_Buttons_Present_By_Default()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.Contains("Year", labels);
        Assert.Contains("Agenda", labels);
        // Order: Day, WorkWeek, Week, Month, Year, Agenda (Timeline absent because no
        // lanes; issue #7 slots WorkWeek in right after Day).
        Assert.Equal(new[] { "Day", "Work Week", "Week", "Month", "Year", "Agenda" }, labels);
    }

    [Fact]
    public void ShowYearButton_False_Hides_Year_Button()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ShowYearButton, false));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.DoesNotContain("Year", labels);
        Assert.Contains("Agenda", labels);
    }

    [Fact]
    public void ShowAgendaButton_False_Hides_Agenda_Button()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ShowAgendaButton, false));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.DoesNotContain("Agenda", labels);
        Assert.Contains("Year", labels);
    }

    [Fact]
    public async Task Toolbar_Year_Button_Click_Updates_View()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.ViewChanged,
                EventCallback.Factory.Create<SchedulerView>(this, v => captured = v)));

        var yearButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Year");
        await cut.InvokeAsync(() => yearButton.Click());

        Assert.Equal(SchedulerView.Year, captured);
    }

    [Fact]
    public async Task Toolbar_Agenda_Button_Click_Updates_View()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.ViewChanged,
                EventCallback.Factory.Create<SchedulerView>(this, v => captured = v)));

        var agendaButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Agenda");
        await cut.InvokeAsync(() => agendaButton.Click());

        Assert.Equal(SchedulerView.Agenda, captured);
    }

    [Fact]
    public async Task Palette_ViewYear_Invoke_Flips_Root_To_Year_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        var cmd = cut.Instance.Commands.First(c => c.Id == SchedulerCommandIds.ViewYear);
        await cut.InvokeAsync(() => cmd.Invoke());

        Assert.Equal(SchedulerView.Year, cut.Instance._internalView);
        // The Year view DOM should now be present.
        Assert.NotNull(cut.Find("[data-calee-region='year']"));
    }

    [Fact]
    public async Task Palette_ViewAgenda_Invoke_Flips_Root_To_Agenda_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        var cmd = cut.Instance.Commands.First(c => c.Id == SchedulerCommandIds.ViewAgenda);
        await cut.InvokeAsync(() => cmd.Invoke());

        Assert.Equal(SchedulerView.Agenda, cut.Instance._internalView);
        Assert.NotNull(cut.Find("[data-calee-region='agenda']"));
    }

    [Fact]
    public async Task Keystroke_4_Flips_Root_To_Year_In_Uncontrolled_Mode()
    {
        // The active view's chip-scope or grid-scope keystroke dispatch fires the
        // shortcut binding; the root absorbs the OnViewSwitchRequested signal and
        // auto-flips its own view when uncontrolled.
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));
        // Default-view is Week; the Week view's grid receives the keystroke.

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid']")
                .KeyDown(new KeyboardEventArgs { Key = "4" }));

        Assert.Equal(SchedulerView.Year, cut.Instance._internalView);
        Assert.NotNull(cut.Find("[data-calee-region='year']"));
    }

    [Fact]
    public async Task Keystroke_5_Flips_Root_To_Agenda_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid']")
                .KeyDown(new KeyboardEventArgs { Key = "5" }));

        Assert.Equal(SchedulerView.Agenda, cut.Instance._internalView);
        Assert.NotNull(cut.Find("[data-calee-region='agenda']"));
    }

    [Fact]
    public void All_Six_Views_Reachable_Through_Toolbar_View_Switcher()
    {
        // The reviewer-checkpoint smoke test for Task 18: walk Day → Week → Month →
        // Year → Agenda → Timeline → Day and confirm each rendered view shows up at
        // its canonical root marker.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));

        Assert.NotNull(cut.Find(".calee-scheduler-day"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find(".calee-scheduler-week"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find(".calee-scheduler-month"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find("[data-calee-region='year']"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find("[data-calee-region='agenda']"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Timeline)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find(".calee-scheduler-timeline"));

        // And back to Day to prove the cycle is closed.
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Lanes, new[] { LaneOf("r1", "Lane 1") })
            .Add(c => c.LaneKey, (Func<CalendarEvent, string?>)(_ => "r1")));
        Assert.NotNull(cut.Find(".calee-scheduler-day"));
    }

    [Fact]
    public void Range_Label_Year_Format_Through_Root()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Agenda_Format_Through_Root()
    {
        using var ctx = NewContext();
        // Anchor is Tue 2026-05-19; AgendaDays=7 → "May 19 – 25, 2026" (same month).
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 7));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("May 19 – 25, 2026", label.TextContent.Trim());
    }

    [Fact]
    public async Task Toolbar_Prev_Year_Steps_By_One_Year_When_View_Year()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year)
            .Add(c => c.DateChanged,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        await cut.InvokeAsync(() => cut.Find("[data-calee-region='toolbar-prev']").Click());
        Assert.NotNull(captured);
        Assert.Equal(2025, captured!.Value.Year);
    }

    [Fact]
    public async Task Toolbar_Prev_Agenda_Steps_By_AgendaDays()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 14)
            .Add(c => c.DateChanged,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        await cut.InvokeAsync(() => cut.Find("[data-calee-region='toolbar-prev']").Click());
        Assert.Equal(Anchor.AddDays(-14), captured);
    }

    [Fact]
    public void OnRangeChanged_Fires_With_Year_Range_When_Switched_To_Year()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initialCount = ranges.Count;
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year));

        Assert.True(ranges.Count > initialCount);
        var last = ranges[^1];
        // Jan 1, 2026 → Jan 1, 2027 in the configured zone.
        Assert.Equal(new DateTime(2026, 1, 1), last.Start.Date);
        Assert.Equal(new DateTime(2027, 1, 1), last.End.Date);
    }

    [Fact]
    public void OnRangeChanged_Fires_With_Agenda_Window_When_Switched_To_Agenda()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initialCount = ranges.Count;
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 3));

        Assert.True(ranges.Count > initialCount);
        var last = ranges[^1];
        Assert.Equal(Anchor.Date, last.Start.Date);
        Assert.Equal(Anchor.AddDays(3).Date, last.End.Date);
    }

    [Fact]
    public void ToolbarClass_Forwarded_To_Toolbar()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ToolbarClass, "my-fancy-toolbar"));

        var toolbar = cut.Find("[data-calee-region='toolbar']");
        var classes = toolbar.GetAttribute("class") ?? string.Empty;
        Assert.Contains("calee-scheduler-toolbar", classes);
        Assert.Contains("my-fancy-toolbar", classes);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Issue #31 — toolbar content slots forwarded from the root.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolbarStart_And_ToolbarEnd_Forwarded_To_Toolbar()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ToolbarStart, "<span data-testid=\"root-start\">Root start</span>")
            .Add(c => c.ToolbarEnd, "<span data-testid=\"root-end\">Root end</span>"));

        var start = cut.Find("[data-calee-region='toolbar-start']");
        var end = cut.Find("[data-calee-region='toolbar-end']");
        Assert.Contains("Root start", start.InnerHtml);
        Assert.Contains("Root end", end.InnerHtml);
    }

    [Fact]
    public void ToolbarStart_And_ToolbarEnd_Absent_When_ShowToolbar_False()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ShowToolbar, false)
            .Add(c => c.ToolbarStart, "<span>Root start</span>")
            .Add(c => c.ToolbarEnd, "<span>Root end</span>"));

        Assert.Empty(cut.FindAll("[data-calee-region='toolbar-start']"));
        Assert.Empty(cut.FindAll("[data-calee-region='toolbar-end']"));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Issue #7 — SchedulerView.WorkWeek on the root scheduler.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bindable_View_WorkWeek_Renders_FiveDayColumns_MonToFri_Default()
    {
        // Anchor Tue 2026-05-19, default FirstDayOfWeek=Sunday → week is Sun 5/17..Sat
        // 5/23. WorkWeekDays defaults to Mon–Fri → Mon 5/18 .. Fri 5/22.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek));

        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        var weekdays = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-weekday")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }, weekdays);
    }

    [Fact]
    public void WorkWeekDays_Override_Renders_Custom_Subset()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }));

        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        var weekdays = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-weekday")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "Tue", "Wed", "Thu" }, weekdays);
    }

    [Fact]
    public void WorkWeek_Regular_Week_View_Still_Renders_All_Seven_Days()
    {
        // Regression guard: WorkWeekDays must never leak into the plain Week case —
        // the root only forwards VisibleDays when WorkWeek is the active view.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Monday }));

        Assert.Equal(7, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
    }

    [Fact]
    public void WorkWeek_Toolbar_Switch_Sets_ActiveHighlight_And_RangeLabel()
    {
        // Uncontrolled mode (no View param bound) so the toolbar click actually flips
        // the root's own active view — mirrors
        // Toolbar_Uncontrolled_View_Switch_Updates_Active_Highlight_Same_Render.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var workWeekButton = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Work Week");
        workWeekButton.Click();

        var active = cut.FindAll("[data-calee-region='toolbar-view-button'][aria-checked='true']");
        Assert.Single(active);
        Assert.Equal("Work Week", active[0].TextContent.Trim());

        // Anchor is Tue 2026-05-19; default FirstDayOfWeek=Sunday, WorkWeekDays=Mon-Fri
        // → Mon 5/18 .. Fri 5/22 (same month) → "May 18 – 22, 2026".
        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("May 18 – 22, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void WorkWeekDays_Override_Flows_Through_To_Toolbar_RangeLabel()
    {
        // Regression guard for the cascade plumbing: the toolbar's range label must
        // reflect the *resolved* WorkWeekDays (via SchedulerStateContainer.WorkWeekDays),
        // not silently fall back to the Monday–Friday default when the consumer
        // overrides it.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }));

        // Anchor Tue 2026-05-19; default FirstDayOfWeek=Sunday → week Sun 5/17..Sat
        // 5/23. Tue/Wed/Thu subset → 5/19..5/21 → "May 19 – 21, 2026".
        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("May 19 – 21, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void WorkWeekDays_NonContiguous_MonWedFri_FirstDayOfWeekMonday_RendersInOrder_SingleRangeChangedFire()
    {
        // Non-contiguous subset guard: WorkWeekDays doesn't have to be a contiguous
        // run of days. FirstDayOfWeek=Monday → week Mon 5/18..Sun 5/24. Mon/Wed/Fri
        // subset → columns Mon 5/18, Wed 5/20, Fri 5/22, in that order. Range spans
        // first-visible-day start (Mon 00:00) to last-visible-day exclusive end
        // (Sat 00:00, the day after Friday) — reviewer-verified span.
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initialCount = ranges.Count;
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        var weekdays = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-weekday")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "Mon", "Wed", "Fri" }, weekdays);

        // Exactly one additional fire for the view switch.
        Assert.Equal(initialCount + 1, ranges.Count);
        var last = ranges[^1];
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.FromHours(-4)), last.Start);
        Assert.Equal(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.FromHours(-4)), last.End);
    }

    [Fact]
    public void WorkWeekDays_NonContiguous_MonWedFri_FirstDayOfWeekSunday_StillOrdersMonWedFri_RangeUnaffected()
    {
        // Guards the FirstDayOfWeek-not-first-visible interaction: FirstDayOfWeek=Sunday
        // means the underlying week grid starts on Sun 5/17, but the rendered WorkWeek
        // columns and the fired range are unaffected — Mon/Wed/Fri are the same calendar
        // dates regardless of which day the week grid starts on.
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Sunday)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initialCount = ranges.Count;
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Sunday)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        var weekdays = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-weekday")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "Mon", "Wed", "Fri" }, weekdays);

        Assert.Equal(initialCount + 1, ranges.Count);
        var last = ranges[^1];
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.FromHours(-4)), last.Start);
        Assert.Equal(new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.FromHours(-4)), last.End);
    }

    [Fact]
    public void WorkWeek_OnRangeChanged_Spans_FirstVisibleDayStart_To_LastVisibleDayEnd_SingleFire()
    {
        using var ctx = NewContext();
        var ranges = new List<SchedulerRange>();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => ranges.Add(r))));

        var initialCount = ranges.Count;
        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek));

        // Exactly one additional fire for the view switch.
        Assert.Equal(initialCount + 1, ranges.Count);
        var last = ranges[^1];
        Assert.Equal(new DateTime(2026, 5, 18), last.Start.Date);
        Assert.Equal(new DateTime(2026, 5, 23), last.End.Date); // exclusive end = day after Friday
    }

    [Fact]
    public async Task WorkWeek_Toolbar_Prev_Next_Step_Seven_Calendar_Days()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        await cut.InvokeAsync(() => cut.Find("[data-calee-region='toolbar-next']").Click());
        Assert.Equal(Anchor.AddDays(7), captured);

        await cut.InvokeAsync(() => cut.Find("[data-calee-region='toolbar-prev']").Click());
        Assert.Equal(Anchor.AddDays(-7), captured);
    }

    [Fact]
    public void WorkWeek_Toolbar_Today_Snaps_To_Current_Work_Week()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(this, d => captured = d)));

        cut.Find("[data-calee-region='toolbar-today']").Click();

        // "Today" reanchors to today's date; the composed Week view then derives its
        // own Mon-Fri window around that anchor, so the grid stays a 5-column work week.
        Assert.NotNull(captured);
        var todayInZone = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        Assert.Equal(todayInZone.Date, captured!.Value.Date);
        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        var weekdays = headers
            .Select(h => h.QuerySelector(".calee-scheduler-day-header-weekday")!.TextContent.Trim())
            .ToArray();
        Assert.Equal(new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }, weekdays);
    }

    [Fact]
    public void Bindable_View_Controlled_RoundTrips_WorkWeek()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day));

        Assert.NotNull(cut.Find(".calee-scheduler-day"));

        cut.Render(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek));

        Assert.Equal(5, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".calee-scheduler-day"));
    }

    [Fact]
    public async Task Keystroke_7_Flips_Root_To_WorkWeek_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));
        // Default-view is Week; the Week view's grid receives the keystroke.

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid']")
                .KeyDown(new KeyboardEventArgs { Key = "7" }));

        Assert.Equal(SchedulerView.WorkWeek, cut.Instance._internalView);
        Assert.Equal(5, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
    }

    [Fact]
    public async Task DisabledShortcuts_ViewWorkWeek_Suppresses_Keystroke_7()
    {
        using var ctx = NewContext();
        var ev = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.DisabledShortcuts, new[] { SchedulerCommandIds.ViewWorkWeek }));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid']")
                .KeyDown(new KeyboardEventArgs { Key = "7" }));

        Assert.Equal(SchedulerView.Week, cut.Instance._internalView);
    }

    [Fact]
    public async Task Palette_ViewWorkWeek_Invoke_Flips_Root_To_WorkWeek_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        var cmd = cut.Instance.Commands.First(c => c.Id == SchedulerCommandIds.ViewWorkWeek);
        await cut.InvokeAsync(() => cmd.Invoke());

        Assert.Equal(SchedulerView.WorkWeek, cut.Instance._internalView);
        Assert.Equal(5, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
    }

    [Fact]
    public void View_WorkWeek_Without_Lanes_Does_Not_Throw()
    {
        // Contrast with View=Timeline (which throws without Lanes/LaneKey — PRD §4.6):
        // WorkWeek is unconditionally renderable, same as Day/Week/Month, with no
        // Timeline-style availability gate (issue #7).
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek));

        Assert.Equal(5, cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']").Count);
    }

    [Fact]
    public void WorkWeek_Always_Present_In_AvailableViews_Regardless_Of_ShowYearAgendaButtons()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.ShowYearButton, false)
            .Add(c => c.ShowAgendaButton, false));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.Contains("Work Week", labels);
        Assert.Equal(new[] { "Day", "Work Week", "Week", "Month" }, labels);
    }
}
