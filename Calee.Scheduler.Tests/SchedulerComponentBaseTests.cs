using Bunit;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="SchedulerComponentBase{TEvent}"/> covering PRD §4.6 validation,
/// the soft-degradation of null events (PRD §4.6), <c>EventFilter</c> (FR-19b),
/// <c>AdditionalAttributes</c> splatting (FR-53), and the <c>EventClass</c> per-event hook
/// (FR-54).
/// </summary>
public class SchedulerComponentBaseTests
{
    /// <summary>
    /// Trivial concrete view for tests. Renders a single &lt;div&gt; with splattable
    /// attributes so consumers (and these tests) can verify <c>AdditionalAttributes</c>
    /// flow onto the outermost element.
    /// </summary>
    private sealed class TestView : SchedulerComponentBase<CalendarEvent>
    {
        public IReadOnlyList<CalendarEvent> GetFilteredEventsForTest() => GetFilteredEvents();

        public string? GetEventClassForTest(CalendarEvent ev) => GetEventClass(ev);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "calee-test-view");
            builder.AddMultipleAttributes(2, AdditionalAttributes);
            builder.CloseElement();
        }
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        return ctx;
    }

    private static CalendarEvent Event(string id, int hour = 9) =>
        new(id, id, new DateTimeOffset(2026, 5, 18, hour, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 18, hour + 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Missing_TimeZone_Throws()
    {
        using var ctx = NewContext();

        // Don't pass TimeZone, and don't supply a cascade or a DefaultTimeZone option
        // (NewContext's AddCaleeScheduler() leaves DefaultTimeZone at its null default).
        // Mounting triggers OnParametersSet → the layered resolution's terminal throw
        // (issue #34).
        var ex = Assert.Throws<InvalidOperationException>(() => ctx.Render<TestView>());
        Assert.Contains("TimeZone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CascadingValue", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DefaultTimeZone", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_Events_TreatedAsEmpty()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;

        var cut = ctx.Render<TestView>(p => p
            .Add(c => c.TimeZone, tz));

        var filtered = cut.Instance.GetFilteredEventsForTest();
        Assert.Empty(filtered);
    }

    [Fact]
    public void EventFilter_AppliesBeforeRender()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;

        var events = new[] { Event("a", 9), Event("b", 10), Event("c", 11) };

        var cut = ctx.Render<TestView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.Events, events)
            .Add(c => c.EventFilter, e => e.Id == "b"));

        var filtered = cut.Instance.GetFilteredEventsForTest();
        Assert.Single(filtered);
        Assert.Equal("b", filtered[0].Id);
    }

    [Fact]
    public void AdditionalAttributes_SplatsOntoOuterElement()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;

        var cut = ctx.Render<TestView>(p => p
            .Add(c => c.TimeZone, tz)
            .AddUnmatched("class", "custom-consumer-class")
            .AddUnmatched("data-test", "splat-target"));

        var root = cut.Find("div");
        // The library applies its own class, then splats — consumer attributes appear on
        // the outermost element.
        Assert.Contains("custom-consumer-class", root.GetAttribute("class") ?? string.Empty);
        Assert.Equal("splat-target", root.GetAttribute("data-test"));
    }

    [Fact]
    public void EventClass_AppliedPerEvent()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;
        var e1 = Event("event1", 9);

        var cut = ctx.Render<TestView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.Events, new[] { e1 })
            .Add(c => c.EventClass, e => "ev-" + e.Id));

        Assert.Equal("ev-event1", cut.Instance.GetEventClassForTest(e1));
    }

    [Fact]
    public void EventClass_NullHook_ReturnsNull()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;
        var e1 = Event("event1", 9);

        var cut = ctx.Render<TestView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.Events, new[] { e1 }));

        Assert.Null(cut.Instance.GetEventClassForTest(e1));
    }
}
