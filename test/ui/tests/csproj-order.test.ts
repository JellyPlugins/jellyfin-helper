import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, test } from 'vitest';
import { CONFIG_PAGE_MODULES } from '../src/jsModules.js';
import { REPO_ROOT } from '../src/loader.js';

describe('loader module order matches the .csproj compose order', () => {
  test('same files, same sequence', () => {
    const csproj = readFileSync(
      join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'Jellyfin.Plugin.JellyfinHelper.csproj'),
      'utf8',
    );
    const ordered = [...csproj.matchAll(/<JsModule Include="PluginPages\\js\\([^"]+)"\/>/g)].map((m) => m[1]);
    expect(ordered.length).toBeGreaterThan(0);
    expect(CONFIG_PAGE_MODULES).toEqual(ordered);
  });

  test('every composed file exists on disk', () => {
    const dir = join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'PluginPages', 'js');
    void dirname(fileURLToPath(import.meta.url));
    for (const f of CONFIG_PAGE_MODULES) {
      expect(readFileSync(join(dir, f), 'utf8').length).toBeGreaterThan(0);
    }
  });
});
