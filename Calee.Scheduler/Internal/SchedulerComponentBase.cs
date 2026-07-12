#nullable enable
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Internal abstract base for every Calee.Scheduler view (Day, Week, Month, TimelineView).
/// Holds the parameters and behaviors common to all views: <c>TimeZone</c>, <c>Events</c>,
/// <c>EventFilter</c>, <c>EventClass</c>, <c>AdditionalAttributes</c>, and the four
/// shared callbacks (<c>OnEventClicked</c>, <c>OnSlotClicked</c>, <c>OnRangeChanged</c>,
/// <c>OnDayOverflowClicked</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Visibility:</strong> the class is technically <see langword="public"/> because
/// the concrete public views derive from it and C# requires the base to be at least as
/// accessible as any derived public type. It is <em>not</em> part of the supported API
/// surface (PRD §4.7 / NFR-07). Consumers should not derive from this type — the
/// <see cref="System.ComponentModel.EditorBrowsableAttribute"/> hides it from IntelliSense
/// to signal that intent.
/// </para>
/// <para>
/// <strong>Validation policy</strong> follows PRD §4.6:
/// <list type="bullet">
///   <item><description><c>TimeZone</c> is optional (issue #34) but its layered resolution
///     is a hard-fail: when none of the explicit parameter, an ancestor
///     <c>CascadingValue&lt;TimeZoneInfo&gt;</c>, or <see cref="CaleeSchedulerOptions.DefaultTimeZone"/>
///     supply a value, <see cref="OnParametersSet"/> throws
///     <see cref="InvalidOperationException"/>.</description></item>
///   <item><description>Null <c>Events</c> is a soft-degradation case — treated as empty and logged via
///     <see cref="ILogger"/> when one is available.</description></item>
/// </list>
/// </para>
/// </remarks>
/// <typeparam name="TEvent">
/// Consumer event type. Must implement <see cref="ICalendarEvent"/>. The generic-only API
/// shape is mandated by ADR-0004 (FR-02).
/// </typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class SchedulerComponentBase<TEvent> : ComponentBase
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// Time zone used to compute "today", day boundaries, and the offset stamped onto
    /// emitted <see cref="SchedulerSlot"/> values. Optional (issue #34) — resolved via a
    /// layered chain, first non-null wins: this parameter → an ancestor
    /// <c>CascadingValue&lt;TimeZoneInfo&gt;</c> → <see cref="CaleeSchedulerOptions.DefaultTimeZone"/>
    /// → <see cref="InvalidOperationException"/>. See <see cref="ResolvedTimeZone"/> for the
    /// resolved value every grid-frame computation actually reads. ADR-0001 / PRD §4.6.
    /// </summary>
    [Parameter]
    public TimeZoneInfo? TimeZone { get; set; }

    /// <summary>
    /// Ambient time zone supplied by an ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c>
    /// (issue #34) — the second rung of <see cref="TimeZone"/>'s layered resolution.
    /// Distinct from the <see cref="SchedulerStateContainer"/> cascade the root scheduler
    /// uses for its own descendants; this cascade lets a consumer establish a single
    /// ambient zone above a standalone view (or above the whole app) without threading
    /// <c>TimeZone</c> through every call site.
    /// </summary>
    [CascadingParameter]
    private TimeZoneInfo? AmbientTimeZone { get; set; }

    /// <summary>
    /// The time zone actually used for every grid-frame computation this render pass —
    /// "today", day/week/month boundaries, and the offset stamped onto emitted
    /// <see cref="SchedulerSlot"/> values. Computed once per <c>OnParametersSet</c> by
    /// <see cref="ResolveTimeZone"/> and never <see langword="null"/> once that method has
    /// run without throwing. Event <c>Start</c>/<c>End</c> rendering never reads this —
    /// events still render at their literal <see cref="DateTimeOffset"/> (ADR-0001).
    /// </summary>
    protected TimeZoneInfo ResolvedTimeZone { get; private set; } = default!;

    /// <summary>
    /// Resolves <see cref="ResolvedTimeZone"/> per issue #34's layered precedence, first
    /// non-null wins: <see cref="TimeZone"/> → <see cref="AmbientTimeZone"/> →
    /// <see cref="CaleeSchedulerOptions.DefaultTimeZone"/> → throw. Deliberately does NOT
    /// fall back to <see cref="TimeZoneInfo.Local"/> or <see cref="TimeZoneInfo.Utc"/> —
    /// a silent local/UTC substitution is exactly the footgun this chain exists to avoid
    /// (see <see cref="CaleeSchedulerOptions"/>'s remarks).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// None of the three sources supplied a value.
    /// </exception>
    private protected TimeZoneInfo ResolveTimeZone() =>
        TimeZone
        ?? AmbientTimeZone
        ?? SchedulerOptions.Value.DefaultTimeZone
        ?? throw new InvalidOperationException(
            "Calee.Scheduler could not resolve a TimeZone. Supply one of, in order of " +
            "precedence: (1) the component's TimeZone parameter, (2) an ancestor " +
            "<CascadingValue TValue=\"TimeZoneInfo\" Value=\"...\"> wrapping this component, " +
            "or (3) a service-level default via " +
            "services.AddCaleeScheduler(o => o.DefaultTimeZone = ...). The library never " +
            "falls back to TimeZoneInfo.Local or TimeZoneInfo.Utc silently.");

    /// <summary>
    /// Events to render. <see langword="null"/> is treated as empty per PRD §4.6
    /// (soft-degradation), with a debug-level log if a logger is available. Event
    /// <see cref="ICalendarEvent.Id"/> values must be unique after <see cref="EventFilter"/>
    /// is applied; duplicates raise an <see cref="ArgumentException"/> before rendering.
    /// </summary>
    [Parameter]
    public IReadOnlyList<TEvent>? Events { get; set; }

    /// <summary>
    /// Optional predicate applied before any layout computation. When <see langword="null"/>
    /// the full <see cref="Events"/> list flows through unchanged. See FR-19b.
    /// </summary>
    [Parameter]
    public Func<TEvent, bool>? EventFilter { get; set; }

    /// <summary>
    /// Optional per-event CSS class hook. The returned class (when non-null) is applied
    /// alongside the library's own classes — no precedence games. See FR-54.
    /// </summary>
    [Parameter]
    public Func<TEvent, string?>? EventClass { get; set; }

    /// <summary>
    /// Optional per-day state hook (issue #8), following the <see cref="EventFilter"/> /
    /// <see cref="EventClass"/> idiom. Lets the consumer mark specific days as blocked —
    /// e.g. days with no published route, a holiday, or a maintenance window. A
    /// <see langword="null"/> return (including when this parameter itself is
    /// <see langword="null"/>, the default) means "normal day": zero visual change and
    /// zero behavioral change on that day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Evaluated per rendered day, not per slot.</strong> Day/Week/Month views
    /// call this once per rendered day (in the grid time zone, ADR-0001 — the argument
    /// is always a day's midnight boundary in <see cref="ResolvedTimeZone"/>) and cache the
    /// result for the render, the same way <c>EventClass</c> is evaluated once per
    /// event rather than recomputed per pixel. A Week/WorkWeek view with a
    /// <c>VisibleDays</c> subset only evaluates the hook for the visible columns —
    /// hidden days are never passed to it.
    /// </para>
    /// <para>
    /// <strong>What blocking suppresses.</strong> See <see cref="SchedulerDayState"/>'s
    /// remarks for the exact create-vs-move split: create affordances (double-click,
    /// drag-to-create, the create-at-focus keystroke) are fail-closed no-ops on a
    /// blocked day; drag-to-move/resize onto the day and <c>OnSlotClicked</c> are
    /// unaffected.
    /// </para>
    /// <para>
    /// <strong>Which views honor this.</strong> Day, Week (including WorkWeek and any
    /// <c>VisibleDays</c> subset), and Month. Year, Agenda, and Timeline do not
    /// currently read this parameter — it is inherited (so consumers composing those
    /// views under the same generic surface don't get a compile error) but has no
    /// effect there.
    /// </para>
    /// </remarks>
    [Parameter]
    public Func<DateTimeOffset, SchedulerDayState?>? DayModifier { get; set; }

    /// <summary>
    /// Optional render fragment for the *inside* of each day-header cell on time-grid
    /// views (issue #9) — e.g. a per-day count badge. Receives the day's midnight
    /// <see cref="DateTimeOffset"/> in <see cref="ResolvedTimeZone"/>: the same value shape
    /// <see cref="DayModifier"/> receives, for consistency across the library's
    /// per-day hooks. The library owns the header cell container and the default
    /// weekday/date label; this template renders *after* that label, inside the same
    /// cell — it does not replace it (ADR-0002 spirit: the library owns the
    /// container, the consumer owns the injected extras). A <see langword="null"/>
    /// template (the default) renders byte-identical markup to the read-only path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Which views honor this.</strong> Day and Week (including the WorkWeek
    /// arm and any <c>VisibleDays</c> subset — the template is only evaluated for the
    /// visible columns; hidden days never see it). Month is out of scope for issue #9
    /// (Month's header cells show weekday names, not dates); Year, Agenda, and
    /// Timeline likewise inherit this parameter for a uniform generic surface but have
    /// no effect, mirroring <see cref="DayModifier"/>'s own "which views honor this"
    /// contract.
    /// </para>
    /// <para>
    /// <strong>Composes with blocked days (issue #8).</strong> A day <see cref="DayModifier"/>
    /// marks blocked keeps its blocked class/label and still renders this template —
    /// blocking gates create affordances, not header content. <c>aria-disabled</c> is
    /// suppressed on the blocked label whenever <see cref="OnDayHeaderClicked"/> is
    /// wired (an operable control must not also announce "disabled") — see that
    /// parameter's remarks.
    /// </para>
    /// <para>
    /// <strong>⚠️ Do not nest interactive controls inside this template when
    /// <see cref="OnDayHeaderClicked"/> is wired.</strong> The header cell itself
    /// becomes the interactive element (<c>role="button"</c>); a <c>&lt;button&gt;</c>,
    /// <c>&lt;a&gt;</c>, or other focusable/clickable control placed inside it is a
    /// nested-interactive ARIA violation, and its clicks bubble up to also fire
    /// <see cref="OnDayHeaderClicked"/> — a double-fire footgun. This mirrors the
    /// <c>EventTemplate</c> contract (ADR-0002): the library owns the interactive
    /// envelope, the template owns non-interactive content inside it (e.g. an
    /// <c>aria-hidden="true"</c> count badge).
    /// </para>
    /// </remarks>
    [Parameter]
    public RenderFragment<DateTimeOffset>? DayHeaderTemplate { get; set; }

    /// <summary>
    /// Fired when the user activates a day-header cell on a time-grid view (issue #9)
    /// — pointer click, or Enter/Space while the header holds keyboard focus. Receives
    /// the day's midnight <see cref="DateTimeOffset"/> in <see cref="ResolvedTimeZone"/>,
    /// matching <see cref="DayHeaderTemplate"/>'s context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Fail-closed by design.</strong> A header cell is made focusable and
    /// interactive (<c>tabindex</c>, <c>role="button"</c>, pointer cursor) only when
    /// this callback has a delegate wired (<see cref="EventCallback{T}.HasDelegate"/>).
    /// An unwired header renders exactly as it did before issue #9. When wired, a
    /// blocked day's <c>aria-disabled</c> is dropped (see <see cref="DayHeaderTemplate"/>'s
    /// remarks) — an operable control never announces "disabled."
    /// </para>
    /// <para>
    /// <strong>Drag precedence.</strong> Suppressed while a drag is active, matching
    /// every other click handler in the library (ADR-0006).
    /// </para>
    /// <para>
    /// <strong>Space doesn't scroll the page.</strong> The header is a
    /// <c>&lt;div role="button"&gt;</c>, not a native <c>&lt;button&gt;</c>, so the
    /// browser's default "Space scrolls the viewport" behavior isn't suppressed for
    /// free. Rather than a Blazor <c>@onkeydown:preventDefault</c> directive (which is
    /// element-wide and would also swallow Tab's default focus-move — a keyboard
    /// trap), the view registers a scoped JS listener
    /// (<c>calee-scheduler.js</c>'s <c>registerDayHeaderKeyGuard</c>) while this
    /// callback has a delegate, and unregisters it on dispose. The listener
    /// <c>preventDefault()</c>s only the Space key targeting an interactive day
    /// header — every other key, including Tab, is untouched.
    /// </para>
    /// <para><strong>Which views honor this.</strong> Same as <see cref="DayHeaderTemplate"/>.</para>
    /// </remarks>
    [Parameter]
    public EventCallback<DateTimeOffset> OnDayHeaderClicked { get; set; }

    /// <summary>
    /// Unmatched HTML attributes captured from the consumer's markup and splatted onto
    /// the view's outermost rendered element. <c>class</c>, <c>style</c>, <c>data-*</c>,
    /// and <c>aria-*</c> from the consumer compose with the library's own values rather
    /// than replacing them. See FR-53.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Fired when the user activates an event chip. Receives the original <typeparamref name="TEvent"/>.
    /// </summary>
    [Parameter]
    public EventCallback<TEvent> OnEventClicked { get; set; }

    /// <summary>
    /// Fired when the user clicks an empty time slot in the grid. Receives a
    /// <see cref="SchedulerSlot"/> describing the click target.
    /// </summary>
    [Parameter]
    public EventCallback<SchedulerSlot> OnSlotClicked { get; set; }

    /// <summary>
    /// Fired whenever the visible range changes (e.g., navigation, view switch). Lets
    /// consumers refetch only the events they need for the window.
    /// </summary>
    [Parameter]
    public EventCallback<SchedulerRange> OnRangeChanged { get; set; }

    /// <summary>
    /// Fired when the user activates an overflow chip ("+N more" in Month view; "+N
    /// earlier" / "+N later" in Day/Week/TimelineView).
    /// </summary>
    [Parameter]
    public EventCallback<DayOverflowContext<TEvent>> OnDayOverflowClicked { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view wires drag-to-move on event chips (FR-25).
    /// Defaults to <see langword="false"/> per the fail-closed convention (FR-29) — the
    /// consumer must explicitly opt in. When <see langword="false"/>, event chips render
    /// exactly as in the read-only path and no pointer-down side effects fire.
    /// </summary>
    /// <remarks>
    /// The flag is declared on the shared base so every view inherits it uniformly,
    /// but per-view wiring is added incrementally across Phase 2 Tasks 4–6. Setting
    /// this on a view whose drag implementation hasn't landed yet is a no-op — the
    /// pointer-down handler simply isn't attached.
    /// </remarks>
    [Parameter]
    public bool AllowDragToMove { get; set; }

    /// <summary>
    /// Fired when an event is moved via drag (FR-25). The library applies the move
    /// optimistically before this callback fires; setting
    /// <see cref="EventMoveContext.Cancel"/> to <see langword="true"/> in the consumer's
    /// handler reverts the optimistic position. See ADR-0006.
    /// </summary>
    [Parameter]
    public EventCallback<EventMoveContext> OnEventMoved { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view wires drag-to-resize on event chips (FR-26).
    /// Defaults to <see langword="false"/> per the fail-closed convention (FR-29) — the
    /// consumer must explicitly opt in. When <see langword="false"/>, event chips render
    /// without the resize cursor, the <c>data-calee-drag-handle="resize-end"</c> data
    /// attribute, or the resize-half of the <c>aria-roledescription</c> string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resize semantics: only the trailing edge (<c>End</c>) moves — the leading edge
    /// (<c>Start</c>) is unchanged. Per Phase 2 plan §5.1 #2 the resize-handle UI is
    /// cursor-only: the bottom 8 px (Day/Week, vertical-time views) or right 8 px
    /// (TimelineView, horizontal-time views) of each chip is a resize hit-zone surfaced
    /// via <c>cursor: ns-resize</c> or <c>cursor: ew-resize</c>; there is no visible
    /// handle line, matching the Google/Apple convention.
    /// </para>
    /// <para>
    /// When both <see cref="AllowDragToMove"/> and <see cref="AllowDragToResize"/> are
    /// <see langword="true"/>, event chips carry <c>aria-roledescription="draggable
    /// resizable event"</c> so screen readers announce both affordances. Resize-only
    /// chips carry <c>aria-roledescription="resizable event"</c>.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowDragToResize { get; set; }

    /// <summary>
    /// Fired when an event is resized via drag (FR-26). The library applies the resize
    /// optimistically before this callback fires; setting
    /// <see cref="EventResizeContext.Cancel"/> to <see langword="true"/> in the consumer's
    /// handler reverts the optimistic end. See ADR-0006.
    /// </summary>
    /// <remarks>
    /// Only <see cref="EventResizeContext.NewEnd"/> is carried — drag-to-resize moves
    /// only the trailing edge. Start-side resize ships as a separate keyboard-only
    /// affordance (Phase 2 Task 14 — Shift+ArrowUp / Shift+ArrowDown).
    /// </remarks>
    [Parameter]
    public EventCallback<EventResizeContext> OnEventResized { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view wires drag-to-create on the empty grid
    /// background (FR-24). Defaults to <see langword="false"/> per the fail-closed
    /// convention (FR-29). The drag draws a ghost rectangle from the press point to
    /// the current cursor along the time axis (vertical for Day/Week, horizontal for
    /// TimelineView); on release the library fires <see cref="OnEventCreated"/> with
    /// the spanned <c>(Start, End [, LaneId])</c>. A press-and-release without
    /// meaningful movement (below the 5 px threshold) falls through to the regular
    /// slot-click flow (<c>OnSlotClicked</c>) — small jitter is never interpreted as
    /// a create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="AllowDragToMove"/> / <see cref="AllowDragToResize"/>, the
    /// create affordance has no visible per-chip handle — the affordance is "empty
    /// grid area", so the cue is a <c>cursor: cell</c> on the time-grid background
    /// (per-view CSS). Month view is intentionally excluded from drag-to-create;
    /// double-click-to-create (Phase 2 Task 9 → FR-32) covers the create surface on
    /// Month and on every other view as a complement to drag.
    /// </para>
    /// <para>
    /// Lifecycle is <em>not</em> the optimistic-pin pattern used by move/resize.
    /// There is no pre-existing event to pin against, and synthesizing a placeholder
    /// event with a fake Id risks colliding with consumer-supplied ids. Instead the
    /// library does nothing visually until the consumer pushes the real event back
    /// through <see cref="SchedulerComponentBase{TEvent}.Events"/> (Option A —
    /// matches the Google/Apple calendar UX). When the consumer sets
    /// <see cref="EventCreateContext.Cancel"/> to <see langword="true"/> there is
    /// literally nothing to revert.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowDragToCreate { get; set; }

    /// <summary>
    /// Fired when the user drags out a new event region on the empty grid (FR-24)
    /// or double-clicks an empty slot / month cell (FR-32). Carries an
    /// <see cref="EventCreateContext"/> whose <see cref="EventCreateContext.Slot"/>
    /// describes the spanned <c>(Start, End)</c> in the configured
    /// <see cref="ResolvedTimeZone"/>'s offset. In
    /// <c>CaleeSchedulerTimelineView</c> the <see cref="SchedulerSlot.LaneId"/> is
    /// populated to the anchor row's lane id (<see langword="null"/> when the anchor
    /// row is the unassigned row). In Day, Week, and Month views
    /// <see cref="SchedulerSlot.LaneId"/> is always <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting <see cref="EventCreateContext.Cancel"/> to <see langword="true"/>
    /// signals "do not create" — under the library's create lifecycle (Option A in
    /// the Task 8 commit message: no optimistic phantom event) this means simply
    /// that the consumer skips persisting; there is no library-side rendered state
    /// to revert. See ADR-0006.
    /// </para>
    /// <para>
    /// Both drag-to-create (FR-24) and double-click-to-create (FR-32) funnel through
    /// this single callback. <see cref="EventCreateContext"/> does not carry a flag
    /// distinguishing the two affordances — the consumer's reaction is identical in
    /// both cases (typically: "open my editor at this Slot").
    /// </para>
    /// </remarks>
    [Parameter]
    public EventCallback<EventCreateContext> OnEventCreated { get; set; }

    /// <summary>
    /// When <see langword="true"/>, double-clicking an empty slot (Day/Week/TimelineView)
    /// or an empty date cell (Month) fires <see cref="OnEventCreated"/> with a default
    /// duration (FR-32). Defaults to <see langword="false"/> per the fail-closed
    /// convention (FR-29) — the consumer must explicitly opt in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Default duration.</strong> The proposed event spans
    /// <see cref="CaleeSchedulerOptions.DefaultCreateDurationMinutes"/> when the option
    /// is set explicitly; otherwise it resolves per view: one
    /// <c>SlotDurationMinutes</c> for the time-grid views (Day/Week/TimelineView at
    /// <c>TimelineScale.Day</c>) and one day (1440 minutes) for whole-day cells
    /// (Month view; TimelineView at <c>TimelineScale.Week</c>/<c>Month</c>).
    /// </para>
    /// <para>
    /// <strong>Month view all-day shape.</strong> Month view cells are whole-day,
    /// so a double-click-to-create on Month produces an event with
    /// <c>Start = midnight in ResolvedTimeZone</c> and <c>End = Start + 1 day</c>. The
    /// <see cref="EventCreateContext"/> contract has no <c>IsAllDay</c> field; the
    /// all-day intent is conveyed via that Start/End shape. Consumers that need an
    /// <c>IsAllDay = true</c> flag on the persisted event set it themselves after
    /// inspecting the proposed Slot (whose duration is exactly 24 hours starting at
    /// midnight).
    /// </para>
    /// <para>
    /// <strong>Touch input caveat.</strong> Mobile browsers preempt
    /// <c>@ondblclick</c> for double-tap-to-zoom and similar gestures, so
    /// double-click-to-create is mouse / pointer only. The touch equivalent for
    /// create is the long-press-into-drag-to-create path (<see cref="AllowDragToCreate"/>).
    /// </para>
    /// <para>
    /// <strong>Coexistence with drag-to-create.</strong> <see cref="AllowDragToCreate"/>
    /// and <see cref="AllowDoubleClickToCreate"/> are independent flags. Both can be
    /// <see langword="true"/> simultaneously; the JS pointer-events module's 5 px
    /// movement threshold ensures a quick double-click never trips the drag-to-create
    /// path.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowDoubleClickToCreate { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view honors Ctrl/Cmd+click (toggle the clicked
    /// event into/out of the selection) and Shift+click (range-select between the
    /// anchor and the clicked event in render order) on event chips (FR-34). Defaults
    /// to <see langword="false"/> per the fail-closed convention (FR-29) — when
    /// disabled, modifier keys are ignored and every click acts as a single-event
    /// selection (replacing any previous selection).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What "selection" affects.</strong> Selected events render with a
    /// distinct outline (per-view CSS — distinct from the focus ring so a focused-but-
    /// not-selected event is visually unambiguous) and carry <c>aria-selected="true"</c>.
    /// Selection state survives <c>View</c> switches when the view is hosted inside
    /// <c>CaleeScheduler&lt;TEvent&gt;</c> (the cascading state container holds the
    /// canonical set); for standalone-view usage, the view holds its own local set.
    /// </para>
    /// <para>
    /// <strong>Out of scope here (Task 10).</strong> Keyboard equivalents
    /// (Space-toggle, Esc-clear, Shift+Arrow grow) ship in Task 11; the Delete-key
    /// batch flow ships in Task 12. This flag only governs mouse-click selection.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowMultiSelect { get; set; }

    /// <summary>
    /// Fires whenever the selection set changes — including the empty → single-id
    /// transition that happens on the first plain click, and the multi → single
    /// collapse that happens on a plain click while items were selected. Receives a
    /// freshly-materialized snapshot in anchor order (oldest first; the active anchor
    /// for Shift+click range select is the last entry). Does NOT fire when a click
    /// produces an identical set (e.g., plain-clicking the sole selected event again
    /// — a no-op); see Task 10's commit body for the per-modifier policy table.
    /// </summary>
    [Parameter]
    public EventCallback<IReadOnlyList<TEvent>> OnSelectionChanged { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view wires the Delete keystroke on a focused
    /// event chip to fire <see cref="OnEventDeleted"/> (single) or
    /// <see cref="OnEventsDeleted"/> (batch — when the focused chip is part of a
    /// multi-event selection set). Defaults to <see langword="false"/> per the
    /// fail-closed convention (FR-29) — the consumer must explicitly opt in. When
    /// disabled, Delete on a focused chip is a no-op from the library's perspective
    /// (the keystroke is not bound at all).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Routing rule.</strong> When <see cref="AllowDelete"/> is
    /// <see langword="true"/> AND the focused chip is in a selection set of two or
    /// more events, the library fires <see cref="OnEventsDeleted"/> with the full
    /// selection set. When the focused chip is NOT in the selection set (or the
    /// selection holds zero/one entries), the library fires
    /// <see cref="OnEventDeleted"/> with just the focused chip. This "focused
    /// outside selection" rule avoids the surprise-delete pitfall where pressing
    /// Delete on a chip the user is actively looking at would wipe out a set of
    /// unrelated events still held in selection from an earlier action.
    /// </para>
    /// <para>
    /// <strong>No confirmation prompt.</strong> The library ships zero built-in
    /// confirmation UI (ADR-0010). The consumer's handler is responsible for any
    /// "Are you sure?" — set <c>ctx.Cancel = true</c> to reject. The lifecycle
    /// mirrors create (ADR-0006 Option A): no optimistic phantom-removed state, so
    /// on reject there is literally nothing to revert.
    /// </para>
    /// <para>
    /// <strong>Keystroke alias.</strong> Both <c>e.Key == "Delete"</c> and
    /// <c>e.Key == "Backspace"</c> trigger the delete action. On macOS the
    /// keyboard's primary "Delete" key reports <c>"Backspace"</c>; the
    /// Forward-Delete key (fn+Backspace) reports <c>"Delete"</c>. ADR-0013's
    /// canonical map names the action <c>Delete</c>; the Backspace alias is the
    /// spirit-of-the-binding cross-platform accommodation. Task 14 will formalize
    /// this through the <c>ShortcutMap</c> API; Task 12 hardcodes both keys on
    /// the views.
    /// </para>
    /// <para>
    /// <strong>Drag precedence.</strong> When a drag is in flight the JS pointer
    /// module owns the keystroke (its window-level <c>keydown</c> listener
    /// preventDefault's the cancel keys). The C# delete handler short-circuits on
    /// <c>IsDragActive</c> so the same keystroke never doubles as "abort the drag
    /// AND delete the focused event."
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowDelete { get; set; }

    /// <summary>
    /// Fired when the user presses the delete keystroke on a focused event chip with
    /// <see cref="AllowDelete"/> = <see langword="true"/> AND either no multi-event
    /// selection is held OR the focused chip is not in the held selection. Carries
    /// the focused event in an <see cref="EventDeleteContext"/> whose
    /// <see cref="EventDeleteContext.Cancel"/> may be set to reject the deletion
    /// (consumer typically renders a confirmation prompt first — see ADR-0010).
    /// </summary>
    /// <remarks>
    /// On accept the library prunes the deleted event's id from the selection set
    /// and fires <see cref="OnSelectionChanged"/> if the selection actually changed
    /// (typically only when the deleted id was selected). Per ADR-0006 Option A
    /// (matching create), no optimistic phantom-removed state is rendered; the
    /// chip stays in the DOM until the consumer pushes an updated <c>Events</c>
    /// list back in.
    /// </remarks>
    [Parameter]
    public EventCallback<EventDeleteContext> OnEventDeleted { get; set; }

    /// <summary>
    /// Fired when the user presses the delete keystroke on a focused event chip
    /// with <see cref="AllowDelete"/> = <see langword="true"/> AND the focused chip
    /// is in a multi-event selection set. Carries the full selection set in an
    /// <see cref="EventsDeletedContext{TEvent}"/>; setting
    /// <see cref="EventsDeletedContext{TEvent}.Cancel"/> = <see langword="true"/>
    /// rejects ALL deletions atomically.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="OnEventDeleted"/> — exactly one of the
    /// two callbacks fires per delete keystroke. On accept the library empties
    /// the deleted ids from the selection set and fires
    /// <see cref="OnSelectionChanged"/> once with the post-delete selection.
    /// </remarks>
    [Parameter]
    public EventCallback<EventsDeletedContext<TEvent>> OnEventsDeleted { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the view wires the Cmd+Z / Ctrl+Z keystroke to
    /// fire <see cref="OnUndoRequested"/> and Cmd+Shift+Z / Ctrl+Y to fire
    /// <see cref="OnRedoRequested"/> (FR-35). Defaults to <see langword="false"/>
    /// per the fail-closed convention (FR-29) — the consumer must explicitly opt
    /// in. When disabled, undo/redo keystrokes are ignored by the library; the
    /// browser default proceeds, which on a non-text element typically does
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The library emits triggers; the consumer owns the stack.</strong>
    /// Per ADR-0012 the library never tracks an operation history. The two
    /// callbacks (<see cref="OnUndoRequested"/> / <see cref="OnRedoRequested"/>)
    /// fire with no payload — they signal "the user pressed undo / redo" and
    /// nothing more. The consumer maintains its own history (typically by
    /// listening to the existing CRUD callbacks — <see cref="OnEventMoved"/>,
    /// <see cref="OnEventResized"/>, <see cref="OnEventCreated"/>,
    /// <see cref="OnEventDeleted"/>, <see cref="OnEventsDeleted"/>) and applies
    /// the inverse on undo / redo by pushing an updated
    /// <see cref="SchedulerComponentBase{TEvent}.Events"/> list back through the
    /// cascade. This keeps the library aligned with ADR-0010 (no built-in UI, no
    /// internal state the consumer can't see) and ensures one source of truth
    /// for whether an operation succeeded: the consumer's data layer.
    /// </para>
    /// <para>
    /// <strong>Bindings.</strong> The library hardcodes the ADR-0013 canonical
    /// map:
    /// <list type="bullet">
    ///   <item><description><c>Cmd+Z</c> (macOS) / <c>Ctrl+Z</c> (Windows / Linux)
    ///     — without Shift — fires <see cref="OnUndoRequested"/>.</description></item>
    ///   <item><description><c>Cmd+Shift+Z</c> (macOS) — fires
    ///     <see cref="OnRedoRequested"/>.</description></item>
    ///   <item><description><c>Ctrl+Y</c> (Windows convention) — fires
    ///     <see cref="OnRedoRequested"/>. ADR-0013 specifies <c>Ctrl+Y</c> only;
    ///     <c>Cmd+Y</c> on macOS is a system gesture (Yank / app-specific) and
    ///     the library does NOT bind it.</description></item>
    /// </list>
    /// Task 14 will refactor these bindings into the data-driven
    /// <c>ShortcutMap</c> API; Task 13 hardcodes them on the views.
    /// </para>
    /// <para>
    /// <strong>Focus scoping.</strong> The keystroke is wired on the scheduler's
    /// interactive surfaces — event chips and grid containers — not a
    /// window/document-level listener. The user must hold focus inside the
    /// scheduler region for the binding to fire. Modifier-prefixed shortcuts
    /// don't collide with text-input typing (ADR-0013), so this scoping is the
    /// safe default; a future window-level option may be added if needed.
    /// </para>
    /// <para>
    /// <strong>Drag precedence.</strong> When a drag is in flight the JS pointer
    /// module owns cancel keystrokes; the C# undo/redo handler short-circuits on
    /// <c>IsDragActive</c> so the same keystroke never doubles as "abort the
    /// drag AND undo the previous edit." Same precedent as Esc-mid-drag
    /// (Task 11) and Delete-mid-drag (Task 12).
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowUndoRedo { get; set; }

    /// <summary>
    /// Fired when the user presses the undo keystroke (<c>Cmd+Z</c> / <c>Ctrl+Z</c>
    /// without Shift) inside the scheduler region with <see cref="AllowUndoRedo"/>
    /// = <see langword="true"/>. The callback carries no payload — the library
    /// emits a trigger only (FR-35 / ADR-0012). The consumer is responsible for
    /// owning the operation history and applying the inverse by pushing an
    /// updated <see cref="SchedulerComponentBase{TEvent}.Events"/> list back in.
    /// </summary>
    /// <remarks>
    /// See <see cref="AllowUndoRedo"/> for the canonical contract: library emits
    /// triggers, consumer owns the stack. Pressing Cmd+Z three times fires this
    /// callback three times — there is no coalescing or rate limiting at the
    /// library layer.
    /// </remarks>
    [Parameter]
    public EventCallback OnUndoRequested { get; set; }

    /// <summary>
    /// Fired when the user presses the redo keystroke (<c>Cmd+Shift+Z</c> on
    /// macOS, <c>Ctrl+Y</c> on Windows/Linux) inside the scheduler region with
    /// <see cref="AllowUndoRedo"/> = <see langword="true"/>. The callback carries
    /// no payload — the library emits a trigger only (FR-35 / ADR-0012). The
    /// consumer is responsible for owning the operation history and re-applying
    /// the operation by pushing an updated
    /// <see cref="SchedulerComponentBase{TEvent}.Events"/> list back in.
    /// </summary>
    /// <remarks>
    /// See <see cref="AllowUndoRedo"/> for the canonical contract: library emits
    /// triggers, consumer owns the stack. Pressing Ctrl+Y three times fires this
    /// callback three times — there is no coalescing or rate limiting at the
    /// library layer.
    /// </remarks>
    [Parameter]
    public EventCallback OnRedoRequested { get; set; }

    // ----- Shortcut map remap surface (Phase 2 Task 14 — FR-36 / ADR-0013) -----------

    /// <summary>
    /// Collection of <see cref="SchedulerCommandIds"/> values the library should not
    /// dispatch keystrokes for. Any default-map entry whose <c>CommandId</c> appears here
    /// is skipped; the matching keystroke falls through to the browser default (typically
    /// inert on a focused chip / grid container).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use the constants on <see cref="SchedulerCommandIds"/> so misspellings are
    /// compile-time errors. Disabling a command id disables ALL default bindings for it
    /// — <see cref="SchedulerCommandIds.EditDelete"/> in this list disables both
    /// <c>Delete</c> and <c>Backspace</c>; <see cref="SchedulerCommandIds.EditUndo"/>
    /// disables both <c>Cmd+Z</c> and <c>Ctrl+Z</c>.
    /// </para>
    /// <para>
    /// <strong>Precedence with <see cref="ShortcutMap"/>:</strong> disable wins. When a
    /// command id is in <see cref="DisabledShortcuts"/>, its default-map entries are
    /// dropped before <see cref="ShortcutMap"/> overrides are applied — supplying an
    /// override for a disabled command id is a no-op.
    /// </para>
    /// <para>
    /// <see langword="null"/> means "no commands disabled" (use the default map
    /// unmodified). FR-36 default; preserves backwards-compat with consumers who
    /// supply no shortcut parameters at all.
    /// </para>
    /// </remarks>
    [Parameter]
    public IReadOnlyList<string>? DisabledShortcuts { get; set; }

    /// <summary>
    /// Collection of <see cref="ShortcutBinding"/> entries that replace or extend the
    /// library's default shortcut map. Precedence is documented per ADR-0013:
    /// <list type="number">
    ///   <item><description>An entry whose <see cref="ShortcutBinding.CommandId"/>
    ///     matches a built-in command's id <strong>replaces</strong> the default
    ///     binding(s) for that command — supply <c>Ctrl+Alt+Z</c> for
    ///     <see cref="SchedulerCommandIds.EditUndo"/> and the default <c>Cmd+Z</c> /
    ///     <c>Ctrl+Z</c> stop firing. To bind multiple keystrokes to the same command
    ///     id, supply multiple entries for that id (e.g., two entries for
    ///     <see cref="SchedulerCommandIds.EditUndo"/>, one with each desired keystroke);
    ///     the resolved map honors every entry.</description></item>
    ///   <item><description>An entry whose <see cref="ShortcutBinding.CommandId"/> does
    ///     NOT match any built-in command id is appended to the resolved map. These
    ///     are no-ops in Task 14 (the library has no command handler for them); Task 15's
    ///     <c>SchedulerCommand</c> API will let consumers register handlers for
    ///     consumer-defined ids.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> means "use the default map unchanged." Malformed
    /// <see cref="ShortcutBinding.Key"/> strings (failed parse) are silently skipped —
    /// the library defends against malformed input rather than throwing at parameter
    /// time. Consumers can validate their map by inspecting the iterated
    /// <see cref="SchedulerShortcuts.DefaultMap"/> shape.
    /// </para>
    /// <para>
    /// <strong>Keystroke spec format</strong> is documented on <see cref="ShortcutBinding.Key"/>:
    /// optional modifiers (<c>Cmd</c>, <c>Ctrl</c>, <c>Alt</c>, <c>Shift</c>) joined with
    /// <c>+</c>, then a key name matching <c>KeyboardEventArgs.Key</c> after the library's
    /// letter case-normalization rule.
    /// </para>
    /// </remarks>
    [Parameter]
    public IReadOnlyList<ShortcutBinding>? ShortcutMap { get; set; }

    // ----- Phase 2 Task 14 — callbacks for shortcut commands -------------------------

    /// <summary>
    /// Fired when the user presses the navigate-today binding
    /// (<see cref="SchedulerCommandIds.NavigateToday"/>; default <c>t</c>) inside the
    /// scheduler region. No payload — the library emits a trigger only. The consumer
    /// is responsible for updating the bindable <c>Date</c> parameter; when the
    /// callback is unwired the keystroke is a no-op.
    /// </summary>
    /// <remarks>
    /// When the root scheduler is used and this callback is NOT wired by the consumer,
    /// the root flips its own anchor to "today in <c>ResolvedTimeZone</c>" automatically
    /// (uncontrolled-mode convenience). Wiring the callback opts the consumer in to
    /// owning the navigation behavior. See ADR-0013 row 7.
    /// </remarks>
    [Parameter]
    public EventCallback OnTodayRequested { get; set; }

    /// <summary>
    /// Fired when the user presses the create-at-focus binding
    /// (<see cref="SchedulerCommandIds.EditCreate"/>; default <c>n</c>) inside the
    /// scheduler region with focus on the grid (not a chip). The library emits a trigger
    /// only — there is no library-rendered new event. The consumer typically opens its
    /// editor at the focused slot's <see cref="SchedulerSlot"/> bounds (which the
    /// consumer reads from its own focus tracking, since the library does not expose
    /// the focused-slot index on the public surface today).
    /// </summary>
    /// <remarks>
    /// Grid-scope binding only — see ADR-0013 row 8. The chip-scope keystroke <c>n</c>
    /// is intentionally not bound (a focused chip would have no sensible "create at
    /// focus" semantics).
    /// </remarks>
    [Parameter]
    public EventCallback OnCreateAtFocusRequested { get; set; }

    /// <summary>
    /// Fired when the user presses the help binding (<see cref="SchedulerCommandIds.HelpOpen"/>;
    /// default <c>?</c>) inside the scheduler region. No payload — the library emits a
    /// trigger only. The consumer typically renders a help panel listing the resolved
    /// shortcut map; iterate <see cref="SchedulerShortcuts.DefaultMap"/> together with
    /// your own overrides to populate it.
    /// </summary>
    /// <remarks>
    /// Per ADR-0010 the library ships no built-in help UI; consumers control the entire
    /// surface. See ADR-0013's last row.
    /// </remarks>
    [Parameter]
    public EventCallback OnHelpRequested { get; set; }

    /// <summary>
    /// Fired when the user presses one of the view-switch bindings
    /// (<see cref="SchedulerCommandIds.ViewDay"/> through
    /// <see cref="SchedulerCommandIds.ViewWorkWeek"/>; default <c>1</c>–<c>7</c>) inside
    /// the scheduler region. The payload is the requested <see cref="SchedulerView"/>;
    /// the consumer typically pushes it into the root's bindable <c>View</c> parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Library default behavior when unwired.</strong> When the consumer has NOT
    /// wired this callback AND the root scheduler is in uncontrolled <c>View</c> mode,
    /// the root flips its own active view to the requested one — the library handles the
    /// view switch internally. Wiring the callback opts the consumer in to owning the
    /// switch behavior (typically necessary in <c>@bind-View</c> controlled mode where
    /// the consumer's binding drives the view).
    /// </para>
    /// <para>
    /// <strong>Dispatch sources.</strong> The library raises this callback for both the
    /// keystroke bindings (<see cref="SchedulerCommandIds.ViewDay"/> through
    /// <see cref="SchedulerCommandIds.ViewWorkWeek"/>) and the command-palette dispatch of
    /// the same command ids — both surfaces route through the same emit path so a
    /// consumer-wired handler sees one signal regardless of which surface the user used.
    /// </para>
    /// <para>
    /// <strong>Cascaded vs standalone routing.</strong> When this view is composed under
    /// <c>CaleeScheduler&lt;TEvent&gt;</c> (the root), the root intercepts each child's
    /// <see cref="OnViewSwitchRequested"/> via <c>HandleChildViewSwitchRequestedAsync</c>;
    /// in uncontrolled <c>View</c> mode the root auto-flips its own active view, and in
    /// controlled <c>@bind-View</c> mode the root defers to the consumer's binding. When a
    /// view component is used standalone (no root above it) the consumer owns the routing —
    /// wire this callback to update whatever bound <c>View</c> drives the surface.
    /// </para>
    /// </remarks>
    [Parameter]
    public EventCallback<SchedulerView> OnViewSwitchRequested { get; set; }

    /// <summary>
    /// Fired when the user presses the command-palette binding
    /// (<see cref="SchedulerCommandIds.PaletteOpen"/>; default <c>Cmd+K</c> /
    /// <c>Ctrl+K</c>) inside the scheduler region with <see cref="AllowCommandPalette"/>
    /// = <see langword="true"/>. No payload — the library emits a trigger only. Per
    /// ADR-0010 / ADR-0014 the actual palette overlay is consumer-rendered; the
    /// <see cref="Commands"/> property exposes the merged list of built-in plus
    /// <see cref="CustomCommands"/> the consumer iterates to populate the palette.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="AllowCommandPalette"/> per the fail-closed convention
    /// (FR-29). When the flag is <see langword="false"/> (the default) the Cmd+K /
    /// Ctrl+K keystroke is a no-op from the library's perspective — neither this
    /// callback fires nor does the <see cref="Commands"/> list populate.
    /// </remarks>
    [Parameter]
    public EventCallback OnCommandPaletteRequested { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the library fires <see cref="OnCommandPaletteRequested"/>
    /// on the <see cref="SchedulerCommandIds.PaletteOpen"/> binding (default <c>Cmd+K</c> /
    /// <c>Ctrl+K</c>) and populates <see cref="Commands"/> with the built-in command list
    /// (per the merge rules under <see cref="CustomCommands"/>). Defaults to
    /// <see langword="false"/> per the fail-closed convention (FR-29) — the consumer must
    /// explicitly opt in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two surfaces gate on this flag together.</strong> The keystroke side
    /// (Cmd+K / Ctrl+K) is gated at the shortcut-dispatch layer; the API side
    /// (<see cref="Commands"/>) is empty when the flag is off so consumers don't see
    /// "phantom" commands they can't invoke. The two stay in lockstep — a consumer who
    /// hasn't opted in cannot stumble into the palette through either route.
    /// </para>
    /// <para>
    /// <strong>What the library does NOT ship.</strong> Per ADR-0010 / ADR-0014 the
    /// command palette overlay UI (focus-trapped modal, search-as-you-type, keyboard
    /// arrow navigation within the palette) is consumer-rendered. The library exposes
    /// only the trigger callback and the queryable command list; the consumer's design-
    /// system palette / drawer / popover handles the rest.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool AllowCommandPalette { get; set; }

    /// <summary>
    /// Consumer-supplied commands appended to the library's built-in command list
    /// (ADR-0014). The merged result is exposed on <see cref="Commands"/> for the
    /// consumer's palette to query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Merge rules (per ADR-0014).</strong>
    /// <list type="bullet">
    ///   <item><description>Built-in commands appear first, in the canonical order
    ///     documented on <see cref="Commands"/>.</description></item>
    ///   <item><description><see cref="CustomCommands"/> entries are appended after
    ///     the built-ins.</description></item>
    ///   <item><description>Custom command <see cref="SchedulerCommand.Id"/> values
    ///     SHOULD NOT shadow built-in ids (the <c>view.*</c>, <c>navigate.*</c>,
    ///     <c>edit.*</c>, <c>palette.*</c>, <c>help.*</c> namespaces). When a
    ///     collision occurs, the consumer-supplied command wins (last-write) and
    ///     replaces the built-in's <see cref="SchedulerCommand.Label"/> and
    ///     <see cref="SchedulerCommand.Invoke"/> in the merged list — useful when a
    ///     consumer wants to relabel a built-in or interpose its own behavior.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Ignored when <see cref="AllowCommandPalette"/> is <see langword="false"/>.</strong>
    /// The fail-closed default keeps the <see cref="Commands"/> list empty regardless of
    /// what's supplied here. Setting <see cref="CustomCommands"/> without flipping
    /// <see cref="AllowCommandPalette"/> on is a no-op.
    /// </para>
    /// </remarks>
    [Parameter]
    public IReadOnlyList<SchedulerCommand>? CustomCommands { get; set; }

    /// <summary>
    /// Fired when the user presses the move-mode binding
    /// (<see cref="SchedulerCommandIds.EditMove"/>; default <c>m</c>) on a focused
    /// event chip. <strong>Placeholder in Task 14:</strong> the library fires the
    /// trigger but does not implement move-mode behavior itself — the consumer or a
    /// Phase 3 task wires the actual mode (cross-lane keyboard drag).
    /// </summary>
    /// <remarks>
    /// Chip-scope binding only (see ADR-0013 row 10). Until move-mode behavior lands
    /// in the library, consumers can implement their own by listening to this callback
    /// and rendering a visual cue (highlight on the focused chip), then translating
    /// subsequent arrow keys into <c>OnEventMoved</c> events via their own dispatch.
    /// </remarks>
    [Parameter]
    public EventCallback OnMoveModeRequested { get; set; }

    /// <summary>
    /// Fired when the user presses one of the resize keystrokes
    /// (<see cref="SchedulerCommandIds.EditResize"/>; default <c>Shift+ArrowUp</c> /
    /// <c>Shift+ArrowDown</c>) on a focused event chip. <strong>Placeholder in Task 14:</strong>
    /// the library fires the trigger but does not adjust the event's <c>End</c> itself.
    /// The consumer is responsible for resolving the focused event, computing the new
    /// <c>End</c>, and firing <c>OnEventResized</c> downstream.
    /// </summary>
    /// <remarks>
    /// Chip-scope binding only (see ADR-0013 row 11). The library does not currently
    /// expose the focused event id on the public surface, so consumers wanting a fully-
    /// functional keyboard resize need a wrapper component that tracks focus. A future
    /// task (Phase 3) will likely package this behavior with a richer payload.
    /// </remarks>
    [Parameter]
    public EventCallback OnResizeKeystrokeRequested { get; set; }

    /// <summary>
    /// Fired when the user presses the move-mode binding (<see cref="SchedulerCommandIds.EditMove"/>;
    /// default <c>m</c>) on a focused event chip. Payload identifies the focused event so the consumer
    /// can show a visual cue or log the action. The library implements the phantom movement logic
    /// internally (arrow keys adjust position, Enter commits, Escape cancels); the consumer doesn't
    /// need to track focus or implement move logic. The library fires <see cref="OnEventMoved"/> with
    /// <see cref="EventMoveContext"/> when the user commits the move.
    /// </summary>
    /// <remarks>
    /// Chip-scope binding only (see ADR-0013 row 10). Fires alongside the existing parameterless
    /// <see cref="OnMoveModeRequested"/> for backward compatibility. Consumers can wire either or both.
    /// </remarks>
    [Parameter]
    public EventCallback<KeyboardMoveRequest> OnKeyboardMoveRequested { get; set; }

    /// <summary>
    /// Fired when the user presses one of the resize keystrokes (<see cref="SchedulerCommandIds.EditResize"/>;
    /// default <c>Shift+ArrowUp</c> / <c>Shift+ArrowDown</c>) on a focused event chip. Payload identifies
    /// the focused event and the resize direction so the consumer can show a visual cue or log the action.
    /// The library implements the resize logic internally (each keystroke resizes by one slot); the consumer
    /// doesn't need to track focus or implement resize logic. The library fires <see cref="OnEventResized"/>
    /// with <see cref="EventResizeContext"/> after each resize.
    /// </summary>
    /// <remarks>
    /// Chip-scope binding only (see ADR-0013 row 11). Fires alongside the existing parameterless
    /// <see cref="OnResizeKeystrokeRequested"/> for backward compatibility. Consumers can wire either or both.
    /// </remarks>
    [Parameter]
    public EventCallback<KeyboardResizeRequest> OnKeyboardResizeRequested { get; set; }

    // ----- Phase 2 Task 15 — Commands API (FR-37 / ADR-0014) -------------------------

    /// <summary>
    /// The merged list of commands the consumer's command-palette UI queries (ADR-0014).
    /// Contains built-in commands followed by <see cref="CustomCommands"/>, with the
    /// fail-closed gate on <see cref="AllowCommandPalette"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Empty when <see cref="AllowCommandPalette"/> is <see langword="false"/>.</strong>
    /// The fail-closed default returns <see cref="Array.Empty{T}"/> regardless of what
    /// <see cref="CustomCommands"/> contains — consumers see no "phantom" commands they
    /// can't actually invoke.
    /// </para>
    /// <para>
    /// <strong>Built-in command list (when <see cref="AllowCommandPalette"/> is
    /// <see langword="true"/>).</strong> One entry per default shortcut in ADR-0013's
    /// table, in this order:
    /// <list type="number">
    ///   <item><description><c>view.day</c> — "Day view" (group <c>View</c>; always present)</description></item>
    ///   <item><description><c>view.week</c> — "Week view" (<c>View</c>; always present)</description></item>
    ///   <item><description><c>view.month</c> — "Month view" (<c>View</c>; always present)</description></item>
    ///   <item><description><c>view.year</c> — "Year view" (<c>View</c>; always present;
    ///     <see cref="SchedulerCommand.Invoke"/> fires <see cref="OnViewSwitchRequested"/>
    ///     with <see cref="SchedulerView.Year"/> per Task 16's wiring)</description></item>
    ///   <item><description><c>view.agenda</c> — "Agenda view" (<c>View</c>; always present;
    ///     <see cref="SchedulerCommand.Invoke"/> fires <see cref="OnViewSwitchRequested"/>
    ///     with <see cref="SchedulerView.Agenda"/> per Task 17's wiring)</description></item>
    ///   <item><description><c>view.timeline</c> — "Timeline view" (<c>View</c>; always present)</description></item>
    ///   <item><description><c>view.workweek</c> — "Work Week view" (<c>View</c>; always
    ///     present; <see cref="SchedulerCommand.Invoke"/> fires
    ///     <see cref="OnViewSwitchRequested"/> with <see cref="SchedulerView.WorkWeek"/> —
    ///     issue #7)</description></item>
    ///   <item><description><c>navigate.today</c> — "Go to today" (group <c>Navigate</c>; always present)</description></item>
    ///   <item><description><c>edit.create</c> — "Create event" (group <c>Edit</c>; always present)</description></item>
    ///   <item><description><c>edit.delete</c> — "Delete focused event" (<c>Edit</c>;
    ///     only present when <see cref="AllowDelete"/> is <see langword="true"/>)</description></item>
    ///   <item><description><c>edit.move</c> — "Move-mode" (<c>Edit</c>; always present;
    ///     no-op behavior — fires <see cref="OnMoveModeRequested"/> only, same as the
    ///     <c>m</c> keystroke today)</description></item>
    ///   <item><description><c>edit.undo</c> — "Undo" (<c>Edit</c>; only present when
    ///     <see cref="AllowUndoRedo"/> is <see langword="true"/>)</description></item>
    ///   <item><description><c>edit.redo</c> — "Redo" (<c>Edit</c>; only present when
    ///     <see cref="AllowUndoRedo"/> is <see langword="true"/>)</description></item>
    ///   <item><description><c>help.open</c> — "Help" (group <c>Help</c>; always present)</description></item>
    /// </list>
    /// The <c>palette.open</c> command is intentionally excluded — the palette cannot
    /// invoke its own opening; the keystroke (Cmd+K / Ctrl+K) is the trigger.
    /// </para>
    /// <para>
    /// <strong>Custom commands.</strong> Appended after the built-ins in the order the
    /// consumer supplied. Custom command ids that collide with a built-in's id replace
    /// the built-in in place (last-write — see <see cref="CustomCommands"/>).
    /// </para>
    /// <para>
    /// <strong>Built-in command Invoke wiring.</strong> Each built-in's
    /// <see cref="SchedulerCommand.Invoke"/> action routes into the same internal dispatch
    /// the keystroke handlers use. For example, <c>edit.undo</c>.Invoke fires
    /// <see cref="OnUndoRequested"/> just as Cmd+Z does. The
    /// <see cref="SchedulerCommand.Invoke"/> shape is synchronous (an <see cref="Action"/>);
    /// each built-in fires its underlying async <c>EventCallback.InvokeAsync</c> as a
    /// fire-and-forget Task — the consumer's palette doesn't block on completion. This
    /// mirrors the keystroke-dispatch behavior, where each callback fires and the handler
    /// returns immediately.
    /// </para>
    /// <para>
    /// <strong>Order stability.</strong> The built-in order is stable across renders and
    /// across changes to unrelated parameters. Toggling <see cref="AllowDelete"/> /
    /// <see cref="AllowUndoRedo"/> on or off only adds / removes those specific entries;
    /// it does not re-shuffle the others.
    /// </para>
    /// <para>
    /// <strong>Not a <see cref="ParameterAttribute"/>.</strong> This is a read-only
    /// computed property the consumer reads, not a value Blazor supplies. Computed in
    /// <see cref="OnParametersSet"/> whenever any of the inputs change
    /// (<see cref="AllowCommandPalette"/>, <see cref="AllowDelete"/>,
    /// <see cref="AllowUndoRedo"/>, <see cref="CustomCommands"/>).
    /// </para>
    /// </remarks>
    public IReadOnlyList<SchedulerCommand> Commands { get; private set; } = Array.Empty<SchedulerCommand>();

    /// <summary>
    /// Cached parameter snapshot used to skip <see cref="Commands"/> recomputation when
    /// nothing the merged list depends on has changed. Reference equality on
    /// <see cref="CustomCommands"/> is sufficient — if a consumer rebuilds the list on
    /// every render we eat the cost of one re-merge per render (microseconds for a
    /// ~13-entry list). The bool flags participate by value.
    /// </summary>
    private (bool Palette, bool Delete, bool UndoRedo, IReadOnlyList<SchedulerCommand>? Custom) _lastCommandsInputs;

    /// <summary>First-render gate for <see cref="Commands"/>; mirrors the shortcut-map flag.</summary>
    private bool _commandsInitialized;

    /// <summary>
    /// Hook for components that want to intercept the view-switch Invoke routed through
    /// the palette (Phase 2 Task 15 — FR-37 / ADR-0014). The root <c>CaleeScheduler</c>
    /// overrides this so the palette's view-switch commands flow through the same
    /// auto-flip path the keystroke dispatch uses (Task 14's
    /// <c>HandleChildViewSwitchRequestedAsync</c>); child views inherit the default
    /// implementation that fires <see cref="OnViewSwitchRequested"/> directly.
    /// </summary>
    /// <remarks>
    /// The default fire-and-forget pattern matches the standalone-view path: the
    /// consumer's <see cref="OnViewSwitchRequested"/> callback runs, and if no
    /// auto-flip is wanted (or possible without root scope), the keystroke and palette
    /// behave identically.
    /// </remarks>
    private protected virtual void InvokeViewSwitchFromCommand(SchedulerView view)
    {
        _ = OnViewSwitchRequested.InvokeAsync(view);
    }

    /// <summary>
    /// Hook for components that want to intercept the navigate-today Invoke routed
    /// through the palette (Phase 2 Task 15). Same shape as
    /// <see cref="InvokeViewSwitchFromCommand"/>: the root overrides to apply its
    /// uncontrolled-mode auto-reanchor; child views inherit the direct-fire default.
    /// </summary>
    private protected virtual void InvokeTodayFromCommand()
    {
        _ = OnTodayRequested.InvokeAsync();
    }

    /// <summary>
    /// Hook letting a view report whether the currently keyboard-focused grid position
    /// (the roving-tabindex slot/cell, not a focused event chip) sits on a day the
    /// consumer's <see cref="DayModifier"/> marked blocked (issue #8). The base
    /// dispatch's <see cref="SchedulerCommandIds.EditCreate"/> branch consults this
    /// before firing <see cref="OnCreateAtFocusRequested"/> so the create-at-focus
    /// keystroke suppresses fail-closed on blocked days — the same create-only rule
    /// double-click-to-create and drag-to-create follow.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see langword="false"/> (no suppression) — a
    /// view that doesn't track a focused grid position, or hasn't been wired for
    /// <see cref="DayModifier"/> support, is unaffected. Day/Week/Month override this
    /// against their own focused-slot / focused-column / focused-cell state.
    /// </remarks>
    private protected virtual bool IsFocusedGridDayBlocked() => false;

    /// <summary>
    /// Cached static "Built-in" commands without consumer wiring would be a footgun —
    /// the <see cref="SchedulerCommand.Invoke"/> closures must capture <c>this</c> so
    /// they fire the right view's callbacks. We rebuild on every input change rather
    /// than caching by identity.
    /// </summary>
    private void RebuildCommands()
    {
        if (!AllowCommandPalette)
        {
            Commands = Array.Empty<SchedulerCommand>();
            return;
        }

        // Build a 14-slot list (worst case: all gated-on, no custom). The exact built-in
        // list shape is documented on the Commands XML doc above; this is the single
        // source of truth for the list's content + order.
        var builtIns = new List<SchedulerCommand>(14);

        // View group — every entry always present. With Task 17 (Agenda) live every
        // built-in view command Invoke now flows through InvokeViewSwitchFromCommand
        // (Tasks 16 + 17 widened the SchedulerView enum); the previous Year/Agenda
        // matched-but-no-op placeholders are gone.
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewDay, "Day view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Day)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewWeek, "Week view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Week)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewMonth, "Month view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Month)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewYear, "Year view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Year)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewAgenda, "Agenda view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Agenda)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewTimeline, "Timeline view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.Timeline)));
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.ViewWorkWeek, "Work Week view", "View",
            () => InvokeViewSwitchFromCommand(SchedulerView.WorkWeek)));

        // Navigate group.
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.NavigateToday, "Go to today", "Navigate",
            () => InvokeTodayFromCommand()));

        // Edit group.
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.EditCreate, "Create event", "Edit",
            () =>
            {
                // Issue #8 — the palette's Invoke must match the keystroke dispatch's
                // fail-closed gate (see the EditCreate case in the shortcut-dispatch
                // switch below): a blocked focused grid position suppresses create
                // here too, not just on the "n" keystroke.
                if (IsFocusedGridDayBlocked()) return;
                _ = OnCreateAtFocusRequested.InvokeAsync();
            }));

        if (AllowDelete)
        {
            // edit.delete has different semantics from the keystroke path: the keystroke
            // handler has a focused chip by construction (the chip's onkeydown fired);
            // the palette's Invoke does not. The TryDeleteFocusedEventAsync helper needs
            // both a focused id AND the resolved TEvent, neither of which the palette
            // surfaces. Until a future task threads the focused id through the palette
            // path explicitly, edit.delete's Invoke is documented as a no-op when nothing
            // is focused — same shape as the pre-Task-16/17 Year/Agenda "matched but no-op"
            // pattern (now retired — Year/Agenda Invoke fire view-switches).
            // Consumers wanting "delete the currently-selected event(s)" can wire that
            // through a custom command that reads their own selection state.
            builtIns.Add(new SchedulerCommand(
                SchedulerCommandIds.EditDelete, "Delete focused event", "Edit",
                () => { /* No focused-chip plumbing through the palette — see remarks above. */ }));
        }

        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.EditMove, "Move-mode", "Edit",
            () => _ = OnMoveModeRequested.InvokeAsync()));

        if (AllowUndoRedo)
        {
            builtIns.Add(new SchedulerCommand(
                SchedulerCommandIds.EditUndo, "Undo", "Edit",
                () => _ = OnUndoRequested.InvokeAsync()));
            builtIns.Add(new SchedulerCommand(
                SchedulerCommandIds.EditRedo, "Redo", "Edit",
                () => _ = OnRedoRequested.InvokeAsync()));
        }

        // Help group. palette.open is intentionally excluded — the palette cannot invoke
        // its own opening; the keystroke is the trigger.
        builtIns.Add(new SchedulerCommand(
            SchedulerCommandIds.HelpOpen, "Help", "Help",
            () => _ = OnHelpRequested.InvokeAsync()));

        var customs = CustomCommands;
        if (customs is null || customs.Count == 0)
        {
            Commands = builtIns;
            return;
        }

        // Merge in custom commands: id-collision wins for the consumer (replace in place,
        // preserving the built-in's slot in the list); non-colliding ids append after.
        // Build an id→index lookup of built-ins so we can find collisions in O(1).
        var builtInIndexById = new Dictionary<string, int>(builtIns.Count, StringComparer.Ordinal);
        for (var i = 0; i < builtIns.Count; i++)
        {
            builtInIndexById[builtIns[i].Id] = i;
        }

        var merged = new List<SchedulerCommand>(builtIns.Count + customs.Count);
        merged.AddRange(builtIns);

        // Track which custom ids we've already appended so duplicate consumer-defined ids
        // also resolve last-write (matching ADR-0014). For built-in collisions, replace
        // in place each time (the last consumer entry for that id wins for the in-place
        // replacement too).
        var appendedCustomIndexById = new Dictionary<string, int>(customs.Count, StringComparer.Ordinal);
        for (var i = 0; i < customs.Count; i++)
        {
            var custom = customs[i];
            if (custom is null) continue; // Defensive; null SchedulerCommand in the list
                                          // is a consumer bug but we don't crash on it.
            if (builtInIndexById.TryGetValue(custom.Id, out var builtInIdx))
            {
                // Built-in id collision: consumer wins, replace in-place. Preserves the
                // canonical built-in slot rather than reordering the list — the consumer
                // overrides the label / Invoke but the slot stays the same.
                merged[builtInIdx] = custom;
                continue;
            }
            if (appendedCustomIndexById.TryGetValue(custom.Id, out var customIdx))
            {
                // Duplicate consumer-defined id: last-write wins, replace the already-
                // appended entry in place.
                merged[customIdx] = custom;
                continue;
            }
            // Fresh consumer-defined id — append at tail.
            appendedCustomIndexById[custom.Id] = merged.Count;
            merged.Add(custom);
        }

        Commands = merged;
    }

    /// <summary>
    /// Cascaded shared state populated by the root <c>CaleeScheduler&lt;TEvent&gt;</c>.
    /// When present, the canonical selection set lives in
    /// <see cref="SchedulerStateContainer.Selection"/> and survives view swaps. When
    /// absent (a view hosted standalone, without the root scheduler wrapper), the
    /// view falls back to its own <see cref="_localSelection"/> instance.
    /// </summary>
    [CascadingParameter]
    private SchedulerStateContainer? CascadedState { get; set; }

    /// <summary>
    /// Per-view selection storage used when no cascading state is present (standalone
    /// view). When the cascade is present, this field is unused — selection lives in
    /// the cascade so cross-view persistence works.
    /// </summary>
    private readonly SchedulerSelection _localSelection = new();

    /// <summary>
    /// Standalone-mode resolved shortcut map (Phase 2 Task 14 — FR-36). When the view
    /// is hosted under <c>CaleeScheduler&lt;TEvent&gt;</c> the cascade carries the
    /// canonical resolved map; this field is the fallback for standalone hosting. The
    /// view's <c>OnParametersSet</c> recomputes this whenever the consumer's
    /// <see cref="DisabledShortcuts"/> / <see cref="ShortcutMap"/> parameters change.
    /// </summary>
    private ResolvedShortcutMap _localResolvedShortcuts = ResolvedShortcutMap.Empty;

    /// <summary>
    /// <see langword="true"/> once <see cref="_localResolvedShortcuts"/> has been
    /// initialized for the current parameter set. The first <c>OnParametersSet</c>
    /// flips this so a standalone view's first dispatch sees the resolved default map
    /// rather than the sentinel <see cref="ResolvedShortcutMap.Empty"/>.
    /// </summary>
    private bool _localResolvedInitialized;

    /// <summary>
    /// Cached input identity for <see cref="_localResolvedShortcuts"/> so we only rebuild
    /// when the parameters actually change. Reference equality is sufficient: if the
    /// consumer rebuilds the list on every render we eat the cost of one re-resolve per
    /// render, which is well under the parameter-set budget.
    /// </summary>
    private (IReadOnlyList<string>? Disabled, IReadOnlyList<ShortcutBinding>? Map) _localResolvedInputs;

    /// <summary>
    /// Returns the effective selection storage. Prefers the cascaded container when
    /// present so cross-view persistence works; falls back to the per-view local
    /// instance when the view is used standalone.
    /// </summary>
    internal SchedulerSelection EffectiveSelection =>
        CascadedState?.Selection ?? _localSelection;

    /// <summary>
    /// <see langword="true"/> when no <see cref="SchedulerStateContainer"/> cascade is
    /// present (view hosted standalone, without the root scheduler wrapper). Views
    /// gate their own post-mutation <c>StateHasChanged()</c> on this — in the cascade
    /// case the root's <c>HandleRequestSelectionChangeAsync</c> already calls
    /// <c>StateHasChanged()</c>, and the cascading-parameter push re-renders the
    /// child view; calling it on the view too is redundant. Standalone views are
    /// the canonical owner of their selection state and need their own re-render.
    /// </summary>
    protected bool IsStandalone => CascadedState is null;

    /// <summary>
    /// Returns <see langword="true"/> when the supplied event id is currently selected.
    /// Used by view markup to apply the selected CSS class and the
    /// <c>aria-selected="true"</c> attribute.
    /// </summary>
    internal bool IsEventSelected(string id) => EffectiveSelection.Contains(id);

    /// <summary>
    /// Process a click on an event with the supplied modifier state and the view's
    /// current render-order id list (used to compute the Shift+click range). Implements
    /// the selection-mutation rules:
    /// <list type="bullet">
    ///   <item><description><strong>Plain click:</strong> selection becomes a single-id
    ///     set containing <paramref name="clickedId"/>. Fires
    ///     <see cref="OnSelectionChanged"/> unless the previous selection was already
    ///     exactly this single id (in which case it's a no-op).</description></item>
    ///   <item><description><strong>Ctrl/Cmd+click</strong> (when
    ///     <see cref="AllowMultiSelect"/>): toggles <paramref name="clickedId"/> in or
    ///     out of the selection. Adding moves the anchor to <paramref name="clickedId"/>.</description></item>
    ///   <item><description><strong>Shift+click</strong> (when
    ///     <see cref="AllowMultiSelect"/>): replaces the selection with the inclusive
    ///     range from the current anchor to <paramref name="clickedId"/> in
    ///     <paramref name="renderOrderIds"/>. If no anchor exists, behaves like a plain
    ///     click.</description></item>
    ///   <item><description>When <see cref="AllowMultiSelect"/> is
    ///     <see langword="false"/>, all modifier keys are ignored — every click
    ///     behaves like a plain click (FR-29 fail-closed).</description></item>
    /// </list>
    /// Returns <see langword="true"/> when the selection set actually changed (so
    /// the caller can decide whether to <see cref="ComponentBase.StateHasChanged"/>
    /// for the visual). <see cref="OnSelectionChanged"/> is fired here; the caller
    /// does not need to fire it again.
    /// </summary>
    /// <param name="clickedId">The id of the event that was clicked.</param>
    /// <param name="ctrlOrMeta">Whether Ctrl (Windows / Linux) or Cmd (macOS) was held.</param>
    /// <param name="shift">Whether Shift was held.</param>
    /// <param name="renderOrderIds">
    /// All currently-visible event ids in the view's render order. Used to compute the
    /// inclusive range for Shift+click. The list must include
    /// <paramref name="clickedId"/>; if it doesn't, Shift+click falls back to a single
    /// selection. The list does NOT need to be deduplicated — the same id may appear
    /// multiple times in Week view (multi-day chunks across columns); the range walks
    /// the first occurrence of each id.
    /// </param>
    protected async Task<bool> ApplyClickSelectionAsync(
        string clickedId,
        bool ctrlOrMeta,
        bool shift,
        IReadOnlyList<string> renderOrderIds)
    {
        var multi = AllowMultiSelect;
        var current = EffectiveSelection;
        IReadOnlyList<string> next;

        if (multi && shift && current.Anchor is { } anchorId && anchorId != clickedId)
        {
            // Range select from anchor to clickedId in render order. Walk renderOrderIds
            // once, collecting unique ids between the first occurrences of anchorId and
            // clickedId (inclusive). The anchor stays as the first element of the new
            // ordered set so subsequent Shift+clicks continue to extend from the same
            // starting point — common spreadsheet behavior.
            var anchorIdx = -1;
            var clickedIdx = -1;
            for (var i = 0; i < renderOrderIds.Count; i++)
            {
                var id = renderOrderIds[i];
                if (anchorIdx < 0 && id == anchorId) anchorIdx = i;
                if (clickedIdx < 0 && id == clickedId) clickedIdx = i;
                if (anchorIdx >= 0 && clickedIdx >= 0) break;
            }

            if (anchorIdx < 0 || clickedIdx < 0)
            {
                // Anchor or clicked event not visible in current render order — fall
                // back to a single-id selection on the clicked event so Shift+click on
                // a partially-out-of-view layout still does something sensible.
                next = new[] { clickedId };
            }
            else
            {
                var lo = Math.Min(anchorIdx, clickedIdx);
                var hi = Math.Max(anchorIdx, clickedIdx);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var ordered = new List<string>(hi - lo + 1);
                // Keep anchor at index 0 of the new ordered set when the anchor is at
                // the higher end (drag-up Shift+click); reverse traversal otherwise.
                if (anchorIdx <= clickedIdx)
                {
                    for (var i = lo; i <= hi; i++)
                    {
                        var id = renderOrderIds[i];
                        if (seen.Add(id)) ordered.Add(id);
                    }
                }
                else
                {
                    for (var i = hi; i >= lo; i--)
                    {
                        var id = renderOrderIds[i];
                        if (seen.Add(id)) ordered.Add(id);
                    }
                }
                next = ordered;
            }
        }
        else if (multi && ctrlOrMeta)
        {
            // Toggle the clicked id in/out of the current selection. Re-add at the end
            // when toggling on so the anchor moves to the most-recently-clicked id; remove
            // in place when toggling off.
            if (current.Contains(clickedId))
            {
                var pruned = new List<string>(current.Count);
                foreach (var id in current)
                {
                    if (id != clickedId) pruned.Add(id);
                }
                next = pruned;
            }
            else
            {
                var extended = new List<string>(current.Count + 1);
                foreach (var id in current) extended.Add(id);
                extended.Add(clickedId);
                next = extended;
            }
        }
        else
        {
            // Plain click (or multi-select disabled and modifiers ignored): single-id
            // selection. Re-clicking the sole selected event is a no-op (no callback)
            // per the Task 10 commit-body policy: collapsing N>1 to 1 fires; collapsing
            // 1 to 1 doesn't.
            next = new[] { clickedId };
        }

        var changed = await ApplyNewSelectionAsync(next);
        return changed;
    }

    /// <summary>
    /// Keyboard Esc-clears-selection helper (Phase 2 Task 11, FR-34 keyboard surface).
    /// When the effective selection is non-empty, replaces it with an empty set,
    /// fires <see cref="OnSelectionChanged"/> with an empty list, and returns
    /// <see langword="true"/>. When the selection is already empty, no-ops and
    /// returns <see langword="false"/> so the caller can fall through to the
    /// existing FR-30 Esc behavior (blur the focused element via the per-view
    /// helper). The Esc-mid-drag precedence rule (ADR-0006) is the caller's
    /// responsibility — views check <c>IsDragActive</c> before calling here so
    /// the drag module's window-level Esc-cancels-drag listener owns the
    /// keystroke during an active drag.
    /// </summary>
    protected async Task<bool> TryClearSelectionViaKeyboardAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0)
        {
            return false;
        }
        return await ApplyNewSelectionAsync(Array.Empty<string>());
    }

    /// <summary>
    /// Phase 2 Task 12 — fire the delete callback for a focused event id, and on
    /// consumer accept prune the deleted ids from the effective selection (routing
    /// through the existing <see cref="ApplyNewSelectionAsync"/> write site so the
    /// cascade-vs-standalone fork and the <c>OnSelectionChanged</c> fire stay in
    /// one place).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements the routing rule documented on <see cref="AllowDelete"/>:
    /// <list type="bullet">
    ///   <item><description>Focused chip in a selection set of two or more events
    ///     → <see cref="OnEventsDeleted"/> with the full selection.</description></item>
    ///   <item><description>Focused chip not in the selection set, or selection
    ///     holds zero/one entries → <see cref="OnEventDeleted"/> with just the
    ///     focused chip.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// No-op when <paramref name="focusedEvent"/> resolves to <see langword="null"/>
    /// (the chip's id is no longer in the consumer's authoritative list) — the
    /// callback would have no event to carry. The caller is responsible for
    /// gating on <see cref="AllowDelete"/> + <c>IsDragActive</c> before invoking;
    /// this helper assumes both checks have already passed.
    /// </para>
    /// </remarks>
    /// <param name="focusedId">The event id the user pressed Delete on (the chip with focus).</param>
    /// <param name="focusedEvent">The resolved consumer event corresponding to <paramref name="focusedId"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the consumer accepted the delete. Returns
    /// <see langword="false"/> when the consumer canceled the delete or no
    /// matching selected event could be resolved for a batch delete. Callers use
    /// a successful result to restore scheduler focus after the focused chip is
    /// removed from the consumer's authoritative event list.
    /// </returns>
    protected async Task<bool> TryDeleteFocusedEventAsync(string focusedId, TEvent focusedEvent)
    {
        var selection = EffectiveSelection;
        var focusedInSelection = selection.Contains(focusedId);
        var multi = selection.Count >= 2 && focusedInSelection;

        if (multi)
        {
            // Snapshot the selection set as resolved consumer events. Mirrors
            // InvokeOnSelectionChangedAsync's id→TEvent projection so the batch
            // callback receives the same anchor-ordered list the consumer sees
            // via OnSelectionChanged. Resolution drops ids whose event isn't in
            // the current authoritative Events list — a stale id is invisible to
            // the consumer anyway, so the batch context shouldn't carry it.
            var resolved = ResolveSelectionToEvents(selection);
            if (resolved.Count == 0)
            {
                return false;
            }

            var batchCtx = new EventsDeletedContext<TEvent> { Events = resolved };
            await OnEventsDeleted.InvokeAsync(batchCtx);

            if (batchCtx.Cancel)
            {
                return false;
            }

            // Prune every deleted id from the selection. Selection is the only
            // library-rendered observable of "these events exist" — deleting them
            // from the consumer-side data store but leaving them in the selection
            // would leak ids the consumer can no longer match.
            var deletedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ev in resolved) deletedIds.Add(ev.Id);
            var pruned = new List<string>(selection.Count);
            foreach (var id in selection)
            {
                if (!deletedIds.Contains(id)) pruned.Add(id);
            }
            await ApplyNewSelectionAsync(pruned);
            return true;
        }

        // Single-event delete path. Covers both "no selection" and "focused chip
        // outside the held selection" (consumer ergonomics: don't surprise the
        // user with a batch wipe of events they didn't focus on).
        var ctx = new EventDeleteContext { Event = focusedEvent };
        await OnEventDeleted.InvokeAsync(ctx);

        if (ctx.Cancel)
        {
            return false;
        }

        // Prune the focused id from the selection if it happened to be in there.
        // When focusedInSelection is false (the "focused outside selection" case)
        // the selection stays intact and ApplyNewSelectionAsync is never called —
        // no spurious OnSelectionChanged fire.
        if (focusedInSelection)
        {
            var pruned = new List<string>(selection.Count - 1);
            foreach (var id in selection)
            {
                if (!string.Equals(id, focusedId, StringComparison.Ordinal)) pruned.Add(id);
            }
            await ApplyNewSelectionAsync(pruned);
        }
        return true;
    }

    /// <summary>
    /// Phase 2 Task 13 — match the supplied keystroke against the canonical undo /
    /// redo bindings (ADR-0013) and dispatch <see cref="OnUndoRequested"/> /
    /// <see cref="OnRedoRequested"/> when it matches. Returns <see langword="true"/>
    /// when the keystroke was a known undo/redo binding AND was dispatched (so the
    /// caller can short-circuit subsequent <c>e.Key</c> case-arms);
    /// <see langword="false"/> for any other keystroke OR when
    /// <see cref="AllowUndoRedo"/> is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bindings (per ADR-0013, hardcoded here until Task 14's <c>ShortcutMap</c>
    /// API lands):
    /// <list type="bullet">
    ///   <item><description><c>(Ctrl|Cmd)+Z</c> without <c>Shift</c> →
    ///     <see cref="OnUndoRequested"/>.</description></item>
    ///   <item><description><c>(Ctrl|Cmd)+Shift+Z</c> →
    ///     <see cref="OnRedoRequested"/>.</description></item>
    ///   <item><description><c>Ctrl+Y</c> (Shift state ignored) →
    ///     <see cref="OnRedoRequested"/>. <c>Cmd+Y</c> is intentionally not bound —
    ///     ADR-0013's canonical map specifies <c>Ctrl+Y</c> only, and on macOS
    ///     <c>Cmd+Y</c> is a system gesture (Yank / app-specific) the library
    ///     should not intercept.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Drag precedence is the caller's concern.</strong> This helper does
    /// NOT check <c>IsDragActive</c> — it mirrors Task 11 / Task 12's pattern
    /// where the view-side handler short-circuits on <c>IsDragActive</c> before
    /// dispatching keystroke matchers. The JS pointer module's window-level
    /// keydown listener owns cancel keystrokes during a drag.
    /// </para>
    /// <para>
    /// <c>e.Key</c> reports the printable character — <c>"z"</c> when Shift is
    /// not held and <c>"Z"</c> when it is (and similarly for <c>"y"</c> /
    /// <c>"Y"</c>). Both cases are matched so the binding works on any keyboard
    /// layout that maps these letters to the same physical keys; the
    /// <c>ShiftKey</c> flag is the canonical signal for the Shift modifier.
    /// </para>
    /// </remarks>
    /// <param name="e">The originating keyboard event.</param>
    /// <returns>
    /// <see langword="true"/> when the keystroke matched an undo/redo binding
    /// and was dispatched (caller should return). <see langword="false"/>
    /// otherwise (caller should continue processing — e.g., fall through to the
    /// regular <c>e.Key</c> switch for Enter / Space / Delete / Escape).
    /// </returns>
    protected async Task<bool> TryDispatchUndoRedoAsync(KeyboardEventArgs e)
    {
        if (!AllowUndoRedo)
        {
            return false;
        }

        var modifier = e.CtrlKey || e.MetaKey;
        if (!modifier)
        {
            return false;
        }

        // The Z bindings split on Shift: undo without Shift, redo with Shift.
        // Match both case variants of e.Key — browsers report "z" / "Z" depending
        // on the Shift state, and we want to be robust against either being
        // reported regardless (some synthetic event sources don't lowercase the
        // letter when ShiftKey is also set).
        if (string.Equals(e.Key, "z", StringComparison.Ordinal)
            || string.Equals(e.Key, "Z", StringComparison.Ordinal))
        {
            if (e.ShiftKey)
            {
                await OnRedoRequested.InvokeAsync();
            }
            else
            {
                await OnUndoRequested.InvokeAsync();
            }
            return true;
        }

        // Ctrl+Y is redo on Windows/Linux. The library does NOT bind Cmd+Y —
        // ADR-0013 specifies Ctrl+Y only, and Cmd+Y on macOS is reserved for
        // system / host-app gestures (Yank, Show Downloads, etc.). When the
        // user presses Cmd+Y the library lets the browser default proceed.
        if (e.CtrlKey
            && (string.Equals(e.Key, "y", StringComparison.Ordinal)
                || string.Equals(e.Key, "Y", StringComparison.Ordinal)))
        {
            await OnRedoRequested.InvokeAsync();
            return true;
        }

        return false;
    }

    // ----- Phase 2 Task 14 — shortcut-map dispatch ----------------------------------

    /// <summary>
    /// The scope a keystroke originated in. Drives which command ids
    /// <see cref="TryDispatchShortcutAsync(KeyboardEventArgs, KeystrokeScope)"/> is
    /// willing to dispatch — chip-scope keys
    /// (Space toggle, Delete) only fire when called from a chip-focused handler;
    /// grid-scope keys (create-at-focus, navigate-today) only fire from the grid; some
    /// (undo/redo, view-switch) fire from both.
    /// </summary>
    protected enum KeystrokeScope
    {
        /// <summary>Keystroke originated on a focused event chip (timed or all-day).</summary>
        Chip,

        /// <summary>Keystroke originated on the grid / cell container (no chip focused).</summary>
        Grid,
    }

    /// <summary>
    /// The resolved shortcut map in effect for this view's current render. Prefers the
    /// cascaded container's snapshot when present so cross-view consistency holds;
    /// otherwise falls back to the per-view local resolution. Rebuilt lazily on each
    /// access in the standalone path so the consumer's most-recent parameter set always
    /// flows through (Blazor's cascading-value path already triggers the rebuild on the
    /// cascade side).
    /// </summary>
    internal ResolvedShortcutMap EffectiveResolvedShortcuts
    {
        get
        {
            if (CascadedState?.ResolvedShortcuts is { } cascaded)
            {
                return cascaded;
            }
            EnsureLocalResolvedShortcuts();
            return _localResolvedShortcuts;
        }
    }

    /// <summary>
    /// Standalone-path rebuild of <c>_localResolvedShortcuts</c> when the consumer's
    /// parameter references change OR on first call after construction (when both
    /// inputs are null-at-default, ReferenceEquals(null, null) returns true and the
    /// dirty check alone wouldn't promote <see cref="ResolvedShortcutMap.Empty"/> to
    /// the full default map — the <c>_localResolvedInitialized</c> flag is the
    /// first-time gate). Called from both <see cref="EffectiveResolvedShortcuts"/>
    /// (covers "getter ran before the next OnParametersSet") and
    /// <see cref="OnParametersSet"/> (covers "parameters just changed; rebuild now so
    /// downstream `Commands` etc. see the fresh map"). Single source of truth so
    /// the two call sites stay in lockstep.
    /// </summary>
    private void EnsureLocalResolvedShortcuts()
    {
        if (!_localResolvedInitialized
            || !ReferenceEquals(_localResolvedInputs.Disabled, DisabledShortcuts)
            || !ReferenceEquals(_localResolvedInputs.Map, ShortcutMap))
        {
            _localResolvedInputs = (DisabledShortcuts, ShortcutMap);
            _localResolvedShortcuts = ResolvedShortcutMap.Resolve(DisabledShortcuts, ShortcutMap);
            _localResolvedInitialized = true;
        }
    }

    /// <summary>
    /// <see langword="true"/> when any chip-scope binding is enabled in the resolved
    /// shortcut map — drives the <c>@onkeydown:preventDefault</c> /
    /// <c>:stopPropagation</c> directives on event chips so the chip's browser
    /// default (Space-activates-button, etc.) is suppressed exactly when the library
    /// might claim the keystroke. With the always-on default map and the wide set of
    /// chip-scope bindings, this is effectively <see langword="true"/> unless the
    /// consumer has explicitly disabled every chip-scope command via
    /// <see cref="DisabledShortcuts"/> — uncommon.
    /// </summary>
    /// <remarks>
    /// Computing this as a property re-walks the resolved snapshot on every render,
    /// which is negligible (~20 entries × an O(1) set lookup). Caching would buy
    /// microseconds at the cost of stale-cache risk when the cascade pushes a new
    /// resolved map; the simple computed form is the safer choice.
    /// </remarks>
    internal bool IsShortcutMapActive
    {
        get
        {
            var resolved = EffectiveResolvedShortcuts;
            for (var i = 0; i < resolved.Snapshot.Count; i++)
            {
                if (IsChipScopeCommand(resolved.Snapshot[i].CommandId))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the supplied command id participates in the
    /// chip keystroke scope (see ADR-0013's binding table). Used by
    /// <see cref="IsShortcutMapActive"/> and by
    /// <see cref="TryDispatchShortcutAsync(KeyboardEventArgs, KeystrokeScope)"/>'s
    /// scope filter.
    /// </summary>
    private static bool IsChipScopeCommand(string commandId) => commandId switch
    {
        SchedulerCommandIds.EditDelete => true,
        SchedulerCommandIds.SelectToggle => true,
        SchedulerCommandIds.Cancel => true,
        SchedulerCommandIds.NavigateToday => true,
        SchedulerCommandIds.EditUndo => true,
        SchedulerCommandIds.EditRedo => true,
        SchedulerCommandIds.ViewDay => true,
        SchedulerCommandIds.ViewWeek => true,
        SchedulerCommandIds.ViewMonth => true,
        SchedulerCommandIds.ViewYear => true,
        SchedulerCommandIds.ViewAgenda => true,
        SchedulerCommandIds.ViewTimeline => true,
        SchedulerCommandIds.ViewWorkWeek => true,
        SchedulerCommandIds.PaletteOpen => true,
        SchedulerCommandIds.HelpOpen => true,
        SchedulerCommandIds.EditMove => true,
        SchedulerCommandIds.EditResize => true,
        _ => false, // edit.create is grid-scope only; consumer-defined ids default to false.
    };

    /// <summary>
    /// Returns <see langword="true"/> when the supplied command id participates in the
    /// grid keystroke scope.
    /// </summary>
    private static bool IsGridScopeCommand(string commandId) => commandId switch
    {
        SchedulerCommandIds.Cancel => true,
        SchedulerCommandIds.NavigateToday => true,
        SchedulerCommandIds.EditCreate => true,
        SchedulerCommandIds.EditUndo => true,
        SchedulerCommandIds.EditRedo => true,
        SchedulerCommandIds.ViewDay => true,
        SchedulerCommandIds.ViewWeek => true,
        SchedulerCommandIds.ViewMonth => true,
        SchedulerCommandIds.ViewYear => true,
        SchedulerCommandIds.ViewAgenda => true,
        SchedulerCommandIds.ViewTimeline => true,
        SchedulerCommandIds.ViewWorkWeek => true,
        SchedulerCommandIds.PaletteOpen => true,
        SchedulerCommandIds.HelpOpen => true,
        _ => false, // chip-only: edit.delete, select.toggle, edit.move, edit.resize.
    };

    /// <summary>
    /// Match the supplied keystroke against the resolved shortcut map and dispatch the
    /// matched command's callback. Returns <see langword="true"/> when a command was
    /// dispatched (so the caller short-circuits subsequent <c>e.Key</c> case-arms),
    /// <see langword="false"/> otherwise. Scope-aware: chip-scope commands only fire
    /// when <paramref name="scope"/> is <see cref="KeystrokeScope.Chip"/>; grid-scope
    /// commands only when <see cref="KeystrokeScope.Grid"/>; some commands (undo/redo,
    /// view-switch, cancel, navigate-today, palette, help) fire from both scopes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Fail-closed gating.</strong> Each dispatch branch additionally checks the
    /// command's backing <c>Allow*</c> flag where one exists:
    /// <list type="bullet">
    ///   <item><description><see cref="SchedulerCommandIds.EditUndo"/> /
    ///     <see cref="SchedulerCommandIds.EditRedo"/> require <see cref="AllowUndoRedo"/>.</description></item>
    ///   <item><description><see cref="SchedulerCommandIds.EditDelete"/> requires
    ///     <see cref="AllowDelete"/>.</description></item>
    ///   <item><description><see cref="SchedulerCommandIds.SelectToggle"/> requires
    ///     <see cref="AllowMultiSelect"/> (matching Task 11's Space-toggle gate).</description></item>
    /// </list>
    /// The shortcut map is the binding layer; the action gates are unchanged. A
    /// keystroke gated out by an <c>Allow*</c> flag is treated as "no match" — the
    /// helper returns <see langword="false"/> so the per-view handler can fall through
    /// to its existing default behavior (typically the FR-30 blur path).
    /// </para>
    /// <para>
    /// <strong>Drag precedence is the caller's concern.</strong> Same shape as
    /// <see cref="TryDispatchUndoRedoAsync"/>: this helper does NOT check
    /// <c>IsDragActive</c>; the view-side handler short-circuits on it before invoking.
    /// </para>
    /// <para>
    /// <strong>Per-view side effects (selection, navigation, etc.)</strong> are NOT in
    /// this helper. Commands that need view-specific work — Space toggle through the
    /// click path, Delete through <see cref="TryDeleteFocusedEventAsync"/>, Escape
    /// through the view's <c>HandleEscapeAsync</c> — are dispatched via the per-view
    /// hook in <see cref="DispatchViewCommandAsync"/>. The base handles the global
    /// commands (today, view-switch, undo/redo, palette, help, move-mode, resize) so
    /// every view inherits identical behavior for them.
    /// </para>
    /// </remarks>
    /// <param name="e">The originating keyboard event.</param>
    /// <param name="scope">Whether the keystroke fired on a chip handler or the grid handler.</param>
    /// <returns>
    /// <see langword="true"/> when a command was matched AND dispatched. <see langword="false"/>
    /// otherwise (caller continues processing).
    /// </returns>
    protected Task<bool> TryDispatchShortcutAsync(KeyboardEventArgs e, KeystrokeScope scope)
        => TryDispatchShortcutAsync(e, scope, focusedEvent: default, focusedEventId: null);

    /// <summary>
    /// Variant of <see cref="TryDispatchShortcutAsync(KeyboardEventArgs, KeystrokeScope)"/>
    /// that threads the chip-handler's focused event through to view-specific dispatch
    /// (Delete / Space toggle). Chip-scope handlers call this overload; grid-scope
    /// handlers call the parameterless variant.
    /// </summary>
    /// <param name="e">The originating keyboard event.</param>
    /// <param name="scope">Always <see cref="KeystrokeScope.Chip"/> for callers that supply a focused event.</param>
    /// <param name="focusedEvent">The consumer <typeparamref name="TEvent"/> the chip represents.</param>
    /// <param name="focusedEventId">Id of the focused event (forwarded to <see cref="TryDeleteFocusedEventAsync"/>).</param>
    protected async Task<bool> TryDispatchShortcutAsync(
        KeyboardEventArgs e,
        KeystrokeScope scope,
        TEvent? focusedEvent,
        string? focusedEventId)
    {
        var resolved = EffectiveResolvedShortcuts;
        var commandId = resolved.Match(e);
        if (commandId is null)
        {
            return false;
        }

        // Scope filter — chip-scope commands only fire from chip handlers, grid-scope
        // only from the grid. Both lists overlap for commands that fire from either
        // scope (undo/redo, cancel, today, view-switch, palette, help).
        var inScope = scope == KeystrokeScope.Chip
            ? IsChipScopeCommand(commandId)
            : IsGridScopeCommand(commandId);
        if (!inScope)
        {
            return false;
        }

        // Fail-closed gates: feature commands only fire when their Allow* flag is true.
        switch (commandId)
        {
            case SchedulerCommandIds.EditUndo:
            case SchedulerCommandIds.EditRedo:
                if (!AllowUndoRedo) return false;
                break;
            case SchedulerCommandIds.EditDelete:
                if (!AllowDelete) return false;
                break;
            case SchedulerCommandIds.SelectToggle:
                // Space-toggle is opt-in via AllowMultiSelect; when disabled the
                // browser default proceeds (Space activates the focused button, which
                // routes through the click path to a single-id selection — FR-29 fail-
                // closed). Returning false here lets the view's existing branch handle
                // that fallback OR the browser-default path run.
                if (!AllowMultiSelect) return false;
                break;
            case SchedulerCommandIds.PaletteOpen:
                // Phase 2 Task 15 (FR-37 / ADR-0014). The Cmd+K / Ctrl+K keystroke is
                // matched by the resolved shortcut map regardless of AllowCommandPalette;
                // the gate lives here so the dispatch returns "no match" when the
                // consumer hasn't opted in. Symmetric with the Commands list being empty
                // in the same configuration — the trigger and the API stay in lockstep.
                if (!AllowCommandPalette) return false;
                break;
        }

        // Dispatch. Base-handled commands fire here; view-specific commands route through
        // the abstract hook so each view's resolution (focused chip id, focused slot
        // index) drives the side effect.
        switch (commandId)
        {
            case SchedulerCommandIds.EditUndo:
                await OnUndoRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.EditRedo:
                await OnRedoRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.NavigateToday:
                await OnTodayRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.HelpOpen:
                await OnHelpRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.PaletteOpen:
                await OnCommandPaletteRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.EditCreate:
                // Issue #8 — fail-closed: the create-at-focus keystroke is a no-op when
                // the focused grid position is on a blocked day. Still "matched" (return
                // true) so no other case-arm double-handles the keystroke — same shape
                // as edit.delete's "matched but no-op when nothing is focused" pattern.
                if (IsFocusedGridDayBlocked()) return true;
                await OnCreateAtFocusRequested.InvokeAsync();
                return true;
            case SchedulerCommandIds.EditMove:
                await OnMoveModeRequested.InvokeAsync();
                await DispatchKeyboardMoveAsync(focusedEvent, focusedEventId);
                return true;
            case SchedulerCommandIds.EditResize:
                await OnResizeKeystrokeRequested.InvokeAsync();
                var direction = e.Key == "ArrowUp" ? KeyboardResizeDirection.Extend : KeyboardResizeDirection.Shrink;
                await DispatchKeyboardResizeAsync(focusedEvent, focusedEventId, direction);
                return true;
            case SchedulerCommandIds.ViewDay:
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Day);
                return true;
            case SchedulerCommandIds.ViewWeek:
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Week);
                return true;
            case SchedulerCommandIds.ViewMonth:
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Month);
                return true;
            case SchedulerCommandIds.ViewYear:
                // Phase 2 Task 16 — Year view shipped, so the binding flips from
                // Task 14's "matched but no-op" placeholder to a live view-switch.
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Year);
                return true;
            case SchedulerCommandIds.ViewAgenda:
                // Phase 2 Task 17 — Agenda view shipped; binding flips from
                // matched-but-no-op to a live view-switch. Mirrors ViewYear's wiring.
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Agenda);
                return true;
            case SchedulerCommandIds.ViewTimeline:
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.Timeline);
                return true;
            case SchedulerCommandIds.ViewWorkWeek:
                // Issue #7 — same live-dispatch shape as ViewYear/ViewAgenda above.
                await OnViewSwitchRequested.InvokeAsync(SchedulerView.WorkWeek);
                return true;
        }

        // Selection-toggle / Cancel / Delete need per-view context (focused chip id,
        // focused slot index, blur target). Delegate to the abstract hook so each view's
        // existing helper drives the side effect.
        return await DispatchViewCommandAsync(commandId, e, scope, focusedEvent, focusedEventId);
    }

    /// <summary>
    /// Per-view hook for command ids that need view-specific context to dispatch:
    /// <see cref="SchedulerCommandIds.SelectToggle"/> needs the focused chip id;
    /// <see cref="SchedulerCommandIds.Cancel"/> needs the view's blur target;
    /// <see cref="SchedulerCommandIds.EditDelete"/> needs both the chip id and the
    /// typed <typeparamref name="TEvent"/>. The keystroke arrives at the chip-level
    /// handler with the chip's <c>ev</c> already resolved by the per-view razor
    /// markup; that callsite passes the chip via a thread-local or via a closure
    /// captured in a wrapper helper (see each view's
    /// <c>HandleEventKeyDownAsync</c> for the precise plumbing).
    /// </summary>
    /// <remarks>
    /// Returning <see langword="false"/> means "I didn't dispatch this command";
    /// returning <see langword="true"/> short-circuits the caller's case-arm switch.
    /// The default base implementation handles only the global commands the base
    /// already knows enough context for; everything view-specific is handled by the
    /// per-view <c>HandleEventKeyDownAsync</c> directly (see Day view for the
    /// pattern), so this hook defaults to returning <see langword="false"/>. Per-
    /// view overrides are optional.
    /// </remarks>
    private protected virtual Task<bool> DispatchViewCommandAsync(
        string commandId,
        KeyboardEventArgs e,
        KeystrokeScope scope,
        TEvent? focusedEvent,
        string? focusedEventId)
        => Task.FromResult(false);

    /// <summary>
    /// Per-view hook for keyboard move dispatch. The base class calls this when the user presses
    /// the move-mode binding (default <c>m</c>) on a focused event chip. Each view overrides this
    /// to enter keyboard move mode, track the phantom position, and handle arrow keys.
    /// </summary>
    private protected virtual Task DispatchKeyboardMoveAsync(TEvent? focusedEvent, string? focusedEventId)
        => Task.CompletedTask;

    /// <summary>
    /// Per-view hook for keyboard resize dispatch. The base class calls this when the user presses
    /// one of the resize keystrokes (default <c>Shift+ArrowUp</c> / <c>Shift+ArrowDown</c>) on a focused
    /// event chip. Each view overrides this to resize the event by one slot and fire <see cref="OnEventResized"/>.
    /// </summary>
    private protected virtual Task DispatchKeyboardResizeAsync(TEvent? focusedEvent, string? focusedEventId, KeyboardResizeDirection direction)
        => Task.CompletedTask;

    /// <summary>
    /// Resolve the supplied selection's ids to consumer <typeparamref name="TEvent"/>
    /// instances using the current authoritative <see cref="Events"/> list. Mirrors
    /// <see cref="InvokeOnSelectionChangedAsync"/>'s projection logic so the batch
    /// delete callback and <see cref="OnSelectionChanged"/> agree on shape.
    /// </summary>
    private IReadOnlyList<TEvent> ResolveSelectionToEvents(SchedulerSelection selection)
    {
        var events = Events;
        if (events is null || events.Count == 0 || selection.Count == 0)
        {
            return Array.Empty<TEvent>();
        }
        var lookup = new Dictionary<string, TEvent>(events.Count, StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            lookup[ev.Id] = ev;
        }
        var resolved = new List<TEvent>(selection.Count);
        foreach (var id in selection)
        {
            if (lookup.TryGetValue(id, out var ev))
            {
                resolved.Add(ev);
            }
        }
        return resolved;
    }

    /// <summary>
    /// Apply the supplied new ordered set as the effective selection. Routes through
    /// the cascaded container's <see cref="SchedulerStateContainer.RequestSelectionChange"/>
    /// callback when present (so the root scheduler owns the single write site and
    /// the consumer-visible <c>OnSelectionChanged</c> fires from one place); otherwise
    /// updates the local selection directly. Fires <c>OnSelectionChanged</c> in the
    /// standalone-view case. Returns <see langword="true"/> when the selection
    /// actually changed.
    /// </summary>
    private async Task<bool> ApplyNewSelectionAsync(IReadOnlyList<string> newOrderedIds)
    {
        if (CascadedState?.RequestSelectionChange is { } request)
        {
            // Cascade path: ask the root to mutate the canonical selection. The root's
            // handler updates SchedulerStateContainer.Selection in place, fires its own
            // OnSelectionChanged from there, and SyncStateContainer-then-StateHasChanged
            // so the cascading consumers re-render. Return the change result by
            // checking whether the cascade's Selection mutated to the new content. The
            // cascade's Replace returns the change bit; we infer it by checking the
            // post-state since the request callback returns Task (no bool channel).
            var pre = SnapshotIds(CascadedState!.Selection);
            await request(newOrderedIds);
            var post = SnapshotIds(CascadedState!.Selection);
            return !SequenceEqual(pre, post);
        }

        // Standalone-view path. We mutate the local selection directly and fire
        // OnSelectionChanged from the view itself.
        var changed = _localSelection.Replace(newOrderedIds);
        if (changed)
        {
            await InvokeOnSelectionChangedAsync();
        }
        return changed;
    }

    /// <summary>
    /// Resolve the current selection's ids to consumer TEvents and fire
    /// <see cref="OnSelectionChanged"/>. Used by the standalone-view path and by the
    /// root scheduler's selection-change handler. Resolution drops ids whose event no
    /// longer appears in <see cref="Events"/> — the consumer's authoritative list is
    /// the source of truth, and a stale id in the selection set survives the next
    /// parameter set's lookup (the visual class won't apply either) without firing a
    /// spurious callback.
    /// </summary>
    internal Task InvokeOnSelectionChangedAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0)
        {
            return OnSelectionChanged.InvokeAsync(Array.Empty<TEvent>());
        }
        var events = Events;
        if (events is null || events.Count == 0)
        {
            return OnSelectionChanged.InvokeAsync(Array.Empty<TEvent>());
        }
        // Build an id→TEvent lookup for the current render pass and project selection
        // ids into TEvents in anchor order. O(N + M) — fine for the typical small
        // visible window.
        var lookup = new Dictionary<string, TEvent>(events.Count, StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            lookup[ev.Id] = ev;
        }
        var resolved = new List<TEvent>(selection.Count);
        foreach (var id in selection)
        {
            if (lookup.TryGetValue(id, out var ev))
            {
                resolved.Add(ev);
            }
        }
        return OnSelectionChanged.InvokeAsync(resolved);
    }

    private static string[] SnapshotIds(SchedulerSelection sel) => sel.ToOrderedList().ToArray();

    private static bool SequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the <c>aria-roledescription</c> string for a chip given the currently-
    /// enabled drag affordances. Returns <see langword="null"/> when neither
    /// <see cref="AllowDragToMove"/> nor <see cref="AllowDragToResize"/> is enabled
    /// (the chip's role is a plain button and no extra description is announced).
    /// </summary>
    /// <remarks>
    /// Centralized here so interactive views emit the exact same string for a given
    /// combination of flags. The "draggable resizable event" combined string is used
    /// when both flags are on so screen readers announce both affordances; the order
    /// matches the chronology of the user's typical interaction (grab to move →
    /// optionally drag the edge to resize).
    /// </remarks>
    protected string? GetEventChipAriaRoleDescription()
    {
        var move = AllowDragToMove;
        var resize = AllowDragToResize;
        if (move && resize) return "draggable resizable event";
        if (move) return "draggable event";
        if (resize) return "resizable event";
        return null;
    }

    /// <summary>
    /// Resolve the default duration (in minutes) for a double-click-to-create
    /// (FR-32). When the consumer has set <see cref="CaleeSchedulerOptions.DefaultCreateDurationMinutes"/>
    /// explicitly, that value wins across every view. When the option is
    /// <see langword="null"/>, the per-view default applies:
    /// <list type="bullet">
    ///   <item>
    ///     <description>Time-grid views (Day / Week / TimelineView at
    ///     <c>TimelineScale.Day</c>) resolve to <paramref name="slotDurationMinutes"/>
    ///     — one slot.</description>
    ///   </item>
    ///   <item>
    ///     <description>Whole-day-cell views (Month; TimelineView at
    ///     <c>TimelineScale.Week</c> / <c>TimelineScale.Month</c>) resolve to
    ///     <c>1440</c> minutes (one day).</description>
    ///   </item>
    /// </list>
    /// Centralizing the resolution here keeps every view honoring the same rule and
    /// gives the consumer one knob to override.
    /// </summary>
    /// <param name="slotDurationMinutes">
    /// The current view's resolved <c>SlotDurationMinutes</c>. Only consulted when
    /// <paramref name="useWholeDayDefault"/> is <see langword="false"/> AND the option
    /// is <see langword="null"/>.
    /// </param>
    /// <param name="useWholeDayDefault">
    /// <see langword="true"/> when the calling view's slot is a whole day (Month;
    /// TimelineView at Week/Month scale). Causes the null-option fallback to resolve
    /// to <c>1440</c> instead of <paramref name="slotDurationMinutes"/>.
    /// </param>
    /// <returns>
    /// The resolved duration in minutes. Always &gt;= 1 (the per-view fallbacks both
    /// satisfy this).
    /// </returns>
    private protected int ResolveDefaultCreateDurationMinutes(int slotDurationMinutes, bool useWholeDayDefault)
    {
        var explicitOption = SchedulerOptions.Value.DefaultCreateDurationMinutes;
        if (explicitOption is int value)
        {
            return value;
        }
        return useWholeDayDefault ? 1440 : slotDurationMinutes;
    }

    /// <summary>
    /// Service-level defaults (start/end hour, slot duration, etc.) registered via
    /// <see cref="ServiceCollectionExtensions.AddCaleeScheduler"/>.
    /// </summary>
    [Inject]
    protected IOptions<CaleeSchedulerOptions> SchedulerOptions { get; set; } = default!;

    /// <summary>
    /// Optional logger. Used for the soft-degradation paths in PRD §4.6 (e.g., null
    /// <see cref="Events"/> treated as empty). When DI did not provide a logger, all
    /// logging calls no-op via the null-conditional operator.
    /// </summary>
    [Inject]
    protected ILogger<SchedulerComponentBase<TEvent>>? Logger { get; set; }

    /// <summary>
    /// Resolves <see cref="ResolvedTimeZone"/> per issue #34's layered chain, then
    /// validates the remaining required parameters per PRD §4.6.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// None of <see cref="TimeZone"/>, an ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c>,
    /// or <see cref="CaleeSchedulerOptions.DefaultTimeZone"/> supplied a value.
    /// </exception>
    protected override void OnParametersSet()
    {
        ResolvedTimeZone = ResolveTimeZone();

        // Phase 2 Task 14 — rebuild the standalone-mode resolved shortcut map when the
        // consumer's parameter references change. The cascade-hosted path is owned by
        // the root's SyncStateContainer; this branch only runs for the standalone case
        // because the cascade preempts in EffectiveResolvedShortcuts.
        EnsureLocalResolvedShortcuts();

        // Phase 2 Task 15 — rebuild the Commands list when any input changes. Same
        // dirty-check pattern as the shortcut map above; the "_commandsInitialized" flag
        // covers the "all inputs at default" first-render case (where reference-equality
        // on null vs null would otherwise skip the first build and leave Commands at the
        // Array.Empty sentinel even when AllowCommandPalette=true with no custom commands).
        if (!_commandsInitialized
            || _lastCommandsInputs.Palette != AllowCommandPalette
            || _lastCommandsInputs.Delete != AllowDelete
            || _lastCommandsInputs.UndoRedo != AllowUndoRedo
            || !ReferenceEquals(_lastCommandsInputs.Custom, CustomCommands))
        {
            _lastCommandsInputs = (AllowCommandPalette, AllowDelete, AllowUndoRedo, CustomCommands);
            RebuildCommands();
            _commandsInitialized = true;
        }
    }

    /// <summary>
    /// Returns the materialized list of events for this render pass, with the optional
    /// <see cref="EventFilter"/> applied. <see langword="null"/> <see cref="Events"/> is
    /// treated as empty (PRD §4.6 soft-degradation).
    /// </summary>
    /// <remarks>
    /// The list is materialized — repeated enumeration is expected during rendering and
    /// returning a deferred enumerable would re-execute the filter every time.
    /// </remarks>
    /// <returns>A non-null, possibly empty, materialized list of events to render.</returns>
    protected IReadOnlyList<TEvent> GetFilteredEvents()
    {
        if (Events is null)
        {
            Logger?.LogDebug(
                "Calee.Scheduler: Events parameter was null; treating as empty (PRD §4.6 soft-degradation).");
            return Array.Empty<TEvent>();
        }

        if (EventFilter is null)
        {
            EnsureUniqueEventIds(Events);
            return Events;
        }

        var filtered = new List<TEvent>(Events.Count);
        for (var i = 0; i < Events.Count; i++)
        {
            var ev = Events[i];
            if (EventFilter(ev))
            {
                filtered.Add(ev);
            }
        }

        EnsureUniqueEventIds(filtered);
        return filtered;
    }

    private static void EnsureUniqueEventIds(IReadOnlyList<TEvent> events)
    {
        if (events.Count < 2) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var id = events[i].Id;
            if (seen.Add(id)) continue;

            throw new ArgumentException(
                $"Events contains duplicate event Id '{id}'. Event Id values must be unique within the rendered event set.",
                nameof(Events));
        }
    }

    /// <summary>
    /// Returns the consumer-supplied CSS class for the given event, or <see langword="null"/>
    /// when no <see cref="EventClass"/> hook is configured.
    /// </summary>
    /// <param name="ev">The event to query.</param>
    /// <returns>A CSS class string, or <see langword="null"/>.</returns>
    protected string? GetEventClass(TEvent ev) => EventClass?.Invoke(ev);

    /// <summary>
    /// Returns the consumer-supplied per-day state for <paramref name="day"/> (issue
    /// #8), or <see langword="null"/> when no <see cref="DayModifier"/> hook is
    /// configured or the hook itself returns <see langword="null"/> for that day
    /// (the "normal day" case in both branches).
    /// </summary>
    /// <param name="day">
    /// The day's midnight boundary in <see cref="ResolvedTimeZone"/> (ADR-0001) — callers
    /// pass the same per-day instant used elsewhere for that day's grid math, not an
    /// arbitrary time-of-day.
    /// </param>
    protected SchedulerDayState? GetDayState(DateTimeOffset day) => DayModifier?.Invoke(day);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="day"/> is blocked per
    /// <see cref="DayModifier"/>. Shorthand for the common
    /// <c>GetDayState(day)?.IsBlocked ?? false</c> check.
    /// </summary>
    protected bool IsDayBlocked(DateTimeOffset day) => GetDayState(day)?.IsBlocked ?? false;

    /// <summary>
    /// Fail-closed check for a create's spanned region (issue #8): returns
    /// <see langword="true"/> when any whole day touched by
    /// <c>[start, endExclusive)</c> is blocked per <see cref="DayModifier"/>. Callers
    /// skip firing <see cref="OnEventCreated"/> when this returns <see langword="true"/>
    /// — "the swept region touches any blocked day" is the simplest defensible rule for
    /// a create that starts on an open day and crosses into a blocked one (or the
    /// reverse). Every current view's create paths (double-click, drag-to-create) are
    /// column/cell-locked to a single day, so in practice this reduces to checking that
    /// one anchor day — but the check is written generally so it stays correct if a
    /// future multi-day create sweep is ever added.
    /// </summary>
    /// <param name="start">Inclusive start of the proposed create span.</param>
    /// <param name="endExclusive">Exclusive end of the proposed create span.</param>
    protected bool CreateSpanTouchesBlockedDay(DateTimeOffset start, DateTimeOffset endExclusive)
    {
        if (DayModifier is null) return false;

        var days = SchedulerViewPrimitives.ComputeDayBounds(start, endExclusive, ResolvedTimeZone);
        for (var i = 0; i < days.Count; i++)
        {
            if (DayModifier(days[i].Start)?.IsBlocked == true) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the current wall-clock time in <see cref="ResolvedTimeZone"/> formatted
    /// as e.g. "10:35 AM". Used by the views' current-time indicators as a hover-tooltip
    /// title so screen-readable info is available without the user having to look
    /// elsewhere. The value is computed at render time — re-renders that change anything
    /// else on the page will refresh it, but the library doesn't poll on a timer to keep
    /// it second-precise.
    /// </summary>
    protected string CurrentTimeText()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolvedTimeZone);
        return now.ToString("h:mm tt");
    }
}
