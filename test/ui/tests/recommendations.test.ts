import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { flush } from '../src/apiStub.js';
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

  test('entries without any id fall through to a later match', () => {
    expect(ui.findUserDiscovery([{}, { userId: 'abc' }], 'ABC')).toMatchObject({ userId: 'abc' });
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
    expect(calls.filter((c) => c.url.includes('Ensemble'))).toHaveLength(1);
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

describe('renderRecommendationCard optional fields', () => {
  test('item type and year tags render with a clamped score', () => {
    const html = ui.renderRecommendationCard({ Score: 0.3, ItemType: 'Movie', Year: 2021 }, 1);
    expect(html).toContain('Unknown');
    expect(html).toContain('recs-tag-type');
    expect(html).toContain('2021');
    expect(html).toContain('30%');
  });

  test('reason key without a reason falls back to the key text', () => {
    const html = ui.renderRecommendationCard({ Name: 'N', Score: 0.1, ReasonKey: 'rk' }, 2);
    expect(html).toContain('rk');
  });

  test('out-of-range placeholders survive substitution', () => {
    const html = ui.renderRecommendationCard(
      { Name: 'N', Score: 0.1, ReasonKey: 'rk', Reason: 'x {9} y', RelatedItemName: 'A' },
      3,
    );
    expect(html).toContain('x {9} y');
  });

  test('plain reason renders without a reason key', () => {
    expect(ui.renderRecommendationCard({ Name: 'N', Score: 0.4, Reason: 'because' }, 4)).toContain('because');
  });

  test('missing reason falls back to the generic text', () => {
    expect(ui.renderRecommendationCard({ Name: 'N', Score: 0.4 }, 5)).toContain('Based on your viewing history');
  });
});

describe('renderDiscoveryCard optional fields', () => {
  test('tv type, year and rating tags render with genres capped at three', () => {
    const html = ui.renderDiscoveryCard(
      { Title: 'T', MediaType: 'tv', TmdbId: 1, Year: 2022, TmdbRating: 7.28, Genres: ['G1', 'G2', 'G3', 'G4'] },
      0,
    );
    expect(html).toContain('Series');
    expect(html).toContain('2022');
    expect(html).toContain('7.3');
    expect(html.match(/recs-tag">G/g)).toHaveLength(3);
  });

  test('missing title falls back to unknown with a zero score', () => {
    const html = ui.renderDiscoveryCard({ MediaType: 'movie', TmdbId: 1 }, 0);
    expect(html).toContain('Unknown');
    expect(html).toContain('0%');
  });

  test('plain reason renders without placeholders', () => {
    const html = ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, Reason: 'because', MediaType: 'movie', TmdbId: 1 }, 0);
    expect(html).toContain('because');
  });

  test('reason key without a reason falls back to the key text', () => {
    const html = ui.renderDiscoveryCard({ Title: 'T', Score: 0.5, ReasonKey: 'rk', MediaType: 'movie', TmdbId: 1 }, 0);
    expect(html).toContain('rk');
  });

  test('reason key substitutes related info placeholders', () => {
    const html = ui.renderDiscoveryCard(
      { Title: 'T', Score: 0.5, ReasonKey: 'rk', Reason: 'because {0}', RelatedInfo: 'Z', MediaType: 'movie', TmdbId: 1 },
      0,
    );
    expect(html).toContain('because Z');
  });
});

describe('renderDiscoveryCards list variants', () => {
  test('missing counter still renders the grid', () => {
    document.body.innerHTML = '<div id="g"></div>';
    const g = document.getElementById('g')!;
    ui.renderDiscoveryCards(g, null, { Recommendations: [{ Title: 'T', MediaType: 'movie', TmdbId: 1 }] });
    expect(g.querySelectorAll('.discovery-card')).toHaveLength(1);
  });

  test('empty list without a counter still renders the empty state', () => {
    document.body.innerHTML = '<div id="g"></div>';
    const g = document.getElementById('g')!;
    ui.renderDiscoveryCards(g, null, null);
    expect(g.textContent).toMatch(/no suggestions/i);
  });

  test('generated-at footer renders for valid dates and is skipped for garbage', () => {
    document.body.innerHTML = '<div id="g"></div>';
    const g = document.getElementById('g')!;
    const base = { Recommendations: [{ Title: 'T', MediaType: 'movie', TmdbId: 1 }] };
    ui.renderDiscoveryCards(g, null, { ...base, GeneratedAt: '2026-02-03T04:05:06Z' });
    expect(g.textContent).toContain('Last updated');
    ui.renderDiscoveryCards(g, null, { ...base, GeneratedAt: 'not-a-date' });
    expect(g.textContent).not.toContain('Last updated');
  });
});

describe('getTopGenresFromDistribution edge cases', () => {
  test('inherited keys are skipped and zero counts keep their own entries', () => {
    const dist = Object.assign(Object.create({ inherited: 9 }), { A: 0, B: 2 });
    expect(ui.getTopGenresFromDistribution(dist, 5)).toEqual(['B', 'A']);
  });
});

describe('renderCompactWatchProfile variants', () => {
  test('missing counts render zeros and empty genres omit the row', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactWatchProfile(c, { GenreDistribution: {} });
    expect(c.textContent).toContain('0 Watched');
    expect(c.textContent).not.toContain('Top Genres');
  });
});

describe('renderCompactActivityTable variants', () => {
  test('empty list shows the empty state', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactActivityTable(c, []);
    expect(c.textContent).toMatch(/no watch activity/i);
  });

  test('series names, missing titles and invalid dates degrade gracefully', () => {
    document.body.innerHTML = '<div id="c"></div>';
    const c = document.getElementById('c')!;
    ui.renderCompactActivityTable(c, [
      { SeriesName: 'Show', EpisodeLabel: 'S1E1', TotalPlayCount: 2, AverageCompletionPercent: 100, MostRecentWatch: 'not-a-date' },
      { SeriesName: 'Show', ItemName: 'Ep2', TotalPlayCount: 1, AverageCompletionPercent: 10, MostRecentWatch: '2026-01-01T00:00:00' },
      { SeriesName: 'Show', TotalPlayCount: 0, AverageCompletionPercent: 50 },
      { TotalPlayCount: 3, AverageCompletionPercent: 0 },
    ]);
    expect(c.querySelectorAll('tbody tr')).toHaveLength(4);
    expect(c.textContent).toContain('Show \u2013 S1E1');
    expect(c.textContent).toContain('Show \u2013 Ep2');
    expect(c.textContent).toContain('\u2014');
  });
});

