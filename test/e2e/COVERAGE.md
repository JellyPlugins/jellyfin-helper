# E2E Coverage Matrix

What the end-to-end suite exercises, mapped to the test that covers it —
endpoints, task modes, settings, backup, trends, trash, authorization, and
every UI interaction. **246 tests** (API + UI) across 38 spec files
(authoritative count: `cd test/e2e && npx playwright test --list`).

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
- API-key mask `***` **functionally proven** to preserve the stored key: after a
  `***` (and whitespace-padded `' *** '`) re-save, an admin `Discovery/Request`
  still authenticates to the mock with the real key — the mock now 401s a literal
  `***`, so a wipe-to-mask would fail the test. `SeerrCleanupAgeDays→0` when URL blank.
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
  and POST matrices across Configuration, Backup, Trash (incl. the destructive
  `Trash/Relocate` and `Trash/FoldersForPath`), Arr/Seerr, Discovery-admin,
  Recommendations, UserActivity, stats/trends, Logs.
- Admin positive control (elevated GET is allowed → not a blanket 403 from broken auth).
- `Translations` is `[AllowAnonymous]`: reachable with no auth header; an elevated endpoint 401s anonymously.
- The non-admin-dependent tests use a shared `requireNormalUser()` guard: in CI
  (`E2E_REQUIRE_NORMAL_USER=1`, set in both workflows) a missing fixture **fails**
  rather than silently skips, so the authorization matrix can't vanish green.
  global-setup also hard-fails at setup under the same flag if provisioning breaks.

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

## 7g. Backup versioning & config schema-evolution → `migration.api.spec.ts`
- Forward-dated `backupVersion` (2) → 400 with an `errors[]` naming the unsupported version
  (only `{1}` is accepted; `BackupValidator.MaxBackupVersion=1`); 999 / -1 / 0 likewise 400.
- **Missing** `backupVersion` → accepted (deserializes to the C# default 1), 200 with `ConfigurationRestored`.
- Non-numeric `backupVersion` ("abc") → a **distinct** 400 (`could not parse`, no `errors[]`), separating
  the parse-failure branch from the version-range validation branch.
- Older-shaped backup (newer fields absent) restores with safe defaults: `DiscoveryUserAccessEnabled`
  and `SyncRecommendationsToPlaylist` → false, `RecommendationsTaskMode` → `DryRun` (ParseTaskMode
  fallback), and a null `SeerrCleanupAgeDays` leaves the prior live value unchanged.
- Unknown/removed fields are silently ignored on both backup import and `PUT /Configuration` (no reject).
- `GET /Configuration` exposes an inert numeric `ConfigVersion` (pinned so a future migration can build on it).

## 7h. Idempotency — repeated mutations converge → `idempotency.api.spec.ts`
- Re-importing the SAME secrets backup: `CredentialsChanged` flips **true → false** (run 2's key already
  matches), and every restored config scalar is identical after both imports (pure overwrite, no drift).
- `PUT /Configuration` twice with the same body → identical `GET` state (keys sent masked so no
  network-dependent connection-test warnings make the assertion flaky).
- Admin `Discovery/Request` submitted twice → the mock's forwarded-request count increments to **2**:
  the plugin deliberately does **not** dedupe the upstream Seerr submission (local cache/feedback
  bookkeeping dedupes; the submission does not) — asserting the correct behavior, not a wrong "dedupe".
- `Trash/Relocate` of an already-drained source → clean no-op `{Moved:0, Failed:0}` 200, destination
  untouched (filesystem-backed; skips loudly without docker).

## 7i. Concurrency invariants → `concurrency.api.spec.ts`
- Concurrent `GET GrowthTimeline?forceRefresh=true` (Promise.all): the process-static semaphore + 30s
  throttle let **at most one** recompute (200); every rejected one is a **429 with a numeric Retry-After**
  — never a 500 or a 2nd concurrent compute. The cached read-back is coherent (non-negative, non-decreasing
  cumulative series → no torn write). Asserted as an interleaving-safe invariant (not a strict [200,429]
  pair, which would be flaky since `_lastRefreshTime` is process-static).
- Racing `PUT /Configuration/LogLevel` (10 concurrent, alternating DEBUG/ERROR): `ReadAndMutate` serializes
  the writes, so the stored `PluginLogLevel` is exactly one submitted value — never torn/invalid, never 500.
  (Complements the existing racing-`RadarrInstances`-PUTs test in `config-adversarial.api.spec.ts`.)

## 7j. Partial downstream failure — Seerr cleanup → `seerr-cleanup.api.spec.ts` (extended)
- **Page-2 fetch fails mid-pagination** (mock `force-fail-page2`: page 1 succeeds and reports a 2nd page,
  page 2 at skip=50 → 500): the plugin's incomplete-snapshot guard aborts ALL deletions — the full seeded
  id set survives, including ids 101/102/108 that page 1 alone would have marked deletable. A `/list-calls`
  hook proves page 1 (200) AND page 2 (500) were both observed, so this genuinely exercises the page-2
  branch (unlike the existing `force-fail` test, which 500s the very first call).

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

## 8. User-facing Discovery → `discovery-my.api.spec.ts` + `discovery-request-auth.api.spec.ts`
- **403 gating:** every `Discovery/My/*` endpoint returns 403 when
  `DiscoveryUserAccessEnabled` is off (tested as a real **non-admin user**).
- **Enabled flow:** My, ExternalLinks, RequestPermissions, Services respond (not 403).
- `Discovery/My/script` is served **anonymously** — fetched with **no auth header**
  (a bare context, not the admin token) and must return JS, not 401/403.
- `Discovery/My/Dismiss` records dismissal.
- **Request authorization** (`discovery-request-auth`): the non-admin user is linked
  in global-setup to the mock's second Seerr user with the Request permission, so
  `POST /Discovery/My/Request` drives the real auth branches — ServerId-without-
  ProfileId → 400; unmatched (ServerId,ProfileId) → 403; wrong RootFolder → 403; a
  valid override AND a no-override submission → success, **forwarded to Seerr with
  the caller's resolved SeerrUserId** (verified via the mock's `/last-request`); the
  10s per-user rate limit → 2nd request 429 + `Retry-After`, and a rejected request
  does not extend the window.
