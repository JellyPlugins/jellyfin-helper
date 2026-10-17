import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
  // Selected-user persistence lives in localStorage and survives a fresh bundle
  // scope, so an earlier test's user switch would otherwise steer later loads.
  window.localStorage.clear();
  ui = freshConfigPage();
});

afterAll(saveCoverage);

function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []) {
  const calls: { url: string; body?: unknown }[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      let body: unknown;
      try {
        body = typeof opts.data === 'string' ? JSON.parse(opts.data) : opts.data;
      } catch {
        body = opts.data;
      }
      calls.push({ url: String(opts.url), body });
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      const url = String(opts.url);
      const exact = Object.entries(routes).find(([key]) => url === key);
      if (exact) return Promise.resolve(exact[1]);
      // Insertion order decides substring matches, so a caller lists a specific
      // route (e.g. Discovery/Request) before the generic one it extends.
      for (const [key, value] of Object.entries(routes)) {
        if (url.includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

// Loose on purpose: inference would lock the map to the happy-path fixture and
// reject the degraded payloads these tests reshape it into.
type RouteTable = Record<string, any>;

const users = (): any[] => [
  {
    UserId: 'u1',
    UserName: 'Alice',
    Recommendations: [{ Name: 'M1', Score: 0.9, Genres: ['Action'], Year: 2020 }],
  },
  { UserId: 'u2', UserName: 'Bob', Recommendations: [] },
];

function fullRoutes(): RouteTable {
  return {
    'Recommendations/WatchProfile/': { WatchedMovieCount: 1, WatchedEpisodeCount: 0, GenreDistribution: { Action: 2 } },
    'UserActivity/User/': [{ ItemName: 'E1', TotalPlayCount: 2, AverageCompletionPercent: 100 }],
    'JellyfinHelper/Discovery': [
      {
        UserId: 'u1',
        Recommendations: [{ Title: 'D1', Score: 0.8, MediaType: 'movie', TmdbId: 5, Genres: ['Drama'] }],
      },
    ],
    'Diagnostics/Ensemble': { Available: true, Alpha: 0.5, NeuralBeta: 0, NeuralEnabled: false },
    'JellyfinHelper/Recommendations': users(),
  };
}

type Deferred = { url: string; resolve: (v: unknown) => void; reject: (e: unknown) => void };

// Hands the test control over when selected requests settle. Settling them in request
// order would paint the newest answer last anyway, so a stale-response guard could be
// deleted without any test noticing; these suites therefore answer the newest request
// first and the superseded one afterwards.
function deferredRouter(shouldDefer: (url: string) => boolean, routes: RouteTable = fullRoutes()) {
  const pending: Deferred[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      const url = String(opts.url);
      if (shouldDefer(url)) {
        return new Promise((resolve, reject) => {
          pending.push({ url, resolve: resolve as (v: unknown) => void, reject });
        });
      }
      for (const [key, value] of Object.entries(routes)) {
        if (url.includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return pending;
}

const waiting = (pending: Deferred[], part: string) => pending.filter((p) => p.url.includes(part));

const activityRow = (name: string) => [{ ItemName: name, TotalPlayCount: 1, AverageCompletionPercent: 100 }];

describe('recommendations tab flow', () => {
  test('loads users, profile, activity, discovery and footnote in one pass', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(document.getElementById('recsUserSelect')).not.toBeNull();
    expect(document.querySelector('#recsUserGrid .recs-item')).not.toBeNull();
    expect(document.getElementById('recsUserProfile')!.textContent).toContain('Watched');
    expect(document.querySelectorAll('#recsUserActivity tbody tr')).toHaveLength(1);
    expect(document.querySelector('#discoveryGrid .discovery-card')).not.toBeNull();
    expect(document.getElementById('recsEnsembleFootnote')!.textContent).toContain('50%');
  });

  test('second visit inside the ttl reuses the cache without refetching', async () => {
    const calls = ajaxRouter(fullRoutes());
    const recsCalls = () => calls.filter((c) => c.url === 'JellyfinHelper/Recommendations').length;
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(recsCalls()).toBe(1);
    await vi.advanceTimersByTimeAsync(6 * 60 * 1000);
    ui.initRecommendationsTab();
    await flush();
    expect(recsCalls()).toBe(2);
    ui.initRecommendationsTab();
    await flush();
    expect(recsCalls()).toBe(2);
  });

  test('failed load renders the error state', async () => {
    ajaxRouter({}, ['JellyfinHelper/Recommendations']);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.loadRecommendations();
    await flush();
    expect(document.getElementById('recsContent')!.textContent).toMatch(/failed to load/i);
  });

  test('missing container does not throw', () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    expect(() => ui.loadRecommendations()).not.toThrow();
  });
});

describe('discovery request through the profile popup', () => {
  // Specific Discovery sub-routes must precede the generic all-users Discovery
  // key: insertion-order substring routing would otherwise answer the service
  // lookup and the request POST with the user-discovery payload.
  const popupRoutes = (services: unknown, extra: RouteTable = {}): RouteTable => ({
    'Discovery/Services/radarr': services,
    ...extra,
    ...fullRoutes(),
  });

  test('profile click submits with server, profile and folder', async () => {
    // Real wire shape: the Services DTO serializes lowercase (id/name/profiles/
    // activeProfileId/activeDirectory), and server id 0 is a valid Seerr id.
    const calls = ajaxRouter(popupRoutes(
      [{ id: 0, name: 'R', profiles: [{ id: 4, name: 'HD' }], activeProfileId: 4, activeDirectory: '/movies' }],
      { 'Discovery/Request': { Success: true } },
    ));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    const profile = document.querySelector('.discovery-popup-user') as HTMLButtonElement;
    expect(profile.textContent).toContain('HD');
    profile.click();
    await flush();
    const post = calls.find((c) => c.url.includes('Discovery/Request'));
    expect(post).toBeDefined();
    expect(post!.body).toMatchObject({ TmdbId: 5, ServerId: 0, ProfileId: 4, RootFolder: '/movies' });
    expect(document.getElementById('seerrUserPopup')).toBeNull();
    // The success response marks the originating card done, proving the request
    // resolved as success and not the generic discovery payload.
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
  });

  test('escape closes the popup without submitting', async () => {
    const calls = ajaxRouter(popupRoutes([{ id: 0, profiles: [{ id: 4, name: 'HD' }] }]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    (document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement).click();
    await flush();
    document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(document.getElementById('seerrUserPopup')).toBeNull();
    expect(calls.some((c) => c.url.includes('Discovery/Request'))).toBe(false);
  });

  test('cancel closes the popup without submitting', async () => {
    ajaxRouter(popupRoutes([{ id: 0, profiles: [{ id: 4, name: 'HD' }] }]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    (document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement).click();
    await flush();
    const cancel = [...document.querySelectorAll('.discovery-popup-cancel')].pop() as HTMLButtonElement;
    cancel.click();
    expect(document.getElementById('seerrUserPopup')).toBeNull();
  });

  test('switching users reloads profile, activity and discovery', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.getElementById('recsUserGrid')!.textContent).toMatch(/no recommendations/i);
  });

  test('cached services reopen the popup and replace any existing overlay', async () => {
    const calls = ajaxRouter(popupRoutes([{ id: 1, profiles: [{ id: 4, name: 'HD' }] }]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    expect(document.querySelectorAll('.discovery-popup-user')).toHaveLength(1);
    // Second open uses the cached lookup and must replace the first overlay.
    btn.click();
    expect(document.querySelectorAll('#seerrUserPopup')).toHaveLength(1);
    expect(document.querySelectorAll('.discovery-popup-user')).toHaveLength(1);
    expect(calls.filter((c) => c.url.includes('Discovery/Services'))).toHaveLength(1);
  });

  test('a leftover overlay without an escape handler is removed cleanly', async () => {
    ajaxRouter(popupRoutes([{ id: 1, profiles: [{ id: 4, name: 'HD' }] }]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    (document.querySelector('.discovery-popup-cancel') as HTMLButtonElement).click();
    document.body.insertAdjacentHTML('beforeend', '<div id="seerrUserPopup"></div>');
    btn.click();
    expect(document.querySelectorAll('#seerrUserPopup')).toHaveLength(1);
  });

  test('multi-server profiles label the server and overlay clicks close the popup', async () => {
    ajaxRouter(popupRoutes([
      { id: 1, name: 'Alpha', profiles: [{ id: 2, name: 'HD' }], activeProfileId: 2, activeDirectory: '/a' },
      { id: 3, name: 'Beta', profiles: [{ id: 4, name: '4K' }], activeProfileId: 9, activeDirectory: '/b' },
    ]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    (document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement).click();
    await flush();
    const labels = [...document.querySelectorAll('.discovery-popup-user')].map((el) => el.textContent || '');
    expect(labels).toHaveLength(2);
    expect(labels.some((l) => l.includes('Beta'))).toBe(true);
    expect(labels.some((l) => l.includes('default'))).toBe(true);
    expect(document.querySelector('.discovery-popup-user-default')).not.toBeNull();
    document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(document.getElementById('seerrUserPopup')).not.toBeNull();
    (document.getElementById('seerrUserPopup') as HTMLElement).click();
    expect(document.getElementById('seerrUserPopup')).toBeNull();
  });

  test('closing an already removed popup is idempotent', async () => {
    ajaxRouter(popupRoutes([{ id: 1, profiles: [{ id: 4, name: 'HD' }] }]));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    const cancel = document.querySelector('.discovery-popup-cancel') as HTMLButtonElement;
    btn.remove();
    cancel.click();
    expect(document.getElementById('seerrUserPopup')).toBeNull();
    expect(() => cancel.click()).not.toThrow();
  });

  test('no services submits the request with defaults', async () => {
    const calls = ajaxRouter(popupRoutes([], { 'Discovery/Request': { Success: true } }));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    const post = calls.find((c) => c.url.includes('Discovery/Request'));
    expect(post).toBeDefined();
    expect(post!.body).toEqual({ TmdbId: 5, MediaType: 'movie' });
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
  });

  test('a server without profiles falls back to a default submit', async () => {
    const calls = ajaxRouter(popupRoutes([{ id: 1, name: 'S' }], { 'Discovery/Request': { Success: true } }));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    expect(calls.some((c) => c.url.includes('Discovery/Request'))).toBe(true);
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
  });

  test('a second media type reuses the initialized services cache', async () => {
    const routes = popupRoutes(
      [{ id: 0, profiles: [{ id: 4, name: 'HD' }] }],
      { 'Discovery/Services/sonarr': [{ id: 0, profiles: [{ id: 6, name: 'TV' }] }] },
    );
    routes['JellyfinHelper/Discovery'] = [{
      UserId: 'u1',
      Recommendations: [
        { Title: 'Movie', Score: 0.8, MediaType: 'movie', TmdbId: 5 },
        { Title: 'Show', Score: 0.7, MediaType: 'tv', TmdbId: 6 },
      ],
    }];
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btns = document.querySelectorAll('#discoveryGrid .discovery-request-btn');
    (btns[0] as HTMLButtonElement).click();
    await flush();
    (document.querySelector('.discovery-popup-cancel') as HTMLButtonElement).click();
    (btns[1] as HTMLButtonElement).click();
    await flush();
    const labels = [...document.querySelectorAll('.discovery-popup-user')].map((el) => el.textContent || '');
    expect(labels.some((l) => l.includes('TV'))).toBe(true);
  });

  test('profile submit failure marks the trigger button as failed', async () => {
    const calls = ajaxRouter(popupRoutes([{ id: 1, profiles: [{ id: 4, name: 'HD' }] }]), ['Discovery/Request']);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    (document.querySelector('.discovery-popup-user') as HTMLButtonElement).click();
    await flush();
    expect(btn.innerHTML).toMatch(/fail/i);
    expect(calls.filter((c) => c.url.includes('Discovery/Request'))).toHaveLength(1);
  });

  test('a detached trigger keeps spinner state on lookup failure', async () => {
    const calls = ajaxRouter({}, ['Discovery/Services']);
    document.body.innerHTML = '';
    const btn = document.createElement('button');
    expect(() => ui.showSeerrUserPopup(9, 'movie', btn)).not.toThrow();
    await flush();
    expect(btn.innerHTML).toContain('spinner');
    expect(calls.some((c) => c.url.includes('Discovery/Request'))).toBe(false);
  });

  test('a detached trigger is left untouched after a successful lookup', async () => {
    ajaxRouter({ 'Discovery/Services/radarr': [{ id: 1, profiles: [{ id: 4, name: 'HD' }] }] });
    const btn = document.createElement('button');
    ui.showSeerrUserPopup(9, 'movie', btn);
    await flush();
    expect(document.querySelector('#seerrUserPopup')).toBeNull();
    expect(btn.disabled).toBe(true);
  });

  test('request handler ignores disabled and malformed buttons', () => {
    ajaxRouter({});
    document.body.innerHTML = '';
    const disabled = document.createElement('button');
    disabled.disabled = true;
    ui.handleDiscoveryRequest({ currentTarget: disabled });
    const malformedId = document.createElement('button');
    malformedId.dataset.tmdbId = 'abc';
    malformedId.dataset.mediaType = 'movie';
    ui.handleDiscoveryRequest({ currentTarget: malformedId });
    const malformedType = document.createElement('button');
    malformedType.dataset.tmdbId = '5';
    ui.handleDiscoveryRequest({ currentTarget: malformedType });
    expect(document.getElementById('seerrUserPopup')).toBeNull();
  });

  test('direct submit failure marks the button as failed', async () => {
    ajaxRouter({}, ['Discovery/Request']);
    document.body.innerHTML = '<div id="b"></div>';
    const btn = document.createElement('button');
    document.getElementById('b')!.appendChild(btn);
    ui.submitDiscoveryRequest(7, 'movie', null, btn);
    await flush();
    expect(btn.innerHTML).toMatch(/fail/i);
  });

  test('undefined services payload submits with defaults', async () => {
    const calls = ajaxRouter(popupRoutes(undefined, { 'Discovery/Request': { Success: true } }));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const btn = document.querySelector('#discoveryGrid .discovery-request-btn') as HTMLButtonElement;
    btn.click();
    await flush();
    const post = calls.find((c) => c.url.includes('Discovery/Request'));
    expect(post).toBeDefined();
    expect(post!.body).toEqual({ TmdbId: 5, MediaType: 'movie' });
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
  });
});

describe('recommendations tab render variants', () => {
  test('toggle buttons expand every collapsible and scored items sort descending', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Recommendations'] = [
      {
        UserId: 'u1',
        UserName: 'Alice',
        Recommendations: [
          { Name: 'Low', Score: 0.2, ItemType: 'Movie', Year: 2001, Genres: ['A', 'B', 'C', 'D'] },
          { Name: 'None1' },
          { Name: 'None2' },
          { Name: 'High', Score: 0.9 },
        ],
      },
    ];
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const titles = [...document.querySelectorAll('#recsUserGrid .recs-item-title')].map((el) => el.textContent);
    expect(titles).toEqual(['High', 'Low', 'None1', 'None2']);
    (document.getElementById('recsGridToggle') as HTMLButtonElement).click();
    expect(document.getElementById('recsGridBody')!.classList.contains('open')).toBe(true);
    (document.getElementById('recsActivityToggle') as HTMLButtonElement).click();
    expect(document.getElementById('recsActivityBody')!.classList.contains('open')).toBe(true);
    (document.getElementById('discoveryToggle') as HTMLButtonElement).click();
    expect(document.getElementById('discoveryBody')!.classList.contains('open')).toBe(true);
  });

  test('saved selection is restored and a user without ids degrades quietly', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Recommendations'] = [
      { UserName: 'Ghost', Recommendations: [{ Name: 'G1', Score: 0.5 }] },
      { UserId: 'u2', UserName: 'Bob', Recommendations: [] },
    ];
    ajaxRouter(routes);
    window.localStorage.clear();
    window.localStorage.setItem('jh_recsSelectedUser', 'u2');
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    expect(select.value).toBe('1');
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.getElementById('recsUserProfile')!.innerHTML).toBe('');
    expect(document.getElementById('recsUserActivity')!.innerHTML).toBe('');
    window.localStorage.clear();
  });

  test('selecting a nonexistent index leaves panels empty', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '9';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.getElementById('recsUserProfile')!.innerHTML).toBe('');
    expect(document.getElementById('recsUserActivity')!.innerHTML).toBe('');
    expect(document.querySelector('#recsUserGrid .recs-item')).not.toBeNull();
    expect(document.querySelector('#discoveryGrid .discovery-card')).not.toBeNull();
  });

  test('a stripped tab tolerates missing panels on user change', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    // Count header gone but grid intact: the grid still repaints.
    document.getElementById('recsGridCount')!.remove();
    select.value = '1';
    expect(() => select.dispatchEvent(new window.Event('change', { bubbles: true }))).not.toThrow();
    // Every panel gone: each loader must bail out silently.
    for (const id of ['recsUserGrid', 'recsUserProfile', 'recsUserActivity', 'discoveryGrid']) {
      document.getElementById(id)!.remove();
    }
    select.value = '0';
    expect(() => select.dispatchEvent(new window.Event('change', { bubbles: true }))).not.toThrow();
    await flush();
    expect(document.getElementById('recsUserSelect')).not.toBeNull();
  });

  test('stale profile and activity failures only paint for the latest selection', async () => {
    const pending = deferredRouter((u) => u.includes('WatchProfile/') || u.includes('UserActivity/User/'));
    const profiles = () => waiting(pending, 'WatchProfile/');
    const activities = () => waiting(pending, 'UserActivity/User/');
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    // Failures are never cached, so the later switch back to u1 really refetches.
    profiles()[0].reject(new Error('down'));
    activities()[0].reject(new Error('down'));
    await flush();
    expect(document.getElementById('recsUserProfile')!.textContent).toMatch(/no watch profile/i);
    expect(document.getElementById('recsUserActivity')!.textContent).toMatch(/no watch activity/i);
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(profiles()).toHaveLength(3);
    // The latest selection (u1) fails and paints the empty states...
    profiles()[2].reject(new Error('down'));
    activities()[2].reject(new Error('down'));
    await flush();
    // ...then the superseded u2 request succeeds late and must be discarded.
    profiles()[1].resolve({ WatchedMovieCount: 3, GenreDistribution: {} });
    activities()[1].resolve(activityRow('StaleItem'));
    await flush();
    const profile = document.getElementById('recsUserProfile')!.textContent!;
    expect(profile).toMatch(/no watch profile/i);
    expect(profile).not.toContain('3 Watched');
    const activity = document.getElementById('recsUserActivity')!.textContent!;
    expect(activity).toMatch(/no watch activity/i);
    expect(activity).not.toContain('StaleItem');
  });

  test('expired discovery caches refetch and later switches reuse the fresh cache', async () => {
    const calls = ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    await vi.advanceTimersByTimeAsync(6 * 60 * 1000);
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(calls.filter((c) => c.url.includes('JellyfinHelper/Discovery'))).toHaveLength(2);
    expect(document.querySelector('#discoveryGrid .discovery-card')).not.toBeNull();
  });

  test('marking discovery items updates caches and can empty the grid', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    ui.markDiscoveryItemRequested(999, 'movie');
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    expect(document.querySelector('#discoveryGrid .discovery-card')).not.toBeNull();
    ui.markDiscoveryItemRequested(5, 'movie');
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    expect(document.getElementById('discoveryCount')!.textContent).toBe('0');
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/no suggestions/i);
  });

  test('marking walks entries that carry no media type', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Discovery'] = [{
      UserId: 'u1',
      Recommendations: [
        { Title: 'NoType', TmdbId: 9 },
        { Title: 'Movie', Score: 0.8, MediaType: 'movie', TmdbId: 5 },
      ],
    }];
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    ui.markDiscoveryItemRequested(9, 'movie');
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    expect(document.getElementById('discoveryCount')!.textContent).toBe('2');
  });

  test('stale profile and activity successes are dropped', async () => {
    const pending = deferredRouter((u) => u.includes('WatchProfile/') || u.includes('UserActivity/User/'));
    const profiles = () => waiting(pending, 'WatchProfile/');
    const activities = () => waiting(pending, 'UserActivity/User/');
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    // Fail the initial u1 load: failures are not cached, so switching back refetches.
    profiles()[0].reject(new Error('down'));
    activities()[0].reject(new Error('down'));
    await flush();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(profiles()).toHaveLength(3);
    expect(activities()).toHaveLength(3);
    // The latest selection (u1) answers first, the superseded u2 request afterwards.
    profiles()[2].resolve({ WatchedMovieCount: 7, GenreDistribution: {} });
    activities()[2].resolve(activityRow('LatestItem'));
    await flush();
    profiles()[1].resolve({ WatchedMovieCount: 3, GenreDistribution: {} });
    activities()[1].resolve(activityRow('StaleItem'));
    await flush();
    const profile = document.getElementById('recsUserProfile')!.textContent!;
    expect(profile).toContain('7 Watched');
    expect(profile).not.toContain('3 Watched');
    const activity = document.getElementById('recsUserActivity')!.textContent!;
    expect(activity).toContain('LatestItem');
    expect(activity).not.toContain('StaleItem');
  });

  test('undefined discovery payload counts as empty', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Discovery'] = undefined;
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/no suggestions/i);
  });

  test('cache invalidation skips falsy user entries', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Recommendations'] = [users()[0], false];
    const calls = ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    await vi.advanceTimersByTimeAsync(6 * 60 * 1000);
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '0';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(calls.filter((c) => c.url.includes('JellyfinHelper/Discovery'))).toHaveLength(2);
    expect(document.querySelector('#discoveryGrid .discovery-card')).not.toBeNull();
  });
});

