# Calee.Scheduler

A Blazor scheduling component suite for internal .NET applications — Day, Week, Month, Year, Agenda, and Timeline views.

**▶ [Live demo](https://dannydel.github.io/calee-scheduler/)** — the demo app built as Blazor WebAssembly and deployed to GitHub Pages on every push to `main`.

- Generic-typed components (`<CaleeScheduler TEvent="MyEvent" ... />`); ships a default `CalendarEvent` record for consumers who do not need a custom type.
- Standalone views or a composed root scheduler with a shared toolbar and view switcher.
- Sweep-line overlap layout with lane reuse; events render correctly without consumer geometry math.
- Fail-closed interaction surface: drag-to-move (Day, Week, Month, Timeline views), drag-to-resize, drag-to-create, double-click-to-create, delete, multi-select, undo/redo triggers, shortcuts, and command-palette hooks.
- Required per-view `TimeZone` parameter — the library never converts event times; it uses the supplied zone for "today", day boundaries, and emitted `SchedulerSlot` offsets only.
- WCAG 2.2 AA-oriented default markup (with documented exceptions — see §9.1a): structural ARIA, roving tabindex, screen-reader-checked, contrast-verified default theme (§9). Keyboard alternatives to all drag interactions: `n` for drag-to-create, `m` + arrow keys + Enter/Escape for drag-to-move, `Shift+ArrowUp`/`Shift+ArrowDown` for drag-to-resize.
- CSS isolation with documented theming levers: `--calee-scheduler-*` custom properties, attribute splatting, named class hooks, and `::deep` via `data-calee-region` attributes.
- No transitive runtime dependencies beyond `Microsoft.AspNetCore.Components.*`.
- Source-stable public API across all 1.x releases.

**Current state:** Phase 1 read-only rendering is complete, and the Phase 2 interaction/power-user surface is implemented in the library. The former Phase 1 "Resource" view is now "Timeline" (`IResource` → `ILane`, `CaleeSchedulerResourceView` → `CaleeSchedulerTimelineView`). The demo app includes one possible consumer-owned editor, action popover, command palette, shortcut help dialog, and undo stack; those are intentionally not part of the RCL.

---

## 1. Quickest start

Five minutes from `dotnet add package` to a rendered Week view.

```csharp
// Program.cs
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;

builder.Services.AddCaleeScheduler(options =>
{
    options.DefaultView = SchedulerView.Week;
});
```

```razor
@* Pages/Schedule.razor *@
@page "/schedule"
@using Calee.Scheduler.Components
@using Calee.Scheduler.Contracts

<CaleeScheduler TEvent="CalendarEvent"
                TimeZone="@_tz"
                Events="@_events" />

@code {
    private readonly TimeZoneInfo _tz =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly List<CalendarEvent> _events = new()
    {
        new CalendarEvent(
            Id:    "e1",
            Title: "Standup",
            Start: new DateTimeOffset(2026, 5, 19,  9,  0, 0, TimeSpan.FromHours(-4)),
            End:   new DateTimeOffset(2026, 5, 19,  9, 30, 0, TimeSpan.FromHours(-4))),
    };
}
```

What each piece does:

- `AddCaleeScheduler` registers `CaleeSchedulerOptions` and the validation rules in PRD §4.6. There is intentionally **no** `DefaultTimeZone` option (ADR-0001).
- `<CaleeScheduler TEvent="CalendarEvent" ...>` mounts the root component (toolbar + active view). `TEvent` is required even when using the default `CalendarEvent` — this is the generic-only API (ADR-0004).
- `TimeZone` is required on every view. It controls "today" highlights and the offsets stamped onto `SchedulerSlot` payloads, **not** event positioning (FR-09).
- `Events` is `IReadOnlyList<TEvent>`. `null` is treated as empty and logged at Warning (PRD §4.6).

Add `@using Calee.Scheduler.Components` and `@using Calee.Scheduler.Contracts` to your `_Imports.razor` to avoid re-importing per page.

---

## 2. Installation

### 2.1 NuGet feed

Calee.Scheduler publishes to **nuget.org** (the public registry). Just add the package:

```bash
dotnet add package Calee.Scheduler
```

No internal feed setup required.

### 2.2 CSS

All component styles ship via Blazor CSS isolation. The component bundle is wired up automatically by the host app's framework reference — make sure the standard isolated-CSS link is present in `App.razor` (it is by default in a new `dotnet new blazor` project):

```html
<link rel="stylesheet" href="@Assets["YourApp.styles.css"]" />
```

### 2.3 Imports

In `_Imports.razor` for any page that uses the scheduler:

```razor
@using Calee.Scheduler.Components
@using Calee.Scheduler.Contracts
```

`Calee.Scheduler.Contracts` carries the public types (`ICalendarEvent`, `CalendarEvent`, `ILane`, `Lane`, `SchedulerView`, `SchedulerSlot`, `SchedulerRange`, `DayOverflowContext`, the interaction context classes, and the `OverflowKind` / `TimelineScale` enums). `Calee.Scheduler.Components` carries the six view components and the root `CaleeScheduler`.

---

## 3. Service registration

```csharp
builder.Services.AddCaleeScheduler(options =>
{
    options.DefaultView                 = SchedulerView.Week;  // default: Week
    options.DefaultStartHour            = 8;                   // default: 8   (0..24)
    options.DefaultEndHour              = 18;                  // default: 18  (0..24)
    options.DefaultSlotDurationMinutes  = 30;                  // default: 30  (15 | 30 | 60)
    options.DefaultFirstDayOfWeek       = DayOfWeek.Sunday;    // default: Sunday
    options.DefaultMaxEventsPerDay      = 3;                   // default: 3   (>= 1)
});
```

Every property has a sane default; `AddCaleeScheduler()` with no arguments is valid. Contract violations (`StartHour > EndHour`, slot duration not in `{15, 30, 60}`, `MaxEventsPerDay < 1`, etc.) surface as `OptionsValidationException` on the first `IOptions<CaleeSchedulerOptions>.Value` access.

`TimeZone` is intentionally **not** an option. It is a required per-component parameter on every view. Picking it once at registration time would let "today" highlights and slot offsets silently disagree page-by-page when developer-machine local time differs from production server time.

---

## 4. View examples

All six views are usable standalone or composed under `CaleeScheduler`. The composed root adds the toolbar and view switcher (FR-08).

### 4.1 Day

A single day with hour slots, all-day row, current-time indicator, and per-day "+N earlier" / "+N later" overflow chips for events outside the visible hour range.

Minimal:

```razor
<CaleeSchedulerDayView TEvent="CalendarEvent"
                       TimeZone="@_tz"
                       Events="@_events" />
```

With typical parameters:

```razor
<CaleeSchedulerDayView TEvent="CalendarEvent"
                       TimeZone="@_tz"
                       Events="@_events"
                       Date="@_anchor"
                       StartHour="6"
                       EndHour="22"
                       SlotDurationMinutes="15"
                       ShowCurrentTimeIndicator="true"
                       EventFilter="@(e => !e.IsAllDay || _showAllDay)"
                       OnEventClicked="HandleEventClicked"
                       OnSlotClicked="HandleSlotClicked"
                       OnDayOverflowClicked="HandleOverflow" />
```

`Date` is bindable (`@bind-Date="_anchor"`). When omitted, the view tracks its own anchor seeded from "today in `TimeZone`."

### 4.2 Week

Seven consecutive days with hour slots, a configurable first-day-of-week (default Sunday), the same overflow + clipping semantics as Day. Multi-day all-day events render as continuous bars across the all-day row; multi-day timed events render as per-day chunks with arrow indicators on the cut edge.

Minimal:

```razor
<CaleeSchedulerWeekView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events" />
```

With typical parameters:

```razor
<CaleeSchedulerWeekView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        @bind-Date="_anchor"
                        FirstDayOfWeek="DayOfWeek.Monday"
                        StartHour="7"
                        EndHour="19"
                        SlotDurationMinutes="30"
                        EventTemplate="EventInner"
                        OnEventClicked="HandleEventClicked"
                        OnRangeChanged="HandleRangeChanged" />

@code {
    private RenderFragment<CalendarEvent> EventInner => evt => __builder =>
    {
        <strong>@evt.Title</strong>
        <small>@evt.Start.ToString("h:mm tt")</small>
    };
}
```

Restricting to a subset of days — e.g. a work week — via `VisibleDays`:

```razor
<CaleeSchedulerWeekView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        VisibleDays="@(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })" />
```

`VisibleDays` is `null` by default (all seven days — no behavior change for existing consumers). When supplied, only the listed days render as columns, always ordered by `FirstDayOfWeek`; the subset doesn't need to be contiguous (e.g. Monday/Wednesday/Friday). Events falling entirely on a hidden day are excluded from the view; a multi-day timed event that continues into a hidden day still shows the existing clip-edge arrow on the visible chunk next to it. `OnRangeChanged` and drag/keyboard navigation operate over the visible subset only. An empty list (or one that matches none of the week's seven days) is treated as "all seven days" with a logged warning, rather than rendering a zero-column grid.

