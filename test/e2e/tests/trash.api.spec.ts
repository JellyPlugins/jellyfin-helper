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
  const seed = await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { UseTrash: true, TrashFolderPath: '.jellyfin-trash', TrashRetentionDays: 30 },
  });
  expect(seed.ok(), `trash config seed failed: ${seed.status()}`).toBeTruthy();
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

test('a literal null body → clean 400 (never 500/NRE) on all three body-taking trash endpoints', async () => {
  // Existing tests post {} (a present-but-empty object) -> the field-blank branch
  // ("TrashFolderPath is required."). A genuinely null/absent body is DIFFERENT: on
  // an [ApiController], model-binding validation short-circuits with an RFC9110
  // ValidationProblemDetails envelope BEFORE the action's own request==null guard
  // ("Request body is required.") can run. Either way the wire contract is a clean
  // 400 - never a 500/NRE. Pin that for each endpoint, tolerating whichever 400
  // shape the framework produces (problem-details envelope OR the {Error} guard).
  for (const route of ['Trash/Relocate', 'Trash/CheckAccess', 'Trash/FoldersForPath']) {
    const res = await ctx.post(p(route), {
      headers: { 'Content-Type': 'application/json' },
      data: 'null',
    });
    expect(res.status(), `${route} null body must be a clean 400`).toBe(400);
    // Body is either the {Error:"Request body is required."} guard or a
    // problem-details {title,status,errors} envelope - both are acceptable 400s;
    // the point is that a null body neither 500s nor is silently accepted.
    const raw = await res.text();
    expect(raw.length, `${route} 400 must carry a body`).toBeGreaterThan(0);
  }
  await assertPluginActive(ctx);
});
