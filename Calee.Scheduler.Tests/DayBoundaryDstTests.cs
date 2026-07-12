using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;

namespace Calee.Scheduler.Tests;

public class DayBoundaryDstTests
{
    private static readonly TimeZoneInfo TimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Theory]
    [InlineData(2026, 3, 8, -5, -4, 23)]
    [InlineData(2026, 11, 1, -4, -5, 25)]
    public void MidnightBoundsResolveEachDatesOffset(
        int year,
        int month,
        int day,
        int expectedStartOffsetHours,
        int expectedEndOffsetHours,
        int expectedDurationHours)
    {
        var date = new DateTime(year, month, day);
        var start = SchedulerViewPrimitives.MidnightInZone(date, TimeZone);
        var end = SchedulerViewPrimitives.MidnightInZone(date.AddDays(1), TimeZone);

        Assert.Equal(TimeSpan.FromHours(expectedStartOffsetHours), start.Offset);
        Assert.Equal(TimeSpan.FromHours(expectedEndOffsetHours), end.Offset);
        Assert.Equal(TimeSpan.FromHours(expectedDurationHours), end - start);
    }

    [Fact]
    public void WeekAndTimelineDayBoundsPreserveSpringForwardDay()
    {
        var anchor = ZonedMidnight(2026, 3, 8);
        var week = SchedulerViewPrimitives.ComputeWeekDays(anchor, DayOfWeek.Sunday, TimeZone);

        Assert.Equal(TimeSpan.FromHours(-5), week[0].Start.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), week[0].End.Offset);
        Assert.Equal(TimeSpan.FromHours(23), week[0].End - week[0].Start);

        using var context = NewContext();
        var cut = context.Render<CaleeSchedulerTimelineView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, anchor)
            .Add(component => component.TimeScale, TimelineScale.Day)
            .Add(component => component.Lanes, new ILane[] { new Lane("lane", "Lane") })
            .Add(component => component.LaneKey, _ => "lane"));

        var bounds = Assert.Single(cut.Instance.DayBounds);
        Assert.Equal(week[0], bounds);
    }

    [Fact]
    public void MonthDoesNotPlaceEarlyNextDayEventInSpringForwardCell()
    {
        using var context = NewContext();
        var anchor = ZonedMidnight(2026, 3, 8);
        var eventStart = new DateTimeOffset(2026, 3, 9, 0, 30, 0, TimeSpan.FromHours(-4));
        var calendarEvent = new CalendarEvent(
            "after-transition",
            "After transition",
            eventStart,
            eventStart.AddMinutes(30));

        var cut = context.Render<CaleeSchedulerMonthView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, anchor)
            .Add(component => component.Events, new[] { calendarEvent }));

        var march8 = cut.Find("[data-calee-date='2026-03-08']");
        var march9 = cut.Find("[data-calee-date='2026-03-09']");
        Assert.Empty(march8.QuerySelectorAll("[data-calee-region='event']"));
        Assert.Single(march9.QuerySelectorAll("[data-calee-region='event']"));
    }

    [Fact]
    public async Task DaySlotAfterSpringTransitionCarriesTheApplicableOffset()
    {
        using var context = NewContext();
        SchedulerSlot? captured = null;
        var cut = context.Render<CaleeSchedulerDayView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, ZonedMidnight(2026, 3, 8))
            .Add(component => component.StartHour, 0)
            .Add(component => component.EndHour, 4)
            .Add(component => component.SlotDurationMinutes, 60)
            .Add(component => component.OnSlotClicked,
                EventCallback.Factory.Create<SchedulerSlot>(this, slot => captured = slot)));

        await cut.InvokeAsync(() => cut.Instance.HandleSlotClickAsync(3));

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Start.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), captured.Start.Offset);
        Assert.Equal(4, captured.End.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), captured.End.Offset);
    }

    [Fact]
    public void YearAndAgendaCallbacksExposeSpringForwardBounds()
    {
        using var context = NewContext();
        var anchor = ZonedMidnight(2026, 3, 8);

        var year = context.Render<CaleeSchedulerYearView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, anchor));
        var yearCell = year.Instance.Months[2].Cells.Single(cell => cell.Date == new DateOnly(2026, 3, 8));
        AssertSpringForwardBounds(yearCell.Start, yearCell.End);

        var agendaEvent = new CalendarEvent(
            "agenda",
            "Agenda",
            anchor.AddHours(12),
            anchor.AddHours(13));
        var agenda = context.Render<CaleeSchedulerAgendaView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, anchor)
            .Add(component => component.Events, new[] { agendaEvent }));
        var group = Assert.Single(agenda.Instance.Groups);
        AssertSpringForwardBounds(group.Start, group.End);
    }

    private static void AssertSpringForwardBounds(DateTimeOffset start, DateTimeOffset end)
    {
        Assert.Equal(TimeSpan.FromHours(-5), start.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), end.Offset);
        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    private static DateTimeOffset ZonedMidnight(int year, int month, int day) =>
        SchedulerViewPrimitives.MidnightInZone(new DateTime(year, month, day), TimeZone);

    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.Services.AddCaleeScheduler();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
