# E2E Coverage Matrix

What the end-to-end suite exercises, mapped to the test that covers it —
endpoints, task modes, settings, backup, trends, trash, and every UI
interaction. ~73 tests across 14 spec files.

## How to see coverage live

- **HTML report:** after any run — `cd test/e2e && npx playwright show-report`.
  Lists every test, pass/fail, timings; on failure embeds trace + screenshot + video.
- **In CI:** the `E2E (Docker)` workflow uploads `e2e-playwright-report` on every
  run (traces/screenshots + Jellyfin server log on failure). PR → Checks → E2E → Artifacts.
- **List without running:** `cd test/e2e && npx playwright test --list`.

---

## 1. Plugin load & routing → `smoke.api.spec.ts`
- Plugin present + **Active** (version read from Directory.Build.props, not hardcoded).
- Config page registered.
- Every GET endpoint routes without 404/500 (Ping, Configuration + sub-routes, Backup/Export,
  Trash/*, Logs + Download, CleanupStatistics, LibraryInsights, Translations, Discovery,
  MediaStatistics/Latest, GrowthTimeline, Recommendations + WatchProfiles, UserActivity/Latest).
- Per-user recommendation + activity routes.

## 2. Scheduled task — all modes → `tasks.api.spec.ts`
- All stages **Deactivate** → completes, no side effects.
- Cleanup stages **DryRun** → completes, deletes nothing (counters unchanged).
- Cleanup stages **Activate** (permanent delete) → completes, counters rise.
- **Activate + UseTrash** → items to trash, trash summary coherent.
- Recommendations **Deactivate** playlist-purge branch is safe.
- Seerr cleanup **Activate** → **verifies real deletion** (mock count before/after; ids 103/104 survive).

## 3. Settings persistence & effect → `settings.api.spec.ts`
- Task modes round-trip; numeric clamp; trash settings + blank-path reset.
- API-key mask `***` preserves stored key; SeerrCleanupAgeDays→0 when URL blank.
- PluginLogLevel ignored by PUT /Configuration, changed only via /LogLevel (+ invalid rejected).
- Arr instances persist (max 3, masked); Language de↔en.

## 4. Backup export / import → `backup.api.spec.ts`
- Export redacts secrets by default; includeSecrets includes key.
- Round-trip restore; tampered valid task mode restores; unknown mode falls back.
- **Hardening:** non-JSON garbage → 400; empty body; JSON array; missing fields; negative trends values.

## 5. Trends & statistics integrity → `trends.api.spec.ts`
- Media stats reflect library, no negative bytes.
- Growth timeline: no negative / no future-dated points (genuine 500 fails, not skips).
- Library insights coherent; cleanup statistics non-negative.

## 6. Arr / Seerr integration (mock green-path) → `integrations.api.spec.ts`
- Radarr/Sonarr connection test + Compare bucketing; Seerr connection test.
- Discovery Users; Discovery Services quality profiles; invalid service type rejected.

## 7. Hardening / edge cases → `hardening.api.spec.ts`
- Invalid Arr URLs; no-instances → 400; out-of-range index.
- Seerr unreachable → 502/504; trash traversal (`..`) + overlong path rejected.
- Unicode library exclusion; malformed language codes; empty/invalid user GUID.
- Concurrent task triggers; rate-limit 429 + Retry-After.
- (Setup putConfig calls fail loudly if a precondition save didn't succeed.)

## 8. User-facing Discovery → `discovery-my.api.spec.ts`  ← NEW
- **403 gating:** every `Discovery/My/*` endpoint returns 403 when
  `DiscoveryUserAccessEnabled` is off (tested as a real **non-admin user**).
- **Enabled flow:** My, ExternalLinks, RequestPermissions, Services respond (not 403).
- `Discovery/My/script` served anonymously (JS content-type).
- `Discovery/My/Dismiss` records dismissal.
- **Admin gaps closed:** `Discovery/Request` submission to mock; `Trash/Relocate` move.

## 9. UI — all 8 tabs → `tabs.ui.spec.ts`
- Overview, Codecs, Health, Trends, Settings, Arr, Logs (+ Recommendations when visible)
  switch + activate, **no JS console errors**. Overview renders stat cards after scan.

## 10. UI — interactions
| Covered | File |
|---|---|
| Codec breakdown row → file tree; folder expand/collapse; Expand/Collapse All; re-click closes | `trees.ui.spec.ts` |
| Health item → detail tree | `trees.ui.spec.ts` |
| Logs arrive + **download file**; level filter → PUT /LogLevel **succeeds + persists DEBUG**; clear → DELETE **succeeds + empty state** | `logs.ui.spec.ts` |
| **Unsaved dialog** — dirty band; appears on leaving dirty tab; absent after save; Discard drops edit | `unsaved-dialog.ui.spec.ts` |
| Arr dropdown → reachability (is-ok); Compare → **successful** comparison card | `arr.ui.spec.ts` |
| Recommendations user selector → WatchProfile response (documented status); sections toggle | `recommendations.ui.spec.ts` |

---

## Still NOT covered (and why)
- ⚠️ **Real Radarr/Sonarr/Seerr servers** — replaced by mocks by design; mocks return the exact
  response shapes the plugin deserializes.
- ⚠️ **`DELETE /Trash/Folders` mass deletion** — routing/guards tested, not bulk removal (determinism).
- ⚠️ **`MaxRecommendationsPerUser` persistence** — no API update field by design (read-only / XML-only).
- ⚠️ **Trends chart hover tooltip** (mouse-driven SVG) — data validated at the API layer instead.
- ⚠️ **Discovery/My/Request full submission as the non-admin user** — permission + service paths are
  covered; the end-to-end user submit is left for a later pass (admin `Discovery/Request` IS covered).

## First-run caveat
Type-checked and infra-validated. The stack now boots (Jellyfin healthy, media generated, deps
installed); the startup-wizard flow was corrected per the JF12 source (POST /Startup/User configures
the pre-existing admin and 403s if it already has a password — no longer swallowed). Expect possible
UI selector/timing tweaks on first full green run; failures come with trace + screenshot + video.
