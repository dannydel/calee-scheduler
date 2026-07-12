using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;

namespace Calee.Scheduler.Tests;

public class EventIdentityValidationTests
{
    private static readonly TimeZoneInfo TimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void DuplicateIdsAfterFilteringThrowBeforeRendering()
    {
        using var context = NewContext();
        var events = new[]
        {
            Timed("duplicate", 9),
            Timed("duplicate", 11),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            context.Render<CaleeSchedulerDayView<CalendarEvent>>(parameters => parameters
                .Add(component => component.TimeZone, TimeZone)
                .Add(component => component.Date, Anchor)
                .Add(component => component.Events, events)));

        Assert.Equal("Events", exception.ParamName);
        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterMayRemoveAnOtherwiseDuplicateId()
    {
        using var context = NewContext();
        var kept = Timed("duplicate", 9);
        var removed = Timed("duplicate", 11);

        var cut = context.Render<CaleeSchedulerDayView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TimeZone)
            .Add(component => component.Date, Anchor)
            .Add(component => component.Events, new[] { kept, removed })
            .Add(component => component.EventFilter, calendarEvent => ReferenceEquals(calendarEvent, kept)));

        Assert.Single(cut.FindAll("[data-calee-region='event']"));
    }

    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.Services.AddCaleeScheduler();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static CalendarEvent Timed(string id, int hour) =>
        new(id, id, Anchor.AddHours(hour), Anchor.AddHours(hour + 1));
}
