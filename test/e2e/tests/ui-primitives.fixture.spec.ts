import { test, expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { CONFIG_PAGE_MODULES } from '../../ui/src/jsModules.ts';

const root = resolve(import.meta.dirname, '../../../Jellyfin.Plugin.JellyfinHelper');
const source = (path: string) => readFileSync(resolve(root, path), 'utf8');
const primitives = source('PluginPages/js/UiPrimitives.js');
const css = [...source('Jellyfin.Plugin.JellyfinHelper.csproj').matchAll(/<CssModule Include="PluginPages\\css\\([^"]+)"\/>/g)]
  .map(match => match[1])
  .map(name => source('PluginPages/css/' + name)).join('\n');

const discoveryRecommendations = [
  {
    TmdbId: 1,
    MediaType: 'movie',
    Title: 'Die außergewöhnliche Reise der Familie Wiśniewska-Kowalczyk',
    Overview: 'A family sets out on a winter journey through unfamiliar towns, hoping to reunite with an old friend. '
      + 'Missed trains and changing weather force them to reconsider their carefully arranged plans. '
      + 'Along the way, conversations with strangers reveal how differently each family member remembers the past. '
      + 'An unexpected invitation gives them a place to rest and a chance to address the disagreements they have avoided. '
      + 'By the time they reach their destination, the journey has become more important than the meeting itself.',
    Score: 0.8,
  },
  { TmdbId: 2, MediaType: 'movie', Title: 'Another title', Overview: 'Another description.', Score: 0.5 },
];

function discoveryFixtureScript(script: string): string {
  const anchor = /^[ \t]*initDiscoveryLifecycle\s*\(\s*\)\s*;[ \t]*\r?$/gm;
  const matches = script.match(anchor);
  if (matches?.length !== 1) throw new Error('Expected exactly one Discovery lifecycle initialization anchor');
  return script.replace(anchor, () => '    window.fixtureDiscovery = { renderCards, injectStyles, consumeDiscoveryItem };');
}

let pageErrors: string[];
test.beforeEach(({ page }) => {
  pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
});
test.afterEach(async ({ page }, info) => {
  expect(pageErrors).toEqual([]);
  if (process.env.JFH_UI_REVIEW_DIR) {
    const name = info.project.name + '-' + info.title.replaceAll(/[^a-zA-Z0-9]+/g, '-');
    await page.screenshot({ path: resolve(process.env.JFH_UI_REVIEW_DIR, name + '.png'), fullPage: true });
  }
});

async function stubApi(page: Page) {
  await page.evaluate(() => {
    const stats = { Libraries: [], Movies: [], TvShows: [], Music: [], Books: [], Other: [] };
    (window as any).ApiClient = {
      getUrl: (path: string) => path,
      accessToken: () => 'token',
      getCurrentUserId: () => 'u1',
      ajax: async (options: any) => {
        if (options.url.endsWith('/Recommendations')) return [{ UserId: 'u1', UserName: 'Ada', Recommendations: [] }];
        if (options.url.includes('RequestPermissions')) return { CanRequest: true, Profiles: [] };
        if (options.url.endsWith('/Request')) return { Success: true };
        if (options.url.includes('Statistics')) return stats;
        return {};
      },
    };
    window.fetch = async () => ({ status: 200, ok: true, json: async () => stats }) as Response;
  });
}

async function dashboard(page: Page) {
  await page.setContent('<html lang="en"><head></head><body></body></html>');
  await stubApi(page);
  const js = CONFIG_PAGE_MODULES.map(name => source('PluginPages/js/' + name)).join('\n');
  const template = source('PluginPages/configPage.template.html')
    .replace('/* CSS_CONTENT */', () => css)
    .replace('/* JS_CONTENT */', () => '(function(){' + js + '\n})();');
  await page.setContent('<style>body{font:16px system-ui;background:#141414;color:#eee}@media(prefers-color-scheme:light){body{background:#fff;color:#17171b}}</style>' + template);
  await expect(page.locator('.tab-btn[data-tab="settings"]')).toBeVisible();
}

async function discovery(page: Page) {
  await page.setContent('<html lang="en"><head></head><body><main id="cards"></main></body></html>');
  await page.addStyleTag({ content: 'body{font:16px system-ui;background:#141414;color:#eee}@media(prefers-color-scheme:light){body{background:#fff;color:#17171b}}' });
  await page.addStyleTag({ content: source('PluginPages/css/Shared.css') });
  await stubApi(page);
  const js = discoveryFixtureScript(source('js/discovery-sidebar.js'));
  await page.addScriptTag({ content: primitives + '\n' + js });
  await page.evaluate(recommendations => {
    const fixture = (window as any).fixtureDiscovery;
    fixture.injectStyles();
    fixture.renderCards(document.getElementById('cards'), { Recommendations: recommendations });
  }, discoveryRecommendations);
}

test('fixture bootstrap accepts formatting and rejects missing or duplicated initialization', () => {
  expect(discoveryFixtureScript('\t initDiscoveryLifecycle ( );\r\n')).toContain('window.fixtureDiscovery');
  expect(() => discoveryFixtureScript('function unrelated() {}')).toThrow('initialization anchor');
  expect(() => discoveryFixtureScript('initDiscoveryLifecycle();\ninitDiscoveryLifecycle();')).toThrow('initialization anchor');
});

for (const width of [320, 1280]) {
  for (const colorScheme of ['dark', 'light'] as const) {
    test(`dashboard ${width}px ${colorScheme}: modal focus and control sizing`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await dashboard(page);
      await page.locator('.tab-btn[data-tab="logs"]').click();
      const opener = page.locator('#btnLogsClear');
      await opener.click();
      const modal = page.getByRole('dialog', { name: 'Clear Logs' });
      await expect(modal).toBeVisible();
      await expect(modal.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
      await page.keyboard.press('Shift+Tab');
      await expect(modal.getByRole('button', { name: 'Clear', exact: true })).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(modal.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
      const rect = await modal.boundingBox();
      expect(rect!.x).toBeGreaterThanOrEqual(0);
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(width);
      await page.keyboard.press('Escape');
      await expect(modal).toHaveCount(0);
      await expect(opener).toBeFocused();
      await expect(page.locator('#JellyfinHelperConfigPage')).not.toHaveAttribute('inert');
      await page.locator('.tab-btn[data-tab="settings"]').click();
      const label = await page.locator('label[for="cfgTrickplayMode"]').boundingBox();
      const info = await page.locator('#taskDescBtn_cfgTrickplayMode').boundingBox();
      expect(Math.abs((info!.y + info!.height / 2) - (label!.y + label!.height / 2))).toBeLessThanOrEqual(5);
      expect(info!.height).toBeLessThanOrEqual(label!.height + 2);
    });

    test(`Discovery ${width}px ${colorScheme}: details and stable cards`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await discovery(page);
      const card = page.locator('.jfh-discovery-card').first();
      const before = await card.boundingBox();
      const button = page.locator('.jfh-discovery-details').first();
      await button.focus();
      await page.keyboard.press('Enter');
      await expect(button).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('.jfh-discovery-flip-back').first()).not.toHaveAttribute('inert');
      const transform = await page.locator('.jfh-discovery-flip-inner').first().evaluate(element => getComputedStyle(element).transform);
      expect(transform).toBe('none');
      const rect = await card.boundingBox();
      expect(rect!.width).toBeCloseTo(before!.width, 5);
      expect(rect!.height).toBeCloseTo(before!.height, 5);
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(width);
      const scrolling = await card.evaluate(element => {
        const back = element.querySelector('.jfh-discovery-flip-back') as HTMLElement;
        const reference = document.createElement('div');
        reference.className = 'tree-view';
        reference.hidden = true;
        element.appendChild(reference);
        const style = getComputedStyle(back);
        const treeStyle = getComputedStyle(reference);
        const result = {
          width: style.scrollbarWidth,
          color: style.scrollbarColor,
          treeWidth: treeStyle.scrollbarWidth,
          treeColor: treeStyle.scrollbarColor,
          overflows: back.scrollHeight > back.clientHeight,
        };
        back.scrollTop = back.scrollHeight;
        reference.remove();
        return { ...result, scrollTop: back.scrollTop };
      });
      expect(scrolling.width).toBe('thin');
      expect(scrolling.width).toBe(scrolling.treeWidth);
      expect(scrolling.color).toBe(scrolling.treeColor);
      expect(scrolling.overflows).toBe(true);
      expect(scrolling.scrollTop).toBeGreaterThan(0);
      const frame = await card.evaluate(element => {
        const style = getComputedStyle(element, '::after');
        return {
          borders: [style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth, style.borderLeftWidth],
          colors: [style.borderTopColor, style.borderRightColor, style.borderBottomColor, style.borderLeftColor],
          pointerEvents: style.pointerEvents,
          zIndex: style.zIndex,
          radius: style.borderRadius,
          cardRadius: getComputedStyle(element).borderRadius,
        };
      });
      expect(frame.borders).toEqual(['1px', '1px', '1px', '1px']);
      expect(new Set(frame.colors).size).toBe(1);
      expect(frame.colors[0]).not.toBe('rgba(0, 0, 0, 0)');
      expect(frame.pointerEvents).toBe('none');
      expect(Number(frame.zIndex)).toBeGreaterThan(0);
      expect(frame.radius).toBe(frame.cardRadius);
      const original = await card.elementHandle();
      await page.evaluate(recommendations => {
        (window as any).fixtureDiscovery.renderCards(document.getElementById('cards'), { Recommendations: recommendations });
      }, discoveryRecommendations);
      expect(await card.evaluate((element, previous) => element === previous, original)).toBe(true);
      await expect(button).toBeFocused();
      expect(await card.locator('.jfh-discovery-flip-back').evaluate(element => element.scrollTop)).toBeGreaterThan(0);
    });
  }
}

