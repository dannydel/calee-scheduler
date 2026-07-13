using System.Diagnostics;
using System.Globalization;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;

namespace Calee.Scheduler.Tests;

public class AgendaPerformanceThresholdTests
{
    private const string ThresholdVariable = "CALEE_AGENDA_MEDIAN_THRESHOLD_MS";
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void VirtualizationBoundsInitiallyMountedGroups()
    {
        using var context = NewContext();
        var events = BuildEvents(2_000);

        var cut = Render(context, events, enableVirtualization: true);

        Assert.Equal(90, cut.Instance.Groups.Count);
        Assert.InRange(cut.FindAll("[data-calee-region='agenda-group']").Count, 1, 12);
        Assert.Equal(2_000, cut.Instance.Groups.Sum(group => group.Rows.Length));
    }

    [Fact]
    public void DragEnabledAgendaRetainsFullRendering()
    {
        using var context = NewContext();
        var events = BuildEvents(90);

        var cut = context.Render<CaleeSchedulerAgendaView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.AgendaDays, 90)
            .Add(component => component.Events, events)
            .Add(component => component.EnableVirtualization, true)
            .Add(component => component.AllowDragToMove, true));

        Assert.Equal(90, cut.FindAll("[data-calee-region='agenda-group']").Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void VirtualizedAgendaRerenderStaysBelowCiMedianThreshold()
    {
        var configuredThreshold = Environment.GetEnvironmentVariable(ThresholdVariable);
        if (string.IsNullOrWhiteSpace(configuredThreshold)) return;

        Assert.True(
            double.TryParse(configuredThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var thresholdMs)
            && thresholdMs > 0,
            $"{ThresholdVariable} must be a positive invariant-culture number.");

        using var context = NewContext();
        var events = BuildEvents(2_000);
        var cut = Render(context, events, enableVirtualization: true);

        for (var i = 0; i < 3; i++) cut.Render();

        var samples = new double[9];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            cut.Render();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        var medianMs = samples[samples.Length / 2];
        Assert.True(
            medianMs <= thresholdMs,
            $"Virtualized Agenda 2,000-event rerender median {medianMs:F2}ms exceeded "
            + $"{thresholdMs:F2}ms. Samples: {string.Join(", ", samples.Select(sample => sample.ToString("F2", CultureInfo.InvariantCulture)))}ms.");
    }

    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.Services.AddCaleeScheduler();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static IRenderedComponent<CaleeSchedulerAgendaView<CalendarEvent>> Render(
        BunitContext context,
        IReadOnlyList<CalendarEvent> events,
        bool enableVirtualization) =>
        context.Render<CaleeSchedulerAgendaView<CalendarEvent>>(parameters => parameters
            .Add(component => component.TimeZone, TZ)
            .Add(component => component.Date, Anchor)
            .Add(component => component.AgendaDays, 90)
            .Add(component => component.Events, events)
            .Add(component => component.EnableVirtualization, enableVirtualization));

    private static IReadOnlyList<CalendarEvent> BuildEvents(int count)
    {
        var events = new CalendarEvent[count];
        for (var i = 0; i < count; i++)
        {
            var start = Anchor.AddDays(i % 90).AddMinutes(i / 90);
            events[i] = new CalendarEvent($"event-{i:D4}", $"Event {i}", start, start.AddMinutes(30));
        }
        return events;
    }
}
