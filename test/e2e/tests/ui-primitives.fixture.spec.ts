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

const discoveryLocales = ['de', 'en', 'es', 'fr', 'pt', 'sv', 'tr', 'zh'].map(language => {
  const translations = JSON.parse(source('i18n/' + language + '.json'));
  return { language, strings: Object.fromEntries(['discoveryRequest', 'discoveryAlreadyRequested', 'discoveryDismiss', 'discoveryColdStart']
    .map(key => [key, translations[key]])) };
});

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
    AvailabilityVerified: true,
  },
  { TmdbId: 2, MediaType: 'movie', Title: 'Another title', Overview: 'Another description.', Score: 0.5, AvailabilityVerified: true },
];

function discoveryFixtureScript(script: string): string {
  const anchor = /^[ \t]*initDiscoveryLifecycle\s*\(\s*\)\s*;[ \t]*\r?$/gm;
  const matches = script.match(anchor);
  if (matches?.length !== 1) throw new Error('Expected exactly one Discovery lifecycle initialization anchor');
  return script.replace(anchor, () => `    window.fixtureDiscovery = {
         renderCards, injectStyles, consumeDiscoveryItem, submitRequest,
         renderLocalizedCards: function (container, recommendations, strings) {
             _strings = strings;
             renderCards(container, { Recommendations: recommendations });
         },
        renderTabs: function (container) {
            _traktEnabled = true;
            _traktLinked = true;
            _strings = { discoveryTabForYou: 'Für dich', discoveryTabTraktForYou: 'Trakt für dich', discoveryTabTraktTrending: 'Trakt Trending' };
            renderShell(container, false);
        }
    };`);
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
        if (options.url.endsWith('/Discovery') || options.url.includes('/UserActivity/User/')) return [];
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
    .replace('/* JS_CONTENT */', () => '(function(){' + js
     + '\nwindow.fixtureAdmin = { fillCodecsData, renderInsightCards, renderFileTree, bindFileTreeHandlers, refreshDiscoveryAccessState, initArrButtons, handleDiscoveryRequestResponse, renderLocalizedDiscoveryCard: function (candidate, strings) { _translations = strings; return renderDiscoveryCard(candidate, 0); } };\n})();');
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

test('requested-card success animation stays in its source on immediate Trakt switches', async ({ page }) => {
  await page.clock.install();
  await discovery(page);
  await page.evaluate(() => {
    const pool = (ids: number[]) => ({ DeliveryId: '00000000-0000-0000-0000-000000000001',
      Recommendations: ids.map(id => ({ TmdbId: id, MediaType: 'movie', Title: 'Movie ' + id, Score: 0.8 })) });
    const api = (window as any).ApiClient;
    (window as any).fixtureRequests = [];
    api.ajax = async (options: any) => {
      (window as any).fixtureRequests.push(options);
      if (options.url.endsWith('/Request')) return { Success: true };
      if (options.url.includes('RequestPermissions')) return { CanRequest: true, Profiles: [] };
      if (options.url.endsWith('/Trakt/Trending')) return pool([4, 2]);
      if (options.url.endsWith('/Trakt')) return { Linked: true, Result: pool([3, 2]) };
      return pool([1, 2]);
    };
    (window as any).fixtureDiscovery.renderTabs(document.getElementById('cards'));
  });
  await page.locator('.jfh-discovery-card[data-tmdb="1"] [data-jfh-action="request"]').click();
  await expect(page.locator('.jfh-discovery-btn-done')).toBeVisible();
  await page.evaluate(() => {
    (window as any).fixtureRequestedCard = document.querySelector('.jfh-discovery-card[data-tmdb="1"]');
  });
  await page.locator('[data-tab="trakt"]').click();
  await expect.poll(() => page.evaluate(() => (window as any).fixtureRequestedCard._jfhConsumeTimer)).toBeNull();
  const cards = page.locator('.jfh-discovery-tab-host .jfh-discovery-card');
  await expect(cards).toHaveCount(2);
  expect(await cards.evaluateAll(elements => elements.map(el => (el as HTMLElement).dataset.tmdb))).toEqual(['3', '2']);
  await page.locator('[data-tab="trending"]').click();
  await expect(cards).toHaveCount(2);
  expect(await cards.evaluateAll(elements => elements.map(el => (el as HTMLElement).dataset.tmdb))).toEqual(['4', '2']);
  // Observe beyond the original 5-second deadline; the old callback must not refill the current source.
  const beforeRequests = await page.evaluate(() => (window as any).fixtureRequests.slice());
  const actions = (requests: any[]) => requests.filter(request => !request.url.endsWith('/Impressions')).map(request => request.type + ' ' + request.url);
  await page.clock.fastForward(5300);
  expect(await cards.evaluateAll(elements => elements.map(el => (el as HTMLElement).dataset.tmdb))).toEqual(['4', '2']);
  const afterRequests = await page.evaluate(() => (window as any).fixtureRequests.slice());
  expect(actions(afterRequests)).toEqual(actions(beforeRequests));
  // Native IntersectionObserver callbacks can arrive during clock advancement, but only for this source.
  const impressions = afterRequests.slice(beforeRequests.length).filter((request: any) => request.url.endsWith('/Impressions'));
  for (const request of impressions) {
    for (const item of JSON.parse(request.data).Items) {
      expect(['movie-4', 'movie-2']).toContain(item.MediaType + '-' + item.TmdbId);
    }
  }
});

async function controlSizes(page: Page, selector: string) {
  return page.locator(selector).evaluateAll(elements => elements.map(element => {
    const rect = element.getBoundingClientRect();
    return { width: rect.width, height: rect.height, fontSize: getComputedStyle(element).fontSize };
  }));
}

test('fixture bootstrap accepts formatting and rejects missing or duplicated initialization', () => {
  expect(discoveryFixtureScript('\t initDiscoveryLifecycle ( );\r\n')).toContain('window.fixtureDiscovery');
  expect(() => discoveryFixtureScript('function unrelated() {}')).toThrow('initialization anchor');
  expect(() => discoveryFixtureScript('initDiscoveryLifecycle();\ninitDiscoveryLifecycle();')).toThrow('initialization anchor');
});

for (const width of [320, 1280]) {
  test(`dashboard ${width}px: cold-start Discovery labels do not block recommendation toggles`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await dashboard(page);
    await page.locator('.tab-btn[data-tab="recommendations"]').click();
    const gridToggle = page.locator('#recsGridToggle');
    await expect(gridToggle).toBeVisible();
    for (const locale of discoveryLocales) {
      await page.evaluate(strings => {
        document.getElementById('discoverySection')!.innerHTML = (window as any).fixtureAdmin.renderLocalizedDiscoveryCard(
          { Title: 'Popular suggestion', IsPersonalized: false }, strings);
      }, locale.strings);
      const label = page.locator('#discoverySection .recs-score-text');
      await expect(label).toHaveText(locale.strings.discoveryColdStart);
      await expect(label).toHaveCSS('position', 'static');
      const labelBox = await label.boundingBox();
      const cardBox = await page.locator('#discoverySection .discovery-card').boundingBox();
      expect(labelBox!.y).toBeGreaterThanOrEqual(cardBox!.y);
      expect(labelBox!.y + labelBox!.height).toBeLessThanOrEqual(cardBox!.y + cardBox!.height);
      await gridToggle.click();
      await expect(gridToggle).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('#recsGridBody')).toBeVisible();
      await gridToggle.click();
      await expect(gridToggle).toHaveAttribute('aria-expanded', 'false');
      await expect(page.locator('#recsGridBody')).toBeHidden();
    }
  });
}

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

    test(`default toast ${width}px ${colorScheme}: standalone styling and pointer dismissal`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await page.setContent('<html lang="en"><head></head><body></body></html>');
      await page.addScriptTag({ content: primitives });
      await page.evaluate(() => (window as any).JellyfinHelperUi.showToast('Connection failed. Please check the server address and permissions.'));
      const toast = page.getByRole('alert');
      const close = toast.getByRole('button', { name: 'Close', exact: true });
      await expect(toast).toBeVisible();
      await expect(close).toBeVisible();
      const styles = await toast.evaluate(element => {
        const style = getComputedStyle(element);
        const button = element.querySelector('button')!;
        return {
          position: style.position,
          pointerEvents: style.pointerEvents,
          closePointerEvents: getComputedStyle(button).pointerEvents,
          background: style.backgroundColor,
          color: style.color,
          transform: style.transform,
        };
      });
      expect(styles.position).toBe('relative');
      expect(styles.pointerEvents).toBe('auto');
      expect(styles.closePointerEvents).toBe('auto');
      expect(styles.background).toBe(colorScheme === 'light' ? 'rgb(255, 255, 255)' : 'rgb(28, 28, 30)');
      expect(styles.color).toBe(colorScheme === 'light' ? 'rgb(23, 23, 27)' : 'rgb(238, 238, 238)');
      expect(styles.transform).toBe('none');
      const rect = await toast.boundingBox();
      const closeRect = await close.boundingBox();
      expect(rect!.x).toBeGreaterThanOrEqual(0);
      expect(rect!.x + rect!.width).toBeLessThanOrEqual(width);
      expect(closeRect!.x).toBeGreaterThanOrEqual(rect!.x);
      expect(closeRect!.x + closeRect!.width).toBeLessThanOrEqual(rect!.x + rect!.width);
      expect(closeRect!.y).toBeGreaterThanOrEqual(rect!.y);
      expect(closeRect!.y + closeRect!.height).toBeLessThanOrEqual(rect!.y + rect!.height);
      await close.click();
      await expect(toast).toHaveCount(0);
      await expect(page.locator('#jfh-toast-stack')).toHaveCount(0);
    });

    test(`Discovery ${width}px ${colorScheme}: details and stable cards`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await discovery(page);
      const card = page.locator('.jfh-discovery-card').first();
      const before = await card.boundingBox();
      await expect(card.locator('.jfh-discovery-card-body button')).toHaveCount(2);
      const button = card.getByRole('button', { name: discoveryRecommendations[0].Title, exact: true });
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
      const link = card.getByRole('link', { name: 'TMDB' });
      await link.focus();
      await page.keyboard.press('Escape');
      await expect(button).toHaveAttribute('aria-expanded', 'false');
      await expect(button).toBeFocused();
      await expect(card.locator('.jfh-discovery-flip-back')).toHaveAttribute('inert');
      await page.keyboard.press('Space');
      await expect(button).toHaveAttribute('aria-expanded', 'true');
      await card.locator('.jfh-discovery-flip-back-text').click();
      await expect(button).toHaveAttribute('aria-expanded', 'false');
      await button.click();
      await expect(button).toHaveAttribute('aria-expanded', 'true');
      await page.context().route('https://www.themoviedb.org/**', route => route.fulfill({ body: 'Movie details' }));
      const popup = page.waitForEvent('popup');
      await link.click();
      const external = await popup;
      await external.close();
      await expect(button).toHaveAttribute('aria-expanded', 'true');
    });

    test(`Discovery ${width}px ${colorScheme}: missing enrichment keeps usable Request and Dismiss controls`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await discovery(page);
      for (const locale of discoveryLocales) {
        for (const verified of [false, undefined]) {
          await page.evaluate(({ recommendations, locale, verified }) => {
            document.documentElement.lang = locale.language;
            (window as any).fixtureDiscovery.renderLocalizedCards(document.getElementById('cards'),
              recommendations.map(candidate => ({ ...candidate, AvailabilityVerified: verified })), locale.strings);
          }, { recommendations: discoveryRecommendations, locale, verified });
          const card = page.locator('.jfh-discovery-card').first();
          const request = card.locator('[data-jfh-action="request"]');
          const dismiss = card.locator('[data-jfh-action="dismiss"]');
          await expect(request).toBeVisible();
          await expect(request).toBeEnabled();
          await expect(request).toHaveText(locale.strings.discoveryRequest);
          await expect(dismiss).toBeEnabled();
          await expect(card.locator('.jfh-discovery-availability')).toHaveCount(0);
          const layout = await card.evaluate(element => {
            const row = element.querySelector('.jfh-discovery-btn-row')!;
            const request = row.querySelector('[data-jfh-action="request"]')!;
            const dismiss = row.querySelector('[data-jfh-action="dismiss"]')!;
            const rowBox = row.getBoundingClientRect();
            const requestBox = request.getBoundingClientRect();
            const dismissBox = dismiss.getBoundingClientRect();
            return { cardOverflow: element.scrollWidth - element.clientWidth, rowOverflow: row.scrollWidth - row.clientWidth,
              requestOverflow: request.scrollWidth - request.clientWidth, dismissOverflow: dismiss.scrollWidth - dismiss.clientWidth,
              requestRight: requestBox.right, dismissLeft: dismissBox.left, requestBottom: requestBox.bottom,
              dismissTop: dismissBox.top, rowWidth: rowBox.width, requestWidth: requestBox.width, dismissWidth: dismissBox.width };
          });
          expect(layout.cardOverflow).toBeLessThanOrEqual(1);
          expect(layout.rowOverflow).toBeLessThanOrEqual(1);
          expect(layout.requestOverflow).toBeLessThanOrEqual(1);
          expect(layout.dismissOverflow).toBeLessThanOrEqual(1);
          expect(layout.requestRight <= layout.dismissLeft || layout.requestBottom <= layout.dismissTop).toBe(true);
          expect(layout.requestWidth).toBeGreaterThan(40);
          expect(layout.dismissWidth).toBeGreaterThan(40);
          expect(layout.dismissWidth).toBeLessThanOrEqual(layout.rowWidth);
          await dismiss.focus();
          await page.keyboard.press('Enter');
          await expect(page.locator('#jfhDiscoveryPopup')).toBeVisible();
          await page.keyboard.press('Escape');
          await expect(page.locator('#jfhDiscoveryPopup')).toHaveCount(0);
          await expect(dismiss).toBeFocused();
        }
      }
    });

    test(`dashboard ${width}px ${colorScheme}: Request remains usable without optional enrichment`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await dashboard(page);
      for (const locale of discoveryLocales) {
        await page.evaluate(({ candidate, locale }) => {
          document.documentElement.lang = locale.language;
          let host = document.getElementById('fixtureAvailabilityCards');
          if (!host) {
            host = document.createElement('div');
            host.id = 'fixtureAvailabilityCards';
            document.getElementById('JellyfinHelperConfigPage')!.prepend(host);
          }
          host.innerHTML = (window as any).fixtureAdmin.renderLocalizedDiscoveryCard({ ...candidate, AvailabilityVerified: false }, locale.strings);
        }, { candidate: discoveryRecommendations[0], locale });
        const card = page.locator('#fixtureAvailabilityCards .discovery-card');
        const request = card.locator('.discovery-request-btn');
        await expect(request).toBeVisible();
        await expect(request).toBeEnabled();
        await expect(request).toContainText(locale.strings.discoveryRequest);
        await expect(card.locator('.discovery-availability')).toHaveCount(0);
        expect(await request.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
        expect(await card.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
        const box = await card.boundingBox();
        expect(box!.x).toBeGreaterThanOrEqual(0);
        expect(box!.x + box!.width).toBeLessThanOrEqual(width);
      }
    });
  }
}

