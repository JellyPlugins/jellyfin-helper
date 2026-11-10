import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, AjaxCall, flush } from '../src/apiStub.js';
import { freshDiscoveryHelpers, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

const FIRST = '00000000-0000-0000-0000-000000000001';
const SECOND = '00000000-0000-0000-0000-000000000002';

class VisibilityObserver {
  static instances: VisibilityObserver[] = [];
  readonly targets = new Set<Element>();
  active = true;

  constructor(private readonly callback: IntersectionObserverCallback) {
    VisibilityObserver.instances.push(this);
  }

  observe(target: Element) { this.targets.add(target); }
  disconnect() { this.active = false; }

  show() {
    if (!this.active) return;
    const entries = Array.from(this.targets, target => ({ target, isIntersecting: true, intersectionRatio: 1 }) as IntersectionObserverEntry);
    this.callback(entries, this as unknown as IntersectionObserver);
  }
}

describe('Discovery delivery-bound impressions', () => {
  let ui: UiBridge;
  let host: HTMLElement;
  let calls: AjaxCall[];

  beforeEach(() => {
    document.body.innerHTML = '';
    window.location.hash = '#/home';
    calls = ajaxRouter({ '/Impressions': undefined });
    (globalThis as any).ApiClient.getCurrentUserId = () => 'user-1';
    VisibilityObserver.instances = [];
    vi.stubGlobal('IntersectionObserver', VisibilityObserver);
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({ x: 0, y: 0, left: 0, top: 0, right: 100, bottom: 100, width: 100, height: 100, toJSON: () => ({}) });
    ui = freshDiscoveryHelpers();
    host = document.createElement('div');
    document.body.appendChild(host);
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  function render(deliveryId: string | undefined, ids = [1], expiresAt?: string) {
    ui.renderCards(host, {
      DeliveryId: deliveryId,
      DeliveryExpiresAtUtc: expiresAt,
      Recommendations: ids.map(TmdbId => ({ TmdbId, MediaType: 'movie', Title: `Movie ${TmdbId}`, Score: 0.7, Genres: ['Drama'], AlreadyRequested: false })),
    });
  }

  async function show() {
    VisibilityObserver.instances.at(-1)!.show();
    vi.advanceTimersByTime(1);
    await flush();
  }

  function posts() { return calls.filter(call => call.url.endsWith('/Impressions')); }

  function freshDelivery() {
    return { DeliveryId: SECOND, DeliveryExpiresAtUtc: new Date(Date.now() + 300_000).toISOString(),
      Recommendations: [{ TmdbId: 1, MediaType: 'movie', Title: 'Movie 1', Score: 0.7, Genres: ['Drama'], AlreadyRequested: false }] };
  }

  test('reports only the visible delivery and deduplicates successful visibility callbacks', async () => {
    render(FIRST);
    await show();
    await show();
    expect(posts()).toHaveLength(1);
    expect(posts()[0].body).toEqual({ DeliveryId: FIRST, Items: [{ TmdbId: 1, MediaType: 'movie' }] });
  });

  test('an identical card in a new delivery carries the new token, not the previous snapshot', async () => {
    render(FIRST);
    await show();
    render(SECOND);
    await show();
    expect(posts().map(call => (call.body as any).DeliveryId)).toEqual([FIRST, SECOND]);
  });

  test('a rerender cancels pending old visibility and schedules the new delivery', async () => {
    render(FIRST);
    VisibilityObserver.instances.at(-1)!.show();
    render(SECOND);
    await show();
    expect(posts()).toHaveLength(1);
    expect((posts()[0].body as any).DeliveryId).toBe(SECOND);
  });

  test('an action renews observation for remaining cards without recording the consumed card', async () => {
    render(FIRST, [1, 2]);
    const old = VisibilityObserver.instances.at(-1)!;
    ui.consumeDiscoveryItem(1, 'movie');
    expect(old.active).toBe(false);
    expect(VisibilityObserver.instances.at(-1)).not.toBe(old);
    await show();
    expect(posts().map(call => call.body)).toEqual([{ DeliveryId: FIRST, Items: [{ TmdbId: 2, MediaType: 'movie' }] }]);
  });

  test('protected old cards and refreshed siblings are sent in separate delivery batches', async () => {
    render(FIRST, [1, 2]);
    const firstCard = host.querySelector('.jfh-discovery-card')!;
    vi.spyOn((window as any).JellyfinHelperUi, 'ownsDialogTrigger').mockImplementation((card: unknown) => card === firstCard);
    render(SECOND, [1, 2]);
    await show();
    expect(posts().map(call => call.body)).toEqual([
      { DeliveryId: FIRST, Items: [{ TmdbId: 1, MediaType: 'movie' }] },
      { DeliveryId: SECOND, Items: [{ TmdbId: 2, MediaType: 'movie' }] },
    ]);
  });

  test.each(['request', 'dismiss'])('a %s action carries its protected card delivery even without an impression', async action => {
    calls = ajaxRouter({ '/Request': { Success: true }, '/Dismiss': { Success: true } });
    (globalThis as any).ApiClient.getCurrentUserId = () => 'user-1';
    render(FIRST, [1, 2]);
    const protectedCard = host.querySelector('.jfh-discovery-card')!;
    vi.spyOn((window as any).JellyfinHelperUi, 'ownsDialogTrigger').mockImplementation((card: unknown) => card === protectedCard);
    render(SECOND, [1, 2]);
    const button = protectedCard.querySelector(`[data-jfh-action="${action}"]`)!;
    if (action === 'request') ui.submitRequest(1, 'movie', null, null, null, button);
    else ui.executeDismiss(1, 'movie', button);
    await flush();
    const endpoint = action === 'request' ? '/Request' : '/Dismiss';
    expect(calls.filter(call => call.url.endsWith(endpoint)).map(call => call.body)).toEqual([{ TmdbId: 1, MediaType: 'movie', DeliveryId: FIRST }]);
    expect(posts()).toHaveLength(0);
  });

  test('a late old acknowledgement cannot undo acknowledgement of the new delivery', async () => {
    let resolveOld!: (value: unknown) => void;
    const old = new Promise(resolve => { resolveOld = resolve; });
    const client = (globalThis as any).ApiClient;
    const ajax = client.ajax;
    client.ajax = (options: any) => {
      const response = ajax(options);
      return JSON.parse(options.data).DeliveryId === FIRST ? old : response;
    };
    render(FIRST);
    await show();
    render(SECOND);
    await show();
    resolveOld(undefined);
    await flush();
    await show();
    expect(posts()).toHaveLength(2);
  });

  test('failed feedback writes remain retryable', async () => {
    calls = ajaxRouter({}, ['/Impressions']);
    (globalThis as any).ApiClient.getCurrentUserId = () => 'user-1';
    render(FIRST);
    await show();
    await show();
    expect(posts()).toHaveLength(2);
  });

  test('expired cached cards acquire fresh server evidence before reporting visibility', async () => {
    calls = ajaxRouter({ '/JellyfinHelper/Discovery/My': freshDelivery(), '/Impressions': undefined });
    (globalThis as any).ApiClient.getCurrentUserId = () => 'user-1';
    render(FIRST, [1], new Date(Date.now() - 1).toISOString());
    await show();
    expect(posts()).toHaveLength(0);
    expect(calls.some(call => call.method === 'GET')).toBe(true);
    await show();
    expect(posts().map(call => (call.body as any).DeliveryId)).toEqual([SECOND]);
  });

  test('an evicted delivery triggers a fresh response instead of repeatedly sending the stale token', async () => {
    calls = ajaxRouter({ '/JellyfinHelper/Discovery/My': freshDelivery(), '/Impressions': undefined });
    const client = (globalThis as any).ApiClient;
    client.getCurrentUserId = () => 'user-1';
    const ajax = client.ajax;
    client.ajax = (options: any) => {
      const result = ajax(options);
      return options.type === 'POST' && JSON.parse(options.data).DeliveryId === FIRST ? Promise.reject({ status: 400 }) : result;
    };
    render(FIRST);
    await show();
    await show();
    expect(posts().map(call => (call.body as any).DeliveryId)).toEqual([FIRST, SECOND]);
    expect((host.querySelector('.jfh-discovery-card') as any)?._jfhDeliveryId).toBe(SECOND);
  });

  test('delivery refresh cannot commit across an account change', async () => {
    let finish!: (value: unknown) => void;
    const pending = new Promise(resolve => { finish = resolve; });
    const client = (globalThis as any).ApiClient;
    client.ajax = () => pending;
    render(FIRST, [1], new Date(Date.now() - 1).toISOString());
    await show();
    client.getCurrentUserId = () => 'user-2';
    finish(freshDelivery());
    await flush();
    expect((host.querySelector('.jfh-discovery-card') as any)._jfhDeliveryId).toBe(FIRST);
    expect(posts()).toHaveLength(0);
  });

  test('nonempty Trakt caches stop being fresh when their server delivery expires', () => {
    const data = freshDelivery();
    const entry = ui.makeTraktCacheEntry(data, 'user-1');
    expect(ui.traktCacheFresh(entry)).toBe(true);
    data.DeliveryExpiresAtUtc = new Date(Date.now() - 1).toISOString();
    expect(ui.traktCacheFresh(entry)).toBe(false);
  });

  test('missing delivery ids and stale account sessions never report visibility', async () => {
    render(undefined);
    await show();
    expect(posts()).toHaveLength(0);
    render(FIRST);
    VisibilityObserver.instances.at(-1)!.show();
    (globalThis as any).ApiClient.getCurrentUserId = () => 'user-2';
    vi.advanceTimersByTime(1);
    await flush();
    expect(posts()).toHaveLength(0);
  });
});