- **Admin gaps closed:** `Discovery/Request` submission to mock; `Trash/Relocate`.
- The two admin-side tests here **snapshot and restore** the shared Seerr/Trash
  configuration (afterAll), so they don't leak state into later specs.

## 8b. Sidebar script injection (fallback path) → `sidebar-injection.api.spec.ts`
The e2e stack ships **no File Transformation plugin**, so the Discovery sidebar can
only appear via the plugin's **disk-write fallback** patching Jellyfin's
`index.html`. This is the exact path that breaks for real users on read-only web
dirs; the container's web dir is writable, so the fallback **must** succeed — the
end-to-end proof the unit tests cannot give (the tag is served by a live Jellyfin).
Timing note: Jellyfin 12 serves `index.html` from disk on every request (no
in-memory page cache), and the fallback writes during plugin startup — which trails
the server becoming reachable — so the test **polls patiently** (a browser reload is
enough once the write lands; no restart/cache-bust needed).
- **Injection happened:** `GET /web/index.html` eventually contains the injected
  `<script plugin="Jellyfin Helper" … src="…/JellyfinHelper/Discovery/My/script">`.
- **Idempotent:** the tag appears **exactly once** despite injection running twice
  per start (plugin ctor + `DiscoverySidebarInjectionService` hosted service, both
  under a lock) — guards against `RemovalRegex` regressions that would stack tags.
