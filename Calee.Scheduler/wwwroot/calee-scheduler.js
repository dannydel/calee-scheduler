// Calee.Scheduler — JS helpers used by every view.
//
// The original Phase 1 surface (scroll-into-view + blur) is preserved unchanged
// for the read-only views (FR-09b, FR-30 Escape behavior). Phase 2 (Task 2)
// extends this module with a pointer-events drag implementation that owns
// visual feedback locally during a drag and calls back into C# only at the
// drop/cancel instant — see ADR-0015. No mid-drag SignalR traffic.

/**
 * Scroll the supplied element so that the supplied pixel offset is roughly
 * centered in its viewport. If the element or offset are unavailable, no-ops.
 *
 * @param {HTMLElement | null} container the scrollable element
 * @param {number} pixelOffset offset (in pixels from container top) to center
 */
export function scrollToOffset(container, pixelOffset) {
    if (!container) return;
    const halfHeight = container.clientHeight / 2;
    const target = Math.max(0, pixelOffset - halfHeight);
    container.scrollTop = target;
}

/**
 * Scroll the supplied element to the supplied pixel offset (top-aligned).
 *
 * @param {HTMLElement | null} container the scrollable element
 * @param {number} pixelOffset offset (in pixels from container top)
 */
export function scrollToTop(container, pixelOffset) {
    if (!container) return;
    container.scrollTop = Math.max(0, pixelOffset);
}

/**
 * Scroll the supplied container so that the supplied hour-fraction (e.g. 14.5
 * for 2:30 PM) is roughly centered. The pixels-per-hour value is read at
 * runtime from the computed `--calee-scheduler-pixels-per-hour` custom property
 * so consumer overrides take effect.
 *
 * @param {HTMLElement | null} container the scrollable element
 * @param {number} hourOffsetFromTop hours from the grid's top edge (typically targetHour - StartHour)
 */
export function scrollToHour(container, hourOffsetFromTop) {
    if (!container) return;
    const raw = getComputedStyle(container)
        .getPropertyValue('--calee-scheduler-pixels-per-hour')
        .trim();
    const pxPerHour = parseFloat(raw) || 56;
    const targetPx = Math.max(0, hourOffsetFromTop) * pxPerHour;
    const halfHeight = container.clientHeight / 2;
    container.scrollTop = Math.max(0, targetPx - halfHeight);
}

/**
 * Horizontal analog of `scrollToHour` for TimelineView (TimeScale=Day): scroll
 * the supplied container so that the supplied hour-fraction is roughly centered
 * along the X axis. The pixels-per-hour value is read from the same
 * `--calee-scheduler-pixels-per-hour` custom property as the vertical helper —
 * TimelineView intentionally reuses the variable so consumers override one knob
 * to retune density across every time-grid view.
 *
 * @param {HTMLElement | null} container the horizontally-scrollable element
 * @param {number} hourOffsetFromLeft hours from the grid's left edge (typically targetHour - StartHour)
 */
export function scrollToHourHorizontal(container, hourOffsetFromLeft) {
    if (!container) return;
    const raw = getComputedStyle(container)
        .getPropertyValue('--calee-scheduler-pixels-per-hour')
        .trim();
    const pxPerHour = parseFloat(raw) || 56;
    const targetPx = Math.max(0, hourOffsetFromLeft) * pxPerHour;
    const halfWidth = container.clientWidth / 2;
    container.scrollLeft = Math.max(0, targetPx - halfWidth);
}

/**
 * Move focus to the supplied element (a no-op if null).
 *
 * @param {HTMLElement | null} element the element to focus
 */
export function focusElement(element) {
    if (element && typeof element.focus === 'function') {
        element.focus();
    }
}

/**
 * Remove focus from the currently-focused element.
 */
export function blurActive() {
    if (document.activeElement && typeof document.activeElement.blur === 'function') {
        document.activeElement.blur();
    }
}

/**
 * Move real browser focus to a grid/list's currently-tabbable roving-tabindex cell
 * (issue #19). Arrow-key navigation swaps which cell carries `tabindex="0"`, but
 * before this fix no view ever called `.focus()` on the newly-active cell —
 * `document.activeElement` stayed on the previously-focused node even though the
 * attribute state had moved on, so keyboard/screen-reader users saw no focus move.
 *
 * Queries `container` for the roving cell using the two shapes the library's views
 * emit it in: `[role="gridcell"][tabindex="0"]` (Day/Week/Month/Year/Timeline slot
 * and day cells) or `[role="listitem"][tabindex="0"]` (Agenda's list-pattern rows).
 * Both the role AND the tabindex attribute must land on the *same* element for the
 * match to count — this is what excludes event chips: they are always
 * independently focusable (`tabindex="0"` hard-coded, not roving) and sometimes sit
 * inside a `role="gridcell"` wrapper of their own, but that wrapper carries no
 * tabindex and the inner focusable button carries no `role="gridcell"`, so an event
 * chip can never satisfy both conditions on one node and is never mistaken for the
 * roving cell.
 *
 * No-ops (does not throw) when `container` is null or no matching cell is found —
 * both are expected before first render and when a view has no cells yet.
 *
 * @param {HTMLElement | null} container the grid/list wrapper to search within
 */
export function focusActiveGridCell(container) {
    if (!container) return;
    const cell = container.querySelector(
        '[role="gridcell"][tabindex="0"], [role="listitem"][tabindex="0"]');
    if (cell && typeof cell.focus === 'function') {
        cell.focus({ preventScroll: false });
    }
}

