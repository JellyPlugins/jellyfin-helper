/**
 * Arr / Seerr integration against the MOCK servers (green path). Complements
 * hardening.api.spec.ts (which covers the failure paths). Requires the mock
 * instances to be configured, which we do here.
 */
import { test, expect, type APIRequestContext } from '@playwright/test';
import { apiContext, loadAuth, p, assertPluginActive } from '../setup/api-client.ts';

const ARR_URL = process.env.MOCK_ARR_URL ?? 'http://mock-arr:9000';
const SEERR_URL = process.env.MOCK_SEERR_URL ?? 'http://mock-seerr:5055';

let ctx: APIRequestContext;

test.beforeAll(async () => {
  ctx = await apiContext(loadAuth());
  // Configure the mock instances up front.
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RadarrInstances: [{ Name: 'Mock Radarr', Url: ARR_URL, ApiKey: 'radarr-key' }],
      SonarrInstances: [{ Name: 'Mock Sonarr', Url: ARR_URL, ApiKey: 'sonarr-key' }],
      SeerrUrl: SEERR_URL,
      SeerrApiKey: 'seerr-key',
    },
  });
});
test.afterAll(async () => {
  await ctx.dispose();
});

test('Radarr connection test succeeds against mock', async () => {
  const res = await ctx.post(p('ArrIntegration/TestConnection'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: ARR_URL, ApiKey: 'radarr-key' },
  });
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Success: boolean; Message: string };
  expect(body.Success).toBe(true);
  expect(body.Message).toContain('Radarr');
});

test('Radarr Compare returns bucketed result', async () => {
  const res = await ctx.get(p('ArrIntegration/Compare/Radarr?index=0'));
  expect(res.ok(), `compare failed: ${res.status()}`).toBeTruthy();
  const body = (await res.json()) as {
    InBoth: string[]; InArrOnly: string[]; InArrOnlyMissing: string[]; InJellyfinOnly: string[];
  };
  // The mock returns "Aurora Skies (2019)" (matches our library) + Inception
  // (hasFile, not in library) + Missing Film (no file, not in library).
  expect(Array.isArray(body.InBoth)).toBeTruthy();
  expect(Array.isArray(body.InArrOnly)).toBeTruthy();
  expect(Array.isArray(body.InArrOnlyMissing)).toBeTruthy();
  // Inception has a file but no matching Jellyfin folder → InArrOnly.
  expect(body.InArrOnly.join(' ')).toContain('Inception');
  // Missing Film has no file and no match → InArrOnlyMissing.
  expect(body.InArrOnlyMissing.join(' ')).toContain('Missing Film');
});

test('Sonarr Compare returns bucketed result', async () => {
  const res = await ctx.get(p('ArrIntegration/Compare/Sonarr?index=0'));
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { InArrOnly: string[]; InArrOnlyMissing: string[] };
  // Ghost Series has 0 episode files → InArrOnlyMissing.
  expect(body.InArrOnlyMissing.join(' ')).toContain('Ghost Series');
});

test('Seerr connection test succeeds against mock', async () => {
  const res = await ctx.post(p('Seerr/Test'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: SEERR_URL, ApiKey: 'seerr-key' },
  });
  expect(res.ok()).toBeTruthy();
  const body = (await res.json()) as { Success: boolean; Message: string };
  expect(body.Success).toBe(true);
  expect(body.Message).toContain('Jellyseerr');
});

test('Discovery Users returns the mock user list', async () => {
  const res = await ctx.get(p('Discovery/Users'));
  expect(res.ok()).toBeTruthy();
  const users = (await res.json()) as Array<{ DisplayName?: string; displayName?: string }>;
  expect(users.length).toBeGreaterThanOrEqual(1);
});

test('Discovery Services for radarr returns quality profiles', async () => {
  const res = await ctx.get(p('Discovery/Services/radarr'));
  expect(res.status()).toBeLessThan(500);
  // Green path: 200 with a service list; may be [] but must not throw.
  if (res.ok()) {
    const services = await res.json();
    expect(Array.isArray(services)).toBeTruthy();
  }
  await assertPluginActive(ctx);
});

test('Discovery Services rejects invalid service type', async () => {
  const res = await ctx.get(p('Discovery/Services/notaservice'));
  expect(res.status()).toBe(400);
});

test('Seerr/Test rejects non-HTTP(S) schemes with the exact message', async () => {
  for (const url of ['ftp://evil', 'javascript:alert(1)', 'not-a-url', 'file:///etc/passwd']) {
    const res = await ctx.post(p('Seerr/Test'), {
      headers: { 'Content-Type': 'application/json' },
      data: { Url: url, ApiKey: 'k' },
    });
    expect(res.status(), `url=${url}`).toBe(400);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toBe('A valid HTTP(S) URL is required.');
  }
  await assertPluginActive(ctx);
});

