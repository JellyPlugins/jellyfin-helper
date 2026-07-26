/**
 * Mock Jellyseerr/Overseerr (Seerr) server for the Jellyfin Helper E2E tests.
 *
 * Implements the Overseerr API v1 subset the plugin actually calls. Auth is
 * `X-Api-Key` (any non-empty key accepted; `force-fail` -> 500 for negatives).
 * Only the fields the plugin deserializes are populated; camelCase throughout.
 *
 * State: an in-memory list of requests so DELETE actually removes one and the
 * cleanup task's "Deleted" count is real. Reset via GET /reset (test hook).
 *
 * No external dependencies — Node built-in http only.
 */
import http from 'node:http';

const PORT = Number(process.env.PORT ?? 5055);
const FAIL_KEY = 'force-fail';

// --- mutable state ---------------------------------------------------------

function seedRequests() {
  // "Recent" is relative to NOW so it always survives the age cutoff, no matter
  // when the suite runs (a fixed date would eventually age past the threshold).
  const recentIso = new Date().toISOString();
  return [
    // Old + status 1 (pending) => selected for deletion when maxAgeDays passes.
    { id: 101, createdAt: '2023-01-01T00:00:00.000Z', status: 1, media: { mediaType: 'movie', tmdbId: 27205, status: 3 } },
    // Old + status 3 (declined) => also eligible.
    { id: 102, createdAt: '2023-02-01T00:00:00.000Z', status: 3, media: { mediaType: 'tv', tmdbId: 1396, status: 3 } },
    // Old but status 4 (available) => protected, must NOT be deleted.
    { id: 103, createdAt: '2023-03-01T00:00:00.000Z', status: 4, media: { mediaType: 'movie', tmdbId: 438631, status: 4 } },
    // Recent => survives the age cutoff regardless of status.
    { id: 104, createdAt: recentIso, status: 1, media: { mediaType: 'movie', tmdbId: 550, status: 3 } },
  ];
}
let requests = seedRequests();

// --- canned payloads -------------------------------------------------------

const mainSettings = { applicationTitle: 'Jellyseerr' };

const users = [
  {
    id: 3,
    displayName: 'Alice',
    email: 'alice@example.com',
    avatar: '/avatarproxy/abc.png',
    // Overwritten at setup time via /seed-user so it matches the real Jellyfin GUID.
    jellyfinUserId: '00000000000000000000000000000000',
    permissions: 32, // Request
  },
];

const movieTitles = { 27205: 'Inception', 438631: 'Dune', 550: 'Fight Club' };
const tvNames = { 1396: 'Breaking Bad' };

function serviceList(type) {
  return [{ id: 0, name: type === 'sonarr' ? 'Sonarr' : 'Radarr', isDefault: true, is4k: false, activeProfileId: 4, activeDirectory: type === 'sonarr' ? '/tv' : '/movies', profiles: [], rootFolders: [] }];
}
function serviceDetail(type) {
  return {
    id: 0, name: type === 'sonarr' ? 'Sonarr' : 'Radarr', isDefault: true, is4k: false,
    activeProfileId: 4, activeDirectory: type === 'sonarr' ? '/tv' : '/movies',
    profiles: [{ id: 4, name: 'HD-1080p' }, { id: 5, name: 'Ultra-HD' }],
    rootFolders: [{ id: 1, path: type === 'sonarr' ? '/tv' : '/movies' }, { id: 2, path: '/media4k' }],
  };
}
function discoverPage() {
  return {
    page: 1, totalPages: 1, totalResults: 2,
    results: [
      { id: 27205, mediaType: 'movie', title: 'Inception', genreIds: [28, 878], voteAverage: 8.3, popularity: 120.5, releaseDate: '2010-07-16', posterPath: '/p1.jpg', overview: 'A thief...', adult: false },
      { id: 680, mediaType: 'movie', title: 'Pulp Fiction', genreIds: [80, 18], voteAverage: 8.5, popularity: 90.1, releaseDate: '1994-10-14', posterPath: '/p2.jpg', overview: 'The lives...', adult: false },
    ],
  };
}

// --- helpers ---------------------------------------------------------------

function send(res, status, body) {
  const payload = body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(payload);
}
function log(method, path, status) {
  // eslint-disable-next-line no-console
  console.log(`[mock-seerr] ${method} ${path} -> ${status}`);
}
function readBody(req) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => resolve(data));
  });
}

