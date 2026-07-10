#nullable enable
using Calee.Scheduler.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Internal abstract base for views that own a navigable <c>Date</c> anchor (Day, Week,
/// Month, TimelineView). Extends <see cref="SchedulerComponentBase{TEvent}"/> with the
/// bindable <c>Date</c> / <c>DateChanged</c> pattern and an internal-state fallback for
/// the uncontrolled case.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Controllable pattern.</strong> When the consumer supplies <c>Date</c>, the view
/// is in <em>controlled</em> mode: <c>CurrentDate</c> reflects the parameter, and
/// <see cref="SetCurrentDateAsync"/> simply fires <c>DateChanged</c> — the consumer is
/// expected to push a new <c>Date</c> back in (typical <c>@bind-Date</c> usage). When the
/// consumer omits <c>Date</c>, the view is in <em>uncontrolled</em> mode: it tracks the
/// anchor internally, seeded from "today in <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>"
/// per FR-09a.
/// </para>
/// <para>
/// <strong>Why <c>View</c> is not on this base.</strong> The bindable <c>View</c> parameter
/// (FR-31) is exclusive to the root <c>CaleeScheduler&lt;TEvent&gt;</c> component — Day/Week/Month/
/// TimelineView are concrete view modes, so a <c>View</c> on them would be meaningless. The
/// same internal-state-fallback pattern implemented here for <c>Date</c> is reproduced
/// directly on <c>CaleeScheduler&lt;TEvent&gt;</c> in Task 11 for <c>View</c>.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class SchedulerStatefulComponentBase<TEvent> : SchedulerComponentBase<TEvent>, IAsyncDisposable
    where TEvent : ICalendarEvent
{
    /// <summary>
    /// Lazily-initialized JS drag wrapper. Null in test environments where
    /// <see cref="PointerDragInterop.CreateAsync"/> cannot import the module
    /// (no real DOM / no JS runtime), and remains null in views that never
    /// invoke <see cref="BeginDragOnPointerAsync"/> — the module isn't loaded
    /// until the first drag attempt.
    /// </summary>
    private PointerDragInterop? _drag;

    /// <summary>
    /// JS runtime injected for lazy drag-module loading. Routed through
    /// <see cref="PointerDragInterop.CreateAsync"/>; views do not call it directly.
    /// </summary>
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// Internal anchor used when the consumer does not supply <see cref="Date"/>.
    /// Exposed to the test project via <c>InternalsVisibleTo</c> so the uncontrolled-vs-
    /// controlled mode contract can be asserted directly.
    /// </summary>
    internal DateTimeOffset _internalDate;

    /// <summary>
    /// Bindable anchor date for this view. When <see langword="null"/>, the view manages
    /// its own anchor (uncontrolled mode) seeded from "today in
    /// <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>" per FR-09a.
    /// </summary>
    [Parameter]
    public DateTimeOffset? Date { get; set; }

    /// <summary>
    /// Fires whenever the anchor changes — both in controlled mode (so <c>@bind-Date</c>
    /// receives the new value) and in uncontrolled mode (so external observers can still
    /// react to navigation without binding).
    /// </summary>
    [Parameter]
    public EventCallback<DateTimeOffset> DateChanged { get; set; }

    /// <summary>
    /// The currently active anchor date. Reflects <see cref="Date"/> when the consumer
    /// supplies it, otherwise the internally-tracked anchor.
    /// </summary>
    protected DateTimeOffset CurrentDate => Date ?? _internalDate;

    /// <summary>
    /// "Today" expressed in <see cref="SchedulerComponentBase{TEvent}.ResolvedTimeZone"/>.
    /// Used to seed the uncontrolled anchor and to highlight today in the grid.
    /// </summary>
    protected DateTimeOffset Today => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolvedTimeZone);

    /// <summary>
    /// Seeds <see cref="_internalDate"/> with "today in the resolved zone" (issue #34)
    /// so uncontrolled renders have a valid anchor before <c>OnParametersSet</c> ever runs.
    /// </summary>
    /// <remarks>
    /// Defensive fallback: <see cref="SchedulerComponentBase{TEvent}.ResolveTimeZone"/>
    /// throws <see cref="InvalidOperationException"/> when none of the explicit
    /// <c>TimeZone</c> parameter, an ancestor <c>CascadingValue&lt;TimeZoneInfo&gt;</c>, or
    /// <see cref="Calee.Scheduler.Extensions.CaleeSchedulerOptions.DefaultTimeZone"/>
    /// supplied a value. Blazor sets parameters (including the cascade) before
    /// <c>OnInitialized</c> runs, so resolution normally succeeds here too — but rather
    /// than let an unresolved zone throw mid-seed (before the base's own
    /// <c>OnParametersSet</c> gets a chance to raise the same failure authoritatively),
    /// this catches the exception and falls back to <see cref="DateTimeOffset.UtcNow"/>
    /// as a non-throwing seed. The base's <c>OnParametersSet</c> validation still throws
    /// on the same parameter set, surfacing the developer bug exactly once.
    /// </remarks>
    protected override void OnInitialized()
    {
        TimeZoneInfo? tz;
        try
        {
            tz = ResolveTimeZone();
        }
        catch (InvalidOperationException)
        {
            tz = null;
        }

        _internalDate = tz is not null
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz)
            : DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Update the anchor date. Behavior depends on whether the consumer is controlling
    /// <see cref="Date"/>:
    /// <list type="bullet">
    ///   <item><description><strong>Controlled</strong> (<see cref="Date"/> has a value): fire
    ///     <see cref="DateChanged"/>; the consumer pushes a new <see cref="Date"/> in and
    ///     Blazor re-renders. <see cref="_internalDate"/> is intentionally not mutated.</description></item>
    ///   <item><description><strong>Uncontrolled</strong> (<see cref="Date"/> is <see langword="null"/>):
    ///     mutate <see cref="_internalDate"/>, fire <see cref="DateChanged"/> so external
    ///     observers can still react, then request a re-render via
    ///     <see cref="ComponentBase.StateHasChanged"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="newDate">The new anchor date.</param>
    protected async Task SetCurrentDateAsync(DateTimeOffset newDate)
    {
        if (Date.HasValue)
        {
            // Controlled: defer to the consumer's binding.
            await DateChanged.InvokeAsync(newDate);
            return;
        }

        // Uncontrolled: own the state, then notify and re-render.
        _internalDate = newDate;
        await DateChanged.InvokeAsync(newDate);
        StateHasChanged();
    }

    /// <summary>
    /// Begin a drag operation in response to a pointer-down on <paramref name="element"/>.
    /// For mouse pointers the drag starts immediately; for touch pointers the base
    /// waits 300ms for the press to be held without significant movement (per plan
    /// §5.1 #9) before starting. <paramref name="onDrop"/> fires exactly once when
    /// the user releases the pointer; <paramref name="onCancel"/> fires exactly
    /// once if Escape is pressed, the browser cancels the pointer, or the touch
    /// long-press is aborted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The JS drag module is lazy-loaded on first call. In test environments where
    /// no JS runtime is available the call no-ops (no exception, no callback).
    /// </para>
    /// <para>
    /// Per ADR-0015, the 300ms long-press timer + the move-tolerance check live
    /// entirely in JS — C# awaits one Promise rather than polling.
    /// </para>
    /// </remarks>
    /// <param name="args">The originating <see cref="PointerEventArgs"/>.</param>
    /// <param name="element">The element the user grabbed (drag-handle).</param>
    /// <param name="mode">The drag mode.</param>
    /// <param name="snapPixelsX">Horizontal snap granularity in pixels (0 disables horizontal snapping).</param>
    /// <param name="snapPixelsY">Vertical snap granularity in pixels (0 disables vertical snapping).</param>
    /// <param name="ghostClass">CSS class applied to the ghost element.</param>
    /// <param name="onDrop">Fired exactly once when the user releases the pointer.</param>
    /// <param name="onCancel">Fired exactly once on Escape / pointercancel / failed long-press.</param>
    /// <param name="resizeAxis">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.ResizeEnd"/> or
    /// <see cref="DragMode.CreateRegion"/>; ignored otherwise. Determines whether the
    /// ghost stretches horizontally (<see cref="ResizeAxis.X"/> — TimelineView) or
    /// vertically (<see cref="ResizeAxis.Y"/> — Day/Week views).
    /// </param>
    /// <param name="anchorViewportX">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.CreateRegion"/>;
    /// ignored otherwise. Viewport-pixel X of the pointer-down position. The JS ghost
    /// anchors here and grows toward the cursor. Read from <c>args.ClientX</c> at the
    /// call site.
    /// </param>
    /// <param name="anchorViewportY">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.CreateRegion"/>;
    /// ignored otherwise. Viewport-pixel Y of the pointer-down position. Read from
    /// <c>args.ClientY</c> at the call site.
    /// </param>
    /// <param name="thresholdPx">
    /// Optional. For <see cref="DragMode.CreateRegion"/> only — movement (in pixels)
    /// the user must traverse before a pointerup fires the drop handler; below the
    /// threshold the gesture is treated as a click on empty space and
    /// <paramref name="onCancel"/> fires while the slot's regular <c>@onclick</c>
    /// continues to drive <c>OnSlotClicked</c>. Defaults to 5 px.
    /// </param>
    /// <param name="crossAxisIndex">
    /// Optional. For <see cref="DragMode.CreateRegion"/> only. When supplied alongside
    /// <paramref name="crossAxisDivisions"/>, bounds the create-ghost to one slice of
    /// <paramref name="element"/>'s rect on the cross axis instead of spanning the
    /// element's full extent. Used by Week (anchor column index, divisions = 7) and
    /// Timeline (anchor row index, divisions = lane count) so the ghost stays inside
    /// the lane the user pressed in. Day view passes <see langword="null"/> and the
    /// ghost spans the element's full extent — correct because Day's grid is one
    /// column wide.
    /// </param>
    /// <param name="crossAxisDivisions">
    /// Optional. Required when <paramref name="crossAxisIndex"/> is non-null; must be
    /// &gt; 0. Total number of equal slices along the cross axis of
    /// <paramref name="element"/>.
    /// </param>
    /// <param name="highlightContainer">
    /// The grid container the drop-target highlight element is appended to (issue #13).
    /// Pass <see cref="ElementReference"/> of the hour-grid / time-area container.
    /// When omitted (default), no highlight is created.
    /// </param>
    /// <param name="highlightMode">
    /// The shape of the drop-target highlight (issue #13). <c>"slot-band"</c> for
    /// Day/Week views, <c>"lane-row"</c> for Timeline view, <c>"day-cell"</c> for
    /// Month view (deferred to issue #11). When <see langword="null"/>, no highlight.
    /// </param>
    /// <param name="eventDurationPixels">
    /// For move: the event's height (Day/Week) or width (Timeline) in pixels (issue #13).
    /// </param>
    /// <param name="eventDurationSlots">
    /// For move: the event's duration in slot-count (issue #13).
    /// </param>
    /// <param name="eventDurationDays">
    /// For Week/Timeline move: the event's duration in calendar days (issue #13).
    /// </param>
    /// <param name="columnCount">
    /// For Week view: the number of visible day columns (issue #13). Defaults to 1.
    /// </param>
    /// <param name="rowCount">
    /// For Timeline view: the number of lane rows (issue #13). Defaults to 1.
    /// </param>
    /// <param name="slotCount">
    /// For Day/Week/Timeline: the number of time slots (issue #13). Defaults to 0.
    /// </param>
    /// <remarks>
    /// Visibility is <see langword="private protected"/> because the parameter types
    /// (<see cref="DragMode"/>, <see cref="DropPayload"/>) are <see langword="internal"/>
    /// — only views in this assembly can derive from this base and call this hook,
    /// which is exactly the intended scope (PRD §4.7 / NFR-07).
    /// </remarks>
    private protected async Task BeginDragOnPointerAsync(
        PointerEventArgs args,
        ElementReference element,
        DragMode mode,
        double snapPixelsX,
        double snapPixelsY,
        string ghostClass,
        Func<DropPayload, Task> onDrop,
        Func<Task> onCancel,
        ResizeAxis? resizeAxis = null,
        double? anchorViewportX = null,
        double? anchorViewportY = null,
        int thresholdPx = 5,
        int? crossAxisIndex = null,
        int? crossAxisDivisions = null,
        ElementReference highlightContainer = default,
        string? highlightMode = null,
        double eventDurationPixels = 0,
        int eventDurationSlots = 0,
        int eventDurationDays = 0,
        int columnCount = 1,
        int rowCount = 1,
        int slotCount = 0)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(onDrop);
        ArgumentNullException.ThrowIfNull(onCancel);

        _drag ??= await PointerDragInterop.CreateAsync(JS);
        if (_drag is null)
        {
            // Test-environment / no-JS-runtime fallback: silently no-op.
            // Production prerender pass also lands here before JS interop is
            // wired; the user's gesture would be a pointerdown on a non-
            // hydrated element, which the browser doesn't fire anyway.
            return;
        }

        var isTouch = string.Equals(args.PointerType, "touch", StringComparison.OrdinalIgnoreCase);
        if (isTouch)
        {
            // Long-press gesture (plan §5.1 #9): 300ms hold + <= 5px movement.
            // Per ADR-0015 the timer + move-tracking are owned by JS — C# awaits
            // one Promise rather than polling pointer state mid-press.
            var held = await _drag.AwaitLongPressAsync(args.PointerId, 300, 5);
            if (!held)
            {
                await onCancel();
                return;
            }
        }

        await _drag.StartDragAsync(
            element, mode, snapPixelsX, snapPixelsY, ghostClass, onDrop, onCancel,
            resizeAxis,
            anchorViewportX: anchorViewportX,
            anchorViewportY: anchorViewportY,
            thresholdPx: thresholdPx,
            crossAxisIndex: crossAxisIndex,
            crossAxisDivisions: crossAxisDivisions,
            highlightContainer: highlightContainer,
            highlightMode: highlightMode,
            eventDurationPixels: eventDurationPixels,
            eventDurationSlots: eventDurationSlots,
            eventDurationDays: eventDurationDays,
            columnCount: columnCount,
            rowCount: rowCount,
            slotCount: slotCount);
    }

    /// <summary>
    /// Abort any in-progress drag without firing callbacks. Useful when the
    /// consumer's data updates during a drag and the optimistic-pin state is
    /// invalidated. No-op when no drag is active or when the JS module is not
    /// loaded. Visibility matches <see cref="BeginDragOnPointerAsync"/> — only
    /// derived views in this assembly call it.
    /// </summary>
    private protected Task AbortDragAsync() => _drag?.AbortDragAsync() ?? Task.CompletedTask;

    /// <summary>
    /// <see langword="true"/> while a JS-managed drag is in flight (between
    /// <c>BeginDragOnPointerAsync</c> and the eventual drop / cancel callback).
    /// Used by the views' keyboard handlers to defer Esc to the drag module's
    /// own cancel path (ADR-0006) — Esc-mid-drag aborts the drag; only Esc-
    /// without-an-active-drag should clear selection (Task 11 / FR-34).
    /// Returns <see langword="false"/> in test environments where the JS module
    /// could not be loaded (the lazy <c>_drag</c> stays null).
    /// </summary>
    private protected bool IsDragActive => _drag?.IsActive == true;

    /// <summary>
    /// Disposes the lazily-loaded drag-interop wrapper, if any. Subclasses
    /// overriding this MUST call <c>await base.DisposeAsync()</c> to release
    /// the JS module reference.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (_drag is not null)
        {
            await _drag.DisposeAsync();
            _drag = null;
        }
        GC.SuppressFinalize(this);
    }
}
