# AGENTS.md — Calee.Scheduler

## Project identity

Blazor RCL (`Microsoft.NET.Sdk.Razor`) for scheduling UI — Day, Week, Month, Year, Agenda, Timeline views.
Published to **nuget.org** (public). Single package: `Calee.Scheduler`.
.NET 10 (`net10.0`), targeting `browser`. SDK pinned via `global.json` → `10.0.300`.

## Build, test, format

```bash
dotnet restore Calee.Scheduler.sln
dotnet build Calee.Scheduler.sln -warnaserror        # CI uses --configuration Release
dotnet test Calee.Scheduler.sln                       # xunit.v3 + bUnit
dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092
```

Tests are `xunit.v3` with `bunit` for Blazor component testing. No real browser focus or pixel geometry in bUnit — use the Playwright audit for that.

`dotnet format --verify-no-changes` on the whole solution is **dirty on main** (pre-existing whitespace drift in Agenda/Year view .cs files) — always scope format checks to touched files.

## Project layout

| Directory | Role |
|---|---|
| `Calee.Scheduler/` | RCL: `Components/` (7 views + toolbar), `Contracts/` (public types), `Internal/` (private), `Extensions/` (DI), `wwwroot/` (JS interop) |
| `Calee.Scheduler.Tests/` | xunit.v3 + bUnit tests. `InternalsVisibleTo` granted. |
| `Calee.Scheduler.Demo/` | Blazor WASM demo app. Reference seed data shape. Consumer-owned dialogs/undo live here, NOT in the RCL. |
| `docs/adr/` | Architecture Decision Records (gitignored — local-only, present in primary checkout, absent in worktrees). |
| `tools/a11y-audit/` | Playwright + axe-core audit. `npm install && npm run audit`. Requires a running demo. |

## Key design invariants (do not violate)

- **Generic-only `TEvent`** — no non-generic sugar wrapper exists or will exist (ADR-0004).
- **Library owns the rectangle; consumer owns the inside** — templates (`EventTemplate`, `EventChipTemplate`, `DayHeaderTemplate`) fill library-positioned containers (ADR-0002).
- **No timezone conversion on events** — `TimeZone` parameter only governs "today", day boundaries, and `SchedulerSlot` offsets. Events render at their literal `DateTimeOffset` (ADR-0001).
- **Fail-closed interactions** — drag/create/move/resize APIs supply `Cancel` flags; library reverts on cancel.
- **1.x source-stable** — additive API changes only. No renames or removals.
- **WCAG 2.2 AA** — verified by automated axe-core audit + manual checklist. Contrast regression-tested.
- **No dialogs, no recurrence, no data fetching** — the RCL is pure UI + callbacks. Consumer owns CRUD, confirmations, undo.
- **Per-day hooks take midnight in grid TZ** — `DayModifier`, `DayHeaderTemplate` both receive `DateTimeOffset` at day-midnight in the grid's `TimeZone`. Keep that idiom for any new per-day surface.

## CS1591 XML doc enforcement

`GenerateDocumentationFile=true` in the RCL csproj. All public members need XML docs or the build breaks (suppressed only for `RZ2012` — see csproj comment).

## Testing quirks

- bUnit cannot test real browser focus or pixel geometry. Bug #19 (arrow-key roving tabindex not moving real focus) went undetected for months because of this.
- **Always add live-browser assertions** (Playwright audit) for anything focus- or geometry-related.
- Test commands: `dotnet test` (all), `dotnet test --filter "FullyQualifiedName~ClassName"` (single class).
- Demo has 7 routes: `/`, `/day`, `/week`, `/month`, `/year`, `/agenda`, `/fleet`.

## Live audit (Playwright + axe-core)

```bash
# Terminal 1
dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092
# Terminal 2
node tools/a11y-audit/audit.mjs
```

Exits non-zero on any WCAG 2.2 AA violation. Output at `tools/a11y-audit/report.json`.

## CI

4 workflows in `.github/workflows/`:
- **ci** — restore, build `-warnaserror` (Release), test, pack. On every push/PR to `main`.
- **a11y** — boots demo, runs axe-core Playwright audit. Fails on WCAG 2.2 AA violations.
- **release** — on `v*` tag push. Packs and publishes to nuget.org. Needs `NUGET_API_KEY` secret.
- **pages** — deploys demo WASM to GitHub Pages on push to `main`.

## Domain glossary

`CONTEXT.md` is the authoritative domain language. Key terms: Event, Event chunk, Grid time zone, Today, Anchor date, Lane, LaneKey, Stack, TimelineView, Timeline scale, Event filter.

## Known gotchas

- `gh pr create` GraphQL 401 is a **tyler-technologies org** quirk. This is a personal repo — `gh pr create` works fine.
- `docs/` and `docs/adr/` are **gitignored**. Read ADRs from the primary checkout path, not from worktrees.
- Worktree cleanup after merge: `git worktree remove <path>; git branch -D <branch>; git worktree prune; git fetch --prune; git rebase origin/main`.
- Don't `git push` to main; don't `git reset --hard`. Both are blocked by the local permission classifier.
- `Calee.Scheduler.Demo` is WASM — no server-side rendering. `Microsoft.AspNetCore.Components.WebAssembly`.
