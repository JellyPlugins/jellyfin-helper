import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshDiscoveryHelpers, loadDiscoveryHelpers, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

// The unwrapped bundle reads ApiClient off the shared globalThis. An ajax
// router captures every call and resolves/rejects per URL fragment so the
// render + fetch paths run against realistic responses.
function ajaxRouter(
  routes: Record<string, unknown> = {},
  opts: { userId?: string; failures?: Record<string, { status?: number } | Error> } = {},
) {
  const calls: { url: string; type?: string; body?: unknown }[] = [];
  const failures = opts.failures || {};
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => opts.userId ?? 'u1',
    accessToken: () => 't',
    ajax: (o: any) => {
      let body: unknown;
      try {
        body = typeof o.data === 'string' ? JSON.parse(o.data) : o.data;
      } catch {
        body = o.data;
      }
      calls.push({ url: String(o.url), type: o.type, body });
      for (const [frag, err] of Object.entries(failures)) {
        if (String(o.url).includes(frag)) return Promise.reject(err);
      }
      for (const [frag, value] of Object.entries(routes)) {
        if (String(o.url).includes(frag)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

async function flush() {
  for (let i = 0; i < 12; i++) await Promise.resolve();
}

// A minimal but realistic Recommendation DTO as the backend emits it.
function rec(over: Record<string, unknown> = {}) {
  return {
    Title: 'The Matrix',
    Score: 0.9,
    PosterPath: '/poster.jpg',
    MediaType: 'movie',
    TmdbId: 603,
    Year: 1999,
    TmdbRating: 8.7,
    Genres: ['Action', 'Sci-Fi'],
    ReasonKey: 'reasonGenre',
    RelatedInfo: 'Action',
    Overview: 'A hacker learns the truth.',
    AlreadyRequested: false,
    ...over,
  };
}

beforeEach(() => {
  document.body.innerHTML = '';
  document.head.innerHTML = '';
  ajaxRouter();
});

afterEach(() => {
  vi.clearAllTimers();
});

describe('discovery pure helpers', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = loadDiscoveryHelpers();
  });

  test('safeHttpUrl passes http(s) and neutralizes hostile schemes', () => {
    expect(ui.safeHttpUrl('https://seerr.example.com')).toBe('https://seerr.example.com');
    expect(ui.safeHttpUrl('  http://a.b  ')).toBe('http://a.b');
    expect(ui.safeHttpUrl('javascript:alert(1)')).toBe('');
    expect(ui.safeHttpUrl('data:text/html,<script>')).toBe('');
    expect(ui.safeHttpUrl('ftp://x')).toBe('');
    expect(ui.safeHttpUrl(null)).toBe('');
    expect(ui.safeHttpUrl(42)).toBe('');
  });

  test('esc escapes every HTML metacharacter and coerces non-strings', () => {
    expect(ui.esc('<script>alert("x")&\'</script>')).toBe(
      '&lt;script&gt;alert(&quot;x&quot;)&amp;&#39;&lt;/script&gt;',
    );
    expect(ui.esc(1999)).toBe('1999');
    expect(ui.esc('')).toBe('');
    expect(ui.esc(0)).toBe('');
  });

  test('getDiscoveryScoreClass buckets by percentage boundaries', () => {
    expect(ui.getDiscoveryScoreClass(80)).toBe('jfh-discovery-score-high');
    expect(ui.getDiscoveryScoreClass(79)).toBe('jfh-discovery-score-mid');
    expect(ui.getDiscoveryScoreClass(50)).toBe('jfh-discovery-score-mid');
    expect(ui.getDiscoveryScoreClass(49)).toBe('jfh-discovery-score-low');
    expect(ui.getDiscoveryScoreClass(0)).toBe('jfh-discovery-score-low');
  });

  test('insertSlashBreaks inserts a zero-width space after each slash', () => {
    const zwsp = String.fromCodePoint(0x200b);
    expect(ui.insertSlashBreaks('actors/directors')).toBe('actors/' + zwsp + 'directors');
    expect(ui.insertSlashBreaks('a/b/c')).toBe('a/' + zwsp + 'b/' + zwsp + 'c');
    expect(ui.insertSlashBreaks('nobreak')).toBe('nobreak');
  });

  test('t returns the fallback when no strings are loaded', () => {
    expect(ui.t('discoveryTitle', 'Seerr Discovery')).toBe('Seerr Discovery');
    expect(ui.t('onlyKey')).toBe('onlyKey');
  });

  test('discoveryGeneratedAt parses timestamps, 0 on missing or bad', () => {
    const ms = ui.discoveryGeneratedAt({ GeneratedAt: '2024-01-01T00:00:00Z' });
    expect(ms).toBe(Date.parse('2024-01-01T00:00:00Z'));
    expect(ui.discoveryGeneratedAt({})).toBe(0);
    expect(ui.discoveryGeneratedAt({ GeneratedAt: 'not-a-date' })).toBe(0);
    expect(ui.discoveryGeneratedAt(null)).toBe(0);
  });

  test('discoveryVisibleKey fingerprints the visible id/type set', () => {
    expect(
      ui.discoveryVisibleKey({ Recommendations: [{ TmdbId: 1, MediaType: 'movie' }, { TmdbId: 2, MediaType: 'tv' }] }),
    ).toBe('1:movie|2:tv');
    expect(ui.discoveryVisibleKey({})).toBe('');
    expect(ui.discoveryVisibleKey({ Recommendations: 'nope' })).toBe('');
  });

  test('isEmptyTraktResult reflects presence of recommendations', () => {
    expect(ui.isEmptyTraktResult(null)).toBe(true);
    expect(ui.isEmptyTraktResult({ Recommendations: [] })).toBe(true);
    expect(ui.isEmptyTraktResult({ Recommendations: [rec()] })).toBe(false);
  });

  test('spinnerHtml renders the accessible loading container', () => {
    const html = ui.spinnerHtml();
    expect(html).toContain('jfh-discovery-spinner');
    expect(html).toContain('aria-busy="true"');
    expect(html).toContain('Loading recommendations');
  });

  test('isOnHomePage accepts home variants only', () => {
    const set = (h: string) => { window.location.hash = h; };
    set('');
    expect(ui.isOnHomePage()).toBe(true);
    set('#/home');
    expect(ui.isOnHomePage()).toBe(true);
    set('#/home?tab=2');
    expect(ui.isOnHomePage()).toBe(true);
    set('#/configurationpage');
    expect(ui.isOnHomePage()).toBe(false);
  });
});

describe('discovery error mapping', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = loadDiscoveryHelpers();
  });

  test('extractErrorMessage reads responseJSON then responseText', () => {
    expect(ui.extractErrorMessage({ responseJSON: { Message: 'boom' } })).toBe('boom');
    expect(ui.extractErrorMessage({ responseText: '{"Message":"parsed"}' })).toBe('parsed');
    expect(ui.extractErrorMessage({ responseText: 'not json' })).toBe('');
    expect(ui.extractErrorMessage(null)).toBe('');
  });

  test('getUserFriendlyErrorMessage classifies by status/keyword', () => {
    expect(ui.getUserFriendlyErrorMessage('HTTP 403 denied')).toContain('No permission');
    expect(ui.getUserFriendlyErrorMessage('not linked')).toContain('No permission');
    expect(ui.getUserFriendlyErrorMessage('HTTP 503 upstream')).toContain('unreachable');
    expect(ui.getUserFriendlyErrorMessage('request timed out')).toContain('Timed out');
    expect(ui.getUserFriendlyErrorMessage('weird')).toContain('Request failed');
  });
});

describe('formatReason / formatRawReasonFallback', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = loadDiscoveryHelpers();
  });

  test('template keys substitute the {0} anchor with relatedInfo', () => {
    expect(ui.formatReason('reasonGenre', null, 'Action')).toBe('Because you enjoy Action');
    expect(ui.formatReason('reasonPersonNamed', null, 'Keanu Reeves')).toBe('Featuring Keanu Reeves');
  });

  test('phrase keys render localized text with no anchor', () => {
    expect(ui.formatReason('reasonTrending')).toBe('Trending - new and highly rated');
    expect(ui.formatReason('reasonDefault')).toBe('Recommended for you');
  });

  test('empty when neither key nor reason present', () => {
    expect(ui.formatReason('', '')).toBe('');
  });

  test('fallback re-dispatches a raw "reasonKey: anchor" string', () => {
    expect(ui.formatRawReasonFallback('reasonGenre: Horror')).toBe('Because you enjoy Horror');
    // A bare reason* token with no anchor is intentionally hidden.
    expect(ui.formatRawReasonFallback('reasonUnknownToken')).toBe('');
    // A plain non-reason string passes through verbatim.
    expect(ui.formatRawReasonFallback('just text')).toBe('just text');
  });
});

describe('discovery result cache + generation counter', () => {
  // Fresh scope per test: these mutate module-level caches and the generation.
  test('set/get round-trips for the same user', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'alice' });
    expect(ui.getCachedDiscoveryResult()).toBeNull();
    const payload = { Recommendations: [rec()] };
    ui.setCachedDiscoveryResult(payload);
    expect(ui.getCachedDiscoveryResult()).toBe(payload);
  });

  test('cache never leaks across an account switch', () => {
    const ui = freshDiscoveryHelpers();
    let who = 'alice';
    ajaxRouter({}, { userId: 'ignored' });
    (globalThis as any).ApiClient.getCurrentUserId = () => who;
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    expect(ui.getCachedDiscoveryResult()).not.toBeNull();
    who = 'bob';
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });

  test('invalidate clears the cache', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'alice' });
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    ui.invalidateDiscoveryResult();
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });
});

describe('trakt cache freshness', () => {
  test('makeTraktCacheEntry stamps user and time; fresh for same user', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'alice' });
    const entry = ui.makeTraktCacheEntry({ Recommendations: [rec()] }, 'alice');
    expect(entry.userId).toBe('alice');
    expect(typeof entry.storedAt).toBe('number');
    expect(ui.traktCacheFresh(entry)).toBe(true);
  });

  test('wrong-user entry is never fresh', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'alice' });
    const entry = ui.makeTraktCacheEntry({ Recommendations: [rec()] }, 'bob');
    expect(ui.traktCacheFresh(entry)).toBe(false);
    expect(ui.traktCacheFresh(null)).toBe(false);
  });

  test('empty entry expires after the empty-cache window', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'alice' });
    const entry = ui.makeTraktCacheEntry({ Recommendations: [] }, 'alice');
    expect(ui.traktCacheFresh(entry)).toBe(true);
    // TRAKT_EMPTY_CACHE_MS is 15000; advance just past it.
    vi.advanceTimersByTime(15001);
    expect(ui.traktCacheFresh(entry)).toBe(false);
  });
});

describe('toast lifecycle', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = loadDiscoveryHelpers();
  });

  test('showToast appends a role=alert node carrying the message', () => {
    ui.showToast('something failed');
    const toast = document.querySelector('.jfh-discovery-toast') as HTMLElement;
    expect(toast).not.toBeNull();
    expect(toast.textContent).toBe('something failed');
    expect(toast.getAttribute('role')).toBe('alert');
    // Reflow-triggered visible class is applied synchronously.
    expect(toast.classList.contains('jfh-discovery-toast-visible')).toBe(true);
  });

  test('empty message is a no-op', () => {
    ui.showToast('');
    expect(document.querySelector('.jfh-discovery-toast')).toBeNull();
  });

  test('auto-dismiss removes the node after duration + fade', () => {
    ui.showToast('bye', 1000);
    const toast = document.querySelector('.jfh-discovery-toast') as HTMLElement;
    expect(toast).not.toBeNull();
    vi.advanceTimersByTime(1000);
    expect(toast.classList.contains('jfh-discovery-toast-hidden')).toBe(true);
    vi.advanceTimersByTime(300);
    expect(document.querySelector('.jfh-discovery-toast')).toBeNull();
  });

  test('dismissToast tolerates a detached node', () => {
    const orphan = document.createElement('div');
    expect(() => ui.dismissToast(orphan)).not.toThrow();
    expect(() => ui.dismissToast(null)).not.toThrow();
  });
});