test('Seerr/Test rejects blank URL/key and null body before any network call', async () => {
  const bodies: unknown[] = [
    { Url: SEERR_URL, ApiKey: '' },
    { Url: '', ApiKey: 'k' },
    { Url: '   ', ApiKey: 'k' },
    {},
  ];
  for (const data of bodies) {
    const res = await ctx.post(p('Seerr/Test'), {
      headers: { 'Content-Type': 'application/json' },
      data: JSON.stringify(data),
    });
    expect(res.status()).toBe(400);
    const body = (await res.json()) as { Success: boolean; Message: string };
    expect(body.Success).toBe(false);
    expect(body.Message).toBe('URL and API Key are required.');
  }
  await assertPluginActive(ctx);
});

test('Arr Compare 502 aggregation names the failing instance', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { RadarrInstances: [{ Name: 'FailBox', Url: ARR_URL, ApiKey: 'force-fail' }] },
  });
  try {
    const res = await ctx.get(p('ArrIntegration/Compare/Radarr?index=0'));
    expect(res.status()).toBe(502);
    expect(await res.text()).toContain('FailBox');
  } finally {
    // Restore the working instance even if an assertion above throws, so this
    // shared backend isn't left pinned to the broken FailBox config for later
    // tests/files that assume a reachable Radarr.
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { RadarrInstances: [{ Name: 'Mock Radarr', Url: ARR_URL, ApiKey: 'radarr-key' }] },
    });
  }
  await assertPluginActive(ctx);
});

// --- Sonarr Compare error-branch parity with Radarr ------------------------
// Compare/Sonarr is a duplicated code block from Compare/Radarr; the two can drift
// independently. Radarr's three error branches are asserted (empty→400,
// out-of-range→400, failing-instance→502-naming-instance); mirror them for Sonarr
// so a copy-paste regression (wrong status, missing instance name) can't hide.

test('Sonarr Compare with no instances → 400 naming the requirement', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SonarrInstances: [] },
  });
  try {
    const res = await ctx.get(p('ArrIntegration/Compare/Sonarr'));
    expect(res.status()).toBe(400);
    expect(await res.text()).toContain('At least one Sonarr instance');
  } finally {
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { SonarrInstances: [{ Name: 'Mock Sonarr', Url: ARR_URL, ApiKey: 'sonarr-key' }] },
    });
  }
  await assertPluginActive(ctx);
});

test('Sonarr Compare with out-of-range index → 400 with the range message', async () => {
  // Exactly one instance configured (restored above) → valid range is 0-0.
  const res = await ctx.get(p('ArrIntegration/Compare/Sonarr?index=99'));
  expect(res.status()).toBe(400);
  expect(await res.text()).toContain('Invalid instance index 99. Valid range: 0-0.');
  await assertPluginActive(ctx);
});

test('Sonarr Compare 502 aggregation names the failing instance', async () => {
  await ctx.put(p('Configuration'), {
    headers: { 'Content-Type': 'application/json' },
    data: { SonarrInstances: [{ Name: 'FailBoxS', Url: ARR_URL, ApiKey: 'force-fail' }] },
  });
  try {
    const res = await ctx.get(p('ArrIntegration/Compare/Sonarr?index=0'));
    expect(res.status()).toBe(502);
    const body = await res.text();
    expect(body).toContain('FailBoxS');
    expect(body).toContain('Sonarr instance(s)');
  } finally {
    await ctx.put(p('Configuration'), {
      headers: { 'Content-Type': 'application/json' },
      data: { SonarrInstances: [{ Name: 'Mock Sonarr', Url: ARR_URL, ApiKey: 'sonarr-key' }] },
    });
  }
  await assertPluginActive(ctx);
});

test('Seerr/Test against a reachable-but-failing upstream → 502 with a generic non-leaking message', async () => {
  // The mock's 'force-fail' key returns HTTP 500 on every authed call: a REACHABLE
  // upstream failure (distinct from the dead-port HttpRequestException in hardening).
  // The controller must return 502 with a fixed generic message and must NOT reflect
  // the raw upstream error text (internal-reachability-oracle suppression).
  const res = await ctx.post(p('Seerr/Test'), {
    headers: { 'Content-Type': 'application/json' },
    data: { Url: SEERR_URL, ApiKey: 'force-fail' },
  });
  expect(res.status()).toBe(502);
  const body = (await res.json()) as { Success: boolean; Message: string };
  expect(body.Success).toBe(false);
  expect(body.Message).toBe('Connection failed. Please verify URL and API Key and try again.');
  // Must not leak the mock's upstream error wording.
  expect(body.Message.toLowerCase()).not.toContain('forced');
  expect(body.Message).not.toContain('500');
  await assertPluginActive(ctx);
});