for (const view of ['sidebar', 'admin'] as const) {
  for (const outcome of ['duplicate', 'failure'] as const) {
    test(`${view}: ${outcome} request outcome preserves the correct toast and card lifecycle`, async ({ page }) => {
      await page.clock.install();
      await page.emulateMedia({ reducedMotion: 'reduce' });
      if (view === 'sidebar') await discovery(page);
      else await dashboard(page);
      await page.evaluate(({ candidate, view, outcome }) => {
        const result = { Success: outcome === 'duplicate', AlreadyRequested: true, Message: 'Seerr status check failed' };
        if (view === 'sidebar') {
          const api = (window as any).ApiClient;
          api.ajax = async () => result;
          const fixture = (window as any).fixtureDiscovery;
          fixture.renderLocalizedCards(document.getElementById('cards'), [{ ...candidate, AvailabilityVerified: false }],
            { discoveryAlreadyRequested: 'Titel wurde bereits angefragt' });
          fixture.submitRequest(candidate.TmdbId, candidate.MediaType, null, null, null, document.querySelector('[data-jfh-action="request"]'));
        } else {
          document.getElementById('discoveryCount')?.remove();
          const host = document.createElement('div');
          host.id = 'fixtureRequests';
          host.innerHTML = '<div class="discovery-grid">' + (window as any).fixtureAdmin.renderLocalizedDiscoveryCard(
            { ...candidate, AvailabilityVerified: false }, { discoveryAlreadyRequested: 'Titel wurde bereits angefragt' }) + '</div><span id="discoveryCount">1</span>';
          document.getElementById('JellyfinHelperConfigPage')!.prepend(host);
          (window as any).fixtureAdmin.handleDiscoveryRequestResponse(result, host.querySelector('.discovery-request-btn'), candidate.TmdbId, candidate.MediaType);
        }
      }, { candidate: discoveryRecommendations[0], view, outcome });
      const card = page.locator(view === 'sidebar' ? '.jfh-discovery-card' : '#fixtureRequests .discovery-card');
      const toast = page.locator(view === 'sidebar' ? '.jfh-discovery-toast' : '.jfh-toast');
      if (outcome === 'duplicate') {
        await expect(toast).toContainText('Titel wurde bereits angefragt');
        await expect(card.locator(view === 'sidebar' ? '[data-jfh-action="request"]' : '.discovery-request-btn')).toBeDisabled();
        await page.clock.runFor(5400);
        await expect(card).toHaveCount(0);
        if (view === 'admin') await expect(page.locator('#discoveryCount')).toHaveText('0');
      } else {
        await expect(toast.filter({ hasText: 'Titel wurde bereits angefragt' })).toHaveCount(0);
        await page.clock.runFor(5400);
        await expect(card).toBeVisible();
        await expect(card.locator(view === 'sidebar' ? '[data-jfh-action="request"]' : '.discovery-request-btn')).toBeEnabled();
        if (view === 'admin') await expect(page.locator('#discoveryCount')).toHaveText('1');
      }
    });
  }
}

