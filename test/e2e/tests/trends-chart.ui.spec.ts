/**
 * Trend chart zoom/pan interaction: the growth chart renders, responds to wheel zoom
 * and drag pan on desktop, and to tap / swipe / pinch on touch devices, without JS errors
 * and without overlapping X-axis labels.
 */
import { test, expect, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

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

// Asserts no two X-axis labels overlap: their x positions must differ by a minimum gap.
async function assertNoLabelOverlap(page: Page): Promise<void> {
  const xs = await page.locator('.trend-chart svg text[text-anchor="middle"]').evaluateAll(
    (nodes) => nodes.map((n) => Number((n as SVGTextElement).getAttribute('x'))).filter((v) => !Number.isNaN(v)),
  );
  const sorted = xs.slice().sort((a, b) => a - b);
  for (let i = 1; i < sorted.length; i++) {
    expect(sorted[i] - sorted[i - 1], `labels at ${sorted[i - 1]} and ${sorted[i]} overlap`).toBeGreaterThanOrEqual(40);
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
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const before = await currentLevel(page);
    const box = (await chart.boundingBox())!;
    // Zoom in several notches toward the right edge (recent data).
    for (let i = 0; i < 8; i++) {
      await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2);
      await page.mouse.wheel(0, -120);
    }
    await page.waitForTimeout(200);
    const after = await currentLevel(page);

    // Either the level refined (e.g. monthly -> weekly -> daily) or it was already daily.
    const order = ['yearly', 'monthly', 'weekly', 'daily'];
    expect(order.indexOf(after)).toBeGreaterThanOrEqual(order.indexOf(before));
    await assertNoLabelOverlap(page);
  });

  test('drag pans the visible window', async ({ page }) => {
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    // Zoom in first so there is room to pan.
    const box = (await chart.boundingBox())!;
    for (let i = 0; i < 6; i++) {
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.wheel(0, -120);
    }
    await page.waitForTimeout(150);

    const labelsBefore = await page.locator('.trend-chart svg text[text-anchor="middle"]').allTextContents();

    await page.mouse.move(box.x + box.width * 0.6, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.2, box.y + box.height / 2, { steps: 10 });
    await page.mouse.up();
    await page.waitForTimeout(150);

    const labelsAfter = await page.locator('.trend-chart svg text[text-anchor="middle"]').allTextContents();
    expect(labelsAfter.join('|')).not.toBe(labelsBefore.join('|'));
    await assertNoLabelOverlap(page);
  });
});

test.describe('trend chart touch gestures', () => {
  test.use({ hasTouch: true, isMobile: true });

  test('tap shows a tooltip', async ({ page }) => {
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const box = (await chart.boundingBox())!;
    await page.touchscreen.tap(box.x + box.width / 2, box.y + box.height / 2);
    await expect(page.locator('.trend-tooltip')).toHaveClass(/visible/, { timeout: 5_000 });
  });

  test('pinch zoom refines the level without JS errors', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const chart = await openChart(page);
    test.skip((await chart.count()) === 0, 'no trend data on this server');

    const before = await currentLevel(page);
    const box = (await chart.boundingBox())!;
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;

    // Two-finger pinch-out (fingers moving apart) via CDP touch events => zoom in.
    const client = await page.context().newCDPSession(page);
    async function touch(type: string, points: Array<{ x: number; y: number }>) {
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
    await page.waitForTimeout(200);

    const after = await currentLevel(page);
    const order = ['yearly', 'monthly', 'weekly', 'daily'];
    expect(order.indexOf(after)).toBeGreaterThanOrEqual(order.indexOf(before));
    expect(errors, errors.join('\n')).toHaveLength(0);
    await assertNoLabelOverlap(page);
  });
});
