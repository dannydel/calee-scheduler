#nullable enable
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Internal C# wrapper around the JS pointer-drag module (ADR-0015). Each view
/// that supports drag operations instantiates one of these, calls
/// <see cref="StartDragAsync"/> to begin a drag, and registers callbacks via the
/// wrapper. The JS module owns visuals during the drag; C# only sees drop or
/// cancel (per ADR-0015 — JS owns mid-drag visuals so Blazor Server pays no
/// SignalR round-trip per pointermove).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: the wrapper owns the imported <see cref="IJSObjectReference"/>, the
/// <see cref="DotNetObjectReference{TValue}"/>, and any active drag handle. It
/// implements <see cref="IAsyncDisposable"/> so the host view (a one-per-view
/// instance, lifecycle-tied to the view component) can dispose it cleanly.
/// </para>
/// <para>
/// The class is <see langword="internal"/> — it's not part of the public API
/// surface (PRD §4.7 / NFR-07). Exposed to the test project via
/// <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
internal sealed class PointerDragInterop : IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private readonly DotNetObjectReference<PointerDragInterop> _dotnetRef;
    private string? _activeHandle;
    private Func<DropPayload, Task>? _onDrop;
    private Func<Task>? _onCancel;
    private bool _disposed;

    private PointerDragInterop(IJSObjectReference module)
    {
        _module = module;
        _dotnetRef = DotNetObjectReference.Create(this);
    }

    /// <summary>
    /// <see langword="true"/> while a drag is in flight (a <c>StartDragAsync</c> has
    /// returned and neither <see cref="OnDropAsync"/> nor <see cref="OnCancelAsync"/>
    /// has fired yet). Used by the views' keyboard handlers to suppress Esc-clears-
    /// selection while a drag is being cancelled — Esc-mid-drag belongs to the JS
    /// drag module (which calls <c>preventDefault</c> on its window-level listener
    /// and routes through <see cref="OnCancelAsync"/>); the C# keydown handler MUST
    /// NOT also fire its own Esc-clears-selection path or selection would be
    /// cleared as a side effect of an Esc the user pressed only to abort a drag.
    /// See ADR-0006 for the drag-cancel lifecycle.
    /// </summary>
    public bool IsActive => _activeHandle is not null;

    /// <summary>
    /// Construct + load the JS module. Returns <see langword="null"/> when the
    /// JS runtime is unavailable or the module fails to import (typical in test
    /// environments without a real DOM).
    /// </summary>
    /// <param name="js">The injected <see cref="IJSRuntime"/>.</param>
    /// <returns>
    /// A ready-to-use <see cref="PointerDragInterop"/>, or <see langword="null"/>
    /// when no JS runtime is available.
    /// </returns>
    public static async Task<PointerDragInterop?> CreateAsync(IJSRuntime js)
    {
        var module = await SchedulerViewPrimitives.TryLoadJsModuleAsync(js);
        return module is null ? null : new PointerDragInterop(module);
    }

    /// <summary>
    /// Begin a drag operation. The supplied callbacks fire exactly once: either
    /// <paramref name="onDrop"/> OR <paramref name="onCancel"/>. If a drag is
    /// already active when this is called, the older drag is aborted silently
    /// (no callbacks fired for it) and the new drag starts.
    /// </summary>
    /// <param name="elementRef">The element the user grabbed (drag-handle).</param>
    /// <param name="mode">The drag mode (move / resize-end / create).</param>
    /// <param name="snapPixelsX">Horizontal snap granularity in pixels; 0 disables horizontal snapping.</param>
    /// <param name="snapPixelsY">Vertical snap granularity in pixels; 0 disables vertical snapping.</param>
    /// <param name="ghostClass">CSS class applied to the ghost element. Defaults to the library's ghost class when null/empty.</param>
    /// <param name="onDrop">Fired when the user releases the pointer; receives the final drop payload.</param>
    /// <param name="onCancel">Fired when the user presses Escape or the browser cancels the drag.</param>
    /// <param name="resizeAxis">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.ResizeEnd"/> or
    /// <see cref="DragMode.CreateRegion"/>; ignored otherwise. <see cref="ResizeAxis.Y"/> =
    /// vertical-time views (Day/Week — the rectangle grows along Y);
    /// <see cref="ResizeAxis.X"/> = horizontal-time views (TimelineView — the rectangle
    /// grows along X).
    /// </param>
    /// <param name="anchorViewportX">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.CreateRegion"/>;
    /// ignored otherwise. Viewport-pixel X of the pointer-down position — the JS ghost
    /// anchors here and grows toward the cursor.
    /// </param>
    /// <param name="anchorViewportY">
    /// Required when <paramref name="mode"/> is <see cref="DragMode.CreateRegion"/>;
    /// ignored otherwise. Viewport-pixel Y of the pointer-down position.
    /// </param>
    /// <param name="thresholdPx">
    /// Optional. For <see cref="DragMode.CreateRegion"/> only — movement (in pixels)
    /// required before pointerup fires <c>onDrop</c> rather than <c>onCancel</c>. Below
    /// this threshold the gesture falls through to a regular slot click (FR-21). Defaults
    /// to 5 px, matching the touch-long-press move tolerance.
    /// </param>
    /// <param name="crossAxisIndex">
    /// Optional. For <see cref="DragMode.CreateRegion"/> only — when supplied alongside
    /// <paramref name="crossAxisDivisions"/>, bounds the create ghost to one slice of
    /// <paramref name="elementRef"/>'s rect on the cross axis instead of spanning the
    /// full element. Index 0 is leftmost (axis=Y) or topmost (axis=X). Used by Week
    /// (anchor column index out of 7) and Timeline (anchor row index out of N) so the
    /// ghost stays inside the lane the user pressed in even when <paramref name="elementRef"/>
    /// is the whole grid. Day view passes <see langword="null"/> and the ghost spans
    /// the element's full extent — correct because Day's grid is one column wide.
    /// </param>
    /// <param name="crossAxisDivisions">
    /// Optional. Required when <paramref name="crossAxisIndex"/> is non-null; must be
    /// &gt; 0. Total number of equal slices along the cross axis. The ghost's cross-axis
    /// size becomes <c>elementRect.crossAxis / crossAxisDivisions</c>.
    /// </param>
    public async Task StartDragAsync(
        ElementReference elementRef,
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
        int? crossAxisDivisions = null)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PointerDragInterop));
        }
        ArgumentNullException.ThrowIfNull(onDrop);
        ArgumentNullException.ThrowIfNull(onCancel);
        if (mode == DragMode.ResizeEnd && resizeAxis is null)
        {
            throw new ArgumentException(
                "ResizeAxis is required when mode == DragMode.ResizeEnd.",
                nameof(resizeAxis));
        }
        if (mode == DragMode.CreateRegion)
        {
            if (resizeAxis is null)
            {
                throw new ArgumentException(
                    "ResizeAxis is required when mode == DragMode.CreateRegion (carries the ghost's growth axis).",
                    nameof(resizeAxis));
            }
            if (anchorViewportX is null || anchorViewportY is null)
            {
                throw new ArgumentException(
                    "anchorViewportX + anchorViewportY are required when mode == DragMode.CreateRegion.",
                    nameof(anchorViewportX));
            }
        }

        // If a drag is already active, abort it first — no callbacks fired.
        if (_activeHandle is not null)
        {
            await _module.InvokeVoidAsync("abortDrag", _activeHandle);
            _activeHandle = null;
            _onDrop = null;
            _onCancel = null;
        }

        _onDrop = onDrop;
        _onCancel = onCancel;

        // Emit only the options the chosen mode needs so the JS-side validator can
        // assert presence/absence cleanly. The JS module rejects unknown modes and
        // missing required options.
        if (mode == DragMode.ResizeEnd)
        {
            _activeHandle = await _module.InvokeAsync<string>("startDrag", elementRef, new
            {
                mode = ModeToString(mode),
                axis = AxisToString(resizeAxis!.Value),
                dotnetRef = _dotnetRef,
                onDropMethodName = nameof(OnDropAsync),
                onCancelMethodName = nameof(OnCancelAsync),
                snapPixelsX,
                snapPixelsY,
                ghostClass,
            });
        }
        else if (mode == DragMode.CreateRegion)
        {
            // crossAxisIndex + crossAxisDivisions are an optional pair — both null
            // means the JS uses elementRef's full cross-axis extent (Day view);
            // both set means it slices that extent into N parts and uses the i-th
            // slice (Week / Timeline lane-bounded ghost).
            _activeHandle = await _module.InvokeAsync<string>("startDrag", elementRef, new
            {
                mode = ModeToString(mode),
                axis = AxisToString(resizeAxis!.Value),
                anchorX = anchorViewportX!.Value,
                anchorY = anchorViewportY!.Value,
                thresholdPx,
                crossAxisIndex,
                crossAxisDivisions,
                dotnetRef = _dotnetRef,
                onDropMethodName = nameof(OnDropAsync),
                onCancelMethodName = nameof(OnCancelAsync),
                snapPixelsX,
                snapPixelsY,
                ghostClass,
            });
        }
        else
        {
            _activeHandle = await _module.InvokeAsync<string>("startDrag", elementRef, new
            {
                mode = ModeToString(mode),
                dotnetRef = _dotnetRef,
                onDropMethodName = nameof(OnDropAsync),
                onCancelMethodName = nameof(OnCancelAsync),
                snapPixelsX,
                snapPixelsY,
                ghostClass,
            });
        }
    }

    /// <summary>
    /// Wait for a touch pointer to be held without significant movement for the
    /// supplied duration. Delegates to the JS module's <c>awaitLongPress</c>
    /// helper (plan §5.1 #9); the timer + move-tracking live entirely in JS so
    /// C# isn't woken at 60Hz to poll pointer state.
    /// </summary>
    /// <param name="pointerId">The pointerId from the originating pointerdown event.</param>
    /// <param name="durationMs">How long the press must be held before resolving true (e.g., 300).</param>
    /// <param name="moveTolerancePx">Maximum movement (in pixels) tolerated before aborting.</param>
    /// <returns>
    /// <see langword="true"/> when the press is held for the full duration;
    /// <see langword="false"/> when the user releases early, moves past the
    /// tolerance, or the browser cancels the pointer.
    /// </returns>
    internal async Task<bool> AwaitLongPressAsync(long pointerId, int durationMs, int moveTolerancePx)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PointerDragInterop));
        }
        return await _module.InvokeAsync<bool>("awaitLongPress", pointerId, durationMs, moveTolerancePx);
    }

    /// <summary>
    /// Programmatically abort an in-progress drag. No callback fires — the
    /// caller initiated the abort and already knows. Idempotent: calling when
    /// no drag is active is a no-op.
    /// </summary>
    public async Task AbortDragAsync()
    {
        if (_disposed || _activeHandle is null)
        {
            return;
        }

        var handleToAbort = _activeHandle;
        _activeHandle = null;
        _onDrop = null;
        _onCancel = null;
        try
        {
            await _module.InvokeVoidAsync("abortDrag", handleToAbort);
        }
        catch (JSDisconnectedException) { /* Circuit gone; nothing to clean up. */ }
        catch (JSException ex) { Debug.WriteLine($"[PointerDragInterop] AbortDragAsync swallowed JSException: {ex.Message}"); }
    }

    /// <summary>
    /// JS-invokable drop hook. Public visibility is required by Blazor's
    /// interop discovery; intended only for the JS module to call.
    /// </summary>
    /// <param name="payload">The final drop coordinates + drag mode.</param>
    [JSInvokable]
    public async Task OnDropAsync(DropPayload payload)
    {
        // Capture + clear state BEFORE invoking the user's handler so the handler
        // can start a fresh drag (or call AbortDragAsync) without state confusion.
        var onDrop = _onDrop;
        _activeHandle = null;
        _onDrop = null;
        _onCancel = null;
        if (onDrop is not null)
        {
            await onDrop(payload);
        }
    }

    /// <summary>
    /// JS-invokable cancel hook. Public visibility is required by Blazor's
    /// interop discovery; intended only for the JS module to call.
    /// </summary>
    [JSInvokable]
    public async Task OnCancelAsync()
    {
        var onCancel = _onCancel;
        _activeHandle = null;
        _onDrop = null;
        _onCancel = null;
        if (onCancel is not null)
        {
            await onCancel();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_activeHandle is not null)
        {
            try { await _module.InvokeVoidAsync("abortDrag", _activeHandle); }
            catch (JSDisconnectedException) { /* Circuit gone; nothing to clean up. */ }
            catch (JSException ex) { Debug.WriteLine($"[PointerDragInterop] DisposeAsync abort swallowed JSException: {ex.Message}"); }
            _activeHandle = null;
        }

        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { /* Circuit gone; nothing to clean up. */ }
        catch (JSException ex) { Debug.WriteLine($"[PointerDragInterop] DisposeAsync module dispose swallowed JSException: {ex.Message}"); }

        _dotnetRef.Dispose();
    }

    private static string ModeToString(DragMode mode) => mode switch
    {
        DragMode.Move => "move",
        DragMode.ResizeEnd => "resize-end",
        DragMode.CreateRegion => "create-region",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DragMode."),
    };

    private static string AxisToString(ResizeAxis axis) => axis switch
    {
        ResizeAxis.X => "x",
        ResizeAxis.Y => "y",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown ResizeAxis."),
    };
}