test.describe('touch controls', () => {
  test.use({ hasTouch: true });

  for (const width of [320, 390]) {
    for (const colorScheme of ['dark', 'light'] as const) {
      test(`dashboard ${width}px ${colorScheme}: touch keeps Settings consistent and Arr compact`, async ({ page, browser }) => {
        await page.setViewportSize({ width, height: 844 });
        await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
        const referenceContext = await browser.newContext({ viewport: { width, height: 844 }, colorScheme, reducedMotion: 'reduce', hasTouch: false });
        try {
          const reference = await referenceContext.newPage();
          reference.on('pageerror', error => pageErrors.push(error.message));
          for (const fixture of [page, reference]) {
            await dashboard(fixture);
            await fixture.locator('.tab-btn[data-tab="settings"]').click();
          }
          const fields = '#cfgOrphanAge, #cfgLang, #cfgTrickplayMode, #cfgSeerrUrl, #cfgSeerrApiKey, #cfgExcludedWrapper .library-multiselect-toggle';
          const expectedFields = await controlSizes(reference, fields);
          expect(expectedFields).toHaveLength(6);
          const touchFields = await controlSizes(page, fields);
          expect(touchFields.map(field => field.width)).toEqual(expectedFields.map(field => field.width));
          expect(touchFields.map(field => field.fontSize)).toEqual(Array(6).fill('16px'));
          const heights = touchFields.map(field => field.height);
          expect(Math.max(...heights) - Math.min(...heights)).toBeLessThanOrEqual(2);
          expect(Math.max(...heights)).toBeLessThan(44);
          for (const fixture of [page, reference]) {
            await fixture.locator('.tab-btn[data-tab="arr"]').click();
            await fixture.evaluate(() => (window as any).fixtureAdmin.initArrButtons({
              RadarrInstances: [{ Name: 'Radarr', Url: 'http://radarr:7878', ApiKey: 'key' }],
              SonarrInstances: [{ Name: 'Sonarr', Url: 'http://sonarr:8989', ApiKey: 'key' }],
            }));
          }
          const expectedButtons = await controlSizes(reference, '.arr-compare-btn');
          expect(expectedButtons).toHaveLength(2);
          expect(await controlSizes(page, '.arr-compare-btn')).toEqual(expectedButtons);
          for (const button of expectedButtons) expect(button.height).toBeLessThan(44);
        } finally {
          await referenceContext.close();
        }
      });

      test(`Discovery ${width}px ${colorScheme}: touch preserves compact subtab sizes`, async ({ page, browser }) => {
        await page.setViewportSize({ width, height: 844 });
        await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
        const referenceContext = await browser.newContext({ viewport: { width, height: 844 }, colorScheme, reducedMotion: 'reduce', hasTouch: false });
        try {
          const reference = await referenceContext.newPage();
          reference.on('pageerror', error => pageErrors.push(error.message));
          for (const fixture of [page, reference]) {
            await discovery(fixture);
            await fixture.evaluate(() => (window as any).fixtureDiscovery.renderTabs(document.getElementById('cards')));
            await expect(fixture.getByRole('tab')).toHaveCount(3);
          }
          const expectedTabs = await controlSizes(reference, '.jfh-discovery-tab');
          expect(await controlSizes(page, '.jfh-discovery-tab')).toEqual(expectedTabs);
          expect(await controlSizes(page, '.jfh-discovery-tabs')).toEqual(await controlSizes(reference, '.jfh-discovery-tabs'));
          for (const tab of expectedTabs) expect(tab.height).toBeLessThan(44);
          await page.getByRole('tab', { name: 'Trakt Trending', exact: true }).tap();
          await expect(page.getByRole('tab', { name: 'Trakt Trending', exact: true })).toHaveAttribute('aria-selected', 'true');
        } finally {
          await referenceContext.close();
        }
      });
    }
  }

  test('editable fields prevent focus zoom and scale without enlarging other controls', async ({ page }) => {
    await page.setContent(`<style>body{font:16px system-ui}input,textarea,select,button{font-size:.85em}</style>
      <main class="jfh-ui-surface">
        <input class="editable"><input class="editable" type="text"><input class="editable" type="number">
        <input class="editable" type="password"><input class="editable" type="search"><input class="editable" type="email">
        <input class="editable" type="url"><input class="editable" type="tel"><textarea class="editable"></textarea>
        <input class="unchanged" type="checkbox"><input class="unchanged" type="radio"><input class="unchanged" type="range">
        <select class="unchanged"><option>Default</option></select><button class="unchanged">Action</button>
      </main><input id="hostInput" type="text">`);
    const before = await controlSizes(page, '.unchanged, #hostInput');
    await page.addScriptTag({ content: primitives });
    expect(await controlSizes(page, '.unchanged, #hostInput')).toEqual(before);
    expect((await controlSizes(page, '.editable')).map(field => field.fontSize)).toEqual(Array(9).fill('16px'));
    await page.locator('.editable').first().focus();
    await expect(page.locator('.editable').first()).toBeFocused();
    await page.addStyleTag({ content: '.jfh-ui-surface{font-size:200%}' });
    expect((await controlSizes(page, '.editable')).map(field => field.fontSize)).toEqual(Array(9).fill('32px'));
  });

  test('folder browser field retains the touch font minimum without overriding desktop sizing', async ({ page, browser }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const referenceContext = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: false });
    try {
      const reference = await referenceContext.newPage();
      reference.on('pageerror', error => pageErrors.push(error.message));
      for (const fixture of [page, reference]) {
        await dashboard(fixture);
        await fixture.locator('.tab-btn[data-tab="settings"]').click();
        await fixture.locator('#cfgTrash').check();
        await fixture.locator('#btnBrowseTrash').click();
        await expect(fixture.locator('#folderBrowserNewName')).toBeVisible();
      }
      expect((await controlSizes(page, '#folderBrowserNewName'))[0].fontSize).toBe('16px');
      expect(Number.parseFloat((await controlSizes(reference, '#folderBrowserNewName'))[0].fontSize)).toBeCloseTo(14.4, 1);
      for (const fixture of [page, reference]) {
        await fixture.addStyleTag({ content: 'body{font-size:200%}' });
        expect(Number.parseFloat((await controlSizes(fixture, '#folderBrowserNewName'))[0].fontSize)).toBeCloseTo(28.8, 1);
        await fixture.locator('#folderBrowserCancel').click();
        await expect(fixture.locator('#btnBrowseTrash')).toBeFocused();
      }
    } finally {
      await referenceContext.close();
    }
  });

  test('Discovery poster taps open and close details without a separate button', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await discovery(page);
    const card = page.locator('.jfh-discovery-card').first();
    const poster = card.locator('.jfh-discovery-poster-toggle');
    await expect(card.locator('.jfh-discovery-card-body button')).toHaveCount(2);
    await poster.tap();
    await expect(poster).toHaveAttribute('aria-expanded', 'true');
    await card.locator('.jfh-discovery-flip-back-text').tap();
    await expect(poster).toHaveAttribute('aria-expanded', 'false');
  });

  test('inputs and save band respect reduced motion at 200% text size', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await dashboard(page);
    await page.addStyleTag({ content: '#JellyfinHelperConfigPage{font-size:200%}' });
    await page.locator('.tab-btn[data-tab="settings"]').click();
    const input = page.locator('#cfgOrphanAge');
    await expect(input).toBeVisible();
    const fontSizes = await input.evaluate(element => ({
      input: Number.parseFloat(getComputedStyle(element).fontSize),
      form: Number.parseFloat(getComputedStyle(element.closest('.settings-form')!).fontSize),
    }));
    expect(fontSizes.form).toBe(32);
    expect(fontSizes.input).toBeCloseTo(fontSizes.form * 0.95, 1);
    const fields = await controlSizes(page, '#cfgLang, #cfgSeerrUrl, #cfgSeerrApiKey, #cfgExcludedWrapper .library-multiselect-toggle');
    expect(fields).toHaveLength(4);
    for (const field of fields) expect(Number.parseFloat(field.fontSize)).toBeCloseTo(fontSizes.input, 1);
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

async function recordPanelMotion(page: Page) {
  await page.evaluate(() => {
    const nativeAnimate = Element.prototype.animate;
    (window as any).fixturePanelMotion = [];
    Element.prototype.animate = function (frames, options) {
      const animation = nativeAnimate.call(this, frames, options);
      (window as any).fixturePanelMotion.push({
        id: this.id,
        frames: animation.effect instanceof KeyframeEffect ? animation.effect.getKeyframes() : [],
        duration: animation.effect?.getTiming().duration,
        hidden: (this as HTMLElement).hidden,
        inert: this.hasAttribute('inert'),
        ariaHidden: this.getAttribute('aria-hidden'),
      });
      return animation;
    };
  });
}

test('admin panels and dropdowns use short native transitions with immediate accessibility state', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await recordPanelMotion(page);
  await page.locator('.tab-btn[data-tab="settings"]').click();
  const header = page.locator('#arrCollapsibleHeaderSeerr');
  const body = page.locator('#arrCollapsibleBodySeerr');
  await header.click();
  await expect(header).toHaveAttribute('aria-expanded', 'false');
  await expect(body).toBeHidden();
  const closing = await page.evaluate(() => (window as any).fixturePanelMotion.find((entry: any) => entry.id === 'arrCollapsibleBodySeerr'));
  expect(closing.hidden).toBe(false);
  expect(closing.inert).toBe(true);
  expect(closing.ariaHidden).toBe('true');
  expect(closing.duration).toBeGreaterThan(0);
  expect(closing.duration).toBeLessThanOrEqual(200);
  expect(Number.parseFloat(closing.frames[0].height)).toBeGreaterThan(0);
  expect(closing.frames.at(-1).height).toBe('0px');
  await header.click();
  await expect(body).toBeVisible();
  await expect(body).not.toHaveAttribute('inert');
  await expect.poll(() => body.evaluate(element => element.getAnimations().length)).toBe(0);
  await page.locator('.library-multiselect-toggle').first().click();
  const dropdown = page.locator('.library-multiselect-panel').first();
  await expect(dropdown).toBeVisible();
  await expect.poll(() => dropdown.evaluate(element => element.getAnimations().length)).toBe(0);
  await page.locator('.library-multiselect-toggle').first().click();
  await expect(dropdown).toBeHidden();
  await page.locator('.tab-btn[data-tab="recommendations"]').click();
  await page.locator('#recsGridToggle').click();
  await expect(page.locator('#recsGridToggle')).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#recsGridBody')).toBeVisible();
  const records = await page.evaluate(() => (window as any).fixturePanelMotion);
  expect(records.some((entry: any) => entry.id === 'recsGridBody' && entry.ariaHidden === 'false')).toBe(true);
  expect(records.some((entry: any) => entry.duration > 0 && entry.duration <= 160 && entry.id === '')).toBe(true);
});

