/**
 * Trash contract + path-safety at the API layer. tasks/hardening specs drive
 * the trash flow, but these lock the response shapes the UI binds to and the
 * containment guards that stop an admin from probing arbitrary host paths.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  });
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Trash/Folders shape: relative path → IsAbsolute false, Paths array', async () => {
  const res = await ctx.get(p('Trash/Folders'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Paths: string[]; IsAbsolute: boolean };
  expect(body.IsAbsolute).toBe(false);
  expect(Array.isArray(body.Paths)).toBe(true);
});

test('Trash/Contents shape: {UseTrash, RetentionDays, Libraries[]} PascalCase', async () => {
  const res = await ctx.get(p('Trash/Contents'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as {
    UseTrash: boolean;
    RetentionDays: number;
    Libraries: Array<{ LibraryPath: string; LibraryName: string; Items: unknown[] }>;
  };
  expect(typeof body.UseTrash).toBe('boolean');
  expect(typeof body.RetentionDays).toBe('number');
  expect(Array.isArray(body.Libraries)).toBe(true);
  for (const lib of body.Libraries) {
    expect(typeof lib.LibraryPath).toBe('string');
    expect(typeof lib.LibraryName).toBe('string');
    expect(Array.isArray(lib.Items)).toBe(true);
  }
});

test('Trash/CheckAccess rejects traversal and overlong paths (400)', async () => {
  const trav = await ctx.post(p('Trash/CheckAccess'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TrashFolderPath: 'a/../b' },
  });
  expect(trav.status()).toBe(400);

  const overlong = await ctx.post(p('Trash/CheckAccess'), {
    headers: { 'Content-Type': 'application/json' },
    data: { TrashFolderPath: 'a'.repeat(600) },
  });
  expect(overlong.status()).toBe(400);
  await assertPluginActive(ctx);
});

test('Trash/CheckAccess rejects missing body/field distinctly (400 {Error})', async () => {
  const missing = await ctx.post(p('Trash/CheckAccess'), {
    headers: { 'Content-Type': 'application/json' },
    data: {},
  });
  expect(missing.status()).toBe(400);
  expect((await missing.json()) as { Error: string }).toHaveProperty('Error');
  await assertPluginActive(ctx);
});

test('Trash/Relocate error-body contract: traversal → bare string, missing field → {Error}', async () => {
  const empty = await ctx.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: {},
  });
  expect(empty.status()).toBe(400);
  expect((await empty.json()) as { Error: string }).toHaveProperty('Error');

  const missing = await ctx.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: { OldTrashPath: '.jellyfin-trash' },
  });
  expect(missing.status()).toBe(400);
  expect((await missing.json()) as { Error: string }).toHaveProperty('Error');

  const traversal = await ctx.post(p('Trash/Relocate'), {
    headers: { 'Content-Type': 'application/json' },
    data: { OldTrashPath: '.jellyfin-trash', NewTrashPath: '../x' },
  });
  expect(traversal.status()).toBe(400);
  expect((await traversal.text()).trim()).toContain('Path traversal not allowed');
  await assertPluginActive(ctx);
});
