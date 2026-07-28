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
 * Timing reality (verified against the running stack): Jellyfin 12 serves
 * /web/index.html straight from disk on every request — there is NO in-memory
 * page cache — so once the fallback has written the file, the very next request
 * sees the tag (a browser reload is enough; no server restart or cache-bust). But
 * the write happens during plugin startup (constructor + the
 * DiscoverySidebarInjectionService hosted service), which Jellyfin runs somewhat
 * AFTER the server begins answering requests. So a fetch fired immediately after
 * "healthy" can legitimately race the injection. We therefore POLL patiently —
 * the real-world equivalent of reloading the page until the plugin has finished
 * its startup work — rather than asserting on the first hit.
 *
 * Asserts:
 *   - GET /web/index.html eventually contains the plugin's
 *     <script plugin="Jellyfin Helper"> tag (fallback injection at startup).
 *   - The tag appears EXACTLY once (idempotent — ctor + hosted service both run
 *     InjectScript under a lock; RemovalRegex + skip-if-unchanged prevent stacking).
 *   - The injected src is reachable and serves JavaScript.
 *   - The plugin stays Active throughout (injection didn't destabilise startup).
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, assertPluginActive, sleep } from '../setup/api-client.ts';

const auth = loadAuth();

// Injection lands during plugin startup, which can trail the server becoming
// "healthy" by a good margin under CI load. Poll generously — this mirrors a user
// reloading the page a few times right after a fresh server boot.
const MAX_ATTEMPTS = 30;
const POLL_MS = 2000;

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(auth);
});

test.afterAll(async () => {
  await ctx.dispose();
});

/**
 * Fetch /web/index.html, polling until the injected tag appears. On exhaustion it
 * throws with a self-diagnosing message (status, body length, whether </body> and
 * the React root are present) so a CI failure explains itself instead of just
 * saying "no tag" — distinguishing "served a different/te unexpected document"
 * from "injection never ran".
 */
async function fetchInjectedIndexHtml(): Promise<string> {
  let lastStatus = 0;
  let lastBody = '';
  for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
    const res = await ctx.get('/web/index.html');
    lastStatus = res.status();
    if (res.ok()) {
      lastBody = await res.text();
      if (lastBody.includes('plugin="Jellyfin Helper"')) {
        return lastBody;
      }
    }
    await sleep(POLL_MS);
  }

  const hasBodyTag = /<\/body>/i.test(lastBody);
  const hasReactRoot = /reactRoot/i.test(lastBody);
  throw new Error(
    `index.html never contained the injected tag after ${MAX_ATTEMPTS} attempts ` +
      `(~${(MAX_ATTEMPTS * POLL_MS) / 1000}s). last status=${lastStatus}, ` +
      `bodyLength=${lastBody.length}, has</body>=${hasBodyTag}, reactRoot=${hasReactRoot}. ` +
      `If has</body>=true the fallback could patch it but never wrote — check the ` +
      `"[Discovery Sidebar]" lines in the Jellyfin server log.`,
  );
}

test('fallback injects the sidebar <script> into index.html (no File Transformation present)', async () => {
  const html = await fetchInjectedIndexHtml();

  expect(html).toContain('plugin="Jellyfin Helper"');
  expect(html).toContain('/JellyfinHelper/Discovery/My/script');

  await assertPluginActive(ctx);
});

test('the injected tag appears exactly once (idempotent — no stacking)', async () => {
  const html = await fetchInjectedIndexHtml();

  // ctor injection + the startup hosted service both run InjectScript under a lock;
  // the RemovalRegex + "already up to date → skip write" logic must keep it to one.
  const occurrences = (html.match(/plugin="Jellyfin Helper"/g) ?? []).length;
  expect(occurrences, 'sidebar script tag must be injected exactly once').toBe(1);
});

test('the injected script src is reachable and serves JavaScript', async () => {
  const html = await fetchInjectedIndexHtml();

  // Extract the src the browser would load (…/JellyfinHelper/Discovery/My/script?v=…).
  const match = html.match(/src="([^"]*JellyfinHelper\/Discovery\/My\/script[^"]*)"/);
  expect(match, 'injected <script> must carry a Discovery/My/script src').toBeTruthy();

  // The src is relative ("../JellyfinHelper/…"); resolve it to the absolute route.
  const res = await ctx.get('/JellyfinHelper/Discovery/My/script');
  expect(res.ok(), `injected script src not reachable: ${res.status()}`).toBeTruthy();
  expect(res.headers()['content-type'] ?? '').toContain('javascript');

  await assertPluginActive(ctx);
});