/**
 * Remove focus from the active element ONLY when it is an event chip
 * (i.e., carries `data-calee-region="event"`). Called by slot-click
 * handlers so that clicking the empty grid drops the chip's focus ring
 * without also clobbering the slot's roving-tabindex position when the
 * clicked slot was itself the focused element. The conditional check
 * lives in JS because the C# side doesn't track which DOM element
 * currently holds focus.
 */
export function blurActiveIfEvent() {
    const el = document.activeElement;
    if (el && el.getAttribute && el.getAttribute('data-calee-region') === 'event'
        && typeof el.blur === 'function') {
        el.blur();
    }
}

/**
 * Return the bounding-rect height of the supplied element (in CSS pixels), or 0
 * when the element is null/undefined. Used by drag-to-move implementations that
 * need the hour-grid's physical pixel height to inverse-map a drop Y offset to a
 * time via `EventLayoutEngine.InverseY`. Reading the height in JS avoids
 * threading a Blazor-cascading pixel-density parameter through every view.
 *
 * @param {HTMLElement | null} element
 * @returns {number}
 */
export function getElementHeight(element) {
    if (!element) return 0;
    return element.getBoundingClientRect().height;
}

/**
 * Return the bounding-rect width of the supplied element (in CSS pixels), or 0
 * when the element is null/undefined. Horizontal counterpart to
 * {@link getElementHeight}; will be used by Timeline-view drag in Phase 2 Task 6
 * for InverseX hit-testing of cross-day drops.
 *
 * @param {HTMLElement | null} element
 * @returns {number}
 */
export function getElementWidth(element) {
    if (!element) return 0;
    return element.getBoundingClientRect().width;
}

// ---------------------------------------------------------------------------
// Day-header key guard (issue #9).
//
// An interactive day header (role="button", tabindex="0") is a <div>, not a
// real <button> — so unlike the library's other keyboard-activated controls,
// nothing suppresses the browser's global "Space scrolls the viewport"
// default for it. Blazor's `@onkeydown:preventDefault` directive is
// element-wide (it can't distinguish Space from Tab), and blanket-preventing
// every keydown on the header would also swallow Tab's default focus-move —
// a keyboard trap. So the guard lives here instead, scoped to exactly one key
// and one element shape: only Space, only when the event target is (or is
// nested inside) a day-header cell currently rendered as an interactive
// button. Every other key — including Tab — passes through untouched.
//
// Defense-in-depth: DayHeaderTemplate is documented as "don't nest interactive
// controls" (README §4.8 / ADR-0002), but a consumer could still nest an
// editable element (an <input>, <textarea>, a <select>, or a
// contenteditable node) for some other reason. Space typed into one of those
// is text entry, not button activation, so the guard bails out before the
// closest() check whenever the event target itself is editable — regardless
// of whether it also happens to sit inside a day-header cell.
// ---------------------------------------------------------------------------

/** Active day-header key guards keyed by handle (mirrors _activeDrags' shape). */
const _dayHeaderKeyGuards = new Map();

/**
 * Register a window-level keydown listener that calls `preventDefault()` on
 * the Space key ONLY when it targets an interactive day-header cell
 * (`[data-calee-region="day-header"][role="button"]`, matched via
 * `closest()` so a click that lands on injected `DayHeaderTemplate` content
 * still counts) — and never when the target itself is an editable element
 * (input/textarea/select/contenteditable), so typing a space into a nested
 * text field is never suppressed. Each view instance that wires
 * `OnDayHeaderClicked` calls this once (typically from its first render) and
 * calls `unregisterDayHeaderKeyGuard` with the returned handle on dispose —
 * mirroring `PointerDragInterop`'s register/dispose lifecycle.
 *
 * @returns {string} an opaque handle for `unregisterDayHeaderKeyGuard`.
 */
export function registerDayHeaderKeyGuard() {
    const handler = (ev) => {
        if (ev.code !== 'Space') return;
        const target = ev.target;
        if (target && typeof target.matches === 'function'
            && target.matches('input, textarea, select, [contenteditable=""], [contenteditable="true"]')) {
            return;
        }
        if (target && typeof target.closest === 'function'
            && target.closest('[data-calee-region="day-header"][role="button"]')) {
            ev.preventDefault();
        }
    };
    window.addEventListener('keydown', handler);
    const handle = _newHandle();
    _dayHeaderKeyGuards.set(handle, handler);
    return handle;
}

/**
 * Remove a listener previously registered by `registerDayHeaderKeyGuard`.
 * No-ops silently for an unknown/already-removed handle (defensive — dispose
 * paths may race with a component that never finished registering).
 *
 * @param {string} handle
 */
export function unregisterDayHeaderKeyGuard(handle) {
    const handler = _dayHeaderKeyGuards.get(handle);
    if (!handler) return;
    window.removeEventListener('keydown', handler);
    _dayHeaderKeyGuards.delete(handle);
}

