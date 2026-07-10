using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for issue #34's layered <c>TimeZone</c> resolution: explicit parameter →
/// ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c> → <see cref="CaleeSchedulerOptions.DefaultTimeZone"/>
/// → <see cref="InvalidOperationException"/>. Exercised through the root
/// <see cref="CaleeScheduler{TEvent}"/> so the resolution flows through
/// <c>OnParametersSet</c> exactly as a consumer would experience it.
/// </summary>
public class CaleeSchedulerTimeZoneResolutionTests
{
    // Three distinct zones so a test failure can't accidentally pass by mixing up
    // which rung of the chain actually won.
    private static readonly TimeZoneInfo ParamTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo CascadeTz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly TimeZoneInfo OptionTz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext(TimeZoneInfo? defaultTimeZone = null)
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler(o => o.DefaultTimeZone = defaultTimeZone);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static CalendarEvent Timed(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: false);

    [Fact]
    public void Explicit_Param_Wins_Over_Cascade_And_Option()
    {
        using var ctx = NewContext(defaultTimeZone: OptionTz);

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .AddCascadingValue(CascadeTz)
            .Add(c => c.TimeZone, ParamTz)
            .Add(c => c.Date, Anchor));

        Assert.Same(ParamTz, cut.Instance.State.TimeZone);
    }

    [Fact]
    public void Cascade_Wins_Over_Option_When_Param_Omitted()
    {
        using var ctx = NewContext(defaultTimeZone: OptionTz);

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .AddCascadingValue(CascadeTz)
            .Add(c => c.Date, Anchor));

        Assert.Same(CascadeTz, cut.Instance.State.TimeZone);
    }

    [Fact]
    public void Option_Resolves_When_Param_And_Cascade_Both_Omitted()
    {
        using var ctx = NewContext(defaultTimeZone: OptionTz);

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.Date, Anchor));

        Assert.Same(OptionTz, cut.Instance.State.TimeZone);
    }

    [Fact]
    public void None_Supplied_Throws_InvalidOperationException_With_Actionable_Message()
    {
        using var ctx = NewContext(defaultTimeZone: null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
                .Add(c => c.Date, Anchor)));

        Assert.Contains("TimeZone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CascadingValue", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DefaultTimeZone", ex.Message, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Invariant: events still render at their literal DateTimeOffset (ADR-0001) —
    // only the grid frame (today / day boundaries / slot offsets) reads the
    // resolved zone. Resolving TimeZone from a cascade instead of the explicit
    // parameter must not change how an event's own Start/End render.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Event_Renders_Identically_Whether_TimeZone_Comes_From_Param_Or_Cascade()
    {
        // The event's own DateTimeOffset carries an offset (+05:30, India) that matches
        // neither the grid zone nor either candidate resolution source — if the library
        // ever "re-timezoned" the event itself (rather than just anchoring the grid),
        // the two renders below would disagree. The absolute instant chosen here is
        // noon–1 PM in CascadeTz (America/Chicago, CDT -05:00 on this date), so it falls
        // inside the Day view's default 8 AM–6 PM window.
        var eventStart = new DateTimeOffset(2026, 5, 19, 22, 30, 0, TimeSpan.FromHours(5.5));
        var eventEnd = eventStart.AddHours(1);
        var ev = Timed("e1", eventStart, eventEnd);

        using var explicitCtx = NewContext();
        var explicitCut = explicitCtx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, CascadeTz)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, new[] { ev }));

        using var cascadeCtx = NewContext();
        var cascadeCut = cascadeCtx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .AddCascadingValue(CascadeTz)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.Events, new[] { ev }));

        var explicitChip = explicitCut.Find("[data-calee-region='event']");
        var cascadeChip = cascadeCut.Find("[data-calee-region='event']");

        // Same absolute instant, same resolved grid zone (CascadeTz either way) →
        // byte-identical accessible label and displayed time text, regardless of
        // which rung of the chain supplied the zone.
        Assert.Equal(
            explicitChip.GetAttribute("aria-label"),
            cascadeChip.GetAttribute("aria-label"));

        // And that shared label reflects the event's literal instant converted into the
        // resolved grid zone — not the event's own embedded +05:30 offset, and not some
        // other zone the resolution chain didn't actually pick.
        var expectedTimeText = SchedulerViewPrimitives.FormatEventTimeRange(ev, CascadeTz);
        Assert.Contains(expectedTimeText, explicitChip.GetAttribute("aria-label"), StringComparison.Ordinal);
    }
}