describe('renderCards', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
  });

  test('empty payload renders the no-results message', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [] });
    expect(el.querySelector('.jfh-discovery-msg')).not.toBeNull();
    expect(el.textContent).toContain('No suggestions available');
    expect(el.querySelector('.jfh-discovery-card')).toBeNull();
  });

  test('renders one card per recommendation with escaped title and score class', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ Title: 'Movie A' }), rec({ Title: 'Movie B', TmdbId: 2 })] });
    const cards = el.querySelectorAll('.jfh-discovery-card');
    expect(cards).toHaveLength(2);
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('Movie A');
    // Score 0.9 -> 90% -> high bucket.
    expect(el.querySelector('.jfh-discovery-score-high')).not.toBeNull();
    // Request button carries the tmdb id as a data attribute.
    const reqBtn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLElement;
    expect(reqBtn.dataset.tmdb).toBe('603');
    expect(reqBtn.dataset.type).toBe('movie');
  });

  test('escapes a hostile title so no script element is created', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ Title: '<img src=x onerror=alert(1)>' })] });
    expect(el.querySelector('img[onerror]')).toBeNull();
    // The literal angle brackets survive as text, proving escaping.
    expect(el.innerHTML).toContain('&lt;img src=x onerror=alert(1)&gt;');
  });

  test('AlreadyRequested card is disabled and omits the dismiss button', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ AlreadyRequested: true })] });
    const btn = el.querySelector('.jfh-discovery-btn') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(btn.classList.contains('jfh-discovery-btn-done')).toBe(true);
    expect(el.querySelector('.jfh-discovery-btn-dismiss')).toBeNull();
  });

  test('a missing poster falls back to the no-poster placeholder', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ PosterPath: null })] });
    expect(el.querySelector('.jfh-discovery-no-poster')).not.toBeNull();
    expect(el.querySelector('.jfh-discovery-flip-front img')).toBeNull();
  });

  test('poster click toggles the flipped class', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec()] });
    const poster = el.querySelector('.jfh-discovery-card-poster') as HTMLElement;
    (el.querySelector('.jfh-discovery-flip-inner') as HTMLElement).click();
    expect(poster.classList.contains('flipped')).toBe(true);
  });

  test('a hostile reason is escaped before entering the DOM', () => {
    const el = document.createElement('div');
    ui.renderCards(el, {
      Recommendations: [rec({ ReasonKey: '', RelatedInfo: '', Reason: '<b>xss</b>' })],
    });
    const reason = el.querySelector('.jfh-discovery-reason');
    expect(reason).not.toBeNull();
    expect(reason!.querySelector('b')).toBeNull();
    // Escaped before DOM insertion (insertSlashBreaks adds a zero-width space
    // after the slash, so match the escaped head rather than the exact tail).
    expect(reason!.innerHTML).toContain('&lt;b&gt;xss');
    expect(reason!.textContent).toContain('<b>xss');
  });
});

describe('tabButton + renderShell + renderTraktNotLinked', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
  });

  test('tabButton escapes the label and marks data-tab', () => {
    const html = ui.tabButton('own', '<b>For you</b>');
    expect(html).toContain('data-tab="own"');
    expect(html).toContain('&lt;b&gt;For you&lt;/b&gt;');
    expect(html).toContain('role="tab"');
  });

  test('renderTraktNotLinked shows the official-plugin guidance', () => {
    const el = document.createElement('div');
    ui.renderTraktNotLinked(el);
    expect(el.textContent).toContain('official Trakt plugin');
    expect(el.querySelector('.jfh-discovery-msg')).not.toBeNull();
  });

  test('renderShell with Trakt off renders a single own grid, no tab bar', async () => {
    const el = document.createElement('div');
    document.body.appendChild(el);
    // Background refresh returns the same visible set so the cache-first cards survive.
    ajaxRouter({ 'Discovery/My': { Recommendations: [rec()] } }, { userId: 'u1' });
    // Seed the own cache so renderOwnTab renders instantly from it.
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    ui.renderShell(el, false);
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    expect(el.querySelector('.jfh-discovery-card')).not.toBeNull();
  });
});

describe('renderOwnTab network paths', () => {
  test('cache hit renders instantly and fires a background refresh GET', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter({ 'Discovery/My': { Recommendations: [rec()] } }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'cached' })] });
    ui.renderOwnTab(el, false);
    // Rendered synchronously from cache.
    expect(el.querySelector('.jfh-discovery-card')).not.toBeNull();
    await flush();
    // Background refresh hit the My endpoint.
    expect(calls.some((c) => c.url.includes('/Discovery/My') && c.type === 'GET')).toBe(true);
  });

  test('cold fetch shows a spinner then renders cards from the response', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'Discovery/My': { Recommendations: [rec({ Title: 'fetched' })] } }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderOwnTab(el, true);
    // Before the promise resolves the spinner is visible.
    expect(el.querySelector('.jfh-discovery-spinner')).not.toBeNull();
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('fetched');
  });

  test('a 403 renders the disabled message and clears the cache', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { 'Discovery/My': { status: 403 } } });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    ui.renderOwnTab(el, true);
    await flush();
    expect(el.textContent).toContain('Discovery is not enabled');
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });
});

describe('renderOwnError', () => {
  let ui: UiBridge;
  let el: HTMLElement;
  beforeEach(() => {
    ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    el = document.createElement('div');
    document.body.appendChild(el);
  });

  test('403 invalidates the cache and shows the disabled message', () => {
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    ui.renderOwnError(el, { status: 403 });
    expect(ui.getCachedDiscoveryResult()).toBeNull();
    expect(el.textContent).toContain('Discovery is not enabled');
  });

  test('transient error with a cached fallback re-renders the last good cards', () => {
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'fallback' })] });
    ui.renderOwnError(el, { status: 500 });
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('fallback');
  });

  test('transient error with no cache shows the generic load error and a toast', () => {
    ui.renderOwnError(el, { responseJSON: { Message: 'HTTP 503 upstream' } });
    expect(el.textContent).toContain('Could not load discovery suggestions');
    const toast = document.querySelector('.jfh-discovery-toast');
    expect(toast!.textContent).toContain('unreachable');
  });
});

describe('renderTraktError + trakt tab fetches', () => {
  test('403 in trakt error shows the trakt-disabled message', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderTraktError(el, { status: 403 });
    expect(el.textContent).toContain('Trakt is not enabled');
  });

  test('non-403 trakt error shows the generic load error', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderTraktError(el, { status: 500 });
    expect(el.textContent).toContain('Could not load discovery suggestions');
  });

  test('renderTraktTrending fetches and renders the trending pool', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'Trakt/Trending': { Recommendations: [rec({ Title: 'trend' })] } }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderTraktTrending(el, true);
    expect(el.querySelector('.jfh-discovery-spinner')).not.toBeNull();
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('trend');
  });

  test('renderTraktPersonal shows not-linked when the server reports Linked:false', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'Discovery/My/Trakt': { Linked: false } }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderTraktPersonal(el, true);
    await flush();
    expect(el.textContent).toContain('not linked');
  });

  test('renderTraktPersonal renders cards from Result when linked', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      { 'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec({ Title: 'personal' })] } } },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderTraktPersonal(el, true);
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('personal');
  });
});

describe('maybeRefreshInBackground', () => {
  test('re-renders in place only when the visible set changed', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      { 'Discovery/My': { Recommendations: [rec({ Title: 'new', TmdbId: 999 })] } },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    // Prime a previous cache + render with a different id so the key differs.
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.renderCards(el, { Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('new');
  });

  test('a 403 during background refresh invalidates the cache', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { 'Discovery/My': { status: 403 } } });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });
});

describe('checkEmptyDiscoveryState', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
  });

  test('swaps the grid for the empty message once every card is gone', () => {
    const host = document.createElement('div');
    host.className = 'jfh-discovery-container';
    host.innerHTML = '<div class="jfh-discovery-grid"></div>';
    ui.checkEmptyDiscoveryState(host);
    expect(host.querySelector('.jfh-discovery-grid')).toBeNull();
    expect(host.textContent).toContain('No suggestions available');
  });

  test('leaves a grid that still has cards untouched', () => {
    const host = document.createElement('div');
    host.className = 'jfh-discovery-container';
    host.innerHTML = '<div class="jfh-discovery-grid"><div class="jfh-discovery-card"></div></div>';
    ui.checkEmptyDiscoveryState(host);
    expect(host.querySelector('.jfh-discovery-card')).not.toBeNull();
  });

  test('no grid is a safe no-op', () => {
    const host = document.createElement('div');
    expect(() => ui.checkEmptyDiscoveryState(host)).not.toThrow();
    expect(() => ui.checkEmptyDiscoveryState(null)).not.toThrow();
  });
});

describe('request flow', () => {
  function cardFixture(ui: UiBridge, over: Record<string, unknown> = {}) {
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec(over)] });
    return el;
  }

  test('handleRequest with a single profile submits and marks the card requested', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      {
        'RequestPermissions': { CanRequest: true, Profiles: [{ ServerId: 's', ProfileId: 'p', RootFolder: '/r' }] },
        '/Request': { Success: true },
      },
      { userId: 'u1' },
    );
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    btn.click();
    await flush();
    const post = calls.find((c) => c.type === 'POST' && c.url.includes('/Request'));
    expect(post).toBeTruthy();
    expect((post!.body as any).TmdbId).toBe(603);
    expect((post!.body as any).ProfileId).toBe('p');
    expect(btn.classList.contains('jfh-discovery-btn-done')).toBe(true);
  });

  test('permission denial marks the button failed and toasts the reason', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      { 'RequestPermissions': { CanRequest: false, DeniedReason: 'quota reached' } },
      { userId: 'u1' },
    );
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    btn.click();
    await flush();
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toBe('quota reached');
  });

  test('multiple profiles open the quality-profile popup; selection submits', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      {
        'RequestPermissions': {
          CanRequest: true,
          Profiles: [
            { ServerId: 's1', ProfileId: 'p1', ProfileName: 'HD', ServerName: 'Radarr', IsDefault: true },
            { ServerId: 's2', ProfileId: 'p2', ProfileName: 'UHD', ServerName: 'Radarr2' },
          ],
        },
        '/Request': { Success: true },
      },
      { userId: 'u1' },
    );
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    btn.click();
    await flush();
    const popup = document.getElementById('jfhDiscoveryPopup');
    expect(popup).not.toBeNull();
    const items = popup!.querySelectorAll('.jfh-discovery-popup-item');
    expect(items).toHaveLength(2);
    (items[1] as HTMLElement).click();
    await flush();
    const post = calls.find((c) => c.type === 'POST' && c.url.includes('/Request'));
    expect((post!.body as any).ProfileId).toBe('p2');
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
  });

  test('showProfilePopup renders profile names via textContent (no HTML injection)', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    ui.showProfilePopup(603, 'movie', btn, [{ ServerId: 's', ProfileId: 'p', ProfileName: '<b>evil</b>' }]);
    const item = document.querySelector('.jfh-discovery-popup-item')!;
    // Rendered as text, so the <b> is literal content, not an element.
    expect(item.querySelector('b')).toBeNull();
    expect(item.textContent).toContain('<b>evil</b>');
  });

  test('closeDiscoveryPopup removes the overlay and restores the dismiss sibling', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const row = document.createElement('div');
    row.className = 'jfh-discovery-btn-row';
    const req = document.createElement('button');
    const dismiss = document.createElement('button');
    dismiss.className = 'jfh-discovery-btn-dismiss';
    dismiss.style.display = 'none';
    row.append(req, dismiss);
    document.body.appendChild(row);
    ui.showProfilePopup(1, 'movie', req, [
      { ServerId: 'a', ProfileId: '1', ProfileName: 'A' },
      { ServerId: 'b', ProfileId: '2', ProfileName: 'B' },
    ]);
    expect(document.getElementById('jfhDiscoveryPopup')).not.toBeNull();
    ui.closeDiscoveryPopup(req, true);
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
    // Cancelled close restores the hidden dismiss sibling.
    expect(dismiss.style.display).toBe('');
  });
});

describe('dismiss flow', () => {
  test('handleDismissClick opens the confirmation; confirming posts Dismiss', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter({ '/Dismiss': {} }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec({ Title: 'Dismiss Me' })] });
    const dismissBtn = el.querySelector('.jfh-discovery-btn-dismiss') as HTMLButtonElement;
    dismissBtn.click();
    const popup = document.getElementById('jfhDiscoveryPopup');
    expect(popup).not.toBeNull();
    // The confirmation references the title it will hide.
    expect(popup!.textContent).toContain('Dismiss Me');
    const confirm = popup!.querySelector('.jfh-discovery-popup-item') as HTMLElement;
    confirm.click();
    await flush();
    const post = calls.find((c) => c.type === 'POST' && c.url.includes('/Dismiss'));
    expect(post).toBeTruthy();
    expect((post!.body as any).TmdbId).toBe(603);
  });

  test('executeDismiss failure restores the button and toasts', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { '/Dismiss': new Error('nope') } });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const dismissBtn = el.querySelector('.jfh-discovery-btn-dismiss') as HTMLButtonElement;
    ui.executeDismiss(603, 'movie', dismissBtn);
    await flush();
    expect(dismissBtn.disabled).toBe(false);
    expect(dismissBtn.textContent).toContain('Not interested');
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('Could not dismiss');
  });

  test('showDismissConfirmation without a title uses the generic text', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    ui.showDismissConfirmation(603, 'movie', '', btn);
    const popup = document.getElementById('jfhDiscoveryPopup')!;
    expect(popup.textContent).toContain('hidden from future suggestions');
  });
});

describe('injectStyles + injectNavigation', () => {
  test('injectStyles appends the style tag once (idempotent)', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.injectStyles();
    ui.injectStyles();
    expect(document.querySelectorAll('#jfhelper-discovery-styles')).toHaveLength(1);
    expect(document.getElementById('jfhelper-discovery-styles')!.textContent).toContain('jfh-discovery-grid');
  });

  test('injectNavigation adds the sidebar entry once', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    document.body.appendChild(sidebar);
    ui.injectNavigation();
    ui.injectNavigation();
    const items = sidebar.querySelectorAll('.jfhelper-nav-discovery');
    expect(items).toHaveLength(1);
    expect(items[0].textContent).toContain('Seerr Discovery');
  });

  test('injectNavigation is a no-op without a sidebar container', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    expect(() => ui.injectNavigation()).not.toThrow();
    expect(document.querySelector('.jfhelper-nav-discovery')).toBeNull();
  });
});

