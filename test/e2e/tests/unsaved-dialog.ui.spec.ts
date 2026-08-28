/** * Unsaved-changes dialog - the explicitly requested behaviour: change a setting * (e.g. the retention/days number), do NOT save, then try to leave the settings * tab -> the "unsaved changes" dialog must appear. */
import { test, expect, type Page } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';

async function openSettings(page: Page): Promise<void> {
  await openDashboard(page);
  await switchTab(page, 'settings');
  await expect(page.locator('#settingsForm')).toBeVisible({ timeout: 15_000 });
}

/** Change a number field that always exists and is safe to edit. */
async function makeDirty(page: Page): Promise<void> {
  const field = page.locator('#cfgOrphanAge');
  await expect(field).toBeVisible();
  const current = await field.inputValue();
  const next = current === '5' ? '9' : '5';
  await field.fill(next);
  await field.blur();
  // The floating save band should flip to the unsaved state (debounced ~600ms).
  await expect(page.locator('#settingsSaveBand')).toHaveClass(/is-unsaved/, { timeout: 5000 });
}

test('changing a setting marks the form dirty (unsaved band appears)', async ({ page }) => {
  await openSettings(page);
  await makeDirty(page);
});

test('leaving the settings tab while dirty shows the unsaved-changes dialog', async ({ page }) => {
  await openSettings(page);
  await makeDirty(page);

  // Try to switch to another tab - the guard should intercept.
  await page.locator('.tab-btn[data-tab="overview"]').click();

  const dialog = page.locator('#unsavedDialogOverlay');
  await expect(dialog, 'unsaved-changes dialog must appear').toBeVisible({ timeout: 5000 });

  // Cancel keeps us on settings with the change intact. Check count() first (like the Discard lookup below) rather than click().catch() - otherwise a locale mismatch would burn the full ~30s default click timeout before the fallback runs.
  const cancelBtn = dialog.getByRole('button').filter({ hasText: /cancel|abbrechen/i }).first();
  if (await cancelBtn.count()) {
    await cancelBtn.click();
  } else {
    // Fallback if button text is localized differently: click the first button.
    await dialog.locator('button').first().click();
  }
  await expect(page.locator('#tab-settings')).toHaveClass(/active/);
});

test('after saving, leaving the tab does NOT show the dialog', async ({ page }) => {
  await openSettings(page);
  await makeDirty(page);

  // Save via the band button, and wait for the PUT to actually complete - not just for the band to drop is-unsaved.
  const [saveResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Configuration') && r.request().method() === 'PUT',
      { timeout: 15_000 },
    ),
    page.locator('#btnSaveSettings').click(),
  ]);
  expect(saveResp.ok(), `save PUT failed: ${saveResp.status()}`).toBeTruthy();
  // Band should also have left the unsaved state.
  await expect(page.locator('#settingsSaveBand')).not.toHaveClass(/is-unsaved/, { timeout: 10_000 });

  // Now switching tabs should be clean - no dialog.
  await page.locator('.tab-btn[data-tab="overview"]').click();
  await expect(page.locator('#tab-overview')).toHaveClass(/active/, { timeout: 10_000 });
  await expect(page.locator('#unsavedDialogOverlay')).toBeHidden();
});

test('Discard Changes leaves the tab and drops the edit', async ({ page }) => {
  await openSettings(page);
  const field = page.locator('#cfgOrphanAge');
  const original = await field.inputValue();
  await makeDirty(page);

  await page.locator('.tab-btn[data-tab="overview"]').click();
  const dialog = page.locator('#unsavedDialogOverlay');
  await expect(dialog).toBeVisible();

  // Click "Discard" (middle option). Match by text, fall back to 2nd button.
  const discard = dialog.getByRole('button').filter({ hasText: /discard|verwerfen/i }).first();
  if (await discard.count()) {
    await discard.click();
  } else {
    await dialog.locator('button').nth(1).click();
  }

  // Left the tab.
  await expect(page.locator('#tab-overview')).toHaveClass(/active/, { timeout: 10_000 });

  // Returning to settings shows the original value (edit discarded).
  await switchTab(page, 'settings');
  await expect(page.locator('#cfgOrphanAge')).toHaveValue(original);
});
