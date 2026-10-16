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
      // Mirror the wire: apiPost serializes to a JSON string, so parse it back to
      // the object the server receives and assertions can match on the real shape.
      let body: unknown;
      try {
        body = typeof opts.data === 'string' ? JSON.parse(opts.data) : opts.data;
      } catch {
        body = opts.data;
      }
      calls.push({ url: String(opts.url), body });
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

describe('getTopGenresFromDistribution', () => {
  test('sorts by count and caps', () => {
    expect(ui.getTopGenresFromDistribution({ Action: 5, Drama: 9, Comedy: 1 }, 2)).toEqual(['Drama', 'Action']);
  });

  test.each([[null], [undefined], ['x'], [[]]])('garbage yields empty', (v) => {
    expect(ui.getTopGenresFromDistribution(v, 3)).toEqual([]);
  });
});

describe('findUserDiscovery matches case-insensitively', () => {
  const all = [{ UserId: 'ABC', Recommendations: [] }, { userId: 'def', Recommendations: [] }];

  test.each([[all, 'abc', 0], [all, 'DEF', 1], [all, 'zzz', null]])('lookup', (data: any, id: string, idx: number | null) => {
    expect(ui.findUserDiscovery(data, id)).toBe(idx === null ? null : data[idx]);
  });

  test.each([[[], 'a'], [null, 'a'], [all, ''], [all, null]])('empty input yields null', (data: any, id: string) => {
    expect(ui.findUserDiscovery(data, id)).toBeNull();
  });
});

describe('renderRecommendationCard clamps and escapes', () => {
  test('score above one clamps to one hundred', () => {
    const html = ui.renderRecommendationCard({ Name: 'M', Score: 1.5, Genres: ['A', 'B', 'C', 'D'] }, 1);
    expect(html).toContain('100%');
    expect(html).toContain('recs-score-high');
    expect(html.match(/recs-tag">A/g)).toHaveLength(1);
  });

  test('negative scores bottom out', () => {
    expect(ui.renderRecommendationCard({ Name: 'M', Score: -2 }, 2)).toContain('0%');
  });

  test('reason placeholders substitute without cascading', () => {
    const html = ui.renderRecommendationCard(
      { Name: 'M', Score: 0.5, ReasonKey: 'rk', Reason: 'x {0} y {1}', RelatedItemName: 'A | {1}' },
      1,
    );
    expect(html).toContain('x A y {1}');
  });

  test('titles escape markup', () => {
    expect(ui.renderRecommendationCard({ Name: '<img>', Score: 0 }, 1)).toContain('&lt;img&gt;');
  });
});

describe('renderDiscoveryCard hardens untrusted fields', () => {
  test('evil poster paths render no image', () => {
    for (const poster of ['javascript:alert(1)', '/x.exe', '"><img src=x onerror=y>', '/a b.jpg']) {
      const html = ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, PosterPath: poster, MediaType: 'movie', TmdbId: 1 }, 0);
      expect(html).not.toContain('<img');
      expect(html).toContain('discovery-card-poster-empty');
    }
  });

  test('valid posters resolve to tmdb urls', () => {
    expect(ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, PosterPath: '/a_b-c.jpg', MediaType: 'movie', TmdbId: 1 }, 0))
      .toContain('https://image.tmdb.org/t/p/w185/a_b-c.jpg');
  });

  test('requested items show a disabled done button', () => {
    const html = ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, AlreadyRequested: true, MediaType: 'movie', TmdbId: 7 }, 0);
    expect(html).toContain('disabled');
    expect(html).not.toContain('data-tmdb-id="7"');
  });

  test('unparseable tmdb ids zero out', () => {
    expect(ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, MediaType: 'movie', TmdbId: 'abc' }, 0))
      .toContain('data-tmdb-id="0"');
  });

  test('dollar patterns in related info stay literal', () => {
    const html = ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, Reason: 'a {0} b', RelatedInfo: '$&$\'$\`' }, 0);
    expect(html).toContain('$&amp;$&#39;$`');
  });
});

describe('request response handling', () => {
  function buttonInGrid() {
    document.body.innerHTML =
      '<div class="discovery-grid"><div class="discovery-card">' +
      '<button class="discovery-request-btn">Request</button></div></div>' +
      '<span id="discoveryCount">3</span>';
    return document.querySelector('.discovery-request-btn') as HTMLButtonElement;
  }

  test('success marks done without touching the counter yet', () => {
    const btn = buttonInGrid();
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    expect(btn.disabled).toBe(true);
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('3');
  });

  test('failed success flag degrades to the error path', () => {
    const btn = buttonInGrid();
    ui.handleDiscoveryRequestResponse({ Success: false }, btn, 5, 'movie');
    expect(btn.disabled).toBe(false);
    expect(btn.textContent).toMatch(/fail/i);
  });

  test('repeated errors reset the timer instead of stacking', async () => {
    const btn = buttonInGrid();
    ui.handleDiscoveryRequestError(btn);
    // Let the first deadline almost arrive, then fire a second error. The reset
    // must restart the countdown, so the original deadline no longer applies.
    await vi.advanceTimersByTimeAsync(2500);
    ui.handleDiscoveryRequestError(btn);
    expect(btn.disabled).toBe(false);
    expect(btn.innerHTML).toMatch(/fail/i);
    // Past the first error's original 3000ms deadline, the label still shows the
    // error because the second error reset the timer, proving no early wipe.
    await vi.advanceTimersByTimeAsync(1000);
    expect(btn.innerHTML).toMatch(/fail/i);
    // Only after the second error's full window does it revert to Request.
    await vi.advanceTimersByTimeAsync(2000);
    expect(btn.innerHTML).toMatch(/request/i);
  });

  test('submit posts the expected payload shape', async () => {
    const calls = ajaxRouter({ 'Discovery/Request': { Success: true } });
    const btn = buttonInGrid();
    ui.submitDiscoveryRequest(42, 'tv', 'user9', btn);
    await flush();
    expect(calls[0].body).toMatchObject({ TmdbId: 42, MediaType: 'tv', SeerrUserId: 'user9' });
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
  });

  test('submit without a seerr user omits the field', async () => {
    const calls = ajaxRouter({ 'Discovery/Request': { Success: true } });
    const btn = buttonInGrid();
    ui.submitDiscoveryRequest(42, 'movie', null, btn);
    await flush();
    expect(calls[0].body).not.toHaveProperty('SeerrUserId');
  });

  test('marking with empty caches is a silent no-op', () => {
    expect(() => ui.markDiscoveryItemRequested(1, 'movie')).not.toThrow();
  });
});