test('panel reversal preserves presentation height and cancels stale closing cleanup', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await page.locator('.tab-btn[data-tab="settings"]').click();
  await page.clock.install();
  const result = await page.evaluate(() => {
    const ui = (window as any).JellyfinHelperUi;
    const body = document.getElementById('arrCollapsibleBodySeerr')!;
    const parent = body.parentElement!;
    const trigger = document.getElementById('arrCollapsibleHeaderSeerr')!;
    const input = body.querySelector('input');
    const options = (expanded: boolean) => ({ update: () => parent.classList.toggle('arr-expanded', expanded) });
    ui.setExpanded(body, false, trigger, options(false));
    const closing = body.getAnimations()[0];
    closing.pause();
    closing.currentTime = 80;
    const before = body.getBoundingClientRect().height;
    ui.setExpanded(body, true, trigger, options(true));
    const opening = body.getAnimations()[0];
    opening.pause();
    opening.currentTime = 0;
    const after = body.getBoundingClientRect().height;
    opening.finish();
    return { before, after, preservedInput: input === body.querySelector('input'), closedState: closing.playState };
  });
  expect(result.before).toBeGreaterThan(0);
  expect(result.after).toBeCloseTo(result.before, 1);
  expect(result.preservedInput).toBe(true);
  expect(result.closedState).toBe('idle');
  await page.clock.runFor(300);
  await expect(page.locator('#arrCollapsibleBodySeerr')).toBeVisible();
  await expect(page.locator('#arrCollapsibleBodySeerr')).not.toHaveAttribute('inert');
  expect(await page.locator('#arrCollapsibleBodySeerr').evaluate(element => element.getAnimations().length)).toBe(0);
});

