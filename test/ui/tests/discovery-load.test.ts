import { afterAll, describe, expect, test, vi } from 'vitest';
import { loadDiscovery, reloadDiscoveryBundle, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

// The browser suite checks layout behavior because it requires real measurements.
describe('discovery sidebar bundle', () => {
  test('loads without throwing and arms the init guard', () => {
    expect(() => loadDiscovery()).not.toThrow();
    expect((globalThis as any).window.__jfhDiscoverySidebarInitialized).toBe(true);
  });

  test('second evaluation is a no-op through the guard', () => {
    const addListener = vi.spyOn(window, 'addEventListener');
    expect(() => reloadDiscoveryBundle()).not.toThrow();
    expect(addListener).not.toHaveBeenCalled();
    addListener.mockRestore();
    expect((globalThis as any).window.__jfhDiscoverySidebarInitialized).toBe(true);
  });
});