describe('renderCards rich branches', () => {
  let ui: UiBridge;
  beforeEach(() => {
    ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
  });

  test('a tv item builds a TMDB tv deep link and the TV meta tag', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ MediaType: 'tv' })] });
    const tmdbLink = el.querySelector('.jfh-discovery-flip-link') as HTMLElement;
    expect(tmdbLink.dataset.href).toBe('https://www.themoviedb.org/tv/603');
    expect(el.textContent).toContain('TV');
  });

  test('no rating and no genres omit those tags', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ TmdbRating: 0, Genres: [] })] });
    expect(el.querySelector('.jfh-discovery-card-genres')).toBeNull();
    // Only year + type tags remain, no star rating tag.
    expect(el.textContent).not.toContain('⭐');
  });

  test('a rating renders a one-decimal star tag', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ TmdbRating: 7 })] });
    expect(el.textContent).toContain('⭐ 7.0');
  });

  test('an empty title falls back to the Unknown label but keeps an empty title attr', () => {
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ Title: '' })] });
    const titleEl = el.querySelector('.jfh-discovery-card-title')!;
    expect(titleEl.textContent).toBe('Unknown');
    expect(titleEl.getAttribute('title')).toBe('');
  });

  test('a flip-link click opens a safe http url via window.open', () => {
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    (el.querySelector('.jfh-discovery-flip-link') as HTMLElement).click();
    expect(open).toHaveBeenCalledWith('https://www.themoviedb.org/movie/603', '_blank', 'noopener,noreferrer');
    open.mockRestore();
  });
});

describe('renderCards with Seerr + Trakt enabled', () => {
  test('seerr base url adds a Seerr flip link; trakt tab adds a local-score tag and Trakt link', async () => {
    const ui = freshDiscoveryHelpers();
    // ExternalLinks returns a Seerr url; loading it populates _seerrBaseUrl.
    ajaxRouter({ 'ExternalLinks': { SeerrUrl: 'https://seerr.example.com/' } }, { userId: 'u1' });
    await ui.loadExternalLinksConfig();
    // Enable Trakt + select the trending tab by driving renderShell through a probe.
    ajaxRouter(
      { 'Discovery/My/Trakt/Trending': { Recommendations: [rec({ TraktSlug: 'the-matrix-1999' })] },
        'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [] } } },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderDiscovery(el, true);
    await flush();
    // Click the Trending tab so renderCards runs with _activeTab = trending.
    const trendingBtn = Array.from(el.querySelectorAll('.jfh-discovery-tab'))
      .find((b) => (b as HTMLElement).dataset.tab === 'trending') as HTMLElement;
    trendingBtn.click();
    await flush();
    const links = Array.from(el.querySelectorAll('.jfh-discovery-flip-link')).map((l) => (l as HTMLElement).dataset.href);
    expect(links).toContain('https://seerr.example.com/movie/603');
    expect(links).toContain('https://trakt.tv/movies/the-matrix-1999');
    expect(el.querySelector('.jfh-discovery-local-score')).not.toBeNull();
  });
});

describe('renderDiscovery trakt probe', () => {
  test('a successful linked probe upgrades the panel to a three-tab bar', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      {
        'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec()] } },
        'Discovery/My': { Recommendations: [rec()] },
      },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderDiscovery(el, true);
    await flush();
    const tabs = el.querySelectorAll('.jfh-discovery-tab');
    expect(tabs).toHaveLength(3);
    const labels = Array.from(tabs).map((t) => t.textContent);
    expect(labels).toContain('Trakt for you');
    expect(labels).toContain('Trakt trending');
  });

  test('an unlinked probe hides the personal tab (two tabs only)', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      {
        'Discovery/My/Trakt': { Linked: false },
        'Discovery/My': { Recommendations: [rec()] },
      },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderDiscovery(el, true);
    await flush();
    const labels = Array.from(el.querySelectorAll('.jfh-discovery-tab')).map((t) => t.textContent);
    expect(labels).not.toContain('Trakt for you');
    expect(labels).toContain('Trakt trending');
  });

  test('a 403 probe keeps the single own grid with no tab bar', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(
      { 'Discovery/My': { Recommendations: [rec()] } },
      { userId: 'u1', failures: { 'Discovery/My/Trakt': { status: 403 } } },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    expect(el.querySelector('.jfh-discovery-card')).not.toBeNull();
  });
});

describe('fetchPermissionsAndRequest + decideAndSubmit', () => {
  test('no profiles submits directly with no extra payload fields', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      { 'RequestPermissions': { CanRequest: true, Profiles: [] }, '/Request': { Success: true } },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    ui.fetchPermissionsAndRequest(603, 'movie', btn);
    await flush();
    const post = calls.find((c) => c.type === 'POST' && c.url.includes('/Request'));
    expect(post).toBeTruthy();
    expect((post!.body as any).ProfileId).toBeUndefined();
  });

  test('a cached permission result skips the network and submits', async () => {
    const ui = freshDiscoveryHelpers();
    const perm = { CanRequest: true, Profiles: [{ ServerId: 's', ProfileId: 'p' }] };
    const calls = ajaxRouter(
      { 'RequestPermissions': perm, '/Request': { Success: true } },
      { userId: 'u1' },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    // First call populates the perm cache.
    ui.fetchPermissionsAndRequest(603, 'movie', btn);
    await flush();
    const permCallsAfterFirst = calls.filter((c) => c.url.includes('RequestPermissions')).length;
    // Second call for the same service/type/user must hit the cache, not refetch.
    ui.fetchPermissionsAndRequest(603, 'movie', btn);
    await flush();
    expect(calls.filter((c) => c.url.includes('RequestPermissions'))).toHaveLength(permCallsAfterFirst);
  });

  test('a permissions network error falls back to submitting with defaults', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      { '/Request': { Success: true } },
      { userId: 'u1', failures: { 'RequestPermissions': new Error('down') } },
    );
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    ui.fetchPermissionsAndRequest(603, 'movie', btn);
    await flush();
    expect(calls.some((c) => c.type === 'POST' && c.url.includes('/Request'))).toBe(true);
    expect(btn.classList.contains('jfh-discovery-btn-done')).toBe(true);
  });

  test('decideAndSubmit with a single profile forwards its ids', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter({ '/Request': { Success: true } }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    ui.decideAndSubmit(603, 'movie', btn, { CanRequest: true, Profiles: [{ ServerId: 'srv', ProfileId: 'prof', RootFolder: '/data' }] });
    await flush();
    const post = calls.find((c) => c.type === 'POST' && c.url.includes('/Request'));
    expect((post!.body as any).ServerId).toBe('srv');
    expect((post!.body as any).RootFolder).toBe('/data');
  });
});

describe('submitRequest outcomes', () => {
  function reqBtn(ui: UiBridge) {
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    return { el, btn: el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement };
  }

  test('a Success:false response marks the button failed, toasts, then auto-resets', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ '/Request': { Success: false, Message: 'HTTP 503' } }, { userId: 'u1' });
    const { btn } = reqBtn(ui);
    ui.submitRequest(603, 'movie', null, null, null, btn);
    await flush();
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('unreachable');
    // The 3s reset timer restores the Request label.
    await vi.advanceTimersByTimeAsync(3000);
    expect(btn.textContent).toContain('Request');
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(false);
  });

  test('a rejected request marks failed and surfaces the extracted server message', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { '/Request': { responseJSON: { Message: 'timed out' } } as any } });
    const { btn } = reqBtn(ui);
    ui.submitRequest(603, 'movie', null, null, null, btn);
    await flush();
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('Timed out');
  });

  test('a successful request invalidates the cache and animates the card out', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ '/Request': { Success: true } }, { userId: 'u1' });
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    const { el, btn } = reqBtn(ui);
    ui.submitRequest(603, 'movie', null, null, null, btn);
    await flush();
    expect(ui.getCachedDiscoveryResult()).toBeNull();
    expect(btn.classList.contains('jfh-discovery-btn-done')).toBe(true);
    // 5s hold, then 400ms fade removes the card and re-renders the empty state.
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(400);
    await flush();
    expect(el.querySelector('.jfh-discovery-card')).toBeNull();
  });
});

describe('setDismissSiblingHidden', () => {
  test('toggles the sibling dismiss button display', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const row = document.createElement('div');
    const req = document.createElement('button');
    const dismiss = document.createElement('button');
    dismiss.className = 'jfh-discovery-btn-dismiss';
    row.append(req, dismiss);
    ui.setDismissSiblingHidden(req, true);
    expect(dismiss.style.display).toBe('none');
    ui.setDismissSiblingHidden(req, false);
    expect(dismiss.style.display).toBe('');
  });
});

describe('popup finalize + styles', () => {
  test('injectPopupStyles appends once', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.injectPopupStyles();
    ui.injectPopupStyles();
    expect(document.querySelectorAll('#jfhelper-popup-styles')).toHaveLength(1);
  });

  test('finalizeDiscoveryPopup wires Escape and overlay-click to close', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    const overlay = document.createElement('div');
    overlay.id = 'jfhDiscoveryPopup';
    const popup = document.createElement('div');
    const list = document.createElement('div');
    ui.finalizeDiscoveryPopup(overlay, popup, list, btn);
    expect(document.getElementById('jfhDiscoveryPopup')).not.toBeNull();
    // Escape closes it.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
  });

  test('overlay background click closes the popup', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    const overlay = document.createElement('div');
    overlay.id = 'jfhDiscoveryPopup';
    ui.finalizeDiscoveryPopup(overlay, document.createElement('div'), document.createElement('div'), btn);
    overlay.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    // The handler closes only when the event target is the overlay itself.
    overlay.click();
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
  });
});

describe('bootstrap probe handlers', () => {
  test('handleDiscoveryProbe with recommendations seeds the cache and injects full UI', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    window.location.hash = '#/home';
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    document.body.appendChild(sidebar);
    ui.handleDiscoveryProbe({ Recommendations: [rec()] }, 'u1');
    // Full UI injects the sidebar nav and seeds the own-grid cache.
    expect(sidebar.querySelector('.jfhelper-nav-discovery')).not.toBeNull();
    expect(ui.getCachedDiscoveryResult()).not.toBeNull();
  });

  test('handleDiscoveryProbe with no results injects styles but not the sidebar nav', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    window.location.hash = '#/home';
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    document.body.appendChild(sidebar);
    ui.handleDiscoveryProbe({ Recommendations: [] }, 'u1');
    expect(document.getElementById('jfhelper-discovery-styles')).not.toBeNull();
    expect(sidebar.querySelector('.jfhelper-nav-discovery')).toBeNull();
  });

  test('handleDiscoveryProbe discards a result from a prior account', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'current' });
    // startedUserId differs from the live user: must not seed the cache.
    ui.handleDiscoveryProbe({ Recommendations: [rec()] }, 'stale');
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });

  test('probeDiscoveryAvailability routes a successful probe into handleDiscoveryProbe', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'Discovery/My': { Recommendations: [rec()] } }, { userId: 'u1' });
    window.location.hash = '#/home';
    ui.probeDiscoveryAvailability('u1');
    await flush();
    // The full-UI path seeds the cache on a non-empty probe.
    expect(ui.getCachedDiscoveryResult()).not.toBeNull();
  });
});

describe('sidebar activation helpers', () => {
  test('activateDiscoveryTab returns false when no marker exists', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    expect(ui.activateDiscoveryTab()).toBe(false);
  });

  test('activateDiscoveryTab clicks the legacy positional tab for the panel index', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const panel = document.createElement('div');
    panel.dataset.index = '1';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    const bar = document.createElement('div');
    bar.className = 'headerTabs';
    const t0 = document.createElement('button');
    const t1 = document.createElement('button');
    bar.append(t0, t1);
    document.body.appendChild(bar);
    const clicked = vi.fn();
    t1.addEventListener('click', clicked);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(clicked).toHaveBeenCalled();
  });

  test('notifyDiscoveryTabMissing falls back to a toast when Dashboard is absent', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.notifyDiscoveryTabMissing();
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('Discovery tab could not be found');
  });
});

// ---------------------------------------------------------------------------
// Extended coverage. The suite above exercises the render flows; the blocks
// below drive the retry bootstrap, string loading, staleness guards, popups,
// and the watcher/mount machinery directly on the unwrapped bridge.
// ---------------------------------------------------------------------------

function setHash(hash: string) {
  // replaceState updates location.hash without firing hashchange, so watchers
  // started by other spec scopes never cross-fire into this test's mounts.
  window.history.replaceState(null, '', hash);
}

function deferredAjaxRouter(opts: { userId?: string } = {}) {
  const pending: { url: string; type?: string; resolve: (v: unknown) => void; reject: (e: unknown) => void }[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => opts.userId ?? 'u1',
    accessToken: () => 't',
    ajax: (o: any) =>
      new Promise((resolve, reject) => {
        pending.push({ url: String(o.url), type: o.type, resolve, reject });
      }),
  };
  return pending;
}

