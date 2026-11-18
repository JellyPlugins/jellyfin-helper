import { test, expect } from '@playwright/test';
import { apiContext, loadAuth, p } from '../setup/api-client.ts';
import { seedRewind, visibleRewindWindow } from '../setup/yearly-rewind.ts';
import { openDashboard } from './_ui-helpers.ts';

for (const layout of ['modern', 'desktop-legacy']) {
test(`YearlyRewind mounts independently and disappears when disabled in ${layout}`, async ({page}) => {
 await page.addInitScript(value => localStorage.setItem('layout', value), layout);
 const auth = loadAuth(), admin = await apiContext(auth);
 const original = await (await admin.get(p('Configuration'))).json();
 seedRewind(auth.userId);
 try {
   expect((await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: true, ...visibleRewindWindow(), DiscoveryUserAccessEnabled: layout === 'modern', RecommendationsTaskMode: layout === 'modern' ? 'DryRun' : 'Deactivate'}})).ok()).toBeTruthy();
   await expect.poll(async () => (await admin.get(p('YearlyRewind/My'))).status()).toBe(200);
   await openDashboard(page);
   await page.goto(auth.baseUrl + '/web/index.html#/home');
   const entry = page.locator('#jfh-tab-btn-yearly-rewind, #jfh-tab-link-yearly-rewind').filter({visible: true});
   await expect(entry).toHaveCount(1, {timeout: 20000});
   await entry.click();
   const panel = page.locator('#jfh-tab-panel-yearly-rewind');
   await expect(panel.locator('.jfh-rewind-stat strong')).toHaveText(['2', '1', '0', '0', '3']);
   await expect(panel.locator('.jfh-rewind-stat')).toHaveCount(5);
   await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: false}});
   await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
   await expect(entry).toHaveCount(0);
   if (layout === 'modern') {
     await expect(page.locator('#jfh-tab-btn-discovery, #jfh-tab-link-discovery').filter({visible: true})).toHaveCount(1);
   }
 } finally {
   await admin.put(p('Configuration'), {data: original}); await admin.dispose();
 }
});

}
