# Calee.Scheduler — accessibility audit harness

Automated WCAG 2.2 AA audit (NFR-06; raised from 2.1 to 2.2 AA per issue #12)
for the demo app. Uses Playwright + axe-core.

This tooling lives **outside** the .NET solution on purpose: the library and
unit tests have no Node dependency, and the audit never runs as part of
`dotnet build` or `dotnet test`. It is run once per release and any time the
demo, the components, or the default theme change.

It also runs in CI on every push to `main` and every pull request — see
[`.github/workflows/a11y.yml`](../../.github/workflows/a11y.yml). A failed
audit there blocks the workflow; download the `a11y-report` artifact from the
workflow run to triage.

## Prerequisites

- Node 18+ (tested on Node 22).
- The demo app running locally at `http://localhost:5092` (overridable via the
  `CALEE_DEMO_URL` environment variable).

## Install & run

```bash
# From the repo root, start the demo:
dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092

# In another shell:
cd tools/a11y-audit
npm install
npx playwright install chromium   # first run only — downloads the browser
npm run audit
```

The script writes a structured report to `report.json` in this directory and
exits non-zero if any WCAG 2.2 AA violation is found.

## What it checks

For each of the seven demo routes (`/`, `/day`, `/week`, `/month`, `/year`,
`/agenda`, `/fleet`):

1. Loads the page and waits for the scheduler to render.
2. Runs `axe-core` with the WCAG 2.1 + 2.2 A and AA rule set
   (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22a`, `wcag22aa`).
   Requires axe-core ≥4.10 for the 2.2 rules (this repo pins
   `@axe-core/playwright` `^4.10.0`, currently resolving to 4.11.x, which
   already satisfies that floor — no dependency bump was needed for issue #12).
   As of axe-core 4.11, the only rule newly in scope from the 2.2 tags is
   `target-size` (SC 2.5.8) — axe has no automated check for 2.5.7 (dragging
   movements) or 2.4.11 (focus not obscured); those are manual-only, see
   [`MANUAL-CHECKLIST.md`](./MANUAL-CHECKLIST.md) §8.
3. Collects every violation (id, impact, helpUrl, offending selector + HTML
   snippet) into the report.

## What it does NOT check

- **Screen-reader announcements (NVDA / VoiceOver).** Those require a Windows
  or macOS GUI session plus a human listening. See [`MANUAL-CHECKLIST.md`](./MANUAL-CHECKLIST.md)
  for the manual smoke-test script.
- **Default-theme contrast ratios.** Those are regression-tested directly in
  the .NET test suite — see
  `Calee.Scheduler.Tests/Accessibility/DefaultThemeContrastTests.cs`.
- **Roving-tabindex correctness.** Asserted in bUnit tests — see
  `Calee.Scheduler.Tests/Accessibility/RovingTabindexTests.cs`. Note: those
  tests assert the `tabindex` *attribute* transfers correctly on keydown;
  they run against bUnit's headless DOM and cannot assert that real browser
  focus follows — see `MANUAL-CHECKLIST.md`'s top-of-file banner for a known
  gap this surfaced.
- **WCAG 2.2 SC 2.5.7 (dragging movements) and SC 2.4.11 (focus not obscured).**
  Neither is mechanically checkable by axe-core as of 4.11 — both are manual
  spot-checks, see `MANUAL-CHECKLIST.md` §8.1 / §8.2.

## Output

`report.json` shape:

```jsonc
{
  "timestamp": "...",
  "baseUrl": "http://localhost:5092",
  "wcagTags": ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"],
  "routes": [
    { "route": "/",       "violations": [] },
    { "route": "/day",    "violations": [] },
    { "route": "/week",   "violations": [] },
    { "route": "/month",  "violations": [] },
    { "route": "/year",   "violations": [] },
    { "route": "/agenda", "violations": [] },
    { "route": "/fleet",  "violations": [] }
  ],
  "totalViolations": 0
}
```

A violation entry includes the axe rule id, impact (minor / moderate /
serious / critical), a help URL pointing to deque.com, and each offending
node's selector + 240-char HTML snippet.