// ---------------------------------------------------------------------------
// Pointer-events drag module (Phase 2, Task 2 — ADR-0015).
//
// Design constraints captured in ADR-0015 + plan §5.1:
//   - JS owns visuals during a drag (ghost element CSS transform, cursor cues).
//   - C# is called only at drop or cancel — never mid-drag — so Blazor Server
//     does not pay a SignalR round-trip per pointermove.
//   - Snap-on-drop only (plan §5.1 #4); ghost follows the cursor 1:1 mid-drag.
//   - Esc + pointercancel both fire onCancel (plan §5.1 #8).
//   - One drag at a time per module load; starting a new drag aborts any older
//     one silently (no callbacks fired for the aborted drag).
//
// Touch input flows through pointer events automatically (pointerType==='touch');
// the long-press-to-drag gesture (plan §5.1 #9) is the *caller's* responsibility —
// startDrag assumes the caller has decided drag has begun.
// ---------------------------------------------------------------------------

/**
 * Active drag sessions keyed by handle. A single drag at a time is the common
 * case, but a map keeps the abort-and-replace semantics simple to reason about.
 *
 * Each entry shape:
 *   {
 *     ghost: HTMLElement,            // the absolutely-positioned clone shown during drag
 *     startX, startY: number,        // pointer client coords at drag start
 *     ghostStartLeft, ghostStartTop, // ghost's initial top/left relative to offsetParent
 *     snapPixelsX, snapPixelsY,      // snap granularity (0 disables that axis)
 *     mode: string,                  // echoed to C# in the drop payload
 *     dotnetRef: DotNetObjectReference,
 *     onDropMethodName: string,
 *     onCancelMethodName: string,
 *     pointerId: number,
 *     priorBodyCursor: string,       // restored at cleanup
 *     listeners: Array<{target, type, fn}>, // every listener registered, for cleanup
 *   }
 */
const _activeDrags = new Map();

/** Generate a uuid-ish handle. Not cryptographic; only needs uniqueness within the page. */
function _newHandle() {
    return 'd-' + Math.random().toString(36).slice(2) + '-' + Date.now().toString(36);
}

/** Snap a value to the nearest multiple of `step`. step<=0 disables snapping. */
function _snap(value, step) {
    if (!step || step <= 0) return value;
    return Math.round(value / step) * step;
}

/** Tear down a drag session by handle. Does NOT fire any C# callback. */
/**
 * Swallow the next click event the browser dispatches at the window level.
 * Called from the drag's pointerup handler when a real drag completes (drop
 * fires) so the synthesized click that the browser emits after pointerup
 * doesn't reach the chip's @onclick / the slot's @onclick — without this,
 * a drag-to-move would also fire OnEventClicked (which the demo's edit
 * dialog hangs off), and a drag-to-create would also fire OnSlotClicked.
 *
 * Capture-phase so it runs before any in-component bubble-phase listener.
 * Self-clears on the first click. A zero-delay setTimeout removes the
 * listener if no click ever fires (most browsers suppress synthesized
 * clicks past a movement threshold themselves, so this is the common path)
 * — without the timeout the listener would lurk and swallow a later
 * unrelated user click.
 */
function _suppressNextClick() {
    const handler = (ev) => {
        ev.stopPropagation();
        ev.preventDefault();
        window.removeEventListener('click', handler, true);
    };
    window.addEventListener('click', handler, true);
    setTimeout(() => {
        window.removeEventListener('click', handler, true);
    }, 0);
}

function _cleanup(handle) {
    const state = _activeDrags.get(handle);
    if (!state) return;
    _activeDrags.delete(handle);

    // Remove every event listener we registered.
    for (const { target, type, fn } of state.listeners) {
        try { target.removeEventListener(type, fn); } catch { /* best-effort */ }
    }

    // Release pointer capture if we still hold it.
    if (state.captureTarget && typeof state.captureTarget.releasePointerCapture === 'function') {
        try { state.captureTarget.releasePointerCapture(state.pointerId); } catch { /* best-effort */ }
    }

    // Remove the ghost.
    if (state.ghost && state.ghost.parentNode) {
        state.ghost.parentNode.removeChild(state.ghost);
    }

    // Remove the highlight (issue #13).
    if (state.highlight && state.highlight.parentNode) {
        state.highlight.parentNode.removeChild(state.highlight);
    }

    // Restore the body cursor.
    document.body.style.cursor = state.priorBodyCursor ?? '';
}

/**
 * Update the drop-target highlight for slot-band mode (Day/Week views).
 * The highlight occupies one column and spans eventDurationPixels vertically,
 * anchored at the snapped (column, slot) position within the grid rect.
 * Issue #13 — no Blazor round-trip, driven from the existing pointermove path.
 *
 * @param {object} state The drag state.
 * @param {number} dxSnapped Snapped horizontal delta in pixels.
 * @param {number} dySnapped Snapped vertical delta in pixels.
 * @param {DOMRect} originalRect The source element's bounding rect at drag start.
 */