describe('ensemble diagnostics rendering variants', () => {
  function host() {
    document.body.innerHTML = '<div id="h"></div>';
    return document.getElementById('h')!;
  }

  test('per-user model names the user and a frozen gate wins over the trend', () => {
    const h = host();
    ui.renderEnsembleDiagnostics(h, {
      Available: true, Alpha: 0.8, NeuralBeta: 0.5, NeuralEnabled: true,
      IsPerUser: true, UserName: 'Alice', Trend: 'Stable', TrainingExampleCount: 7, QualityGateFrozen: true,
    });
    expect(h.textContent).toContain('Alice');
    expect(h.textContent).toContain('frozen');
    expect(h.textContent).toContain('20%');
    expect(h.textContent).toContain('40%');
  });

  test('per-user model without a name uses the neutral term', () => {
    const h = host();
    ui.renderEnsembleDiagnostics(h, {
      Available: true, Alpha: 0.5, NeuralBeta: 0, NeuralEnabled: false,
      IsPerUser: true, Trend: 'Degrading', TrainingExampleCount: 0,
    });
    expect(h.textContent).toContain('this user');
    expect(h.textContent).toContain('Degrading');
  });

  test('unknown trend passes through and missing factors read as off', () => {
    const h = host();
    ui.renderEnsembleDiagnostics(h, { Available: true, Trend: 'Weird', TrainingExampleCount: 0 });
    expect(h.textContent).toContain('Weird');
    expect(h.textContent).toContain('100%');
    expect(h.textContent).toContain('off');
  });
});

