/**
 * Shared Discovery configuration for the e2e suite.
 *
 * global-setup applies this once at startup, but the `api` project (which runs
 * before `ui`) PUTs /Configuration in many specs and leaves DiscoveryUserAccessEnabled
 * false + SeerrUrl empty by the time the `ui` project runs. The ui-setup project
 * re-applies this so discovery-sidebar.js gets a 200 from /Discovery/My instead of
 * a benign-but-noisy 403, and the built-in Discovery tab actually registers.
 */
import type { APIRequestContext } from '@playwright/test';

export const OFFICIAL_TRAKT_GUID = '4fe3201e-d6ae-4f2e-8917-e12bda571281';

// The token the official-plugin spec expects the Helper to read from Trakt.xml and forward to mock-trakt. Resolves
// from the SAME env var + default as mock-trakt's TRAKT_EXPECTED_BEARER (test/e2e/mocks/trakt-server.js), so an
// override stays in sync on both sides instead of the mock 401ing a seed it never agreed on.
export const SEEDED_OFFICIAL_TRAKT_TOKEN = process.env.TRAKT_EXPECTED_BEARER ?? 'seeded-official-token';

// Internal compose address of the mock Seerr; the toggle only sticks with
// Recommendations active + Seerr configured.
const INTERNAL_MOCK_SEERR_URL = 'http://mock-seerr:5055'; // NOSONAR - internal test mock, no TLS

/**
 * Enable the Discovery user-access toggle so the plugin registers its built-in
 * Home tab and sidebar entry. Runs unconditionally: the tab is part of the
 * plugin now, so no external tab plugin has to be staged first. Throws on a
 * non-ok response so a broken setup is visible rather than surfacing later as a
 * confusing 403.
 */
export async function ensureDiscoveryConfigured(
  admin: APIRequestContext,
  log: (msg: string) => void = () => {},
): Promise<void> {
  const cfg = await admin.put('/JellyfinHelper/Configuration', {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: INTERNAL_MOCK_SEERR_URL,
      SeerrApiKey: 'seerr-key',
      DiscoveryUserAccessEnabled: true,
      TraktSourcingEnabled: true,
      ExcludedLibraries: '',
    },
  });
  log(`enable discovery access -> ${cfg.status()}`);
  if (!cfg.ok()) {
    throw new Error(`Discovery access setup failed: ${cfg.status()} ${(await cfg.text()).slice(0, 300)}`);
  }
}

/**
 * Seed the OFFICIAL Trakt plugin's configuration with a linked user holding a known, unexpired access token, so
 * the Helper's "source Trakt through the official plugin" mode has something real to read. POSTing to the
 * official plugin's /Configuration endpoint makes Jellyfin persist it to configurations/Trakt.xml - exactly the
 * file OfficialTraktPluginReader reads in-process. No-op unless the official Trakt plugin was staged. Throws on a
 * non-ok response so a broken seed is visible immediately.
 */
export async function seedOfficialTraktPlugin(
  admin: APIRequestContext,
  jellyfinUserId: string,
  log: (msg: string) => void = () => {},
): Promise<void> {
  if (process.env.JFH_E2E_EXTERNAL_PLUGINS !== '1') {
    return;
  }

  // Far-future expiry so the reader never treats it as expired regardless of the server's clock/timezone. Written
  // WITHOUT an offset on purpose: the real official plugin stores DateTime.Now (local, Unspecified kind), and the
  // reader interprets an offset-less value as local time to match - adding a 'Z' would misrepresent the format.
  const res = await admin.post(`/Plugins/${OFFICIAL_TRAKT_GUID}/Configuration`, {
    headers: { 'Content-Type': 'application/json' },
    data: {
      TraktUsers: [
        {
          LinkedMbUserId: jellyfinUserId,
          AccessToken: SEEDED_OFFICIAL_TRAKT_TOKEN,
          RefreshToken: 'seeded-official-refresh',
          AccessTokenExpiration: '2099-01-01T00:00:00',
        },
      ],
    },
  });
  log(`seed official Trakt plugin config -> ${res.status()}`);
  if (!res.ok()) {
    throw new Error(`Official Trakt plugin seed failed: ${res.status()} ${(await res.text()).slice(0, 300)}`);
  }
}
