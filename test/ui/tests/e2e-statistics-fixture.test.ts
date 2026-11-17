import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { join } from 'node:path';
import { describe, expect, test, vi } from 'vitest';
import { REPO_ROOT } from '../src/loader.js';

const response = (body: unknown, status = 200) => ({ ok: () => status >= 200 && status < 300, status: () => status, json: async () => body });

function fixture(relativePath: string, ctx: Record<string, unknown>, fs: Record<string, unknown> = {}) {
  const before: Array<() => Promise<void>> = [];
  const after: Array<() => Promise<void>> = [];
  const setTimeout = vi.fn();
  const describeGroup = (_name: string, body: () => void) => body();
  const hooks = Object.assign(() => {}, {
    beforeAll: (body: () => Promise<void>) => before.push(body),
    afterAll: (body: () => Promise<void>) => after.push(body),
    afterEach: () => {},
    skip: () => {},
    setTimeout,
    describe: Object.assign(describeGroup, { serial: describeGroup }),
  });
  const fileSystem = {
    hasDocker: () => true,
    containerExists: () => true,
    containerFileExists: () => true,
    containerWriteFile: vi.fn(),
    containerRm: vi.fn(),
    execInContainer: vi.fn(() => ({ code: 0, stderr: '' })),
    q: (value: string) => value,
    ...fs,
  };
  const source = readFileSync(join(REPO_ROOT, relativePath), 'utf8');
  const withoutImports = source.replace(/^import[\s\S]*?from ['"][^'"]+['"];[ \t]*$/gm, '');
  const compiled = stripTypeScriptTypes(withoutImports, { mode: 'strip' });
  const bindings = { test: hooks, expect, apiContext: async () => ctx, loadAuth: () => ({}), p: (path: string) => path, sleep: async () => {}, ...fileSystem };
  new Function(...Object.keys(bindings), compiled)(...Object.values(bindings));
  return { before, after, setTimeout, fileSystem };
}

const path = 'test/e2e/tests/media-stats-fs.api.spec.ts';
const backup = { radarrInstances: [{ name: 'Radarr', url: 'http://arr.test', apiKey: 'fixture-only-radarr', libraries: 'Movies' }], sonarrInstances: [{ name: 'Sonarr', url: 'http://arr.test', apiKey: 'fixture-only-sonarr', libraries: 'Shows' }] };

function context(exported: () => Promise<ReturnType<typeof response>>) {
  return {
    get: vi.fn((url: string) => url.startsWith('Backup/') ? exported() : Promise.resolve(response({ ExcludedLibraries: 'Original' }))),
    put: vi.fn().mockResolvedValue(response({}, 204)),
    dispose: vi.fn(),
  };
}

describe('shipped statistics fixture setup and restoration', () => {
  test.each(['http', 'transport', 'incomplete'])('a %s backup failure never changes Arr settings or performs cleanup scans', async failure => {
    const ctx = context(async () => {
      if (failure === 'transport') throw new Error('backup transport failure');
      return response(failure === 'incomplete' ? { radarrInstances: [] } : {}, failure === 'http' ? 500 : 200);
    });
    const sut = fixture(path, ctx);
    await sut.before[0]();
    await expect(sut.before[1]()).rejects.toThrow();
    await sut.after[0]();
    expect(ctx.put).not.toHaveBeenCalled();
    expect(sut.fileSystem.containerWriteFile).not.toHaveBeenCalled();
    expect(ctx.get.mock.calls.some(([url]) => url.includes('ScanLibraries'))).toBe(false);
  });

  test('complete backup followed by a filesystem setup failure cleans owned files without a settings restore', async () => {
    const ctx = context(async () => response(backup));
    const sut = fixture(path, ctx, { execInContainer: () => ({ code: 1, stderr: 'fixture copy failed' }) });
    await sut.before[0]();
    await expect(sut.before[1]()).rejects.toThrow();
    await sut.after[0]();
    expect(ctx.put).not.toHaveBeenCalled();
    expect(sut.fileSystem.containerRm).toHaveBeenCalled();
  });

  test('an uncertain settings write still restores both complete original Arr lists', async () => {
    const ctx = context(async () => response(backup));
    ctx.put.mockResolvedValueOnce(response({}, 500));
    const sut = fixture(path, ctx);
    await sut.before[0]();
    await expect(sut.before[1]()).rejects.toThrow();
    await sut.after[0]();
    expect(ctx.put).toHaveBeenCalledTimes(2);
    expect(ctx.put.mock.calls[1][1].data).toMatchObject({
      ExcludedLibraries: 'Original',
      RadarrInstances: [{ ApiKey: 'fixture-only-radarr', Libraries: 'Movies' }],
      SonarrInstances: [{ ApiKey: 'fixture-only-sonarr', Libraries: 'Shows' }],
    });
  });

  test('the per-user observed seed hook covers the complete ten-refresh budget', async () => {
    const sut = fixture('test/e2e/tests/recommendations-peruser-fs.api.spec.ts', {}, { hasDocker: () => false });
    await sut.before[1]();
    expect(sut.setTimeout).toHaveBeenCalledWith(990_000);
  });
});