### 4.3 Month

A six-week (42-cell) grid anchored at `FirstDayOfWeek`. Each cell shows up to `MaxEventsPerDay` chips; overflow surfaces as a "+N more" chip that fires `OnDayOverflowClicked(... OverflowKind.Month ...)`. Multi-day all-day events always render in full as continuous bars across the cells they span (the "+N more" budget shrinks accordingly).

Minimal:

```razor
<CaleeSchedulerMonthView TEvent="CalendarEvent"
                         TimeZone="@_tz"
                         Events="@_events" />
```

With typical parameters:

```razor
<CaleeSchedulerMonthView TEvent="CalendarEvent"
                         TimeZone="@_tz"
                         Events="@_events"
                         @bind-Date="_anchor"
                         FirstDayOfWeek="DayOfWeek.Sunday"
                         MaxEventsPerDay="5"
                         EventChipTemplate="ChipInner"
                         OnDayOverflowClicked="HandleOverflow" />

@code {
    private RenderFragment<CalendarEvent> ChipInner => evt => __builder =>
    {
        <span>@evt.Title</span>
    };
}
```

Note: Month uses `EventChipTemplate`, not `EventTemplate` (chips are visually distinct from time-grid event blocks).

### 4.4 Year

A twelve-month overview for event density. The default `MiniMonths` mode renders day numbers with density dots; `Heatmap` renders compact density squares. Month headers can fire `OnMonthClicked` so consumers can drill into their own Month view route or update a composed root scheduler.

Minimal:

```razor
<CaleeSchedulerYearView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events" />
```

With typical parameters:

```razor
<CaleeSchedulerYearView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        @bind-Date="_anchor"
                        FirstDayOfWeek="DayOfWeek.Monday"
                        Style="YearViewStyle.Heatmap"
                        Layout="YearViewLayout.Grid3x4"
                        OnMonthClicked="HandleMonthClicked" />
```

### 4.5 Agenda

A date-grouped list view for narrow layouts, screen-reader-heavy workflows, and "what's next" scans. It renders a rolling `AgendaDays` window from the anchor date, hides empty days, and supports an `EventRowTemplate` for row content.

Minimal:

```razor
<CaleeSchedulerAgendaView TEvent="CalendarEvent"
                          TimeZone="@_tz"
                          Events="@_events" />
```

With typical parameters:

```razor
<CaleeSchedulerAgendaView TEvent="CalendarEvent"
                          TimeZone="@_tz"
                          Events="@_events"
                          @bind-Date="_anchor"
                          AgendaDays="14"
                          EventRowTemplate="AgendaRow"
                          OnEventClicked="HandleEventClicked"
                          OnDateClicked="HandleDateClicked" />
```

### 4.6 Timeline view

Rows = lanes, X-axis = time. Three selectable horizontal time scales: `Day` (hours), `Week` (weekday + date columns), `Month` (day-of-month columns). Events whose `LaneKey` returns `null` (or an Id not in `Lanes`) land in the trailing "Unassigned" row, hideable via `ShowUnassignedRow=false`. All-day events render as a thin banner strip on the lane's row label, never as a full-width band in the time grid.

Minimal (TimeScale=Day):

```razor
<CaleeSchedulerTimelineView TEvent="RouteAssignment"
                            TimeZone="@_tz"
                            Events="@_events"
                            Lanes="@_drivers"
                            LaneKey="@(e => e.DriverId)" />
```

`TimeScale=Day` — hour grid, narrowed hour range, per-row "+N earlier" / "+N later" chips:

```razor
<CaleeSchedulerTimelineView TEvent="RouteAssignment"
                            TimeZone="@_tz"
                            Events="@_events"
                            Lanes="@_drivers"
                            LaneKey="@(e => e.DriverId)"
                            TimeScale="TimelineScale.Day"
                            StartHour="6"
                            EndHour="20"
                            SlotDurationMinutes="30" />
```

`TimeScale=Week` — one column per day across the visible week; multi-day timed events render as a single continuous block (no per-day split — the X-axis is already continuous time):

```razor
<CaleeSchedulerTimelineView TEvent="RouteAssignment"
                            TimeZone="@_tz"
                            Events="@_events"
                            Lanes="@_drivers"
                            LaneKey="@(e => e.DriverId)"
                            TimeScale="TimelineScale.Week"
                            FirstDayOfWeek="DayOfWeek.Monday" />
```

`TimeScale=Month` — one column per day in the visible month:

