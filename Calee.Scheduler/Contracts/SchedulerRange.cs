namespace Calee.Scheduler.Contracts;

/// <summary>
/// The visible date/time range currently displayed by a view. Surfaced via
/// <c>OnRangeChanged</c> so consumers can fetch only the events they need
/// for the window. Half-open interval: <see cref="Start"/> is inclusive,
/// <see cref="End"/> is exclusive.
/// </summary>
/// <param name="Start">Inclusive start of the visible range, in the view's configured <c>TimeZone</c>.</param>
/// <param name="End">Exclusive end of the visible range, in the view's configured <c>TimeZone</c>.</param>
public sealed record SchedulerRange(DateTimeOffset Start, DateTimeOffset End);
