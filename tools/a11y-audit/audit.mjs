// Calee.Scheduler — automated WCAG 2.1 AA audit (NFR-06, PRD Task 14).
//
// Boots a headless Chromium via Playwright, visits every demo route, and runs
// axe-core against the rendered DOM. Writes the structured findings to
// report.json and exits non-zero if any violation is found.
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

// The demo routes. Each entry pairs a route with a CSS selector that, once
// present in the DOM, indicates the scheduler has rendered. The selector list
// covers the composed root plus one dedicated page per view.
const ROUTES = [
    { path: '/',       waitFor: '[data-calee-region="scheduler"], [data-calee-region="day-header"], [role="grid"]' },
    { path: '/day',    waitFor: '[role="grid"]' },
    { path: '/week',   waitFor: '[role="grid"]' },
    { path: '/month',  waitFor: '[role="grid"]' },
    { path: '/year',   waitFor: '[data-calee-region="year"]' },
    { path: '/agenda', waitFor: '[data-calee-region="agenda"]' },
    { path: '/fleet',  waitFor: '[role="grid"]' },
];

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

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
        results.push({ route: route.path, violations });
    }

    await browser.close();

    const report = {
        timestamp: new Date().toISOString(),
        baseUrl: BASE_URL,
        wcagTags: WCAG_TAGS,
        routes: results,
        totalViolations: results.reduce((acc, r) => acc + (r.violations?.length ?? 0), 0),
    };

    const outPath = join(__dirname, 'report.json');
    await writeFile(outPath, JSON.stringify(report, null, 2), 'utf8');
    console.log(`\nreport written to ${outPath}`);
    console.log(`total violations: ${report.totalViolations}`);

    if (report.totalViolations > 0 || results.some(r => r.error)) {
        process.exit(1);
    }
}

auditOnce().catch(err => {
    console.error('\n[FATAL]', err);
    process.exit(3);
});
