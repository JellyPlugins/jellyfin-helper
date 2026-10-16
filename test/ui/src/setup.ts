import { afterAll, beforeAll, vi } from 'vitest';

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

afterAll(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
