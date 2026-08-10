/**
 * Mock Radarr + Sonarr server for the Jellyfin Helper E2E tests.
 *
 * One process serves BOTH Radarr and Sonarr APIs - they only differ by path
 * (/api/v3/movie vs /api/v3/series) and the appName in /system/status, so the
 * plugin can point two "instances" at this one server. The plugin authenticates
 * with `X-Api-Key`; we accept any non-empty key (and expose a way to force a
 * failure for negative tests via a special key).
 *
 * Only the fields the plugin actually deserializes are included; extra fields
 * are harmless (the plugin is case-insensitive and non-strict).
 *
 * No external dependencies - Node built-in http only.
 */
import http from 'node:http';

const PORT = Number(process.env.PORT ?? 9000);

// A request carrying this API key gets a 500, to exercise the plugin's
// error/502 path in negative tests.
const FAIL_KEY = 'force-fail';
// Adversarial sentinel keys (hardening tests only): slow-loris, over-large body,
// non-JSON garbage. Green-path keys are unaffected.
const SLOW_KEY = 'force-slow';
const GIANT_KEY = 'force-giant';
const GARBAGE_KEY = 'force-garbage';

// --- canned payloads -------------------------------------------------------

const radarrStatus = { appName: 'Radarr', version: '5.2.6.8376' };
const sonarrStatus = { appName: 'Sonarr', version: '4.0.9.2244' };

// Movie folder names are the LAST path segment; the plugin matches them against
// Jellyfin library folder names. Our fake library has "Aurora Skies (2019)" and
// "Nebula Drift (2021)" - so "Inception (2010)" lands in InArrOnly (hasFile) and
// "Missing Film (2099)" in InArrOnlyMissing (no file).
const radarrMovies = [
  { title: 'Aurora Skies', year: 2019, imdbId: 'tt0001', tmdbId: 111, hasFile: true, path: '/movies/Aurora Skies (2019)' },
  { title: 'Inception', year: 2010, imdbId: 'tt1375666', tmdbId: 27205, hasFile: true, path: '/movies/Inception (2010)' },
  { title: 'Missing Film', year: 2099, imdbId: 'tt0009', tmdbId: 999, hasFile: false, path: '/movies/Missing Film (2099)' },
];

const sonarrSeries = [
  { title: 'Test Show', year: 2020, imdbId: 'tt0100', tvdbId: 100, tmdbId: 1396, path: '/tv/Test Show', statistics: { episodeFileCount: 2, totalEpisodeCount: 2 } },
  { title: 'Ghost Series', year: 2018, imdbId: 'tt0200', tvdbId: 200, tmdbId: 1397, path: '/tv/Ghost Series', statistics: { episodeFileCount: 0, totalEpisodeCount: 10 } },
];

// --- helpers ---------------------------------------------------------------

function send(res, status, body) {
  const payload = typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(payload);
}

function log(method, path, status) {
  // eslint-disable-next-line no-console
  console.log(`[mock-arr] ${method} ${path} -> ${status}`);
}

// --- server ----------------------------------------------------------------

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? '/', `http://localhost:${PORT}`);
  const path = url.pathname;
  const apiKey = req.headers['x-api-key'];

  // Unauthenticated health probe for the compose healthcheck.
  if (path === '/health') return finish('/health', send(res, 200, { ok: true }));

  // Everything else requires an API key (mirrors real Arr behaviour).
  if (!apiKey) return finish(path, send(res, 401, { error: 'missing X-Api-Key' }));
  if (apiKey === FAIL_KEY) return finish(path, send(res, 500, { error: 'forced failure' }));

  // Adversarial sentinel keys.
  if (apiKey === SLOW_KEY) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    setTimeout(() => { try { res.end('[]'); } catch { /* client gone */ } }, 40_000);
    return finish(path, undefined);
  }
  if (apiKey === GIANT_KEY) {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Transfer-Encoding': 'chunked' });
    res.write('[');
    const chunk = `{"title":"${'x'.repeat(1024 * 1024)}"},`;
    let sent = 0;
    const pump = () => {
      if (sent >= 120) { res.end('{}]'); return; }
      sent += 1;
      if (res.write(chunk)) { setImmediate(pump); } else { res.once('drain', pump); }
    };
    pump();
    return finish(path, undefined);
  }
  if (apiKey === GARBAGE_KEY) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('<html>not json</html>{truncated');
    return finish(path, undefined);
  }

  if (path === '/api/v3/system/status') {
    // Distinguish Radarr vs Sonarr by an optional ?app= hint; default Radarr.
    const app = url.searchParams.get('app');
    return finish(path, send(res, 200, app === 'sonarr' ? sonarrStatus : radarrStatus));
  }
  if (path === '/api/v3/movie') return finish(path, send(res, 200, radarrMovies));
  if (path === '/api/v3/series') return finish(path, send(res, 200, sonarrSeries));

  return finish(path, send(res, 404, { error: 'not found' }));

  function finish(p, _r) {
    log(req.method ?? '?', p, res.statusCode);
  }
});

server.listen(PORT, () => {
  // eslint-disable-next-line no-console
  console.log(`[mock-arr] listening on :${PORT} (serves Radarr + Sonarr)`);
});