describe('seerr popup fails closed', () => {
  test('profile lookup failure restores the button without submitting', async () => {
    const calls = ajaxRouter({}, ['Discovery/Services']);
    document.body.innerHTML = '<button id="b">Request</button>';
    const btn = document.getElementById('b') as HTMLButtonElement;
    ui.showSeerrUserPopup(9, 'movie', btn);
    await flush();
    expect(btn.disabled).toBe(false);
    expect(calls.some((c) => c.url.includes('Discovery/Request'))).toBe(false);
    expect(document.getElementById('seerrUserPopup')).toBeNull();
  });
});

describe('ensemble diagnostics math', () => {
  test.each([[2.5, '100%'], [-1, '0%'], [0.333, '33%']])('share %s', (v, text) => {
    expect(ui.ensembleShareText(v)).toBe(text);
  });

  test('blend splits into three named shares', () => {
    document.body.innerHTML = '<div id="host"></div>';
    const host = document.getElementById('host')!;
    ui.renderEnsembleDiagnostics(host, {
      Available: true, Alpha: 0.6, NeuralBeta: 0.5, NeuralEnabled: true,
      IsPerUser: false, Trend: 'Improving', TrainingExampleCount: 42,
    });
    expect(host.textContent).toContain('40%');
    expect(host.textContent).toContain('30%');
    expect(host.textContent).toContain('42');
  });

  test('unavailable data clears the footnote', () => {
    document.body.innerHTML = '<div id="host">old</div>';
    const host = document.getElementById('host')!;
    ui.renderEnsembleDiagnostics(host, { Available: false });
    expect(host.innerHTML).toBe('');
  });

  test('diagnostics load caches per user', async () => {
    const calls = ajaxRouter({ 'Diagnostics/Ensemble': { Available: false } });
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('u1');
    await flush();
    ui.loadEnsembleDiagnostics('u1');
    await flush();
    expect(calls.filter((c) => c.url.includes('Ensemble')).length).toBe(1);
  });
});

describe('watch profile and activity rendering', () => {
  test('empty profile shows the empty state', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactWatchProfile(c, null);
    expect(c.textContent).toMatch(/no watch profile/i);
  });

  test('profile totals and top genres render', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactWatchProfile(c, {
      WatchedMovieCount: 2, WatchedEpisodeCount: 3, WatchedSeriesCount: 1,
      FavoriteCount: 0, GenreDistribution: { Action: 4, Drama: 2 },
    });
    expect(c.textContent).toContain('5 Watched');
    expect(c.textContent).toContain('Action');
  });

  test('activity caps rows and reports the remainder', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    const items = Array.from({ length: 60 }, (_, i) => ({
      ItemName: 'E' + i, ItemType: 'Episode', TotalPlayCount: 1,
      MostRecentWatch: '2026-01-01T00:00:00', AverageCompletionPercent: 100,
    }));
    ui.renderCompactActivityTable(c, items);
    expect(c.querySelectorAll('tbody tr').length).toBeLessThanOrEqual(50);
    expect(c.textContent).toMatch(/more/);
  });

  test('completion bands classify correctly', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactActivityTable(c, [
      { ItemName: 'A', AverageCompletionPercent: 100 },
      { ItemName: 'B', AverageCompletionPercent: 50 },
      { ItemName: 'C', AverageCompletionPercent: 0 },
    ]);
    expect(c.querySelector('.activity-status-done')).not.toBeNull();
    expect(c.querySelector('.activity-status-progress')).not.toBeNull();
    expect(c.querySelector('.activity-status-new')).not.toBeNull();
  });
});

describe('toggleCollapsible', () => {
  test('toggles open state and aria', () => {
    document.body.innerHTML =
      '<div id="body"></div><button id="tgl" aria-expanded="false"><span class="recs-collapsible-arrow"></span></button>';
    ui.toggleCollapsible('body', 'tgl');
    expect(document.getElementById('body')!.classList.contains('open')).toBe(true);
    expect(document.getElementById('tgl')!.getAttribute('aria-expanded')).toBe('true');
    ui.toggleCollapsible('body', 'tgl');
    expect(document.getElementById('body')!.classList.contains('open')).toBe(false);
  });

  test('missing body does not throw', () => {
    expect(() => ui.toggleCollapsible('nope', 'tgl')).not.toThrow();
  });
});
