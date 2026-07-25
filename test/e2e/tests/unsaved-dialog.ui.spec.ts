/**
 * Unsaved-changes dialog — the explicitly requested behaviour: change a setting
 * (e.g. the retention/days number), do NOT save, then try to leave the settings
 * tab → the "unsaved changes" dialog must appear. Counter-checks: after saving,
 * no dialog; Cancel keeps you on the tab with the change intact; Discard leaves.
 *
 * Selectors (from the UI map):
 *   - dirty band:        #settingsSaveBand (class .is-unsaved when dirty)
 *   - save button:       #btnSaveSettings
 *   - unsaved dialog:    #unsavedDialogOverlay (Cancel / Discard / Save&Continue)
 *   - a numeric field:   #cfgTrashDays (needs Use Trash on) / #cfgOrphanAge
 */
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

  // Try to switch to another tab — the guard should intercept.
  await page.locator('.tab-btn[data-tab="overview"]').click();

  const dialog = page.locator('#unsavedDialogOverlay');
  await expect(dialog, 'unsaved-changes dialog must appear').toBeVisible({ timeout: 5000 });

  // Cancel keeps us on settings with the change intact.
  await dialog.getByRole('button').filter({ hasText: /cancel|abbrechen/i }).first().click()
    .catch(async () => {
      // Fallback if button text is localized differently: click the first button.
      await dialog.locator('button').first().click();
    });
  await expect(page.locator('#tab-settings')).toHaveClass(/active/);
});

test('after saving, leaving the tab does NOT show the dialog', async ({ page }) => {
  await openSettings(page);
  await makeDirty(page);

  // Save via the band button.
  await page.locator('#btnSaveSettings').click();
  // Band transitions to saved (or at least leaves the unsaved state).
  await expect(page.locator('#settingsSaveBand')).not.toHaveClass(/is-unsaved/, { timeout: 10_000 });

  // Now switching tabs should be clean — no dialog.
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