test('reduced motion keeps panel and dropdown changes immediate', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await dashboard(page);
  await recordPanelMotion(page);
  await page.locator('.tab-btn[data-tab="settings"]').click();
  await page.locator('#arrCollapsibleHeaderSeerr').click();
  await expect(page.locator('#arrCollapsibleBodySeerr')).toBeHidden();
  await page.locator('.library-multiselect-toggle').first().click();
  await expect(page.locator('.library-multiselect-panel').first()).toBeVisible();
  expect(await page.evaluate(() => (window as any).fixturePanelMotion.length)).toBe(0);
});

test('disabling Discovery cancels hint motion and the first click after reenabling opens it', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await page.locator('.tab-btn[data-tab="settings"]').click();
  await page.clock.install();
  const closing = await page.evaluate(() => {
    const mode = document.getElementById('cfgRecommendationsMode') as HTMLSelectElement;
    mode.value = 'Activate';
    (document.getElementById('cfgSeerrUrl') as HTMLInputElement).value = 'http://seerr:5055';
    (document.getElementById('cfgSeerrApiKey') as HTMLInputElement).value = 'key';
    const refresh = (window as any).fixtureAdmin.refreshDiscoveryAccessState;
    refresh();
    const trigger = document.getElementById('btnToggleDiscoveryHint')!;
    const panel = document.getElementById('discoveryHintPanel')!;
    trigger.click();
    const opening = panel.getAnimations()[0];
    opening.pause();
    opening.currentTime = 60;
    const height = panel.getBoundingClientRect().height;
    mode.value = 'Deactivate';
    refresh();
    const state = {
      height,
      hidden: panel.hidden,
      display: panel.style.display,
      inert: panel.hasAttribute('inert'),
      ariaHidden: panel.getAttribute('aria-hidden'),
      expanded: trigger.getAttribute('aria-expanded'),
      animations: panel.getAnimations().length,
      openingState: opening.playState,
    };
    mode.value = 'Activate';
    refresh();
    return state;
  });
  expect(closing.height).toBeGreaterThan(0);
  expect(closing.hidden).toBe(true);
  expect(closing.display).toBe('none');
  expect(closing.inert).toBe(true);
  expect(closing.ariaHidden).toBe('true');
  expect(closing.expanded).toBe('false');
  expect(closing.animations).toBe(0);
  expect(closing.openingState).toBe('idle');
  const trigger = page.locator('#btnToggleDiscoveryHint');
  const panel = page.locator('#discoveryHintPanel');
  await trigger.click();
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');
  await expect(panel).toHaveAttribute('aria-hidden', 'false');
  await page.clock.runFor(300);
  await expect(panel).toBeVisible();
  await expect(panel).not.toHaveAttribute('inert');
  expect(await panel.evaluate(element => element.getAnimations().length)).toBe(0);
});