describe('waitForApi retry budget', () => {
  test('calls back immediately when a user is already present', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const cb = vi.fn();
    const onTimeout = vi.fn();
    ui.waitForApi(cb, onTimeout);
    expect(cb).toHaveBeenCalledTimes(1);
    expect(onTimeout).not.toHaveBeenCalled();
  });

  test('retries while ApiClient is unavailable and recovers once it appears', async () => {
    const ui = freshDiscoveryHelpers();
    delete (globalThis as any).ApiClient;
    const cb = vi.fn();
    const onTimeout = vi.fn();
    ui.waitForApi(cb, onTimeout);
    expect(cb).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(500 * 3);
    expect(cb).not.toHaveBeenCalled();
    (globalThis as any).ApiClient = { getUrl: (p: string) => p, getCurrentUserId: () => 'u1' };
    await vi.advanceTimersByTimeAsync(500);
    expect(cb).toHaveBeenCalledTimes(1);
    expect(onTimeout).not.toHaveBeenCalled();
  });

  test('treats a missing or empty current user as unavailable', async () => {
    const ui = freshDiscoveryHelpers();
    (globalThis as any).ApiClient = { getUrl: (p: string) => p };
    const cb = vi.fn();
    ui.waitForApi(cb);
    await vi.advanceTimersByTimeAsync(500);
    expect(cb).not.toHaveBeenCalled();
    (globalThis as any).ApiClient = { getUrl: (p: string) => p, getCurrentUserId: () => '' };
    await vi.advanceTimersByTimeAsync(500);
    expect(cb).not.toHaveBeenCalled();
    (globalThis as any).ApiClient = { getUrl: (p: string) => p, getCurrentUserId: () => 'u1' };
    await vi.advanceTimersByTimeAsync(500);
    expect(cb).toHaveBeenCalledTimes(1);
  });

  test('gives up after the combined fast + slow retry budget and calls onTimeout', async () => {
    const ui = freshDiscoveryHelpers();
    delete (globalThis as any).ApiClient;
    const cb = vi.fn();
    const onTimeout = vi.fn();
    ui.waitForApi(cb, onTimeout);
    await vi.advanceTimersByTimeAsync(60 * 500 + 40 * 3000 + 3000);
    expect(cb).not.toHaveBeenCalled();
    expect(onTimeout).toHaveBeenCalledTimes(1);
  });

  test('an absent onTimeout is tolerated at exhaustion', async () => {
    const ui = freshDiscoveryHelpers();
    delete (globalThis as any).ApiClient;
    const cb = vi.fn();
    ui.waitForApi(cb);
    await vi.advanceTimersByTimeAsync(60 * 500 + 40 * 3000 + 3000);
    expect(cb).not.toHaveBeenCalled();
    expect(document.body.textContent).toBe('');
  });
});

describe('currentDiscoveryUserId + loadStrings', () => {
  test('currentDiscoveryUserId is empty without ApiClient or a user', () => {
    const ui = freshDiscoveryHelpers();
    delete (globalThis as any).ApiClient;
    expect(ui.currentDiscoveryUserId()).toBe('');
    (globalThis as any).ApiClient = { getUrl: (p: string) => p };
    expect(ui.currentDiscoveryUserId()).toBe('');
  });

  test('loadStrings stores translations and t() serves them', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ Translations: { discoveryTitle: 'Entdeckung' } }, { userId: 'u1' });
    const done = vi.fn();
    ui.loadStrings(done);
    await flush();
    expect(done).toHaveBeenCalledTimes(1);
    expect(ui.t('discoveryTitle', 'Seerr Discovery')).toBe('Entdeckung');
  });

  test('a null payload falls back to an empty string table', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ Translations: null }, { userId: 'u1' });
    const done = vi.fn();
    ui.loadStrings(done);
    await flush();
    expect(done).toHaveBeenCalledTimes(1);
    expect(ui.t('discoveryTitle', 'Seerr Discovery')).toBe('Seerr Discovery');
  });

  test('a failed translations fetch still calls back with fallbacks', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { Translations: new Error('down') } });
    const done = vi.fn();
    ui.loadStrings(done);
    await flush();
    expect(done).toHaveBeenCalledTimes(1);
    expect(ui.t('discoveryTitle', 'Seerr Discovery')).toBe('Seerr Discovery');
  });
});

describe('error messages, visible key, and reasons', () => {
  test.each([
    ['HTTP 403: forbidden', 'No permission. Contact your admin.'],
    ['user not linked', 'No permission. Contact your admin.'],
    ['no permission for this', 'No permission. Contact your admin.'],
    ['HTTP 503 upstream', 'Server unreachable. Try again later.'],
    ['server unreachable', 'Server unreachable. Try again later.'],
    ['request timed out', 'Timed out. Try again later.'],
    ['gateway timeout', 'Timed out. Try again later.'],
    ['something odd', 'Request failed. Try again later.'],
    ['', 'Request failed. Try again later.'],
  ])('getUserFriendlyErrorMessage maps %j', (input, expected) => {
    const ui = freshDiscoveryHelpers();
    expect(ui.getUserFriendlyErrorMessage(input)).toBe(expected);
  });

  test('discoveryVisibleKey joins ids and types, and bails on non-arrays', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.discoveryVisibleKey(null)).toBe('');
    expect(ui.discoveryVisibleKey({ Recommendations: 'nope' })).toBe('');
    expect(ui.discoveryVisibleKey({ Recommendations: [] })).toBe('');
    expect(ui.discoveryVisibleKey({
      Recommendations: [
        { TmdbId: 11, MediaType: 'movie' },
        { MediaType: 'tv' },
        { TmdbId: 22 },
      ],
    })).toBe('11:movie|:tv|22:');
  });

  test('formatReason handles empty, templates, phrases, and raw fallbacks', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.formatReason('', '')).toBe('');
    expect(ui.formatReason('reasonPersonNamed', null, 'Sigourney Weaver')).toBe('Featuring Sigourney Weaver');
    expect(ui.formatReason('reasonGenre', null, 'Drama')).toBe('Because you enjoy Drama');
    expect(ui.formatReason('reasonTrending', 'reasonTrending')).toBe('Trending - new and highly rated');
    expect(ui.formatReason('reasonPopularAmongViewers', null)).toBe('Popular with other viewers');
    expect(ui.formatReason('reasonDefault', null)).toBe('Recommended for you');
    expect(ui.formatReason('reasonUnknown', 'reasonUnknown')).toBe('');
    expect(ui.formatReason('reasonPersonNamed', 'reasonPersonNamed: Al Pacino')).toBe('Featuring Al Pacino');
    expect(ui.formatReason('', 'A raw explanatory reason')).toBe('A raw explanatory reason');
    expect(ui.formatReason('reasonGenre', 'reasonGenre')).toBe('');
  });

  test('formatReason localizes known custom keys when strings are loaded', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({
      Translations: {
        reasonGenre: 'Weil Sie {0} mögen',
        reasonSpecial: 'Speziell für Sie',
        reasonAnchored: 'Mit {0}',
      },
    }, { userId: 'u1' });
    ui.loadStrings(() => {});
    await flush();
    expect(ui.formatReason('reasonGenre', null, 'Komödie')).toBe('Weil Sie Komödie mögen');
    expect(ui.formatReason('reasonSpecial', null)).toBe('Speziell für Sie');
    expect(ui.formatReason('reasonAnchored', null, 'Bryan Cranston')).toBe('Mit Bryan Cranston');
  });

  test('formatRawReasonFallback passes plain text through and hides bare tokens', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.formatRawReasonFallback('Popular and highly rated')).toBe('Popular and highly rated');
    expect(ui.formatRawReasonFallback('reasonTrending')).toBe('');
    expect(ui.formatRawReasonFallback('')).toBe('');
  });

  test('toasts dismiss on click and remove themselves after the fade', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.notifyDiscoveryTabMissing();
    const toast = document.querySelector('.jfh-discovery-toast')!;
    expect(toast.classList.contains('jfh-discovery-toast-visible')).toBe(true);
    toast.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(toast.classList.contains('jfh-discovery-toast-hidden')).toBe(true);
    await vi.advanceTimersByTimeAsync(300);
    expect(document.querySelector('.jfh-discovery-toast')).toBeNull();
  });

  test('toasts auto-dismiss after the display duration', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.notifyDiscoveryTabMissing();
    expect(document.querySelectorAll('.jfh-discovery-toast')).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(300);
    expect(document.querySelectorAll('.jfh-discovery-toast')).toHaveLength(0);
  });
});

describe('isActiveTabContainer + findActiveContainer', () => {
  function layout(el: Element) {
    Object.defineProperty(el, 'offsetParent', { value: document.body, configurable: true });
    return el;
  }

  test('rejects disconnected elements and hidden ancestors', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.isActiveTabContainer(document.createElement('div'))).toBe(false);
    const hiddenParent = document.createElement('div');
    hiddenParent.hidden = true;
    const child = document.createElement('div');
    hiddenParent.appendChild(child);
    document.body.appendChild(hiddenParent);
    expect(ui.isActiveTabContainer(child)).toBe(false);
    const classHidden = document.createElement('div');
    classHidden.className = 'hide';
    const inner = document.createElement('div');
    classHidden.appendChild(inner);
    document.body.appendChild(classHidden);
    expect(ui.isActiveTabContainer(inner)).toBe(false);
  });

  test('rejects markers in inactive tabContent wrappers but accepts active ones', () => {
    const ui = freshDiscoveryHelpers();
    const inactive = document.createElement('div');
    inactive.className = 'tabContent';
    const markerA = document.createElement('div');
    inactive.appendChild(markerA);
    document.body.appendChild(inactive);
    layout(markerA);
    expect(ui.isActiveTabContainer(markerA)).toBe(false);

    const active = document.createElement('div');
    active.className = 'tabContent is-active';
    const markerB = document.createElement('div');
    active.appendChild(markerB);
    document.body.appendChild(active);
    layout(markerB);
    expect(ui.isActiveTabContainer(markerB)).toBe(true);
  });

  test('requires real layout so display:none panels are rejected', () => {
    const ui = freshDiscoveryHelpers();
    const marker = document.createElement('div');
    document.body.appendChild(marker);
    expect(ui.isActiveTabContainer(marker)).toBe(false);
    layout(marker);
    expect(ui.isActiveTabContainer(marker)).toBe(true);
  });

  test('findActiveContainer prefers the last laid-out marker and skips hidden ones', () => {
    const ui = freshDiscoveryHelpers();
    const first = document.createElement('div');
    first.className = 'jellyfinhelper discovery';
    layout(first);
    const hiddenWrap = document.createElement('div');
    hiddenWrap.hidden = true;
    const second = document.createElement('div');
    second.className = 'jellyfinhelper discovery';
    hiddenWrap.appendChild(second);
    document.body.appendChild(first);
    document.body.appendChild(hiddenWrap);
    layout(second);
    expect(ui.findActiveContainer()).toBe(first);

    const third = document.createElement('div');
    third.className = 'jellyfinhelper discovery';
    document.body.appendChild(third);
    layout(third);
    expect(ui.findActiveContainer()).toBe(third);
  });

  test('findActiveContainer returns null when no marker is active', () => {
    const ui = freshDiscoveryHelpers();
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    document.body.appendChild(marker);
    expect(ui.findActiveContainer()).toBeNull();
  });
});

function makeMarker(layout = false) {
  const el = document.createElement('div');
  el.className = 'jellyfinhelper discovery';
  document.body.appendChild(el);
  if (layout) Object.defineProperty(el, 'offsetParent', { value: document.body, configurable: true });
  return el;
}

describe('maybeRefreshInBackground guards', () => {
  test('the in-flight flag prevents overlapping refreshes', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.renderCards(el, { Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.maybeRefreshInBackground(el);
    ui.maybeRefreshInBackground(el);
    expect(pending).toHaveLength(1);
    pending[0].resolve({ Recommendations: [rec({ Title: 'new', TmdbId: 2 })] });
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('new');
    ui.maybeRefreshInBackground(el);
    expect(pending).toHaveLength(2);
  });

  test('an identical payload is not re-rendered', async () => {
    const ui = freshDiscoveryHelpers();
    const payload = { Recommendations: [rec({ Title: 'same', TmdbId: 3 })] };
    ajaxRouter({ 'Discovery/My': payload }, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult(payload);
    ui.renderCards(el, payload);
    el.innerHTML = '';
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(el.querySelector('.jfh-discovery-container')).toBeNull();
  });

  test('a generation bump during the flight drops the payload', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.renderCards(el, { Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.maybeRefreshInBackground(el);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Recommendations: [rec({ Title: 'new', TmdbId: 2 })] });
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('old');
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });

  test('an account switch during the flight drops the payload', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.renderCards(el, { Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.maybeRefreshInBackground(el);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    pending[0].resolve({ Recommendations: [rec({ Title: 'new', TmdbId: 2 })] });
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('old');
  });

  test('a detached target container is skipped', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'Discovery/My': { Recommendations: [rec({ Title: 'new', TmdbId: 2 })] } }, { userId: 'u1' });
    const el = document.createElement('div');
    ui.setCachedDiscoveryResult({ Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.renderCards(el, { Recommendations: [rec({ Title: 'old', TmdbId: 1 })] });
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('old');
  });

  test('with trakt tabs on and no mounted host tracked, the refresh is a no-op', async () => {
    const ui = freshDiscoveryHelpers();
    let ownFetches = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Trakt/Trending')) return Promise.resolve({ Recommendations: [rec({ Title: 'trending' })] });
        if (url.includes('Trakt')) {
          return Promise.resolve({ Linked: true, Result: { Recommendations: [rec({ Title: 'probe' })] } });
        }
        ownFetches++;
        return Promise.resolve({ Recommendations: [rec({ Title: 'own', TmdbId: 7 })] });
      },
    };
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelector('.jfh-discovery-tab-host')!.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('own');
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(ownFetches).toBe(3);
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });
});

