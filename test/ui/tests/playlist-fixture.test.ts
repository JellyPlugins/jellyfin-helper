import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, test, vi } from 'vitest';
import { removeFixtureRecommendationPlaylists } from '../../e2e/setup/playlist-fixture.js';
import { REPO_ROOT } from '../src/loader.js';

const USER = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const FIRST = '11111111111111111111111111111111';
const SECOND = '22222222222222222222222222222222';
const NAME = 'jfh-cold-fixture';
const EXPECTED = '🎬 Recommended for ' + NAME;
const response = (body: unknown, status = 200) => ({ ok: () => status >= 200 && status < 300, status: () => status, json: async () => body });

describe('temporary-account recommendation playlist cleanup', () => {
  test('removes exact and numeric-suffix fixture playlists, not copies or another account’s playlists', async () => {
    const ctx = {
      get: vi.fn().mockResolvedValue(response({ Items: [
        { Id: FIRST, Name: EXPECTED }, { Id: SECOND, Name: EXPECTED + '42' },
        { Id: '33333333333333333333333333333333', Name: EXPECTED + '_Backup' },
        { Id: '44444444444444444444444444444444', Name: '🎬 Recommended for someone-else' },
        { Id: '55555555555555555555555555555555', Name: 'Weekend Watchlist' },
      ] })),
      delete: vi.fn().mockResolvedValue(response({}, 204)),
    };
    expect(await removeFixtureRecommendationPlaylists(ctx, USER, NAME)).toBe(2);
    expect(ctx.get).toHaveBeenCalledWith(`/Items?IncludeItemTypes=Playlist&Recursive=true&userId=${USER}`);
    expect(ctx.delete.mock.calls).toEqual([[`/Items/${FIRST}`], [`/Items/${SECOND}`]]);
  });

  test.each([401, 500])('failed lookups are errors, never an empty successful cleanup: %s', async status => {
    const ctx = { get: vi.fn().mockResolvedValue(response({}, status)), delete: vi.fn() };
    await expect(removeFixtureRecommendationPlaylists(ctx, USER, NAME)).rejects.toThrow(`lookup failed: ${status}`);
    expect(ctx.delete).not.toHaveBeenCalled();
  });

  test('failed deletion stays observable', async () => {
    const ctx = { get: vi.fn().mockResolvedValue(response({ Items: [{ Id: FIRST, Name: EXPECTED }] })), delete: vi.fn().mockResolvedValue(response({}, 500)) };
    await expect(removeFixtureRecommendationPlaylists(ctx, USER, NAME)).rejects.toThrow('deletion failed');
  });

  test.each(['http', 'transport'])('waits for every deletion before account teardown after a %s failure', async failure => {
    let completeSecond!: (value: ReturnType<typeof response>) => void;
    const second = new Promise<ReturnType<typeof response>>(resolve => { completeSecond = resolve; });
    const ctx = {
      get: vi.fn().mockResolvedValue(response({ Items: [{ Id: FIRST, Name: EXPECTED }, { Id: SECOND, Name: EXPECTED + '2' }] })),
      delete: vi.fn((url: string) => {
        if (url === `/Items/${FIRST}`) return failure === 'http' ? Promise.resolve(response({}, 500)) : Promise.reject(new Error('transport failure'));
        if (url === `/Items/${SECOND}`) return second;
        return Promise.resolve(response({}, 204));
      }),
    };
    const cleanup = removeFixtureRecommendationPlaylists(ctx, USER, NAME)
      .finally(() => ctx.delete(`/Users/${USER}`));
    const result = cleanup.then(() => null, error => error);
    await vi.advanceTimersByTimeAsync(0);
    try {
      expect(ctx.delete).toHaveBeenCalledWith(`/Items/${SECOND}`);
      expect(ctx.delete).not.toHaveBeenCalledWith(`/Users/${USER}`);
    } finally {
      completeSecond(response({}, 204));
    }
    const error = await result;
    expect(error).toBeInstanceOf(Error);
    expect(error.message).toContain(failure === 'http' ? 'deletion failed' : 'transport failure');
    expect(ctx.delete.mock.calls).toEqual([[`/Items/${FIRST}`], [`/Items/${SECOND}`], [`/Users/${USER}`]]);
  });

  test('validates every matching identity before deleting any playlist', async () => {
    const ctx = {
      get: vi.fn().mockResolvedValue(response({ Items: [{ Id: FIRST, Name: EXPECTED }, { Id: '../other', Name: EXPECTED + '2' }] })),
      delete: vi.fn().mockResolvedValue(response({}, 204)),
    };
    await expect(removeFixtureRecommendationPlaylists(ctx, USER, NAME)).rejects.toThrow('invalid item identity');
    expect(ctx.delete).not.toHaveBeenCalled();
  });

  test('missing Items and malformed item IDs cannot be mistaken for successful cleanup', async () => {
    const ctx = { get: vi.fn().mockResolvedValue(response({})), delete: vi.fn() };
    await expect(removeFixtureRecommendationPlaylists(ctx, USER, NAME)).rejects.toThrow('Items array');
    ctx.get.mockResolvedValue(response({ Items: [{ Id: '../other', Name: EXPECTED }] }));
    await expect(removeFixtureRecommendationPlaylists(ctx, USER, NAME)).rejects.toThrow('invalid item identity');
    expect(ctx.delete).not.toHaveBeenCalled();
  });

  test('empty results do not delete anything', async () => {
    const ctx = { get: vi.fn().mockResolvedValue(response({ Items: [] })), delete: vi.fn() };
    expect(await removeFixtureRecommendationPlaylists(ctx, USER, NAME)).toBe(0);
    expect(ctx.delete).not.toHaveBeenCalled();
  });

  test('the cold-start spec isolates sync and cleans owned resources before account deletion', () => {
    const source = readFileSync(join(REPO_ROOT, 'test/e2e/tests/discovery-my.api.spec.ts'), 'utf8');
    expect(source).toContain('SyncRecommendationsToPlaylist: false');
    expect(source).toContain('SyncRecommendationsToPlaylist: c.SyncRecommendationsToPlaylist');
    const cleanup = source.indexOf('await removeFixtureRecommendationPlaylists(admin, account.Id, name)');
    const accountDeletion = source.indexOf('admin.delete(`/Users/${account.Id}`)');
    expect(cleanup).toBeGreaterThanOrEqual(0);
    expect(accountDeletion).toBeGreaterThan(cleanup);
  });
});