- **src reachable:** the injected script URL resolves and serves `javascript`.
- Plugin stays **Active** throughout (startup injection didn't destabilise boot).

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
- **Cleanup discrimination** (`cleanup-fs.api.spec.ts`): each stage in isolation deletes the
  orphan AND keeps the valid — video-backed `.trickplay` survives, matching &
  multi-language subtitles survive, `.DTS` non-language orphan removed,
  metadata-only / audio-only / nested-video folders survive; **DryRun leaves all
  orphans on disk**; permanent-delete creates no trash; **age gating** keeps a
  too-new orphan then removes it at 0.
- **Trash move + retention** (`trash-fs.api.spec.ts`): orphan leaves the library and appears
  under `.jellyfin-trash` as `yyyyMMdd-HHmmss_<name>` with contents intact;
  **expired entries purge by name-timestamp, fresh survive**; `retention<=0`
  disables purge; foreign non-timestamp entries untouched; an **expired symlinked
  entry is unlinked but its target survives byte-for-byte** (reparse-point =
  link-only delete, guarding the recursive-delete data-loss path).
- **Trash relocate** (`trash-relocate-fs.api.spec.ts`): all four abs/rel quadrants move REAL
  seeded content — `Moved>0`/`Failed==0`, source folder emptied+removed, and the
  destination holds exactly the moved entry with a matching **sha256**; plus the
  `Trash/CheckAccess` **success** path (`AllAccessible=true` with per-library
  read/write probes).
- **Link repair** (`link-repair-fs.api.spec.ts`): repairable `.strm` rewritten to its lone
  sibling (**DryRun leaves it byte-unchanged first**); ambiguous / broken / URL
  targets untouched; **broken symlink repaired, valid symlink unchanged**;
  relative-traversal and absolute-out-of-library targets refused.
- **Seerr cleanup** (`seerr-cleanup.api.spec.ts`): **exact-id** deletion (expired
  pending/declined gone; status 2/4/5 + recent + inside-age-boundary survive);
  DryRun deletes nothing; incomplete-snapshot (force-fail) deletes nothing.
- **Recommendations playlists** (`recommendations-playlist.api.spec.ts`): Activate+sync
  creates then Deactivate purges; cache written on Activate, absent on DryRun.
- **Recommendations ranking** (`recommendations-ranking.api.spec.ts`): the engine
  consumes a REAL watch profile — `WatchProfile/{userId}` reflects played items,
  `Recommendations/{userId}` EXCLUDES anything watched, and results are ranked
  (Score in [0,1], sorted descending).
- **Media statistics** (`media-stats-fs.api.spec.ts`): codec / resolution / health
  breakdowns match the KNOWN fixtures — H.264 / HEVC / MPEG-4 keys with positive
  counts, sub-less clips reflected in the no-subtitle health count.
- **Growth timeline** (`growth-timeline-fs.api.spec.ts`): the cumulative series is
  non-empty and monotonically non-decreasing, latest totals are positive/coherent
  (bytes > 0, files > 0), directories-scanned positive, no future-dated point.
- **Library insights** (`insights-fs.api.spec.ts`): "largest dirs" sorted by size
  descending over real `/media` dirs with `LargestTotalSize == sum(sizes)`, a known
  generated movie present — ranking/aggregate invariants that hold despite the 15m cache.
- **User activity** (`user-activity-fs.api.spec.ts`): mark an item PLAYED via
  Jellyfin's API, rebuild the activity cache, and assert it surfaces as watched in
  both `UserActivity/Latest` and `UserActivity/User/{userId}` with a matching play count.
- **Backup round-trip** (`backup.api.spec.ts` extended): full config field-set,
  growth timeline via `GET GrowthTimeline`, Arr credential preserve-then-change.

## 12b. Feature coverage — folder browser & per-stage task modes
- **Folder browser** (`folder-browser.api.spec.ts`): behavioral (browsing roots and
  a known media dir lists real children; going up works; LibraryPaths lists the
  configured libraries) **plus** adversarial/hardening — read-only endpoint refuses
  any mutation, rejects `..` traversal, non-absolute paths, NUL bytes, and sensitive
  system dirs; validation failures surface as HTTP 200 with a non-null `Error` body.
- **Per-stage task modes** (`task-modes.api.spec.ts`): proves each mode value does
  what it promises at STAGE granularity in one mixed pass — Deactivate skips
  entirely (orphan survives, no dry-run log), DryRun runs+logs but changes nothing
  on disk, Activate performs the real delete/trash move — distinguishing Deactivate
  from DryRun (which `cleanup-fs` cannot) and proving modes are honoured independently.

## 13. Adversarial — misuse breaks nothing (canary-guarded)
Every destructive case asserts library-external **canary files survive**. The
canaries are (re-)planted inside each destructive spec's `beforeAll`/`beforeEach`
via `ensureCanariesPlanted()`, which also asserts at least one canary is actually
present — so `verifyCanaries()` can never pass vacuously against an empty set, and
the check works inside Playwright's worker processes (not just the global-setup
process that first plants them). Without Docker the destructive specs skip loudly.
- **Trash escape** (`trash-abuse.api.spec.ts`): absolute `/config` trash path via
  `DELETE /Trash/Folders` and every `Trash/Relocate` branch is refused; config
  dir intact. (Regression coverage for the fixed FS-escape bugs.)