```razor
<CaleeSchedulerTimelineView TEvent="RouteAssignment"
                            TimeZone="@_tz"
                            Events="@_events"
                            Lanes="@_drivers"
                            LaneKey="@(e => e.DriverId)"
                            TimeScale="TimelineScale.Month" />
```

The Timeline view is also reachable through `CaleeScheduler` itself — wiring both `Lanes` and `LaneKey` on the root makes the toolbar's view switcher surface the sixth "Timeline" entry.

### 4.7 Blocked days

`DayModifier` (issue #8) is a per-day state hook on Day, Week (including WorkWeek and any `VisibleDays` subset), and Month — the same `Func<T, TResult>` idiom as `EventFilter` / `EventClass`, but keyed on a day instead of an event:

```razor
<CaleeSchedulerWeekView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        DayModifier="@GetDayState" />

@code {
    private SchedulerDayState? GetDayState(DateTimeOffset day) =>
        _blockedDates.Contains(day.Date)
            ? new SchedulerDayState(IsBlocked: true, Label: "Blocked — no route published yet")
            : null;
}
```

The hook is evaluated once per rendered day, in the grid time zone (ADR-0001) — not per slot — and only for the columns/cells actually rendered: a Week/WorkWeek view with a `VisibleDays` subset never evaluates the hook for a hidden day. Returning `null` for a day (the default when `DayModifier` itself is `null`) means "normal day" — zero visual or behavioral change.

`SchedulerDayState` carries:

| Property     | Type      | Meaning                                                                         |
| ------------ | --------- | -------------------------------------------------------------------------------- |
| `IsBlocked`  | `bool`    | Renders the day with the blocked-day visual treatment and suppresses create affordances on it. |
| `Class`      | `string?` | Optional per-day CSS class hook, composed alongside the library's own classes (same convention as `EventClass`). Independent of `IsBlocked` — usable to annotate a day without blocking it. |
| `Label`      | `string?` | Accessible label announced to screen readers. Falls back to a generic "blocked" announcement when null and `IsBlocked` is true. |

**Create-only suppression — the create-vs-move split.** A blocked day fails closed on every *create* affordance:

- Double-click-to-create (`AllowDoubleClickToCreate`) — no-op, no `OnEventCreated`.
- Drag-to-create (`AllowDragToCreate`) — no-op; the drag doesn't even start on a blocked anchor day, so no ghost rectangle is drawn. If a drag's swept region touches *any* blocked day (e.g. it starts on an open day and crosses into a blocked one), the create does not fire — the simplest defensible fail-closed rule.
- The create-at-focus keystroke (`OnCreateAtFocusRequested`, default `n`) — no-op when the keyboard-focused grid cell sits on a blocked day.

It does **not** suppress:

- **Drag-to-move / drag-to-resize onto the day.** `OnEventMoved` / `OnEventResized` still fire — reschedule and create are different permissions in the consuming product. Reject the move/resize yourself via `EventMoveContext.Cancel` / `EventResizeContext.Cancel` if your permission model requires it.
- **`OnSlotClicked`.** Selection and navigation are not creation, so clicking (or Enter-ing) a blocked slot/cell still fires it.

**Accessibility.** Blocked cells/headers carry `aria-disabled="true"` and an `aria-label` announcing why the day is inert, but stay in the roving-tabindex sequence — a blocked cell is reachable and focusable like any other cell, it just can't be used to create. The default visual treatment (`calee-scheduler-day-blocked`, §8.1) combines a background tint with a diagonal-stripe pattern so the state doesn't rely on color alone.

**Month view has no per-date header.** Month's weekday header row (`Sun`, `Mon`, …) is generic across all six weeks, not per-date, so `DayModifier` only affects Month's day *cells* — there's no separate per-date header element to mark.

### 4.8 Day-header template and click (issue #9)

`DayHeaderTemplate` and `OnDayHeaderClicked` let a consumer inject per-day content into the day-header cell on Day and Week (including WorkWeek and any `VisibleDays` subset) and react to the header being activated — e.g. a per-day count badge that opens a side panel. Both are forwarded by the root `CaleeScheduler` to its Day/WorkWeek/Week arms; Month is out of scope (its header row is generic weekday names, not per-date, same reasoning as `DayModifier` in §4.7).

```razor
<CaleeSchedulerWeekView TEvent="CalendarEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        DayHeaderTemplate="DayBadge"
                        OnDayHeaderClicked="HandleDayHeaderClicked" />

@code {
    private RenderFragment<DateTimeOffset> DayBadge => day => __builder =>
    {
        var count = _events.Count(e => e.Start.Date == day.Date);
        if (count > 0)
        {
            <span class="day-count-badge" aria-hidden="true">@count</span>
        }
    };

    private void HandleDayHeaderClicked(DateTimeOffset day) =>
        OpenDaySidePanel(day);
}
```

**Template context.** `DayHeaderTemplate` receives the day's midnight `DateTimeOffset` in the grid time zone — the exact same value shape `DayModifier` receives (§4.7), so a consumer combining both hooks reasons about one date shape everywhere. The template renders *after* the library's own weekday/date label, inside the same header cell — it does not replace the label (ADR-0002 spirit: the library owns the cell container and label, the consumer owns the injected extras). A `null` template (the default) renders byte-identical markup to the read-only path.

**`OnDayHeaderClicked` and the fail-closed default.** The callback receives the same midnight value and fires on pointer click or on Enter/Space while the header holds keyboard focus. A header cell is made focusable and interactive (`tabindex="0"`, `role="button"`, pointer cursor, an `aria-label` announcing the full date) **only when this callback has a delegate wired** — leaving it unset keeps the header exactly as it renders today: no tabindex, no role, no cursor change. Like every other click path in the library, activation is suppressed while a drag is in flight.

**Space doesn't scroll the page.** An interactive day header is a `<div role="button">`, not a native `<button>`, so nothing stops the browser's global "Space scrolls the viewport" default automatically. Blazor's `@onkeydown:preventDefault` directive is element-wide — binding it here to swallow Space would also swallow Tab's default focus-move off the header, a keyboard trap — so the suppression instead lives in the shipped JS module (`calee-scheduler.js`'s `registerDayHeaderKeyGuard`), scoped to exactly the Space key on an interactive day-header target. The view registers the guard while `OnDayHeaderClicked` has a delegate and unregisters it on dispose; every other key, including Tab, is untouched. No wiring is required on your part — this is purely internal to the library's activation path.

**⚠️ Don't nest interactive controls inside `DayHeaderTemplate` when `OnDayHeaderClicked` is wired.** The header cell itself becomes the button (`role="button"`, per the note above) — placing a `<button>`, `<a>`, or another focusable/clickable control inside it creates a nested-interactive ARIA violation (assistive tech cannot meaningfully expose a button inside a button), and a real click on the nested control still bubbles up to fire `OnDayHeaderClicked` too, double-firing both handlers. This mirrors the `EventTemplate` contract (§6.1) — the library owns the outer interactive envelope; the template owns non-interactive content inside it. The badge in the example above is `aria-hidden="true"` and inert by design — that's the endorsed pattern. If you need the badge itself to be separately clickable (e.g. a different action from the header click), don't wire `OnDayHeaderClicked` at all and instead drive both interactions from your own button placed *outside* the header, or reconsider the row you want to make clickable.

**Composes with blocked days (§4.7).** A day `DayModifier` marks blocked keeps its blocked `aria-label` (the blocked text wins over the plain date name) and its blocked class — but the template still renders and the click still fires. Blocking gates *create* affordances, not header content or interaction. **`aria-disabled` is suppressed whenever the header is interactive** — an operable `role="button"` whose click and Enter/Space demonstrably work must never also announce "disabled" to a screen reader; the blocked state is still conveyed through the `", blocked"`-suffixed `aria-label`. A non-interactive blocked header (no `OnDayHeaderClicked` delegate) keeps `aria-disabled="true"` exactly as issue #8 shipped it.

**Accessible names are `en-US`-formatted.** `DayHeaderAccessibleName`'s `"dddd, MMMM d, yyyy"` output (and the blocked-day label built on top of it) is always formatted with the `en-US` culture, matching every other accessible-name call site in the library — this is a deliberate 1.x consistency choice, not an oversight, and it applies even to Week's non-blocked header label, which previously formatted with the ambient `CurrentCulture`. Localizing accessible names is a roadmap item; until then, expect `en-US` date text regardless of the host's culture.

---

## 5. TimeZone semantics and footguns

The single most-asked question. Read this before shipping.

### 5.1 Why `TimeZone` is required on every view

The library has to know what day "today" is, where day boundaries fall, and what offset to stamp onto the `SchedulerSlot` it emits when a consumer clicks an empty cell. There is no universal right answer — desktop apps want browser-local, multi-tenant SaaS wants the user's profile zone, ops dashboards want the data-center zone.

Rather than guess, every view requires an explicit `TimeZoneInfo TimeZone` parameter. Passing `null` is a hard fail (`ArgumentNullException`). There is intentionally no service-level `DefaultTimeZone` silently inheriting a wrong default is worse than failing loudly at the call site.

There is a roadmap item to make this easier and or remove it entirely since it can be quite tedious to have to define this over and over again.

### 5.2 What the library does and doesn't do

The library **never converts** `ICalendarEvent.Start` / `ICalendarEvent.End`. They are taken at face value — the offset on each `DateTimeOffset` is honored as the event's authoritative time, and the event is placed on the grid at whatever wall-clock time falls out of that.

The library uses `TimeZone` to:

- Compute "today".
- Compute day boundaries (so a `Date` parameter resolves to a 24-hour span in the right zone).
- Stamp the offset on `SchedulerSlot.Start` / `SchedulerSlot.End` when the consumer clicks an empty cell.

That's it. No event mutation, no implicit `ConvertTime`, no time-of-day arithmetic on the event values themselves.

### 5.3 Problems that were faced: mixed offsets

Because events render at their own offsets, a list that mixes offsets visually surprises:

```csharp
new CalendarEvent("a", "EST event", new(2026, 5, 19, 9, 0, 0, TimeSpan.FromHours(-4)), ...),
new CalendarEvent("b", "PST event", new(2026, 5, 19, 9, 0, 0, TimeSpan.FromHours(-7)), ...),
```

Both events claim 9 AM but on an `America/New_York` grid the PST event lands at noon. Sometimes that's exactly what you want (a fleet that spans time zones). More often it's a bug — the consumer wanted "the local 9 AM in each driver's zone" rendered side by side, not literal wall-clock times.

**Recommendation: normalize events to the grid zone before passing them in.** Here is a simple helper example:

```csharp
private IReadOnlyList<CalendarEvent> NormalizeToGrid(
    IEnumerable<CalendarEvent> events, TimeZoneInfo grid) =>
    events.Select(e => e with
    {
        Start = TimeZoneInfo.ConvertTime(e.Start, grid),
        End   = TimeZoneInfo.ConvertTime(e.End,   grid),
    }).ToList();
```

`TimeZoneInfo.ConvertTime` preserves the absolute instant and changes the offset. The wall-clock time on the grid is now consistent across every event.

### 5.4 DST

The library's day-boundary math goes through `TimeZoneInfo.GetUtcOffset`, so a Day or Week rendered across a DST transition has the correct 23-hour or 25-hour day. Consumers don't need to do anything special — pass the zone and the boundaries fall where they should.

---

## 6. EventTemplate and EventChipTemplate

The template contract is split across two parameters because time-grid event blocks and Month chips are visually different elements (ADR-0002).

| Parameter            | Views that accept it                | Renders inside                                  |
| -------------------- | ----------------------------------- | ----------------------------------------------- |
| `EventTemplate`      | Day, Week, Timeline                 | The library-positioned event card               |
| `EventChipTemplate`  | Month                               | The compact chip in a month cell                |

### 6.1 What the library owns vs. what the template owns

The library owns:

- The outer container (position, size, `data-calee-region="event"`).
- The colored left border driven by `ICalendarEvent.Color` (or the `--calee-scheduler-event-default-color` fallback).
- The focus outline (`--calee-scheduler-focus-color`).
- Click handlers and ARIA wiring.
- Drag handles when the relevant interaction flags are enabled.

The template owns the **inside** of that container. Default content (when the parameter is `null`):

- Time-grid blocks: bold title + muted time range.
- Month chips: colored dot + truncated title.

### 6.2 Custom template example

```razor
<CaleeSchedulerWeekView TEvent="MyEvent"
                        TimeZone="@_tz"
                        Events="@_events"
                        EventTemplate="EventInner" />

@code {
    private RenderFragment<MyEvent> EventInner => evt => __builder =>
    {
        <div class="evt-row">
            @if (evt.IsUrgent)
            {
                <span class="evt-badge evt-badge--urgent" aria-label="Urgent">!</span>
            }
            <strong>@evt.Title</strong>
        </div>
        <small>@evt.Start.ToString("h:mm tt") — @evt.End.ToString("h:mm tt")</small>
    };
}
```

The template never sets `position`, `top`, `height`, or the colored border — those come from the library and would be overwritten on the next render. If you need to influence the visual envelope beyond what the template provides, use the `EventClass` per-event class hook (§7.3) or one of the CSS custom properties (§7.1).

---

## 7. Timeline binding

The lane concept is intentionally narrow (ADR-0011, supersedes ADR-0008): a row identity and nothing more. Events do not know what lane they belong to; the view's `LaneKey` projection does the mapping. Lanes can be drivers, vehicles, rooms, practitioners — or projects, statuses, tags, anything you want a row per.

### 7.1 Why lanes are not on `ICalendarEvent`

Adding a `LaneId` to the event contract would make every consumer pay the cost — even those who never touch the Timeline view — and would prevent the same event from being grouped against multiple lane axes (driver + vehicle, room + practitioner, status + assignee). Keeping the projection on the view lets one event power any number of lane cuts.

### 7.2 Wiring it up

```razor
<CaleeSchedulerTimelineView TEvent="RouteAssignment"
                            TimeZone="@_tz"
                            Events="@_events"
                            Lanes="@_drivers"
                            LaneKey="@(e => e.DriverId)"
                            ShowUnassignedRow="true" />

@code {
    private readonly List<Lane> _drivers = new()
    {
        new Lane("d-1", "Avery"),
        new Lane("d-2", "Blair"),
        new Lane("d-3", "Casey"),
    };

    private List<RouteAssignment> _events = new()
    {
        new RouteAssignment { Id = "r1", DriverId = "d-1", /* ... */ },
        new RouteAssignment { Id = "r2", DriverId = null,  /* ... */ }, // unassigned row
    };
}
```

`Lanes` and `LaneKey` are required (`null` hard-fails). An empty `Lanes` list is allowed — the view renders the toolbar and an unassigned row only.

The same Timeline view can model a fleet (lanes = drivers, as above) or a project board (lanes = projects, with `LaneKey="@(e => e.ProjectId)"`) or an availability grid (lanes = rooms). Pick whatever string identifier maps cleanly onto your domain.

### 7.3 Null `LaneKey` and missing-Id soft degradation

- `LaneKey(event)` returning `null` → unassigned row.
- `LaneKey(event)` returning a string not present in `Lanes` → unassigned row, with a warning logged through `ILogger<CaleeScheduler>` if one is registered (PRD §4.6).
- `ShowUnassignedRow="false"` hides the row entirely; events routed to it are not rendered. The row is also auto-hidden when no events route to it in the visible range.

### 7.4 Cross-day events under TimeScale=Week / Month

Unlike Day and Week views (which split multi-day timed events into per-day chunks), the Timeline view's X-axis is continuous time across the visible range under `TimeScale=Week` and `TimeScale=Month`. Multi-day timed events render as a **single continuous block** that spans the days they cover (FR-09e). No per-day split, no arrow indicators on day boundaries.

Phase 2's drag-to-move across lane rows will populate `EventMoveContext.NewLaneId` with the target row's `ILane.Id` — that field is always set when a move originates in Timeline view, even when the user moved within the same row, so consumers can distinguish "time-only" from "reassignment" with one comparison (PRD §4.2).

---

## 8. Styling levers

The library exposes four mechanisms for visual customization (ADR-0005). Pick the lightest one that does the job.

### 8.1 CSS custom properties

Set on any ancestor of `<CaleeScheduler>` (typically `:root` or a single-app wrapper):

```css
:root {
    --calee-scheduler-bg: #0b1220;
    --calee-scheduler-event-default-color: #38bdf8;
    --calee-scheduler-focus-color: #f59e0b;
    --calee-scheduler-today-background: #1e293b;
}
```

Full surface (defaults shown):

| Variable                                           | Default     | Description                                                                 |
| -------------------------------------------------- | ----------- | --------------------------------------------------------------------------- |
| `--calee-scheduler-bg`                             | `#ffffff`   | Root scheduler container background.                                        |
| `--calee-scheduler-event-default-color`            | `#4a6ea8`   | Per-event fallback color when `ICalendarEvent.Color` is null.               |
| `--calee-scheduler-event-color`                    | (inline)    | Per-event color. Set inline by the renderer from `ICalendarEvent.Color`.    |
| `--calee-scheduler-focus-color`                    | `#2563eb`   | 2px focus outline on every interactive element.                             |
| `--calee-scheduler-today-background`               | `#eef4ff`   | Background of the today column / today cell / today header.                 |
| `--calee-scheduler-blocked-day-bg`                 | `#e4e4e7`   | Background tint of a blocked day's header/cell (§4.7, issue #8).            |
| `--calee-scheduler-blocked-day-pattern-color`      | `rgba(24, 24, 27, 0.1)` | Diagonal-stripe pattern color layered over `--calee-scheduler-blocked-day-bg` — the pattern (not color alone) signals the blocked state. |
| `--calee-scheduler-grid-line-color`                | `#d4d4d8`   | Primary grid separator color (hour rules, day-column borders).              |
| `--calee-scheduler-slot-line-color`                | `#ececef`   | Lighter slot-boundary lines inside the hour grid.                           |
| `--calee-scheduler-current-time-indicator-color`   | `#dc2626`   | Current-time line and leading dot.                                          |
| `--calee-scheduler-pixels-per-hour`                | `56px`      | Vertical density (Day/Week) and per-hour horizontal density (Timeline@Day). |
| `--calee-scheduler-day-column-min-width`           | `80px`      | Week view — minimum width of a single day column.                           |
| `--calee-scheduler-month-cell-min-height`          | `5rem`      | Month view — minimum height of a single day cell.                           |
| `--calee-scheduler-month-cell-muted-opacity`       | `0.65`      | Month view — opacity of date numbers in cells outside the displayed month.  |
| `--calee-scheduler-month-bar-height`               | `1.25rem`   | Month view — visual height of a multi-day bar lane.                         |
| `--calee-scheduler-timeline-label-width`           | `12rem`     | Timeline view — fixed-width left strip per lane row.                        |
| `--calee-scheduler-timeline-pixels-per-day`        | `120px`     | Timeline view — horizontal density at TimeScale=Week / Month.               |
| `--calee-scheduler-timeline-row-min-height`        | `5rem`      | Timeline view — row min-height (≈ 3 stacked events).                        |
| `--calee-scheduler-toolbar-bg`                     | `#ffffff`   | Toolbar background.                                                         |
| `--calee-scheduler-toolbar-padding`                | `0.5rem 0.75rem` | Toolbar padding.                                                       |
| `--calee-scheduler-toolbar-text-color`             | `#18181b`   | Toolbar default text color.                                                 |
| `--calee-scheduler-toolbar-button-bg`              | `#f4f4f5`   | Toolbar button background (idle).                                           |
| `--calee-scheduler-toolbar-button-hover-bg`        | `#e4e4e7`   | Toolbar button background (hover).                                          |
| `--calee-scheduler-toolbar-active-bg`              | `#dbeafe`   | Toolbar active-view-button highlight tint.                                  |
| `--calee-scheduler-drop-target-bg`                 | `rgba(37, 99, 235, 0.15)` | Drop-target highlight background tint (issue #13, §8.7).           |
| `--calee-scheduler-drop-target-border`             | `#2563eb`   | Dashed border color of the drop-target highlight (issue #13, §8.7).         |
| `--calee-scheduler-drop-target-outline`            | `rgba(37, 99, 235, 0.3)`  | Optional outline for additional highlight contrast (issue #13, §8.7).  |

The default theme is WCAG 2.1 AA contrast-verified — regression tests in `Calee.Scheduler.Tests/Accessibility/DefaultThemeContrastTests.cs` lock these defaults to passing values. If you override them, re-run the contrast checks against your palette.

#### Responsive breakpoint (issue #10)

The library is mobile-friendly down to a **390px** viewport. All views and the toolbar carry a single mobile breakpoint at **`max-width: 640px`**. CSS media queries cannot read custom properties, so 640px is a documented literal in each view's `.razor.css`, not a themable token — override it by shipping your own media query via a global stylesheet or the `::deep` escape hatch (§8.4) if your product needs a different threshold.

At mobile widths:

- **Toolbar** wraps instead of clipping — the nav group and view switcher share the first row and the `aria-live` range label drops to its own full-width row. Every control is held to the WCAG 2.2 SC 2.5.8 24px minimum target size.
- **Day / Week** compress the time gutter (`4rem → 2.75rem`) and hour labels. Week additionally lowers its per-column floor so all seven day columns fit the page without horizontal scroll:

  | Variable | Desktop default | Mobile (≤640px) |
  | --- | --- | --- |
  | `--calee-scheduler-day-column-min-width` (Week) | `80px` | `40px` |
  | `--calee-scheduler-timeline-label-width` (Timeline) | `12rem` | `7rem` |

  These are the same public tokens listed above — the mobile values are set inside each view's breakpoint. Override the desktop token as usual; if you need a different *mobile* value, restate it inside your own `max-width: 640px` rule.
- **Month** keeps its fluid 7-column grid (cells narrow to ~1/7 of the viewport) and tightens chip/header padding + font; the "+N more" overflow pill continues to absorb entries that no longer fit.
- **Year** collapses every multi-column layout variant (`Grid4x3`/`Grid3x4`/`Grid2x6`/`Grid6x2`) to **2 columns** so each mini-month's day cells stay legible; the single-column layout is unchanged.
- **Agenda** narrows the fixed time cell and tightens row padding. **Timeline** shrinks the lane-label gutter and contains its (intrinsically wide) time axis to an internal horizontal scroll region so the page never overflows.

The narrow-width formats are layout-only (CSS): no interop or C# behavior changes, and touch drag-to-move / drag-to-create continue to flow through the same pointer events at mobile width. The `tools/a11y-audit` script runs every route at **both** 1280×720 and 390×844, asserting zero WCAG 2.2 AA violations and no horizontal page overflow at the mobile viewport.

### 8.2 Attribute splatting

Every public component captures unmatched attributes and splats them onto its outermost element:

```razor
<CaleeScheduler TEvent="CalendarEvent"
                TimeZone="@_tz"
                Events="@_events"
                class="my-cal"
                data-testid="schedule-grid"
                aria-describedby="schedule-help" />
```

`class`, `style`, `data-*`, and `aria-*` compose with the library's own values — the consumer's class is appended, not substituted.

### 8.3 Named class hooks

Per-region class hooks (FR-54) for finer-grained styling without `::deep`:

| Hook                  | Type                      | Applies to                                                |
| --------------------- | ------------------------- | --------------------------------------------------------- |
| `ToolbarClass`        | `string?`                 | The toolbar root.                                         |
| `DayHeaderClass`      | `string?`                 | Day-header cells (Day / Week / Month).                    |
| `TimeGutterClass`     | `string?`                 | Time-gutter column (Day / Week / Timeline@Day).           |
| `AllDayRowClass`      | `string?`                 | All-day row (Day / Week / Timeline).                      |
| `LaneLabelClass`      | `string?`                 | Lane row labels (Timeline view only).                     |
| `EventClass`          | `Func<TEvent, string?>?`  | Per-event class — receives the event, returns a class or null. |
| `DayModifier`         | `Func<DateTimeOffset, SchedulerDayState?>?` | Per-day state (§4.7, issue #8) — receives a day's midnight boundary, returns `null` for a normal day or a `SchedulerDayState` carrying `IsBlocked` / `Class` / `Label`. Day / Week / Month only. |
| `DayHeaderTemplate`   | `RenderFragment<DateTimeOffset>?` | Content injected into the day-header cell, after the default label (§4.8, issue #9). Receives the day's midnight boundary. Day / Week (incl. WorkWeek / `VisibleDays`) only. |
| `OnDayHeaderClicked`  | `EventCallback<DateTimeOffset>`   | Fires on day-header click or Enter/Space when focused (§4.8, issue #9). Header is only made focusable/interactive when this has a delegate wired. Day / Week (incl. WorkWeek / `VisibleDays`) only. |

### 8.4 `::deep` escape hatch via `data-calee-region`

Every region carries a stable `data-calee-region` attribute (FR-55) so consumer selectors can survive internal class renames. Use isolated `::deep` selectors in the consumer's `.razor.css`:

```css
::deep [data-calee-region="event"]:hover {
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.18);
}

::deep [data-calee-region="overflow-chip"] {
    font-variant-numeric: tabular-nums;
}
```

Documented regions: `scheduler`, `toolbar`, `toolbar-today`, `toolbar-prev`, `toolbar-next`, `range-label`, `view-switcher`, `toolbar-view-button`, `day-header`, `time-gutter`, `all-day`, `hour-grid`, `event`, `overflow-chip`, `month-cell`, `lane-rows`, `lane-row`, `unassigned-row`, `lane-label`. JS-created elements: `.calee-scheduler-drop-target-highlight` (drop-target highlight, issue #13), `.calee-scheduler-drag-ghost` (drag ghost).

### 8.5 Putting it together

A consumer that wants a darker theme, a custom test hook, a class on the toolbar, a per-event status class, and a custom hover treatment for events:

```razor
@* Page.razor *@
<div class="schedule-shell">
    <CaleeScheduler TEvent="MyEvent"
                    TimeZone="@_tz"
                    Events="@_events"
                    class="my-calendar"
                    data-testid="schedule"
                    ToolbarClass="my-toolbar"
                    EventClass="@(e => e.Status == EventStatus.Tentative ? "evt-tentative" : null)" />
</div>
```

```css
/* Page.razor.css */
.schedule-shell {
    --calee-scheduler-bg: #0b1220;
    --calee-scheduler-event-default-color: #38bdf8;
    --calee-scheduler-focus-color: #f59e0b;
}

::deep .my-toolbar { border-bottom: 2px solid #1e293b; }

::deep [data-calee-region="event"]:hover {
    transform: translateY(-1px);
    transition: transform 80ms ease-out;
}

::deep .evt-tentative { opacity: 0.65; border-left-style: dashed !important; }
```

### 8.7 Drop-target highlight (issue #13)

During drag operations (move, resize), the library renders a visual drop-target highlight that tracks the snapped target location — the user sees where the event will land before releasing the pointer. The highlight is JS-created and lives in the grid container (not `<body>`) so it scrolls with the view. Keyboard move/resize mirrors the same visual treatment via the `.calee-scheduler-event--keyboard-phantom` class on the phantom event chip.

**CSS custom properties** (defaults, set on `.calee-scheduler`):

| Variable                               | Default                  | Description |
| -------------------------------------- | ------------------------ | ----------- |
| `--calee-scheduler-drop-target-bg`     | `rgba(37, 99, 235, 0.15)` | Background tint. |
| `--calee-scheduler-drop-target-border` | `#2563eb`                | Dashed border color (meets WCAG 2.2 AA 3:1 non-text contrast against the default grid background). |
| `--calee-scheduler-drop-target-outline`| `rgba(37, 99, 235, 0.3)`  | Optional outline for additional contrast weighting. |

**Class hooks:**

| Class | Where | Purpose |
| ----- | ----- | ------- |
| `.calee-scheduler-drop-target-highlight` | JS-created element, child of the grid container during a drag. | Pointer-driven drop-target indicator. |
| `.calee-scheduler-event--keyboard-phantom` | Event chip during keyboard-move/resize mode. | Keyboard-driven target indicator (same visual treatment). |

To restyle the highlight, override the properties or use `::deep`:

```css
::deep .calee-scheduler-drop-target-highlight {
    background: rgba(16, 185, 129, 0.15);  /* Green tint */
    border-color: #10b981;
}

::deep .calee-scheduler-event--keyboard-phantom {
    background: rgba(16, 185, 129, 0.15);
    border-color: #10b981;
}
```

The default highlight does **not** rely on color alone (WCAG 2.2 AA 1.4.11): it uses a background tint + dashed border + outline.

---

## 9. Accessibility

The library ships **WCAG 2.2 AA**-oriented default markup and a contrast-verified default theme (raised from 2.1 AA per issue #12 — see §9.1a for the per-criterion breakdown).

### 9.1 What's verified automatically

- **axe-core / Playwright audit** at `tools/a11y-audit/`. Run from the demo:

  ```bash
  dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092
  # In another shell:
  cd tools/a11y-audit
  npm install
  npx playwright install chromium   # first run only
  npm run audit
  ```

  The script audits the composed root plus the six dedicated view routes (`/`, `/day`, `/week`, `/month`, `/year`, `/agenda`, `/fleet`) against `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22a`, `wcag22aa` and exits non-zero on any violation. Output is structured JSON at `tools/a11y-audit/report.json`. Requires axe-core ≥4.10 for the 2.2 rule set; `@axe-core/playwright ^4.10.0` already resolves there.

- **Default-theme contrast** is regression-tested in xUnit at `Calee.Scheduler.Tests/Accessibility/DefaultThemeContrastTests.cs`. Token changes that drop below 4.5:1 break the build. (Contrast is a WCAG 2.1 criterion — 1.4.3 — unchanged by 2.2; this is the same test suite before and after the 2.2 upgrade.)

- **Roving-tabindex correctness** is bUnit-tested at `Calee.Scheduler.Tests/Accessibility/RovingTabindexTests.cs` — at the `tabindex`-attribute level (bUnit's headless DOM doesn't exercise real browser focus). The same file also asserts, via JS-module-invocation mocks, that every arrow-key roving move calls the JS focus-transfer helper described below; the resulting real-browser `document.activeElement` move is verified live by `tools/a11y-audit/audit.mjs`'s focus-check step (issue #19).

### 9.1a WCAG 2.2 AA — per-criterion approach (issue #12)

axe-core's `wcag22a`/`wcag22aa` tags currently add exactly one automatically-checkable rule: `target-size` (SC 2.5.8). The other two 2.2 criteria most relevant to a drag-and-drop scheduling grid — SC 2.5.7 (dragging movements) and SC 2.4.11 (focus not obscured) — have no axe rule as of axe-core 4.11 and are covered by manual spot-checks instead. Summary (full detail in `tools/a11y-audit/MANUAL-CHECKLIST.md` §8):

| Criterion | Mechanism | Status |
| --- | --- | --- |
| 2.5.8 Target size (minimum) | Automated — `npm run audit`'s `target-size` rule, every route, on every push/PR (`.github/workflows/a11y.yml`) | **Pass.** Month view's event chips and "+N more" overflow chip were the only real violations found (~20px tall, under the 24px floor); fixed via `min-height: 1.5rem` + `flex-shrink: 0`. A few controls (the toolbar's view-switcher buttons, the Day/Week overflow chips, the toolbar chevrons) pass at or slightly under 24px via the SC 2.5.8 spacing exception rather than true size — see the checklist §8.3 table before touching their CSS. |
| 2.4.11 Focus not obscured (minimum) | Manual spot-check, `MANUAL-CHECKLIST.md` §8.2 | **Pass**, one fix applied. Day/Week/Month header rows are structurally immune (outside the grid body's own scroll region). Agenda's sticky date-group headers *did* obscure the row immediately beneath them after a small scroll (~55% of the row's height, live-measured) — fixed with `scroll-margin-top` on `.calee-scheduler-agenda-row` so focus-triggered scrolling clears the sticky header. |
| 2.5.7 Dragging movements | Manual verification, `MANUAL-CHECKLIST.md` §8.1 | **Partial — known gap, tracked as issue #20 (open).** Drag-to-create has a working keyboard alternative in every timed view (the `n` keystroke → `OnCreateAtFocusRequested`). Drag-to-move and drag-to-resize do **not**: `OnMoveModeRequested` / `OnResizeKeystrokeRequested` are parameterless trigger placeholders (no focused-event payload), the demo app doesn't wire either into real behavior, and closing this gap needs either a richer callback payload (an API shape change, out of this issue's additive-only scope) or a documented consumer wrapper once one exists. Issue #20 was blocked on real roving-tabindex focus transfer (issue #19, below) — that block is now cleared, but #20 itself is unresolved. |

**Also found while manually verifying the criteria above, not part of SC 2.5.7/2.4.11/2.5.8 itself, and fixed as issue #19 (P0):** arrow-key roving-tabindex navigation updated the `tabindex` state correctly in every view but never moved real browser focus (no view called `FocusAsync`/the shipped `focusElement()` JS helper after an arrow-key re-render) — Tab-based navigation was unaffected. Fixed by adding `calee-scheduler.js`'s `focusActiveGridCell(container)` helper (queries the view's grid/list container for the roving cell and calls `.focus()` on it) and invoking it from every view's arrow-key handler after the tabindex swap has rendered, in Day, Week/WorkWeek, Month, Year, Timeline, and Agenda. Verified live via `tools/a11y-audit/audit.mjs`'s focus-check step (Playwright/Chromium) and via JS-invocation-assertion tests in `RovingTabindexTests.cs`.

### 9.2 What's verified manually

Screen-reader smoke tests cannot be headless. The script in `tools/a11y-audit/MANUAL-CHECKLIST.md` walks NVDA (Windows, Edge + Firefox) and VoiceOver (macOS, Safari) across every demo route, with expected announcements for the toolbar, range label live region, grid navigation, event focus, and overflow chips. Run this once per release. §8 of the same document adds the WCAG 2.2 manual checks described above.

### 9.3 Keyboard navigation

| Key            | Behavior                                                                                       |
| -------------- | ---------------------------------------------------------------------------------------------- |
| Tab            | Move between major regions (toolbar → grid → events). Roving tabindex inside each region.      |
| Shift+Tab      | Reverse of Tab.                                                                                |
| Arrow keys     | Move focus between slot cells (Day / Week / Timeline@Day) or day cells (Month / Timeline@Week/Month). |
| Enter          | On a slot: fire `OnSlotClicked`. On an event: fire `OnEventClicked`. On a chip: fire its handler. On a day header with `OnDayHeaderClicked` wired: fire it. |
| Space          | Same as Enter on events / chips / an interactive day header.                                   |
| Escape         | Release focus to the parent container.                                                         |
| Delete         | Fire `OnEventDeleted` on a focused event when `AllowDelete=true`.                              |

The toolbar's view switcher is a `radiogroup`; arrow Left/Right moves the active radio. The range label is announced as a polite live region (`aria-live="polite"`) so date navigation surfaces without forcing focus.

### 9.4 Roving tabindex

Inside each grid (hour grid, month grid, timeline grid), exactly one cell carries `tabindex="0"` at a time; every other cell carries `tabindex="-1"`. Arrow keys move the "0" between cells, keeping the grid a single tab stop from the consumer's perspective while making every cell directly reachable from the keyboard. The contract is asserted by `RovingTabindexTests.cs` for every view.

--

### 10. What the library will never own

**No built-in dialogs, drawers, or confirmation UI**. The library is pure UI for visualization + interaction events.CRUD callbacks (`OnEventCreated`, `OnEventMoved`, `OnEventResized`, `OnEventDeleted`, `OnEventsDeleted`) hand the consumer a context with a mutable `Cancel` flag; the consumer renders its own confirmation, mutates its own data store, and sets `Cancel = true` if it wants the library to revert.

The demo app's `EventEditorDialog`, `EventActionPopover`, `CommandPaletteDialog`, `ShortcutHelpDialog`, and `SimpleUndoStack` are examples of consumer-owned wiring. They deliberately live under `Calee.Scheduler.Demo`, not in the RCL.

**No recurrence expansion.** Consumers pass concrete event instances.

**No data fetching, persistence, auth, sync, or notifications.** None of them.

---

## 11. Building from source

```bash
git clone <repo-url>
cd calee-scheduler
dotnet restore
dotnet build           # 0 warnings, 0 errors with the CS1591 enforcement on
dotnet test
```

Run the demo:

```bash
dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092
```

Seven demo routes are wired:

| Route       | Demonstrates                                                          |
| ----------- | --------------------------------------------------------------------- |
| `/`         | Composed root `CaleeScheduler` with the toolbar and a default view.   |
| `/day`      | `CaleeSchedulerDayView` with a three-way overlap cluster and overflow chips. |
| `/week`     | `CaleeSchedulerWeekView` including multi-day all-day bars.            |
| `/month`    | `CaleeSchedulerMonthView` with bar + chip overflow.                   |
| `/year`     | `CaleeSchedulerYearView` density overview in mini-month / heatmap styles. |
| `/agenda`   | `CaleeSchedulerAgendaView` rolling date-grouped list.                 |
| `/fleet`    | `CaleeSchedulerTimelineView` with three drivers as lanes and an unassigned event. |

The demo is the canonical reference for the seed-data shape that exercises every visual rule.

### 12 Continuous integration

Three GitHub Actions workflows live under `.github/workflows/`:

| Workflow | File | Trigger | Purpose |
| -------- | ---- | ------- | ------- |
| `ci`      | [`.github/workflows/ci.yml`](.github/workflows/ci.yml)         | push / PR to `main` | Restore, build (`-warnaserror`), test, pack. Uploads `test-results` and `nupkg` artifacts. |
| `a11y`    | [`.github/workflows/a11y.yml`](.github/workflows/a11y.yml)     | push / PR to `main` | Boots the demo and runs the Playwright + axe-core audit at `tools/a11y-audit/`. Fails on any WCAG 2.2 AA violation. Uploads `a11y-report`. |
| `release` | [`.github/workflows/release.yml`](.github/workflows/release.yml) | push of a `v*` tag | Mirrors `ci`, then publishes the `.nupkg` to **nuget.org** (not Tyler Tech's Artifactory) and creates a GitHub Release. |

Dependabot keeps NuGet packages and GitHub Actions versions current weekly — see [`.github/dependabot.yml`](.github/dependabot.yml).

The release workflow needs **one** repository secret (Settings → Secrets and variables → Actions):

| Secret | Description |
| ------ | ----------- |
| `NUGET_API_KEY` | API key generated at <https://www.nuget.org/account/apikeys>. Scope: **Push (new versions of this package)**. Glob pattern: `Calee.Scheduler` (or wider if this repo will publish more packages later). Expiry: 365 days recommended. |

The nuget.org source URL is pinned in the workflow's `env.NUGET_SOURCE` (currently `https://api.nuget.org/v3/index.json`) — change it there if the destination feed ever moves.

---

## 13. Releasing

Releases ship from the `main` branch. Tag the commit you want to ship and push the tag:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The `release` workflow fires on any tag matching `v*`. It runs the same restore / build / test / pack sequence as CI, then:

1. Pushes the `.nupkg` to **nuget.org** via `dotnet nuget push --source $NUGET_SOURCE --api-key $NUGET_API_KEY --skip-duplicate`, so re-running the workflow on an already-published version is a no-op rather than a failure.
2. Uploads the `.nupkg` as a workflow artifact.
3. Creates a GitHub Release whose name and tag both match the pushed tag, with the `.nupkg` attached.

Required secret is listed in §12.1. If it's missing, the workflow fails before pushing anything.

**Switching destinations later.** If you want to also push to Tyler Tech's Artifactory (or any other internal feed) at some point, edit `release.yml`: change `env.NUGET_SOURCE` to the feed's v3 index URL, and re-add the `dotnet nuget add source` step with `--username` / `--password` (Artifactory requires basic auth, not just an API key). The `dotnet nuget push` step itself stays the same.
