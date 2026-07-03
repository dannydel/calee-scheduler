# Calee.Scheduler — manual screen-reader checklist

This checklist covers the two screen-reader smoke tests required by Task 14
(NFR-06) that cannot be automated from a headless CLI: **NVDA** (Windows) and
**VoiceOver** (macOS).

Walk through every step in both tools, on both browsers (Chromium-based +
Firefox for NVDA; Safari for VoiceOver). Capture any unexpected announcement
or silent control. File one issue per surprise.

The automated portions of Task 14 (axe-core WCAG audit, contrast verification,
roving-tabindex correctness) are covered by `npm run audit` and the
`Calee.Scheduler.Tests/Accessibility/*` xUnit files — those must already be
passing before you start here.

Raised to a **WCAG 2.2 AA** baseline (issue #12) — see §8 for the 2.2-specific
manual checks axe cannot run itself (2.5.7 dragging movements, 2.4.11 focus
not obscured, 2.5.8 target size spacing-exception notes).

> ✅ **P0 gap fixed (issue #19).** Arrow-key roving-tabindex navigation
> previously updated the `tabindex`/`aria-label` state correctly but never
> moved actual browser focus — `document.activeElement` stayed on the
> previously-focused node. Fixed by calling `calee-scheduler.js`'s new
> `focusActiveGridCell(container)` helper from every view's arrow-key handler,
> after the tabindex swap has rendered (`OnAfterRenderAsync`'s
> set-flag-then-consume-post-render pattern, mirroring the existing
> `_scrollPending` idiom). Verified in a real browser via
> `tools/a11y-audit/audit.mjs`'s focus-check step (Playwright driving Chromium,
> not bUnit — bUnit's headless DOM cannot exercise real focus) on Day, Week,
> Month, Fleet (Timeline), and Agenda; also covered by JS-module-invocation
> assertions in `Calee.Scheduler.Tests/Accessibility/RovingTabindexTests.cs`.
> Tab-based navigation was never affected. **Year view has the same
> pre-existing gap but is out of scope for issue #19** (not listed in that
> issue's affected-view enumeration) — treat any "Arrow-key navigation" row
> for Year below as *pre-existing intent*, not verified current behavior,
> until a follow-up covers it.

## Setup

1. Start the demo: `dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092`.
2. **NVDA**: install [NVDA](https://www.nvaccess.org/) on a Windows host. Launch
   it with default settings. Open the demo in Edge (Chromium) and again in Firefox.
3. **VoiceOver**: on macOS press **⌘+F5** to toggle VoiceOver on. Open the demo
   in Safari. (Chrome works but VoiceOver+Safari is the canonical pairing.)

## Per-route walk

Repeat the checklist below on each route: `/`, `/day`, `/week`, `/month`, `/year`, `/agenda`, `/fleet`.

### 1. Page landing

| Expected announcement |
| --- |
| Page title (e.g. "Calee.Scheduler — Overview"). |
| The hero heading text on the route's editorial header. |
| When you Tab forward, the first reachable control is the toolbar's "Today" button (or the active view-switcher radio when the toolbar isn't visible). |

### 2. Toolbar

Tab forward from the page into the toolbar.

| Step | Expected |
| --- | --- |
| Tab to **Today** button | "Today, button" |
| Tab to **previous range** chevron | "Previous range, button" |
| Tab to **next range** chevron | "Next range, button" |
| Tab past the chevrons | The range label is announced as a polite live region (`aria-live="polite"`). Format: "May 17 – May 23, 2026" for Week, "May 19, 2026" for Day, "May 2026" for Month. |
| Tab to **view switcher** | Reader announces "Day Week Month Year Agenda Fleet, radiogroup". The currently-active view button is `aria-checked="true"`. |
| Arrow Left/Right within the switcher | Focus moves between Day/Week/Month/Year/Agenda/Fleet. The active button announces "checked"; others "not checked". |

### 3. Tabbing into the scheduler grid

| Step | Expected |
| --- | --- |
| Tab from the toolbar into the grid | Focus lands inside the hour grid on the **single tabbable** slot cell (roving tabindex). NVDA announces "Day view hour grid, grid" (or "Week view hour grid"); VoiceOver announces the equivalent. |
| On Month view | "Month view, grid". |
| On Year view | "Year view, grid". |
| On Agenda view | Reader lands on the agenda region/list content; verify dated groups and event buttons are announced in chronological order. |
| On Fleet view | "Timeline view grid, grid". The first roving cell is the top-left lane slot of the first lane row. |

### 4. Arrow-key navigation within the grid

Inside the grid (NVDA: also press NVDA+Space to enter "focus mode" so arrows
pass through to the widget; VoiceOver: use plain arrows):

| View | Arrow | Expected |
| --- | --- | --- |
| Day | Up / Down | Focus moves one slot up / down. The cell's `aria-label` should announce the time slot (we currently rely on the cell's positional context — see open issue below if the announcement is empty). |
| Week | Left / Right | Focus moves one column. Up / Down moves one slot row in the same column. |
| Month | Left / Right / Up / Down | Focus moves one day cell. Each cell announces its full date via `aria-label` (e.g. "Tuesday, May 19, 2026"). |
| Year | Left / Right / Up / Down | Focus moves between day cells within the year grid. Each cell announces its full date via `aria-label`. |
| Agenda | Up / Down / Tab | Focus moves through the chronological event list without trapping keyboard navigation. |
| Fleet (TimeScale=Day) | Up / Down | Focus moves between resource rows. Left / Right moves along the time axis in the same row. |

### 5. Tab to an event

After focus is on an empty slot, Tab forward.

| Step | Expected |
| --- | --- |
| Tab to a timed event | Reader announces the event's accessible name, e.g. "Standup, 9:00 AM to 9:30 AM, button" (Day/Week). Timeline view also includes the lane name. |
| Tab to an all-day event | "{Title}, all day, button". |
| Tab to an overflow chip | "+3 earlier events, button" or "+2 later events, button" (Day/Week/Timeline), "+4 more, button" (Month/Year). |
| Press Enter on an event | The OnEventClicked handler fires. The reader should not detect a route change unless your consumer code triggers one. |
| Press Enter on an overflow chip | Same handler model (OnDayOverflowClicked / OnWeekOverflowClicked / OnTimelineOverflowClicked / OnMonthOverflowChipClicked / OnYearOverflowClicked). |

### 6. Today indicator

| View | Expected |
| --- | --- |
| Day (when Date is today) | The day header announces "current, date" alongside the date. Visual: `aria-current="date"`. |
| Week | The today column's header announces "current, date". |
| Month | The today cell announces "current, date" inside its `aria-label`. |
| Year | The today cell announces "current, date" inside its `aria-label`. |
| Agenda | Today's group heading is announced in the agenda chronology when present. |
| Fleet (Day mode) | The current time indicator is purely decorative (`aria-hidden`); no announcement expected. |

### 7. Focus visibility

For every focusable element, verify the focus outline (2px solid `#2563eb`,
1–2px offset) is visible at the configured zoom level. Use the `Default_Theme_Pair_Meets_Threshold`
xUnit tests as a baseline — they assert the outline meets 3:1 non-text
contrast against every background it draws on.

## 8. WCAG 2.2 AA additions (issue #12)

Criteria axe-core cannot check automatically (`npm run audit`'s `wcag22a`/`wcag22aa`
tags currently only cover 2.5.8 via the `target-size` rule — see
[`README.md`](../../README.md) "Accessibility" for the full per-criterion map).
Each subsection below was verified against the live demo (Playwright driving
real Chromium, not bUnit) as of this issue.

### 8.1 SC 2.5.7 — Dragging Movements

Every drag interaction (move, resize, create) needs a single-pointer-free
alternative. Verified per view, per interaction:

| View | Drag-to-create alternative | Verified? | Keystroke sequence |
| --- | --- | --- | --- |
| Day / Week / WorkWeek / Month / Fleet (Timeline) | `n` → `OnCreateAtFocusRequested` | ✅ **Works** | Tab into the grid (any cell) → press **n** → the editor dialog opens pre-filled with a default slot (the demo pages default to "now, rounded to the hour/day" — see each page's `HandleCreateAtFocus`) → Tab to Title, type → Tab to Save → **Enter**. No drag gesture required at any point. |
| Agenda | N/A — Agenda has no drag-to-create (it's a flat list, no grid to draw on) | N/A | — |

| View | Drag-to-move / drag-to-resize alternative | Verified? | Notes |
| --- | --- | --- | --- |
| Day / Week / WorkWeek / Fleet (Timeline) | `m` → `OnMoveModeRequested`; `Shift+ArrowUp`/`Shift+ArrowDown` → `OnResizeKeystrokeRequested` | ❌ **Not a functional alternative as shipped** | Both are documented **placeholders** (`SchedulerComponentBase` XML docs, `OnMoveModeRequested`/`OnResizeKeystrokeRequested`): the library fires the trigger callback but does not itself move or resize the event, and the callback is parameterless (no focused-event payload), so a consumer can't reliably build the behavior on top without their own focus-tracking wrapper. The demo app does **not** wire either callback (confirmed: zero references in `Calee.Scheduler.Demo/`). Pressing `m` or `Shift+ArrowUp/Down` today does nothing observable. |
| Month | No move/resize keystrokes at all (Month doesn't wire chip-scope `m`/`Shift+Arrow` — bars/chips there aren't resizable) | ❌ N/A | Month events are all-day-shaped; drag-to-move exists (chip drag), no keyboard equivalent ships. |
| Agenda | N/A — Agenda has no drag-to-move/resize (flat list, no positional dragging) | N/A | — |

**Design-decision flag (see final report):** drag-to-move and drag-to-resize do
**not** have a working keyboard (or other single-pointer) alternative in the
library as shipped, for any view. This is a genuine SC 2.5.7 gap, not a
mechanical CSS/markup fix — closing it needs either (a) widening
`OnMoveModeRequested`/`OnResizeKeystrokeRequested` to carry the focused event
(an API shape change, out of this issue's additive-only scope) or (b) a
documented consumer-side wrapper pattern once the library exposes enough to
build one. Tracked as **issue #20** (open); do not mark this row passing.
#20 was blocked on real roving-tabindex focus transfer (issue #19, now fixed —
a keyboard "move mode" needs to know which event is actually focused), so #20
is unblocked but not itself resolved by #19.

### 8.2 SC 2.4.11 — Focus Not Obscured (Minimum)

Spot-check: for every focusable element, confirm no sticky/fixed chrome with
an opaque background renders on top of the element's focus indicator at any
scroll position.

| Region | Sticky/fixed chrome present? | Spot-check | Result |
| --- | --- | --- | --- |
| Day / Week / WorkWeek header row + all-day row | No — the header/all-day rows sit in normal flow *outside* the grid body's own `overflow-y: auto` region (they're not `position: sticky`), so the internally-scrolling body can never carry a focused cell underneath them. | Scroll the internal grid body to the bottom via keyboard (arrow-key focus-scroll or mouse wheel), confirm header stays visible and never paints over a focused cell. | Pass — structural, not just visual. |
| Agenda date-group headers | **Yes** — `.calee-scheduler-agenda-header` is `position: sticky; top: 0; z-index: 2`, opaque background, inside the scrollable `.calee-scheduler-agenda-list`. | Scroll the list ~1 row height, then focus the row immediately below the sticky header (via Tab, or via ArrowDown now that issue #19 makes arrow-key roving transfer real focus too). Confirm the row's `:focus-visible` outline is not clipped by the header. | **Found broken, now fixed:** before this issue, a row scrolled to just under the header measured ~55% of its height behind the sticky header (25px of 45.5px, live-measured). Fixed by adding `scroll-margin-top: 3rem` to `.calee-scheduler-agenda-row` so the browser's native scroll-into-view-on-focus reserves clearance past the header. Re-verified after the fix: 0px overlap. Re-spot-check on any future change to `--calee-scheduler-agenda-header` padding/font-size (the `3rem` margin is a hand-measured constant, not derived from the header's actual height token). |
| Month "+N more" popover / event popovers, command palette, editor dialog | These are modal/anchored overlays the consumer's demo renders (`EventActionPopover`, `OverflowChooserPopover`, `EventEditorDialog`, `CommandPaletteDialog`) — check that Tab is trapped inside each while open and that no library chrome (toolbar, sticky headers) renders on top of the dialog's own focused control. | Open each dialog, Tab through its controls. | Pass (dialogs render above the scheduler via normal stacking; no sticky chrome shares their z-index layer in the demo's CSS). |
| Toolbar (all views) | Toolbar is not sticky/fixed — it's a normal-flow row above the view body. | N/A | Pass — no overlap is structurally possible. |

### 8.3 SC 2.5.8 — Target Size (Minimum) — spacing-exception notes

`npm run audit`'s `target-size` rule (axe-core ≥4.10, tag `wcag22aa`) checks
this automatically; passing the audit means every target is either ≥24×24 CSS
px *or* has enough clearance to its nearest neighbor for the exception to
apply. This section records **which** targets rely on which path, so a future
CSS change doesn't silently regress a target that currently only passes via
spacing:

| Target | Rendered size (measured) | Compliance path |
| --- | --- | --- |
| `.calee-scheduler-month-chip` (event chips) | 24px tall (was ~20.2px) | **True size** — `min-height: 1.5rem` + `flex-shrink: 0` added by this issue. No longer depends on spacing. |
| `.calee-scheduler-month-overflow-chip` ("+N more") | 24px tall (was ~20.5px) | **True size** — same fix, same reasoning. |
| `.calee-scheduler-overflow-chip` (Day/Week "+N earlier/later") | 24px tall exactly | **True size** — already compliant pre-issue-#12; right at the floor, no margin for regression. |
| `.calee-scheduler-toolbar-view-button` (Day/Week/Month/Year/Agenda/Timeline/Work Week switcher) | ~23.6px tall (font-size 0.8125rem × line-height 1.2 + 0.25rem vertical padding) | **Spacing exception** — height is ~0.4px under the 24px floor; passes because the switcher buttons sit in an isolated toolbar row with ample vertical clearance above/below (no other target closer than 24px in any direction). Width is always well over 24px. **If the toolbar's vertical padding/row height ever shrinks, re-run the audit** — this one has no size margin to fall back on. |
| `.calee-scheduler-toolbar-button` (Today) | ~25.6px tall | True size. |
| `.calee-scheduler-toolbar-button--chevron` (Prev/Next) | 24px tall exactly | Borderline true size — no margin. |

## Known limitations / open items

- ~~The Day-view slot cells don't currently emit an `aria-label` carrying the slot's
  time-of-day.~~ **Resolved:** every slot gridcell now carries an `aria-label`. Day:
  `"9:00 AM, empty slot"`. Week: `"Tuesday, 9:00 AM, empty slot"`. Timeline Day:
  `"Alex Chen, 9:00 AM, empty slot"`. Timeline Week/Month day-cells:
  `"Alex Chen, Wednesday, May 20, empty cell"`. Verify these are spoken correctly
  on tab into a slot.
- Timeline view in `TimeScale=Week` / `TimeScale=Month` reuses the same `[role="gridcell"]`
  markup as the `Day` mode but each cell represents a whole day rather than a slot —
  the `aria-label` distinguishes them ("empty slot" for the hourly Day mode, "empty cell"
  for the daily Week/Month modes).
- ~~**[P0, found 2026, issue #12 verification]** Arrow-key roving-tabindex
  navigation does not move real browser focus in any view (Day, Week/WorkWeek,
  Month, Timeline/Fleet, Agenda).~~ **Resolved (issue #19)** — see the banner
  near the top of this document. Year view has the identical pre-existing gap
  but was not in issue #19's affected-view enumeration; still open there.
- **[Issue #20, open]** Drag-to-move and drag-to-resize have no functional
  keyboard (or other single-pointer) alternative in the library as shipped —
  see §8.1. `OnMoveModeRequested` / `OnResizeKeystrokeRequested` are
  parameterless trigger placeholders; the demo does not wire either. Issue #20
  was blocked on the roving-tabindex real-focus fix above (a keyboard "move
  mode" needs to know which event is actually focused); that block is now
  cleared, but #20 itself still needs either a richer payload (API change) or
  a documented consumer wrapper pattern — not fixed by #19.

## When this checklist passes

When every row in every "Expected" column matches the actual screen-reader
behavior across NVDA+Edge, NVDA+Firefox, and VoiceOver+Safari — mark Task 14
NFR-06 (screen-reader portion) **complete**. Until then, mark it **pending
manual verification**.
