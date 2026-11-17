import {test, expect} from '@playwright/test';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
const root = resolve(import.meta.dirname, '../../../Jellyfin.Plugin.JellyfinHelper');
const script = readFileSync(resolve(root, 'js/yearly-rewind.js'), 'utf8');
for (const language of ['en', 'de', 'zh']) {
 for (const width of [390, 1440]) {
  test(`YearlyRewind ${language} at ${width}px remains accessible and within the viewport`, async ({page}) => {
   await page.setViewportSize({width, height: 900});
   await page.emulateMedia({colorScheme: width === 390 ? 'light' : 'dark', reducedMotion: 'reduce'});
   await page.setContent('<html><head><style>html{color-scheme:light dark}body{background:Canvas;color:CanvasText}</style></head><body><main id="report"></main></body></html>');
   const strings = JSON.parse(readFileSync(resolve(root, `i18n/${language}.json`), 'utf8'));
   await page.evaluate(({language, strings}) => {
    document.documentElement.lang = language;
    const report = {SnapshotId: 'fixed', PeriodStartUtc: '2025-12-15T00:00:00Z', SnapshotAtUtc: '2026-12-15T00:00:00Z', TimeZoneId: 'UTC', DataState: 'Partial', ActiveDays: 20,
      Items: [{ItemId: 'deleted', Name: 'Eine sehr lange Filmüberschrift mit außergewöhnlichen Wörtern und 中文字符', ItemType: 'Movie', ActiveSeconds: 5400, Genres: ['Drama']}, {ItemId: 'episode', Name: 'Episode', ItemType: 'Episode', SeriesId: 'series', SeriesName: 'Demo series', ActiveSeconds: 1800, Genres: ['Comedy']}],
      Months: Array.from({length: 12}, (_, index) => ({StartUtc: new Date(Date.UTC(2025, 11 + index, 15)).toISOString(), EndUtc: new Date(Date.UTC(2026, index, 15)).toISOString(), ActiveSeconds: 600, Genres: [{Name: 'Drama', ActiveSeconds: 450}, {Name: 'Comedy', ActiveSeconds: 150}]})),
      Insights: {HourlySeconds: Array.from({length:24}, (_, hour) => hour === 20 ? 5400 : hour === 9 ? 1800 : 0), WeekdaySeconds: [0,0,0,0,5400,1800,0], LongestStreakDays:2, LongestStreakStart:'2026-10-02', LongestStreakEnd:'2026-10-03'},
      Comparison: {PeriodStartUtc:'2024-12-15T00:00:00Z',SnapshotAtUtc:'2025-12-15T00:00:00Z',TimeZoneId:'UTC',DataState:'Ready',ActiveSeconds:3600,MovieCount:2,EpisodeCount:0,SeriesCount:0,ActiveDays:12,Genres:['Drama','Adventure'],TopGenre:'Drama'}};
    (window as any).ApiClient = {getCurrentUserId: () => 'a', accessToken: () => 'token', serverAddress: () => 'server', getUrl: (path: string) => path,
      ajax: ({url}: {url: string}) => Promise.resolve(url.includes('Translations') ? strings : url.includes('Status') ? {Enabled: true, TabVisible: true, Available: true, SnapshotId: 'fixed'} : report)};
    (window as any).JellyfinHelperTabs = {register: (_id: string, _title: string, mount: (root: HTMLElement) => void) => mount(document.getElementById('report')!), unregister: () => {}};
   }, {language, strings});
   await page.addScriptTag({content: script});
   await expect(page.locator('.jfh-rewind-stat')).toHaveCount(5);
   await expect(page.locator('h2')).toContainText(strings['rewind.title']);
   await expect(page.locator('.jfh-rewind-months > h3')).toHaveText(strings['rewind.months'] + ' (' + strings['rewind.hourUnitLong'] + ')');
   await expect(page.locator('p.jfh-rewind-chart-unit')).toHaveCount(0);
   await expect(page.locator('.jfh-rewind-pie')).toHaveCount(2);
   await expect(page.locator('.jfh-rewind-pie').first()).toHaveAttribute('aria-label', /75/);
   await expect(page.locator('.jfh-rewind-patterns .jfh-rewind-distribution')).toHaveCount(2);
   await expect(page.locator('.jfh-rewind-streak-length')).toContainText('2');
   await expect(page.locator('.jfh-rewind-comparison tbody tr')).toHaveCount(5);
   await expect(page.locator('.jfh-rewind-comparison .jfh-rewind-notice')).toHaveCount(1);
   const selector = page.locator('.jfh-rewind-select');
   await expect(selector.locator('option')).toHaveCount(13);
   await selector.selectOption('0');
   await expect(page.locator('.jfh-rewind-genres .jfh-rewind-pie')).toHaveAttribute('aria-label', /75/);
   await selector.selectOption('all');
   await expect(page.locator('.jfh-rewind-chart')).toHaveCount(1);
   await expect(page.locator('.jfh-rewind-chart [role=listitem]')).toHaveCount(12);
   await expect(page.locator('.jfh-rewind-chart [role=listitem]').first()).toHaveAttribute('aria-label', /2025/);
   await expect(page.locator('progress')).toHaveCount(0);
   expect(await page.locator('.jfh-rewind-chart-scroll').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
   await expect(page.locator('.jfh-rewind-top-titles')).toHaveCount(0);
   await expect(page.getByRole('heading', {name: strings['rewind.topMovies'], exact: true})).toHaveCount(0);
   await expect(page.getByRole('heading', {name: strings['rewind.topSeries'], exact: true})).toHaveCount(0);
   const heading = (await page.locator('.jfh-rewind-heading > span').first().boundingBox())!;
   const period = (await page.locator('.jfh-rewind-period').boundingBox())!;
   if (width === 390) expect(period.y).toBeGreaterThan(heading.y);
   expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
   if (language === 'de') await expect(page.locator('.jfh-rewind-stat').first()).toContainText('2');
  });
 }
}

for (const width of [390, 1440]) {
 test(`YearlyRewind reconstructed data at ${width}px stays distinct from measured playback`, async ({page}) => {
  await page.setViewportSize({width, height: 900});
  await page.setContent('<main id="report"></main>');
  const strings = JSON.parse(readFileSync(resolve(root, 'i18n/de.json'), 'utf8'));
  await page.evaluate(strings => {
   document.documentElement.lang = 'de';
   const report = {SnapshotId: 'reconstructed', PeriodStartUtc: '2025-12-15T00:00:00Z', SnapshotAtUtc: '2026-12-15T00:00:00Z', TimeZoneId: 'UTC', DataState: 'Partial', ActiveDays: 0, ReconstructedItemCount: 1,
    Items: [{ItemId: 'imported', Name: 'Rekonstruierter Titel', ItemType: 'Movie', ActiveSeconds: 3600, EstimatedSeconds: 3600, HasReconstructedActivity: true, Genres: ['Drama']}],
    Months: [{StartUtc: '2025-12-15T00:00:00Z', EndUtc: '2026-01-15T00:00:00Z', ActiveSeconds: 3600, EstimatedSeconds: 3600}],
    Insights: {HourlySeconds: Array(24).fill(0), WeekdaySeconds: Array(7).fill(0), LongestStreakDays: 0}};
   (window as any).ApiClient = {getCurrentUserId: () => 'a', accessToken: () => 'token', serverAddress: () => 'server', getUrl: (path: string) => path,
    ajax: ({url}: {url: string}) => Promise.resolve(url.includes('Translations') ? strings : url.includes('Status') ? {Enabled: true, TabVisible: true, Available: true, SnapshotId: 'reconstructed'} : report)};
   (window as any).JellyfinHelperTabs = {register: (_id: string, _title: string, mount: (root: HTMLElement) => void) => mount(document.getElementById('report')!), unregister: () => {}};
  }, strings);
  await page.addScriptTag({content: script});
  await expect(page.locator('.jfh-rewind-notice').first()).toHaveText(strings['rewind.reconstructed'].replace('{count}', '1'));
  await expect(page.locator('.jfh-rewind-stat').first()).toContainText('≈ 1');
  await expect(page.locator('.jfh-rewind-stat').last()).toContainText('—');
  await expect(page.locator('.jfh-rewind-estimated-bar')).toHaveCount(1);
  await expect(page.locator('.jfh-rewind-time-legend')).toContainText(strings['rewind.estimated']);
  await expect(page.locator('.jfh-rewind-patterns')).toContainText(strings['rewind.noMeasuredInsights']);
  await expect(page.locator('.jfh-rewind-patterns .jfh-rewind-distribution')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
 });
}
