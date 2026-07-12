using System.Diagnostics;
using System.Globalization;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Internal;

namespace Calee.Scheduler.Tests;

public class LayoutPerformanceThresholdTests
{
    private const string ThresholdVariable = "CALEE_LAYOUT_MEDIAN_THRESHOLD_MS";
    private static readonly DateTimeOffset Day = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Performance")]
    public void DenseStaggeredLayoutStaysBelowCiMedianThreshold()
    {
        var configuredThreshold = Environment.GetEnvironmentVariable(ThresholdVariable);
        if (string.IsNullOrWhiteSpace(configuredThreshold)) return;

        Assert.True(
            double.TryParse(configuredThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var thresholdMs)
            && thresholdMs > 0,
            $"{ThresholdVariable} must be a positive invariant-culture number.");

        var events = BuildDenseStaggeredEvents(800);
        var engine = new EventLayoutEngine();

        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(engine.Layout(events, Day, Day.AddDays(2)));
        }

        var samples = new double[9];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = engine.Layout(events, Day, Day.AddDays(2));
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(events.Count, result.Positioned.Count);
        }

        Array.Sort(samples);
        var medianMs = samples[samples.Length / 2];
        Assert.True(
            medianMs <= thresholdMs,
            $"Dense 800-event layout median {medianMs:F2}ms exceeded CI threshold {thresholdMs:F2}ms. "
            + $"Samples: {string.Join(", ", samples.Select(sample => sample.ToString("F2", CultureInfo.InvariantCulture)))}ms.");
    }

    private static IReadOnlyList<ICalendarEvent> BuildDenseStaggeredEvents(int count)
    {
        var events = new ICalendarEvent[count];
        for (var i = 0; i < count; i++)
        {
            var start = Day.AddMinutes(i);
            events[i] = new CalendarEvent(
                $"dense-{i:D4}",
                $"Dense {i}",
                start,
                start.AddMinutes(count));
        }
        return events;
    }
}