describe('ensemble diagnostics loading variants', () => {
  test('userless diagnostics omit the query and cache under the empty key', async () => {
    const calls = ajaxRouter({ 'Diagnostics/Ensemble': { Available: false } });
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics(null);
    await flush();
    ui.loadEnsembleDiagnostics(null);
    await flush();
    expect(calls).toHaveLength(1);
    expect(calls[0].url).not.toContain('userId=');
  });

  test('cached diagnostics repaint into a replaced host and tolerate a missing host', async () => {
    ajaxRouter({ 'Diagnostics/Ensemble': { Available: true, Alpha: 0.25 } });
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('u1');
    await flush();
    expect(document.getElementById('recsEnsembleFootnote')!.textContent).toContain('75%');
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('u1');
    expect(document.getElementById('recsEnsembleFootnote')!.textContent).toContain('75%');
    document.body.innerHTML = '';
    expect(() => ui.loadEnsembleDiagnostics('u1')).not.toThrow();
  });

  // Deferred responses are what makes these two tests real: resolving in request order
  // would paint the newest answer last anyway, so the guard could be deleted unnoticed.
  function deferredEnsemble() {
    const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => '',
      accessToken: () => 't',
      ajax: (opts: any) => new Promise((resolve, reject) => {
        const url = String(opts.url);
        pending.set(url.slice(url.indexOf('userId=') + 'userId='.length), {
          resolve: resolve as (v: unknown) => void,
          reject,
        });
      }),
    };
    return pending;
  }

  test('stale diagnostics responses cannot overwrite the latest user footnote', async () => {
    const pending = deferredEnsemble();
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('u1');
    ui.loadEnsembleDiagnostics('u2');
    // Newest answer lands first, the superseded one afterwards - the worst case for the guard.
    pending.get('u2')!.resolve({ Available: true, Alpha: 0.7 });
    await flush();
    pending.get('u1')!.resolve({ Available: true, Alpha: 0.2 });
    await flush();
    const text = document.getElementById('recsEnsembleFootnote')!.textContent!;
    // u2 renders heuristic 30% / ML 70%; the stale u1 shares (80% / 20%) must not appear.
    expect(text).toContain('30%');
    expect(text).toContain('70%');
    expect(text).not.toContain('80%');
    expect(text).not.toContain('20%');
  });

  test('a stale failure is ignored while the latest failure clears the footnote', async () => {
    const pending = deferredEnsemble();
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('u1');
    ui.loadEnsembleDiagnostics('u2');
    pending.get('u2')!.resolve({ Available: true, Alpha: 0.7 });
    await flush();
    // The superseded u1 request fails late: it must leave the painted u2 footnote alone.
    pending.get('u1')!.reject(new Error('down'));
    await flush();
    expect(document.getElementById('recsEnsembleFootnote')!.textContent).toContain('30%');
    // Only the newest request may clear the footnote when it fails.
    ui.loadEnsembleDiagnostics('u3');
    pending.get('u3')!.reject(new Error('down'));
    await flush();
    expect(document.getElementById('recsEnsembleFootnote')!.innerHTML).toBe('');
  });

  test('a failure without a host is a silent no-op', async () => {
    ajaxRouter({}, ['Diagnostics/Ensemble']);
    document.body.innerHTML = '';
    expect(() => ui.loadEnsembleDiagnostics('u1')).not.toThrow();
    await flush();
  });

  test('a success arriving after the footnote is gone is cached for the next render', async () => {
    ajaxRouter({ 'Diagnostics/Ensemble': { Available: true, Alpha: 0.5 } });
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('gone');
    document.body.innerHTML = '';
    await flush();
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    ui.loadEnsembleDiagnostics('gone');
    expect(document.getElementById('recsEnsembleFootnote')!.textContent).toContain('50%');
  });
});

