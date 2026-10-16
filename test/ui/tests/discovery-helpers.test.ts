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
    expect(cards.length).toBe(2);
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
    expect(items.length).toBe(2);
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
    expect(document.querySelectorAll('#jfhelper-discovery-styles').length).toBe(1);
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
    expect(items.length).toBe(1);
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
    expect(tabs.length).toBe(3);
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
    expect(calls.filter((c) => c.url.includes('RequestPermissions')).length).toBe(permCallsAfterFirst);
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
    expect(document.querySelectorAll('#jfhelper-popup-styles').length).toBe(1);
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

