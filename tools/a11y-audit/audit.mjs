// Calee.Scheduler — automated WCAG 2.2 AA audit (NFR-06, PRD Task 14; raised
// from 2.1 to 2.2 AA per issue #12).
//
// Boots a headless Chromium via Playwright, visits every demo route, and runs
// axe-core against the rendered DOM. Writes the structured findings to
// report.json and exits non-zero if any violation is found.
//
// Issue #19 — per-route axe checks are followed by a live-browser roving-
// tabindex focus check on the routes that carry a roving-tabindex grid/list
// (Day, Week, Month, Timeline/"fleet", Agenda). bUnit's headless DOM cannot
// exercise real browser focus (see RovingTabindexTests.cs's remarks and
// README §9.1a), so this script is the only place that verifies arrow-key
// navigation actually moves `document.activeElement` — not just the
// `tabindex` attribute — onto the newly-active cell/row.
//
// This script intentionally lives outside the .NET solution — the library has
// no Node dependency. To run:
//
//   1. Start the demo:   dotnet run --project Calee.Scheduler.Demo --urls http://localhost:5092
//   2. In another shell: cd tools/a11y-audit && npm install && npm run audit
//
// If the demo is not reachable the script fails fast with a clear message.

import { chromium } from 'playwright';
import AxeBuilder from '@axe-core/playwright';
import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

const BASE_URL = process.env.CALEE_DEMO_URL ?? 'http://localhost:5092';

// Roving-tabindex cell/row selector shared with calee-scheduler.js's
// focusActiveGridCell — the roving cell always carries BOTH the role AND the
// tabindex="0" attribute on the same element (see that function's doc comment
// for why this excludes always-focusable event chips).
const ROVING_CELL_SELECTOR = '[role="gridcell"][tabindex="0"], [role="listitem"][tabindex="0"]';