describe('discovery card removal animation', () => {
  function cardInGrid(count?: string) {
    document.body.innerHTML =
      '<div class="discovery-grid"><div class="discovery-card"><button class="discovery-request-btn">Request</button></div></div>'
      + (count === undefined ? '' : '<span id="discoveryCount">' + count + '</span>');
    return document.querySelector('.discovery-request-btn') as HTMLButtonElement;
  }

  test('success removes the card, decrements the counter and shows the empty state', async () => {
    const btn = cardInGrid('1');
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    await vi.advanceTimersByTimeAsync(5000);
    expect(document.querySelector('.discovery-card')!.classList.contains('discovery-card-removing')).toBe(true);
    await vi.advanceTimersByTimeAsync(300);
    expect(document.querySelectorAll('.discovery-card')).toHaveLength(0);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('0');
    expect(document.querySelector('.discovery-grid')!.textContent).toMatch(/no suggestions/i);
  });

  test('a card removed before the fade never touches the counter', async () => {
    const btn = cardInGrid('1');
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    document.querySelector('.discovery-card')!.remove();
    await vi.advanceTimersByTimeAsync(5300);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('1');
  });

  test('a card detached during the fade keeps the counter intact', async () => {
    const btn = cardInGrid('1');
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    await vi.advanceTimersByTimeAsync(5000);
    document.querySelector('.discovery-card')!.remove();
    await vi.advanceTimersByTimeAsync(300);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('1');
  });

  test('remaining cards keep the grid and non-numeric counters clamp at zero', async () => {
    document.body.innerHTML =
      '<div class="discovery-grid">' +
      '<div class="discovery-card"><button class="discovery-request-btn">R</button></div>' +
      '<div class="discovery-card"><button>b</button></div></div>' +
      '<span id="discoveryCount">x</span>';
    const btn = document.querySelector('.discovery-request-btn') as HTMLButtonElement;
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    await vi.advanceTimersByTimeAsync(5300);
    expect(document.querySelectorAll('.discovery-card')).toHaveLength(1);
    expect(document.getElementById('discoveryCount')!.textContent).toBe('0');
  });

  test('a card outside any grid removes without counter updates', async () => {
    document.body.innerHTML = '<div class="discovery-card"><button class="discovery-request-btn">R</button></div>';
    const btn = document.querySelector('.discovery-request-btn') as HTMLButtonElement;
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    await vi.advanceTimersByTimeAsync(5300);
    expect(document.querySelectorAll('.discovery-request-btn')).toHaveLength(0);
  });

  test('success without a card only marks the button', async () => {
    document.body.innerHTML = '<button id="solo">R</button>';
    const btn = document.getElementById('solo') as HTMLButtonElement;
    ui.handleDiscoveryRequestResponse({ Success: true }, btn, 5, 'movie');
    expect(btn.classList.contains('discovery-request-done')).toBe(true);
    await vi.advanceTimersByTimeAsync(5300);
  });

  test('error timer on a detached button leaves the label alone', async () => {
    const btn = document.createElement('button');
    ui.handleDiscoveryRequestError(btn);
    await vi.advanceTimersByTimeAsync(3000);
    expect(btn.innerHTML).toMatch(/fail/i);
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

  test('missing toggle and arrow still flip the body', () => {
    document.body.innerHTML = '<div id="body"></div>';
    ui.toggleCollapsible('body', 'tgl');
    expect(document.getElementById('body')!.classList.contains('open')).toBe(true);
    ui.toggleCollapsible('body', 'tgl');
    expect(document.getElementById('body')!.classList.contains('open')).toBe(false);
  });
});
