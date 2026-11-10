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

  test('the demo loads the shared UI runtime before Discovery', () => {
    const demo = readFileSync(join(REPO_ROOT, 'docs', 'index.html'), 'utf8');
    const parsedDemo = new DOMParser().parseFromString(demo, 'text/html');
    const scripts = Array.from(parsedDemo.querySelectorAll('script[src]'), script => script.getAttribute('src'));
    const runtime = scripts.indexOf('js/UiPrimitives.js');
    const discovery = scripts.indexOf('js/discovery-sidebar.js');
    expect(runtime).toBeGreaterThanOrEqual(0);
    expect(discovery).toBeGreaterThan(runtime);
    expect(demo).toContain("'js/Shared.js', 'js/UiPrimitives.js'");
  });

  // ComposeConfigPage.cs enforces these invariants at build time instead of a
  // literal list: Shared.js first (helpers exist before any tab uses them),
  // Main.js last (it closes the IIFE and wires routing), Codecs before
  // CodecsExplorer. Pin the same rules here so a loader reorder that still
  // matches a drifted csproj cannot slip past both guards.
  test('order satisfies the ComposeConfigPage invariants', () => {
    expect(CONFIG_PAGE_MODULES[0]).toBe('Shared.js');
    expect(CONFIG_PAGE_MODULES[CONFIG_PAGE_MODULES.length - 1]).toBe('Main.js');
    expect(CONFIG_PAGE_MODULES.indexOf('Codecs.js')).toBeLessThan(
      CONFIG_PAGE_MODULES.indexOf('CodecsExplorer.js'),
    );
  });

  // The build invariants live in ComposeConfigPage.cs; if that file stops
  // naming them, the guard above is silently checking rules nothing enforces.
  test('ComposeConfigPage.cs still declares the Shared-first / Main-last rules', () => {
    const compose = readFileSync(
      join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'BuildTasks', 'ComposeConfigPage.cs'),
      'utf8',
    );
    expect(compose).toContain('Shared.js');
    expect(compose).toContain('Main.js');
    expect(compose).toContain('CodecsExplorer.js');
  });
});

describe('Vitest MSBuild integration', () => {
  function vitestExec() {
    const project = new DOMParser().parseFromString(readFileSync(
      join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper.Tests', 'Jellyfin.Plugin.JellyfinHelper.Tests.csproj'),
      'utf8',
    ), 'application/xml');
    const exec = project.querySelector('Target[Name="VitestUi"] > Exec');
    expect(exec).not.toBeNull();
    return exec!;
  }

  test('successful test names containing error are not parsed as build failures', () => {
    expect(vitestExec().getAttribute('IgnoreStandardErrorWarningFormat')).toBe('true');
  });

  test('test and coverage failure exit codes remain fatal', () => {
    const exec = vitestExec();
    expect(exec.getAttribute('Command')).toContain('run test:coverage');
    expect(exec.getAttribute('IgnoreExitCode')).not.toBe('true');
    expect([null, 'false', 'ErrorAndStop']).toContain(exec.getAttribute('ContinueOnError'));
  });
});