describe('trakt cache helpers + cache-hit paths', () => {
  test('isEmptyTraktResult detects missing and empty pools', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.isEmptyTraktResult(null)).toBe(true);
    expect(ui.isEmptyTraktResult({})).toBe(true);
    expect(ui.isEmptyTraktResult({ Recommendations: [] })).toBe(true);
    expect(ui.isEmptyTraktResult({ Recommendations: [rec()] })).toBe(false);
  });

  test('traktCacheFresh honors owner, non-empty pools, and empty expiry', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    expect(ui.traktCacheFresh(null)).toBe(false);
    const nonEmpty = ui.makeTraktCacheEntry({ Recommendations: [rec()] }, 'u1');
    expect(ui.traktCacheFresh(nonEmpty)).toBe(true);
    expect(ui.traktCacheFresh({ userId: 'u2', data: nonEmpty.data, storedAt: nonEmpty.storedAt })).toBe(false);
    const empty = ui.makeTraktCacheEntry({ Recommendations: [] }, 'u1');
    expect(ui.traktCacheFresh(empty)).toBe(true);
    await vi.advanceTimersByTimeAsync(15001);
    expect(ui.traktCacheFresh(empty)).toBe(false);
    const staleNonEmpty = ui.makeTraktCacheEntry({ Recommendations: [rec()] }, 'u1');
    await vi.advanceTimersByTimeAsync(60000);
    expect(ui.traktCacheFresh(staleNonEmpty)).toBe(true);
  });

  test('renderTraktPersonal serves the cached pool on a non-forced remount', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      { 'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec({ Title: 'personal' })] } } },
      { userId: 'u1' },
    );
    const el1 = document.createElement('div');
    document.body.appendChild(el1);
    ui.renderTraktPersonal(el1, true);
    await flush();
    const before = calls.length;
    const el2 = document.createElement('div');
    document.body.appendChild(el2);
    ui.renderTraktPersonal(el2, false);
    await flush();
    expect(calls.length).toBe(before);
    expect(el2.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('personal');
  });

  test('renderTraktTrending serves the cached pool on a non-forced remount', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(
      { 'Trakt/Trending': { Recommendations: [rec({ Title: 'trend' })] } },
      { userId: 'u1' },
    );
    const el1 = document.createElement('div');
    document.body.appendChild(el1);
    ui.renderTraktTrending(el1, true);
    await flush();
    const before = calls.length;
    const el2 = document.createElement('div');
    document.body.appendChild(el2);
    ui.renderTraktTrending(el2, false);
    await flush();
    expect(calls.length).toBe(before);
    expect(el2.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('trend');
  });

  test('staleness drops a trakt personal failure instead of rendering it', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktPersonal(host, true);
    ui.invalidateDiscoveryResult();
    pending[0].reject({ status: 500 });
    await flush();
    expect(host.querySelector('.jfh-discovery-spinner')).not.toBeNull();
  });

  test('staleness drops a trakt trending failure instead of rendering it', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktTrending(host, true);
    ui.invalidateDiscoveryResult();
    pending[0].reject({ status: 500 });
    await flush();
    expect(host.querySelector('.jfh-discovery-spinner')).not.toBeNull();
  });
});

describe('renderDiscovery probe + shell flows', () => {
  const TRAKT_ROUTES = {
    'Trakt/Trending': { Recommendations: [rec({ Title: 'trendy' })] },
    'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec({ Title: 'perso' })] } },
    'Discovery/My': { Recommendations: [rec({ Title: 'own', TmdbId: 7 })] },
  };

  test('renders the own grid immediately, then upgrades to the tab bar', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter(TRAKT_ROUTES, { userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelectorAll('.jfh-discovery-tab')).toHaveLength(3);
    expect(el.querySelector('[data-tab="own"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);
    const host = el.querySelector('.jfh-discovery-tab-host')!;
    expect(host.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('own');
    expect(el.textContent).toContain('Trakt for you');
  });

  test('shows the pending slot only while the probe is genuinely outstanding', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    expect(el.querySelector('.jfh-trakt-pending')).toBeNull();
    await vi.advanceTimersByTimeAsync(300);
    expect(el.querySelector('.jfh-trakt-pending')).not.toBeNull();
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My')!.resolve({ Recommendations: [rec()] });
    await flush();
    expect(el.querySelector('.jfh-trakt-pending')).toBeNull();
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('a stalled probe fails closed and the next mount re-probes', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await vi.advanceTimersByTimeAsync(300);
    expect(el.querySelector('.jfh-trakt-pending')).not.toBeNull();
    await vi.advanceTimersByTimeAsync(12000);
    expect(el.querySelector('.jfh-trakt-pending')).toBeNull();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    ui.renderDiscovery(el, true);
    expect(pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')).toHaveLength(2);
  });

  test('a 403 probe resets trakt state and the next mount re-probes', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(TRAKT_ROUTES, { userId: 'u1', failures: { 'Discovery/My/Trakt': { status: 403 } } });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    ui.renderDiscovery(el, true);
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My/Trakt')).toHaveLength(2);
  });

  test('a transient probe failure keeps the single grid and re-probes later', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(TRAKT_ROUTES, { userId: 'u1', failures: { 'Discovery/My/Trakt': { status: 500 } } });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('own');
    ui.renderDiscovery(el, true);
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My/Trakt')).toHaveLength(2);
  });

  test('a stale probe from a previous account never upgrades the shell', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
  });

  test('a linked:false probe renders no personal tab', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ ...TRAKT_ROUTES, 'Discovery/My/Trakt': { Linked: false } }, { userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    const labels = Array.from(el.querySelectorAll('.jfh-discovery-tab')).map((b) => b.textContent);
    expect(labels).toHaveLength(2);
    expect(el.textContent).not.toContain('Trakt for you');
  });

  test('a same-user probe already in flight is not fired twice', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    ui.renderDiscovery(el, true);
    expect(pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')).toHaveLength(1);
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My')!.resolve({ Recommendations: [rec()] });
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('tab clicks switch the active tab and repaint the host', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(TRAKT_ROUTES, { userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    const host = () => el.querySelector('.jfh-discovery-tab-host')!;

    (el.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await flush();
    expect(el.querySelector('[data-tab="trakt"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);
    expect(host().querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('perso');

    const before = calls.length;
    (el.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await flush();
    expect(calls.length).toBe(before);

    (el.querySelector('[data-tab="trending"]') as HTMLElement).click();
    await flush();
    expect(el.querySelector('[data-tab="trending"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);
    expect(host().querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('trendy');

    (el.querySelector('[data-tab="own"]') as HTMLElement).click();
    await flush();
    expect(el.querySelector('[data-tab="own"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);
    expect(host().querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('own');
  });

  test('an account switch while the personal tab is active falls back to the own grid', async () => {
    const ui = freshDiscoveryHelpers();
    let user = 'u1';
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => user,
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Trakt/Trending')) return Promise.resolve({ Recommendations: [rec({ Title: 'trendy' })] });
        if (url.includes('Trakt')) {
          return Promise.resolve(user === 'u1'
            ? { Linked: true, Result: { Recommendations: [rec({ Title: 'perso' })] } }
            : { Linked: false });
        }
        return Promise.resolve({ Recommendations: [rec({ Title: 'own', TmdbId: 7 })] });
      },
    };
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    (el.querySelector('[data-tab="trakt"]') as HTMLElement).click();
    await flush();
    expect(el.querySelector('[data-tab="trakt"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);

    user = 'u2';
    ui.renderDiscovery(el, true);
    await flush();
    expect(el.querySelectorAll('.jfh-discovery-tab')).toHaveLength(2);
    expect(el.querySelector('[data-tab="own"]')!.classList.contains('jfh-discovery-tab-active')).toBe(true);
    expect(el.textContent).not.toContain('Trakt for you');
    expect(el.querySelector('.jfh-discovery-tab-host')!.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('own');
  });
});

describe('renderOwnTab shared fetch + renderOwnError', () => {
  test('concurrent own-tab renders share one request and both hosts render', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    const h2 = document.createElement('div');
    document.body.appendChild(h1);
    document.body.appendChild(h2);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h2, true);
    expect(pending).toHaveLength(1);
    pending[0].resolve({ Recommendations: [rec({ Title: 'shared' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('shared');
    expect(h2.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('shared');
  });

  test('a detached host is skipped while the attached one still renders', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    const h2 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h2, true);
    pending[0].resolve({ Recommendations: [rec({ Title: 'shared' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')).not.toBeNull();
    expect(h2.querySelector('.jfh-discovery-card-title')).toBeNull();
    expect(h2.querySelector('.jfh-discovery-spinner')).not.toBeNull();
  });

  test('a generation bump drops shared results', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Recommendations: [rec({ Title: 'stale' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')).toBeNull();
  });

  test('a tab shell built meanwhile wins over the stale own-grid result', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    h1.innerHTML = '<div class="jfh-discovery-tabs"></div>';
    pending[0].resolve({ Recommendations: [rec({ Title: 'late' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    expect(h1.querySelector('.jfh-discovery-card-title')).toBeNull();
  });

  test('a rejected shared fetch renders the error into the attached host', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    pending[0].reject({ status: 500 });
    await flush();
    expect(h1.textContent).toContain('Could not load discovery suggestions');
  });

  test('success and failure are dropped once the request generation moved on', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Recommendations: [rec({ Title: 'stale' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')).toBeNull();
    const h2 = document.createElement('div');
    document.body.appendChild(h2);
    ui.renderOwnTab(h2, true);
    ui.invalidateDiscoveryResult();
    pending[1].reject({ status: 500 });
    await flush();
    expect(h2.textContent).not.toContain('Could not load discovery suggestions');
  });

  test('renderOwnError is a no-op for a detached container', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderOwnError(el, { status: 500 });
    expect(el.textContent).toBe('');
  });

  test('renderOwnError falls back to cached cards on a transient failure', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const fallback = { Recommendations: [rec({ Title: 'cached' })] };
    ui.setCachedDiscoveryResult(fallback);
    const el = document.createElement('div');
    el.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-grid"></div></div>';
    document.body.appendChild(el);
    ui.renderOwnError(el, { status: 500 });
    expect(el.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('cached');
  });

  test('renderOwnError keeps a tab shell instead of overwriting it with the fallback', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    ui.setCachedDiscoveryResult({ Recommendations: [rec()] });
    const el = document.createElement('div');
    el.innerHTML = '<div class="jfh-discovery-tabs"></div>';
    document.body.appendChild(el);
    ui.renderOwnError(el, { status: 500 });
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    expect(el.querySelector('.jfh-discovery-card')).toBeNull();
  });

  test('renderOwnError shows the disabled message and a friendly toast on 403', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderOwnError(el, { status: 403, responseJSON: { Message: 'HTTP 403 Forbidden' } });
    expect(el.textContent).toContain('Discovery is not enabled');
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('No permission');
    await vi.advanceTimersByTimeAsync(5400);
    expect(document.querySelectorAll('.jfh-discovery-toast')).toHaveLength(0);
  });
});

describe('injectNavigation + activateDiscoveryTab', () => {
  function clearMarkers() {
    document.querySelectorAll('.jellyfinhelper.discovery').forEach((n) => n.remove());
    document.getElementById('jfhDiscoveryPopup')?.remove();
  }

  function buildSidebar() {
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    const media = document.createElement('div');
    media.className = 'libraryMenuOptions';
    sidebar.appendChild(media);
    document.body.appendChild(sidebar);
    return { sidebar, media };
  }

  test('injectNavigation inserts the section before the media options and is idempotent', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const { sidebar, media } = buildSidebar();
    ui.injectNavigation();
    const nav = sidebar.querySelector('a')!;
    expect(nav).not.toBeNull();
    expect(sidebar.querySelector('.sidebarHeader')!.textContent).toBe('Jellyfin Helper');
    expect(nav.closest('.sidebarHeader') === null).toBe(true);
    expect(sidebar.firstElementChild!.nextElementSibling).toBe(media);
    ui.injectNavigation();
    expect(sidebar.querySelectorAll('a')).toHaveLength(1);
  });

  test('injectNavigation appends the section when media options are absent', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    document.body.appendChild(sidebar);
    ui.injectNavigation();
    expect(sidebar.querySelector('a')).not.toBeNull();
  });

  test('injectNavigation without a sidebar is a safe no-op', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    expect(() => ui.injectNavigation()).not.toThrow();
  });

  test('a nav click activates the modern MUI anchor for the marker panel', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const { sidebar } = buildSidebar();
    ui.injectNavigation();
    const panel = document.createElement('div');
    panel.dataset.index = '1';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    const header = document.createElement('header');
    header.className = 'MuiAppBar-root';
    const anchor = document.createElement('a');
    anchor.setAttribute('href', '#/home?tab=1');
    header.appendChild(anchor);
    document.body.appendChild(header);
    const clicked = vi.fn();
    anchor.addEventListener('click', clicked);
    (sidebar.querySelector('a') as HTMLElement).click();
    expect(clicked).toHaveBeenCalledTimes(1);
  });

  test('a nav click without a container routes home and alerts after the retry', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const { sidebar } = buildSidebar();
    ui.injectNavigation();
    const show = vi.fn();
    (globalThis as any).Emby = { Page: { show } };
    const alertSpy = vi.fn();
    (globalThis as any).Dashboard = { alert: alertSpy };
    (sidebar.querySelector('a') as HTMLElement).click();
    expect(show).toHaveBeenCalledWith('/home.html');
    await vi.advanceTimersByTimeAsync(800);
    expect(alertSpy).toHaveBeenCalledTimes(1);
    delete (globalThis as any).Emby;
    delete (globalThis as any).Dashboard;
  });

  test('a nav click without Emby falls back to the missing-tab toast', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const { sidebar } = buildSidebar();
    ui.injectNavigation();
    (sidebar.querySelector('a') as HTMLElement).click();
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('could not be found');
  });

  test('activateDiscoveryTab clicks the matching MUI anchor', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const panel = document.createElement('div');
    panel.dataset.index = '2';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    const header = document.createElement('header');
    header.className = 'MuiAppBar-root';
    const anchor = document.createElement('a');
    anchor.setAttribute('href', '#/home?tab=2');
    header.appendChild(anchor);
    document.body.appendChild(header);
    const clicked = vi.fn();
    anchor.addEventListener('click', clicked);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(clicked).toHaveBeenCalledTimes(1);
  });

  test('activateDiscoveryTab falls back to the positional legacy tab', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const panel = document.createElement('div');
    panel.dataset.index = '1';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    const tabs = document.createElement('div');
    tabs.className = 'headerTabs';
    const first = document.createElement('button');
    const second = document.createElement('button');
    tabs.appendChild(first);
    tabs.appendChild(second);
    document.body.appendChild(tabs);
    const clicked = vi.fn();
    second.addEventListener('click', clicked);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(clicked).toHaveBeenCalledTimes(1);
  });

  test('activateDiscoveryTab matches data-tab / data-tabid attributes', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    document.body.appendChild(marker);
    const tabA = document.createElement('button');
    tabA.setAttribute('role', 'tab');
    tabA.dataset.tabid = 'jellyfinhelper-discovery';
    document.body.appendChild(tabA);
    const clickedA = vi.fn();
    tabA.addEventListener('click', clickedA);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(clickedA).toHaveBeenCalledTimes(1);

    tabA.remove();
    const tabB = document.createElement('button');
    tabB.setAttribute('role', 'tab');
    tabB.dataset.tab = 'jellyfinhelper-discovery';
    document.body.appendChild(tabB);
    const clickedB = vi.fn();
    tabB.addEventListener('click', clickedB);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(clickedB).toHaveBeenCalledTimes(1);
  });

  test('activateDiscoveryTab routes through the hash when only the MUI header exists', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    const panel = document.createElement('div');
    panel.dataset.index = '3';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    const header = document.createElement('header');
    header.className = 'MuiAppBar-root';
    document.body.appendChild(header);
    expect(ui.activateDiscoveryTab()).toBe(true);
    expect(window.location.hash).toBe('#/home?tab=3');
    setHash('#/nowhere');
  });

  test('activateDiscoveryTab returns false when nothing can be activated', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    clearMarkers();
    expect(ui.activateDiscoveryTab()).toBe(false);
    const panel = document.createElement('div');
    panel.dataset.index = '4';
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    expect(ui.activateDiscoveryTab()).toBe(false);
  });

  test('notifyDiscoveryTabMissing prefers Dashboard.alert when available', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const alertSpy = vi.fn();
    (globalThis as any).Dashboard = { alert: alertSpy };
    ui.notifyDiscoveryTabMissing();
    expect(alertSpy).toHaveBeenCalledTimes(1);
    delete (globalThis as any).Dashboard;
  });
});

describe('renderCards metadata fallbacks', () => {
  test('missing title, poster, year, rating, and genres use fallbacks', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderCards(el, {
      Recommendations: [rec({
        Title: '',
        PosterPath: '',
        Overview: '',
        Year: 0,
        TmdbRating: 0,
        Genres: [],
        MediaType: '',
        ReasonKey: '',
        Reason: '',
      })],
    });
    expect(el.querySelector('.jfh-discovery-no-poster')).not.toBeNull();
    expect(el.querySelector('.jfh-discovery-card-title')!.textContent).toBe('Unknown');
    expect(el.querySelectorAll('.jfh-discovery-tag')).toHaveLength(0);
    expect(el.querySelector('.jfh-discovery-reason')).toBeNull();
    expect(el.querySelector('.jfh-discovery-score-text')!.textContent).toContain('90%');
  });

  test('a title-less card escapes a tampered poster URL and flips on click', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ Title: 'x" onerror="evil', Overview: '' })] });
    const poster = el.querySelector('.jfh-discovery-card-poster')!;
    poster.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(poster.classList.contains('flipped')).toBe(true);
  });

  test('an already-requested card is disabled and offers no dismiss', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ AlreadyRequested: true })] });
    const btn = el.querySelector('.jfh-discovery-btn') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(btn.textContent).toContain('Requested');
    expect(el.querySelector('.jfh-discovery-btn-dismiss')).toBeNull();
  });

  test('flip links open only safe URLs at the sink', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec()] });
    const link = el.querySelector('.jfh-discovery-flip-link[data-href]') as HTMLElement;
    expect(link.dataset.href).toContain('themoviedb.org');
    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(openSpy).toHaveBeenCalledWith(expect.stringContaining('themoviedb.org'), '_blank', 'noopener,noreferrer');
    link.dataset.href = 'javascript:alert(1)';
    link.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(openSpy).toHaveBeenCalledTimes(1);
    openSpy.mockRestore();
  });

  test('a raw reason with slashes stays breakable and escaping holds', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    ui.renderCards(el, { Recommendations: [rec({ ReasonKey: '', Reason: 'actors/directors <b>' })] });
    const reason = el.querySelector('.jfh-discovery-reason')!.textContent!;
    expect(reason).toContain('actors/\u200Bdirectors');
    expect(reason).toContain('<b>');
  });

  test('a Seerr base URL from config adds the Seerr link; unsafe values are rejected', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'ExternalLinks': { SeerrUrl: 'https://seerr.example/' } }, { userId: 'u1' });
    ui.loadExternalLinksConfig();
    await flush();
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec()] });
    const hrefs = Array.from(el.querySelectorAll('.jfh-discovery-flip-link')).map((l) => (l as HTMLElement).dataset.href);
    expect(hrefs.some((h) => h!.startsWith('https://seerr.example/movie/'))).toBe(true);

    const ui2 = freshDiscoveryHelpers();
    ajaxRouter({ 'ExternalLinks': { SeerrUrl: 'javascript:alert(1)' } }, { userId: 'u1' });
    ui2.loadExternalLinksConfig();
    await flush();
    const el2 = document.createElement('div');
    document.body.appendChild(el2);
    ui2.renderCards(el2, { Recommendations: [rec()] });
    const hrefs2 = Array.from(el2.querySelectorAll('.jfh-discovery-flip-link')).map((l) => (l as HTMLElement).dataset.href);
    expect(hrefs2.some((h) => h!.includes('javascript'))).toBe(false);
    expect(hrefs2).toHaveLength(1);
  });
});

