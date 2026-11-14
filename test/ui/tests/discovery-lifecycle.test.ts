import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { JSDOM } from 'jsdom';
import { afterAll, afterEach, describe, expect, test, vi } from 'vitest';
import { freshDiscoveryHelpers, REPO_ROOT, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

const scripts = [
  readFileSync(join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper/PluginPages/js/UiPrimitives.js'), 'utf8'),
  ...['client-lifecycle.js', 'native-tabs.js', 'discovery-sidebar.js'].map(name =>
    readFileSync(join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper/js', name), 'utf8')),
];
const documents: JSDOM[] = [];

afterEach(() => {
  documents.splice(0).forEach(dom => dom.window.close());
  vi.clearAllTimers();
});

function recommendations(title: string, posterPath?: string) {
  return { Recommendations: [{ Title: title, TmdbId: 123, MediaType: 'movie', Score: 0.8, Genres: [], PosterPath: posterPath, AvailabilityVerified: true }] };
}

function appendHomeLink(list: Element) {
  const item = list.ownerDocument.createElement('li');
  const link = list.ownerDocument.createElement('a');
  link.setAttribute('href', '#/home');
  link.textContent = 'Home';
  item.appendChild(link);
  list.appendChild(item);
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

async function settle() {
  await vi.advanceTimersByTimeAsync(200);
}

function clientDocument(options: { modern?: boolean; hash?: string; lateApi?: boolean; initialUser?: string; responses?: Record<string, () => Promise<unknown>> } = {}) {
  // A separate document runs both shipped scripts without exposing or replacing their internal helpers.
  const dom = new JSDOM('<!doctype html><body><div class="headerRight"></div><div class="mainDrawer"><div class="mainDrawer-scrollContainer"></div></div><div class="headerTabs"><div is="emby-tabs"><div class="emby-tabs-slider"><button class="emby-tab-button emby-tab-button-active" data-index="0">Home</button><button class="emby-tab-button" data-index="1">Favorites</button></div></div></div><div id="panels"><div class="tabContent pageTabContent is-active" data-index="0"></div><div class="tabContent pageTabContent" data-index="1"></div></div></body>', {
    url: 'http://localhost/web/index.html' + (options.hash ?? '#/home'),
    runScripts: 'dangerously',
    pretendToBeVisual: true,
  });
  documents.push(dom);
  const w = dom.window;
  w.setTimeout = ((callback: () => void, delay: number) => setTimeout(callback, delay)) as typeof w.setTimeout;
  w.clearTimeout = clearTimeout as typeof w.clearTimeout;
  w.setInterval = ((callback: () => void, delay: number) => setInterval(callback, delay)) as typeof w.setInterval;
  w.clearInterval = clearInterval as typeof w.clearInterval;
  w.requestAnimationFrame = callback => Number(setTimeout(() => callback(Date.now()), 16));
  if (options.modern) (w.document.querySelector('.headerTabs') as HTMLElement).style.display = 'none';
  Object.defineProperty(w.HTMLElement.prototype, 'offsetParent', {
    get() {
      let node = this;
      while (node) {
        if (node.hidden || node.classList?.contains('hide') || node.style?.display === 'none') return null;
        node = node.parentElement;
      }
      return w.document.body;
    },
  });
  const tabs = w.document.querySelector('[is="emby-tabs"]') as any;
  tabs.selectedTabIndex = 0;
  tabs.refresh = vi.fn();
  const emit = (type: string, index: number, previousIndex: number) =>
    tabs.dispatchEvent(new w.CustomEvent(type, { detail: { selectedTabIndex: index, previousIndex } }));
  const select = (index: number, delayed: boolean, previousIndex = tabs.selectedTabIndex) => {
    tabs.querySelectorAll('.emby-tab-button').forEach((button: HTMLElement, position: number) => {
      if (button.classList.contains('emby-tab-button-active') !== (position === index)) {
        button.classList.toggle('emby-tab-button-active', position === index);
      }
    });
    w.document.querySelectorAll('#panels > .tabContent').forEach((panel, position) => {
      if (panel.classList.contains('is-active') !== (position === index)) panel.classList.toggle('is-active', position === index);
    });
    emit('beforetabchange', index, previousIndex);
    const finish = () => { tabs.selectedTabIndex = index; emit('tabchange', index, previousIndex); };
    if (delayed) w.setTimeout(finish, 120);
    else finish();
  };
  tabs.selectedIndex = (index?: number) => {
    if (index === undefined) return tabs.selectedTabIndex;
    const previousIndex = tabs.selectedTabIndex;
    tabs.selectedTabIndex = index;
    select(index, index !== previousIndex, previousIndex);
    return tabs.selectedTabIndex;
  };
  tabs.addEventListener('click', (event: Event) => {
    const button = (event.target as Element).closest('.emby-tab-button') as HTMLElement | null;
    if (button && !button.classList.contains('emby-tab-button-active')) select(Number(button.dataset.index), true);
  });
  let userId = options.initialUser ?? 'a';
  let token = userId ? 'session-' + userId : '';
  let reply: () => Promise<unknown> = () => Promise.resolve(recommendations('Recommendations for ' + userId));
  const sourceReplies = new Map<string, () => Promise<unknown>>(Object.entries(options.responses ?? {}));
  const dispatch = (request: { url: string; type?: string }) => {
    const sourceReply = sourceReplies.get(request.url);
    if (sourceReply) return sourceReply();
    if (request.url === '/JellyfinHelper/Discovery/My') return reply();
    if (request.url.includes('/Trakt')) return Promise.reject({ status: 403 });
    return Promise.resolve({});
  };
  const api = {
    getCurrentUserId: () => userId,
    accessToken: () => token,
    setAuthenticationInfo: (accessToken: string, user: string) => { userId = user || ''; token = accessToken || ''; },
    getUrl: (path: string) => path,
    ajax: vi.fn(dispatch),
  };
  const clients = [api];
  const requests = () => clients.flatMap(client => client.ajax.mock.calls.map(([request]) => request));
  if (!options.lateApi) (w as any).ApiClient = api;
  for (const source of scripts) {
    const script = w.document.createElement('script');
    script.textContent = source;
    w.document.body.appendChild(script);
  }
  const route = (hash: string) => {
    w.history.pushState(null, '', hash);
    w.document.body.classList.toggle('login-page', hash.startsWith('#/login'));
    w.document.dispatchEvent(new w.Event('viewshow'));
  };
  return {
    w, api, tabs, route, requests,
    setSession: (user: string, accessToken = 'session-' + user) => ((w as any).ApiClient ?? api).setAuthenticationInfo(accessToken, user),
    setReply: (next: () => Promise<unknown>) => { reply = next; },
    setSourceReply: (path: string, next: () => Promise<unknown>) => { sourceReplies.set(path, next); },
    replaceApiClient: () => {
      const replacement = { ...api, ajax: vi.fn(dispatch) };
      clients.push(replacement);
      (w as any).ApiClient = replacement;
    },
    panel: () => w.document.getElementById('jfh-tab-panel-discovery'),
    entry: () => w.document.getElementById(options.modern ? 'jfh-tab-link-discovery' : 'jfh-tab-btn-discovery'),
    ownRequests: () => requests().filter(request => request.url === '/JellyfinHelper/Discovery/My').length,
  };
}

type ClientDocument = ReturnType<typeof clientDocument>;

const translationsPath = '/JellyfinHelper/Translations';
const externalLinksPath = '/JellyfinHelper/Discovery/My/ExternalLinks';
const personalPath = '/JellyfinHelper/Discovery/My/Trakt';
const trendingPath = '/JellyfinHelper/Discovery/My/Trakt/Trending';
const permissionsPath = '/JellyfinHelper/Discovery/My/RequestPermissions/radarr?mediaType=movie';

const currentTranslations = () => ({ discoveryRequest: 'Current request', discoveryTitle: 'Current Discovery' });
const currentResponses = (): Record<string, () => Promise<unknown>> => ({
  [translationsPath]: () => Promise.resolve(currentTranslations()),
  [externalLinksPath]: () => Promise.resolve({ SeerrUrl: 'https://current.example/' }),
  [personalPath]: () => Promise.resolve({ Linked: false, Result: null }),
  [trendingPath]: () => Promise.resolve(recommendations('Current trending', '/poster.jpg')),
});

const staleResponseCases = [
  { source: 'Trakt personal', path: personalPath, oldResponse: () => ({ Linked: true, Result: recommendations('Old private recommendations') }) },
  { source: 'Trakt trending', path: trendingPath, oldResponse: () => recommendations('Old private recommendations') },
  { source: 'translations', path: translationsPath, oldResponse: () => ({ discoveryRequest: 'Old request', discoveryTitle: 'Old Discovery' }) },
  { source: 'external links', path: externalLinksPath, oldResponse: () => ({ SeerrUrl: 'https://old.example/' }) },
].flatMap(source => (['token', 'client'] as const).flatMap(transition =>
  (['success', 'failure'] as const).map(outcome => ({ ...source, transition, outcome }))));

function profiles(prefix: string) {
  return [1, 2].map(id => ({ ServerId: 1, ProfileId: id, RootFolder: '/media/movies', ProfileName: prefix + ' quality ' + id, ServerName: 'Seerr' }));
}

async function openDialog(client: ClientDocument, dialog: 'profile' | 'dismiss') {
  (client.w as any).JellyfinHelperTabs.focus('discovery');
  await settle();
  const selector = dialog === 'profile' ? '.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)' : '.jfh-discovery-btn-dismiss';
  const trigger = client.panel()!.querySelector(selector) as HTMLElement;
  expect(trigger).not.toBeNull();
  trigger.click();
  await settle();
  const overlay = client.w.document.getElementById('jfhDiscoveryPopup')!;
  expect(overlay).not.toBeNull();
  return {
    overlay,
    action: overlay.querySelector('.jfh-discovery-popup-item') as HTMLElement,
    cancel: overlay.querySelector('.jfh-discovery-popup-cancel') as HTMLElement,
    escapeHandler: (overlay as any)._onEsc,
  };
}

describe.each([false, true])('shipped Discovery lifecycle with modern layout %s', modern => {
  test.each(['success', 'duplicate', 'failure'])('profile selection after an original %s cannot resubmit a consumed replacement card', async outcome => {
    const root = '/JellyfinHelper/Discovery/My';
    const pending = deferred<unknown>();
    let posts = 0;
    const client = clientDocument({ modern, responses: {
      [root]: () => Promise.resolve(recommendations('Movie')),
      [externalLinksPath]: () => Promise.resolve({ TraktEnabled: true }),
      [personalPath]: () => Promise.resolve({ Linked: true, Result: recommendations('Trakt movie') }),
      [permissionsPath]: () => Promise.resolve({ CanRequest: true, Profiles: profiles('Movie') }),
      [root + '/Request']: () => ++posts === 1 ? pending.promise : Promise.resolve({ Success: true }),
    } });
    await settle();
    const firstDialog = await openDialog(client, 'profile');
    firstDialog.action.click();
    await settle();
    expect(posts).toBe(1);
    const original = client.panel()!.querySelector('.jfh-discovery-card');
    (client.panel()!.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await settle();
    (client.panel()!.querySelector('[data-tab="own"]') as HTMLElement).click();
    await settle();
    const replacement = client.panel()!.querySelector('.jfh-discovery-card');
    expect(replacement).not.toBe(original);
    const replacementDialog = await openDialog(client, 'profile');
    pending.resolve({ Success: outcome !== 'failure', AlreadyRequested: outcome === 'duplicate' });
    await settle();
    const button = replacement!.querySelector('[data-jfh-action="request"]') as HTMLButtonElement;
    expect(button.disabled).toBe(outcome !== 'failure');
    expect(replacementDialog.overlay.isConnected).toBe(true);
    replacementDialog.action.click();
    await settle();
    expect(client.w.document.getElementById('jfhDiscoveryPopup')).toBeNull();
    expect(posts).toBe(outcome === 'failure' ? 2 : 1);
    expect(button.disabled).toBe(true);
    expect(button.classList.contains('jfh-discovery-btn-done')).toBe(true);
    expect(button.classList.contains('jfh-discovery-btn-failed')).toBe(false);
    expect((replacement!.querySelector('.jfh-discovery-btn-dismiss') as HTMLElement).style.display).toBe('none');
  });

  test.each(['application', 'transport'])('an original success cancels a replacement %s failure reset', async failure => {
    const root = '/JellyfinHelper/Discovery/My';
    const originalRequest = deferred<unknown>();
    const replacementRequest = deferred<unknown>();
    const pool = () => ({ DeliveryId: '00000000-0000-0000-0000-000000000001', Recommendations: [
      { Title: 'Movie', TmdbId: 1, MediaType: 'movie', Score: .8 },
      { Title: 'Television', TmdbId: 1, MediaType: 'tv', Score: .7 },
    ] });
    let posts = 0;
    const client = clientDocument({ modern, responses: {
      [root]: () => Promise.resolve(pool()),
      [externalLinksPath]: () => Promise.resolve({ TraktEnabled: true }),
      [personalPath]: () => Promise.resolve({ Linked: true, Result: pool() }),
      [permissionsPath]: () => Promise.resolve({ CanRequest: true, Profiles: [] }),
      [root + '/Request']: () => ++posts === 1 ? originalRequest.promise : replacementRequest.promise,
    } });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    const requestButton = () => client.panel()!.querySelector('.jfh-discovery-card[data-type="movie"][data-tmdb="1"] [data-jfh-action="request"]') as HTMLButtonElement;
    requestButton().click();
    await settle();
    (client.panel()!.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await settle();
    (client.panel()!.querySelector('[data-tab="own"]') as HTMLElement).click();
    await settle();
    const replacement = requestButton();
    replacement.click();
    await settle();
    if (failure === 'application') replacementRequest.resolve({ Success: false });
    else replacementRequest.reject({ status: 500 });
    await settle();
    expect(replacement.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    originalRequest.resolve({ Success: true });
    await settle();
    await vi.advanceTimersByTimeAsync(3100);
    expect(replacement.disabled).toBe(true);
    expect(replacement.classList.contains('jfh-discovery-btn-done')).toBe(true);
    expect(replacement.classList.contains('jfh-discovery-btn-failed')).toBe(false);
    replacement.click();
    await settle();
    expect(posts).toBe(2);
  });

  test.each(['trakt', 'trending'])('a requested card never follows the user into %s during its success timeout', async tab => {
    const root = '/JellyfinHelper/Discovery/My';
    const pool = (ids: number[]) => ({ DeliveryId: '00000000-0000-0000-0000-000000000001',
      Recommendations: ids.map(id => ({ Title: 'Movie ' + id, TmdbId: id, MediaType: 'movie', Score: 0.8, Genres: [] })) });
    const client = clientDocument({ modern, responses: {
      [root]: () => Promise.resolve(pool([1, 2])),
      [externalLinksPath]: () => Promise.resolve({ TraktEnabled: true }),
      [personalPath]: () => Promise.resolve({ Linked: true, Result: pool([3, 2]) }),
      [trendingPath]: () => Promise.resolve(pool([4, 2])),
      [permissionsPath]: () => Promise.resolve({ CanRequest: true, Profiles: [] }),
      [root + '/Request']: () => Promise.resolve({ Success: true }),
    } });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    const button = client.panel()!.querySelector('[data-tmdb="1"] [data-jfh-action="request"]') as HTMLElement;
    button.click();
    await settle();
    expect(client.panel()!.querySelector('[data-tmdb="1"].jfh-discovery-card .jfh-discovery-btn-done')).not.toBeNull();
    (client.panel()!.querySelector('[data-tab="' + tab + '"]') as HTMLElement).click();
    await settle();
    const cards = () => Array.from(client.panel()!.querySelectorAll('.jfh-discovery-card'), card => (card as HTMLElement).dataset.tmdb);
    const expected = tab === 'trakt' ? ['3', '2'] : ['4', '2'];
    expect(cards()).toEqual(expected);
    const requestsBeforeTimeout = client.requests().length;
    await vi.advanceTimersByTimeAsync(5300);
    expect(cards()).toEqual(expected);
    expect(client.requests()).toHaveLength(requestsBeforeTimeout);
    expect(client.requests().filter(request => request.url === root + '/Request')).toHaveLength(1);
  });

  test.each(['success', 'duplicate', 'failure'])('a late %s response updates the visible typed title without transplanting the original timer', async outcome => {
    const root = '/JellyfinHelper/Discovery/My';
    const pending = deferred<unknown>();
    const pool = () => ({ DeliveryId: '00000000-0000-0000-0000-000000000001', Recommendations: [
      { Title: 'Requested movie', TmdbId: 1, MediaType: 'movie', Score: 0.8 },
      { Title: 'TV with same numeric ID', TmdbId: 1, MediaType: 'tv', Score: 0.7 },
      { Title: 'Another movie', TmdbId: 2, MediaType: 'movie', Score: 0.6 },
    ] });
    const client = clientDocument({ modern, responses: {
      [root]: () => Promise.resolve(pool()),
      [externalLinksPath]: () => Promise.resolve({ TraktEnabled: true }),
      [personalPath]: () => Promise.resolve({ Linked: true, Result: pool() }),
      [trendingPath]: () => Promise.resolve(pool()),
      [permissionsPath]: () => Promise.resolve({ CanRequest: true, Profiles: [] }),
      [root + '/Request']: () => pending.promise,
    } });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    const original = client.panel()!.querySelector('.jfh-discovery-card[data-type="movie"][data-tmdb="1"]')!;
    (original.querySelector('[data-jfh-action="request"]') as HTMLElement).click();
    await settle();
    (client.panel()!.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await settle();
    (client.panel()!.querySelector('[data-tab="own"]') as HTMLElement).click();
    await settle();
    const replacement = client.panel()!.querySelector('.jfh-discovery-card[data-type="movie"][data-tmdb="1"]')!;
    expect(replacement).not.toBe(original);
    pending.resolve({ Success: outcome !== 'failure', AlreadyRequested: outcome === 'duplicate' });
    await settle();
    const replacementRequest = replacement.querySelector('[data-jfh-action="request"]') as HTMLButtonElement;
    if (outcome === 'failure') {
      expect(replacementRequest.disabled).toBe(false);
      expect(replacement.querySelector('.jfh-discovery-btn-done, .jfh-discovery-btn-failed')).toBeNull();
    } else {
      expect(replacementRequest.disabled).toBe(true);
      expect(replacementRequest.classList.contains('jfh-discovery-btn-done')).toBe(true);
      expect((replacement as any)._jfhConsumeTimer).toBeFalsy();
      replacementRequest.click();
      await settle();
    }
    const television = client.panel()!.querySelector('.jfh-discovery-card[data-type="tv"][data-tmdb="1"]')!;
    expect((television.querySelector('[data-jfh-action="request"]') as HTMLButtonElement).disabled).toBe(false);
    await vi.advanceTimersByTimeAsync(5300);
    expect(client.panel()!.querySelector('.jfh-discovery-card[data-type="movie"][data-tmdb="1"]')).toBe(replacement);
    expect(client.requests().filter(request => request.url === root + '/Request')).toHaveLength(1);
    (client.panel()!.querySelector('[data-tab="trending"]') as HTMLElement).click();
    await settle();
    const keys = Array.from(client.panel()!.querySelectorAll('.jfh-discovery-card'), card => (card as HTMLElement).dataset.cardKey);
    expect(keys).toEqual(outcome === 'failure' ? ['movie-1', 'tv-1', 'movie-2'] : ['tv-1', 'movie-2']);
  });

  test('a clean login route opens Discovery on the first authenticated Home visit without a reload', async () => {
    const client = clientDocument({ modern, hash: '#/login.html', initialUser: '' });
    await settle();
    expect(client.entry()).toBeNull();
    expect(client.ownRequests()).toBe(0);
    client.setSession('a');
    client.route('#/home');
    await settle();
    expect(client.entry()).not.toBeNull();
    expect(client.panel()?.textContent).toContain('Recommendations for a');
    expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
  });

  test('a non-bubbling view event restores the sidebar on the same route without another probe', async () => {
    const client = clientDocument({ modern });
    await settle();
    const before = client.ownRequests();
    client.w.document.querySelector('.jfhelper-nav-discovery')!.remove();
    client.w.document.getElementById('panels')!.dispatchEvent(new client.w.Event('viewshow'));
    expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
    await settle();
    expect(client.ownRequests()).toBe(before);
  });

  test('a newer Discovery selection survives an older Home completion during rapid navigation', async () => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    for (let visit = 0; visit < 20; visit++) {
      client.tabs.querySelector('[data-index="0"]').click();
      expect(client.panel()?.classList.contains('is-active')).toBe(false);
      expect(client.w.location.hash).toBe('#/home');
      await vi.advanceTimersByTimeAsync(16);
      (client.w as any).JellyfinHelperTabs.focus('discovery');
      const historyLength = client.w.history.length;
      await vi.advanceTimersByTimeAsync(16);
      expect(client.panel()?.classList.contains('is-active')).toBe(true);
      await vi.advanceTimersByTimeAsync(160);
      expect(client.w.location.hash).toBe('#/home?tab=2&jfhTab=discovery');
      expect(client.tabs.selectedIndex()).toBe(2);
      expect(client.panel()?.classList.contains('is-active')).toBe(true);
      expect(client.w.document.querySelectorAll('#panels > .tabContent.is-active')).toHaveLength(1);
      expect(client.w.history).toHaveLength(historyLength);
    }
  });

  test('a host selection without fresh input cannot replace the Discovery route', async () => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    const historyLength = client.w.history.length;
    client.tabs.selectedIndex(0);
    await settle();
    expect(client.w.location.hash).toBe('#/home?tab=2&jfhTab=discovery');
    expect(client.tabs.selectedIndex()).toBe(2);
    expect(client.panel()?.classList.contains('is-active')).toBe(true);
    expect(client.w.history).toHaveLength(historyLength);
  });

  test.each([0, 1])('native destination %s repairs an older Discovery completion without adding history', async index => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    client.tabs.querySelector('[data-index="' + index + '"]').click();
    await settle();
    const historyLength = client.w.history.length;
    // emby-tabs writes its captured index before dispatching a delayed completion.
    client.tabs.selectedTabIndex = 2;
    client.tabs.dispatchEvent(new client.w.CustomEvent('tabchange', { detail: { selectedTabIndex: 2, previousIndex: index } }));
    await settle();
    expect(client.w.location.hash).toBe(index === 0 ? '#/home' : '#/home?tab=1');
    expect(client.tabs.selectedIndex()).toBe(index);
    expect(client.panel()?.classList.contains('is-active')).toBe(false);
    expect(client.w.document.querySelector('#panels > [data-index="' + index + '"]')?.classList.contains('is-active')).toBe(true);
    expect(client.w.history).toHaveLength(historyLength);
  });

  test('history restoration survives overlapping tab completions without rewriting the destination', async () => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    client.tabs.querySelector('[data-index="0"]').click();
    await vi.advanceTimersByTimeAsync(16);
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await vi.advanceTimersByTimeAsync(16);
    const historyLength = client.w.history.length;
    client.w.history.back();
    await vi.advanceTimersByTimeAsync(16);
    expect(client.w.location.hash).toBe('#/home');
    client.w.history.forward();
    await settle();
    expect(client.w.location.hash).toBe('#/home?tab=2&jfhTab=discovery');
    expect(client.tabs.selectedIndex()).toBe(2);
    expect(client.panel()?.classList.contains('is-active')).toBe(true);
    expect(client.w.history).toHaveLength(historyLength);
  });

  test('logout removes all cached copies and rejects header controls restored later on the login page', async () => {
    const client = clientDocument({ modern });
    await settle();
    const original = client.entry()!;
    const copies = Array.from({ length: 3 }, () => original.cloneNode(true) as HTMLElement);
    copies.forEach(copy => client.w.document.body.appendChild(copy));
    client.setSession('', '');
    client.route('#/login.html');
    await settle();
    expect(client.w.document.querySelectorAll('#jfh-tab-btn-discovery, #jfh-tab-link-discovery')).toHaveLength(0);
    client.w.document.body.appendChild(copies[0]);
    await settle();
    expect(client.w.document.querySelectorAll('#jfh-tab-btn-discovery, #jfh-tab-link-discovery')).toHaveLength(0);
  });

  test('a stylesheet becoming ready mounts Discovery without another route change', async () => {
    const client = clientDocument({ modern });
    const root = client.w.document.getElementById('panels')!;
    let ready = false;
    Object.defineProperty(root, 'offsetParent', { get: () => ready ? client.w.document.body : null });
    await settle();
    expect(client.entry()).toBeNull();
    ready = true;
    const stylesheet = client.w.document.createElement('link');
    stylesheet.rel = 'stylesheet';
    client.w.document.head.appendChild(stylesheet);
    stylesheet.dispatchEvent(new client.w.Event('load'));
    await settle();
    expect(client.entry()).not.toBeNull();
  });

  test('logout removes every entry and its empty sidebar section without a compass click', async () => {
    const client = clientDocument({ modern });
    await settle();
    expect(client.entry()).not.toBeNull();
    client.setSession('', '');
    client.route('#/login');
    await settle();
    expect(client.w.document.querySelector('#jfh-tab-btn-discovery, #jfh-tab-link-discovery, #jfh-tab-panel-discovery')).toBeNull();
    expect(client.w.document.querySelector('.jfhelper-nav-discovery, .jellyfinHelperSection')).toBeNull();
  });

  test('DOM changes detect logout when the router emits no browser history event', async () => {
    const client = clientDocument({ modern });
    await settle();
    client.setSession('', '');
    client.w.history.pushState(null, '', '#/login');
    client.w.document.body.classList.add('signed-out');
    await settle();
    expect(client.entry()).toBeNull();
    expect(client.panel()).toBeNull();
  });

  test('an account switch removes the previous cards before the new probe finishes', async () => {
    const client = clientDocument({ modern });
    await settle();
    expect(client.panel()?.textContent).toContain('Recommendations for a');
    const pending = deferred<unknown>();
    client.setReply(() => pending.promise);
    client.setSession('b');
    client.route('#/home?account=b');
    await settle();
    expect(client.panel()).toBeNull();
    pending.resolve(recommendations('Recommendations for b'));
    await settle();
    expect(client.panel()?.textContent).toContain('Recommendations for b');
    expect(client.panel()?.textContent).not.toContain('Recommendations for a');
  });

  test('a denied replacement account cannot retain the previous account entry', async () => {
    const client = clientDocument({ modern });
    await settle();
    client.setReply(() => Promise.reject({ status: 403 }));
    client.setSession('b');
    client.route('#/home?account=b');
    await settle();
    expect(client.entry()).toBeNull();
    expect(client.panel()).toBeNull();
  });

  test('reopening a retained panel refreshes changed recommendations', async () => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    client.tabs.querySelector('[data-index="0"]').click();
    await settle();
    const before = client.ownRequests();
    client.setReply(() => Promise.resolve(recommendations('Fresh recommendations')));
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    expect(client.ownRequests()).toBeGreaterThan(before);
    expect(client.panel()?.textContent).toContain('Fresh recommendations');
  });

  test('revoking access removes cached cards and navigation on the next activation', async () => {
    const client = clientDocument({ modern });
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    client.tabs.querySelector('[data-index="0"]').click();
    await settle();
    client.setReply(() => Promise.reject({ status: 403 }));
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    expect(client.panel()).toBeNull();
    expect(client.entry()).toBeNull();
    expect(client.w.document.querySelector('.jfhelper-nav-discovery')).toBeNull();
    expect(client.tabs.selectedIndex()).toBe(0);
    expect(client.w.document.querySelector('#panels > .tabContent[data-index="0"]')?.classList.contains('is-active')).toBe(true);
  });

  test('empty recommendations still expose the available Discovery navigation', async () => {
    const client = clientDocument({ modern });
    client.setReply(() => Promise.resolve({ Recommendations: [] }));
    await settle();
    expect(client.entry()).not.toBeNull();
    expect(client.panel()?.textContent).toContain('No suggestions available');
    expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
  });

  test.each(staleResponseCases)('late $source $outcome cannot affect a replacement $transition session', async ({ path, oldResponse, transition, outcome }) => {
    const pending = deferred<unknown>();
    const responses = currentResponses();
    const client = clientDocument({ modern, responses: { ...responses, [path]: () => pending.promise } });
    client.setReply(() => Promise.resolve(recommendations('Current recommendations')));
    await settle();
    expect(client.requests()).toContainEqual(expect.objectContaining({ url: path, type: 'GET' }));
    client.setSourceReply(path, responses[path]);
    if (transition === 'token') client.setSession('a', 'replacement-token');
    else client.replaceApiClient();
    client.route('#/home?session=' + transition);
    await settle();
    const trending = client.panel()!.querySelector('.jfh-discovery-tab[data-tab="trending"]') as HTMLElement;
    expect(trending).not.toBeNull();
    trending.click();
    await settle();
    expect(client.panel()?.textContent).toContain('Current trending');
    if (outcome === 'success') pending.resolve(oldResponse());
    else pending.reject({ status: 403, responseJSON: { Message: 'Old session denial' } });
    await settle();
    client.tabs.querySelector('[data-index="0"]').click();
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    expect(client.panel()?.textContent).toContain('Current trending');
    expect(client.panel()?.textContent).not.toContain('Old private recommendations');
    expect(client.panel()?.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)')?.textContent).toBe('Current request');
    expect(client.panel()?.querySelector('.jfh-discovery-flip-link[data-href*="current.example"]')).not.toBeNull();
    expect(client.w.document.querySelector('[data-href*="old.example"]')).toBeNull();
    expect(client.panel()?.querySelector('.jfh-discovery-tab[data-tab="trakt"]')).toBeNull();
    expect(client.entry()).not.toBeNull();
    expect(client.w.document.querySelector('.jfh-discovery-toast')).toBeNull();
    expect(client.requests().filter(request => request.type === 'POST')).toHaveLength(0);
  });

  for (const dialog of ['profile', 'dismiss'] as const) {
    test.each(['logout', 'account'] as const)('an open ' + dialog + ' dialog is retired on %s and cannot affect a new dialog', async transition => {
      const client = clientDocument({ modern });
      client.setSourceReply(permissionsPath, () => Promise.resolve({ CanRequest: true, Profiles: profiles('Old') }));
      await settle();
      const previous = await openDialog(client, dialog);
      const removeListener = vi.spyOn(client.w.document, 'removeEventListener');
      try {
        if (transition === 'logout') {
          client.setSession('', '');
          client.route('#/login');
        } else {
          client.setSession('b');
          client.route('#/home?account=b');
        }
        await settle();
        expect(previous.overlay.isConnected).toBe(false);
        expect(client.w.document.getElementById('jfhDiscoveryPopup')).toBeNull();
        expect(removeListener).toHaveBeenCalledWith('keydown', previous.escapeHandler);
        let replacement: Awaited<ReturnType<typeof openDialog>> | null = null;
        if (transition === 'account') {
          client.setSourceReply(permissionsPath, () => Promise.resolve({ CanRequest: true, Profiles: profiles('Current') }));
          replacement = await openDialog(client, dialog);
        }
        previous.action.click();
        previous.cancel.click();
        previous.overlay.dispatchEvent(new client.w.MouseEvent('click', { bubbles: true }));
        previous.escapeHandler(new client.w.KeyboardEvent('keydown', { key: 'Escape' }));
        await settle();
        expect(client.requests().filter(request => request.type === 'POST')).toHaveLength(0);
        expect(client.w.document.querySelector('.jfh-discovery-toast')).toBeNull();
        if (replacement) {
          expect(client.w.document.getElementById('jfhDiscoveryPopup')).toBe(replacement.overlay);
          expect(replacement.overlay.isConnected).toBe(true);
        } else {
          expect(client.w.document.getElementById('jfhDiscoveryPopup')).toBeNull();
        }
      } finally {
        removeListener.mockRestore();
      }
    });

    test.each(['token', 'client'] as const)('an open ' + dialog + ' dialog cannot submit during a %s change before cleanup runs', async transition => {
      const client = clientDocument({ modern });
      client.setSourceReply(permissionsPath, () => Promise.resolve({ CanRequest: true, Profiles: profiles('Old') }));
      await settle();
      const previous = await openDialog(client, dialog);
      if (transition === 'token') client.setSession('a', 'replacement-token');
      else client.replaceApiClient();
      expect(previous.overlay.isConnected).toBe(transition === 'client');
      previous.action.click();
      if (transition === 'client') client.w.document.dispatchEvent(new client.w.Event('viewshow'));
      await settle();
      expect(client.requests().filter(request => request.type === 'POST')).toHaveLength(0);
      expect(client.w.document.querySelector('.jfh-discovery-toast')).toBeNull();
      expect(previous.overlay.isConnected).toBe(false);
    });
  }
});

test.each(['#/login', '#/login.html', '#!/login.html', '#/selectserver.html', '#/addserver.html'])('a retained identity cannot advertise Discovery on %s', async route => {
  const client = clientDocument({ modern: true });
  await settle();
  expect(client.entry()).not.toBeNull();
  client.route(route);
  await settle();
  expect(client.api.getCurrentUserId()).toBe('a');
  expect(client.entry()).toBeNull();
  expect(client.panel()).toBeNull();
});

test('late API initialization remains supported without a global Events object', async () => {
  const client = clientDocument({ lateApi: true });
  await settle();
  expect(client.entry()).toBeNull();
  (client.w as any).ApiClient = client.api;
  await vi.advanceTimersByTimeAsync(600);
  expect(client.entry()).not.toBeNull();
});

test('authentication clearing removes the compass even before the page changes', async () => {
  const client = clientDocument({ modern: true });
  await settle();
  expect(client.entry()).not.toBeNull();
  client.setSession('', '');
  await vi.advanceTimersByTimeAsync(300);
  expect(client.entry()).toBeNull();
  expect(client.panel()).toBeNull();
});

test('router navigation from a cold dashboard bootstraps Discovery when Home mounts', async () => {
  const client = clientDocument({ hash: '#/dashboard' });
  await settle();
  expect(client.ownRequests()).toBe(0);
  client.w.history.pushState(null, '', '#/home');
  client.w.document.body.classList.add('home-page');
  await settle();
  expect(client.entry()).not.toBeNull();
});

test('a late probe cannot recreate navigation on a login route with a retained user ID', async () => {
  const client = clientDocument();
  const pending = deferred<unknown>();
  client.setReply(() => pending.promise);
  await settle();
  client.route('#/login');
  pending.resolve(recommendations('Old session'));
  await settle();
  expect(client.entry()).toBeNull();
  expect(client.w.document.querySelector('.jfhelper-nav-discovery')).toBeNull();
});

test('a stale failure cannot revoke access after signing back into the same account', async () => {
  const client = clientDocument();
  const pending = deferred<unknown>();
  client.setReply(() => pending.promise);
  await settle();
  client.setSession('', '');
  client.route('#/login');
  await settle();
  client.setSession('a', 'replacement-token');
  client.setReply(() => Promise.resolve(recommendations('New session')));
  client.route('#/home');
  await settle();
  pending.reject({ status: 403 });
  await settle();
  expect(client.panel()?.textContent).toContain('New session');
  expect(client.entry()).not.toBeNull();
});

test.each([0, 1])('native tab selection %s removes the Discovery deep link', async index => {
  const client = clientDocument();
  await settle();
  (client.w as any).JellyfinHelperTabs.focus('discovery');
  await settle();
  expect(client.w.location.hash).toContain('jfhTab=discovery');
  client.tabs.querySelector('[data-index="' + index + '"]').click();
  await settle();
  expect(client.tabs.selectedIndex()).toBe(index);
  expect(client.w.location.hash).not.toContain('jfhTab');
  expect(client.w.location.hash).toBe(index === 0 ? '#/home' : '#/home?tab=1');
});

test.each([false, true])('browser history restores cached Home and Discovery with modern layout %s', async modern => {
  const client = clientDocument({ modern });
  await settle();
  for (let visit = 0; visit < 20; visit++) {
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
    client.tabs.querySelector('[data-index="0"]').click();
    await settle();
    (client.w as any).JellyfinHelperTabs.focus('discovery');
    await settle();
  }
  const historyLength = client.w.history.length;
  expect(client.panel()?.classList.contains('is-active')).toBe(true);
  client.w.history.back();
  await settle();
  expect(client.w.location.hash).toBe('#/home');
  expect(client.tabs.selectedIndex()).toBe(0);
  expect(client.panel()?.classList.contains('is-active')).toBe(false);
  expect(client.w.document.querySelector('#panels > .tabContent[data-index="0"]')?.classList.contains('is-active')).toBe(true);
  expect(client.w.history).toHaveLength(historyLength);
  client.w.history.forward();
  await settle();
  expect(client.w.location.hash).toContain('jfhTab=discovery');
  expect(client.tabs.selectedIndex()).toBe(2);
  expect(client.panel()?.classList.contains('is-active')).toBe(true);
  expect(client.w.history).toHaveLength(historyLength);
});

test.each([false, true])('browser history restores Favorites before Discovery with modern layout %s', async modern => {
  const client = clientDocument({ modern });
  await settle();
  (client.w as any).JellyfinHelperTabs.focus('discovery');
  await settle();
  client.tabs.querySelector('[data-index="1"]').click();
  await settle();
  (client.w as any).JellyfinHelperTabs.focus('discovery');
  await settle();
  const historyLength = client.w.history.length;
  client.w.history.back();
  await settle();
  expect(client.w.location.hash).toBe('#/home?tab=1');
  expect(client.tabs.selectedIndex()).toBe(1);
  expect(client.panel()?.classList.contains('is-active')).toBe(false);
  expect(client.w.document.querySelector('#panels > .tabContent[data-index="1"]')?.classList.contains('is-active')).toBe(true);
  expect(client.w.history).toHaveLength(historyLength);
  client.w.history.forward();
  await settle();
  expect(client.panel()?.classList.contains('is-active')).toBe(true);
  expect(client.w.history).toHaveLength(historyLength);
});

test('a layout change immediately removes the obsolete compass fallback', async () => {
  const client = clientDocument({ modern: true });
  await settle();
  expect(client.w.document.getElementById('jfh-tab-link-discovery')).not.toBeNull();
  (client.w.document.querySelector('.headerTabs') as HTMLElement).style.display = '';
  client.w.dispatchEvent(new client.w.Event('resize'));
  await settle();
  expect(client.w.document.getElementById('jfh-tab-link-discovery')).toBeNull();
});

test('the sidebar is restored when its entire drawer is replaced', async () => {
  const client = clientDocument();
  await settle();
  const replacement = client.w.document.createElement('div');
  replacement.className = 'mainDrawer';
  replacement.innerHTML = '<div class="mainDrawer-scrollContainer"></div>';
  client.w.document.querySelector('.mainDrawer')!.replaceWith(replacement);
  await settle();
  expect(replacement.querySelector('.jfhelper-nav-discovery')).not.toBeNull();
});

test('the first login moves Discovery into a drawer whose Home href becomes ready later without refresh', async () => {
  const client = clientDocument({ modern: true, hash: '#/login', initialUser: '' });
  const drawer = client.w.document.createElement('div');
  drawer.className = 'MuiDrawer-paper';
  drawer.innerHTML = '<ul class="MuiList-root"><li><a href="#">Home</a></li></ul>';
  client.w.document.body.appendChild(drawer);
  await settle();
  expect(client.w.document.querySelector('.jfhelper-nav-discovery')).toBeNull();
  client.setSession('a');
  client.route('#/home');
  await settle();
  expect(client.w.document.querySelector('.mainDrawer-scrollContainer .jfhelper-nav-discovery')).not.toBeNull();
  const before = client.ownRequests();
  drawer.querySelector('a')!.setAttribute('href', '#/home');
  await settle();
  expect(drawer.querySelector('.jfhelper-nav-discovery')).not.toBeNull();
  expect(client.w.document.querySelector('.mainDrawer-scrollContainer .jfhelper-nav-discovery')).toBeNull();
  expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
  expect(client.ownRequests()).toBe(before);
  client.setSession('', '');
  client.route('#/login');
  await settle();
  expect(drawer.querySelector('.jfhelper-nav-discovery')).toBeNull();
});

test('navigation moves to the modern drawer and returns to the legacy drawer when it unmounts', async () => {
  const client = clientDocument({ modern: true });
  await settle();
  const paper = client.w.document.createElement('div');
  paper.className = 'MuiDrawer-paper';
  paper.innerHTML = '<ul class="MuiList-root"></ul>';
  appendHomeLink(paper.querySelector('ul')!);
  client.w.document.body.appendChild(paper);
  await settle();
  expect(paper.querySelector('.jfhelper-nav-discovery')).not.toBeNull();
  expect(paper.querySelector('.jellyfinHelperSection')?.tagName).toBe('LI');
  expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
  expect(client.w.document.querySelector('.mainDrawer-scrollContainer .jfhelper-nav-discovery')).toBeNull();
  paper.remove();
  await settle();
  expect(client.w.document.querySelector('.mainDrawer-scrollContainer .jfhelper-nav-discovery')).not.toBeNull();
  expect(client.w.document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
});

test('modern navigation ignores unrelated lists and preserves other section actions', () => {
  const ui = freshDiscoveryHelpers();
  window.history.replaceState(null, '', '#/home');
  (globalThis as any).ApiClient = { getCurrentUserId: () => 'a' };
  document.body.innerHTML = '<div class="MuiDrawer-paper"><ul class="MuiList-root"><li><a href="https://example.org">Other link</a></li></ul><ul class="MuiList-root"></ul></div><div class="mainDrawer-scrollContainer"><div class="jellyfinHelperSection"><button>Other action</button></div></div>';
  appendHomeLink(document.querySelectorAll('.MuiDrawer-paper ul')[1]);
  ui.injectNavigation();
  ui.injectNavigation();
  expect(document.querySelector('.MuiDrawer-paper .jfhelper-nav-discovery')).not.toBeNull();
  expect(document.querySelectorAll('.jfhelper-nav-discovery')).toHaveLength(1);
  ui.removeDiscoveryNavigation();
  expect(document.querySelector('.mainDrawer-scrollContainer button')?.textContent).toBe('Other action');
  expect(document.querySelector('.MuiDrawer-paper .jellyfinHelperSection')).toBeNull();
  (globalThis as any).ApiClient.getCurrentUserId = () => '';
  ui.injectNavigation();
  expect(document.querySelector('.jfhelper-nav-discovery')).toBeNull();
  (globalThis as any).ApiClient.getCurrentUserId = () => 'a';
  window.history.replaceState(null, '', '#/login');
  ui.injectNavigation();
  expect(document.querySelector('.jfhelper-nav-discovery')).toBeNull();
});

test('modern sidebar uses a short localized label without inherited legacy spacing classes', async () => {
  const client = clientDocument({ modern: true, responses: {
    [translationsPath]: () => Promise.resolve({ discoveryNavigationTitle: 'Entdecken' }),
  } });
  const drawer = client.w.document.createElement('div');
  drawer.className = 'MuiDrawer-paper';
  drawer.innerHTML = '<ul class="MuiList-root"></ul>';
  appendHomeLink(drawer.querySelector('ul')!);
  client.w.document.body.appendChild(drawer);
  await settle();
  const navigation = drawer.querySelector('.jfhelper-nav-discovery')!;
  expect(navigation.textContent).toBe('exploreEntdecken');
  expect(navigation.getAttribute('aria-label')).toBe('Entdecken');
  expect(navigation.querySelector('.navMenuOptionIcon, .navMenuOptionText')).toBeNull();
  expect(navigation.querySelector('.jfh-discovery-navigation-icon')).not.toBeNull();
  expect(navigation.querySelector('.jfh-discovery-navigation-text')?.textContent).toBe('Entdecken');
  expect(drawer.querySelector('.jfh-discovery-sidebar-heading')?.textContent).toBe('Jellyfin Helper');
  const style = client.w.getComputedStyle(navigation);
  expect(style.display).toBe('grid');
  expect(style.gridTemplateColumns).toMatch(/minmax\(0(?:px)?,\s*1fr\)/);
});

test('request ownership includes the token and API client as well as the user', () => {
  const ui = freshDiscoveryHelpers();
  window.history.replaceState(null, '', '#/home');
  const api = { getCurrentUserId: () => 'a', accessToken: () => 'first' };
  (globalThis as any).ApiClient = api;
  const request = ui.captureDiscoveryRequest();
  expect(ui.isCurrentDiscoveryRequest(request)).toBe(true);
  api.accessToken = () => 'second';
  expect(ui.isCurrentDiscoveryRequest(request)).toBe(false);
  (globalThis as any).ApiClient = { ...api, accessToken: () => 'first' };
  expect(ui.isSameDiscoverySession(request)).toBe(false);
});

test.each([true, false])('stale permission completion cannot submit a request in a replacement session, success %s', async success => {
  const ui = freshDiscoveryHelpers();
  window.history.replaceState(null, '', '#/home');
  document.body.innerHTML = '<button id="request">Request</button>';
  const pending = deferred<unknown>();
  let token = 'first';
  const ajax = vi.fn(() => pending.promise);
  (globalThis as any).ApiClient = {
    getCurrentUserId: () => 'a', accessToken: () => token, getUrl: (path: string) => path, ajax,
  };
  ui.fetchPermissionsAndRequest(123, 'movie', document.getElementById('request'));
  token = 'replacement';
  if (success) pending.resolve({ CanRequest: true, Profiles: [] });
  else pending.reject({ status: 500 });
  await settle();
  expect(ajax).toHaveBeenCalledTimes(1);
  expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
});

test.each(['request', 'duplicate', 'dismiss'])('a delayed %s response cannot mutate the replacement session cache', async action => {
  const ui = freshDiscoveryHelpers();
  window.history.replaceState(null, '', '#/home');
  document.body.innerHTML = '<div class="jfh-discovery-card"><button id="request">Request</button></div>';
  const pending = deferred<unknown>();
  let token = 'first';
  (globalThis as any).ApiClient = {
    getCurrentUserId: () => 'a', accessToken: () => token, getUrl: (path: string) => path, ajax: () => pending.promise,
  };
  const button = document.getElementById('request');
  if (action !== 'dismiss') ui.submitRequest(123, 'movie', null, null, null, button);
  else ui.executeDismiss(123, 'movie', button);
  token = 'replacement';
  const replacement = recommendations('Replacement session');
  ui.setCachedDiscoveryResult(replacement);
  pending.resolve({ Success: true, AlreadyRequested: action === 'duplicate' });
  await settle();
  expect(ui.getCachedDiscoveryResult()).toEqual(replacement);
  expect(document.querySelector('.jfh-discovery-toast')).toBeNull();
});

test('the instrumented lifecycle observer detects router page mutations and can be disposed', async () => {
  const ui = freshDiscoveryHelpers();
  document.body.innerHTML = '';
  window.history.replaceState(null, '', '#/dashboard');
  const ajax = vi.fn(() => Promise.resolve({}));
  (globalThis as any).ApiClient = { getCurrentUserId: () => 'a', getUrl: (path: string) => path, ajax };
  const dispose = ui.initDiscoveryLifecycle();
  try {
    window.history.pushState(null, '', '#/home');
    document.body.classList.add('home-mounted');
    await settle();
    expect(ajax.mock.calls.length).toBeGreaterThan(0);
    document.dispatchEvent(new Event('viewshow'));
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('pageshow'));
  } finally {
    dispose();
  }
});
