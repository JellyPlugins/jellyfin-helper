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

// Route table: "METHOD path" -> handler. Keeps the request dispatcher flat (one lookup) instead of a long
// if/else chain, so adding a Trakt endpoint is a single entry.
const routes = {
  'GET /health': (_req, res) => sendJson(res, 200, { ok: true }),

  // Test hooks (unauthenticated, E2E-only).
  'POST /reset': (_req, res) => {
    reset();
    sendJson(res, 200, { ok: true });
  },
  'POST /arm-linked': (_req, res) => {
    devicePending = false;
    sendJson(res, 200, { ok: true });
  },

  // Device flow.
  'POST /oauth/device/code': (_req, res) => {
    lastDeviceCode = 'device-code-xyz';
    sendJson(res, 200, {
      device_code: lastDeviceCode,
      user_code: 'ABC123',
      verification_url: 'https://trakt.tv/activate',
      expires_in: 600,
      interval: 1,
    });
  },
  'POST /oauth/device/token': (_req, res) => {
    if (devicePending) {
      // 400 is Trakt's "authorization pending" during the device flow.
      sendJson(res, 400, { error: 'authorization_pending' });
      return;
    }
    sendJson(res, 200, {
      access_token: 'mock-access-token',
      refresh_token: 'mock-refresh-token',
      expires_in: 7776000,
      created_at: Math.floor(Date.now() / 1000),
    });
  },
  'POST /oauth/token': (_req, res) => {
    // Refresh grant.
    sendJson(res, 200, {
      access_token: 'mock-access-token-refreshed',
      refresh_token: 'mock-refresh-token-2',
      expires_in: 7776000,
      created_at: Math.floor(Date.now() / 1000),
    });
  },

  // Personal recommendations (OAuth) and trending (client-id only).
  'GET /recommendations/movies': (_req, res) => sendJson(res, 200, recommendationMovies),
  'GET /recommendations/shows': (_req, res) => sendJson(res, 200, recommendationShows),
  'GET /movies/trending': (_req, res) => sendJson(res, 200, trendingMovies),
  'GET /shows/trending': (_req, res) => sendJson(res, 200, trendingShows),
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const handler = routes[`${req.method} ${url.pathname}`];
  if (handler) {
    handler(req, res);
    return;
  }

  // Drain any unread body so the socket closes cleanly, then 404.
  await readBody(req);
  return sendJson(res, 404, { error: 'not_found', path: url.pathname });
});

server.listen(PORT, () => {
  process.stdout.write(`mock-trakt listening on ${PORT}\n`);
});
