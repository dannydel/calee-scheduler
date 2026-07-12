import { chromium } from 'playwright';

const baseUrl = process.env.CALEE_DEMO_URL ?? 'http://localhost:5092';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });

for (const lanes of [250, 1_000]) {
    const started = performance.now();
    await page.goto(`${baseUrl}/fleet?lanes=${lanes}`, { waitUntil: 'networkidle', timeout: 30_000 });
    await page.waitForSelector('[data-calee-region="lane-row"]', { timeout: 10_000 });
    await page.waitForTimeout(100);
    const metrics = await page.evaluate(() => ({
        mountedRows: document.querySelectorAll('[data-calee-region="lane-row"]').length,
        slotCells: document.querySelectorAll('.calee-scheduler-timeline-slot').length,
        eventButtons: document.querySelectorAll('[data-calee-region="event"]').length,
        timelineElements: document.querySelectorAll('.calee-scheduler-timeline *').length,
    }));
    console.log(JSON.stringify({ lanes, pageReadyMs: Math.round(performance.now() - started), ...metrics }));
}

await browser.close();
