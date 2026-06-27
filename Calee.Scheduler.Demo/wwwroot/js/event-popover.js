// Demo-only JS helper for EventActionPopover.
//
// The library's chip <button> elements all carry `data-calee-region="event"`,
// but they don't expose a per-event id attribute on the DOM (the library
// tracks chips by an internal ElementReference dictionary instead — see
// CaleeSchedulerDayView.razor.cs et al.). The Agenda view's rows additionally
// carry `data-calee-event-id`, but the time-grid + timeline + month chips do
// not. That means we can't reliably look up a chip by its event id from the
// demo side.
//
// Workaround that stays inside the demo (ADR-0010 — library ships no popovers,
// and this is demo-only): install a capture-phase `pointerdown` listener that
// remembers the most-recently-pressed chip's bounding rect. The C# popover
// host then asks for that rect right after OnEventClicked fires, anchors the
// popover against it, and clears the cache. If the most recent click wasn't
// on a chip (e.g., the user triggered OnEventClicked from a keystroke), the
// lookup returns null and the host falls back to centering the popover.
//
// Two selectors cover every view:
//   * `[data-calee-region="event"]` — Day, Week, Month, Timeline chips/bars.
//   * `[data-calee-region="agenda-row"]` — Agenda rows.
//
// We listen on `pointerdown` (capture phase) so we see the chip BEFORE the
// library's chip handler runs `stopPropagation` (which prevents a bubble-
// phase listener from seeing the chip).

const CHIP_SELECTOR =
    '[data-calee-region="event"], [data-calee-region="agenda-row"], [data-calee-region="overflow-chip"]';

let lastChipRect = null;

function rememberChipRect(event) {
    const target = event.target;
    if (!(target instanceof Element)) {
        return;
    }
    const chip = target.closest(CHIP_SELECTOR);
    if (!chip) {
        // Click outside any chip — leave the previous remembered rect in
        // place; the host clears it after consuming, and a non-chip click
        // wouldn't fire OnEventClicked anyway.
        return;
    }
    const r = chip.getBoundingClientRect();
    lastChipRect = {
        top: r.top,
        left: r.left,
        right: r.right,
        bottom: r.bottom,
        width: r.width,
        height: r.height,
        viewportWidth: window.innerWidth,
        viewportHeight: window.innerHeight,
    };
}

// One-time install — repeated imports of this module across page navigations
// hand back the same module instance (ES module caching), so the listener is
// installed at most once.
let installed = false;
function ensureInstalled() {
    if (installed) return;
    installed = true;
    document.addEventListener('pointerdown', rememberChipRect, /* useCapture */ true);
}

/**
 * Installs the capture-phase listener before the first chip click. The Blazor
 * component calls this on its first interactive render so the initial popover
 * can anchor instead of falling back to center.
 */
export function ensureTracking() {
    ensureInstalled();
}

/**
 * Returns the bounding rect of the most-recently-pressed chip, then clears
 * the cache. Returns null when there's no remembered rect (the popover host
 * should fall back to centering the popover in that case). Calling this also
 * lazily installs the capture-phase listener on first use.
 */
export function consumeLastChipRect() {
    ensureInstalled();
    const rect = lastChipRect;
    lastChipRect = null;
    return rect;
}

/**
 * Move focus back to a chip that's still in the DOM, identified by its
 * accessible name. The library's chips render
 * `aria-label="{Title}, {time-range}"` (Day/Week/Timeline) or use the agenda
 * row's accessible name (Agenda). We match by aria-label prefix on the title
 * so we don't have to re-format the time range exactly here — the title is
 * stable for a given event and unique enough for focus restoration in the
 * demo. If no match exists (e.g., the chip was just deleted), the function
 * returns false and the caller drops focus to <body>.
 */
export function focusChipByTitle(title) {
    if (!title) return false;
    const candidates = document.querySelectorAll(CHIP_SELECTOR);
    for (const el of candidates) {
        const label = el.getAttribute('aria-label') ?? el.textContent ?? '';
        const isMatch =
            label.startsWith(`${title},`) ||
            (el.getAttribute('data-calee-region') === 'agenda-row' && label.includes(title));
        if (isMatch && el instanceof HTMLElement) {
            el.focus();
            return true;
        }
    }
    return false;
}
