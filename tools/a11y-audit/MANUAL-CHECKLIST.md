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

## When this checklist passes

When every row in every "Expected" column matches the actual screen-reader
behavior across NVDA+Edge, NVDA+Firefox, and VoiceOver+Safari — mark Task 14
NFR-06 (screen-reader portion) **complete**. Until then, mark it **pending
manual verification**.
