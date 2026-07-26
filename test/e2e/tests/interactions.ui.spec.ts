/**
 * UI interactions that exist in the dashboard JS but weren't driven by any
 * spec: the manual scan button, quiet auto-save on task-mode change, the
 * insight-card trees, the Seerr Test-Connection button, and backup export.
 * These assert the real request the click fires AND a concrete DOM outcome, so
 * a regression that silently no-ops the handler is caught.
 */
import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { openDashboard, switchTab } from './_ui-helpers.ts';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';

const SEERR_URL = process.env.MOCK_SEERR_URL ?? 'http://mock-seerr:5055';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  // Configure Seerr so the settings-side Test button has something to hit.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SeerrUrl: SEERR_URL, SeerrApiKey: 'seerr-key' },
  });
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Overview: Scan Libraries button fires a scan and re-enables', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'overview');

  const scanBtn = page.locator('#btnScanLibraries');
  await expect(scanBtn).toBeVisible({ timeout: 15_000 });

  const [scanResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/MediaStatistics/ScanLibraries'),
      { timeout: 30_000 },
    ),
    scanBtn.click(),
  ]);
  // A scan may be rate-limited (429) if one just ran; both are acceptable, a
  // 5xx is not.
  expect([200, 429], `scan status ${scanResp.status()}`).toContain(scanResp.status());
  // The button must return to the enabled state (the handler re-enables it in
  // both success and error branches).
  await expect(scanBtn).toBeEnabled({ timeout: 20_000 });
});

test('Settings: changing a task-mode auto-saves quietly (PUT, no unsaved band)', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'settings');
  await expect(page.locator('#settingsForm')).toBeVisible({ timeout: 15_000 });

  const modeSelect = page.locator('#cfgTrickplayMode');
  await expect(modeSelect).toBeVisible();
  const current = await modeSelect.inputValue();
  const next = current === 'Deactivate' ? 'DryRun' : 'Deactivate';

  // Changing a task-mode dropdown triggers a QUIET PUT /Configuration (auto-save)
  // — distinct from the manual #btnSaveSettings path.
  const [putResp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Configuration') && r.request().method() === 'PUT',
      { timeout: 15_000 },
    ),
    modeSelect.selectOption(next),
  ]);
  expect(putResp.ok(), `auto-save PUT failed: ${putResp.status()}`).toBeTruthy();
  // An auto-save must NOT surface the manual unsaved-changes band.
  await expect(page.locator('#settingsSaveBand')).not.toHaveClass(/is-unsaved/, { timeout: 5000 });

  // Restore the original value (also via auto-save) to keep state neutral.
  await modeSelect.selectOption(current);
});

test('Trends: insight cards expand and mutually collapse', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'trends');

  const largestBtn = page.locator('#insightLargestBtn');
  const recentBtn = page.locator('#insightRecentBtn');
  // Insight cards render after the scan-derived insights load.
  await expect(largestBtn).toBeVisible({ timeout: 20_000 });

  await largestBtn.click();
  await expect(page.locator('#insightLargestPanel')).toHaveClass(/visible/, { timeout: 5000 });

  // Opening the other card collapses the first (mutually exclusive).
  await recentBtn.click();
  await expect(page.locator('#insightRecentPanel')).toHaveClass(/visible/, { timeout: 5000 });
  await expect(page.locator('#insightLargestPanel')).not.toHaveClass(/visible/);
});

test('Settings: Seerr Test Connection button hits Seerr/Test', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'settings');
  await expect(page.locator('#settingsForm')).toBeVisible({ timeout: 15_000 });

  // The Seerr Instance section renders COLLAPSED when a Seerr config already
  // exists (our beforeAll set one), which hides the inputs + Test button.
  // Expand it via its collapsible header first.
  const seerrHeader = page.locator('#arrCollapsibleHeaderSeerr');
  if ((await seerrHeader.getAttribute('aria-expanded')) !== 'true') {
    await seerrHeader.click();
  }

  // The handler reads the URL/key from the DOM inputs and short-circuits (no
  // request) if either is blank — the stored key renders masked/empty, so fill
  // both fields explicitly before clicking.
  await page.locator('#cfgSeerrUrl').fill(SEERR_URL);
  await page.locator('#cfgSeerrApiKey').fill('seerr-key');

  const testBtn = page.locator('#btnTestSeerr');
  await expect(testBtn).toBeVisible();

  const [resp] = await Promise.all([
    page.waitForResponse(
      (r) => r.url().includes('/JellyfinHelper/Seerr/Test') && r.request().method() === 'POST',
      { timeout: 15_000 },
    ),
    testBtn.click(),
  ]);
  // The beforeAll configures a working mock-Seerr, so the Test call must genuinely
  // succeed (2xx) — a 4xx/5xx here is a real config/mock regression, not tolerable noise.
  expect(resp.ok(), `Seerr/Test failed: ${resp.status()}`).toBeTruthy();
});

test('Settings: Export Backup button produces a download', async ({ page }) => {
  await openDashboard(page);
  await switchTab(page, 'settings');
  await expect(page.locator('#settingsForm')).toBeVisible({ timeout: 15_000 });

  const exportBtn = page.locator('#btnBackupExport');
  await expect(exportBtn).toBeVisible();

  const [download] = await Promise.all([
    page.waitForEvent('download', { timeout: 20_000 }),
    exportBtn.click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/backup.*\.json/i);
});

test('Settings: folder-browser opens for the trash path', async ({ page }: { page: Page }) => {
  await openDashboard(page);
  await switchTab(page, 'settings');
  await expect(page.locator('#settingsForm')).toBeVisible({ timeout: 15_000 });

  const browseBtn = page.locator('#btnBrowseTrash');
  // #btnBrowseTrash lives inside <fieldset id="trashSettingsWrapper" disabled>
  // when UseTrash is off — a disabled fieldset blocks the button. Enable trash
  // first so the browse control is interactive.
  const trashChk = page.locator('#cfgTrash');
  if (!(await trashChk.isChecked())) {
    await trashChk.check();
  }
  await expect(browseBtn).toBeVisible();

  // Opening the browser populates its library-root list via
  // Configuration/LibraryPaths (BrowseFolders fires later, on navigation).
  const [browseResp] = await Promise.all([
    page.waitForResponse(
      (r) =>
        r.url().includes('/JellyfinHelper/Configuration/LibraryPaths') ||
        r.url().includes('/JellyfinHelper/Configuration/BrowseFolders'),
      { timeout: 15_000 },
    ),
    browseBtn.click(),
  ]);
  expect(browseResp.ok(), `browse request failed: ${browseResp.status()}`).toBeTruthy();
  await expect(page.locator('#folderBrowserOverlay')).toBeVisible({ timeout: 5000 });
});