// --- server ----------------------------------------------------------------

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', `http://localhost:${PORT}`);
  const path = url.pathname;
  const method = req.method ?? 'GET';
  const apiKey = req.headers['x-api-key'];

  const done = (s) => log(method, path + url.search, s);

  // Health probe (unauthenticated).
  if (path === '/health') { send(res, 200, { ok: true }); return done(200); }

  // Test hooks (unauthenticated, E2E-only): reset state / seed the Jellyfin GUID / count.
  if (path === '/reset') { requests = seedRequests(); send(res, 200, { ok: true }); return done(200); }
  if (path === '/count') { send(res, 200, { count: requests.length, ids: requests.map((r) => r.id) }); return done(200); }
  if (path === '/seed-user' && method === 'POST') {
    const body = JSON.parse((await readBody(req)) || '{}');
    if (body.jellyfinUserId) users[0].jellyfinUserId = String(body.jellyfinUserId).replace(/-/g, '');
    send(res, 200, { ok: true }); return done(200);
  }

  if (!apiKey) { send(res, 401, { error: 'missing X-Api-Key' }); return done(401); }
  if (apiKey === FAIL_KEY) { send(res, 500, { error: 'forced failure' }); return done(500); }

  // Se1. connection test
  if (path === '/api/v1/settings/main') { send(res, 200, mainSettings); return done(200); }

  // Se2. request list (paginated) — single page.
  if (path === '/api/v1/request' && method === 'GET') {
    const skip = Number(url.searchParams.get('skip') ?? 0);
    const take = Number(url.searchParams.get('take') ?? 50);
    const slice = requests.slice(skip, skip + take);
    send(res, 200, { pageInfo: { page: 1, pages: 1, results: requests.length, pageSize: take }, results: slice });
    return done(200);
  }
  // Se3. request submission
  if (path === '/api/v1/request' && method === 'POST') {
    await readBody(req);
    send(res, 201, { id: 9999 });
    return done(201);
  }
  // Se2. delete request
  const delMatch = path.match(/^\/api\/v1\/request\/(\d+)$/);
  if (delMatch && method === 'DELETE') {
    const id = Number(delMatch[1]);
    requests = requests.filter((r) => r.id !== id);
    send(res, 200, {});
    return done(200);
  }

  // Title / credits resolution: GET /api/v1/movie|tv/{id}
  const movieMatch = path.match(/^\/api\/v1\/movie\/(\d+)$/);
  if (movieMatch && method === 'GET') {
    const id = Number(movieMatch[1]);
    send(res, 200, { id, title: movieTitles[id] ?? 'Unknown Movie', credits: { cast: [{ id: 1, name: 'Actor One', character: 'Lead', order: 0 }], crew: [{ id: 2, name: 'Dir One', job: 'Director', department: 'Directing' }] } });
    return done(200);
  }
  const tvMatch = path.match(/^\/api\/v1\/tv\/(\d+)$/);
  if (tvMatch && method === 'GET') {
    const id = Number(tvMatch[1]);
    send(res, 200, { id, name: tvNames[id] ?? 'Unknown Show', credits: { cast: [], crew: [] } });
    return done(200);
  }

  // Se4. users (paginated) — single page.
  if (path === '/api/v1/user' && method === 'GET') {
    send(res, 200, { pageInfo: { pages: 1, results: users.length }, results: users });
    return done(200);
  }

  // Se5. service list + detail
  const svcList = path.match(/^\/api\/v1\/service\/(radarr|sonarr)$/);
  if (svcList) { send(res, 200, serviceList(svcList[1])); return done(200); }
  const svcDetail = path.match(/^\/api\/v1\/service\/(radarr|sonarr)\/\d+$/);
  if (svcDetail) { send(res, 200, serviceDetail(svcDetail[1])); return done(200); }

  // Se3. discover (path-based genre/language, ?page=N)
  if (/^\/api\/v1\/discover\/(movies|tv)\//.test(path)) {
    send(res, 200, discoverPage());
    return done(200);
  }

  send(res, 404, { error: 'not found' });
  done(404);
});

server.listen(PORT, () => {
  // eslint-disable-next-line no-console
  console.log(`[mock-seerr] listening on :${PORT}`);
});