test.describe('touch controls', () => {
  test.use({ hasTouch: true });
  test('inputs and save band respect reduced motion at 200% text size', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await dashboard(page);
    await page.addStyleTag({ content: '#JellyfinHelperConfigPage{font-size:200%}' });
    await page.locator('.tab-btn[data-tab="settings"]').click();
    const input = page.locator('#cfgOrphanAge');
    await expect(input).toBeVisible();
    expect(await input.evaluate(element => Number.parseFloat(getComputedStyle(element).fontSize))).toBeGreaterThanOrEqual(32);
    await input.fill('45');
    const band = page.locator('#settingsSaveBand');
    await expect(band).toHaveClass(/is-visible/);
    const transitions = await band.evaluate(element => getComputedStyle(element).transitionProperty);
    expect(transitions).not.toContain('transform');
    const rect = await band.boundingBox();
    expect(rect!.x).toBeGreaterThanOrEqual(0);
    expect(rect!.x + rect!.width).toBeLessThanOrEqual(320);
  });

  test('log filter controls retain their shared column', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await dashboard(page);
    await page.locator('.tab-btn[data-tab="logs"]').click();
    const level = await page.locator('#logsLevelFilter').boundingBox();
    const sourceControl = await page.locator('#logsSourceFilter').boundingBox();
    expect(Math.abs(level!.x - sourceControl!.x)).toBeLessThanOrEqual(2);
    expect(level!.width).toBeLessThan(sourceControl!.width);
  });
});
