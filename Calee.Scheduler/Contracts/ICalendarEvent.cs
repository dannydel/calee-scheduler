namespace Calee.Scheduler.Contracts;

/// <summary>
/// Minimum contract a type must satisfy to be rendered by Calee.Scheduler views.
/// </summary>
/// <remarks>
/// Deliberately narrow: no <c>Metadata</c>, <c>Description</c>, <c>Location</c>, or other
/// "kitchen-sink" fields. Consumers project their own domain types (or extend
/// <see cref="CalendarEvent"/>) and bind them via the generic <c>TEvent</c> parameter
/// on every view. See ADR-0004 for the generic-only rationale.
/// <para>
/// The <see cref="Start"/> and <see cref="End"/> times are <see cref="DateTimeOffset"/> on
/// purpose — the library never converts time zones. Consumers normalize upstream and pass
/// the desired display zone via the <c>TimeZone</c> parameter. See ADR-0001.
/// </para>
/// </remarks>
public interface ICalendarEvent
{
    /// <summary>Stable identifier used for tracking, keyed rendering, and drag-lifecycle correlation.</summary>
    string Id { get; }

    /// <summary>Short display title rendered inside the event chip.</summary>
    string Title { get; }

    /// <summary>Event start instant. Compared in the view's configured <c>TimeZone</c>.</summary>
    DateTimeOffset Start { get; }

    /// <summary>Event end instant. Expected to be greater than or equal to <see cref="Start"/>.</summary>
    DateTimeOffset End { get; }

    /// <summary>
    /// True when the event represents an all-day block. All-day events render in the
    /// all-day banner strip rather than inside the time grid.
    /// </summary>
    bool IsAllDay { get; }

    /// <summary>
    /// Optional CSS color (any valid CSS <c>color</c> value). When <see langword="null"/>,
    /// the theme's default event color applies.
    /// </summary>
    string? Color { get; }
}