describe('request + dismiss edge branches', () => {
  function cardFixture(ui: UiBridge, over: Record<string, unknown> = {}) {
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec(over)] });
    return el;
  }

  test('handleRequest ignores disabled buttons and incomplete dataset', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const disabled = document.createElement('button');
    disabled.disabled = true;
    disabled.dataset.tmdb = '1';
    disabled.dataset.type = 'movie';
    expect(() => ui.handleRequest({ currentTarget: disabled })).not.toThrow();
    const empty = document.createElement('button');
    expect(() => ui.handleRequest({ currentTarget: empty })).not.toThrow();
    expect(document.querySelector('#jfhDiscoveryPopup')).toBeNull();
  });

  test('fetchPermissionsAndRequest clears a stale reset timer before starting', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'RequestPermissions': { CanRequest: true, Profiles: [] } }, { userId: 'u1' });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement & { _resetTimer?: unknown };
    btn._resetTimer = setTimeout(() => { btn.textContent = 'STALE'; }, 10);
    ui.fetchPermissionsAndRequest(603, 'movie', btn);
    await vi.advanceTimersByTimeAsync(20);
    await flush();
    expect(btn.textContent).not.toBe('STALE');
    expect(btn._resetTimer).toBeNull();
  });

  test('a denied permission shows a failed button that auto-resets with the dismiss sibling', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    const dismiss = el.querySelector('.jfh-discovery-btn-dismiss') as HTMLButtonElement;
    ui.setDismissSiblingHidden(btn, true);
    ui.decideAndSubmit(603, 'movie', btn, { CanRequest: false, DeniedReason: 'Nope' });
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    expect(document.querySelector('.jfh-discovery-toast')!.textContent).toContain('Nope');
    await vi.advanceTimersByTimeAsync(3000);
    expect(btn.textContent).toBe('Request');
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(false);
    expect(btn.disabled).toBe(false);
    expect(dismiss.style.display).toBe('');
  });

  test('opening the profile picker twice replaces the previous popup', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    const profiles = [
      { ServerId: 's1', ProfileId: 'p1', RootFolder: '/a', ProfileName: 'One', ServerName: 'A', IsDefault: false },
      { ServerId: 's2', ProfileId: 'p2', RootFolder: '/b', ProfileName: 'Two', ServerName: 'B', IsDefault: true },
    ];
    ui.showProfilePopup(603, 'movie', btn, profiles);
    ui.showProfilePopup(603, 'movie', btn, profiles);
    expect(document.querySelectorAll('#jfhDiscoveryPopup')).toHaveLength(1);
    expect(document.querySelectorAll('.jfh-discovery-popup-item')).toHaveLength(2);
  });

  test('cancelling the profile picker closes it and restores the dismiss sibling', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    const dismiss = el.querySelector('.jfh-discovery-btn-dismiss') as HTMLButtonElement;
    ui.setDismissSiblingHidden(btn, true);
    ui.showProfilePopup(603, 'movie', btn, [
      { ServerId: 's1', ProfileId: 'p1', RootFolder: '/a', ProfileName: 'One', IsDefault: false },
    ]);
    (document.querySelector('.jfh-discovery-popup-cancel') as HTMLElement).click();
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
    expect(dismiss.style.display).toBe('');
  });

  test('closeDiscoveryPopup tolerates a null trigger and missing overlay', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    expect(() => ui.closeDiscoveryPopup(null)).not.toThrow();
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    ui.closeDiscoveryPopup(btn);
    expect(document.activeElement).toBe(btn);
  });

  test('a successful request fades the card out and shows the empty state', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ '/Request': { Success: true } }, { userId: 'u1' });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    ui.submitRequest(603, 'movie', null, null, null, btn);
    await flush();
    expect(btn.textContent).toContain('Requested');
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(400);
    expect(el.querySelector('.jfh-discovery-card')).toBeNull();
    expect(el.textContent).toContain('No suggestions available yet');
  });

  test('a rejected submitRequest marks the button failed and resets later', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { '/Request': new Error('boom') } });
    const el = cardFixture(ui);
    const btn = el.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLButtonElement;
    ui.submitRequest(603, 'movie', null, null, null, btn);
    await flush();
    expect(btn.classList.contains('jfh-discovery-btn-failed')).toBe(true);
    await vi.advanceTimersByTimeAsync(3000);
    expect(btn.textContent).toBe('Request');
    expect(btn.disabled).toBe(false);
  });

  test('handleDismissClick ignores disabled buttons and incomplete dataset', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const disabled = document.createElement('button');
    disabled.disabled = true;
    disabled.dataset.tmdb = '1';
    disabled.dataset.type = 'movie';
    ui.handleDismissClick({ currentTarget: disabled });
    const empty = document.createElement('button');
    ui.handleDismissClick({ currentTarget: empty });
    expect(document.getElementById('jfhDiscoveryPopup')).toBeNull();
  });

  test('the dismiss confirmation interpolates the title and executes on confirm', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ '/Dismiss': {} }, { userId: 'u1' });
    const el = cardFixture(ui);
    const dismiss = el.querySelector('.jfh-discovery-btn-dismiss') as HTMLButtonElement;
    ui.handleDismissClick({ currentTarget: dismiss });
    expect(document.querySelector('.jfh-discovery-popup-subtitle')!.textContent).toContain('The Matrix');
    (document.querySelector('.jfh-discovery-popup-item') as HTMLElement).click();
    await flush();
    await vi.advanceTimersByTimeAsync(300);
    expect(el.querySelector('.jfh-discovery-card')).toBeNull();
    expect(el.textContent).toContain('No suggestions available yet');
  });

  test('a title-less dismiss confirmation uses the generic text and replaces popups', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const btn = document.createElement('button');
    document.body.appendChild(btn);
    ui.showDismissConfirmation(603, 'movie', 'First', btn);
    ui.showDismissConfirmation(603, 'movie', '', btn);
    expect(document.querySelectorAll('#jfhDiscoveryPopup')).toHaveLength(1);
    expect(document.querySelector('.jfh-discovery-popup-subtitle')!.textContent).toContain('hidden from future suggestions');
    ui.closeDiscoveryPopup(btn);
  });
});