function _updateSlotBandHighlight(state, dxSnapped, dySnapped, originalRect) {
    const { highlight, eventDurationPixels, eventDurationSlots, eventDurationDays, columnCount, slotCount } = state;
    if (!highlight) return;

    // Defensive guards: bail out if geometry is invalid (prevents NaN positions).
    if (columnCount <= 0 || slotCount <= 0) return;

    // The source event chip is always one column wide (Day view = single column,
    // Week view = one day column). Derive slot height from the already-correct
    // event duration values rather than dividing the chip's height by slotCount
    // (which would be wrong — the chip spans multiple slots, not the whole grid).
    const columnWidth = originalRect.width;
    if (columnWidth <= 0) return;

    const slotHeight = eventDurationSlots > 0
        ? eventDurationPixels / eventDurationSlots
        : 0;

    const targetColumn = Math.max(0, Math.min(columnCount - 1,
        Math.floor(dxSnapped / columnWidth)));
    const targetSlot = slotHeight > 0
        ? Math.max(0, Math.min(slotCount - 1, Math.floor(dySnapped / slotHeight)))
        : 0;

    // Multi-day events span multiple columns (Week view). Single-day events
    // (eventDurationDays <= 1 or Day view) span one column.
    const highlightWidth = eventDurationDays > 1
        ? columnWidth * eventDurationDays
        : columnWidth;

    highlight.style.left = (targetColumn * columnWidth) + 'px';
    highlight.style.top = (targetSlot * slotHeight) + 'px';
    highlight.style.width = highlightWidth + 'px';
    highlight.style.height = eventDurationPixels + 'px';
}

/**
 * Update the drop-target highlight for lane-row mode (Timeline view).
 * The highlight spans eventDurationPixels horizontally at the snapped
 * (time-position, lane-row) position within the grid rect.
 * Issue #13 — no Blazor round-trip, driven from the existing pointermove path.
 *
 * @param {object} state The drag state.
 * @param {number} dxSnapped Snapped horizontal delta in pixels.
 * @param {number} dySnapped Snapped vertical delta in pixels.
 * @param {DOMRect} originalRect The source element's bounding rect at drag start.
 */
function _updateLaneRowHighlight(state, dxSnapped, dySnapped, originalRect) {
    const { highlight, eventDurationPixels, rowCount } = state;
    if (!highlight) return;

    // Defensive guards: bail out if geometry is invalid (prevents NaN positions).
    if (rowCount <= 0) return;

    // The source event chip is always one lane row tall in Timeline view.
    // Derive row height directly from the chip's height (not divided by rowCount).
    const rowHeight = originalRect.height;
    if (rowHeight <= 0) return;

    const targetRow = Math.max(0, Math.min(rowCount - 1,
        Math.floor(dySnapped / rowHeight)));

    highlight.style.left = dxSnapped + 'px';
    highlight.style.top = (targetRow * rowHeight) + 'px';
    highlight.style.width = eventDurationPixels + 'px';
    highlight.style.height = rowHeight + 'px';
}

/**
 * Update the drop-target highlight for day-cell mode (Month view).
 * Deferred to issue #11 (Month view drag-to-move); no-ops in the meantime.
 *
 * @param {object} state The drag state.
 * @param {number} dxSnapped Snapped horizontal delta in pixels.
 * @param {number} dySnapped Snapped vertical delta in pixels.
 * @param {DOMRect} originalRect The source element's bounding rect at drag start.
 */
function _updateDayCellHighlight(state, dxSnapped, dySnapped, originalRect) {
    // Deferred to issue #11 (Month view drag-to-move).
}

/**
 * Begin a drag operation. Owns the visual feedback locally (ghost element
 * positioning, cursor cues). Calls C# only at drop or cancel — never mid-drag.
 *
 * @param {HTMLElement} elementRef The element/handle the user grabbed (used to
 *     build the ghost and as the pointer-capture target).
 * @param {object} options Drag configuration.
 * @param {'move'|'resize-end'|'create-region'} options.mode Move = the ghost
 *     translates to follow the cursor; resize-end = the ghost stretches along one
 *     edge anchored at the opposite edge (the End of the event moves; the Start
 *     does not); create-region = a fresh ghost rectangle anchored at the
 *     pointer-down position grows toward the cursor along one axis (used by
 *     drag-to-create on empty grid space — FR-24).
 * @param {'x'|'y'} [options.axis] For mode='resize-end' or mode='create-region'.
 *     Required for both; ignored for other modes. 'y' = Day/Week views (vertical
 *     time, the rectangle grows along the Y axis); 'x' = TimelineView (horizontal
 *     time, the rectangle grows along the X axis).
 * @param {number} [options.anchorX] For mode='create-region' only. Required for
 *     create-region. Viewport-pixel X coordinate of the pointer-down position;
 *     the ghost rectangle is anchored here and grows toward the cursor.
 * @param {number} [options.anchorY] For mode='create-region' only. Required for
 *     create-region. Viewport-pixel Y coordinate of the pointer-down position.
 * @param {number} [options.thresholdPx] For mode='create-region' only. Movement
 *     (in pixels) required before drag is considered "started" — below this
 *     threshold a pointerup fires onCancel rather than onDrop, falling through
 *     to the normal click handler. Defaults to 5 (matches awaitLongPress).
 * @param {number} [options.crossAxisIndex] For mode='create-region' only.
 *     When supplied alongside `crossAxisDivisions`, the ghost is bounded to one
 *     slice of `elementRef`'s rect on the cross axis instead of spanning the
 *     element's full extent. Index 0 is the leftmost / topmost slice. Used by
 *     Week (column-bound ghost in a 7-column grid) and Timeline (row-bound
 *     ghost in an N-row grid) to keep the create-region rectangle inside the
 *     lane the user clicked on, even when `elementRef` is the whole grid.
 * @param {number} [options.crossAxisDivisions] For mode='create-region' only.
 *     Total number of equal slices along the cross axis of `elementRef`. Must
 *     be > 0 when `crossAxisIndex` is supplied; ignored otherwise. Day view
 *     omits both and gets the default "ghost spans element's full cross axis"
 *     behavior, which is correct because its element is already one column.
 * @param {object} options.dotnetRef DotNetObjectReference for drop/cancel callbacks.
 * @param {string} options.onDropMethodName e.g. "OnDropAsync".
 * @param {string} options.onCancelMethodName e.g. "OnCancelAsync".
 * @param {number} options.snapPixelsX Horizontal snap granularity in pixels (0 = no snap).
 * @param {number} options.snapPixelsY Vertical snap granularity in pixels (0 = no snap).
 * @param {string} options.ghostClass CSS class applied to the ghost element.
 * @param {HTMLElement} [options.highlightContainer] The grid container to append the highlight to (issue #13).
 * @param {'slot-band'|'lane-row'|'day-cell'} [options.highlightMode] The shape of the highlight (issue #13).
 * @param {number} [options.eventDurationPixels] For move: the event's height/width in pixels (issue #13).
 * @param {number} [options.eventDurationSlots] For move: the event's duration in slots (issue #13).
 * @param {number} [options.eventDurationDays] For Week/Timeline move: the event's duration in days (issue #13).
 * @param {number} [options.columnCount] For Week view: the number of visible day columns (issue #13).
 * @param {number} [options.rowCount] For Timeline view: the number of lane rows (issue #13).
 * @param {number} [options.slotCount] For Day/Week/Timeline: the number of time slots (issue #13).
 * @returns {string} A handle the C# side stores to call abortDrag.
 */
