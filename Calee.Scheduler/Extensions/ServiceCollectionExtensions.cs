using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Extensions;

/// <summary>
/// Dependency-injection registration helpers for Calee.Scheduler.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Calee.Scheduler services and binds <see cref="CaleeSchedulerOptions"/>.
    /// Per issue #34, <c>TimeZone</c> is resolved per component via a layered chain —
    /// explicit parameter → ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c> →
    /// <see cref="CaleeSchedulerOptions.DefaultTimeZone"/> configured here → throw — so
    /// setting <c>o.DefaultTimeZone</c> in <paramref name="configure"/> is one way to
    /// supply a service-level fallback without threading <c>TimeZone</c> through every
    /// component (ADR-0001).
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">
    /// Optional callback that mutates the <see cref="CaleeSchedulerOptions"/> instance.
    /// When <c>null</c>, the defaults documented on <see cref="CaleeSchedulerOptions"/>
    /// are used. The options object is always registered.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <remarks>
    /// Validation is wired through <c>OptionsBuilder&lt;T&gt;.Validate(...)</c>
    /// (<see cref="Microsoft.Extensions.Options.OptionsBuilder{TOptions}"/>).
    /// Per PRD §4.6 the library hard-fails on contract violations:
    /// <list type="bullet">
    ///   <item><description><c>DefaultStartHour</c> outside <c>[0, 24]</c></description></item>
    ///   <item><description><c>DefaultEndHour</c> outside <c>[0, 24]</c></description></item>
    ///   <item><description><c>DefaultStartHour &gt; DefaultEndHour</c></description></item>
    ///   <item><description><c>DefaultSlotDurationMinutes</c> not in <c>{15, 30, 60}</c></description></item>
    ///   <item><description><c>DefaultMaxEventsPerDay &lt; 1</c></description></item>
    /// </list>
    /// Each violation surfaces as a <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
    /// on the first <c>IOptions&lt;CaleeSchedulerOptions&gt;.Value</c> access (the
    /// framework-idiomatic behavior — eager <c>ValidateOnStart</c> would require an extra
    /// reference to <c>Microsoft.Extensions.Hosting</c>, which the RCL deliberately avoids).
    /// </remarks>
    public static IServiceCollection AddCaleeScheduler(
        this IServiceCollection services,
        Action<CaleeSchedulerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<CaleeSchedulerOptions>();

        if (configure is not null)
        {
            builder.Configure(configure);
        }

        builder
            .Validate(
                o => o.DefaultStartHour >= 0,
                "CaleeSchedulerOptions.DefaultStartHour must be >= 0.")
            .Validate(
                o => o.DefaultEndHour <= 24,
                "CaleeSchedulerOptions.DefaultEndHour must be <= 24.")
            .Validate(
                o => o.DefaultStartHour <= o.DefaultEndHour,
                "CaleeSchedulerOptions.DefaultStartHour must be <= DefaultEndHour.")
            .Validate(
                o => o.DefaultSlotDurationMinutes is 15 or 30 or 60,
                "CaleeSchedulerOptions.DefaultSlotDurationMinutes must be one of {15, 30, 60}.")
            .Validate(
                o => o.DefaultMaxEventsPerDay >= 1,
                "CaleeSchedulerOptions.DefaultMaxEventsPerDay must be >= 1.")
            .Validate(
                o => o.DefaultMaxOverlapColumns >= 2,
                "CaleeSchedulerOptions.DefaultMaxOverlapColumns must be >= 2.");

        return services;
    }
}