/// <summary>
/// The kind of drag operation in progress. Affects the cursor cue rendered by
/// the JS module and is echoed in the <see cref="DropPayload.Mode"/> field so
/// the consumer's drop handler can branch on it.
/// </summary>
internal enum DragMode
{
    /// <summary>The user is moving an existing event.</summary>
    Move,

    /// <summary>
    /// The user is resizing an existing event by dragging its trailing edge —
    /// the bottom edge in Day/Week views (vertical time), the right edge in
    /// TimelineView (horizontal time). The axis is supplied separately via
    /// <see cref="ResizeAxis"/>; this enum is intentionally axis-agnostic so the
    /// data-* attribute and the C# call site agree (FR-26).
    /// </summary>
    ResizeEnd,

    /// <summary>
    /// The user is creating a new event by drag-selecting a time range on empty
    /// grid space (FR-24). The ghost is a fresh rectangle anchored at the
    /// pointer-down position; the C# wrapper must supply the viewport anchor +
    /// the growth <see cref="ResizeAxis"/> (Y = Day/Week vertical-time, X =
    /// TimelineView horizontal-time). Below the movement threshold the gesture
    /// falls through to the slot's regular click handler — no
    /// <c>OnEventCreated</c> fires.
    /// </summary>
    CreateRegion,
}

