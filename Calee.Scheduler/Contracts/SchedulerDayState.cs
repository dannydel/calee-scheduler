namespace Calee.Scheduler.Contracts;

/// <summary>
/// Per-day state returned by the consumer's <c>DayModifier</c> hook (issue #8). A
/// <see langword="null"/> return from the hook means "normal day" — this type only
/// carries the deviation, so the default (no hook, or a hook that returns
/// <see langword="null"/> for a given day) is zero visual or behavioral change.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Create-only suppression.</strong> A day with <see cref="IsBlocked"/> =
/// <see langword="true"/> suppresses the view's create affordances on that day —
/// double-click-to-create, drag-to-create, and the create-at-focus keystroke are all
/// no-ops there (fail-closed: no phantom event, no <c>OnEventCreated</c>). It does
/// <strong>not</strong> suppress drag-to-move/resize <em>onto</em> the day (those still
/// fire <c>OnEventMoved</c> / <c>OnEventResized</c> — reject via the context's
/// <c>Cancel</c> flag if the consumer's permission model requires it) and it does not
/// suppress <c>OnSlotClicked</c> (selection/navigation is not creation).
/// </para>
/// <para>
/// <strong>Multi-day create sweeps.</strong> When a drag-to-create's spanned region
/// touches <em>any</em> blocked day, the create does not fire — this is the simplest
/// defensible fail-closed rule for a sweep that starts on an open day and crosses into
/// a blocked one (or vice versa).
/// </para>
/// <para>
/// This is a record rather than a mutable context class (contrast
/// <c>EventMoveContext</c>) — the hook is a pure projection from a day to its state,
/// with no in/out "the library reads this back" channel to justify mutability.
/// </para>
/// </remarks>
/// <param name="IsBlocked">
/// When <see langword="true"/>, the day renders with the library's blocked-day visual
/// treatment (default class <c>calee-scheduler-day-blocked</c>) and create affordances
/// on that day are suppressed. See the remarks above for exactly what is and isn't
/// suppressed.
/// </param>
/// <param name="Class">
/// Optional CSS class hook applied to the day's header/cell elements alongside the
/// library's own classes — no precedence games, same convention as <c>EventClass</c>.
/// Independent of <see cref="IsBlocked"/>: a consumer can supply a class for an
/// "annotated but not blocked" day by returning <c>IsBlocked = false</c> with a
/// non-null <see cref="Class"/>.
/// </param>
/// <param name="Label">
/// Optional accessible label announced to screen readers explaining why the day is
/// inert (e.g., <c>"Blocked — routes not published yet"</c>). When null and
/// <see cref="IsBlocked"/> is <see langword="true"/>, the library falls back to a
/// generic "blocked" announcement appended to the day's normal accessible name.
/// </param>
public sealed record SchedulerDayState(
    bool IsBlocked,
    string? Class = null,
    string? Label = null);