// The demo routes. Each entry pairs a route with a CSS selector that, once
// present in the DOM, indicates the scheduler has rendered. The selector list
// covers the composed root plus one dedicated page per view. Routes that carry
// a roving-tabindex grid/list also declare `focusCheck.keys` — the arrow-key
// sequence driven into the grid after axe passes, to verify issue #19's fix
// (Year is out of scope for issue #19 — see MANUAL-CHECKLIST.md).
const ROUTES = [
    { path: '/',       waitFor: '[data-calee-region="scheduler"], [data-calee-region="day-header"], [role="grid"]' },
    { path: '/day',    waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowDown', 'ArrowDown', 'ArrowDown'] } },
    { path: '/week',   waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowRight', 'ArrowDown', 'ArrowRight'] } },
    { path: '/month',  waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowRight', 'ArrowDown'] } },
    { path: '/year',   waitFor: '[data-calee-region="year"]' },
    { path: '/agenda', waitFor: '[data-calee-region="agenda"]', focusCheck: { keys: ['ArrowDown', 'ArrowDown'] } },
    { path: '/fleet',  waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowRight', 'ArrowDown'] } },
];

/**
 * Issue #19 live-browser focus check. Focuses the route's initial roving cell
 * (the one carrying `tabindex="0"` on page load), drives the supplied arrow-key
 * sequence, and asserts `document.activeElement` matches
 * `ROVING_CELL_SELECTOR` afterward — i.e., that a real DOM element actually
 * received focus, not just that some cell's `tabindex` attribute flipped to
 * `"0"`. Returns `{ pass, detail }`; never throws — a missing initial cell or a
 * Playwright timeout is reported as a failure, not a crashed audit run.
 */
async function checkRovingFocus(page, keys) {
    try {
        const initial = page.locator(ROVING_CELL_SELECTOR).first();
        await initial.waitFor({ state: 'attached', timeout: 5000 });
        await initial.focus();

        for (const key of keys) {
            await page.keyboard.press(key);
        }

        const matched = await page.evaluate((sel) => {
            const active = document.activeElement;
            return !!active && active.matches(sel);
        }, ROVING_CELL_SELECTOR);

        return matched
            ? { pass: true, detail: `document.activeElement followed ${keys.join(', ')}` }
            : { pass: false, detail: `document.activeElement did NOT move after ${keys.join(', ')} — roving cell's tabindex may have updated without real focus (issue #19 regression)` };
    } catch (err) {
        return { pass: false, detail: `focus check threw: ${err.message}` };
    }
}

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];

async function probeServer() {
    try {
        // Blazor Server's middleware rejects HEAD on the page route (405), so use GET.
        const res = await fetch(BASE_URL + '/', { method: 'GET' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (err) {
        console.error(`\n[FAIL] Demo not reachable at ${BASE_URL}.`);
        console.error(`       Start it first:  dotnet run --project Calee.Scheduler.Demo --urls ${BASE_URL}`);
        console.error(`       (Underlying error: ${err.message})\n`);
        process.exit(2);
    }
}

async function auditOnce() {
    await probeServer();

    const browser = await chromium.launch();
    // Emulate prefers-reduced-motion so the demo's entrance fade is skipped. axe
    // must sample text at its final opacity — a foreground caught mid-fade is
    // partially transparent, blends toward the background, and fails contrast
    // checks even when the settled colors pass (NFR-06).
    const context = await browser.newContext({ reducedMotion: 'reduce' });
    const page = await context.newPage();

    const results = [];

    for (const route of ROUTES) {
        const url = BASE_URL + route.path;
        process.stdout.write(`auditing ${route.path} ... `);

        try {
            await page.goto(url, { waitUntil: 'networkidle', timeout: 15000 });
            await page.waitForSelector(route.waitFor, { timeout: 10000 });
        } catch (err) {
            console.log('FAIL (load)');
            results.push({ route: route.path, error: err.message, violations: [] });
            continue;
        }

        const axeResult = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();

        const violations = axeResult.violations.map(v => ({
            id: v.id,
            impact: v.impact,
            help: v.help,
            helpUrl: v.helpUrl,
            tags: v.tags,
            nodes: v.nodes.map(n => ({
                target: n.target,
                html: n.html.slice(0, 240),
                failureSummary: n.failureSummary,
            })),
        }));

        console.log(violations.length === 0 ? 'PASS' : `${violations.length} violation(s)`);

        // Issue #19 — after axe passes for the route, drive the roving-tabindex
        // focus check (only on routes that declare one — see ROUTES above).
        let focusCheck;
        if (route.focusCheck) {
            process.stdout.write(`  focus-check ${route.path} (${route.focusCheck.keys.join(', ')}) ... `);
            focusCheck = await checkRovingFocus(page, route.focusCheck.keys);
            console.log(focusCheck.pass ? 'PASS' : `FAIL — ${focusCheck.detail}`);
        }

        results.push({ route: route.path, violations, ...(focusCheck ? { focusCheck } : {}) });
    }

    await browser.close();

    const focusFailures = results.filter(r => r.focusCheck && !r.focusCheck.pass).length;

    const report = {
        timestamp: new Date().toISOString(),
        baseUrl: BASE_URL,
        wcagTags: WCAG_TAGS,
        routes: results,
        totalViolations: results.reduce((acc, r) => acc + (r.violations?.length ?? 0), 0),
        focusChecksRun: results.filter(r => r.focusCheck).length,
        focusFailures,
    };

    const outPath = join(__dirname, 'report.json');
    await writeFile(outPath, JSON.stringify(report, null, 2), 'utf8');
    console.log(`\nreport written to ${outPath}`);
    console.log(`total violations: ${report.totalViolations}`);
    console.log(`focus checks: ${report.focusChecksRun} run, ${focusFailures} failed`);

    if (report.totalViolations > 0 || focusFailures > 0 || results.some(r => r.error)) {
        process.exit(1);
    }
}

auditOnce().catch(err => {
    console.error('\n[FATAL]', err);
    process.exit(3);
});
