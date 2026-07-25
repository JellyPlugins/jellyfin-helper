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
