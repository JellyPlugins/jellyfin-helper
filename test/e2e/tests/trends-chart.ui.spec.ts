/**
 * Trend chart zoom/pan interaction: the growth chart renders, responds to wheel zoom
 * and drag pan on desktop, and to tap / swipe / pinch on touch devices, without JS errors
 * and without overlapping X-axis labels.
 */
import { test, expect, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';
import { seedGrowthTimeline } from '../setup/seed-timeline.ts';

// The api project runs first (this project dependsOn it) and several api specs call
// GrowthTimeline?forceRefresh=true, which overwrites the cached timeline with a real
// single-day compute. Re-seed the multi-year daily series so the chart has a genuine
// span to zoom and pan across. The controller reads the cache file on every GET, so the
// fresh write is picked up on the next request with no server restart.
test.beforeAll(() => {
  seedGrowthTimeline();
});

// Opens the Trends tab and returns the chart locator, skipping the test if the chart has
// no data yet (a fresh server may not have produced a timeline). The chart needs >= 2 points.
async function openChart(page: Page) {
  await openDashboard(page);
  await switchTab(page, 'trends');
  const chart = page.locator('.trend-chart');
  const empty = page.locator('#trendChartContainer .trend-empty');
  // Wait for either the chart or the empty state to settle.
  await Promise.race([
    chart.waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {}),
    empty.waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {}),
  ]);
  return chart;
}

// Reads the current auto-granularity level from the meta line.
async function currentLevel(page: Page): Promise<string> {
  return (await page.locator('.trend-meta-level').textContent())?.trim() ?? '';
}

// Asserts no two X-axis labels overlap by comparing their rendered bounding boxes (which account
// for text width and text-anchor), not just their x coordinates. Scopes to the x-axis label group
// so it never picks up y-axis tick text, and covers edge labels too (which use start/end anchors,
// so a middle-only selector can be empty when zoomed in).
async function assertNoLabelOverlap(page: Page): Promise<void> {
  const boxes = await page.locator('.trend-chart svg .trend-xlabels text').evaluateAll((nodes) =>
    nodes
      .map((n) => {
        const r = (n as SVGTextElement).getBoundingClientRect();
        return { left: r.left, right: r.right };
      })
      .filter((b) => Number.isFinite(b.left) && Number.isFinite(b.right) && b.right > b.left),
  );
  const sorted = boxes.slice().sort((a, b) => a.left - b.left);
  for (let i = 1; i < sorted.length; i++) {
    expect(
      sorted[i].left - sorted[i - 1].right,
      `labels [${sorted[i - 1].left},${sorted[i - 1].right}] and [${sorted[i].left},${sorted[i].right}] overlap`,
    ).toBeGreaterThanOrEqual(0);
  }
}

test.describe('trend chart desktop zoom/pan', () => {
  test('renders and shows a tooltip on hover', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    await chart.locator('svg').hover({ position: { x: 300, y: 100 } });
    await expect(page.locator('.trend-tooltip')).toHaveClass(/visible/, { timeout: 5_000 });
    await assertNoLabelOverlap(page);
    expect(errors, errors.join('\n')).toHaveLength(0);
  });

  test('wheel zoom-in narrows the window and can refine the level', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const before = await currentLevel(page);
    const order = ['yearly', 'monthly', 'weekly', 'daily'];
    const box = (await chart.boundingBox())!;
    // Zoom in several notches toward the right edge (recent data).
    for (let i = 0; i < 8; i++) {
      await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2);
      await page.mouse.wheel(0, -120);
    }
    // The seeded series spans 2016..now, so the initial level is the coarsest one and eight
    // zoom-in notches must refine it. Poll on that refinement, not on a non-empty string
    // (which passes on the first read and can observe the pre-zoom level, comparing it to itself).
    await expect
      .poll(async () => order.indexOf(await currentLevel(page)), { timeout: 5_000 })
      .toBeGreaterThan(order.indexOf(before));
    await assertNoLabelOverlap(page);
    expect(errors, errors.join('\n')).toHaveLength(0);
  });

  test('drag pans the visible window', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    // One notch keeps the window wide (far from the min-span clamp) with plenty of pan room.
    const box = (await chart.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, -120);
    await expect.poll(async () => await page.locator('.trend-chart svg').count(), { timeout: 2_000 }).toBeGreaterThan(0);

    const labelsBefore = await page.locator('.trend-chart svg .trend-xlabels text').allTextContents();

    // Drag content to the right, which pans the window toward earlier dates and away from the
    // right (newest) domain edge, so clamping cannot swallow the movement.
    await page.mouse.move(box.x + box.width * 0.25, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.75, box.y + box.height / 2, { steps: 12 });
    await page.mouse.up();
    await expect.poll(async () => (await page.locator('.trend-chart svg .trend-xlabels text').allTextContents()).join('|'), { timeout: 2_000 }).not.toBe(labelsBefore.join('|'));

    const labelsAfter = await page.locator('.trend-chart svg .trend-xlabels text').allTextContents();
    expect(labelsAfter.join('|')).not.toBe(labelsBefore.join('|'));
    await assertNoLabelOverlap(page);
    expect(errors, errors.join('\n')).toHaveLength(0);
  });
});

test.describe('trend chart touch gestures', () => {
  test.use({ hasTouch: true, isMobile: true });

  test('tap shows a tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const box = (await chart.boundingBox())!;
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;

    // A single-finger touchStart+touchEnd at the same point is a tap. Dispatched via CDP for a
    // deterministic touch sequence (the same mechanism the pinch test uses). The touchEnd MUST
    // carry the released point so the browser populates event.changedTouches with a single entry -
    // the chart's tap handler bails when changedTouches.length !== 1.
    const client = await page.context().newCDPSession(page);
    await client.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: cx, y: cy }] });
    await client.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [{ x: cx, y: cy }] });

    // Synchronize on the observable outcome (tooltip becomes visible), not a fixed wait.
    await expect(page.locator('.trend-tooltip')).toHaveClass(/visible/, { timeout: 5_000 });
    expect(errors, errors.join('\n')).toHaveLength(0);
  });

  test('pinch zoom refines the level without JS errors', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const before = await currentLevel(page);
    const order = ['yearly', 'monthly', 'weekly', 'daily'];
    const box = (await chart.boundingBox())!;
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;

    // Two-finger pinch-out (fingers moving apart) via CDP touch events => zoom in.
    const client = await page.context().newCDPSession(page);
    async function touch(type: 'touchStart' | 'touchEnd' | 'touchMove' | 'touchCancel', points: Array<{ x: number; y: number }>) {
      await client.send('Input.dispatchTouchEvent', {
        type,
        touchPoints: points.map((p) => ({ x: p.x, y: p.y })),
      });
    }
    await touch('touchStart', [{ x: cx - 20, y: cy }, { x: cx + 20, y: cy }]);
    for (let i = 1; i <= 6; i++) {
      const spread = 20 + i * 20;
      await touch('touchMove', [{ x: cx - spread, y: cy }, { x: cx + spread, y: cy }]);
    }
    await touch('touchEnd', []);
    // The seeded series starts at the coarsest level, so a 6x pinch-out must refine it. Poll on
    // that refinement rather than on a non-empty string (which passes immediately, before zoom).
    await expect
      .poll(async () => order.indexOf(await currentLevel(page)), { timeout: 5_000 })
      .toBeGreaterThan(order.indexOf(before));
    expect(errors, errors.join('\n')).toHaveLength(0);
    await assertNoLabelOverlap(page);
  });
});
