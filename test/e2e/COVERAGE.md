# E2E Coverage Matrix

What the end-to-end suite exercises, mapped to the test that covers it —
endpoints, task modes, settings, backup, trends, trash, authorization, and
every UI interaction. **207 tests** (API + UI) across the spec files.

Beyond "does it route / does the UI render", the suite now proves features
**actually work on disk** and that **misuse breaks nothing**:
- **Behavioral (filesystem-verified):** cleanup deletes the orphan and keeps the
  valid file; trash actually moves items and purges by retention date; link
  repair rewrites the right `.strm`/symlink and refuses the wrong ones; Seerr
  cleanup deletes exactly the expired requests; backup round-trips real data.
  These read the container FS via `docker exec` (see `setup/fs-assert.ts`); when
  Docker isn't reachable from the test host they **skip loudly**, never pass
  vacuously.
- **Adversarial (canary-guarded):** fat-finger and hostile inputs must fail
  cleanly (400/502/504, never 500/hang), and **canary files planted outside
  `/media` must survive every destructive test** — the proof that no misuse can
  delete or move data outside the media library.

## How to see coverage live

- **HTML report:** after any run — `cd test/e2e && npx playwright show-report`.
  Lists every test, pass/fail, timings; on failure embeds trace + screenshot + video.
- **In CI:** the `E2E (Docker)` workflow uploads `e2e-playwright-report` on every
  run (plus a separate `e2e-traces` artifact with traces/screenshots on failure).
  The Jellyfin/mock container logs are **printed into the job log** on failure
  (not bundled into an artifact). PR → Checks → E2E → Artifacts.
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

## 7b. Authorization gating → `authz.api.spec.ts`
- **Non-admin denied (401/403) on every `[RequiresElevation]` controller** — GET, PUT/DELETE
  and POST matrices across Configuration, Backup, Trash, Arr/Seerr, Discovery-admin,
  Recommendations, UserActivity, stats/trends, Logs.
- Admin positive control (elevated GET is allowed → not a blanket 403 from broken auth).
- `Translations` is `[AllowAnonymous]`: reachable with no auth header; an elevated endpoint 401s anonymously.

## 7c. Settings validation & contracts → `settings.api.spec.ts` (extended)
- Arr instance rules: no-key → 400, >3 → 400, name >100 → 400, fully-blank row skipped.
- Seerr URL with blank key → 400 (no mutation); invalid scheme → 400 (no mutation).
- Ensemble alpha invariant: after any save `0 ≤ min ≤ max ≤ 1`, penalty floor clamped to [0,1].
- Unsupported/injection Language coerces to `en`.
- Config-save strictly blocks traversal / invalid-char / blank-when-enabled trash paths (400, no mutation).
- LogLevel-differing save returns a non-empty `Warnings[]` and leaves the level unchanged.

## 7d. Backup state-integrity & hardening → `backup.api.spec.ts` (extended)
- Redacted re-import **preserves** the live Seerr key (empty value = leave in place; `CredentialsChanged` false).
- Traversal trash path in a backup is defanged to `.jellyfin-trash` on restore.
- Invalid `seerrUrl` scheme in a backup → 400 (hard validator error survives the sanitizer).
- Out-of-range numerics clamp to a 200 restore (not 400); persisted values stay in-range.
- Import success summary is a PascalCase four-field object; `CredentialsChanged` flips on a new key.
- Wrong Content-Type rejected (400/415) before body read.

## 7e. Trash contract & path-safety → `trash.api.spec.ts`
- `Trash/Folders` shape (`IsAbsolute`, `Paths[]`); `Trash/Contents` shape (`UseTrash`, `RetentionDays`, `Libraries[]`).
- `CheckAccess` rejects traversal + overlong; missing body/field → 400 `{Error}`.
- `Relocate` error-body contract: traversal → bare string, missing field → `{Error}` object.

## 7f. Logs & Translations API → `logs.api.spec.ts`
- Logs envelope `{TotalBuffered, Returned, Entries}` with `Returned === Entries.length`; entry `Level` in the valid set.
- Invalid `minLevel` → 400; lowercase level accepted (OrdinalIgnoreCase); `limit` clamped; `source` >200 → 400 (200 boundary OK).
- `Logs/Download` validates `minLevel` and serves timestamped `text/plain`.
- Translations happy-path returns a non-empty string map for `en`/`de`.

