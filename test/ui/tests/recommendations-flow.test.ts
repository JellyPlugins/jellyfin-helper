import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

let ui: UiBridge;
beforeEach(() => {
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

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

const users = () => [
  {
    UserId: 'u1',
    UserName: 'Alice',
    Recommendations: [{ Name: 'M1', Score: 0.9, Genres: ['Action'], Year: 2020 }],
  },
  { UserId: 'u2', UserName: 'Bob', Recommendations: [] },
];

function fullRoutes() {
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

describe('recommendations tab flow', () => {
  test('loads users, profile, activity, discovery and footnote in one pass', async () => {
    ajaxRouter(fullRoutes());
    document.body.innerHTML = '<div id="recsContent"></div>';
    ui.initRecommendationsTab();
    await flush();
    expect(document.getElementById('recsUserSelect')).not.toBeNull();
    expect(document.querySelector('#recsUserGrid .recs-item')).not.toBeNull();
    expect(document.getElementById('recsUserProfile')!.textContent).toContain('Watched');
    expect(document.querySelectorAll('#recsUserActivity tbody tr').length).toBe(1);
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
  const popupRoutes = (services: unknown, extra: Record<string, unknown> = {}) => ({
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
});