test('opening panels retarget when asynchronous content arrives', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await page.locator('.tab-btn[data-tab="settings"]').click();
  await page.clock.install();
  const initialHeight = await page.evaluate(() => {
    const ui = (window as any).JellyfinHelperUi;
    const body = document.getElementById('arrCollapsibleBodySeerr')!;
    const parent = body.parentElement!;
    const trigger = document.getElementById('arrCollapsibleHeaderSeerr')!;
    parent.classList.remove('arr-expanded');
    ui.setExpanded(body, false, trigger);
    ui.setExpanded(body, true, trigger, { update: () => parent.classList.add('arr-expanded') });
    const animation = body.getAnimations()[0];
    animation.pause();
    animation.currentTime = 60;
    const target = Number.parseFloat((animation.effect as KeyframeEffect).getKeyframes().at(-1)!.height as string);
    const paragraph = document.createElement('p');
    paragraph.textContent = 'Additional connection information is now available. Verify the server address, permissions, certificate and quality profiles before submitting requests.';
    body.appendChild(paragraph);
    return target;
  });
  const result = await page.evaluate(() => {
    const body = document.getElementById('arrCollapsibleBodySeerr')!;
    const animation = body.getAnimations()[0];
    const frame = (animation.effect as KeyframeEffect).getKeyframes().at(-1)!;
    const height = Number.parseFloat(frame.height as string);
    const duration = animation.effect!.getTiming().duration;
    animation.finish();
    return { height, duration };
  });
  expect(result.height).toBeGreaterThan(initialHeight);
  expect(Number(result.duration)).toBeLessThanOrEqual(200);
  await page.clock.runFor(300);
  await expect(page.locator('#arrCollapsibleBodySeerr')).toBeVisible();
  expect(await page.locator('#arrCollapsibleBodySeerr').evaluate(element => element.getAnimations().length)).toBe(0);
});

