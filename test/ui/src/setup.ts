import { afterAll, beforeAll, beforeEach, vi } from 'vitest';

beforeAll(() => {
  vi.useFakeTimers();
  window.location.hash = '#/nowhere';
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
