/**
 * Mock Trakt server for E2E tests. Implements the subset of the Trakt API the plugin uses: personal
 * recommendations (Bearer required) and global trending (client-id only). An unauthenticated /reset test hook
 * clears recorded state. Loopback-only, like the other mocks. Real Trakt sits behind Cloudflare, which 403s any
 * API request without a User-Agent; this mock enforces the same on every real API path (test hooks exempt) so
 * the suite catches a missing UA.
 */
import http from 'node:http';

const PORT = Number(process.env.PORT ?? 9100);

let lastRecommendationBearer = null;
let lastUserAgent = null;

function reset() {
  lastRecommendationBearer = null;
  lastUserAgent = null;
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

// Personal recommendations require a Bearer token: the official-plugin spec seeds a known token into the
// official plugin's Trakt.xml, and the Helper forwards it here. A 200 proves a real token was presented (a
// wrong/absent bearer gets 401, exactly as real Trakt behaves). Keep the value in sync with the seeded token.
const EXPECTED_OFFICIAL_BEARER = process.env.TRAKT_EXPECTED_BEARER ?? 'seeded-official-token';
const VALID_BEARERS = new Set([
  EXPECTED_OFFICIAL_BEARER,
]);

function bearerOf(req) {
  const auth = req.headers['authorization'];
  if (typeof auth !== 'string' || !auth.startsWith('Bearer ')) {
    return null;
  }
  return auth.slice('Bearer '.length);
}

function requireBearer(req, res, handler) {
  const token = bearerOf(req);
  if (token === null || !VALID_BEARERS.has(token)) {
    sendJson(res, 401, { error: 'unauthorized' });
    return;
  }
  handler(req, res);
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

  // Personal recommendations (OAuth, Bearer required) and trending (client-id only, no bearer).
  'GET /recommendations/movies': (req, res) => requireBearer(req, res, (r, s) => {
    lastRecommendationBearer = bearerOf(r);
    sendJson(s, 200, recommendationMovies);
  }),
  'GET /recommendations/shows': (req, res) => requireBearer(req, res, (r, s) => {
    lastRecommendationBearer = bearerOf(r);
    sendJson(s, 200, recommendationShows);
  }),
  'GET /movies/trending': (_req, res) => sendJson(res, 200, trendingMovies),
  'GET /shows/trending': (_req, res) => sendJson(res, 200, trendingShows),

  // Test hook: report the Bearer token last presented to a recommendations endpoint, so a spec can prove the
  // official plugin's seeded token (not our own-flow token) actually reached Trakt.
  'GET /last-recommendation-bearer': (_req, res) => sendJson(res, 200, { bearer: lastRecommendationBearer }),

  // Test hook: report the User-Agent last presented on a real API path, so a spec can prove the plugin sends
  // one (real Trakt/Cloudflare 403s requests without a UA - the production bug this guards against).
  'GET /last-user-agent': (_req, res) => sendJson(res, 200, { userAgent: lastUserAgent }),
};

// Test hooks and health are driven by Playwright (not the plugin) and are exempt from the Cloudflare-style
// User-Agent gate below; everything else is a real Trakt API path the plugin calls.
const UA_EXEMPT = new Set([
  'GET /health',
  'POST /reset',
  'GET /last-recommendation-bearer',
  'GET /last-user-agent',
]);

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const routeKey = `${req.method} ${url.pathname}`;

  // Real Trakt sits behind Cloudflare, which 403s any request without a User-Agent. The plugin failed exactly
  // this way in production (HttpClient sends none by default). Enforce the same here so the e2e suite proves
  // the plugin now sends a UA - a missing one is a 403 on every real API path, not a silent pass.
  if (!UA_EXEMPT.has(routeKey)) {
    const ua = req.headers['user-agent'];
    if (typeof ua !== 'string' || ua.trim() === '') {
      await readBody(req);
      return sendJson(res, 403, { error: 'forbidden', reason: 'missing user-agent' });
    }
    // Record the UA the plugin presented so a spec can prove it is non-empty and identifies the plugin.
    lastUserAgent = ua;
  }

  const handler = routes[routeKey];
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
