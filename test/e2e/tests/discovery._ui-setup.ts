/**
 * UI-project setup: re-apply the Discovery configuration before the ui specs run.
 *
 * global-setup configures it once, but the `api` project runs first and its
 * many PUT /Configuration calls leave DiscoveryUserAccessEnabled=false and
 * SeerrUrl empty. Without this, discovery-sidebar.js gets a 403 from /Discovery/My
 * - which is logged by the browser as a console error that trips trends-chart's
 * strict no-error gate, and no Discovery tab is registered.
 */
import { test } from '@playwright/test';
import { apiContext, loadAuth } from '../setup/api-client.ts';
import { ensureDiscoveryConfigured } from '../setup/discovery-config.ts';

test('re-assert discovery config for the ui phase', async () => {
  const admin = await apiContext(loadAuth());
  try {
    // eslint-disable-next-line no-console
    await ensureDiscoveryConfigured(admin, (m) => console.log(`[ui-setup] ${m}`));
  } finally {
    await admin.dispose();
  }
});
