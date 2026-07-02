using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerOptions"/> and the
/// <see cref="ServiceCollectionExtensions.AddCaleeScheduler"/> DI extension.
/// Covers PRD §4.3 (defaults) and PRD §4.6 (hard-fail validation).
/// </summary>
public class CaleeSchedulerOptionsTests
{
    private static CaleeSchedulerOptions Resolve(Action<CaleeSchedulerOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddCaleeScheduler(configure);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<CaleeSchedulerOptions>>().Value;
    }

    [Fact]
    public void AddCaleeScheduler_WithoutConfigure_UsesPrdDefaults()
    {
        var options = Resolve(configure: null);

        Assert.Equal(SchedulerView.Week, options.DefaultView);
        Assert.Equal(8, options.DefaultStartHour);
        Assert.Equal(18, options.DefaultEndHour);
        Assert.Equal(30, options.DefaultSlotDurationMinutes);
        Assert.Equal(DayOfWeek.Sunday, options.DefaultFirstDayOfWeek);
        Assert.Equal(3, options.DefaultMaxEventsPerDay);
    }

    [Fact]
    public void AddCaleeScheduler_WithConfigure_AppliesOverride()
    {
        var options = Resolve(o => o.DefaultView = SchedulerView.Day);

        Assert.Equal(SchedulerView.Day, options.DefaultView);
        // Untouched properties retain defaults.
        Assert.Equal(8, options.DefaultStartHour);
        Assert.Equal(18, options.DefaultEndHour);
    }

    [Fact]
    public void Validation_Fails_When_StartHour_Greater_Than_EndHour()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o =>
        {
            o.DefaultStartHour = 20;
            o.DefaultEndHour = 10;
        }));

        Assert.Contains(
            "DefaultStartHour must be <= DefaultEndHour",
            string.Join(" | ", ex.Failures));
    }

    [Fact]
    public void Validation_Fails_When_SlotDurationMinutes_Is_20()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o =>
        {
            o.DefaultSlotDurationMinutes = 20;
        }));

        Assert.Contains(
            "DefaultSlotDurationMinutes must be one of {15, 30, 60}",
            string.Join(" | ", ex.Failures));
    }

    [Fact]
    public void Validation_Fails_When_MaxEventsPerDay_Is_Zero()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(o =>
        {
            o.DefaultMaxEventsPerDay = 0;
        }));

        Assert.Contains(
            "DefaultMaxEventsPerDay must be >= 1",
            string.Join(" | ", ex.Failures));
    }

    [Fact]
    public void DefaultMaxOverlapColumns_DefaultsToThree()
    {
        var opts = Resolve(_ => { });
        Assert.Equal(3, opts.DefaultMaxOverlapColumns);
    }

    [Fact]
    public void DefaultMaxOverlapColumns_LessThanTwo_Throws()
    {
        Assert.Throws<OptionsValidationException>(() =>
            Resolve(o => o.DefaultMaxOverlapColumns = 1));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Issue #7 — SchedulerView.WorkWeek as a valid DefaultView.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddCaleeScheduler_WithConfigure_AcceptsWorkWeek_AsDefaultView()
    {
        // WorkWeek must validate like any other declared view — no exception, and the
        // resolved options carry the value through untouched.
        var options = Resolve(o => o.DefaultView = SchedulerView.WorkWeek);

        Assert.Equal(SchedulerView.WorkWeek, options.DefaultView);
    }
}
