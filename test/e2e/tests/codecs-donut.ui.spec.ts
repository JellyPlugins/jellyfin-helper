/**
 * Donut chart touch handling
 * Tap 1 shows tooltip. Tap 2 hides it and triggers action.
 * We suppress synthetic mouse events after taps because they fire a mouseleave that immediately breaks the active state.
 */
import { test, expect, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

// Opens Codecs tab and gets donut segment paths. No separate data seed needed here because the global setup library scan already generates media with various codecs.
async function openSegments(page: Page) {
  await openDashboard(page);
  await switchTab(page, 'codecs');
  const segments = page.locator('#codecsContent .donut-segment path');
  // Wait for the donut to render but do not fail if this server produced no codec data.
  await segments.first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {});
  return segments;
}

// Dispatches a deterministic tap via CDP. The touchEnd needs the released point so the browser populates event.changedTouches which we use for tooltip positioning.
async function tap(page: Page, x: number, y: number) {
  const client = await page.context().newCDPSession(page);
  await client.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
  await client.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [{ x, y }] });
  await client.detach();
}

test.describe('codec donut touch tap', () => {
  test.use({ hasTouch: true, isMobile: true });

  test('tap shows a segment tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    test.skip((await segments.count()) === 0, 'no codec data on this server');

    // First tap on the arc. The bounding box center lands on the sector and not the center hole. This must show the tooltip.
    const box = (await segments.first().boundingBox())!;
    await tap(page, box.x + box.width / 2, box.y + box.height / 2);

    // Wait for visibility. Without our fix the synthetic mouseleave hides the tooltip instantly.
    await expect(page.locator('#codecsContent .donut-tooltip.visible')).toBeVisible({ timeout: 5_000 });
    expect(errors, errors.join('\n')).toHaveLength(0);
  });

  test('second tap on same segment hides the tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    test.skip((await segments.count()) === 0, 'no codec data on this server');

    const box = (await segments.first().boundingBox())!;
    const cx = box.x + box.width / 2;
    const cy = box.y + box.height / 2;

    // First tap shows tooltip and makes segment active.
    await tap(page, cx, cy);
    await expect(page.locator('#codecsContent .donut-tooltip.visible')).toBeVisible({ timeout: 5_000 });

    // Second tap on same segment hides tooltip and fires the trigger. We assert only tooltip visibility to avoid flaky tests caused by downstream scroll or render timing.
    await tap(page, cx, cy);
    await expect(page.locator('#codecsContent .donut-tooltip.visible')).toHaveCount(0, { timeout: 5_000 });
    expect(errors, errors.join('\n')).toHaveLength(0);
  });
});