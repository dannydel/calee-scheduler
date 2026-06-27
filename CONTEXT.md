# Calee.Scheduler — Domain Glossary

The shared language for this library. Terms here are domain concepts, not implementation details.

## Event

A single occurrence on the schedule, supplied by a consumer. The library never creates events; it positions and renders them. Identified by `Id`. Carries `Start`, `End`, `Title`, `IsAllDay`, and optional `Color`. Consumers needing extra per-event fields implement `ICalendarEvent` on their own `TEvent` (generics are the canonical extension path; the contract has no metadata bag).

## All-day event

An [[Event]] with `IsAllDay = true`. A **claim by the consumer** that the event has no time-of-day — it belongs to a calendar date (or range of dates), not to specific hours.

- The time components of `Start` and `End` are ignored for all-day events; only the date portion is meaningful.
- All-day events render in the dedicated all-day row, never in the hour grid.
- A "full-day training" that has actual hours (8am–5pm) is **not** an all-day event — the consumer should use `IsAllDay = false` with a tall block in the time grid.

## Multi-day event

An [[Event]] whose `Start.Date != End.Date`. Multi-day is **derived** (from the dates), not a flag the consumer sets. Orthogonal to [[All-day event]] — both timed and all-day events can be multi-day.

## Zero-duration event

An [[Event]] where `Start == End` and `IsAllDay = false`. Derived, not a flag.

## Event chunk

A renderer-facing fragment of an [[Event]]. The unit the renderer treats as a single positioned rectangle in the time grid. Carries:

- A reference to the underlying [[Event]] (the consumer's original `TEvent`)
- A chunk-local `Start` / `End` confined to one day's visible window (under `PerDay` split) or to the original event's full extent (under `Continuous` split)
- Clip-at-start / clip-at-end flags that drive the ↑/↓/←/→ continues-from / continues-to indicators

Chunks themselves implement `ICalendarEvent` so the [[Stack]] layout (`EventLayoutEngine`) can consume them without knowing whether they're whole events or split fragments.

For Day and Week views, a multi-day timed event becomes one chunk per visible day, each carrying the same underlying event reference — so `OnEventClicked` always fires with the consumer's authoritative event regardless of which chunk was clicked. For TimelineView (`Continuous` split), a multi-day timed event remains one chunk spanning all the days it touches, because TimelineView's X-axis is continuous time across days.

## Grid time zone

The frame of reference the calendar grid uses for **its own** date math: "today" (for the current-time indicator), the first-day-of-week computation, day-column boundaries, and the offset stamped onto `SchedulerSlot` values the library emits.

The grid time zone is supplied by the consumer as a `TimeZoneInfo` parameter. The library does **not** convert consumer event `Start`/`End` values into this zone — those are still taken at face value. The grid time zone only governs what the *library itself* generates and renders.

A consumer who supplies events in mixed offsets is responsible for whatever weirdness ensues; the library will lay them out using their literal offsets and may produce surprising results.

## Today

The current date in the [[Grid time zone]]. Used to highlight the today column, to render the current-time indicator (FR-07), and as the default anchor when the toolbar's "Today" button is pressed.

## Anchor date

The date a view is currently rendering around. Exposed as a bindable `Date` parameter on each view (and on the root scheduler). Each view normalizes the anchor differently:

- **Day view** — renders the anchor date itself.
- **Week view** — renders the seven days starting at the first-day-of-week boundary that contains the anchor.
- **Month view** — renders the six-week grid covering the calendar month that contains the anchor.

The consumer can read or set the anchor; if no value is supplied, the view manages its own internal anchor (starting at [[Today]]).

## Current view

Which of `Day`/`Week`/`Month`/`Timeline` is being rendered. Exposed as a bindable `View` parameter on the root scheduler. When the consumer doesn't supply one, the root manages internal state (starting from the configured `DefaultView`).

## Lane

A row in [[TimelineView]] — a driver, vehicle, room, project, status, or any other schedulable axis the consumer groups events by. Modeled by the `ILane` interface (Id, Name, optional Color). The library knows nothing about lanes outside of TimelineView; the [[Event]] contract is **not** widened to carry a lane reference. The library never assumes events know which lane they belong to; consumers always supply the [[LaneKey]] projection.

See [[ADR-0011]] (which supersedes the Phase 1 "Resource" framing in ADR-0008).

## LaneKey

The `Func<TEvent, string?>` the consumer passes to [[TimelineView]] to map each event to a lane ID. An event whose `LaneKey` returns null (or returns an Id not in the supplied `Lanes` list) is rendered in a designated "unassigned" row at the bottom of the view.

## Stack

A perpendicular-to-time sub-slot used when two or more events share time. The engine's internal overlap concept, produced by `EventLayoutEngine`'s sweep-line algorithm — distinct from the public [[Lane]] (which names a TimelineView row).

In Day/Week views, stacks are sub-columns within a day column. In TimelineView, stacks are sub-rows within a lane's row. Stack assignment is **sweep-line**: each event takes the lowest-numbered stack slot not currently occupied at its start, and releases that slot at its end. Other events can reuse the slot afterward.

The `StackCount` exposed on `PositionedEvent` is the **maximum concurrent overlap that occurs during this specific event's lifetime**, not a count across a transitive overlap group. An event A that overlaps B (which also overlaps C, but A and C do not overlap) renders at 50% width, not 33%. See [[ADR-0003]].

## Overlap group

An informal term for "events that visually compete for stack space at some instant." Not a stored or computed structure — the sweep-line algorithm never materializes a group. Avoid this term in code; prefer "concurrent at instant T" or "max concurrency during E's lifetime."

## TimelineView

The fourth view: rows = lanes, X-axis = time. Scales via [[Timeline scale]] (Day / Week / Month). Distinct from Day/Week/Month, which all have time on the Y-axis. The same sweep-line layout primitives are reused internally; only the axis interpretation differs.

Supersedes the Phase 1 "ResourceView" framing; the rename is per [[ADR-0011]].

## Timeline scale

A property of [[TimelineView]]: `Day` (24 hours across the X-axis with slot granularity), `Week` (7 days across), or `Month` (~30 days across). Determines tick formatting and the granularity of `OnSlotClicked` payloads. Orthogonal to [[Anchor date]] — anchor still picks *which* day/week/month is shown.

## Event filter

A `Func<TEvent, bool>?` parameter on every view. Pre-filters events before layout. Distinct from [[TimelineView]]'s grouping: filtering means "drop events that don't match"; grouping means "lay events out under their lane row." Use a filter to show "just driver A's Week view"; use TimelineView to show "all drivers' day at a glance."