- **Config** (`config-adversarial.api.spec.ts`): `LogLevel` null body → 400 (was 500);
  unknown enum atomic-reject; 10k-instance array → 400 with a **pre-existing
  known-good instance preserved** (not a vacuous length check); XML-hostile input
  no-corrupt; racing PUTs converge to one coherent set.
- **Backup** (`backup-adversarial.api.spec.ts`): `[null]` instance is **sanitized away** (a
  re-export proves no null persists) and a mixed `[valid,null]` keeps the valid
  one; absolute `/config` trash path from a backup can't make cleanup escape;
  NaN/Infinity/overflow, depth-bomb, array/truncated bodies → **exact 400** at the
  JSON parse layer (never 500).
- **Integrations** (`integrations-adversarial.api.spec.ts`): SSRF targets never succeed/hang;
  non-HTTP schemes → exact-message 400 on **both** `Seerr/Test` **and**
  `ArrIntegration/TestConnection`; high-byte keys no 500; slow/giant/garbage
  upstreams degrade cleanly; Compare index overflow handled.
- **Cleanup** (`cleanup-abuse.api.spec.ts`): symlink-out-of-library target survives cleanup;
  excluded library + its trash fully hands-off; emoji/long names ok.
- **Discovery** (`discovery-abuse.api.spec.ts`): write endpoints 403 when access disabled
  with **no leak to Seerr**; adversarial Dismiss inputs 4xx; identity-spoof
  `SeerrUserId` not forwarded.

## 14. Documentation drift guard → `coverage-doc.api.spec.ts`
A pure-filesystem meta-test (no Jellyfin stack needed) that reads the `tests/`
directory and asserts **every `*.spec.ts` is referenced by filename in this file**.
Add a spec without documenting it here and this guard fails — so this coverage map
cannot silently fall out of date. The guard excludes only itself.

---

## Still NOT covered (and why)
- ⚠️ **Real Radarr/Sonarr/Seerr servers** — replaced by mocks by design; mocks return the exact
  response shapes the plugin deserializes.
- ⚠️ **Backup restore partial-failure / "manual-recovery" branch** (`BackupService.RestoreBackup`) —
  only triggers when a timeline/baseline file write succeeds and a later step throws. File writes
  swallow I/O errors (return false, no throw) and every restored config value is clamped/sanitized
  before write, so validated HTTP input cannot make the config-restore step throw. Reaching it needs
  filesystem/permission tampering — deliberately out of scope for the HTTP-only `migration.api.spec.ts`
  rather than faked with a vacuous assertion. Covered instead at the unit level.
- ⚠️ **XML config schema migration on load** (obsolete-element discarding, clamp-report startup
  warnings) — happens during `XmlSerializer` load of the on-disk config; no HTTP endpoint feeds
  arbitrary XML. The clamp *effect* is testable via a config round-trip; the load-time warning is not.
- ⚠️ **`DELETE /Trash/Folders` mass deletion** — routing/guards tested, not bulk removal (determinism).
- ⚠️ **`MaxRecommendationsPerUser` persistence** — no API update field by design (read-only / XML-only).
- ⚠️ **Trends chart hover tooltip** (mouse-driven SVG) — data validated at the API layer instead.
- ⚠️ **`Trash/FoldersForPath` SUCCESS-body contract** (`{Paths[], IsAbsolute}`) — its auth gating and
  error branches are covered; the exact success shape is not yet pinned. (`Trash/Relocate`'s
  `{Moved, Failed}` and `Trash/CheckAccess`'s `{AllAccessible, Results[]}` success shapes ARE now
  pinned in `trash-relocate-fs.api.spec.ts`.)
- ⚠️ **Settings dialogs** — trash-disable "Keep/Delete" + trash-path-change relocation dialogs, the
  Excluded-Libraries multi-select, and the Backup **Import** confirm dialog are not yet UI-driven
  (Export is; the backup API round-trip is fully covered).

## First-run caveat
Type-checked and infra-validated. The stack now boots (Jellyfin healthy, media generated, deps
installed); the startup-wizard flow was corrected per the JF12 source (POST /Startup/User configures
the pre-existing admin and 403s if it already has a password — no longer swallowed). Expect possible
UI selector/timing tweaks on first full green run; failures come with trace + screenshot + video.