test('Explorer menus, Codec trees and Insights share the panel motion behavior', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await recordPanelMotion(page);
  await page.evaluate(() => {
    const codecs = ['H264', 'HEVC', 'AV1', 'VP9', 'MPEG2', 'VC1'];
    const library = {
      LibraryName: 'Movies', CollectionType: 'movies', VideoFileCount: codecs.length,
      VideoCodecs: Object.fromEntries(codecs.map(codec => [codec, 1])),
      VideoCodecPaths: Object.fromEntries(codecs.map(codec => [codec, ['/media/Movies/' + codec + '/Movie.mkv']])),
    };
    (window as any).fixtureAdmin.fillCodecsData({ Libraries: [library], Movies: [library],
      TvShows: [], Music: [], Books: [], Other: [], MovieRootPaths: ['/media/Movies'] });
  });
  await page.locator('.tab-btn[data-tab="codecs"]').click();
  await page.locator('#codecExplorerToggle').click();
  await expect(page.locator('#codecExplorerBody')).toBeVisible();
  await page.locator('#codecFilterAddBtn').click();
  const menu = page.locator('[data-filter-pop]');
  await expect(menu).toBeVisible();
  await expect.poll(() => menu.evaluate(element => element.getAnimations().length)).toBe(0);
  await page.locator('#codecFilterAddBtn').click();
  await expect(menu).toHaveCount(0);
  const chart = page.locator('[data-chart-id="videoCodecs"]');
  await chart.locator('[data-breakdown-toggle]').click();
  await chart.locator('.codec-clickable').first().click();
  const detail = page.locator('#codecDetail_videoCodecs');
  await expect(detail).toBeVisible();
  await expect.poll(() => detail.evaluate(element => element.getAnimations().length)).toBe(0);
  await detail.locator('.tree-toggle').first().click();
  await expect(detail.locator('.tree-children').first()).toBeVisible();
  await detail.locator('.tree-toggle').first().click();
  await expect(detail.locator('.tree-children').first()).toBeHidden();
  await page.locator('.tab-btn[data-tab="trends"]').click();
  await page.evaluate(() => (window as any).fixtureAdmin.renderInsightCards({
    LargestTotalSize: 1024, RecentTotalCount: 0,
    Largest: [{ LibraryName: 'Movies', CollectionType: 'movies', Name: 'Movie.mkv', Size: 1024 }], Recent: [],
  }));
  await page.locator('#insightLargestBtn').click();
  await expect(page.locator('#insightLargestPanel')).toBeVisible();
  const ids = await page.evaluate(() => (window as any).fixturePanelMotion.map((entry: any) => entry.id));
  expect(ids).toContain('codecExplorerBody');
  expect(ids).toContain('codecDetail_videoCodecs');
  expect(ids).toContain('insightLargestPanel');
});

