/**
 * Fallback script injection into index.html.
 *
 * The e2e stack ships NO File Transformation plugin, so the Discovery sidebar
 * can only appear if the plugin's *disk-write fallback* successfully patched
 * Jellyfin's index.html. This is the exact path that breaks for real users on
 * read-only web dirs — here the container's web dir is writable, so the fallback
 * MUST succeed. That makes this the end-to-end proof the unit tests can't give:
 * the tag is actually served by a live Jellyfin.
 *
 * Asserts:
 *   - GET /web/index.html contains the plugin's <script plugin="Jellyfin Helper">
 *     tag (fallback injection happened at server start).
 *   - The tag appears EXACTLY once (idempotent — ctor + startup hosted service
 *     re-inject, but RemovalRegex must prevent stacking).
 *   - The injected src is reachable and serves JavaScript.
 *   - The plugin stays Active throughout (injection didn't destabilise startup).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, assertPluginActive, sleep } from '../setup/api-client.ts';

const auth = loadAuth();

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(auth);
});

test.afterAll(async () => {
  await ctx.dispose();
});

/**
 * Fetch /web/index.html, retrying briefly. Injection runs at server start (the
 * plugin constructor and the DiscoverySidebarInjectionService hosted service),
 * which has long completed by the time the suite runs — but a short retry keeps
 * the test robust against any startup-timing edge without masking a real failure.
 */
async function fetchIndexHtml(): Promise<string> {
  let lastStatus = 0;
  for (let attempt = 0; attempt < 5; attempt++) {
    const res = await ctx.get('/web/index.html');
    lastStatus = res.status();
    if (res.ok()) {
      const body = await res.text();
      if (body.includes('plugin="Jellyfin Helper"')) return body;
      // Served but not yet patched — give startup injection a moment.
    }
    await sleep(1000);
  }
  throw new Error(`index.html never contained the injected tag (last status ${lastStatus})`);
}

test('fallback injects the sidebar <script> into index.html (no File Transformation present)', async () => {
  const html = await fetchIndexHtml();

  // The tag the plugin injects via the disk-write fallback.
  expect(html).toContain('plugin="Jellyfin Helper"');
  expect(html).toContain('/JellyfinHelper/Discovery/My/script');

  await assertPluginActive(ctx);
});

test('the injected tag appears exactly once (idempotent — no stacking)', async () => {
  const html = await fetchIndexHtml();

  // ctor injection + the startup hosted service both run InjectScript; the
  // RemovalRegex + "already up to date → skip write" logic must keep it to one.
  const occurrences = (html.match(/plugin="Jellyfin Helper"/g) ?? []).length;
  expect(occurrences, 'sidebar script tag must be injected exactly once').toBe(1);
});

test('the injected script src is reachable and serves JavaScript', async () => {
  const html = await fetchIndexHtml();

  // Extract the src the browser would load (…/JellyfinHelper/Discovery/My/script?v=…).
  const match = html.match(/src="([^"]*JellyfinHelper\/Discovery\/My\/script[^"]*)"/);
  expect(match, 'injected <script> must carry a Discovery/My/script src').toBeTruthy();

  // The src is relative ("../JellyfinHelper/…"); resolve it to the absolute route.
  const res = await ctx.get('/JellyfinHelper/Discovery/My/script');
  expect(res.ok(), `injected script src not reachable: ${res.status()}`).toBeTruthy();
  expect(res.headers()['content-type'] ?? '').toContain('javascript');

  await assertPluginActive(ctx);
});
