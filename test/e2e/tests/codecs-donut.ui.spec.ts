/**
 * Donut chart touch handling: one tap shows the tooltip and opens the
 * drill-down together (no hover on touch, so splitting them was confusing).
 */
import { test, expect, type Locator, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

async function openSegments(page: Page) {
  await openDashboard(page);
  await switchTab(page, 'codecs');
  // Wait for the codecs render to settle: either the donut appears (has data) or the no-data box
  // does. Both replace the initial placeholder, so a render that never settles fails the test here
  // instead of being swallowed into a false skip. The no-data box has no .donut-container inside it.
  const donut = page.locator('#codecsContent .donut-container');
  const noData = page.locator('#codecsContent .chart-box:not(:has(.donut-container))');
  await expect(donut.first().or(noData.first())).toBeVisible({ timeout: 15_000 });
  return page.locator('#codecsContent .donut-segment path');
}

// The touchend handler is bound per <path>, and a coordinate tap resolves to the <svg> ancestor, so
// dispatch on the path itself. getPointAtLength gives a point on the arc; the bbox center is the hole.
// After the tap we replay the synthetic mouseleave the browser emits for touch, which is exactly the
// event the fix must ignore, so this exercises the regression rather than a bare touchend.
async function tapPath(segmentPath: Locator) {
  await segmentPath.evaluate((el) => {
    const path = el as unknown as SVGPathElement;
    const pt = path.getPointAtLength(path.getTotalLength() * 0.5);
    const ctm = path.getScreenCTM();
    if (!ctm) {
      throw new Error('path has no screen CTM');
    }
    const clientX = ctm.a * pt.x + ctm.c * pt.y + ctm.e;
    const clientY = ctm.b * pt.x + ctm.d * pt.y + ctm.f;

    const touch = new Touch({ identifier: 0, target: path, clientX, clientY });
    path.dispatchEvent(new TouchEvent('touchend', {
      changedTouches: [touch],
      bubbles: true,
      cancelable: true,
    }));
    path.dispatchEvent(new MouseEvent('mouseleave', { clientX, clientY, bubbles: false }));
  });
}

test.describe('codec donut touch tap', () => {
  test.use({ hasTouch: true, isMobile: true });

  test('tap shows a segment tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    // The fixture library always yields codec data; zero segments means the scan or
    // the stats pipeline broke - fail instead of skipping into a vacuous pass.
    expect(await segments.count(), 'codec donut must have segments on the fixture library').toBeGreaterThan(0);

    const segment = segments.first();
    await tapPath(segment);

    // The tooltip is per .donut-container, so scope the check to the tapped segment's container.
    const tooltip = segment.locator('xpath=ancestor::div[contains(@class,"donut-container")]')
      .locator('.donut-tooltip.visible');
    await expect(tooltip.first()).toBeVisible({ timeout: 5_000 });
    // Background tabs (Discover) may 403 on this stack; that noise is unrelated
    // to the donut under test, same as in the explorer spec.
    const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
    expect(scriptErrors, scriptErrors.join('\n')).toHaveLength(0);
  });

  test('tap opens the drill-down while the tooltip stays visible', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    expect(await segments.count(), 'codec donut must have segments on the fixture library').toBeGreaterThan(0);

    const segment = segments.first();
    const tooltip = page.locator('#codecsContent .donut-tooltip.visible');

    await tapPath(segment);
    await expect(tooltip.first()).toBeVisible({ timeout: 5_000 });
    // The same tap opens the drill-down panel instead of waiting for a second one.
    await expect(page.locator('#codecsContent .file-tree-panel-visible').first()).toBeVisible({ timeout: 10_000 });
    const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
    expect(scriptErrors, scriptErrors.join('\n')).toHaveLength(0);
  });

  test('second tap on the same segment dismisses tooltip and drill-down', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    expect(await segments.count(), 'codec donut must have segments on the fixture library').toBeGreaterThan(0);

    const segment = segments.first();
    const tooltip = page.locator('#codecsContent .donut-tooltip.visible');
    const panel = page.locator('#codecsContent .file-tree-panel-visible');

    await tapPath(segment);
    await expect(tooltip.first()).toBeVisible({ timeout: 5_000 });
    await expect(panel.first()).toBeVisible({ timeout: 10_000 });

    // Second tap toggles everything off again.
    await tapPath(segment);
    await expect(tooltip).toHaveCount(0, { timeout: 5_000 });
    await expect(panel).toHaveCount(0, { timeout: 5_000 });
    const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
    expect(scriptErrors, scriptErrors.join('\n')).toHaveLength(0);
  });

  test('tap in another chart clears the first chart tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    await openSegments(page);
    const containers = page.locator('#codecsContent .donut-container');
    const chartCount = await containers.count();
    expect(chartCount, 'need at least two donut charts').toBeGreaterThan(1);
    // Both charts must offer a segment; an empty second chart means the scan or
    // the stats pipeline broke - fail instead of skipping into a vacuous pass.
    for (let c = 0; c < 2; c++) {
      expect(
        await containers.nth(c).locator('.donut-segment path').count(),
        `chart ${c} must have segments on the fixture library`,
      ).toBeGreaterThan(0);
    }

    await tapPath(containers.nth(0).locator('.donut-segment path').first());
    const tooltipA = containers.nth(0).locator('.donut-tooltip.visible');
    await expect(tooltipA.first()).toBeVisible({ timeout: 5_000 });

    // Tapping chart B must not strand chart A's tooltip while the shared panel
    // handler closes A's drill-down.
    await tapPath(containers.nth(1).locator('.donut-segment path').first());
    await expect(tooltipA).toHaveCount(0, { timeout: 5_000 });
    await expect(containers.nth(1).locator('.donut-tooltip.visible').first()).toBeVisible({ timeout: 5_000 });
    const scriptErrors = errors.filter((e) => !/Failed to load resource.*\b403\b/i.test(e));
    expect(scriptErrors, scriptErrors.join('\n')).toHaveLength(0);
  });
});
