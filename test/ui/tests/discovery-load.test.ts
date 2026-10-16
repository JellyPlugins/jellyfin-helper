import { afterAll, describe, expect, test } from 'vitest';
import { loadDiscovery, reloadDiscoveryBundle, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

// The tab matrix itself needs real layout (offsetParent) and lives in the
// Playwright spec discovery-customtab.ui.spec.ts. Here the bundle must load
// cleanly with its double-init guard and stay inert off the home context.
describe('discovery sidebar bundle', () => {
  test('loads without throwing and arms the init guard', () => {
    expect(() => loadDiscovery()).not.toThrow();
    expect((globalThis as any).window.__jfhDiscoverySidebarInitialized).toBe(true);
  });

  test('second evaluation is a no-op through the guard', () => {
    expect(() => reloadDiscoveryBundle()).not.toThrow();
    expect((globalThis as any).window.__jfhDiscoverySidebarInitialized).toBe(true);
  });
});
