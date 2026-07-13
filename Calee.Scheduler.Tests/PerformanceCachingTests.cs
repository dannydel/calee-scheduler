using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

public class PerformanceCachingTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Day_UnchangedInputsReuseGeometry_AndInPlaceMutationInvalidatesIt()
    {
        using var context = NewContext();
        var ev = new MutableEvent("a", "A", Anchor.AddHours(9), Anchor.AddHours(10));
        var events = new List<MutableEvent> { ev };
        var cut = RenderDay(context, events);
        var initialLayout = cut.Instance.PositionedEvents;

        SetDayParameters(cut, events);
        Assert.Same(initialLayout, cut.Instance.PositionedEvents);

        ev.Start = Anchor.AddHours(11);
        ev.End = Anchor.AddHours(12);
        SetDayParameters(cut, events);

        Assert.NotSame(initialLayout, cut.Instance.PositionedEvents);
        Assert.Equal(30, Assert.Single(cut.Instance.PositionedEvents).TimeStartPercent, 6);
    }

    [Fact]
    public void Day_FilterClosureChangesInvalidateGeometry_AndStaleRefsArePruned()
    {
        using var context = NewContext();
        var include = true;
        Func<MutableEvent, bool> filter = _ => include;
        var events = new List<MutableEvent>
        {
            new("a", "A", Anchor.AddHours(9), Anchor.AddHours(10)),
        };
        var cut = context.Render<CaleeSchedulerDayView<MutableEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events)
            .Add(component => component.EventFilter, filter));

        Assert.Contains("a", cut.Instance.EventRefsByEventId.Keys);

        include = false;
        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events)
            .Add(component => component.EventFilter, filter));

        Assert.Empty(cut.Instance.PositionedEvents);
        Assert.Empty(cut.Instance.EventRefsByEventId);
    }

    [Fact]
    public void Timeline_LaneProjectionClosureChangeInvalidatesGeometry()
    {
        using var context = NewContext();
        var laneId = "a";
        Func<MutableEvent, string?> laneKey = _ => laneId;
        var lanes = new ILane[] { new Lane("a", "A"), new Lane("b", "B") };
        var events = new List<MutableEvent>
        {
            new("event", "Event", Anchor.AddHours(9), Anchor.AddHours(10)),
        };
        var cut = context.Render<CaleeSchedulerTimelineView<MutableEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Lanes, lanes)
            .Add(component => component.LaneKey, laneKey)
            .Add(component => component.Events, events));

        Assert.Single(cut.Instance.Row(0).Layout.Positioned);
        Assert.Empty(cut.Instance.Row(1).Layout.Positioned);

        laneId = "b";
        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Lanes, lanes)
            .Add(component => component.LaneKey, laneKey)
            .Add(component => component.Events, events));

        Assert.Empty(cut.Instance.Row(0).Layout.Positioned);
        Assert.Single(cut.Instance.Row(1).Layout.Positioned);
    }

    [Fact]
    public void Agenda_UnchangedInputsReuseGroups_And_Exclude_OffWindow_Lookups()
    {
        using var context = NewContext();
        var visible = new MutableEvent("visible", "Visible", Anchor.AddHours(9), Anchor.AddHours(10));
        var future = new MutableEvent("future", "Future", Anchor.AddDays(30), Anchor.AddDays(30).AddHours(1));
        var events = new List<MutableEvent> { visible, future };
        var cut = context.Render<CaleeSchedulerAgendaView<MutableEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));
        var initialGroups = cut.Instance.Groups;

        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));

        Assert.Same(initialGroups, cut.Instance.Groups);
        Assert.NotNull(cut.Instance.TypedForId("visible"));
        Assert.Null(cut.Instance.TypedForId("future"));

        visible.Start = Anchor.AddHours(11);
        visible.End = Anchor.AddHours(12);
        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));

        Assert.NotSame(initialGroups, cut.Instance.Groups);
    }

    [Fact]
    public void Year_UnchangedInputsReuseMonths_AndInPlaceMutationInvalidatesDensity()
    {
        using var context = NewContext();
        var ev = new MutableEvent("event", "Event", Anchor.AddHours(9), Anchor.AddHours(10));
        var events = new List<MutableEvent> { ev };
        var cut = context.Render<CaleeSchedulerYearView<MutableEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));
        var initialMonths = cut.Instance.Months;

        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));

        Assert.Same(initialMonths, cut.Instance.Months);

        ev.Start = Anchor.AddDays(1).AddHours(9);
        ev.End = Anchor.AddDays(1).AddHours(10);
        cut.Render(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));

        Assert.NotSame(initialMonths, cut.Instance.Months);
    }

    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.Services.AddCaleeScheduler();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static IRenderedComponent<CaleeSchedulerDayView<MutableEvent>> RenderDay(
        BunitContext context,
        IReadOnlyList<MutableEvent> events) =>
        context.Render<CaleeSchedulerDayView<MutableEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, events));

    private static void SetDayParameters(
        IRenderedComponent<CaleeSchedulerDayView<MutableEvent>> component,
        IReadOnlyList<MutableEvent> events) =>
        component.Render(parameters => parameters
            .Add(view => view.TimeZone, TZ)
            .Add(view => view.Date, Anchor)
            .Add(view => view.Events, events));

    private sealed class MutableEvent(
        string id,
        string title,
        DateTimeOffset start,
        DateTimeOffset end) : ICalendarEvent
    {
        public string Id { get; set; } = id;
        public string Title { get; set; } = title;
        public DateTimeOffset Start { get; set; } = start;
        public DateTimeOffset End { get; set; } = end;
        public bool IsAllDay { get; set; }
        public string? Color { get; set; }
    }
}