export function startDrag(elementRef, options) {
    // Input validation — development-only sanity, no production guard.
    if (!elementRef || typeof elementRef.getBoundingClientRect !== 'function') {
        throw new Error('startDrag: elementRef must be an HTMLElement.');
    }
    if (!options || typeof options !== 'object') {
        throw new Error('startDrag: options object is required.');
    }
    const { mode, axis, anchorX, anchorY, thresholdPx,
            crossAxisIndex, crossAxisDivisions,
            dotnetRef, onDropMethodName, onCancelMethodName,
            snapPixelsX, snapPixelsY, ghostClass } = options;
    const highlightContainer = options.highlightContainer || null;
    const highlightMode = options.highlightMode || null;
    const eventDurationPixels = options.eventDurationPixels || 0;
    const eventDurationSlots = options.eventDurationSlots || 0;
    const eventDurationDays = options.eventDurationDays || 0;
    const columnCount = options.columnCount || 1;
    const rowCount = options.rowCount || 1;
    const slotCount = options.slotCount || 0;
    if (mode !== 'move' && mode !== 'resize-end' && mode !== 'create-region') {
        throw new Error(`startDrag: unknown mode '${mode}'.`);
    }
    if (mode === 'resize-end' && axis !== 'x' && axis !== 'y') {
        throw new Error(`startDrag: mode='resize-end' requires axis='x' or 'y' (got '${axis}').`);
    }
    if (mode === 'create-region') {
        if (axis !== 'x' && axis !== 'y') {
            throw new Error(`startDrag: mode='create-region' requires axis='x' or 'y' (got '${axis}').`);
        }
        if (typeof anchorX !== 'number' || typeof anchorY !== 'number') {
            throw new Error(`startDrag: mode='create-region' requires numeric anchorX + anchorY (got ${anchorX}, ${anchorY}).`);
        }
    }
    if (!dotnetRef || typeof dotnetRef.invokeMethodAsync !== 'function') {
        throw new Error('startDrag: options.dotnetRef must be a DotNetObjectReference.');
    }
    if (typeof onDropMethodName !== 'string' || onDropMethodName.length === 0) {
        throw new Error('startDrag: options.onDropMethodName must be a non-empty string.');
    }
    if (typeof onCancelMethodName !== 'string' || onCancelMethodName.length === 0) {
        throw new Error('startDrag: options.onCancelMethodName must be a non-empty string.');
    }

    // One drag at a time — silently abort any older drags.
    if (_activeDrags.size > 0) {
        for (const oldHandle of Array.from(_activeDrags.keys())) {
            _cleanup(oldHandle);
        }
    }

    const handle = _newHandle();
    const rect = elementRef.getBoundingClientRect();

    // Build the ghost.
    //
    // For move/resize-end the ghost is a clone of the source element positioned
    // at its current viewport-pixel rect; mid-drag we either translate (move) or
    // grow/shrink one dimension (resize-end). For create-region there's no
    // existing event to clone — the ghost is a fresh <div> anchored at the
    // pointer-down position with zero initial extent along the configured axis;
    // mid-drag we grow it along that axis as the cursor moves.
    let ghost;
    if (mode === 'create-region') {
        // Fresh ghost; sits at the anchor with cross-axis matching `elementRef`'s
        // extent (or one slice of it when crossAxisIndex + crossAxisDivisions are
        // supplied — e.g., Week passes column index + 7 to bound the ghost to
        // the column the user pressed in).
        const useSlice = typeof crossAxisIndex === 'number'
            && typeof crossAxisDivisions === 'number'
            && crossAxisDivisions > 0;
        let crossStart, crossSize;
        if (axis === 'y') {
            if (useSlice) {
                crossSize = rect.width / crossAxisDivisions;
                crossStart = rect.left + crossAxisIndex * crossSize;
            } else {
                crossStart = rect.left;
                crossSize = rect.width;
            }
        } else {
            if (useSlice) {
                crossSize = rect.height / crossAxisDivisions;
                crossStart = rect.top + crossAxisIndex * crossSize;
            } else {
                crossStart = rect.top;
                crossSize = rect.height;
            }
        }
        ghost = document.createElement('div');
        ghost.style.position = 'fixed';
        if (axis === 'y') {
            // Vertical-time ghost: anchored X = column/lane left; width = column/lane width.
            ghost.style.left = crossStart + 'px';
            ghost.style.top = anchorY + 'px';
            ghost.style.width = crossSize + 'px';
            ghost.style.height = '1px';
        } else {
            // Horizontal-time ghost: anchored Y = row/lane top; height = row/lane height.
            ghost.style.left = anchorX + 'px';
            ghost.style.top = crossStart + 'px';
            ghost.style.width = '1px';
            ghost.style.height = crossSize + 'px';
        }
    } else {
        ghost = elementRef.cloneNode(true);
        ghost.removeAttribute('id');
        ghost.style.position = 'fixed';
        ghost.style.left = rect.left + 'px';
        ghost.style.top = rect.top + 'px';
        ghost.style.width = rect.width + 'px';
        ghost.style.height = rect.height + 'px';
        // Default visual: transform identity (no movement yet).
        ghost.style.transform = 'translate(0px, 0px)';
    }
    ghost.classList.add(ghostClass || 'calee-scheduler-drag-ghost');
    ghost.style.margin = '0';
    ghost.style.pointerEvents = 'none';
    // Visual defaults — applied inline so they survive Blazor's CSS isolation
    // (the ghost is appended to <body> and inherits the cloned element's scope
    // attribute, but the matching .razor.css rule won't be the root
    // CaleeScheduler.razor's scope; safer to set them inline). Consumers can
    // restyle by supplying a different `ghostClass` and a rule that wins via
    // higher specificity in a global stylesheet.
    ghost.style.opacity = '0.7';
    ghost.style.zIndex = '1000';
    ghost.style.boxShadow = '0 8px 24px rgba(0, 0, 0, 0.18)';
    ghost.style.transition = 'none';
    document.body.appendChild(ghost);

    // Build the highlight element (issue #13). The highlight is appended to the grid
    // container (not <body>) so it scrolls with the grid and stays in the same
    // coordinate system as the snap math. Like the ghost, the highlight is
    // position:absolute + pointer-events:none.
    let highlight = null;
    if (highlightContainer && highlightMode) {
        highlight = document.createElement('div');
        highlight.classList.add('calee-scheduler-drop-target-highlight');
        highlight.style.position = 'absolute';
        highlight.style.pointerEvents = 'none';
        highlightContainer.appendChild(highlight);
    }

    // Body cursor cue. For resize-end and create-region the axis decides the
    // cursor; for move we use the standard grabbing affordance.
    const priorBodyCursor = document.body.style.cursor;
    if (mode === 'resize-end') {
        document.body.style.cursor = axis === 'x' ? 'ew-resize' : 'ns-resize';
    } else if (mode === 'create-region') {
        // 'cell' matches the typical "draw a region" affordance in calendars.
        document.body.style.cursor = 'cell';
    } else {
        document.body.style.cursor = 'grabbing';
    }

    // Pointer capture: we capture on the source element so the OS keeps routing
    // pointer events to us even when the cursor leaves the element's hit box.
    // The current pointerdown event has the pointerId; the caller is expected to
    // have decided drag-has-begun on pointerdown/move, but we don't have that
    // event directly — startDrag is called *after* the gesture. So we use
    // pointerType-agnostic listening on the window and rely on the first
    // pointermove to bind the pointerId.
    //
    // Practical approach: listen on window for pointermove/up/cancel + key. The
    // first pointermove records its pointerId and we call setPointerCapture on
    // the source element so subsequent events stay routed even outside the box.

    const listeners = [];
    const addL = (target, type, fn, opts) => {
        target.addEventListener(type, fn, opts);
        listeners.push({ target, type, fn });
    };

    const state = {
        ghost,
        highlight,
        highlightContainer,
        highlightMode,
        eventDurationPixels,
        eventDurationSlots,
        eventDurationDays,
        columnCount,
        rowCount,
        slotCount,
        // For create-region the anchor IS the pointer-down position (supplied by C#).
        // For move/resize-end the anchor is the source element's center; the first
        // pointermove re-binds startX/Y to the real cursor position so the delta math
        // is exact regardless of where inside the chip the user pressed.
        startX: mode === 'create-region' ? anchorX : (rect.left + rect.width / 2),
        startY: mode === 'create-region' ? anchorY : (rect.top + rect.height / 2),
        // For snap-on-drop, the snap is applied to the *delta*. The ghost's
        // visual position during the drag is unsnapped (per plan §5.1 #4).
        snapPixelsX: snapPixelsX || 0,
        snapPixelsY: snapPixelsY || 0,
        mode,
        // For mode='resize-end' or 'create-region': which dimension grows/shrinks
        // ('x' or 'y'). Null for move.
        axis: (mode === 'resize-end' || mode === 'create-region') ? axis : null,
        // Original ghost dimensions — preserved so resize-end can grow/shrink one
        // dimension off this baseline rather than accumulating from the previous frame.
        originalWidth: rect.width,
        originalHeight: rect.height,
        // For create-region: anchor in viewport coords; threshold for "real drag started".
        anchorX: mode === 'create-region' ? anchorX : 0,
        anchorY: mode === 'create-region' ? anchorY : 0,
        thresholdPx: (mode === 'create-region')
            ? (typeof thresholdPx === 'number' ? thresholdPx : 5)
            : 0,
        // True once the user's movement has crossed the threshold (create-region
        // only). When false at pointerup the drag is considered a click and
        // onCancel fires instead of onDrop — see plan §5.1 #2/#4.
        crossedThreshold: false,
        dotnetRef,
        onDropMethodName,
        onCancelMethodName,
        // pointerId + captureTarget are populated on the first pointermove.
        pointerId: -1,
        captureTarget: null,
        priorBodyCursor,
        listeners,
        // Track the latest delta so up/key handlers see the final movement.
        lastDeltaX: 0,
        lastDeltaY: 0,
        // Track whether startX/Y has been initialized from the first move event.
        // For create-region we treat the anchor as already-initialized (the
        // pointerdown position was passed in directly); other modes use the
        // first pointermove.
        startInitialized: mode === 'create-region',
    };
    _activeDrags.set(handle, state);

    const onPointerMove = (ev) => {
        // First move: record the actual pointer start position + bind pointer capture.
        // Skipped for create-region — its anchor was supplied at start time.
        if (!state.startInitialized) {
            state.startInitialized = true;
            state.startX = ev.clientX;
            state.startY = ev.clientY;
            state.pointerId = ev.pointerId;
            if (typeof elementRef.setPointerCapture === 'function') {
                try {
                    elementRef.setPointerCapture(ev.pointerId);
                    state.captureTarget = elementRef;
                } catch { /* best-effort */ }
            }
        } else if (state.mode === 'create-region' && state.pointerId === -1) {
            // create-region: bind pointer capture on the first move so the OS keeps
            // routing events to us even when the cursor leaves elementRef's hit-box.
            state.pointerId = ev.pointerId;
            if (typeof elementRef.setPointerCapture === 'function') {
                try {
                    elementRef.setPointerCapture(ev.pointerId);
                    state.captureTarget = elementRef;
                } catch { /* best-effort */ }
            }
        }
        const dx = ev.clientX - state.startX;
        const dy = ev.clientY - state.startY;
        state.lastDeltaX = dx;
        state.lastDeltaY = dy;
        // Mid-drag: ghost follows the cursor 1:1 (no snap preview — plan §5.1 #4).
        // - move: translate the whole ghost.
        // - resize-end + axis='y': anchor top, grow/shrink height by dy. (Min 1 px
        //   so the ghost doesn't disappear at zero or negative deltas; the C# drop
        //   handler clamps the *time* to a minimum slot duration.)
        // - resize-end + axis='x': anchor left, grow/shrink width by dx.
        // - create-region + axis='y': anchor at (anchorX, anchorY); ghost.top
        //   shifts up when dy<0 and ghost.height grows in the direction of dy.
        // - create-region + axis='x': symmetric on the X axis.
        if (state.mode === 'resize-end') {
            if (state.axis === 'y') {
                const newH = Math.max(1, state.originalHeight + dy);
                ghost.style.height = newH + 'px';
            } else {
                const newW = Math.max(1, state.originalWidth + dx);
                ghost.style.width = newW + 'px';
            }
        } else if (state.mode === 'create-region') {
            // Track threshold crossing so onPointerUp can decide drop-vs-cancel.
            if (!state.crossedThreshold) {
                if (Math.hypot(dx, dy) > state.thresholdPx) {
                    state.crossedThreshold = true;
                }
            }
            if (state.axis === 'y') {
                // Ghost top = anchorY when dy>=0, otherwise anchorY+dy (so the
                // rectangle grows upward); height = |dy| with a 1 px floor so the
                // ghost remains visible at threshold.
                const h = Math.max(1, Math.abs(dy));
                ghost.style.top = (state.anchorY + Math.min(0, dy)) + 'px';
                ghost.style.height = h + 'px';
            } else {
                const w = Math.max(1, Math.abs(dx));
                ghost.style.left = (state.anchorX + Math.min(0, dx)) + 'px';
                ghost.style.width = w + 'px';
            }
        } else {
            ghost.style.transform = `translate(${dx}px, ${dy}px)`;
        }

        // Update the drop-target highlight (issue #13). The snap is applied to
        // the highlight position so it shows where the event will land on drop.
        if (state.highlight) {
            const dxSnapped = _snap(dx, state.snapPixelsX);
            const dySnapped = _snap(dy, state.snapPixelsY);
            if (state.highlightMode === 'slot-band') {
                _updateSlotBandHighlight(state, dxSnapped, dySnapped, rect);
            } else if (state.highlightMode === 'lane-row') {
                _updateLaneRowHighlight(state, dxSnapped, dySnapped, rect);
            } else if (state.highlightMode === 'day-cell') {
                _updateDayCellHighlight(state, dxSnapped, dySnapped, rect);
            }
        }
    };

    const onPointerUp = (ev) => {
        // Below-threshold create-region: treat as a click on empty space and fire
        // onCancel rather than onDrop. The slot cell's regular @onclick handler
        // (which fires synthesized after pointerdown+up on the same element) then
        // continues to drive OnSlotClicked unchanged. See plan §5.1 #4 (snap on
        // drop) — for create the threshold check happens here, not in the snap.
        if (state.mode === 'create-region' && !state.crossedThreshold) {
            const ref = state.dotnetRef;
            const method = state.onCancelMethodName;
            _cleanup(handle);
            try {
                ref.invokeMethodAsync(method);
            } catch (e) {
                console.warn('[calee-scheduler] onCancel handler invocation failed:', e);
            }
            return;
        }

        // Compute final position with snap applied to the delta.
        const dxRaw = state.lastDeltaX;
        const dyRaw = state.lastDeltaY;
        const dxSnapped = _snap(dxRaw, state.snapPixelsX);
        const dySnapped = _snap(dyRaw, state.snapPixelsY);
        const finalLeftPx = rect.left + dxSnapped;
        const finalTopPx = rect.top + dySnapped;

        const payload = {
            finalLeftPx,
            finalTopPx,
            deltaXPx: dxSnapped,
            deltaYPx: dySnapped,
            mode: state.mode,
        };

        const ref = state.dotnetRef;
        const method = state.onDropMethodName;
        _cleanup(handle);
        // Suppress the synthesized click that the browser dispatches after
        // pointerup so a drag-to-move doesn't also fire the chip's @onclick
        // (which now drives the demo's edit dialog) and a drag-to-create
        // doesn't also fire the slot's @onclick → OnSlotClicked. Only the
        // "real drag" path (this drop branch) installs the suppressor; the
        // below-threshold cancel branch above intentionally lets the click
        // fall through so a tap-without-drag still drives OnSlotClicked.
        _suppressNextClick();
        try {
            ref.invokeMethodAsync(method, payload);
        } catch (e) {
            // C# side may already be torn down (Blazor circuit gone, component disposed
            // mid-drag). Surface via console.warn so consumer-handler bugs aren't invisible
            // while drag works correctly in the steady state.
            console.warn('[calee-scheduler] onDrop handler invocation failed:', e);
        }
    };

    const fireCancel = () => {
        const ref = state.dotnetRef;
        const method = state.onCancelMethodName;
        _cleanup(handle);
        try {
            ref.invokeMethodAsync(method);
        } catch (e) {
            console.warn('[calee-scheduler] onCancel handler invocation failed:', e);
        }
    };

    const onPointerCancel = () => fireCancel();
    const onKeyDown = (ev) => {
        if (ev.key === 'Escape') {
            ev.preventDefault();
            fireCancel();
        }
    };

    // Window-level listeners — works regardless of where the cursor goes.
    addL(window, 'pointermove', onPointerMove);
    addL(window, 'pointerup', onPointerUp);
    addL(window, 'pointercancel', onPointerCancel);
    addL(window, 'keydown', onKeyDown);

    return handle;
}

