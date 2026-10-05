/**
 * Mock Trakt server for E2E tests. Implements the subset of the Trakt API the plugin uses: the OAuth device
 * flow (code + token + refresh), personal recommendations, and global trending. Unauthenticated test hooks
 * (/reset, /arm-*) let specs drive the device-flow state machine deterministically. Loopback-only, like the
 * other mocks.
 */
import http from 'node:http';

const PORT = Number(process.env.PORT ?? 9100);

// Device-flow state. The token endpoint returns 400 (pending) until armed to succeed, so a spec can assert
// the "keep polling" path and then the "linked" path without timing races.
let devicePending = true;
let lastDeviceCode = null;

function reset() {
  devicePending = true;
  lastDeviceCode = null;
}

// Fixtures. Each list intentionally includes one item WITHOUT a tmdb id so the plugin's drop-and-count path
// is exercised end to end.
const recommendationMovies = [
  { title: 'Rec Movie A', year: 2021, overview: 'a', rating: 8.2, ids: { trakt: 1, slug: 'rec-movie-a', tmdb: 11 } },
  { title: 'Rec Movie NoTmdb', year: 2020, overview: 'b', rating: 7.0, ids: { trakt: 2, slug: 'rec-movie-notmdb' } },
];
const recommendationShows = [
  { title: 'Rec Show A', year: 2019, overview: 'c', rating: 7.8, ids: { trakt: 3, slug: 'rec-show-a', tmdb: 22 } },
];
const trendingMovies = [
  { watchers: 120, movie: { title: 'Trend Movie A', year: 2022, rating: 8.0, ids: { trakt: 4, slug: 'trend-movie-a', tmdb: 33 } } },
  { watchers: 90, movie: { title: 'Trend Movie NoTmdb', year: 2022, ids: { trakt: 5, slug: 'trend-movie-notmdb' } } },
];
const trendingShows = [
  { watchers: 80, show: { title: 'Trend Show A', year: 2023, rating: 7.5, ids: { trakt: 6, slug: 'trend-show-a', tmdb: 44 } } },
];

function sendJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) });
  res.end(payload);
}

async function readBody(req) {
  const chunks = [];
  for await (const chunk of req) {
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks).toString('utf8');
  try {
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const path = url.pathname;

  // Liveness probe for the compose health check.
  if (req.method === 'GET' && path === '/health') {
    return sendJson(res, 200, { ok: true });
  }

  // Test hooks (unauthenticated, E2E-only).
  if (req.method === 'POST' && path === '/reset') {
    reset();
    return sendJson(res, 200, { ok: true });
  }
  if (req.method === 'POST' && path === '/arm-linked') {
    devicePending = false;
    return sendJson(res, 200, { ok: true });
  }

  // Device flow.
  if (req.method === 'POST' && path === '/oauth/device/code') {
    lastDeviceCode = 'device-code-xyz';
    return sendJson(res, 200, {
      device_code: lastDeviceCode,
      user_code: 'ABC123',
      verification_url: 'https://trakt.tv/activate',
      expires_in: 600,
      interval: 1,
    });
  }
  if (req.method === 'POST' && path === '/oauth/device/token') {
    if (devicePending) {
      // 400 is Trakt's "authorization pending" during the device flow.
      return sendJson(res, 400, { error: 'authorization_pending' });
    }
    return sendJson(res, 200, {
      access_token: 'mock-access-token',
      refresh_token: 'mock-refresh-token',
      expires_in: 7776000,
      created_at: Math.floor(Date.now() / 1000),
    });
  }
  if (req.method === 'POST' && path === '/oauth/token') {
    // Refresh grant.
    return sendJson(res, 200, {
      access_token: 'mock-access-token-refreshed',
      refresh_token: 'mock-refresh-token-2',
      expires_in: 7776000,
      created_at: Math.floor(Date.now() / 1000),
    });
  }

  // Personal recommendations (OAuth) and trending (client-id only).
  if (req.method === 'GET' && path === '/recommendations/movies') {
    return sendJson(res, 200, recommendationMovies);
  }
  if (req.method === 'GET' && path === '/recommendations/shows') {
    return sendJson(res, 200, recommendationShows);
  }
  if (req.method === 'GET' && path === '/movies/trending') {
    return sendJson(res, 200, trendingMovies);
  }
  if (req.method === 'GET' && path === '/shows/trending') {
    return sendJson(res, 200, trendingShows);
  }

  // Drain any unread body so the socket closes cleanly, then 404.
  await readBody(req);
  return sendJson(res, 404, { error: 'not_found', path });
});

server.listen(PORT, () => {
  process.stdout.write(`mock-trakt listening on ${PORT}\n`);
});
