import {test, expect} from '@playwright/test';
import {readFileSync} from 'node:fs';
import {resolve, extname, sep} from 'node:path';

const docs = resolve(import.meta.dirname, '../../../docs');
const plugin = resolve(docs, '../Jellyfin.Plugin.JellyfinHelper');
const types: Record<string, string> = {'.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json'};

for (const width of [390, 1440]) {
 test(`GitHub demo at ${width}px switches between admin, Discovery and YearlyRewind`, async ({page}) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.setViewportSize({width, height: 900});
  await page.route('**/*', async route => {
   const url = new URL(route.request().url());
   if (url.hostname !== 'demo.test') { await route.abort(); return; }
   const path = resolve(docs, '.' + (url.pathname === '/' ? '/index.html' : decodeURIComponent(url.pathname)));
   if (!path.startsWith(docs + sep)) { await route.fulfill({status: 403}); return; }
   try {
    let body;
    try { body = readFileSync(path); }
    catch {
     // A fresh checkout can run fixtures before generating ignored docs assets.
     // Serve the same sources used by SyncDocsAssets and prepare-demo-site.ps1.
     const relative = url.pathname.slice(1);
     const source = relative === 'js/mock-data.js' ? resolve(docs, 'mock/mock-data.js')
      : relative.startsWith('i18n/') ? resolve(plugin, relative)
      : ['js/yearly-rewind.js', 'js/discovery-sidebar.js', 'js/native-tabs.js'].includes(relative) ? resolve(plugin, relative)
      : resolve(plugin, 'PluginPages', relative);
     body = readFileSync(source);
    }
    await route.fulfill({body, contentType: types[extname(path)] || 'application/octet-stream'});
   }
   catch { await route.fulfill({status: 404}); }
  });
  await page.goto('https://demo.test/');
  await expect(page.locator('#demoViewAdmin')).toBeVisible();
  await expect(page.locator('#statsResult')).toBeVisible();
  await page.locator('#demoViewBtnDiscovery').click();
  await expect(page.locator('#demoViewDiscovery')).toBeVisible();
  await expect(page.locator('#demoViewDiscovery .jellyfinhelper.discovery')).not.toBeEmpty();
  await page.locator('#demoViewBtnRewind').click();
  const rewind = page.locator('#demoViewRewind');
  await expect(rewind).toBeVisible();
  await expect(rewind.locator('h2')).toContainText('Your year in review 2026');
  await expect(rewind.locator('.jfh-rewind-stat')).toHaveCount(5);
  await expect(rewind.locator('.jfh-rewind-pie')).toHaveCount(2);
  await expect(rewind.locator('.jfh-rewind-chart [role="listitem"]')).toHaveCount(12);
  await expect(rewind.locator('.jfh-rewind-estimated-bar')).toHaveCount(1);
  await expect(rewind.locator('.jfh-rewind-distribution')).toHaveCount(2);
  await expect(rewind.locator('.jfh-rewind-comparison tbody tr')).toHaveCount(5);
  await expect(rewind.locator('.jfh-rewind-top-titles')).toHaveCount(0);
  await expect(page.locator('#demoViewBtnRewind')).toHaveAttribute('title', 'Year in review 2026');
  await rewind.locator('select').selectOption('0');
  await page.locator('#demoViewBtnAdmin').click();
  await expect(rewind).toBeHidden();
  await expect(page.locator('#demoViewBtnAdmin')).toHaveAttribute('aria-selected', 'true');
  await page.locator('#demoViewBtnRewind').click();
  await expect(rewind.locator('select')).toHaveValue('0');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
 });
}