/**
 * Programmatically abort an in-progress drag. C# calls this when its own state
 * invalidates the drag (e.g., the events list changed during a drag). No
 * callback fires — C# initiated the abort and already knows.
 *
 * @param {string} handle The handle returned from startDrag.
 */
export function abortDrag(handle) {
    if (!handle) return;
    _cleanup(handle);
}

// ---------------------------------------------------------------------------
// Touch long-press gesture (plan §5.1 #9).
//
// Touch drags require a 300ms hold without significant movement before drag
// mode begins, matching iOS/Android calendar convention. Per ADR-0015 the
// timer + move-tracking lives entirely in JS so C# isn't woken at 60Hz to
// poll pointer state; C# awaits one Promise that resolves true (held) or
// false (released early / moved too far / cancelled).
// ---------------------------------------------------------------------------

/**
 * Wait for a touch pointer to be held for `durationMs` without moving more than
 * `moveTolerancePx`. Resolves true when the long-press completes successfully,
 * false if the pointer releases early or moves too far.
 *
 * The first pointermove after this call records the press's anchor position;
 * subsequent moves are compared against that anchor. Pointerup or pointercancel
 * before the timer fires resolves false. Listeners are cleaned up in every
 * resolution path so no leak survives a settled Promise.
 *
 * @param {number} pointerId     The pointerId from the pointerdown event.
 * @param {number} durationMs    How long to wait before resolving true (e.g., 300).
 * @param {number} moveTolerancePx  Maximum movement in pixels before aborting.
 * @returns {Promise<boolean>}
 */
export function awaitLongPress(pointerId, durationMs, moveTolerancePx) {
    return new Promise(resolve => {
        let startX = null;
        let startY = null;
        let timer = null;

        const cleanup = () => {
            if (timer) clearTimeout(timer);
            document.removeEventListener('pointermove', onMove);
            document.removeEventListener('pointerup', onUp);
            document.removeEventListener('pointercancel', onUp);
        };

        const onMove = (e) => {
            if (e.pointerId !== pointerId) return;
            if (startX === null) { startX = e.clientX; startY = e.clientY; return; }
            const dx = e.clientX - startX;
            const dy = e.clientY - startY;
            if (Math.hypot(dx, dy) > moveTolerancePx) {
                cleanup();
                resolve(false);
            }
        };
        const onUp = (e) => {
            if (e.pointerId !== pointerId) return;
            cleanup();
            resolve(false);
        };

        document.addEventListener('pointermove', onMove);
        document.addEventListener('pointerup', onUp);
        document.addEventListener('pointercancel', onUp);

        timer = setTimeout(() => {
            cleanup();
            resolve(true);
        }, durationMs);
    });
}
