/**
 * Donut chart touch handling: first tap shows the tooltip, second tap on the same segment hides it.
 */
import { test, expect, type Locator, type Page } from '@playwright/test';
import { openDashboard, switchTab, trackConsoleErrors } from './_ui-helpers.ts';

async function openSegments(page: Page) {
  await openDashboard(page);
  await switchTab(page, 'codecs');
  const segments = page.locator('#codecsContent .donut-segment path');
  // Do not fail if this server produced no codec data.
  await segments.first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {});
  return segments;
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
    test.skip((await segments.count()) === 0, 'no codec data on this server');

    const segment = segments.first();
    await tapPath(segment);

    // The tooltip is per .donut-container, so scope the check to the tapped segment's container.
    const tooltip = segment.locator('xpath=ancestor::div[contains(@class,"donut-container")]')
      .locator('.donut-tooltip.visible');
    await expect(tooltip.first()).toBeVisible({ timeout: 5_000 });
    expect(errors, errors.join('\n')).toHaveLength(0);
  });

  test('second tap on same segment hides the tooltip', async ({ page }) => {
    const errors = trackConsoleErrors(page);
    const segments = await openSegments(page);
    test.skip((await segments.count()) === 0, 'no codec data on this server');

    const segment = segments.first();
    const tooltip = page.locator('#codecsContent .donut-tooltip.visible');

    await tapPath(segment);
    await expect(tooltip.first()).toBeVisible({ timeout: 5_000 });

    // Second tap on the same segment takes the tap-again branch and hides the tooltip.
    await tapPath(segment);
    await expect(tooltip).toHaveCount(0, { timeout: 5_000 });
    expect(errors, errors.join('\n')).toHaveLength(0);
  });
});
