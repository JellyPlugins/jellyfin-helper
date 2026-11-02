import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { JSDOM } from 'jsdom';
import { afterAll, afterEach, describe, expect, test, vi } from 'vitest';
import { freshDiscoveryHelpers, REPO_ROOT, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

const scripts = ['native-tabs.js', 'discovery-sidebar.js'].map(name =>
  readFileSync(join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper/js', name), 'utf8'));
const documents: JSDOM[] = [];

afterEach(() => {
  documents.splice(0).forEach(dom => dom.window.close());
  vi.clearAllTimers();
});

function recommendations(title: string) {
  return { Recommendations: [{ Title: title, TmdbId: 123, MediaType: 'movie', Score: 0.8, Genres: [] }] };
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

function clientDocument(options: { modern?: boolean; hash?: string; lateApi?: boolean } = {}) {
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
  let userId = 'a';
  let token = 'session-a';
  let reply: () => Promise<unknown> = () => Promise.resolve(recommendations('Recommendations for ' + userId));
  const api = {
    getCurrentUserId: () => userId,
    accessToken: () => token,
    getUrl: (path: string) => path,
    ajax: vi.fn((request: { url: string }) => {
      if (request.url === '/JellyfinHelper/Discovery/My') return reply();
      if (request.url.includes('/Trakt')) return Promise.reject({ status: 403 });
      return Promise.resolve({});
    }),
  };
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
    w, api, tabs, route,
    setSession: (user: string, accessToken = 'session-' + user) => { userId = user; token = accessToken; },
    setReply: (next: () => Promise<unknown>) => { reply = next; },
    panel: () => w.document.getElementById('jfh-tab-panel-discovery'),
    entry: () => w.document.getElementById(options.modern ? 'jfh-tab-link-discovery' : 'jfh-tab-btn-discovery'),
    ownRequests: () => api.ajax.mock.calls.filter(([request]) => request.url === '/JellyfinHelper/Discovery/My').length,
  };
}

describe.each([false, true])('shipped Discovery lifecycle with modern layout %s', modern => {
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

test('navigation moves to the modern drawer and returns to the legacy drawer when it unmounts', async () => {
  const client = clientDocument({ modern: true });
  await settle();
  const paper = client.w.document.createElement('div');
  paper.className = 'MuiDrawer-paper';
  paper.innerHTML = '<ul class="MuiList-root"><li><a href="#/home">Home</a></li></ul>';
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
  document.body.innerHTML = '<div class="MuiDrawer-paper"><ul class="MuiList-root"><li><a href="https://example.org">Other link</a></li></ul><ul class="MuiList-root"><li><a href="#/home">Home</a></li></ul></div><div class="mainDrawer-scrollContainer"><div class="jellyfinHelperSection"><button>Other action</button></div></div>';
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

test.each(['request', 'dismiss'])('a delayed %s response cannot mutate the replacement session cache', async action => {
  const ui = freshDiscoveryHelpers();
  window.history.replaceState(null, '', '#/home');
  document.body.innerHTML = '<div class="jfh-discovery-card"><button id="request">Request</button></div>';
  const pending = deferred<unknown>();
  let token = 'first';
  (globalThis as any).ApiClient = {
    getCurrentUserId: () => 'a', accessToken: () => token, getUrl: (path: string) => path, ajax: () => pending.promise,
  };
  const button = document.getElementById('request');
  if (action === 'request') ui.submitRequest(123, 'movie', null, null, null, button);
  else ui.executeDismiss(123, 'movie', button);
  token = 'replacement';
  const replacement = recommendations('Replacement session');
  ui.setCachedDiscoveryResult(replacement);
  pending.resolve({ Success: true });
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
