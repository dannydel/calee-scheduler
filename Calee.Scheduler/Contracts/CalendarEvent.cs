namespace Calee.Scheduler.Contracts;

/// <summary>
/// Default <see cref="ICalendarEvent"/> implementation. Suitable for demos and for
/// consumers who do not need a custom domain type — write <c>TEvent="CalendarEvent"</c>
/// at the call site.
/// </summary>
/// <param name="Id">Stable identifier used for tracking and keyed rendering.</param>
/// <param name="Title">Short display title rendered inside the event chip.</param>
/// <param name="Start">Event start instant.</param>
/// <param name="End">Event end instant. Expected to be greater than or equal to <paramref name="Start"/>.</param>
/// <param name="IsAllDay">True when the event renders in the all-day banner strip.</param>
/// <param name="Color">Optional CSS color value; <see langword="null"/> falls back to the theme default.</param>
/// <remarks>
/// Intentionally lacks a <c>Metadata</c> bag. Consumers needing extra fields should
/// implement <see cref="ICalendarEvent"/> on their own type rather than smuggling data
/// through an untyped property. See ADR-0004.
/// </remarks>
public sealed record CalendarEvent(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay = false,
    string? Color = null
) : ICalendarEvent;