test('closing a language editor preserves a tree and file detail opened during its transition', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await dashboard(page);
  await page.evaluate(() => {
    const paths = ['/media/Movies/One/Movie.mkv', '/media/Movies/Two/Movie.mkv'];
    const library = { LibraryName: 'Movies', CollectionType: 'movies', RootPaths: ['/media/Movies'],
      VideoFileCount: 2, AudioLanguages: { eng: 2, deu: 2 }, AudioLanguagePaths: { eng: paths, deu: paths } };
    (window as any).fixtureAdmin.fillCodecsData({ Libraries: [library], Movies: [library],
      TvShows: [], Music: [], Books: [], Other: [], MovieRootPaths: ['/media/Movies'] });
  });
  await page.locator('.tab-btn[data-tab="codecs"]').click();
  await page.locator('#codecExplorerToggle').click();
  await page.locator('#codecFilterAddBtn').click();
  await page.locator('[data-filter-dim="audioLanguages"]').click();
  const boxes = page.locator('.codec-filter-editor [data-multi-panel="audioLanguages"] input');
  await boxes.nth(0).check();
  await boxes.nth(1).check();
  const values = await boxes.evaluateAll(elements => elements.map(element => (element as HTMLInputElement).value));
  for (const value of values) await expect(page.locator('.codec-explorer-summary')).toContainText(value);
  await page.evaluate(() => {
    const menu = document.querySelector('[data-filter-pop]')!;
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    const closing = menu.getAnimations()[0];
    closing.pause();
    closing.currentTime = 40;
    const results = document.getElementById('codecExplorerResults')!;
    results.querySelector<HTMLButtonElement>('[data-tree-action="expand"]')!.click();
    const leaf = results.querySelector<HTMLElement>('.tree-leaf[title]')!;
    leaf.click();
    (window as any).fixtureOpenedLeaf = leaf;
    (window as any).fixtureOpenedDetail = leaf.nextElementSibling;
    closing.finish();
  });
  await expect(page.locator('[data-filter-pop]')).toHaveCount(0);
  const detail = page.locator('#codecExplorerResults .codec-file-detail');
  await expect(detail).toBeVisible();
  await expect(page.locator('#codecExplorerResults .tree-leaf[aria-expanded="true"]')).toHaveCount(1);
  const preserved = await page.evaluate(() => ({
    leaf: document.getElementById('codecExplorerResults')!.contains((window as any).fixtureOpenedLeaf),
    detail: document.querySelector('#codecExplorerResults .codec-file-detail') === (window as any).fixtureOpenedDetail,
  }));
  expect(preserved).toEqual({ leaf: true, detail: true });
});
