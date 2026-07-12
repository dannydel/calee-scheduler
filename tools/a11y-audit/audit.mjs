// Calee.Scheduler — automated WCAG 2.2 AA audit (NFR-06, PRD Task 14; raised
// from 2.1 to 2.2 AA per issue #12).
//
// Boots a headless Chromium via Playwright, visits every demo route, and runs
// axe-core against the rendered DOM. Writes the structured findings to
// report.json and exits non-zero if any violation is found.
//
// Issue #19 — per-route axe checks are followed by a live-browser roving-
// tabindex focus check on the routes that carry a roving-tabindex grid/list
// (Day, Week, Month, Year, Timeline/"fleet", Agenda). bUnit's headless DOM cannot
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
// sequence driven into the grid after axe passes, to verify issue #19's fix.
// Year's arrow keys move the anchor within a month matrix (ArrowDown +7,
// ArrowRight +1); leading with ArrowDown guarantees a real move from the
// initial row-0 cell regardless of which weekday the 1st falls on.
const ROUTES = [
    { path: '/',       waitFor: '[data-calee-region="scheduler"], [data-calee-region="day-header"], [role="grid"]' },
    { path: '/day',    waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowDown', 'ArrowDown', 'ArrowDown'] } },
    { path: '/week',   waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowRight', 'ArrowDown', 'ArrowRight'] } },
    { path: '/month',  waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowRight', 'ArrowDown'] } },
    { path: '/year',   waitFor: '[role="grid"]', focusCheck: { keys: ['ArrowDown', 'ArrowRight', 'ArrowDown'] } },
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

// Issue #10 — the audit runs at two viewports. Desktop is the historical pass
// (1280×720) and owns the roving-tabindex focus checks (viewport-independent, so
// they only need to run once). Mobile (390×844, the AC target width) additionally
// asserts there is no horizontal *page* overflow: at that width Week/Timeline
// scroll their grids internally, but the document element must never grow wider
// than the viewport (WCAG 2.2 SC 1.4.10 reflow + the issue's explicit AC).
const VIEWPORTS = [
    { name: 'desktop', viewport: { width: 1280, height: 720 }, runFocusChecks: true, checkOverflow: false },
    { name: 'mobile', viewport: { width: 390, height: 844 }, runFocusChecks: false, checkOverflow: true },
];

/**
 * Measures whether the document overflows the viewport horizontally. A small
 * 1px tolerance absorbs sub-pixel rounding. Returns `{ overflow, scrollWidth,
 * clientWidth }`; `overflow: true` means the page (not an internal scroll
 * region) is wider than the viewport — a hard fail at mobile.
 */
async function checkNoPageOverflow(page) {
    const m = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
    }));
    return { overflow: m.scrollWidth > m.clientWidth + 1, ...m };
}

/**
 * Issue #38 — a Month bar title must not outgrow its rendered segment. The
 * long-title fixture is measured in the live browser because bUnit cannot
 * resolve flex sizing, clipping, or ellipsis geometry.
 */
async function checkMonthBarLabelContainment(page) {
    return page.evaluate(() => {
        const tolerance = 0.5;
        const failures = [...document.querySelectorAll('.calee-scheduler-month-bar')]
            .map((bar) => {
                const title = bar.querySelector('.calee-scheduler-month-bar-title');
                if (!title) return 'bar has no title element';

                const barRect = bar.getBoundingClientRect();
                const titleRect = title.getBoundingClientRect();
                const barStyle = getComputedStyle(bar);
                const titleStyle = getComputedStyle(title);
                const titleEscapes = titleRect.left < barRect.left - tolerance
                    || titleRect.right > barRect.right + tolerance;
                const hasExternalMargins = Math.abs(parseFloat(barStyle.marginLeft)) > tolerance
                    || Math.abs(parseFloat(barStyle.marginRight)) > tolerance;

                if (titleEscapes) return `title geometry escapes bar (${titleRect.left.toFixed(1)}-${titleRect.right.toFixed(1)} vs ${barRect.left.toFixed(1)}-${barRect.right.toFixed(1)})`;
                if (titleStyle.minWidth !== '0px') return `title min-width is ${titleStyle.minWidth}, expected 0px`;
                if (hasExternalMargins) return `bar has external horizontal margins (${barStyle.marginLeft}, ${barStyle.marginRight})`;
                return null;
            })
            .filter(Boolean);

        return { pass: failures.length === 0, detail: failures.join('; ') || 'all Month bar titles are contained' };
    });
}

async function auditOnce() {
    await probeServer();

    const browser = await chromium.launch();
    const results = [];

    for (const vp of VIEWPORTS) {
        // Emulate prefers-reduced-motion so the demo's entrance fade is skipped. axe
        // must sample text at its final opacity — a foreground caught mid-fade is
        // partially transparent, blends toward the background, and fails contrast
        // checks even when the settled colors pass (NFR-06). A fresh context per
        // viewport applies the width cleanly.
        const context = await browser.newContext({ reducedMotion: 'reduce', viewport: vp.viewport });
        const page = await context.newPage();

        console.log(`\n=== viewport: ${vp.name} (${vp.viewport.width}×${vp.viewport.height}) ===`);

        for (const route of ROUTES) {
            const url = BASE_URL + route.path;
            process.stdout.write(`auditing ${route.path} ... `);

            try {
                await page.goto(url, { waitUntil: 'networkidle', timeout: 15000 });
                await page.waitForSelector(route.waitFor, { timeout: 10000 });
            } catch (err) {
                console.log('FAIL (load)');
                results.push({ viewport: vp.name, route: route.path, error: err.message, violations: [] });
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

            // Issue #10 — mobile page-overflow assertion.
            let overflowCheck;
            if (vp.checkOverflow) {
                process.stdout.write(`  overflow-check ${route.path} ... `);
                overflowCheck = await checkNoPageOverflow(page);
                console.log(overflowCheck.overflow
                    ? `FAIL — page scrollWidth ${overflowCheck.scrollWidth} > clientWidth ${overflowCheck.clientWidth}`
                    : 'PASS');
            }

            // Issue #19 — after axe passes, drive the roving-tabindex focus check
            // (desktop only; viewport-independent, and routes must declare one).
            let focusCheck;
            if (vp.runFocusChecks && route.focusCheck) {
                process.stdout.write(`  focus-check ${route.path} (${route.focusCheck.keys.join(', ')}) ... `);
                focusCheck = await checkRovingFocus(page, route.focusCheck.keys);
                console.log(focusCheck.pass ? 'PASS' : `FAIL — ${focusCheck.detail}`);
            }

            let monthBarLabelCheck;
            if (route.path === '/month') {
                process.stdout.write(`  month-bar-label-check ${route.path} ... `);
                monthBarLabelCheck = await checkMonthBarLabelContainment(page);
                console.log(monthBarLabelCheck.pass ? 'PASS' : `FAIL — ${monthBarLabelCheck.detail}`);
                if (!monthBarLabelCheck.pass) {
                    await page.screenshot({
                        path: join(__dirname, `issue-38-month-label-failure-${vp.name}.png`),
                        fullPage: true,
                    });
                }
            }

            results.push({
                viewport: vp.name,
                route: route.path,
                violations,
                ...(focusCheck ? { focusCheck } : {}),
                ...(overflowCheck ? { overflowCheck } : {}),
                ...(monthBarLabelCheck ? { monthBarLabelCheck } : {}),
            });
        }

        await context.close();
    }

    await browser.close();

    const focusFailures = results.filter(r => r.focusCheck && !r.focusCheck.pass).length;
    const overflowFailures = results.filter(r => r.overflowCheck && r.overflowCheck.overflow).length;
    const monthBarLabelFailures = results.filter(r => r.monthBarLabelCheck && !r.monthBarLabelCheck.pass).length;

    const report = {
        timestamp: new Date().toISOString(),
        baseUrl: BASE_URL,
        wcagTags: WCAG_TAGS,
        viewports: VIEWPORTS.map(v => v.name),
        routes: results,
        totalViolations: results.reduce((acc, r) => acc + (r.violations?.length ?? 0), 0),
        focusChecksRun: results.filter(r => r.focusCheck).length,
        focusFailures,
        overflowChecksRun: results.filter(r => r.overflowCheck).length,
        overflowFailures,
        monthBarLabelChecksRun: results.filter(r => r.monthBarLabelCheck).length,
        monthBarLabelFailures,
    };

    const outPath = join(__dirname, 'report.json');
    await writeFile(outPath, JSON.stringify(report, null, 2), 'utf8');
    console.log(`\nreport written to ${outPath}`);
    console.log(`total violations: ${report.totalViolations}`);
    console.log(`focus checks: ${report.focusChecksRun} run, ${focusFailures} failed`);
    console.log(`overflow checks: ${report.overflowChecksRun} run, ${overflowFailures} failed`);
    console.log(`Month bar label checks: ${report.monthBarLabelChecksRun} run, ${monthBarLabelFailures} failed`);

    if (report.totalViolations > 0 || focusFailures > 0 || overflowFailures > 0 || monthBarLabelFailures > 0 || results.some(r => r.error)) {
        process.exit(1);
    }
}

// Keyboard interaction smoke tests for SC 2.5.7 (issue #20). Exercises the
// keyboard move/resize alternatives against the live demo Day page.
async function auditKeyboardInteractions() {
    await probeServer();

    const browser = await chromium.launch();
    const context = await browser.newContext({ reducedMotion: 'reduce' });
    const page = await context.newPage();
    let failures = [];

    try {
        // -- Day view: keyboard resize (Shift+ArrowUp) --
        process.stdout.write('keyboard resize (Day) ... ');
        await page.goto(BASE_URL + '/day', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[role="button"][data-calee-region="event"]', { timeout: 10000 });

        // Tab to the first event chip
        await page.keyboard.press('Tab');
        // Shift+ArrowUp to extend the event End by one slot
        await page.keyboard.press('Shift+ArrowUp');
        await page.waitForTimeout(300);

        // Verify no crash — the page should still render events
        const eventsAfterResize = await page.$$eval('[role="button"][data-calee-region="event"]', els => els.length);
        if (eventsAfterResize === 0) {
            failures.push('Day keyboard resize: no event chips visible after resize');
            console.log('FAIL');
        } else {
            console.log('PASS');
        }

        // -- Day view: keyboard move (m → ArrowDown → Enter) --
        process.stdout.write('keyboard move (Day) ... ');
        await page.goto(BASE_URL + '/day', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[role="button"][data-calee-region="event"]', { timeout: 10000 });
        await page.keyboard.press('Tab');
        await page.keyboard.press('m');       // enter move mode
        await page.keyboard.press('ArrowDown'); // move down one slot
        await page.keyboard.press('Enter');     // commit
        await page.waitForTimeout(300);

        const eventsAfterMove = await page.$$eval('[role="button"][data-calee-region="event"]', els => els.length);
        if (eventsAfterMove === 0) {
            failures.push('Day keyboard move: no event chips visible after move');
            console.log('FAIL');
        } else if (failures.length === 0) {
            console.log('PASS');
        }

        // -- Month view: keyboard move (m → ArrowRight → Enter) --
        process.stdout.write('keyboard move (Month) ... ');
        await page.goto(BASE_URL + '/month', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[role="button"][data-calee-region="event"]', { timeout: 10000 });
        await page.keyboard.press('Tab');
        await page.keyboard.press('m');         // enter move mode
        await page.keyboard.press('ArrowRight'); // move right one cell
        await page.keyboard.press('Enter');      // commit
        await page.waitForTimeout(300);

        const eventsAfterMonthMove = await page.$$eval('[role="button"][data-calee-region="event"]', els => els.length);
        if (eventsAfterMonthMove === 0) {
            failures.push('Month keyboard move: no event chips visible after move');
            console.log('FAIL');
        } else {
            console.log('PASS');
        }

        // -- Month view: keyboard move cancel (m → ArrowRight → Escape) --
        process.stdout.write('keyboard move cancel (Month) ... ');
        await page.goto(BASE_URL + '/month', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[role="button"][data-calee-region="event"]', { timeout: 10000 });
        await page.keyboard.press('Tab');
        await page.keyboard.press('m');         // enter move mode
        await page.keyboard.press('ArrowRight'); // move right one cell
        await page.keyboard.press('Escape');     // cancel
        await page.waitForTimeout(300);

        const eventsAfterCancel = await page.$$eval('[role="button"][data-calee-region="event"]', els => els.length);
        if (eventsAfterCancel === 0) {
            failures.push('Month keyboard move cancel: no event chips visible after cancel');
            console.log('FAIL');
        } else {
            console.log('PASS');
        }

        // -- Month view: pointer drag-to-move --
        process.stdout.write('pointer drag (Month) ... ');
        await page.goto(BASE_URL + '/month', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[data-calee-drag-handle="move"]', { timeout: 10000 });
        
        const chip = await page.$('[data-calee-drag-handle="move"]');
        if (chip) {
            const box = await chip.boundingBox();
            if (box) {
                // Drag 100px to the right (1 cell)
                await chip.hover();
                await page.mouse.down();
                await page.mouse.move(box.x + box.width + 100, box.y, { steps: 10 });
                await page.mouse.up();
                await page.waitForTimeout(300);

                const eventsAfterDrag = await page.$$eval('[role="button"][data-calee-region="event"]', els => els.length);
                if (eventsAfterDrag === 0) {
                    failures.push('Month pointer drag: no event chips visible after drag');
                    console.log('FAIL');
                } else {
                    console.log('PASS');
                }
            } else {
                failures.push('Month pointer drag: could not get chip bounding box');
                console.log('FAIL');
            }
        } else {
            failures.push('Month pointer drag: no draggable chip found');
            console.log('FAIL');
        }

        // -- Agenda view: keyboard move (m → ArrowDown → Enter), issue #30 --
        process.stdout.write('keyboard move (Agenda) ... ');
        await page.goto(BASE_URL + '/agenda', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[data-calee-region="agenda-row"]', { timeout: 10000 });

        // Focus the first row directly rather than pressing Tab — unlike Day/Month
        // (no page furniture before the grid), the Agenda page has a toolbar + window-
        // length radios ahead of the list, so a single Tab lands on those, not a row.
        await page.locator('[data-calee-region="agenda-row"]').first().focus();
        const movedEventId = await page.evaluate(() =>
            document.activeElement?.getAttribute('data-calee-event-id') ?? null);
        const agendaGroupDateOf = async (id) => page.evaluate((eventId) => {
            const row = document.querySelector(`[data-calee-event-id="${eventId}"]`);
            return row?.closest('[data-calee-region="agenda-group"]')?.getAttribute('data-calee-date') ?? null;
        }, id);
        const groupDateBeforeMove = await agendaGroupDateOf(movedEventId);

        await page.keyboard.press('m');         // enter move mode
        await page.keyboard.press('ArrowDown'); // retarget the date cursor one day forward
        await page.keyboard.press('Enter');     // commit
        await page.waitForTimeout(300);

        const rowsAfterAgendaMove = await page.$$('[data-calee-region="agenda-row"]');
        const groupDateAfterMove = await agendaGroupDateOf(movedEventId);
        if (rowsAfterAgendaMove.length === 0) {
            failures.push('Agenda keyboard move: no rows visible after move');
            console.log('FAIL');
        } else if (!movedEventId || groupDateAfterMove === groupDateBeforeMove) {
            failures.push(`Agenda keyboard move: moved row's date group unchanged (${groupDateBeforeMove} -> ${groupDateAfterMove})`);
            console.log('FAIL');
        } else {
            console.log('PASS');
        }

        // -- Agenda view: keyboard move focus retention (issue #19-class check
        //    bUnit cannot do — see README §9.1a / AGENTS.md testing quirks) --
        process.stdout.write('keyboard move focus retention (Agenda) ... ');
        await page.goto(BASE_URL + '/agenda', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[data-calee-region="agenda-row"]', { timeout: 10000 });

        await page.locator('[data-calee-region="agenda-row"]').first().focus();
        const focusedEventId = await page.evaluate(() =>
            document.activeElement?.getAttribute('data-calee-event-id') ?? null);

        await page.keyboard.press('m');
        await page.keyboard.press('ArrowDown');
        await page.waitForTimeout(200);

        const activeElementMatches = (id) => page.evaluate((eventId) => {
            const active = document.activeElement;
            return !!active
                && active.getAttribute('role') === 'listitem'
                && active.getAttribute('tabindex') === '0'
                && active.getAttribute('data-calee-event-id') === eventId;
        }, id);

        if (!focusedEventId) {
            failures.push('Agenda keyboard move focus retention: could not resolve the initially-focused row id');
            console.log('FAIL');
        } else if (!(await activeElementMatches(focusedEventId))) {
            failures.push('Agenda keyboard move focus retention: document.activeElement did not follow the moving row across its re-bucket (issue #19-class regression)');
            console.log('FAIL');
        } else {
            await page.keyboard.press('Escape'); // cancel
            await page.waitForTimeout(200);
            if (!(await activeElementMatches(focusedEventId))) {
                failures.push('Agenda keyboard move focus retention: focus did not stay on the row after Escape-cancel');
                console.log('FAIL');
            } else {
                console.log('PASS');
            }
        }

        // -- Agenda view: keyboard move cancel (m → ArrowDown → Escape) --
        process.stdout.write('keyboard move cancel (Agenda) ... ');
        await page.goto(BASE_URL + '/agenda', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[data-calee-region="agenda-row"]', { timeout: 10000 });

        await page.locator('[data-calee-region="agenda-row"]').first().focus();
        const cancelEventId = await page.evaluate(() =>
            document.activeElement?.getAttribute('data-calee-event-id') ?? null);
        const cancelGroupBefore = await agendaGroupDateOf(cancelEventId);

        await page.keyboard.press('m');
        await page.keyboard.press('ArrowDown');
        await page.keyboard.press('Escape');
        await page.waitForTimeout(300);

        const rowsAfterAgendaCancel = await page.$$('[data-calee-region="agenda-row"]');
        const cancelGroupAfter = await agendaGroupDateOf(cancelEventId);
        if (rowsAfterAgendaCancel.length === 0) {
            failures.push('Agenda keyboard move cancel: no rows visible after cancel');
            console.log('FAIL');
        } else if (cancelGroupAfter !== cancelGroupBefore) {
            failures.push(`Agenda keyboard move cancel: row did not return to its original group (${cancelGroupBefore} -> ${cancelGroupAfter})`);
            console.log('FAIL');
        } else {
            console.log('PASS');
        }

        // -- Agenda view: pointer drag-to-move onto a later date group --
        process.stdout.write('pointer drag (Agenda) ... ');
        await page.goto(BASE_URL + '/agenda', { waitUntil: 'networkidle', timeout: 15000 });
        await page.waitForSelector('[data-calee-drag-handle="move"]', { timeout: 10000 });

        const agendaDragRows = await page.$$('[data-calee-drag-handle="move"]');
        const agendaGroupEls = await page.$$('[data-calee-region="agenda-group"]');
        if (agendaDragRows.length > 0 && agendaGroupEls.length > 1) {
            const sourceRow = agendaDragRows[0];
            const targetGroup = agendaGroupEls[agendaGroupEls.length - 1];
            const sourceBox = await sourceRow.boundingBox();
            const targetBox = await targetGroup.boundingBox();
            if (sourceBox && targetBox) {
                const targetX = targetBox.x + targetBox.width / 2;
                const targetY = targetBox.y + targetBox.height / 2;

                await sourceRow.hover();
                await page.mouse.down();
                await page.mouse.move(targetX, targetY, { steps: 10 });
                await page.waitForTimeout(100);

                // Assert the highlight is not only visible but geometrically tracks
                // the target group's box (issue #30 fix: the list needs
                // position:relative or the +scrollTop term drifts the highlight off
                // the target). Require meaningful vertical overlap between the two.
                const highlightState = await page.evaluate(() => {
                    const el = document.querySelector('.calee-scheduler-drop-target-highlight');
                    if (!el) return { visible: false, overlap: 0 };
                    const visible = window.getComputedStyle(el).display !== 'none';
                    const hr = el.getBoundingClientRect();
                    const groups = [...document.querySelectorAll('[data-calee-region="agenda-group"]')];
                    const target = groups[groups.length - 1];
                    if (!target) return { visible, overlap: 0 };
                    const tr = target.getBoundingClientRect();
                    const overlap = Math.max(0, Math.min(hr.bottom, tr.bottom) - Math.max(hr.top, tr.top));
                    const ratio = tr.height > 0 ? overlap / tr.height : 0;
                    return { visible, overlap: ratio };
                });

                await page.mouse.up();
                await page.waitForTimeout(300);

                const rowsAfterAgendaDrag = await page.$$('[data-calee-region="agenda-row"]');
                if (!highlightState.visible) {
                    failures.push('Agenda pointer drag: no drop-target highlight appeared mid-drag');
                    console.log('FAIL');
                } else if (highlightState.overlap < 0.5) {
                    failures.push('Agenda pointer drag: highlight did not track the target group box (overlap ratio ' + highlightState.overlap.toFixed(2) + ' < 0.5 — position:relative regression?)');
                    console.log('FAIL');
                } else if (rowsAfterAgendaDrag.length === 0) {
                    failures.push('Agenda pointer drag: no rows visible after drag');
                    console.log('FAIL');
                } else {
                    console.log('PASS');
                }
            } else {
                failures.push('Agenda pointer drag: could not get bounding boxes for source row / target group');
                console.log('FAIL');
            }
        } else {
            failures.push('Agenda pointer drag: not enough rows/groups visible to exercise a cross-group drag');
            console.log('FAIL');
        }
    } catch (err) {
        failures.push(err.message);
        console.log('FAIL (' + err.message + ')');
    } finally {
        await browser.close();
    }

    if (failures.length > 0) {
        console.log('\nkeyboard interaction failures:');
        for (const f of failures) console.log('  - ' + f);
        process.exit(1);
    }
    console.log('\nall keyboard interaction tests passed');
}

auditOnce().catch(err => {
    console.error('\n[FATAL]', err);
    process.exit(3);
}).then(() => auditKeyboardInteractions());
