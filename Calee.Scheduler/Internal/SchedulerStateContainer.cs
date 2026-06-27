#nullable enable
using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Mutable cascading-value carrier shared between the root <c>CaleeScheduler&lt;TEvent&gt;</c>
/// component (Task 11) and <c>CaleeSchedulerToolbar</c> (this task). The root populates
/// every member on each parameter set, wires the <c>Request*</c> callbacks to its own
/// state-update methods, and surfaces the instance via
/// <c>&lt;CascadingValue Value="@_state" IsFixed="false"&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a mutable class instead of a record.</strong> The toolbar reads from this
/// container on every render; treating it as immutable would force the root scheduler to
/// allocate a new instance whenever any field changes, which would re-render the entire
/// subtree under <c>CascadingValue</c> regardless of which field actually moved. Keeping
/// the instance stable and mutating its members lets Blazor's normal change-detection
/// flow through.
/// </para>
/// <para>
/// <strong>Visibility.</strong> Internal. The toolbar and the root scheduler are the only
/// consumers; making this <see langword="public"/> would expose a Phase 1 implementation
/// detail to the contract surface (NFR-07).
/// </para>
/// </remarks>
internal sealed class SchedulerStateContainer
{
    /// <summary>The view currently displayed by the root scheduler.</summary>
    public SchedulerView CurrentView { get; set; }

    /// <summary>The anchor date of the currently-displayed view.</summary>
    public DateTimeOffset CurrentDate { get; set; }

    /// <summary>The visible range (start inclusive, end exclusive) for the toolbar's range label.</summary>
    public SchedulerRange CurrentRange { get; set; } = new(DateTimeOffset.MinValue, DateTimeOffset.MinValue);

    /// <summary>The grid time zone supplied to the root scheduler. Required (PRD §4.6).</summary>
    public TimeZoneInfo TimeZone { get; set; } = default!;

    /// <summary>The configured first day of the week (FR-04). Drives week-boundary computation in the range label.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; }

    /// <summary>
    /// The set of view buttons rendered by the toolbar's view switcher. The Timeline entry
    /// is included only when the root scheduler is wired with lane parameters per FR-09c.
    /// </summary>
    public IReadOnlyList<SchedulerView> AvailableViews { get; set; } = Array.Empty<SchedulerView>();

    /// <summary>
    /// The current X-axis time scale when <see cref="CurrentView"/> is
    /// <see cref="SchedulerView.Timeline"/>; <see langword="null"/> otherwise. Drives
    /// the range-label format for TimelineView.
    /// </summary>
    public TimelineScale? TimelineScale { get; set; }

    /// <summary>
    /// The rolling N-day window length used by the Agenda view (Phase 2 Task 17 — FR-39).
    /// Drives the toolbar's prev/next step (the window length, not one day) and the
    /// FormatRangeLabel output when <see cref="CurrentView"/> is
    /// <see cref="SchedulerView.Agenda"/>. Defaults to <c>7</c> to match the
    /// <c>AgendaDays</c> parameter default on
    /// <c>CaleeSchedulerAgendaView&lt;TEvent&gt;</c>.
    /// </summary>
    public int AgendaDays { get; set; } = 7;

    /// <summary>
    /// Callback the toolbar invokes when the user picks a different view from the switcher.
    /// The root scheduler wires this to its own view-state update so the bindable
    /// <c>View</c> parameter and <c>OnViewChanged</c> callback fire correctly (FR-31, FR-22).
    /// </summary>
    public Func<SchedulerView, Task>? RequestViewChange { get; set; }

    /// <summary>
    /// Callback the toolbar invokes for Today / Previous / Next navigation. The root scheduler
    /// wires this to its own date-state update so <c>DateChanged</c> and <c>OnRangeChanged</c>
    /// fire correctly (FR-23).
    /// </summary>
    public Func<DateTimeOffset, Task>? RequestDateChange { get; set; }

    /// <summary>
    /// Whether the root scheduler has opted in to multi-event selection (FR-29 fail-closed
    /// default is <see langword="false"/>). Cascaded down to views so they can ignore
    /// ctrl/shift modifiers when the consumer has not enabled multi-select.
    /// </summary>
    public bool AllowMultiSelect { get; set; }

    /// <summary>
    /// The current selection, an ordered set of event ids preserving insertion order so
    /// the anchor for Shift+click range select (Task 11 keyboard equivalents reuse it) is
    /// always the last entry. Read-only externally — mutations route through
    /// <see cref="RequestSelectionChange"/> so the root owns the canonical write site
    /// and fires <c>OnSelectionChanged</c> from one place.
    /// </summary>
    public SchedulerSelection Selection { get; } = new();

    /// <summary>
    /// Callback descendants invoke when a click handler determines a new selection set.
    /// The root scheduler wires this to its own selection-update method so the cascaded
    /// <see cref="Selection"/> stays in lockstep with the consumer-visible
    /// <c>OnSelectionChanged</c> callback. The argument is the new ordered id set; the
    /// root replaces <see cref="Selection"/> with it.
    /// </summary>
    /// <remarks>
    /// Selection lives on the root because the active view component changes when the
    /// consumer switches <c>View</c> — a Day → Week swap unmounts the Day view instance
    /// entirely. Holding selection in the cascading container (whose reference identity
    /// is stable across view swaps) is what gives FR-34 its cross-view persistence.
    /// </remarks>
    public Func<IReadOnlyList<string>, Task>? RequestSelectionChange { get; set; }

    /// <summary>
    /// The canonical resolved keyboard shortcut map for the root scheduler (Phase 2
    /// Task 14 — FR-36). Computed once per <c>OnParametersSet</c> on the root and
    /// stored here so every descendant view reads the same snapshot via the cascade —
    /// avoids each view re-parsing the consumer's overrides on every render and keeps
    /// the dispatch deterministic across view swaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never <see langword="null"/> after the root's first <c>OnParametersSet</c> runs;
    /// initialized to <see cref="ResolvedShortcutMap.Empty"/> in the container's
    /// constructor so a brief cascade-not-yet-set window doesn't NRE the view-side
    /// reader.
    /// </para>
    /// <para>
    /// Standalone views (no cascade) compute their own resolved map and ignore this
    /// field — see <c>SchedulerComponentBase.EffectiveResolvedShortcuts</c>.
    /// </para>
    /// </remarks>
    public ResolvedShortcutMap ResolvedShortcuts { get; set; } = ResolvedShortcutMap.Empty;
}
