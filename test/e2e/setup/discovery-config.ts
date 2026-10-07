/**
 * Shared Discovery custom-tab configuration for the e2e suite.
 *
 * global-setup applies this once at startup, but the `api` project (which runs
 * before `ui`) PUTs /Configuration in many specs and leaves DiscoveryUserAccessEnabled
 * false + SeerrUrl empty by the time the `ui` project runs. The ui-setup project
 * re-applies this so discovery-sidebar.js (injected on every page via File
 * Transformation) gets a 200 from /Discovery/My instead of a benign-but-noisy 403,
 * and the custom tab actually mounts.
 */
import type { APIRequestContext } from '@playwright/test';

export const CUSTOM_TABS_GUID = 'fbacd0b6-fd46-4a05-b0a4-2045d6a135b0';

// Internal compose address of the mock Seerr; the toggle only sticks with
// Recommendations active + Seerr configured.
const INTERNAL_MOCK_SEERR_URL = 'http://mock-seerr:5055'; // NOSONAR - internal test mock, no TLS

/**
 * Enable the Discovery user-access toggle and register the Custom Tab whose HTML
 * content is the discovery marker div, mirroring the admin setup documented in
 * the plugin's settings hint. No-op unless the external plugins are staged
 * (JFH_E2E_EXTERNAL_PLUGINS=1). Throws on a non-ok response so a broken setup is
 * visible rather than surfacing later as a confusing 403.
 */
export async function ensureDiscoveryConfigured(
  admin: APIRequestContext,
  log: (msg: string) => void = () => {},
): Promise<void> {
  if (process.env.JFH_E2E_EXTERNAL_PLUGINS !== '1') {
    return;
  }

  const cfg = await admin.put('/JellyfinHelper/Configuration', {
    headers: { 'Content-Type': 'application/json' },
    data: {
      RecommendationsTaskMode: 'Activate',
      SeerrUrl: INTERNAL_MOCK_SEERR_URL,
      SeerrApiKey: 'seerr-key',
      DiscoveryUserAccessEnabled: true,
      TraktEnabled: true,
      TraktClientId: 'mock-trakt-client-id',
      TraktClientSecret: 'mock-trakt-client-secret',
      ExcludedLibraries: '',
    },
  });
  log(`enable discovery access -> ${cfg.status()}`);
  if (!cfg.ok()) {
    throw new Error(`Discovery access setup failed: ${cfg.status()} ${(await cfg.text()).slice(0, 300)}`);
  }

  const tab = await admin.post(`/Plugins/${CUSTOM_TABS_GUID}/Configuration`, {
    headers: { 'Content-Type': 'application/json' },
    data: { Tabs: [{ Title: 'Seerr Discovery', ContentHtml: '<div class="jellyfinhelper discovery"></div>' }] },
  });
  log(`configure Custom Tabs tab -> ${tab.status()}`);
  if (!tab.ok()) {
    throw new Error(`Custom Tabs tab setup failed: ${tab.status()} ${(await tab.text()).slice(0, 300)}`);
  }
}