## 8. Integrations (mock green-path + validation) → `integrations.api.spec.ts` (extended)
- Radarr/Sonarr connection test + Compare bucketing; Seerr connection test.
- `Seerr/Test` scheme guard (non-HTTP(S) → 400 exact message); blank URL/key/null body → 400.
- Arr Compare 502 aggregation names the failing instance (force-fail key).

## 8. User-facing Discovery → `discovery-my.api.spec.ts`  ← NEW
- **403 gating:** every `Discovery/My/*` endpoint returns 403 when
  `DiscoveryUserAccessEnabled` is off (tested as a real **non-admin user**).
- **Enabled flow:** My, ExternalLinks, RequestPermissions, Services respond (not 403).
- `Discovery/My/script` served anonymously (JS content-type).
- `Discovery/My/Dismiss` records dismissal.
- **Admin gaps closed:** `Discovery/Request` submission to mock; `Trash/Relocate` move.

## 9. UI — all 8 tabs → `tabs.ui.spec.ts`
- Overview, Codecs, Health, Trends, Settings, Arr, Logs switch + activate, **no uncaught
  JS errors** (failed-resource-load status noise is filtered; real pageerror/console.error
  JS still fails). Overview renders stat cards after scan. The Arr and Recommendations UI
  specs now **self-provision** their preconditions (configure Mock Radarr / set a
  non-Deactivate recs mode via API in `beforeAll`) instead of relying on leftover state,
  so they run rather than skip.

## 10. UI — interactions
| Covered | File |
|---|---|
| Codec breakdown row → file tree; folder expand/collapse; Expand/Collapse All; re-click closes | `trees.ui.spec.ts` |
| Health item → detail tree | `trees.ui.spec.ts` |
| Logs arrive + **download file**; level filter → PUT /LogLevel **succeeds + persists DEBUG**; clear → DELETE **succeeds + empty state** | `logs.ui.spec.ts` |
| **Unsaved dialog** — dirty band; appears on leaving dirty tab; absent after save; Discard drops edit | `unsaved-dialog.ui.spec.ts` |
| Arr dropdown → reachability (is-ok); Compare → **successful** comparison card | `arr.ui.spec.ts` |
| Recommendations user selector → WatchProfile response (documented status); sections toggle | `recommendations.ui.spec.ts` |
| Overview **Scan Libraries** button → ScanLibraries + button re-enable lifecycle | `interactions.ui.spec.ts` |
| Settings task-mode change → **quiet auto-save** PUT (no unsaved band) | `interactions.ui.spec.ts` |
| Trends **insight cards** → expand + mutual-collapse | `interactions.ui.spec.ts` |
| Settings Seerr **Test Connection** → POST /Seerr/Test (expands section, fills inputs) | `interactions.ui.spec.ts` |
| Settings **Export Backup** → file download | `interactions.ui.spec.ts` |
| Settings **folder-browser** → opens overlay (enables UseTrash fieldset first) | `interactions.ui.spec.ts` |

## 11. API contract pinning → `contracts.api.spec.ts`
Endpoints that smoke only *routed* or hardening only *tolerated a status class*
([200,400,404,503]) — pinned here so a regression flipping a branch can't slip
through:
- **503 Deactivate guards** on all Recommendation + UserActivity endpoints (message asserted).
- **empty-GUID → 400** on `Recommendations/{id}`, `Recommendations/WatchProfile/{id}`, `UserActivity/User/{id}`.
- `UserActivity/User/{id}`: valid-but-unknown user → **404**; `maxResults` clamp holds.
- `Discovery/Request` validation → **400** — documents the actual `ValidationProblemDetails`
  envelope from `[ApiController]` (TmdbId/MediaType messages), plus the null-body 400.
- `Ping` → `{Ok:true, Plugin:"JellyfinHelper", Version}` liveness contract.
- `Translations` no-`lang` → configured-language fallback (non-empty map); malformed `lang` → **400** pinned.
- `Configuration/Libraries` + `Configuration/LibraryPaths` response shapes.
- `GrowthTimeline?forceRefresh=true` recompute path (200 or 429 + `Retry-After`).

---

## 12. Behavioral — features actually work on disk (`*-fs.api.spec.ts`)
Filesystem-verified via `docker exec` (skips loudly without Docker):
- **Cleanup discrimination** (`cleanup-fs`): each stage in isolation deletes the
  orphan AND keeps the valid — video-backed `.trickplay` survives, matching &
  multi-language subtitles survive, `.DTS` non-language orphan removed,
  metadata-only / audio-only / nested-video folders survive; **DryRun leaves all
  orphans on disk**; permanent-delete creates no trash; **age gating** keeps a
  too-new orphan then removes it at 0.
