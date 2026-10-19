import { afterAll, beforeAll, beforeEach, vi } from 'vitest';

// jsdom only ships "not implemented" shims for these; every call is logged to
// the virtual console. Production code uses them for scroll restore and
// auto-scroll, so replace them with inert stubs once per file. Assign via
// defineProperty so vi.spyOn (scroll-behavior assertions) can still wrap them.
function inertMethod(target: object, name: string): void {
  Object.defineProperty(target, name, {
    value: () => undefined,
    writable: true,
    configurable: true,
  });
}

// Install the fake clock before the test modules evaluate: Main.js's top-level
// bootstrap can schedule real setTimeouts during import, which beforeAll would
// install too late to capture.
vi.useFakeTimers();

beforeAll(() => {
  window.location.hash = '#/nowhere';
  inertMethod(window, 'scrollTo');
  inertMethod(window, 'scrollBy');
  inertMethod(Element.prototype, 'scrollIntoView');
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 'test-token',
    ajax: () => Promise.resolve({}),
  };
  vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
});

// The fake clock is installed once per file, so timers a test leaves pending
// (dirty-check debounces, band reveal/fade timers) would otherwise fire inside a
// later test's advanceTimersByTime and mutate that test's DOM - cross-test bleed.
beforeEach(() => {
  vi.clearAllTimers();
});

afterAll(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