describe('probe + own-fetch residual branches', () => {
  test('a shared own fetch dropped by a generation bump renders nowhere', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    const h2 = document.createElement('div');
    document.body.appendChild(h1);
    document.body.appendChild(h2);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h2, true);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Recommendations: [rec({ Title: 'stale' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')).toBeNull();
    expect(h2.querySelector('.jfh-discovery-card-title')).toBeNull();
  });

  test('a shared own fetch skips a host that became a tab shell', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    const h2 = document.createElement('div');
    document.body.appendChild(h1);
    document.body.appendChild(h2);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h2, true);
    h2.innerHTML = '<div class="jfh-discovery-tabs"></div>';
    pending[0].resolve({ Recommendations: [rec({ Title: 'shared' })] });
    await flush();
    expect(h1.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('shared');
    expect(h2.querySelector('.jfh-discovery-card-title')).toBeNull();
    expect(h2.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('a shared own fetch dropped by a generation bump renders no error either', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h1, true);
    ui.invalidateDiscoveryResult();
    pending[0].reject({ status: 500 });
    await flush();
    expect(h1.textContent).not.toContain('Could not load');

    const h2 = document.createElement('div');
    document.body.appendChild(h2);
    ui.renderOwnTab(h2, true);
    ui.renderOwnTab(h2, true);
    pending[1].reject({ status: 500 });
    await flush();
    expect(h2.textContent).toContain('Could not load discovery suggestions');
  });

  test('a non-shared own fetch skips a detached container on success', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    h1.remove();
    pending[0].resolve({ Recommendations: [rec({ Title: 'gone' })] });
    await flush();
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });

  test('renderOwnError without a cache still refuses to overwrite a tab shell', () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1' });
    const el = document.createElement('div');
    el.innerHTML = '<div class="jfh-discovery-tabs"></div>';
    document.body.appendChild(el);
    ui.renderOwnError(el, { status: 500 });
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    expect(el.textContent).not.toContain('Could not load');
  });

  test('a linked:false probe payload renders the not-linked safety net when consumed directly', async () => {
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter({ 'Discovery/My/Trakt': { Linked: false }, 'Discovery/My': { Recommendations: [rec()] } }, { userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    const host = document.createElement('div');
    document.body.appendChild(host);
    const before = calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My/Trakt').length;
    ui.renderTraktPersonal(host, false);
    await flush();
    expect(host.textContent).toContain('not linked');
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My/Trakt')).toHaveLength(before);
  });

  test('a stale personal-tab success is dropped', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktPersonal(host, true);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Linked: true, Result: { Recommendations: [rec({ Title: 'stale' })] } });
    await flush();
    expect(host.querySelector('.jfh-discovery-card-title')).toBeNull();
  });

  test('a stale trending-tab success is dropped', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktTrending(host, true);
    ui.invalidateDiscoveryResult();
    pending[0].resolve({ Recommendations: [rec({ Title: 'stale' })] });
    await flush();
    expect(host.querySelector('.jfh-discovery-card-title')).toBeNull();
  });

  test('a superseded probe neither upgrades nor re-renders', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    ui.renderDiscovery(el, true);
    const traktReqs = pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt');
    expect(traktReqs).toHaveLength(2);
    traktReqs[0].resolve({ Linked: true, Result: { Recommendations: [rec({ Title: 'old' })] } });
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    traktReqs[1].resolve({ Linked: true, Result: { Recommendations: [rec({ Title: 'new' })] } });
    pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My').forEach((p) => p.resolve({ Recommendations: [rec()] }));
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('a probe that resolves after its timeout never upgrades the shell', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await vi.advanceTimersByTimeAsync(12000);
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
  });

  test('the pending slot is not shown after an account switch or a detach', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    await vi.advanceTimersByTimeAsync(300);
    expect(el.querySelector('.jfh-trakt-pending')).toBeNull();

    const ui2 = freshDiscoveryHelpers();
    deferredAjaxRouter({ userId: 'u1' });
    const el2 = makeMarker();
    ui2.renderDiscovery(el2, true);
    el2.remove();
    await vi.advanceTimersByTimeAsync(300);
    expect(el2.querySelector('.jfh-trakt-pending')).toBeNull();
    expect(pending.length).toBeGreaterThan(0);
  });

  test('a settled same-user probe re-renders the shell directly on the next mount', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({
      'Trakt/Trending': { Recommendations: [rec({ Title: 'trendy' })] },
      'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec()] } },
      'Discovery/My': { Recommendations: [rec({ Title: 'own' })] },
    }, { userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    ui.renderDiscovery(el, false);
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    expect(el.querySelector('.jfh-discovery-tab-host')!.querySelector('.jfh-discovery-card-title')).not.toBeNull();
  });

  test('the trending preload skips when a fresh cache, an empty user, or its own flight exists', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My').forEach((p) => p.resolve({ Recommendations: [rec()] }));
    await flush();
    const trendingCount = () => pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt/Trending').length;
    expect(trendingCount()).toBe(1);
    ui.renderShell(el, true);
    expect(trendingCount()).toBe(1);

    (globalThis as any).ApiClient.getCurrentUserId = () => '';
    ui.renderShell(el, true);
    expect(trendingCount()).toBe(1);
  });

  test('an in-flight trending preload for the same user is not duplicated', async () => {
    const ui = freshDiscoveryHelpers();
    let resolveTrending: ((v: unknown) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Trakt/Trending')) {
          return new Promise((resolve) => { resolveTrending = resolve; });
        }
        if (url.includes('Trakt')) {
          return Promise.resolve({ Linked: true, Result: { Recommendations: [rec({ Title: 'probe' })] } });
        }
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' })] });
      },
    };
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    await flush();
    expect(resolveTrending).not.toBeNull();
    ui.renderShell(el, true);
    expect(typeof resolveTrending).toBe('function');
    resolveTrending!({ Recommendations: [rec({ Title: 'trendy' })] });
    await flush();
    (el.querySelector('[data-tab="trending"]') as HTMLElement).click();
    await flush();
    expect(el.querySelector('.jfh-discovery-tab-host')!.querySelector('.jfh-discovery-card-title')!.getAttribute('title')).toBe('trendy');
  });
});

describe('probe + fetch top-ups', () => {
  test('a superseded probe rejection is ignored', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    ui.renderDiscovery(el, true);
    const traktReqs = pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt');
    traktReqs[0].reject({ status: 500 });
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).toBeNull();
    traktReqs[1].resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    pending.filter((p) => p.url === '/JellyfinHelper/Discovery/My').forEach((p) => p.resolve({ Recommendations: [rec()] }));
    await flush();
    expect(el.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('a probe resolving after the container detached is dropped', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    const traktReq = pending.find((p) => p.url === '/JellyfinHelper/Discovery/My/Trakt')!;
    el.remove();
    traktReq.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
    await flush();
    expect(document.querySelector('.jfh-discovery-tabs')).toBeNull();
  });

  test('the pending slot never duplicates while one is already shown', async () => {
    const ui = freshDiscoveryHelpers();
    deferredAjaxRouter({ userId: 'u1' });
    const el = makeMarker();
    ui.renderDiscovery(el, true);
    ui.renderDiscovery(el, true);
    await vi.advanceTimersByTimeAsync(300);
    expect(el.querySelectorAll('.jfh-trakt-pending')).toHaveLength(1);
  });

  test('a shared own fetch skips a detached host on failure', async () => {
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    const h1 = document.createElement('div');
    const h2 = document.createElement('div');
    document.body.appendChild(h1);
    ui.renderOwnTab(h1, true);
    ui.renderOwnTab(h2, true);
    pending[0].reject({ status: 500 });
    await flush();
    expect(h1.textContent).toContain('Could not load discovery suggestions');
    expect(h2.querySelector('.jfh-discovery-spinner')).not.toBeNull();
  });

  test('a failed personal fetch renders the trakt error', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { 'Discovery/My/Trakt': { status: 500 } } });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktPersonal(host, true);
    await flush();
    expect(host.textContent).toContain('Could not load discovery suggestions');
  });

  test('a failed trending fetch renders the trakt error', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({}, { userId: 'u1', failures: { 'Trakt/Trending': { status: 500 } } });
    const host = document.createElement('div');
    document.body.appendChild(host);
    ui.renderTraktTrending(host, true);
    await flush();
    expect(host.textContent).toContain('Could not load discovery suggestions');
  });
});