describe('recommendations loading variants', () => {
  test('undefined recommendation payload counts as empty', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Recommendations'] = undefined;
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.loadRecommendations();
    await flush();
    expect(document.getElementById('recsContent')!.textContent).toMatch(/no recommendations available/i);
  });

  test('users without a recommendations array count as zero items', async () => {
    const routes = fullRoutes();
    routes['JellyfinHelper/Recommendations'] = [
      { UserId: 'x', UserName: 'X' },
      { UserId: 'y', UserName: 'Y', Recommendations: [{ Name: 'R', Score: 0.3 }] },
    ];
    ajaxRouter(routes);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.loadRecommendations();
    await flush();
    const options = document.querySelectorAll('#recsUserSelect option');
    expect(options).toHaveLength(2);
    expect(options[0].textContent).toContain('0 items');
  });

  test('a stale recommendation response cannot overwrite the newer one', async () => {
    const pending = deferredRouter((u) => u === 'JellyfinHelper/Recommendations');
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.loadRecommendations();
    ui.loadRecommendations();
    await flush();
    expect(pending).toHaveLength(2);
    // The newest list lands first; the superseded three-user payload follows.
    pending[1].resolve(users());
    await flush();
    pending[0].resolve([...users(), { UserId: 'u3', UserName: 'StaleUser', Recommendations: [] }]);
    await flush();
    expect(document.querySelectorAll('#recsUserSelect option')).toHaveLength(2);
    expect(document.getElementById('recsContent')!.textContent).not.toContain('StaleUser');
  });

  test('a stale recommendation failure is dropped', async () => {
    const pending = deferredRouter((u) => u === 'JellyfinHelper/Recommendations');
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.loadRecommendations();
    ui.loadRecommendations();
    await flush();
    // The newest request renders the list; the superseded one then fails.
    pending[1].resolve(users());
    await flush();
    pending[0].reject(new Error('down'));
    await flush();
    expect(document.getElementById('recsUserSelect')).not.toBeNull();
    expect(document.getElementById('recsContent')!.textContent).not.toMatch(/failed to load/i);
    // Only the newest request may paint the error banner.
    ui.loadRecommendations();
    await flush();
    pending[2].reject(new Error('down'));
    await flush();
    expect(document.getElementById('recsContent')!.textContent).toMatch(/failed to load/i);
  });

  test('cached revisit without a content container is a no-op', async () => {
    const calls = ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    const before = calls.filter((c) => c.url === 'JellyfinHelper/Recommendations').length;
    document.body.innerHTML = '';
    expect(() => ui.initRecommendationsTab()).not.toThrow();
    await flush();
    expect(calls.filter((c) => c.url === 'JellyfinHelper/Recommendations')).toHaveLength(before);
  });

  test('stale discovery failures are dropped while the latest paints the error', async () => {
    const pending = deferredRouter((u) => u.includes('JellyfinHelper/Discovery'));
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(pending).toHaveLength(1);
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    // Nothing cached yet (the first request is still open), so the switch refetches.
    expect(pending).toHaveLength(2);
    // The newest request fails and paints the error...
    pending[1].reject(new Error('down'));
    await flush();
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/could not load/i);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('0');
    // ...the superseded one then succeeds late and must not revive the cards.
    pending[0].resolve([
      { UserId: 'u1', Recommendations: [{ Title: 'StaleCard', Score: 0.8, MediaType: 'movie', TmdbId: 5 }] },
    ]);
    await flush();
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/could not load/i);
    expect(document.getElementById('discoveryGrid')!.textContent).not.toContain('StaleCard');
    expect(document.getElementById('discoveryCount')!.textContent).toBe('0');
  });

  test('discovery failure without a counter still renders the message', async () => {
    ajaxRouter(fullRoutes(), ['JellyfinHelper/Discovery']);
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    document.getElementById('discoveryCount')!.remove();
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    await flush();
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/could not load/i);
  });

  test('delayed stale discovery responses cannot overwrite the latest user data', async () => {
    const pending: Array<(v: unknown) => void> = [];
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => {
        const url = String(opts.url);
        if (url.includes('JellyfinHelper/Discovery')) {
          return new Promise((resolve) => { pending.push(resolve as (v: unknown) => void); });
        }
        if (url.includes('Diagnostics/Ensemble')) return Promise.resolve({ Available: false });
        if (url.includes('WatchProfile/')) return Promise.resolve({ GenreDistribution: {} });
        if (url.includes('UserActivity/')) return Promise.resolve([]);
        if (url.includes('JellyfinHelper/Recommendations')) return Promise.resolve(users());
        return Promise.resolve({});
      },
    };
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(pending).toHaveLength(1);
    const select = document.getElementById('recsUserSelect') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new window.Event('change', { bubbles: true }));
    expect(pending).toHaveLength(2);
    pending[1]([{ UserId: 'u2', Recommendations: [] }]);
    pending[0]([{ UserId: 'u1', Recommendations: [{ Title: 'D1', MediaType: 'movie', TmdbId: 5 }] }]);
    await flush();
    expect(document.getElementById('discoveryGrid')!.textContent).toMatch(/no suggestions/i);
  });
});
