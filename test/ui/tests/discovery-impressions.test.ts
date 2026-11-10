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

  function render(deliveryId: string | undefined, ids = [1]) {
    ui.renderCards(host, {
      DeliveryId: deliveryId,
      Recommendations: ids.map(TmdbId => ({ TmdbId, MediaType: 'movie', Title: `Movie ${TmdbId}`, Score: 0.7, Genres: ['Drama'], AlreadyRequested: false })),
    });
  }

  async function show() {
    VisibilityObserver.instances.at(-1)!.show();
    vi.advanceTimersByTime(1);
    await flush();
  }

  function posts() { return calls.filter(call => call.url.endsWith('/Impressions')); }

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