// The mount machinery starts body observers and window listeners, so this
// block runs last and swaps in a clean body before every test: observers bound
// to a previous body cannot see the next test's DOM, and replaceState-based
// navigation never fires a hashchange into a leaked watcher.
describe('bootstrap + mount machinery', () => {
  beforeEach(async () => {
    setHash('#/nowhere');
    await vi.advanceTimersByTimeAsync(80);
    await flush();
    document.body = document.createElement('body');
  });

  function buildSidebar() {
    const sidebar = document.createElement('div');
    sidebar.className = 'mainDrawer-scrollContainer';
    const media = document.createElement('div');
    media.className = 'libraryMenuOptions';
    sidebar.appendChild(media);
    document.body.appendChild(sidebar);
    return { sidebar, media };
  }

  function homeRoutes(own: unknown = { Recommendations: [rec({ Title: 'own', TmdbId: 7 })] }) {
    return {
      'Trakt/Trending': { Recommendations: [rec({ Title: 'trendy' })] },
      'Discovery/My/Trakt': { Linked: true, Result: { Recommendations: [rec({ Title: 'perso' })] } },
      'ExternalLinks': {},
      'Translations': {},
      'Discovery/My': own,
    };
  }

  function makeStrandedPanel(index: string) {
    const panel = document.createElement('div');
    panel.id = 'customTab_' + index;
    panel.dataset.index = index;
    const marker = document.createElement('div');
    marker.className = 'jellyfinhelper discovery';
    panel.appendChild(marker);
    document.body.appendChild(panel);
    return panel;
  }

  test('a home bootstrap mounts the panel and injects the sidebar', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(homeRoutes(), { userId: 'u1' });
    const { sidebar } = buildSidebar();
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(marker.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    expect(sidebar.querySelector('a')).not.toBeNull();
    expect(document.getElementById('jfhelper-discovery-styles')).not.toBeNull();
    expect(calls.some((c) => c.url === '/JellyfinHelper/Discovery/My')).toBe(true);
  });

  test('an empty probe keeps the gate open, skips the sidebar, and re-bootstraps', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter({
      'ExternalLinks': {},
      'Translations': {},
      'Discovery/My': { Recommendations: [] },
    }, { userId: 'u1' });
    const { sidebar } = buildSidebar();
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(sidebar.querySelector('a')).toBeNull();
    expect(marker.querySelector('.jfh-discovery-container')).not.toBeNull();
    const first = calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My').length;
    ui.bootstrapDiscovery();
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My').length).toBeGreaterThan(first);
  });

  test('an off-home bootstrap does nothing at all', async () => {
    setHash('#/nowhere');
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(homeRoutes(), { userId: 'u1' });
    ui.bootstrapDiscovery();
    await flush();
    expect(calls).toHaveLength(0);
  });

  test('a second bootstrap while a probe is in flight is ignored', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    const pending = deferredAjaxRouter({ userId: 'u1' });
    ui.bootstrapDiscovery();
    ui.bootstrapDiscovery();
    ui.bootstrapDiscovery();
    expect(pending).toHaveLength(2);
    pending.find((p) => p.url.includes('Translations'))!.resolve({ discoveryTitle: 'T' });
    pending.find((p) => p.url.includes('ExternalLinks'))!.resolve({});
    await flush();
    expect(pending.some((p) => p.url.includes('Discovery/My'))).toBe(true);
  });

  test('mutations while mounts are gated defer until init opens the gate', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let resolveStrings: ((v: unknown) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Translations')) return new Promise((r) => { resolveStrings = r; });
        if (url.includes('ExternalLinks')) return Promise.resolve({});
        if (url.includes('Trakt')) return Promise.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' })] });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    document.body.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(100);
    expect(marker.querySelector('.jfh-discovery-container')).toBeNull();
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(100);
    expect(marker.querySelector('.jfh-discovery-container')).toBeNull();
    resolveStrings!({});
    await flush();
    await vi.advanceTimersByTimeAsync(100);
    expect(marker.querySelector('.jfh-discovery-container')).not.toBeNull();
  });

  test('a rebuilt container is remounted while an unchanged one is left alone', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    ajaxRouter(homeRoutes(), { userId: 'u1' });
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(marker.querySelector('.jfh-discovery-tabs')).not.toBeNull();
    const firstChild = marker.firstElementChild;
    document.body.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(50);
    expect(marker.firstElementChild).toBe(firstChild);
    marker.remove();
    const marker2 = makeMarker(true);
    document.body.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(50);
    expect(marker2.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('a mounted background refresh targets the live own-tab host', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let ownFetches = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Translations') || url.includes('ExternalLinks')) return Promise.resolve({});
        if (url.includes('Trakt/Trending')) return Promise.resolve({ Recommendations: [rec()] });
        if (url.includes('Trakt')) return Promise.resolve({ Linked: true, Result: { Recommendations: [rec()] } });
        ownFetches++;
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' + ownFetches, TmdbId: 700 + ownFetches, Score: ownFetches / 10 })] });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    const c1 = ownFetches;
    const title = () => marker.querySelector('.jfh-discovery-tab-host')!.querySelector('.jfh-discovery-card-title')!.getAttribute('title');
    expect(title()).toBe('own' + c1);
    ui.maybeRefreshInBackground(marker);
    await flush();
    expect(ownFetches).toBe(c1 + 1);
    expect(title()).toBe('own' + (c1 + 1));
    expect(marker.querySelector('.jfh-discovery-tabs')).not.toBeNull();
  });

  test('without trakt the background refresh repaints the mounted marker', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let ownFetches = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Translations') || url.includes('ExternalLinks')) return Promise.resolve({});
        if (url.includes('Trakt')) return Promise.reject({ status: 403 });
        ownFetches++;
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' + ownFetches, TmdbId: 700 + ownFetches, Score: ownFetches / 10 })] });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(marker.querySelector('.jfh-discovery-container')).not.toBeNull();
    const c1 = ownFetches;
    const title = () => marker.querySelector('.jfh-discovery-card-title')!.getAttribute('title');
    expect(title()).toBe('own' + c1);
    ui.maybeRefreshInBackground(marker);
    await flush();
    expect(ownFetches).toBe(c1 + 1);
    expect(title()).toBe('own' + (c1 + 1));
    expect(marker.querySelector('.jfh-discovery-tabs')).toBeNull();
  });

  test('request and dismiss refresh the mounted view after removing a card', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let ownFetches = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('Translations') || url.includes('ExternalLinks')) return Promise.resolve({});
        if (url.includes('RequestPermissions')) return Promise.resolve({ CanRequest: true, Profiles: [] });
        if (url.includes('/Request') || url.includes('/Dismiss')) return Promise.resolve({ Success: true });
        if (url.includes('Trakt')) return Promise.reject({ status: 403 });
        ownFetches++;
        const payload = ownFetches <= 2
          ? [rec({ Title: 'Alpha', TmdbId: 1 }), rec({ Title: 'Beta', TmdbId: 2 })]
          : ownFetches === 3 ? [rec({ Title: 'Beta', TmdbId: 2 })] : [];
        return Promise.resolve({ Recommendations: payload });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(marker.querySelectorAll('.jfh-discovery-card')).toHaveLength(2);
    (marker.querySelector('.jfh-discovery-btn:not(.jfh-discovery-btn-dismiss)') as HTMLElement).click();
    await flush();
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(400);
    await flush();
    expect(marker.querySelectorAll('.jfh-discovery-card')).toHaveLength(1);
    (marker.querySelector('.jfh-discovery-btn-dismiss') as HTMLElement).click();
    (document.querySelector('.jfh-discovery-popup-item') as HTMLElement).click();
    await flush();
    await vi.advanceTimersByTimeAsync(300);
    await flush();
    expect(marker.querySelectorAll('.jfh-discovery-card')).toHaveLength(0);
    expect(marker.textContent).toContain('No suggestions available');
  });

  test('a stalled waitForApi releases the gate so a later sign-in probes', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    delete (globalThis as any).ApiClient;
    ui.bootstrapDiscovery();
    await flush();
    await vi.advanceTimersByTimeAsync(153000);
    const calls = ajaxRouter(homeRoutes(), { userId: 'u1' });
    ui.bootstrapDiscovery();
    await flush();
    expect(calls.some((c) => c.url === '/JellyfinHelper/Discovery/My')).toBe(true);
  });

  test('a 403 probe is terminal for the session', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(homeRoutes(), { userId: 'u1', failures: { 'Discovery/My': { status: 403 } } });
    ui.bootstrapDiscovery();
    await flush();
    ui.bootstrapDiscovery();
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My')).toHaveLength(1);
  });

  test('a stale probe failure never disables the feature for the new user', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    const calls = ajaxRouter(homeRoutes(), { userId: 'u1', failures: { 'Discovery/My': { status: 403 } } });
    ui.probeDiscoveryAvailability('u0');
    await flush();
    ui.bootstrapDiscovery();
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My').length).toBeGreaterThanOrEqual(2);
  });

  test('an account switch resets the bootstrap gates and re-probes', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let user = 'u1';
    const calls = ajaxRouter(homeRoutes(), { userId: user });
    (globalThis as any).ApiClient.getCurrentUserId = () => user;
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    // Probe and the own-tab render share the same URL, so count the baseline first.
    const afterFirst = calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My').length;
    expect(afterFirst).toBeGreaterThanOrEqual(2);
    user = 'u2';
    ui.bootstrapDiscovery();
    await flush();
    expect(calls.filter((c) => c.url === '/JellyfinHelper/Discovery/My').length).toBeGreaterThan(afterFirst);
    expect(marker.querySelector('.jfh-discovery-container')).not.toBeNull();
  });

  test('a late Seerr URL re-renders the mounted view once it arrives', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let resolveSeerr: ((v: unknown) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('ExternalLinks')) return new Promise((r) => { resolveSeerr = r; });
        if (url.includes('Translations')) return Promise.resolve({});
        if (url.includes('Trakt')) return Promise.resolve({ Linked: false });
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' })] });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    expect(marker.querySelector('.jfh-discovery-container')).not.toBeNull();
    expect(marker.innerHTML).not.toContain('seerr.example');
    resolveSeerr!({ SeerrUrl: 'https://seerr.example' });
    await flush();
    expect(marker.innerHTML).toContain('https://seerr.example/movie/');
  });

  test('a stale Seerr completion never re-renders for the new account', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let resolveSeerr: ((v: unknown) => void) | null = null;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        if (url.includes('ExternalLinks')) return new Promise((r) => { resolveSeerr = r; });
        if (url.includes('Translations')) return Promise.resolve({});
        if (url.includes('Trakt')) return Promise.resolve({ Linked: false });
        return Promise.resolve({ Recommendations: [rec({ Title: 'own' })] });
      },
    };
    const marker = makeMarker(true);
    ui.bootstrapDiscovery();
    await flush();
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    resolveSeerr!({ SeerrUrl: 'https://seerr.example' });
    await flush();
    expect(marker.innerHTML).not.toContain('seerr.example');
  });

  test('a stale strings callback never fires the probe', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    let resolveStrings: ((v: unknown) => void) | null = null;
    const calls: string[] = [];
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        const url = String(o.url);
        calls.push(url);
        if (url.includes('Translations')) return new Promise((r) => { resolveStrings = r; });
        return Promise.resolve({});
      },
    };
    ui.bootstrapDiscovery();
    await flush();
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u2';
    resolveStrings!({});
    await flush();
    expect(calls.filter((u) => u === '/JellyfinHelper/Discovery/My')).toHaveLength(0);
  });

  test('a stranded panel is nudged once per cooldown up to the budget', async () => {
    setHash('#/home?tab=2');
    const ui = freshDiscoveryHelpers();
    ajaxRouter(homeRoutes(), { userId: 'u1' });
    const p1 = makeStrandedPanel('2');
    ui.bootstrapDiscovery();
    await flush();
    expect(document.getElementById('customTab_2')).toBeNull();
    expect(p1.isConnected).toBe(false);

    // The nudge's own hashchange queued rAF mounts; drain them before planting the
    // next panel so no stale watcher sees a stranded marker while its mount is pending.
    await vi.advanceTimersByTimeAsync(50);

    const p2 = makeStrandedPanel('2');
    await vi.advanceTimersByTimeAsync(50);
    expect(document.getElementById('customTab_2')).toBe(p2);

    await vi.advanceTimersByTimeAsync(1600);
    document.body.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(50);
    expect(document.getElementById('customTab_2')).toBeNull();

    for (let i = 0; i < 2; i++) {
      makeStrandedPanel('2');
      await vi.advanceTimersByTimeAsync(1600);
      document.body.appendChild(document.createElement('span'));
      await vi.advanceTimersByTimeAsync(50);
      expect(document.getElementById('customTab_2')).toBeNull();
    }

    const p5 = makeStrandedPanel('2');
    await vi.advanceTimersByTimeAsync(1600);
    document.body.appendChild(document.createElement('span'));
    await vi.advanceTimersByTimeAsync(50);
    expect(document.getElementById('customTab_2')).toBe(p5);
    p5.remove();
  });

  test('a stranded panel for another tab index is left untouched', async () => {
    setHash('#/home?tab=2');
    const ui = freshDiscoveryHelpers();
    ajaxRouter(homeRoutes(), { userId: 'u1' });
    const p7 = makeStrandedPanel('7');
    ui.bootstrapDiscovery();
    await flush();
    expect(document.getElementById('customTab_7')).toBe(p7);
  });

  test('no tab deep link means no nudge even with a stranded marker', async () => {
    setHash('#/home');
    const ui = freshDiscoveryHelpers();
    ajaxRouter(homeRoutes(), { userId: 'u1' });
    const panel = makeStrandedPanel('2');
    ui.bootstrapDiscovery();
    await flush();
    expect(document.getElementById('customTab_2')).toBe(panel);
  });

  test('a visible stranded sibling suppresses the nudge entirely', async () => {
    setHash('#/home?tab=2');
    const ui = freshDiscoveryHelpers();
    ajaxRouter(homeRoutes(), { userId: 'u1' });
    const stranded = makeStrandedPanel('2');
    const hiddenWrap = document.createElement('div');
    hiddenWrap.className = 'hide';
    const visible = document.createElement('div');
    visible.className = 'jellyfinhelper discovery';
    hiddenWrap.appendChild(visible);
    document.body.appendChild(hiddenWrap);
    Object.defineProperty(visible, 'offsetParent', { value: document.body, configurable: true });
    ui.bootstrapDiscovery();
    await flush();
    expect(stranded.isConnected).toBe(true);
    expect(stranded.querySelector('.jfh-discovery-container')).toBeNull();
    expect(location.hash).toBe('#/home?tab=2');
  });
});

describe('discovery residual branch fallbacks', () => {
  test('cards fall back to zero id and score for unparsable values', () => {
    const ui = freshDiscoveryHelpers();
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec({ TmdbId: 'nope', Score: undefined })] });
    expect(el.querySelector('.jfh-discovery-btn')!.getAttribute('data-tmdb')).toBe('0');
    expect(el.querySelector('.jfh-discovery-btn-dismiss')!.getAttribute('data-tmdb')).toBe('0');
    expect(el.querySelector('.jfh-discovery-score-text')!.textContent).toContain('0%');
  });

  test('a Seerr flip link falls back to a zero tmdb id for unparsable ids', async () => {
    const ui = freshDiscoveryHelpers();
    ajaxRouter({ 'ExternalLinks': { SeerrUrl: 'https://seerr.example/' } }, { userId: 'u1' });
    await ui.loadExternalLinksConfig();
    const el = document.createElement('div');
    document.body.appendChild(el);
    ui.renderCards(el, { Recommendations: [rec({ TmdbId: 'nope', Title: 'Odd' })] });
    const link = el.querySelector('.jfh-discovery-flip-link[data-href*="seerr.example"]');
    expect(link).not.toBeNull();
    expect(link!.getAttribute('data-href')).toBe('https://seerr.example/movie/0');
  });

  test('a 403 background refresh invalidates the cached result', async () => {
    const ui = freshDiscoveryHelpers();
    let calls = 0;
    (globalThis as any).ApiClient = {
      getUrl: (p: string) => p,
      getCurrentUserId: () => 'u1',
      accessToken: () => 't',
      ajax: (o: any) => {
        calls++;
        if (String(o.url).includes('Discovery/My')) return Promise.reject({ status: 403 });
        return Promise.resolve({});
      },
    };
    const el = document.createElement('div');
    document.body.appendChild(el);
    const payload = { Recommendations: [rec({ Title: 'cached', TmdbId: 11 })] };
    ui.setCachedDiscoveryResult(payload);
    ui.renderCards(el, payload);
    expect(ui.getCachedDiscoveryResult()).not.toBeNull();
    ui.maybeRefreshInBackground(el);
    await flush();
    expect(calls).toBe(1);
    expect(ui.getCachedDiscoveryResult()).toBeNull();
  });

  test('extractErrorMessage ignores parsed JSON without a message', () => {
    const ui = freshDiscoveryHelpers();
    expect(ui.extractErrorMessage({ responseText: '{"Detail":"nope"}' })).toBe('');
  });
});