/// <summary>
/// Which axis the resize-end ghost grows/shrinks along. Required when
/// <see cref="DragMode"/> is <see cref="DragMode.ResizeEnd"/>; ignored otherwise.
/// </summary>
internal enum ResizeAxis
{
    /// <summary>Horizontal-time views (TimelineView). Right edge moves; left is anchored.</summary>
    X,

    /// <summary>Vertical-time views (Day/Week). Bottom edge moves; top is anchored.</summary>
    Y,
}

/// <summary>
/// The payload delivered to C# at drop. Coordinates are in viewport pixels; the
/// engine's inverse-mapping helper (added in Task 3) converts these to
/// (date, time, lane). The deltas have snap-on-drop applied; mid-drag the
/// ghost follows the cursor 1:1 per plan §5.1 #4.
/// </summary>
/// <param name="FinalLeftPx">The ghost's final left position (viewport pixels).</param>
/// <param name="FinalTopPx">The ghost's final top position (viewport pixels).</param>
/// <param name="DeltaXPx">Total horizontal movement from drag start, snap-applied.</param>
/// <param name="DeltaYPx">Total vertical movement from drag start, snap-applied.</param>
/// <param name="Mode">Echoes the <see cref="DragMode"/> the drag was started with.</param>
internal sealed record DropPayload(
    double FinalLeftPx,
    double FinalTopPx,
    double DeltaXPx,
    double DeltaYPx,
    string Mode);