- **Trash move + retention** (`trash-fs`): orphan leaves the library and appears
  under `.jellyfin-trash` as `yyyyMMdd-HHmmss_<name>` with contents intact;
  **expired entries purge by name-timestamp, fresh survive**; `retention<=0`
  disables purge; foreign non-timestamp entries untouched.
- **Link repair** (`link-repair-fs`): repairable `.strm` rewritten to its lone
  sibling (**DryRun leaves it byte-unchanged first**); ambiguous / broken / URL
  targets untouched; **broken symlink repaired, valid symlink unchanged**;
  relative-traversal and absolute-out-of-library targets refused.
- **Seerr cleanup** (`seerr-cleanup`): **exact-id** deletion (expired
  pending/declined gone; status 2/4/5 + recent + inside-age-boundary survive);
  DryRun deletes nothing; incomplete-snapshot (force-fail) deletes nothing.
- **Recommendations playlists** (`recommendations-playlist`): Activate+sync
  creates then Deactivate purges; cache written on Activate, absent on DryRun.
- **Backup round-trip** (`backup.api.spec.ts` extended): full config field-set,
  growth timeline via `GET GrowthTimeline`, Arr credential preserve-then-change.

## 13. Adversarial — misuse breaks nothing (canary-guarded)
Every destructive case asserts library-external **canary files survive**:
- **Trash escape** (`trash-abuse`): absolute `/config` trash path via
  `DELETE /Trash/Folders` and every `Trash/Relocate` branch is refused; config
  dir intact. (Regression coverage for the fixed FS-escape bugs.)
- **Config** (`config-adversarial`): `LogLevel` null body → 400 (was 500);
  unknown enum atomic-reject; 10k-instance array bounded; XML-hostile input
  no-corrupt; racing PUTs converge to one coherent set.
- **Backup** (`backup-adversarial`): `[null]` instance no 500; absolute `/config`
  trash path from a backup can't make cleanup escape; NaN/Infinity/overflow,
  depth-bomb (bounded), array/truncated bodies all `<500`.
- **Integrations** (`integrations-adversarial`): SSRF targets never succeed/hang;
  non-HTTP schemes → exact-message 400; high-byte keys no 500; slow/giant/garbage
  upstreams degrade cleanly; Compare index overflow handled.
- **Cleanup** (`cleanup-abuse`): symlink-out-of-library target survives cleanup;
  excluded library + its trash fully hands-off; emoji/long names ok.
- **Discovery** (`discovery-abuse`): write endpoints 403 when access disabled
  with **no leak to Seerr**; adversarial Dismiss inputs 4xx; identity-spoof
  `SeerrUserId` not forwarded.

---

## Still NOT covered (and why)
- ⚠️ **Real Radarr/Sonarr/Seerr servers** — replaced by mocks by design; mocks return the exact
  response shapes the plugin deserializes.
- ⚠️ **`DELETE /Trash/Folders` mass deletion** — routing/guards tested, not bulk removal (determinism).
- ⚠️ **`MaxRecommendationsPerUser` persistence** — no API update field by design (read-only / XML-only).
- ⚠️ **Trends chart hover tooltip** (mouse-driven SVG) — data validated at the API layer instead.
- ⚠️ **Discovery/My/Request full submission as the non-admin user** — permission + service paths are
  covered; the end-to-end user submit is left for a later pass (admin `Discovery/Request` IS covered).
- ⚠️ **`Trash/Relocate` / `Trash/CheckAccess` / `Trash/FoldersForPath` SUCCESS-body contracts** —
  error branches + a loose `<500` relocation move are covered (`trash.api.spec.ts`,
  `discovery-my.api.spec.ts:139`); the exact `{Moved, Failed}` / `{AllAccessible, Results[]}` /
  `{Paths[], IsAbsolute}` success shapes are not yet pinned.
- ⚠️ **Settings dialogs** — trash-disable "Keep/Delete" + trash-path-change relocation dialogs, the
  Excluded-Libraries multi-select, and the Backup **Import** confirm dialog are not yet UI-driven
  (Export is; the backup API round-trip is fully covered).

## First-run caveat
Type-checked and infra-validated. The stack now boots (Jellyfin healthy, media generated, deps
installed); the startup-wizard flow was corrected per the JF12 source (POST /Startup/User configures
the pre-existing admin and 403s if it already has a password — no longer swallowed). Expect possible
UI selector/timing tweaks on first full green run; failures come with trace + screenshot + video.
