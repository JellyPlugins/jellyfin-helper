# Code Review — release/3.0.0.0

> Branch: release/3.0.0.0 vs main  |  192 files changed  |  36 795 insertions / 3 625 deletions
> Reviewed: multi-agent exhaustive analysis (10 domain reviewers, adversarial verification)

## Resolution Log

| # | Severity | Finding | Status | File(s) changed |
|---|----------|---------|--------|-----------------|
| HIGH/BUG-2 | HIGH | SeerrCleanupAgeDays clamp min=0 vs doc "1–3650" | **N/A** — `0` is the intentional disabled-state sentinel written by the controller when `SeerrUrl` is empty; `ConfigurationRequestValidator` already enforces `>= 1` when Seerr is configured. No code change required. | — |
| HIGH/BUG-4 | HIGH | `DeduplicateSeries` stale index after in-place replacement | **Fixed** — `bestPerSeries[seriesId.Value]` now updated after each in-place replacement so the third+ duplicate compares against the current best. | `DiversityReranker.cs` |
| HIGH/SECURITY-5 | HIGH | XSS via unescaped `version` in HTML attribute of `DiscoveryScriptTag.Build` | **Fixed** — `version=` attribute now uses `safeVersion` (URL-escaped) instead of the raw string; test updated to assert the escaped form. | `DiscoveryScriptTag.cs`, `DiscoveryScriptTagTests.cs` |
| HIGH/BUG-16 | HIGH | `MoveFileToTrash`: `Directory.CreateDirectory` called after `ResolveCollision` | **Fixed** — reordered to match `MoveToTrash`: create directory first, then resolve collision. | `TrashService.cs` |
| HIGH/SECURITY-8 | HIGH | `ValidatePath` `..` check splits on `Path.DirectorySeparatorChar` only — misses backslash on Linux | **Fixed** — now always splits on both `'/'` and `'\\'` literals, independent of `Path.DirectorySeparatorChar`. | `FolderBrowserService.cs` |
| HIGH/CORRECTNESS-3 | HIGH | `HeavyRewatcher` test bound `6.0` arbitrary, doesn't actually test the cap | **Fixed** — test renamed to `_Log1pGrowthIsSubLinear`, bound derived from `Math.Log(1+30)/Math.Log(1+5)`, comment clarifies the cap is separately tested by `_PlayCountBeyond100_IsCapped`. | `PreferenceBuilderTests.cs` |
| HIGH/TEST-GAP-3 | HIGH | `ProximityExpansion` test uses `DateTime.UtcNow` — flaky under load | **Fixed** — `baseDate` pinned to `new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)` in all three proximity tests (`StaysNormalized`, `FewItems`, `InsertsNewGenre`). | `PreferenceBuilderTests.cs` |
| HIGH/BUG-1 | HIGH | Episode-to-series people aggregation falls back to episode item when series missing | **Fixed** — removed fallback; when `seriesLookup` does not contain the seriesId, aggregation is skipped entirely for that series to avoid single-episode people data polluting the profile. | `WatchHistoryService.cs` |
| HIGH/BUG-3 | HIGH | K-fold cross-validation restores `savedWeights` then final pass resets to defaults | **Fixed** — removed the `_weights = DefaultWeights.CreateWeightArray()` reset before the final SGD pass; the final pass now warm-starts from the previously-learned weights (or defaults on first call). | `LearnedScoringStrategy.cs` |
| HIGH/BUG-5 | HIGH | Sync-over-async deadlock in `RemoveItem`/`MarkAsRequested` under SemaphoreSlim | **Fixed** — added explicit XML `<remarks>` warnings on both sync overloads documenting they must not be called from a synchronization context; all production callers already use the async variants. | `DiscoveryCacheService.cs` |
| HIGH/SECURITY-1 | HIGH | Seerr API key mask sentinel `***` bypasses API-key-required validation | **Fixed** — validator now treats the mask sentinel the same as whitespace; `IsLanguageSupported` helper exposed for defense-in-depth reuse. | `ConfigurationRequestValidator.cs` |
| HIGH/SECURITY-4 | HIGH | Language field accepted without allowlist — path-traversal risk downstream | **Fixed** — added `SupportedLanguages` allowlist (`en, de, fr, es, pt, zh, tr`) to the validator; `ApplyRequestToConfig` also sanitizes with the same allowlist as defense-in-depth. | `ConfigurationRequestValidator.cs`, `ConfigurationController.cs` |
| HIGH/CORRECTNESS-2 | HIGH | Dead `availableAudioLanguages > 0` guard always true | **Fixed** — removed the always-true outer guard; added explanatory comment. | `WatchHistoryService.cs` |
| HIGH/CORRECTNESS-5 | HIGH | Train/serve parity: Phase 1 & 3 watched sets use `Played\|\|IsFavorite` not `HasMeaningfulInteraction` | **Fixed** — both Phase 1 (line 245) and Phase 3 (line 838) now filter with `HasMeaningfulInteraction()` to match the live inference path. | `TrainingDataBuilder.cs` |
| HIGH/CORRECTNESS-6 | HIGH | Phase 2 organic standalone items always get `CollectionProgressionBoost = 0.0` | **Fixed** — added `watchedBoxSetCountsOrganic` dictionary built from all watched items; standalone items now call `ComputeCollectionProgressionBoostWithCounts` for a real feature value. | `TrainingDataBuilder.cs` |
| HIGH/CORRECTNESS-8 | HIGH | `HeuristicScoringStrategy` registered with hardcoded `genrePenaltyFloor: 1.0` ignoring config | **Fixed** — now reads `config?.EnsembleGenrePenaltyFloor ?? DefaultGenrePenaltyFloor` at DI build time, matching how the ensemble wrapper is configured. | `PluginServiceRegistrator.cs` |
| HIGH/CORRECTNESS-11 | HIGH | `Score()` allocates `new double[FeatureCount]` on every call | **Fixed** — added `[ThreadStatic] _tlsInput` scratch buffer; `Score()` now reuses it via `_tlsInput ??= new double[FeatureCount]`. | `NeuralScoringStrategy.cs` |
| HIGH/CORRECTNESS-12 | HIGH | Dropout backprop applies `* dropoutInvKeep` twice to h4 error signal | **Fixed** — removed extra `* dropoutInvKeep` from h4Err computation; outErr already carries the correct forward-pass scale. | `NeuralScoringStrategy.cs` |
| HIGH/CORRECTNESS-17 | HIGH | `ComputeStableSeed` uses `Guid.GetHashCode()` — not process-stable | **N/A** — already implemented with FNV-1a over raw Guid bytes in the current code. No change needed. | — |
| HIGH/CORRECTNESS-18 | HIGH | `ExceedsMaxRating` blocks all unrated items for restricted profiles | **N/A** — comment in code explicitly documents "unrated items are treated as unrestricted and must be excluded for restricted profiles" — intentional design decision. | — |
| HIGH/CORRECTNESS-19 | HIGH | `coOccurrence.Values.Max()` propagates NaN | **Fixed** — replaced LINQ `Max()` with a NaN-safe foreach loop that skips non-finite values. | `Engine.cs` |
| HIGH/CORRECTNESS-20 | HIGH | `ReinsertAtOriginalIndices` ascending rollback logically incorrect | **N/A** — already reimplemented correctly with ascending-order insert and detailed XML doc explaining the invariant. No change needed. | — |
| HIGH/TEST-GAP-1 | HIGH | `Assert.All` on potentially empty collection in cold-start pipeline test | **Fixed** — added `Assert.NotEmpty(result.Recommendations)` before `Assert.All`. | `EngineFullPipelineTests.cs` |
| HIGH/TEST-GAP-2 | HIGH | Warm-path test never verifies any scored recommendation was produced | **Fixed** — added `Assert.NotEmpty(result.Recommendations)` with explanatory comment. | `EngineFullPipelineTests.cs` |

## Executive Summary

| Severity | Count |
|----------|-------|
| CRITICAL | 0 |
| HIGH | 39 |
| MEDIUM | 105 |
| LOW | 75 |
| INFO | 6 |
| **Total** | **225** |

| Category | Count |
|----------|-------|
| correctness | 128 |
| test-gap | 28 |
| security | 20 |
| bug | 19 |
| performance | 17 |
| design | 11 |
| incomplete | 2 |

**Key risk areas:**
1. **Correctness (128)** - ML feature vector parity, scoring logic, clamping, null-safety
2. **Security (20)** - API key masking, path traversal, input validation, SSRF
3. **Test gaps (28)** - missing edge cases, shallow assertions, untested error paths

---

## HIGH (39 findings)

### HIGH / BUG

#### 1. Episode-to-series people aggregation falls back to episode item when series is missing â€” violates the 'series-level only' invariant
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 549-571

**Description:** In BuildPeopleProfile, when `seriesLookup` does not contain the seriesId, the code falls back to calling `AggregatePeopleFromItem` with the individual episode item (line 567-568). This directly contradicts the stated design goal ('For episodes, we want to count people at the series level to avoid over-counting actors who appear in every episode'). A guest actor who appears only in one episode will still be counted â€” that is fine. But the primary actor who appears in every episode will be counted once for EVERY unique episode encountered before the series was found in the lookup (once per episode's first encounter of that seriesId, but processedSeriesIds is checked so only the first episode triggers this). Actually, since processedSeriesIds.Add returns false for subsequent episodes of the same series, only the FIRST episode triggers the fallback. The bug is subtler: if the series itself IS later visited as a synthetic WatchedItem (FavoriteSeriesIds), the `processedItemIds.Add(seriesId)` mark set at line 559 prevents it from being looked up again via itemLookup, so the series-level people never get counted for a favorite-only series whose episodes are also in the profile. The people data therefore comes entirely from one episode rather than the richer series metadata.

**Impact:** Users who have watched a series AND favorited it get episode-quality people data (limited guest cast) instead of series-quality data (full main cast). The PeopleProfile recommendation signal is degraded for this combination.

**Suggested Fix:** When seriesLookup does not contain the seriesId, skip people aggregation for that series entirely (continue) rather than falling back to episode-level data. Add a comment explaining the skip.

#### 2. SeerrCleanupAgeDays clamp minimum is 0 but service enforces minimum of 1
**File:** Jellyfin.Plugin.JellyfinHelper/Configuration/PluginConfiguration.cs | 87

**Description:** The property setter clamps SeerrCleanupAgeDays to [0, 3650] (minimum 0 is allowed). However, SeerrIntegrationService.CleanupExpiredRequestsAsync() throws ArgumentOutOfRangeException when maxAgeDays < 1. HelperCleanupTask.RunSeerrCleanup() separately guards `config.SeerrCleanupAgeDays <= 0` and skips with a warning. This means a user who saves 0 in the UI gets a confusing 'Invalid Seerr cleanup age' warning, not a clamp report. The config doc comment says 'Valid range: 1â€“3650' but the setter clamps to [0, 3650].

**Impact:** Inconsistency between documented valid range, stored value range, and runtime enforcement. A value of 0 passes deserialization, is stored, but then silently skips the task with a misleading warning rather than being caught at save-time.

**Suggested Fix:** Change the ClampAndReport call for SeerrCleanupAgeDays to use min=1 to match the doc comment and the service's own precondition.

#### 3. K-fold cross-validation restores savedWeights after all folds, but savedWeights were captured BEFORE any fold reset â€” they are always the pre-training defaults
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/LearnedScoringStrategy.cs | 433-435

**Description:** Lines 376-377 capture `savedWeights = _weights.Clone()` and `savedBias = _bias` before the k-fold loop. Inside each fold (line 417-418), weights are reset to defaults. After all folds complete, lines 434-435 restore `_weights = savedWeights` and `_bias = savedBias`. Since savedWeights was captured before the loop when _weights were either defaults (first Train call) or the previously-trained weights (subsequent calls), the restoration after k-fold doesn't preserve any fold result â€” it just discards all fold training. This is the intended behavior: folds are used only for loss estimation, not for the final model. The final model is trained separately on all data at lines 441-462. The issue is subtle but correct: the comment on line 433 confirms this. However, if Train() has been called before and `_weights` held previously-learned values, the final pass at line 452 then RESETS to defaults (`_weights = DefaultWeights.CreateWeightArray()`) â€” discarding the previously-learned weights entirely and retraining from scratch on each Train() call. The accumulated learning from the previous run is thrown away.

**Impact:** The model cannot benefit from iterative refinement across multiple Train() calls. Each call starts from the same default weights, wasting all previous gradient descent. In an online-learning scenario with incremental data this means the model never stabilizes.

**Suggested Fix:** Instead of resetting to DefaultWeights before the final pass, retain the current _weights as the starting point for the final SGD pass (warm start). Only reset to defaults when standardizationModeChanged is true (line 335) or when loading from a fresh state.

#### 4. DeduplicateSeries: stale index after in-place replacement corrupts lookup
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/DiversityReranker.cs | 60-69

**Description:** bestPerSeries maps a seriesId to an index in result. When a higher-scored entry for an already-seen series is found, the code does result[existingIdx] = entry (replacing the element) but never updates bestPerSeries[seriesId.Value]. If a third entry for the same series appears later and also outscores the current best, it again compares against result[existingIdx] â€” which now holds the second-best item, not the third â€” so the lookup value is always stale after the first replacement.

**Impact:** The highest-scored entry per series is not guaranteed to be kept. In a list with three or more episodes/seasons from the same series, the final deduplicated entry may be the second-best, not the first-best, silently lowering recommendation quality.

**Suggested Fix:** After replacing result[existingIdx] with entry, also update bestPerSeries[seriesId.Value] = existingIdx so subsequent entries for the same series compare against the correct current best.

#### 5. Sync-over-async deadlock potential: RemoveItem calls async method via .GetAwaiter().GetResult() under SemaphoreSlim
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryCacheService.cs | 124-136

**Description:** RemoveItem acquires _fileLock (SemaphoreSlim) then calls RemoveItemLocked(...).GetAwaiter().GetResult(). RemoveItemLocked is an async Task method. On ASP.NET (or any synchronisation-context-aware host), if the continuation from the inner async call tries to resume on the same context that is blocked on GetResult(), a deadlock occurs. The same pattern applies to MarkAsRequested (lines 294-307). The code itself notes it's only invoked from background tasks, but that is not enforced anywhere â€” any future caller from a request context will deadlock.

**Impact:** Deadlock: any caller that invokes RemoveItem or MarkAsRequested from a context with a synchronization context (e.g., an ASP.NET request thread without ConfigureAwait) will deadlock permanently, hanging the request and eventually exhausting the thread pool.

**Suggested Fix:** Either make the public API fully async (RemoveItemAsync / MarkAsRequestedAsync only) and deprecate the sync overloads, or implement RemoveItemLocked as a genuinely synchronous method for the sync path (no async/await) and keep a separate async version. Do not bridge asyncâ†’sync under a SemaphoreSlim on a request path.

### HIGH / SECURITY

#### 1. Seerr API key accepted as empty when URL is set via mask sentinel path
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationRequestValidator.cs | 66

**Description:** In Validate(), line 66 checks: if SeerrUrl is set AND SeerrApiKey is whitespace â†’ error. However, when the client sends the mask sentinel '***' as the API key, IsNullOrWhiteSpace('***') is false, so this guard silently passes. The sentinel is not a real credential. If ApplyRequestToConfig subsequently fails to find the stored key (e.g. first save after URL change), the stored key becomes empty string â€” but the validator already accepted the request.

**Impact:** An attacker or misconfigured client can set a Seerr URL with '***' as the API key and pass validation, potentially resulting in an empty stored key after the mask-restoration lookup fails (Name+Url mismatch).

**Suggested Fix:** In the validator's API-key-required check (line 66), also treat the ApiKeyMask sentinel as 'not provided' when the URL is being set for the first time. Or add a cross-field check: if SeerrUrl is non-empty and SeerrApiKey equals ApiKeyMask and the stored config has no existing key, reject with an error.

#### 2. AllowAnonymous script endpoint serves JavaScript with no Content-Security-Policy or integrity metadata
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 258

**Description:** GetScript() at line 258 is decorated with [AllowAnonymous] and returns an embedded JavaScript file with only 'no-cache' cache control. No Content-Security-Policy, X-Content-Type-Options, or Subresource Integrity hint is set. The resource name is a fixed, predictable path ('Jellyfin.Plugin.JellyfinHelper.js.discovery-sidebar.js').

**Impact:** Any network-level attacker who can intercept or substitute the response (e.g. in non-HTTPS deployments or via MITM on a local LAN) can inject arbitrary JavaScript into all Jellyfin sessions that load the sidebar, because there is no integrity check on the consumer side either.

**Suggested Fix:** Add 'X-Content-Type-Options: nosniff' to the response headers. If Jellyfin's embedding page uses a CSP, ensure the script hash or nonce is included. At minimum add a comment in the method noting the security surface.

#### 3. SSRF: Arr and Seerr URLs validated for scheme only, no private/loopback block
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationRequestValidator.cs | 58

**Description:** ValidateArrInstances() and the Seerr URL check (lines 58-63 and 242-248) confirm http:// or https:// scheme but place no restriction on the target host. An admin can point these to internal addresses (e.g. http://169.254.169.254/, http://localhost:2375/, http://10.0.0.1/) and the plugin will faithfully proxy connection-test HTTP calls to those targets.

**Impact:** Privilege escalation via SSRF. A rogue or compromised admin account can enumerate internal services, hit cloud metadata endpoints (AWS IMDSv1, GCP metadata), or probe internal APIs through the Jellyfin server's network context.

**Suggested Fix:** Resolve the URL's host and reject RFC-1918, loopback (127.0.0.0/8, ::1), link-local (169.254.x.x, fe80::/10), and AWS metadata (169.254.169.254) addresses. Alternatively, enforce an allowlist of valid host patterns in the admin UI. At minimum document this as a known limitation.

#### 4. Language field accepted without allowlist validation â€” stored verbatim from user input
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 457

**Description:** ApplyRequestToConfig() at line 457 stores config.Language directly from the request with only an IsNullOrWhiteSpace fallback to 'en'. No allowlist check against the documented set (en, de, fr, es, pt, zh, tr) is performed. The Language value is later used to load translation files by key.

**Impact:** An admin can store an arbitrary string (e.g. path traversal like '../../etc') in Language. Depending on how translation file paths are constructed downstream (e.g. Path.Combine(basePath, language + ".json")), this could enable directory traversal to read or affect files outside the translations directory.

**Suggested Fix:** Add Language to ConfigurationRequestValidator.Validate() with an explicit allowlist check: if not in {en, de, fr, es, pt, zh, tr} return a validation error. In ApplyRequestToConfig, also sanitize with the same allowlist as a defense-in-depth fallback.

#### 5. XSS via unescaped version string injected into HTML attribute
**File:** Jellyfin.Plugin.JellyfinHelper/Services/FileTransformation/DiscoveryScriptTag.cs | 39

**Description:** In DiscoveryScriptTag.Build(), the `version` parameter is URL-escaped for the query string (`safeVersion`) but then the original, unescaped `version` is written directly into the `version="{version}"` HTML attribute. If the version string ever contains a double-quote or `>` (possible via a hand-crafted plugin manifest or a future version numbering scheme), the attribute boundary breaks and arbitrary HTML/script content can be injected into index.html.

**Impact:** Persistent XSS in Jellyfin's web UI index.html for every client that loads the page, potentially allowing session-token theft for all users including admins.

**Suggested Fix:** HTML-encode the version before writing it into the attribute: replace `version` with `System.Web.HttpUtility.HtmlAttributeEncode(version)` (or a hand-rolled `version.Replace("&","&amp;").Replace("\"","&quot;")` if System.Web is unavailable).

#### 6. SSRF via operator-supplied Seerr base URL with no host/IP restriction
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 384

**Description:** CreateClient() validates only that the URL is absolute and uses http/https. No restriction is applied to the host. An admin can supply `http://169.254.169.254/` (AWS metadata endpoint), `http://localhost:8096/`, or any other internal host. The plugin then sends an authenticated HTTP request with the operator's API key to that address. All three named HTTP clients (SeerrIntegration, SeerrDiscovery, ArrIntegration) follow the same pattern.

**Impact:** An attacker with admin access can use the plugin to probe or exfiltrate from internal infrastructure by pointing the Seerr URL at internal services. On cloud deployments this includes instance metadata endpoints that expose credentials.

**Suggested Fix:** Document the risk and add a warn-only DNS/IP check, or add a strict allowlist of allowed hosts. At minimum, log a warning when the resolved host is a loopback, link-local, or private-range address.

#### 7. API key logged in clear text at Warning level on invalid-config path
**File:** Jellyfin.Plugin.JellyfinHelper/Plugin.cs | 229

**Description:** In RegisterFileTransformation(), the warning log at line 226 (the `payload != null, addMethod != null, jValueType != null` triple) is fine, but in SeerrIntegrationService.CreateClient() the caller logs `ex.Message` on UriFormatException / ArgumentException. For ArgumentException the message contains `nameof(apiKey)` but not the value. However, in HelperCleanupTask.RunSeerrCleanup() at line 332 the log message interpolates `config.SeerrCleanupAgeDays` but NOT the key â€” this specific path is fine. The real risk is the TestConnectionAsync catch clause (line 87) which returns `ex.Message` directly to the API caller in the `Message` field of the response tuple. For a malformed API key the ArgumentException message will include the parameter name and could include a fragment of the key depending on the .NET runtime.

**Impact:** API key fragments could appear in HTTP API responses viewed by non-admin users or captured in server logs.

**Suggested Fix:** In TestConnectionAsync, catch ArgumentException for API-key validation before the try block (as already done for the CRLF check) and return a generic error message rather than ex.Message.

#### 8. ValidatePath allows browsing non-existent paths, only errors on existing files/dirs
**File:** Jellyfin.Plugin.JellyfinHelper/Services/FolderBrowser/FolderBrowserService.cs | 263-371

**Description:** `ValidatePath` at line 306-336 checks if the path is a directory when it exists, and returns 'Directory does not exist' when it doesn't. However, the method is also used as a gate before `GetChildren()`. When a path does not exist, `ValidatePath` returns `null` (valid) â€” not an error â€” because the path passed the earlier format checks. A caller passing `/etc/shadow` (which exists as a file on Linux) gets 'Path must point to a directory', but a non-existent path like `/nonexistent/../../etc/shadow` after normalization could still resolve to a sensitive location. More critically, the `..` segment check at line 271 splits on `Path.DirectorySeparatorChar` and `Path.AltDirectorySeparatorChar`, but on Linux `Path.AltDirectorySeparatorChar` equals `Path.DirectorySeparatorChar` (both are `/`), so a Windows-style path `C:\..\etc` with a literal backslash would not be caught on Linux as `ValidatePath` would split on `/` only, keeping `C:\..\etc` as a single segment.

**Impact:** On Linux, a path containing backslash-encoded traversal (`C:\..\..\etc`) is not caught by the `..` check and passes validation. The subsequent `Path.GetFullPath` normalisation catches this, but the defence-in-depth check silently fails.

**Suggested Fix:** In `ValidatePath`, always split on both `/` and `\` hardcoded (not `Path.DirectorySeparatorChar`/`Path.AltDirectorySeparatorChar`) as `BackupValidator.ValidatePathSafety` already does correctly.

#### 9. rootFolder value passed to Seerr without path sanitisation â€” potential path traversal
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 365-368

**Description:** The rootFolder parameter is accepted from the caller, validated only for null/whitespace, and inserted verbatim into the JSON payload sent to Seerr (line 366-368). Nothing prevents a caller from passing ../../etc/passwd or a UNC path. Although Seerr ultimately controls where files land, the plugin blindly forwards attacker-controlled path segments to an internal service.

**Impact:** If Seerr has a bug or misconfiguration that honours arbitrary rootFolder values, this becomes a server-side path traversal. At minimum it allows probing internal Seerr directory structure via error responses.

#### 10. GetServiceInfoAsync serviceType path parameter is not sanitised before URL construction â€” potential SSRF path injection
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 587-589

**Description:** GetServiceInfoAsync and GetServiceInfoWithStatusAsync accept a serviceType string which is validated to be 'radarr' or 'sonarr' at the entry of the public methods. However, the private GetServiceInfoWithStatusAsync (line 686) performs the same check, so this is correctly guarded. But inside GetServiceInfoAsync the server.Id is interpolated directly into the URL at line 619: $"api/v1/service/{serviceType}/{server.Id}". server.Id is an int so cannot be exploited; serviceType is validated. This is correctly handled. HOWEVER: the public GetServiceInfoAsync on line 554 accepts an arbitrary caller-supplied serviceType â€” if for any reason the if-guard at line 554 is removed or bypassed, it directly reaches URL construction.

**Impact:** Low risk in current code due to guards, but the pattern is fragile. If a future refactoring removes the guard, SSRF path injection through serviceType becomes possible.

### HIGH / CORRECTNESS

#### 1. ToLookup + two ToList() calls materialise all streams twice on every played item
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 386-388

**Description:** BuildLanguageProfiles at lines 386-388 calls `allStreams.ToLookup(s => s.Type)` and then `.ToList()` on both the audio and subtitle buckets. Each `.ToList()` on an `IGrouping<>` enumerates the lookup's internal linked list. This means every audio stream is iterated twice (once for the ToLookup, once for the ToList) and every subtitle stream is iterated twice â€” for every played item in the library. For a library with thousands of items and multi-track files the extra allocations and iterations are non-trivial and all three collections (the Lookup, audioStreams List, subtitleStreams List) live simultaneously on the heap. A single `foreach` with a `switch` on `s.Type` would eliminate all three intermediate allocations.

**Impact:** Unnecessary heap pressure and CPU work on every profile build for every played item, proportional to library size.

**Suggested Fix:** Replace the ToLookup+ToList pattern with a single pass: iterate allStreams once, partitioning directly into two pre-allocated lists.

#### 2. availableAudioLanguages count computed before the null-language guard â€” always counts at least 1
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 408-432

**Description:** At line 408-413, `availableAudioLanguages` is computed from `.Where(l => !string.IsNullOrEmpty(l))` on the full audioStreams list. However, this block is only reached after `usedAudioLanguage` has already been resolved to a non-empty string (the outer guard at line 407). There is then a redundant `if (availableAudioLanguages > 0)` check at line 415 â€” since `usedAudioLanguage` was already proven non-empty by going through NormalizeLanguage, the stream it came from is already counted, so `availableAudioLanguages` will always be >= 1 at this point. The guard `if (availableAudioLanguages > 0)` can never be false and is dead code that creates an impression of safety that is not actually needed. More importantly the Distinct(OrdinalIgnoreCase) at line 412 is a hidden allocation on every item just to compute a count â€” a simple boolean `audioStreams.Any(s => NormalizeLanguage(s.Language) != null && ...)` would answer the chosen-vs-forced question more cheaply.

**Impact:** The dead branch erodes code clarity. The repeated NormalizeLanguage calls (once during the `usedAudioLanguage` resolution above, again inside the Count query) mean NormalizeLanguage is called 2Ã—|audioStreams| + 1 times per item instead of |audioStreams| + 1 times. For items with many audio tracks this is wasted work.

**Suggested Fix:** After resolving `usedAudioLanguage`, check if any OTHER audio stream also has a non-empty normalized language. Remove the dead `if (availableAudioLanguages > 0)` guard.

#### 3. HeavyRewatcher test asserts eRatio > mRatio but 'Anchor' item has PlayCount=0 in both profiles â€” anchor weight is identical, so the test only verifies non-linearity of Action weight, not the ratio
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/PreferenceBuilderTests.cs | 62-66

**Description:** BuildGenrePreferenceVector_HeavyRewatcher_DoesNotDominateLinearly: both profiles add an Anchor item with PlayCount=0 at the same timestamp. After max-normalisation the Anchor weight is not independently normalised â€” its value in the vector equals its raw weight divided by the max of the whole vector. Because extreme Action dominates (higher weight), Anchor's normalised value differs between the two profiles. The ratio test mRatio vs eRatio is valid mathematically, but the comment says 'capping' is what keeps eRatio/mRatio < 6.0 â€” in reality, what the test actually verifies is that log1p growth from PlayCount 5â†’30 is sub-linear, not the cap (which only fires above 100). The upper bound of 6.0 is arbitrary and not tied to any constant in the implementation, so a change to PlayCountLog1pCeiling from 2.0 to 3.0 would still pass this test undetected.

**Impact:** The cap guard (PlayCountMaxForLog1p=100) is not actually tested. A regression that removed the cap entirely would still pass because Play 30 naturally grows sub-linearly under log1p.

**Suggested Fix:** The _PlayCountBeyond100_IsCapped test at line 70 does test the actual cap correctly. The HeavyRewatcher test should be renamed and commented to clarify it only guards log1p sub-linearity, not the cap. The arbitrary 6.0 bound should reference a formula-derived constant.

#### 4. Phase 1 abandoned-check fires on neutralised CompletionRatio=0.5 for unwatched Phase 1 items
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 484

**Description:** For Phase 1 items where `wasWatched` is false and `completionRatio` was initialised to 0.5 (the neutral default for series or items with no interaction), the else-if branch at line 484 (`features.CompletionRatio is > 0 and < AbandonedCompletionThreshold`) is evaluated. `0.5` is `> 0` and `< 0.25` is false so it falls through correctly â€” BUT the 0.5 default was set unconditionally for the `default:` switch arm when `hasUserInteraction` is false (line 350: `completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItemForRec) : 0.5`). `ContentScoring.ComputeCompletionRatio(null)` already returns `0.0`, so the ternary is redundant but also masks the following scenario: `watchedItemForRec` is non-null (e.g. an episode matched from `watchedItemLookup`) but `wasWatched` is false â€” `hasUserInteraction` becomes true, `completionRatio` becomes the real ratio from the item, and the item gets `ExposureLabel` only if `completionRatio >= 0.25`, but if the item has completion < 0.25 it is labelled `AbandonedLabel (0.0)` even though it was never labelled as watched. This is logically correct â€” but the `wasWatched=false && CompletionRatio > 0` path being `AbandonedLabel` is a silent conflict: an item the user never watched according to `profileLookup` but has partial playback gets `0.0`, which is a strong negative training signal based solely on a partial-watch detection derived from the watch history that was built with `HasMeaningfulInteraction()`. The gate and the label are therefore inconsistent.

**Impact:** Items partially watched by the user (PlaybackPositionTicks > 0) that are in the watch-history but not yet `HasMeaningfulInteraction()` will be incorrectly labelled `AbandonedLabel=0.0` in Phase 1, producing false strong-negative training signal.

**Suggested Fix:** The `else if (features.CompletionRatio is > 0 and < EngineConstants.AbandonedCompletionThreshold)` at line 484 is unreachable when `wasWatched=false` and `completionRatio` is always 0.5 in that case â€” verify this branch cannot be reached with a real partial-watch ratio, or restrict the check to `!wasWatched && watchedItemForRec is not null`.

#### 5. Phase 1 watched-genre/people/studio sets built with `w.Played || w.IsFavorite` but ContentNearestNeighbor at inference uses all candidates with play activity
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 244

**Description:** The `watchedGenreSets`, `watchedPeopleSets`, and `watchedStudioSets` lists at lines 244-267 are built with `userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite)`. The same lists in Phase 3 (lines 838-857) use the identical predicate. However, the live `Engine.GenerateForUser()` path fills the equivalent sets from candidates that the user has interacted with â€” which includes `PlayCount > 0` and `PlaybackPositionTicks > 0` items per `HasMeaningfulInteraction()`. This is a train/serve parity gap: items with `PlayCount > 0` but `Played=false` contribute to the live ContentNearestNeighbor but not to the training one.

**Impact:** Users with many re-watched (PlayCount > 0, Played=false) or partially-watched (PlaybackPositionTicks > 0) items get a systematically smaller watched-item set at training time than at inference time, causing ContentNearestNeighborScore to be under-estimated in training versus inference. This creates a train/serve skew for one of the composite feature dimensions.

**Suggested Fix:** Change the filter predicate in both Phase 1 (line 244) and Phase 3 (line 838) from `w.Played || w.IsFavorite` to `w.HasMeaningfulInteraction()` to match the live path's coverage.

#### 6. Phase 1 `BuildWatchedIdSet` uses `watchedIds` (filtered by `HasMeaningfulInteraction`) for BoxSet counting but Phase 2 organic BoxSet lookup is entirely absent
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 276

**Description:** Phase 1 and Phase 3 both call `BuildWatchedIdSet(watchedIds, watchedSeriesIds)` (lines 276, 867) to build the per-user BoxSet count dictionary. Phase 2 (organic items) does NOT build a `watchedBoxSetCounts` at all; the `CollectionProgressionBoost` feature is left at 0.0 for all organic items regardless of how many BoxSet siblings the user has watched. This is a train/serve parity gap: the live `Engine.ScoreCandidate()` path computes a real collection progression boost for all candidates, including items the user might organically discover.

**Impact:** Phase 2 organic training examples always receive `CollectionProgressionBoost = 0.0`, misrepresenting the real feature value the model would compute at inference for these same items. The model trains on systematically wrong values for this dimension in Phase 2.

**Suggested Fix:** Build a `watchedBoxSetCountsOrganic` dictionary for the organic Phase 2 loop analogous to Phase 1 (lines 275-288). Pass it to `ComputeCollectionProgressionBoostWithCounts` for organic standalone items; the `AddAggregatedSeriesExample` method should also receive and use it.

#### 7. IReadOnlyList overload of `ComputeTrainingTemporalAffinity` returns neutral 0.5 when `candidateGenres` is empty, but the HashSet overload returns 0.5 when `candidateGenreSet.Count == 0` â€” these are equivalent, but the IReadOnlyList overload allocates a new HashSet even when it will immediately return 0.5
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingFeatureComputer.cs | 116

**Description:** At line 127, the `IReadOnlyList` overload always allocates `new HashSet<string>(candidateGenres, ...)` before delegating to the HashSet overload â€” but then the HashSet overload checks `candidateGenreSet.Count == 0` at line 141. The early-return guard on line 122 (`candidateGenres is null || candidateGenres.Count == 0 â†’ return 0.5`) prevents the empty-list allocation path. The allocation is therefore unavoidable for non-empty lists. However, the `IReadOnlyList` overload is only called from one place (TrainingDataBuilder.BuildExamples, line 412â€“418) where the caller already has a pre-built `recGenreSet` and passes it to both temporal calls. The IReadOnlyList overload is therefore dead at runtime in the hot path â€” the HashSet overload is what gets called. This is a minor issue but the IReadOnlyList overload allocates a redundant set if somehow called.

**Impact:** Minor: extra allocation in the unlikely path where the IReadOnlyList overload is called with a non-empty list. No correctness issue.

**Suggested Fix:** No action required; the IReadOnlyList overload is kept for API completeness per the comment. Low-severity informational note only.

#### 8. HeuristicScoringStrategy registered with hardcoded genrePenaltyFloor=1.0 ignoring config
**File:** Jellyfin.Plugin.JellyfinHelper/PluginServiceRegistrator.cs | 93

**Description:** On line 93, HeuristicScoringStrategy is constructed with `genrePenaltyFloor: 1.0` unconditionally. The EnsembleScoringStrategy is then constructed with the config value for genrePenaltyFloor (line 106), but the standalone HeuristicScoringStrategy singleton â€” which is also injected into the ensemble â€” uses a different, hardcoded value. If the ensemble passes the heuristic strategy's own penalty floor through at scoring time, the config setting has no effect on heuristic scoring.

**Impact:** Admin-configured EnsembleGenrePenaltyFloor is silently ignored for the heuristic sub-strategy, making genre diversity tuning ineffective.

**Suggested Fix:** Pass `config?.EnsembleGenrePenaltyFloor ?? HeuristicScoringStrategy.DefaultGenrePenaltyFloor` (or equivalent) when constructing HeuristicScoringStrategy, consistent with how the ensemble is built.

#### 9. ML strategy configuration frozen at DI build time â€” config changes require restart
**File:** Jellyfin.Plugin.JellyfinHelper/PluginServiceRegistrator.cs | 69

**Description:** All three scoring strategies (LearnedScoringStrategy, NeuralScoringStrategy, EnsembleScoringStrategy) read Plugin.Instance?.Configuration and Plugin.Instance?.DataFolderPath at the moment the DI container is first built (singleton factory lambdas). Any subsequent change to EnsembleAlphaMin, EnsembleAlphaMax, or EnsembleGenrePenaltyFloor in the plugin settings will not take effect until Jellyfin is fully restarted. There is no mechanism to reload these values.

**Impact:** Operators who tune alpha values via the UI will see no change in recommendation behavior until a full server restart, with no indication that this is required.

**Suggested Fix:** Read configuration values at call time (e.g. via a factory or options pattern), or document explicitly that a server restart is needed after changing ML parameters.

#### 10. Two separate lock objects (_rwLock and _syncRoot) protect overlapping state â€” TOCTOU hazard
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 162-163

**Description:** NeuralScoringStrategy uses a ReaderWriterLockSlim (_rwLock) to guard weight arrays during Train()/Score() and a separate Lock (_syncRoot) exclusively for the four LastXxx metric scalars. After Train() exits the write lock (line 1187), it publishes _lastValidationLoss under _syncRoot (line 1164). However, _featureMeans and _featureStdDevs are assigned OUTSIDE _syncRoot (lines 1182-1183, still under _rwLock). Score() reads _featureMeans/_featureStdDevs under _rwLock but reads _lastValidationLoss under _syncRoot. These two locks are never held simultaneously, so a reader of LastValidationLoss cannot know whether the corresponding _featureMeans snapshot is consistent. More critically, lines 1182-1183 (`_featureMeans = featureMeans; _featureStdDevs = featureStdDevs;`) are inside the try block but AFTER ExitWriteLock in the finally on line 1189 â€” meaning these assignments happen WITHOUT holding any lock and are therefore visible races with concurrent Score() calls that are acquiring the read lock.

**Impact:** A concurrent Score() call between the write-lock release and the _featureMeans assignment can observe a partially-updated model: new weights but old (or null) standardization stats, producing systematically wrong scores silently.

**Suggested Fix:** Move _featureMeans/_featureStdDevs assignment to before the finally block so they are written while the write lock is still held, or move them inside the lock (_syncRoot) block on lines 1164-1180.

#### 11. Score() allocates a new double[] on every call despite claiming zero-allocation scoring path
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 388

**Description:** Line 388 does `var vector = new double[CandidateFeatures.FeatureCount];` on every Score() invocation. The class comment says the scoring path is zero-allocation and uses thread-local scratch buffers, but those TLS buffers are only for hidden-layer activations â€” the input vector itself is heap-allocated every call. For a recommendation run over 1000+ candidates this generates 1000+ small array allocations. LearnedScoringStrategy correctly uses ArrayPool<double>.Shared.Rent() for the same purpose.

**Impact:** High GC pressure during recommendation runs; contradicts documented design goal. No correctness impact, but significant performance regression at scale.

**Suggested Fix:** Add a [ThreadStatic] private static double[]? _tlsInput scratch buffer initialized to FeatureCount size, analogous to the hidden-layer TLS buffers already present, and reuse it in Score() and ScoreVector().

#### 12. Gradient loss formula uses sigmoid derivative of training-forward output but MSE loss requires plain error â€” the derivative is already absorbed by the chain rule elsewhere
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 878

**Description:** Line 878: `var outErr = (pred - examples[idx].Label) * pred * (1.0 - pred) * sw;`. For an MSE loss L = (pred - y)^2 / 2 with sigmoid output, dL/dz_output = (pred - y) * sigmoid'(z) = (pred - y) * pred * (1 - pred). This is correct. HOWEVER, the weight update for hidden4â†’output on line 962 also uses `outErr * h4Act[k]`, which is correct. But the weight update formula for the bias on line 972 uses `outErr` directly. And critically, lines 899 and 918 propagate `outErr * _weightsH4O[k] * dropoutInvKeep` as h4Err, which should be `outErr` only (the weight carries the connection, and the sigmoid derivative is already baked into outErr). On closer inspection the backprop is: h4Err[k] = dL/da_{h4,k} = dL/dz_out * w_{h4o,k} = outErr * w_{h4o,k}. This is correct. The concern is that outErr already contains sw (sample weight) at line 878, but h4Err on line 899 then multiplies by dropoutInvKeep which represents 1/keep â€” this is doubling the inverted-dropout scale for the hidden-layer error path when dropout is active: outErr was computed WITHOUT dropout scaling (pred comes from ForwardPassTraining which scaled activations by invKeep), but then the gradient flowing back through the outputâ†’h4 edge is scaled again by dropoutInvKeep.

**Impact:** With dropout active (training sets >= 30 examples), hidden-layer gradients are scaled by (1/keep)^2 instead of (1/keep), biasing weight updates in all hidden layers. This causes systematically over-large gradients in the hidden layers compared to the output layer, leading to instability and suboptimal convergence.

**Suggested Fix:** Remove the extra `* dropoutInvKeep` multiplier from line 899 (h4Err computation). The inverted-dropout scaling is already embedded in h4Act[k] (used in the output-layer weight update). The backpropagated error through the output layer should simply be `outErr * _weightsH4O[k]`, not `outErr * _weightsH4O[k] * dropoutInvKeep`.

#### 13. Input-gradient attribution in ScoreWithExplanation uses standardized vector values â€” produces misleading feature attributions
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 608-629

**Description:** ScoreWithExplanation() calls StandardizeSingleVector on `vector` (line 548) before calling ForwardPass. The ForwardPass stores pre-activation values in h1Pre..h4Pre. The attribution loop at line 623 then multiplies `_weightsIH[baseIdx + i] * vector[i]` â€” but `vector[i]` here is the Z-score-standardized value, not the original feature value. This means the attribution scores are in standardized feature space: a feature with mean 0.5 and stddev 0.1 will show inflated attributions compared to one with mean 0.5 and stddev 0.001 (which would be left near its original value by standardization). The final explanation numbers are therefore not comparable across features and give misleading signals to the UI.

**Impact:** Users seeing score explanations will get distorted per-feature contribution values. The DominantSignal determination will be incorrect for standardized models, potentially showing wrong 'primary reason' messages in the UI.

**Suggested Fix:** Either compute attributions using the original (pre-standardization) feature values, or document clearly that attributions are in standardized space and normalize them back before populating ScoreExplanation.

#### 14. Sonarr key lookup built from already-cleared list
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupService.cs | 391-394

**Description:** At line 391, `previousSonarrKeys` is built from `config.SonarrInstances`. However, `config.SonarrInstances.Clear()` is called at line 395 immediately after. The variable is captured before the clear, so the lookup is correct here. BUT at line 346, `previousRadarrKeys` is similarly built from `config.RadarrInstances` BEFORE its clear at line 350. This is fine. However, `GetConfiguration()` (line 269) returns a new `PluginConfiguration()` when the plugin is not initialized, and `config` here is that same object reference. When `config.RadarrInstances.Clear()` is called on line 350, `previousRadarrKeys` was already populated correctly. But `GetConfiguration()` returns a fresh object (not the live stored config), so the `.Clear()` and `.Add()` calls on lines 350-379 and 395-419 modify only a local transient object â€” `SaveConfiguration()` is still called at line 431 which calls `Plugin.Instance?.SaveConfiguration()`, persisting the Plugin.Instance's own config, NOT the local `config` object that was mutated. This is a critical bug: changes are lost because the modified object is never assigned back. The only way this works is if `_accessor.Configuration` returns the actual live reference, not a copy.

**Impact:** If `PluginConfiguration` is returned by reference (as Jellyfin plugin configuration objects typically are), restoring Arr instances works. If it is ever returned by value or copied, all Arr instance mutations are silently discarded. This is an implicit contract that is not enforced or documented, making future refactors fragile.

**Suggested Fix:** Document explicitly that `GetConfiguration()` must return the live reference, not a copy. Add a comment to `RestoreConfiguration` noting this reliance. Alternatively, have `SaveConfiguration(PluginConfiguration config)` accept the mutated config to make the contract explicit.

#### 15. Radarr credential-change detection compares truncated new key against truncated stored key but stored key may be full-length
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupService.cs | 346-350

**Description:** At line 367, `priorKeys` contains values that were already truncated via `BackupSanitizer.TruncateString(i.ApiKey, MaxApiKeyLength)` (line 349). At line 358, the incoming `apiKey` is also truncated. The comparison at line 367 (`!priorKeys.Any(k => k == apiKey)`) therefore compares truncated-vs-truncated. This is correct for detecting changes. However, if the live stored key is shorter than `MaxApiKeyLength` and the backup key equals it in full, the comparison is fine. The edge case is: if a key was previously stored at exactly 200 chars and the incoming backup also has it at 200 chars with a different actual value, the detection works. This is actually correct. No real bug, but the comment on line 362-363 says 'Both sides are truncated to the same length so a key that was stored full-length but backed up at MaxApiKeyLength is not a false positive' â€” this is only true when both sides are >= MaxApiKeyLength. If the stored key is 201 chars and the backup key is 200 chars (a proper truncation), they compare equal (both truncated to 200), but the key DID change. False negative in credential-change detection.

**Impact:** Credential change audit warning is silently suppressed when the stored key is longer than MaxApiKeyLength and the backup contains the MaxApiKeyLength-length truncated form of a different key. The CredentialsChanged flag is not set even though the stored credential will change.

**Suggested Fix:** Compare the raw (non-truncated) stored key against the raw incoming key before truncation, or at minimum document this edge case as an accepted limitation.

#### 16. MoveFileToTrash creates trash directory AFTER ResolveCollision, causing false collision detection
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 158-163

**Description:** In `MoveFileToTrash` (line 123), the order of operations is: (1) compute `trashItemPath` (line 157), (2) call `ResolveCollision(trashItemPath)` (line 160), (3) call `Directory.CreateDirectory(trashBasePath)` (line 163). `ResolveCollision` checks `File.Exists` and `Directory.Exists` on the candidate paths. If `trashBasePath` does not exist yet, `Directory.Exists(trashBasePath)` returns false, and the collision check against items inside it would always see no existing items. However, if `trashBasePath` already exists but `trashItemPath` doesn't, `ResolveCollision` correctly returns the desired path. The real bug is subtler: in `MoveToTrash` (line 66), the directory IS created BEFORE `ResolveCollision` is called (line 101 then 104). This is correct. But in `MoveFileToTrash`, `ResolveCollision` is called at line 160 BEFORE `Directory.CreateDirectory` at line 163. This is inconsistent but not directly harmful since `ResolveCollision` only checks the candidate file path, not the parent. However, if `trashBasePath` doesn't exist, `File.Move` at line 169 will throw because the destination directory doesn't exist yet. The directory creation should happen before the move and ideally before `ResolveCollision` for consistency with `MoveToTrash`.

**Impact:** In the rare case `trashBasePath` does not exist, `File.Move` will throw `DirectoryNotFoundException` rather than succeed. The exception is caught and logged, but the file is not moved to trash â€” silently failing the operation.

**Suggested Fix:** Move `Directory.CreateDirectory(trashBasePath)` to before `ResolveCollision` in `MoveFileToTrash`, mirroring the order in `MoveToTrash`.

#### 17. ComputeStableSeed uses Guid.GetHashCode() which is NOT process-stable on .NET
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 1940-1949

**Description:** The comment on line 1942 correctly notes that Guid.GetHashCode() is 'deterministic within a process but .NET does not guarantee stability across processes'. However, the method is called at lines 143 and 410 precisely to produce seeds that survive Jellyfin restarts (the code says 'process-independent'). On .NET 6+ the default hash randomisation applies to Guid.GetHashCode() via SipHash, so the same Guid produces a different int after every process restart. The comment is internally contradictory â€” it acknowledges the problem and then uses the problematic method.

**Impact:** After every Jellyfin restart, exploration seeds change for all users even on the same UTC day or same batch generation, defeating the documented 'stable within one day' and 'stable per (user, batch)' contracts. Users experience different exploration picks after a restart, which undermines reproducibility guarantees.

**Suggested Fix:** Extract the 16 bytes of the Guid via id.ToByteArray() or MemoryMarshal and fold them with a fixed hash (e.g. FNV-1a or a direct XOR of the four 32-bit words), then combine with suffix. This is process-stable by construction and requires no external dependency.

#### 18. ExceedsMaxRating blocks ALL unrated items for restricted profiles, including items without OfficialRating metadata
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 2018-2026

**Description:** When maxRating.HasValue is true, the method returns true whenever InheritedParentalRatingValue is null. Items that simply lack rating metadata (no OfficialRating field set) have a null InheritedParentalRatingValue and are silently excluded from recommendations for any user with a parental-rating restriction. This is overly aggressive: 'unrated' does not mean 'adult content', and many legitimate items (home videos, obscure titles) simply have no rating data.

**Impact:** Users with any parental-rating restriction (including mild ones like PG-13) receive zero recommendations for every unrated item in the library. Libraries with sparse metadata will produce severely truncated recommendation lists.

**Suggested Fix:** Consider treating null InheritedParentalRatingValue as 'unrated but allowed' (return false) when maxRating is below a configurable adult-content threshold, or expose a separate plugin configuration flag 'BlockUnratedItems' so the behaviour is opt-in rather than always-on.

#### 19. coOccurrence.Values.Max() called via LINQ enumeration on Dictionary<Guid,double> â€” O(N) allocation per user
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 872

**Description:** Line 872: var collaborativeMax = coOccurrence.Count > 0 ? coOccurrence.Values.Max() : 0; The .Values property on a Dictionary<,> returns a ValueCollection, but .Max() called on it creates a LINQ IEnumerable chain. While not incorrect, this is an O(N) scan that allocates an enumerator per user in the batch, and it silently returns 0 (neutral) when coOccurrence is empty â€” but that case is already guarded. More critically: this uses LINQ Max() on doubles, which does not handle NaN. If any collaborative score is NaN (which can happen if jaccardWeight and combinedModifier produce 0/0 edge cases), Max() propagates NaN, causing all subsequent ComputeCollaborativeScore calls for this user to return NaN rather than a valid score.

**Impact:** A single NaN in the coOccurrence map poisons all collaborative scores for a user, collapsing their recommendations to purely content-based signals without any visible error. This is a silent correctness failure.

**Suggested Fix:** Compute the max in a plain foreach loop that skips NaN/Infinity values, or ensure CollaborativeFilter never writes non-finite values (verify Math.Sqrt and division paths cannot produce NaN under degenerate inputs).

#### 20. ReinsertAtOriginalIndices ascending-order rollback is logically incorrect after multiple removals
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryCacheService.cs | 488

**Description:** Items are removed in descending index order (line 220-223), so after removal the list is shorter and all captured original indices above the first removed item are now stale (shifted). When rolling back via ReinsertAtOriginalIndices in ascending order, inserting item at originalIndex 3 is correct, but then inserting the next item at originalIndex 7 hits a position that is now one slot ahead of where it belongs because the prior insert already shifted elements. For a common case of removing item at index 3 and item at index 4 (same TmdbId appearing twice), the ascending rollback produces the wrong order.

**Impact:** After a rollback (IO failure or cancellation) involving more than one removed item at non-adjacent indices, the in-memory recommendation order is corrupted â€” user sees recommendations in a different order than what is on disk, which diverges until the next full Save().

### HIGH / PERFORMANCE

#### 1. N+1 database query per series candidate in ResolveMediaLanguages via Series.Children
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 1267-1276

**Description:** For series items with no direct media streams, ResolveMediaLanguages traverses series.Children (Seasons) and then season.Children (Episodes). In Jellyfin, Children is a lazy property that issues a database query per call. With 500 series candidates in the library, ScoreCandidate is called once per candidate per user, and each call to ResolveMediaLanguages triggers two nested .Children accesses â€” potentially thousands of DB round-trips per user per recommendation run.

**Impact:** On libraries with hundreds of series, GetAllRecommendations can be dramatically slower due to per-series DB queries inside the parallel scoring loop. The parallel loop amplifies this: all worker threads hit the database simultaneously.

**Suggested Fix:** Pre-resolve series language metadata (e.g. by querying the first episode per series once during LoadCandidateItems) and store the result in the CandidateSnapshot, similar to how peopleLookup and boxSetLookup are pre-built. Alternatively, use GetItemList with a MediaType=Episode filter restricted to first-season, first-episode items.

### HIGH / TEST-GAP

#### 1. Assert.All on potentially empty collection vacuously passes â€” no guarantee warm path actually scored anything
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/EngineFullPipelineTests.cs | 151

**Description:** GetRecommendations_ColdStartUser_WithMovies_ExecutesPipelineAndProducesValidResult uses Assert.All on result.Recommendations without first asserting it is non-empty. The test comment acknowledges this (previously called ReturnsPopulatedRecommendations) but accepts the vacuous pass as intentional. The problem is that the three invariants the test claims to lock in â€” non-empty ItemId, reasonPopular key, non-empty Name â€” are NEVER verified if LoadCandidateItems drops all candidates. The test can be fully green while zero lines of ScoreCandidate, DiversityReranker, or RecommendedItem projection are exercised. The stated coverage goal (800+ previously-uncovered lines) is not achieved.

**Impact:** The cold-start scoring pipeline (rating filter, combined-critic + recency, diversity reranking, RecommendedItem projection) may be completely untested if all Movie instances fail the LoadCandidateItems filter. This test provides false assurance of coverage.

**Suggested Fix:** Add Assert.NotEmpty(result.Recommendations) before Assert.All, OR construct Movie instances that reliably pass LoadCandidateItems by ensuring Path is a valid file-system path that MockFileSystem recognises. If the filter cannot be bypassed in a unit test host, document the gap and add a separate integration test.

#### 2. Warm-path test only asserts Cohort and non-empty ScoringStrategy string, never verifies any scored recommendation was produced
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/EngineFullPipelineTests.cs | 183-192

**Description:** GetRecommendations_WarmUser_WithCandidates_ReturnsScoredRecommendations asserts UserId, UserName, Cohort, and that ScoringStrategy is not empty. It makes no assertion about result.Recommendations. The comment claims to drive 'GenerateForUser â†’ preference vectors â†’ ScoreCandidate â†’ DiversityReranker â†’ RecommendedItem projection', but if the watched item ID in MakeWarmProfile matches none of the candidate Movie Ids (which it will not, because they are independently random), and all candidates are dropped by the same path filter as the cold-start test, the warm scoring loop may have executed with zero candidates, leaving all the advertised code paths uncovered.

**Impact:** The warm scoring path (~800 lines) may remain at near-zero coverage even after this test passes. Incorrect confidence in coverage metrics.

**Suggested Fix:** Assert result.Recommendations is non-empty, or at minimum assert that result.ScoringStrategyKey equals the expected key for the warm path (not cold-start), providing at least a signal that the warm branch was taken.

#### 3. ProximityExpansion test relies on baseline profile with single-genre rows, but minCooccurrences=2 gate means the baseline could also accidentally trigger expansion
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/PreferenceBuilderTests.cs | 404-531

**Description:** BuildGenrePreferenceVector_ProximityExpansion_StaysNormalized constructs a baseline profile with single-genre-per-row items (no co-occurrences). It then asserts sciFiWeight > baselineSciFi + 0.005. However the baseline profile has 20 Action rows, 20 Adventure rows, and 16 SciFi rows â€” all single-genre. Since ExpandGenreProximity needs at least 2 distinct genres per watched item (distinctGenres.Length < 2 short-circuit at line 324 of PreferenceBuilder.cs) AND minCooccurrences=2, the baseline vector is correctly immune. BUT: the test assertion range for baselineSciFi is Assert.InRange(baselineSciFi, 0.79, 0.81). If temporal decay causes even tiny timestamp differences across 56 baseline rows (all use baseDate.AddHours(-i) with i in different ranges), the Action and Adventure rows may not all produce exactly the same weight, potentially causing baseline Action or Adventure to not reach exactly 1.0, and baselineSciFi could fall outside [0.79, 0.81] under fast clock or test parallelism.

**Impact:** Test could flake under high system load or fast-clock environments where DateTime.UtcNow.AddDays(-10) baseline diverges from expectations. The 0.79-0.81 window is narrow.

**Suggested Fix:** Use a fixed reference DateTime rather than DateTime.UtcNow.AddDays(-10) for the baseDate so the temporal decay is deterministic. E.g. new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).

---

## MEDIUM (105 findings)

### MEDIUM / BUG

#### 1. MemoryStream disposed before StreamReader finishes reading â€” potential ObjectDisposedException at runtime
**File:** Jellyfin.Plugin.JellyfinHelper/Api/BackupController.cs | 165

**Description:** At line 165-170, a StreamReader is created with leaveOpen: false, meaning it will dispose the MemoryStream (buffer) when it is disposed (via the using var at line 165). The finally block at line 172-175 then calls buffer.DisposeAsync() on an already-disposed MemoryStream. The using var reader's implicit Dispose() will fire when the block exits, which disposes buffer. Then the finally block tries to dispose it again. While double-disposal of MemoryStream is a no-op in .NET's current implementation, the leaveOpen: false also means reading completes correctly â€” however the StreamReader disposes buffer before the finally, making the finally call redundant but harmless. The real bug: because StreamReader is created as 'using var' inside the try block with buffer also inside the try block and the finally on the outer try, and the StreamReader's Dispose calls buffer.Dispose, the buffer.DisposeAsync() in the finally fires on an already-disposed stream. This is structurally fragile.

**Impact:** Currently harmless due to MemoryStream's idempotent Dispose, but represents a structural defect that could surface as ObjectDisposedException if the buffer implementation changes or if the code is adapted to a different stream type.

**Suggested Fix:** Create the StreamReader with leaveOpen: true, and let the finally block be the single disposal point for the MemoryStream. Or restructure so the StreamReader is also in the finally block's scope.

#### 2. seenPeople.Contains() + seenPeople.Add() is redundant â€” the Contains check is dead
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 630-631

**Description:** In AggregatePeopleFromItem (lines 630-633), the code checks `if (seenPeople.Contains(person.Name)) continue;` then later does `seenPeople.Add(person.Name)`. HashSet.Add returns false when the element already exists, so the check is equivalent to `if (!seenPeople.Add(person.Name)) continue`. The current pattern is not a bug per se, but the Contains call is entirely redundant â€” it is O(1) but doubles the hash lookups for every person. More importantly, the `seenPeople.Add` inside the if-Director and if-Actor branches (lines 644 and 656) can still be reached even if the Contains check was supposed to prevent it, since the Contains check at line 630 only `continue`s the loop on duplicates â€” the Add calls at 644/656 are redundant for a second person with the same name but different role (e.g. actor then director). However, the Contains check IS working correctly because the `continue` on line 631 skips the entire role evaluation. This is correct behaviour but implemented in an overly complicated way.

**Impact:** Minor: double hash lookups per person, and the logic is harder to follow than the idiomatic `if (!seenPeople.Add(person.Name)) continue;` pattern.

**Suggested Fix:** Replace `if (seenPeople.Contains(person.Name)) { continue; }` with `if (!seenPeople.Add(person.Name)) { continue; }` and remove the separate Add calls at lines 644 and 656.

#### 3. Static Instance field set in constructor â€” not thread-safe and leaks on double-init
**File:** Jellyfin.Plugin.JellyfinHelper/Plugin.cs | 37

**Description:** `Instance = this` is executed unconditionally in the constructor. If the plugin host constructs the plugin instance twice (e.g. during a reload or test scenario), the second assignment overwrites the first without any guard, potentially leaving services that captured the first reference pointing at a stale, partially-initialized instance. The field is not volatile or Interlocked, so there is no memory-visibility guarantee.

**Impact:** Services resolved from DI that captured Plugin.Instance at startup may use a stale instance after a plugin reload, leading to reading stale configuration or paths.

**Suggested Fix:** Use `Interlocked.CompareExchange(ref _instanceField, this, null)` or a lock to guard the first-write-wins invariant and log a warning if a second construction occurs.

#### 4. Task.Delay with CancellationToken.None ignores cancellation between DELETE calls
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 287

**Description:** The 100ms courtesy delay between DELETE requests uses `CancellationToken.None` explicitly. When a user cancels the scheduled task while in the middle of deleting a large batch of expired requests, the loop will still complete the delay between each request rather than stopping promptly. With a large expired set (e.g. 1000 requests) this could add up to ~100 seconds of unkillable delay after cancellation is requested.

**Impact:** Task cancellation is unresponsive for up to O(n * 100ms) where n is the number of expired requests. The task appears hung to the user.

**Suggested Fix:** Pass the `cancellationToken` to Task.Delay. Add a note that partial deletion is acceptable since the next run will catch remaining items, which is safer than the current approach of making cancellation non-responsive.

#### 5. SeerrCleanupResult.Failed initialized to 0 then set to 1 (not incremented) on config error
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 104

**Description:** On line 119, when CreateClient throws a configuration exception, the code sets `result.Failed = 1` with direct assignment. If this code path were ever reached after a partial failure (e.g. if the caller structure changed), it would overwrite rather than accumulate failures. More critically, this is inconsistent with every other failure path in the same method which uses `result.Failed++`.

**Impact:** Minor: in the current single-call structure it works correctly, but the inconsistency is a latent bug that will silently reset the failure counter if the code is ever refactored.

**Suggested Fix:** Change `result.Failed = 1` to `result.Failed++` for consistency with all other failure increments.

#### 6. Pagination can loop infinitely if API returns inconsistent pageInfo.Results
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 213

**Description:** The do-while loop terminates only when `skip >= page.PageInfo.Results`. If the Seerr API returns a Results count that never decreases (e.g. new requests arrive between pages, inflating the total), `skip` may never reach `page.PageInfo.Results`. There is no maximum-page guard. With PageSize=50 and a realistic library this would run for a very long time.

**Impact:** A misbehaving or adversarial Seerr server can cause the cleanup task to loop indefinitely, exhausting memory (expiredRequests grows unboundedly) and blocking the task thread.

**Suggested Fix:** Add a maximum page count guard: `var maxPages = (page.PageInfo.Results / PageSize) + 2; var pagesFetched = 0;` and break if `++pagesFetched > maxPages`.

#### 7. itemTotalPlays uses PlayCount (cumulative) but viewerCount is a unique-viewer counter â€” AverageCompletionPercent is skewed
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Activity/UserActivityInsightsService.cs | 148

**Description:** completionSum accumulates the completion percentage once per user regardless of how many times they watched the item (PlayCount). But itemTotalPlays accumulates `userData.PlayCount` (total plays). The average is then `completionSum / viewerCount`. If user A watched 5 times (PlayCount=5) and is near 100% completion, and user B watched once at 50%, completionSum = 200, viewerCount = 2, average = 100. But if user B is also at 100% from their single watch the result is the same â€” the issue is that completionSum is not weighted by PlayCount, which means AverageCompletionPercent is the average of last-known completion per unique viewer, not the average across all plays. This is an unspoken semantic mismatch with the TotalPlayCount field.

**Impact:** AverageCompletionPercent in UserActivitySummary does not account for re-watches in its averaging, making the metric misleading for frequently rewatched content.

**Suggested Fix:** Document explicitly that AverageCompletionPercent is per-unique-viewer (not per-play), or weight by PlayCount: `completionSum += completion * userData.PlayCount` and divide by `itemTotalPlays` instead of `viewerCount`.

#### 8. Path traversal risk in trash purge: GetFullPath can be manipulated via TrashFolderPath
**File:** Jellyfin.Plugin.JellyfinHelper/ScheduledTasks/HelperCleanupTask.cs | 201

**Description:** The trash path validation checks that `trashPath` starts with `libraryRoot + Path.DirectorySeparatorChar`. However, `candidatePath` comes from `_configHelper.GetTrashPath(location)` which incorporates `config.TrashFolderPath`. If an admin configures TrashFolderPath as an absolute path (e.g. `/tmp/evil` or `C:\Windows\Temp`), GetFullPath may resolve to a path that starts with the separator but NOT under the library root. The check catches this correctly for absolute paths that resolve outside. However if TrashFolderPath contains `../../` components that navigate to a sibling directory that happens to have a name starting with the library root string (e.g. library root `/media/movies`, trash resolves to `/media/movies-backup/...`), the StartsWith check passes incorrectly because `/media/movies-backup` starts with `/media/movies`.

**Impact:** Expired items under a path that looks like but is not under the library root could be permanently deleted. E.g. `/media/movies-extra/...` passes the StartsWith check against `/media/movies`.

**Suggested Fix:** The check already appends `Path.DirectorySeparatorChar` to libraryRoot before the StartsWith, so `/media/movies/` vs `/media/movies-backup/` is correctly distinguished. However this should be verified with a unit test for the exact edge case. The real risk remains if GetTrashPath returns an absolute path â€” add a guard that rejects absolute TrashFolderPath values in CleanupConfigHelper.

#### 9. CreateClient mutates a shared named HttpClient from IHttpClientFactory
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 381

**Description:** IHttpClientFactory.CreateClient("SeerrIntegration") returns an HttpClient that may be reused across calls (the factory pools handler instances, but the client itself may be returned with a pre-existing BaseAddress or headers if the handler is pooled). Setting client.BaseAddress and client.DefaultRequestHeaders on a potentially shared instance is not thread-safe. If two concurrent requests invoke CreateClient simultaneously (e.g. a test-connection API call overlapping with the scheduled cleanup task), both mutate the same client, and one request may send its API key to the other's base URL.

**Impact:** Race condition: concurrent Seerr operations could result in requests sent to the wrong base URL or with the wrong API key header.

**Suggested Fix:** Use a per-call HttpClient by wrapping request construction manually: do not set BaseAddress or DefaultRequestHeaders on the shared client. Instead build a full URI for each request and use HttpRequestMessage with explicit headers, or register the named client without configuration and apply per-request headers.

#### 10. Double enumeration of enrichmentCandidates in log statement
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1380-1383

**Description:** Line 1381: enrichmentCandidates.Count(c => c.KnownPeople != null) calls LINQ Count() with a predicate on the List<TmdbDiscoverItem>, iterating it a second time just for logging. While not incorrect, it materialises a full enumeration on every generation cycle for a debug log message. In a hot path generating recommendations for many users, this is a wasted O(N) scan per user.

**Impact:** Minor performance waste. With CreditsEnrichmentBudget=20 items, the cost is small but accumulates across all users.

#### 11. RecordDismissed and RecordRequested do not update UserName on existing user records
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryFeedbackStore.cs | 201-202

**Description:** RecordDismissed (line 201) and RecordRequested (line 239) find or create a DiscoveryFeedbackResult by UserId but never update the UserName field. If a user's display name changes in Jellyfin, the stale name is retained permanently in the feedback store because only RecordShown (via GetOrCreateUserResult at line 541-543) updates it.

**Impact:** Stale display names in feedback store. Low functional impact since UserName is [JsonIgnore], but the data is inconsistent with the RecordShown path.

#### 12. Outer IOException/JsonException catch in RemoveItemLocked sets _memoryCache ??= [] only when null, masking corruption
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryCacheService.cs | 277-285

**Description:** Line 284: _memoryCache ??= []. If _memoryCache already has a non-null (but potentially partially-mutated) value when EnsureLoadedLocked() throws IOException (e.g., during a race with another writer), the existing mutated cache is left intact and the null-coalescing has no effect. The comment says 'prevent repeated failed disk reads' but the guard is only effective when cache is null.

**Impact:** If EnsureLoadedLocked throws due to a concurrent file lock while cache is already populated, the error is silently swallowed without invalidating the cache, leaving stale or inconsistent data.

### MEDIUM / SECURITY

#### 1. Export backup includes API keys in plaintext
**File:** Jellyfin.Plugin.JellyfinHelper/Api/BackupController.cs | 58

**Description:** ExportBackup() calls _backupService.CreateBackup() and serializes the result to JSON for download. Unlike GET /Configuration which masks API keys with '***', the backup export path does not pass through ConfigurationResponse.FromConfig(). If CreateBackup() includes the raw PluginConfiguration (which has plain-text SeerrApiKey and Arr instance ApiKey fields), those credentials are exported in the download.

**Impact:** Any admin who exports a backup and shares the file (e.g. for support, version control, cloud storage) inadvertently leaks all configured API keys. The credentials can be used to access Radarr, Sonarr, and Seerr instances.

**Suggested Fix:** In BackupService.CreateBackup(), mask or omit API keys in the configuration section (emit the mask sentinel or empty string). The import path already has BackupSanitizer/BackupValidator â€” add a corresponding masking step at export time. Alternatively document clearly in the UI that the backup contains credentials.

#### 2. Admin request endpoint allows arbitrary SeerrUserId â€” impersonation of any Seerr user
**File:** Jellyfin.Plugin.JellyfinHelper/Api/DiscoveryController.cs | 163

**Description:** SubmitRequest() at line 115 in DiscoveryController accepts dto.SeerrUserId as a free integer and forwards it directly to SubmitRequestAsync() (line 163). Any admin can submit a request as any Seerr user ID without any verification that the ID exists or is valid.

**Impact:** Admins can generate requests attributed to any Seerr user, potentially filling another user's request quota or submitting on behalf of users who have not consented, bypassing Seerr's own user-level request limits.

**Suggested Fix:** Add server-side verification that the SeerrUserId corresponds to a real Seerr user (via GetSeerrUsersAsync). The user list is already available through the GetSeerrUsers endpoint. Alternatively enforce that this field is only usable when the calling Jellyfin admin has a corresponding Seerr admin account.

#### 3. GetExternalLinksConfig leaks Seerr base URL to all authenticated users â€” information disclosure
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 243

**Description:** GetExternalLinksConfig() at line 230 returns the raw SeerrUrl from the plugin configuration to any authenticated Jellyfin user when DiscoveryUserAccessEnabled is true. The URL may reveal internal hostnames, private IP addresses, or non-public service infrastructure.

**Impact:** Internal network topology disclosure to all Jellyfin users. An attacker who has obtained any Jellyfin user credentials can enumerate the internal Seerr host and port.

**Suggested Fix:** Consider whether users actually need the full server URL, or only a public-facing base URL. If the Seerr instance is on a private network, expose a proxy URL or omit the field entirely. At minimum, validate that the URL is a public HTTPS URL before returning it.

#### 4. UserName logged without sanitisation â€” potential log injection
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 64-98

**Description:** At line 87, `user.Username` is interpolated directly into a warning log message: `$"Failed to build profile for user '{user.Username}'"`. Similarly at lines 69 and 325, usernames are logged in info/debug messages. Jellyfin usernames can contain arbitrary characters including newlines, ANSI escape codes, or structured log field separators (e.g. `"} { malicious_field: evil_value`). Depending on the log sink (structured logging with JSON output), a crafted username could inject additional log fields or corrupt log output.

**Impact:** An admin who creates a user with a crafted username can inject content into plugin log output. Low privilege escalation risk but may corrupt log integrity.

**Suggested Fix:** Sanitise username before logging: replace newlines and control characters. Alternatively use structured logging with the username as a separate parameter: `_logger.LogWarning("Failed to build profile for user {Username}", user.Username)` so the logger escapes it automatically.

#### 5. Path traversal risk: _weightsPath is used without sanitization in TryLoadWeights and TrySaveWeights
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/LearnedScoringStrategy.cs | 816-903

**Description:** Both TryLoadWeights() (line 816) and TrySaveWeights() (line 910) use `_weightsPath` directly with `File.ReadAllText`, `File.Exists`, `Path.GetDirectoryName`, and `Directory.CreateDirectory`. The weightsPath is supplied by the caller (plugin configuration). If a malicious plugin configuration supplies a path like `../../sensitive_file.json`, the code will read or write to that path. The same issue exists in NeuralScoringStrategy and EnsembleScoringStrategy.

**Impact:** Depending on the plugin host permissions, an attacker who can write plugin configuration could read or overwrite arbitrary files accessible to the Jellyfin process.

**Suggested Fix:** Validate that _weightsPath resolves to a path within an allowed base directory (e.g., the plugin data directory). Use Path.GetFullPath() to resolve and then verify the prefix.

#### 6. Arr base URL reflected into log and error message without sanitization
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Arr/ArrIntegrationService.cs | 88-98

**Description:** In `TestConnectionAsync`, `GetRadarrMoviesAsync`, and `GetSonarrSeriesAsync`, the raw `baseUrl` parameter is embedded directly into log messages (lines 88, 94, 159, 219, 224) and user-facing error messages (line 98). If `baseUrl` contains log-injection characters (newlines, ANSI escape codes) or is attacker-controlled (via a malicious config restore), these characters appear verbatim in log output. The `EnsureApiKeyHeaderSafe` guard only protects the API key, not the URL.

**Impact:** An attacker who can supply a crafted `baseUrl` (e.g. via the backup restore path) can inject newlines into log files, potentially splitting log entries or injecting fake log records. This is a low-severity log injection issue.

**Suggested Fix:** Sanitize `baseUrl` in log messages by replacing `\r`, `\n` with whitespace, or truncate to a safe length before embedding in log messages.

#### 7. ResolveBatchGenerationFilePath uses Path.Join without canonicalization â€” potential path traversal via plugin data folder configuration
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 2007-2011

**Description:** ResolveBatchGenerationFilePath combines Plugin.Instance.DataFolderPath with a hardcoded filename via Path.Join. If DataFolderPath is controlled by user-supplied plugin configuration or environment and contains path-traversal components (e.g. '../../../etc'), Path.Join will not sanitize them. Path.Join does not resolve the path; Path.GetFullPath is needed to canonicalize.

**Impact:** Low in practice because Plugin.Instance.DataFolderPath is set by Jellyfin's own plugin infrastructure and not directly user-editable, but defense-in-depth suggests canonicalization.

**Suggested Fix:** Wrap the result with Path.GetFullPath and verify it starts with a known safe prefix (e.g. the plugin data root) before using it in File I/O.

#### 8. Constructor falls back to empty string path when Plugin.Instance is null â€” file operations write to current working directory
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryFeedbackStore.cs | 64-65

**Description:** Line 64: var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty. Path.Join(string.Empty, FileName) returns the filename alone (relative path). If Plugin.Instance is null (e.g., during test execution or early startup), File.ReadAllText and AtomicFile.WriteAllText operate on the current working directory, which may be outside the plugin's intended data folder. The same issue exists in DiscoveryCacheService (line 71).

**Impact:** Data written to unexpected location; potential for writing files to system directories if the process working directory is a privileged location. Could also fail silently or corrupt unrelated files.

### MEDIUM / CORRECTNESS

#### 1. UpdateLogLevel does not call SaveConfiguration after mutating config â€” log level change is lost on restart
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 86

**Description:** At line 155, config is obtained via _configService.GetConfiguration() which likely returns the in-memory config object. Line 167 sets config.PluginLogLevel = level. Line 168 calls _configService.SaveConfiguration(). This appears correct, BUT GetConfiguration() and GetConfig() (used by the GET endpoint at line 86) come from two different services (_configService vs _configHelper). If SaveConfiguration() persists only the _configService instance and GetConfig() reads from _configHelper which may hold a separate reference, the persisted value and the served value may diverge.

**Impact:** If ICleanupConfigHelper and IPluginConfigurationService hold different configuration objects, the GET /Configuration endpoint will return the old log level even after a successful PUT /Configuration/LogLevel, confusing the UI and potentially causing the level to be overwritten on the next POST /Configuration.

**Suggested Fix:** Confirm that _configHelper.GetConfig() and _configService.GetConfiguration() return the same underlying object reference. If they do not, unify via a single service. The GET endpoint should read from _configService, not _configHelper, to guarantee it reflects the same state that PUT writes.

#### 2. Profile validation allows rootFolder=null when matchedProfile.RootFolder is non-empty â€” authorization bypass
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 377

**Description:** In SubmitMyRequest() at line 403-409: when profileHasRootFolder is true, the check is 'rootFolder == null || !string.Equals(rootFolder, matchedProfile.RootFolder)' returns 403. This correctly blocks a non-matching rootFolder. However, when rootFolder IS null and the profile HAS a root folder, the condition 'rootFolder == null' is true and the 403 is returned. This seems correct â€” but look at the condition more carefully: it returns 403 when rootFolder is null AND profileHasRootFolder is true. This means the user must provide the EXACT rootFolder to proceed. That is intentional per the comment. However, the inverse path (line 411-416) checks 'else if (rootFolder != null)' when profile has NO root folder â€” also returns 403. So a user who provides a rootFolder when the profile doesn't specify one is blocked. This logic looks correct on initial read, but there's a subtle issue: a user can bypass the entire ServerId/ProfileId block by simply NOT sending ServerId, ProfileId, or rootFolder (all null/absent). The condition on line 377 is 'if (dto.ServerId.HasValue || dto.ProfileId.HasValue || rootFolder != null)' â€” if all three are null/absent, the entire authorization block is skipped, and the request is submitted with no profile override. This is by design (uses Seerr defaults), but it means a user who has CanRequest=true but zero allowed profiles can still submit a request using server defaults without any profile authorization check.

**Impact:** A user with CanRequest=true but permissions.Profiles.Count==0 can still submit a request using Seerr's default quality profile and root folder. The check at line 387 ('if permissions.Profiles.Count == 0 â†’ 403') is only reached if the user explicitly provides a ServerId or ProfileId. Omitting both skips all profile authorization.

**Suggested Fix:** Move the 'profiles.Count == 0' check outside the ServerId/ProfileId conditional block so it executes regardless of whether the user specifies overrides. If CanRequest is true but Profiles is empty, the user may request only with server defaults â€” verify this is the intended authorization model and document it explicitly.

#### 3. Arr instance key restoration uses Name+Url equality with no collision guard for duplicate names
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 505

**Description:** ApplyRequestToConfig() at lines 505-509 restores a masked API key by calling FirstOrDefault(p => p.Name == instance.Name && p.Url == instance.Url) against the previous list. If two Radarr instances have the same Name and Url (which the validator does not prevent), the first match wins and the second instance's key is silently assigned the first instance's key.

**Impact:** Duplicate-named instances silently receive the wrong API key after a round-trip through the UI. The second instance's key is effectively overwritten with the first's. No error or warning is produced.

**Suggested Fix:** Add a uniqueness check in ConfigurationRequestValidator.ValidateArrInstances() that rejects requests containing duplicate Name+Url combinations. This prevents the ambiguity without requiring a positional fallback.

#### 4. GetDiscoveryResults normalizes MediaType inconsistently with UserDiscoveryController
**File:** Jellyfin.Plugin.JellyfinHelper/Api/DiscoveryController.cs | 57

**Description:** In DiscoveryController.GetDiscoveryResults() at line 57, the excluded set lookup uses 'r.MediaType?.ToLowerInvariant() ?? "movie"' directly without trimming whitespace first. In UserDiscoveryController.GetMyDiscoveryResults() at line 87-89, the normalization is 'string.IsNullOrWhiteSpace(r.MediaType) ? "movie" : r.MediaType.Trim().ToLowerInvariant()'. The admin endpoint skips Trim(), so a cached MediaType value of ' movie' (with leading space) would not be found in the excluded set in the admin view but would be correctly normalized in the user view.

**Impact:** The admin discovery view may show items to the admin that are correctly hidden in the user view, causing inconsistent dismissal/requested-item filtering between the two endpoints.

**Suggested Fix:** Apply the same normalization in DiscoveryController: use 'string.IsNullOrWhiteSpace(r.MediaType) ? "movie" : r.MediaType.Trim().ToLowerInvariant()' in the Where predicate.

#### 5. ValidateArrInstances allows an instance with only an API key and no URL â€” partial configuration stored
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationRequestValidator.cs | 236

**Description:** ValidateArrInstances() at line 236 skips 'completely empty instances' when both Url and ApiKey are whitespace. The subsequent URL format check at line 242 only fires when 'Url is not whitespace'. The required-key-when-URL-set check at line 252 reads: 'if (IsNullOrWhiteSpace(Url) || !IsNullOrWhiteSpace(ApiKey)) continue' â€” meaning: if URL is empty OR key is present, skip the error. So an instance with an empty URL but a non-empty ApiKey passes all validation checks and is stored.

**Impact:** Instances with an API key but no URL are silently accepted and stored. They produce connection-test skips (the skip guard at ConfigurationController line 371 checks IsNullOrWhiteSpace for both Url and ApiKey), but they occupy one of the three allowed instance slots, pollute the configuration, and confuse the UI.

**Suggested Fix:** Add an explicit check: if ApiKey is non-empty but Url is empty, return an error: '{typeName} instance has an API key but no URL.' This is symmetric with the existing URL-but-no-key check.

#### 6. ValidateTrashPathStrict redundantly checks control characters â€” logic overlap with subsequent FirstOrDefault
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationRequestValidator.cs | 118

**Description:** Line 118 checks 'trashFolderPath.FirstOrDefault(static c => c < '\x20')' to find control chars, and line 119 also checks 'trashFolderPath.Contains('\0')'. But '\0' has value 0 which IS less than '\x20' (32), so the Contains('\0') check is fully subsumed by the FirstOrDefault check. Additionally, the check on line 110 includes '*', '?', '<', '>', '|', '"' in the 'all-invalid-chars' guard, but line 127-131 then checks those same characters again individually. The all-invalid-chars guard at line 110 only fires when ALL characters in the string are from that set â€” it does not catch a mix like '/validname*'. The individual char check on line 127 is the actual operative guard for those characters.

**Impact:** No functional defect â€” the redundant checks are harmless. But the double-checking of null char and the confusing 'all chars are invalid' guard create maintenance risk and make the code harder to reason about.

**Suggested Fix:** Remove the redundant Contains('\0') check on line 119 since it is already covered by 'c < \x20'. Rename or document the line 110 guard to clarify it only catches paths that are ENTIRELY composed of those characters (a narrow edge case).

#### 7. AverageCommunityRating accumulates ratings for favorited-but-unplayed items including synthetic series entries
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 240-244

**Description:** The community rating accumulation block (lines 240-244) runs for any item that passed the earlier `!userData.Played && userData.PlaybackPositionTicks <= 0 && !userData.IsFavorite` guard â€” meaning items that are ONLY favorited also contribute to ratingSum/ratingCount. Synthetic series WatchedItemInfo entries (created at line 275) have IsFavorite=true and CommunityRating set from series metadata. These contribute to the average even though the user has never watched any content from the series. A user who favorites 50 unstarted series will have an AverageCommunityRating that reflects the community quality of those series as much as what they actually watch. The field is documented as 'average community rating of watched items', which is violated.

**Impact:** AverageCommunityRating is misleading and will produce incorrect results when used as a training feature, since it mixes watched quality signals with favorited-but-unwatched signals.

**Suggested Fix:** Gate the rating accumulation inside the `if (userData.Played)` block, or create a separate FavoriteAverageCommunityRating field for the other signal.

#### 8. People 15% threshold creates undocumented asymmetry with genre/language profile filters
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 537-545

**Description:** BuildPeopleProfile applies an additional 15% progress threshold (line 541) beyond the outer BuildProfile item-inclusion guard (line 170-173). The outer guard admits items with PlaybackPositionTicks > 0 (any position). The people filter requires >= 15% progress for non-played, non-favorite items. This means an item between 1-14% progress will appear in WatchedItems, GenreDistribution, and LanguageProfile, but will NOT appear in PeopleProfile. These inconsistent filters are applied to the same WatchedItems collection at different stages. If this asymmetry is intentional it must be documented; if unintentional it is a logic error.

**Impact:** Genre and language preferences are shaped by low-engagement items that people never get credited for in the people profile. ML features derived from different sub-profiles will reflect different effective 'watched' sets, degrading model consistency.

**Suggested Fix:** Either align the filters (apply 15% threshold at item-inclusion time so all sub-profiles use the same effective set), or document the intentional asymmetry with a cross-reference comment.

#### 9. LoadResults exception filter narrower than SaveResults â€” SecurityException and NotSupportedException propagate unhandled
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/RecommendationCacheService.cs | 123

**Description:** SaveResults catches six exception types: IOException, UnauthorizedAccessException, JsonException, SecurityException, NotSupportedException, and ArgumentException (lines 83-88). LoadResults only catches three: IOException, JsonException, UnauthorizedAccessException (line 123). On a system where the file exists but a SecurityException is raised on read (locked-down account, ACL change between save and load), or where NotSupportedException occurs (path with unsupported characters on the OS), the exception propagates out of LoadResults unhandled, crashing the caller (RecommendationController.GetAllRecommendations or GetUserRecommendations). This breaks the service's stated best-effort contract.

**Impact:** An unhandled SecurityException or NotSupportedException from LoadResults crashes the scheduled task or API request with a 500 error rather than gracefully falling through to fresh generation.

**Suggested Fix:** Extend the LoadResults catch filter to match SaveResults: `catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)`.

#### 10. GetUserWatchProfile performs two full library scans per call â€” O(N) hidden cost invisible to callers
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 51-59

**Description:** GetUserWatchProfile at line 51 calls BuildProfile(user) with no pre-loaded items. BuildProfile then calls LoadAllVideoItems() and LoadAllSeriesItems() on demand (lines 149 and 257), each issuing a full library query. GetAllUserWatchProfiles amortises both queries across all users, but any single-user caller (the RecommendationController's GET /WatchProfile/{userId} endpoint, or any per-user recommendation task) pays the full library scan cost twice per call. With a large library (10K+ items) this can take meaningful time. The interface contract does not document this cost, and callers have no way to pass pre-loaded state.

**Impact:** Every HTTP request to GET /JellyfinHelper/Recommendations/WatchProfile/{userId} issues two full ILibraryManager.GetItemList queries, which may be database-backed and hold locks.

**Suggested Fix:** Either document the performance contract on GetUserWatchProfile clearly, or add an overload that accepts pre-loaded lists, or cache the library snapshot for a short TTL.

#### 11. SubtitleStreamIndex guard checks >= 0 but subtitle stream index can legitimately be 0 â€” creates subtle edge case
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 436-437

**Description:** At line 436, the subtitle section checks `userData.SubtitleStreamIndex.HasValue && userData.SubtitleStreamIndex.Value >= 0`. Stream index 0 is a legitimate subtitle track index. The intent of `>= 0` is presumably to exclude the sentinel value -1 (which Jellyfin uses to mean 'no subtitle selected'). This is correct. However the comment does not explain the -1 sentinel distinction, and a future reader may be confused about why HasValue alone is not sufficient. Additionally, the audio section (line 395-398) does NOT perform this >= 0 guard â€” it accepts any AudioStreamIndex including hypothetical negative values. If Jellyfin ever uses -1 as a 'no audio track' sentinel (as it does for subtitles), audio entries with a negative index would incorrectly try to look up stream data.

**Impact:** If Jellyfin ever stores -1 as AudioStreamIndex for 'default audio', a FirstOrDefault miss would silently fall through to audioStreams[0] (the default fallback at line 403), which is actually the correct behaviour. But if the sentinel is stored differently the comment asymmetry will cause confusion. Low-risk but worth documenting.

**Suggested Fix:** Add a comment explaining the -1 sentinel for SubtitleStreamIndex. Apply the same `>= 0` guard on AudioStreamIndex for consistency and future-proofing.

#### 12. NormalizeLanguage catch-all returns unmapped 3-letter codes as-is â€” pollutes LanguageProfile with ISO 639-2 codes
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 722

**Description:** The final catch-all at line 722 (`_ => lower`) returns any unmapped code, including 3-letter ISO 639-2 codes like 'lat' (Latin), 'tlh' (Klingon), 'mis' (uncoded), 'mul' (multiple), 'und' (undetermined). These will be stored as-is in LanguageProfile and SubtitleLanguageProfile. If the same item has the same language stored as different representations across metadata providers (e.g. 'und' on one track, 'zxx' on another), they will produce separate LanguageProfile entries that represent the same semantic concept. More critically, codes like 'und' or 'mul' are anti-signals and should be normalised to null rather than passed through as apparent language preferences.

**Impact:** 'und', 'mul', 'mis', 'zxx', 'qaa'-'qtz' (private use) will all generate LanguageProfile entries that look like real languages to the recommendation engine, degrading language preference signals.

**Suggested Fix:** Add explicit null returns for 'und', 'mul', 'mis', 'zxx' and the private-use range check (`lower.StartsWith("qa")`) before the length-2 passthrough. Document the catch-all semantics.

#### 13. XavierInit_IsDeterministic compares two independently-constructed strategies but does not verify the weights are non-default (non-Xavier) after Train()
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/NeuralScoringStrategyTests.cs | 277-284

**Description:** XavierInit_IsDeterministic constructs s1 and s2 and asserts their initial weights match. This is valid for testing deterministic init. However there is no test that asserts s1 and s2 produce DIFFERENT weights after training on the same data â€” which would verify that training is not a no-op and that weights are mutable. The existing Train_UpdatesWeights covers this partially, but a code path where weights are copied rather than updated would pass both tests.

**Impact:** Minor: determinism is validated but mutation-after-deterministic-init is not end-to-end validated. Low risk given Train_UpdatesWeights exists.

**Suggested Fix:** Low priority. Note that Train_UpdatesWeights adequately covers the mutation path. The determinism test is sound as-is.

#### 14. Train_MultipleTimes_ProducesFiniteLoss asserts loss2 <= loss1 + 0.05 but with only 20 examples and Adam optimizer this tolerance could hide persistent divergence
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/NeuralScoringStrategyTests.cs | 519-533

**Description:** The test allows loss2 to be 0.05 higher than loss1 and calls this 'not regressing significantly'. With a small dataset (20 examples), Adam + early stopping, and potential stochastic variation from dropout (which is disabled below MinExamplesForDropout=30, so this is deterministic), loss2 > loss1 should never happen at all since both training runs see the exact same examples and start from the same deterministic initialisation. The 0.05 tolerance is unnecessarily loose and could mask a genuine divergence bug.

**Impact:** The test could pass even if the second training pass substantially worsens model quality (up to 5% additional MSE), masking a training loop regression.

**Suggested Fix:** Since GenerateExamples uses a fixed seed (42) and training with 20 examples is below MinExamplesForDropout (dropout is off), both runs are fully deterministic. Assert Assert.Equal(loss1, loss2, 6) or at most allow a tiny epsilon.

#### 15. ApplyCohortFeedback_InsufficientControlSamples_NoOp: controlResult has recCount=3 watchedCount=3, but 'insufficient' gate threshold is not documented â€” test may be testing wrong boundary
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/EnsembleScoringStrategyAdvancedTests.cs | 162-179

**Description:** BuildCohortResult('control', 3, 3, out cw) creates a control cohort with 3 recommendations all watched (100% CTR). The test asserts the offset does not change, implying 3 samples is below the minimum qualifying threshold for the control cohort. However the threshold is not referenced by name â€” the test relies on an implicit knowledge that 3 < MinControlSamples (or whatever the constant is). If the threshold is later changed to 2 or 1, this test would start testing the 'sufficient samples, control optimal' path instead of 'insufficient samples', silently inverting the test's meaning.

**Impact:** Test correctness is brittle to threshold changes. Could silently test the wrong behavior after a configuration change.

**Suggested Fix:** Reference the minimum sample threshold constant by name from EnsembleScoringStrategy, e.g. recCount = EnsembleScoringStrategy.MinCohortSamples - 1.

#### 16. PhantomRowsForDeletedSeries test asserts live weight is 1.0 in BOTH profiles but does not assert that 'Phantom' key is absent from the 'withPhantoms' vector
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/PreferenceBuilderTests.cs | 840-853

**Description:** BuildGenrePreferenceVector_PhantomRowsForDeletedSeries_AreIgnored asserts that Live weight is 1.0 in both vectorWith and vectorWithout. This indirectly verifies the phantom rows did not contribute (otherwise Live would not be the max). However it does not assert that 'Phantom' is absent from vectorWith.Keys. A regression where phantom rows ARE included would cause 'Phantom' to appear in the vector AND reduce Live's normalised weight below 1.0 (because Phantom would become the max), which the test would catch. But if phantom rows are partially included (contributing to the denominator but not adding a new key), the test could theoretically miss it. The stronger assertion is direct.

**Impact:** Low â€” the indirect check via Live=1.0 is equivalent to verifying Phantom is absent (any Phantom weight would become the max and pull Live below 1.0). But for clarity and explicitness of intent, a direct assertion is preferable.

**Suggested Fix:** Add Assert.DoesNotContain('Phantom', vectorWith.Keys, StringComparer.OrdinalIgnoreCase) for explicitness.

#### 17. Phase 1 series switch: `case true when wasWatched && watchedItemForRec is null` branch sets `completionRatio = 0.5` but the label block below uses `features.CompletionRatio` which is set to `completionRatio` later
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 337

**Description:** For a series-level favourite with no watched episodes (`case true when wasWatched && watchedItemForRec is null`, line 337-345), `completionRatio = 0.5` is set and `hasUserInteraction = false`. The label block (line 448-482) then inspects `watchedItemForRec` which is `null`. The first case in the label switch (`case { IsFavorite: true, Played: false, PlaybackPositionTicks: <= 0, PlayCount: <= 0 }`) will never match a null `watchedItemForRec`. Control falls to `case null when isSeries` at line 459-460 and sets `baseLabel = 0.65`. This is correct behavior â€” but the correctness relies on the pattern `case null when isSeries` appearing before `default`. If the order of those cases were ever swapped, a series-level favourite with no episode data would enter `default` and compute `ContentScoring.ComputeEngagementLabel(0.5) = 0.675` instead of the explicit 0.65. This is a fragile ordering dependency.

**Impact:** No current bug, but the label assignment correctness is order-dependent in the switch expression. A future refactor swapping cases would silently change labels for series-level favourites.

**Suggested Fix:** Add a comment to the `default` case explicitly noting that `case null when isSeries` must appear before `default`, or restructure the check to be order-independent.

#### 18. `organicFallbackTimestamp` uses `previousResults.Min(r => r.GeneratedAt)` which scans all results â€” if `previousResults` is empty, `DateTime.UtcNow.AddDays(-90)` is used, but the call chain guarantees non-empty at line 29's overload entry
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 530

**Description:** The guard comment at line 529 says 'Guard: if previousResults is empty (no prior recommendation runs), use a conservative fallback 90 days ago. This path is defensive only.' However, `DateTime.UtcNow.AddDays(-90)` is evaluated at runtime every single training run, making the `organicFallbackTimestamp` non-deterministic across runs when `previousResults` IS empty (which the comment says is 'defensive only'). More importantly, when `previousResults` is non-empty, this is fine. The only concern is that the two-branch ternary evaluates `previousResults.Min(r => r.GeneratedAt)` which scans the entire list every run â€” this is O(N) and acceptable, but the pattern is slightly cleaner with `LINQ.MinBy`.

**Impact:** Minor: no correctness bug. Informational only.

**Suggested Fix:** No action required. Minor style note.

#### 19. Phase 2 series aggregation: `seriesEpisodeLookupOrganic` is built from ALL watched items (including non-meaningful interactions) but the outer loop only processes `w.HasMeaningfulInteraction()` items
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 592

**Description:** At lines 550-565, `seriesEpisodeLookupOrganic` is populated from all `userProfile.WatchedItems` without filtering for `HasMeaningfulInteraction()`. Then at lines 612-633, `TrainingFeatureComputer.AddAggregatedSeriesExample` is called with `seriesEpisodes` from this lookup. `AddAggregatedSeriesExample` computes `playedEps = episodes.Count(e => e.Played)` and `completionRatio = episodes.Average(ContentScoring.ComputeCompletionRatio)` over all episodes in the list, including those with no meaningful interaction. Episodes with zero interaction (`Played=false, IsFavorite=false, PlayCount=0, PlaybackPositionTicks=0`) dilute the average completion ratio and the played count, potentially under-counting engagement for series where many episodes were skipped entirely.

**Impact:** Series engagement signals (completion ratio, played episode count) in Phase 2 organic examples are diluted by episodes with no interaction. A fully-watched 5-episode series where 10 additional episodes exist but were never opened will have `completionRatio = episodes.Average(...)` include 10 zero-contribution rows, yielding a lower-than-true completion ratio and potentially triggering the abandoned label path incorrectly.

**Suggested Fix:** Filter `seriesEpisodeLookupOrganic` to only include episodes that pass `HasMeaningfulInteraction()`, or filter inside `AddAggregatedSeriesExample` before computing the aggregates.

#### 20. `AddAggregatedSeriesExample`: `ratedEpisodes.Average(e => e.UserRating!.Value) / 10.0` uses null-forgiving operator on nullable double â€” safe only because `Where(e => e.UserRating is > 0)` filters nulls, but `double?` null-forgive is fragile
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingFeatureComputer.cs | 237

**Description:** At line 239, `ratedEpisodes.Average(e => e.UserRating!.Value)` uses the null-forgiving operator `!` on `UserRating` which is `double?`. The preceding `Where(e => e.UserRating is > 0)` does filter out null values (null is not > 0). However, the null-forgiving operator suppresses the compiler warning and hides the assumption. If the filter predicate were ever changed or the type changed, a NullReferenceException could result.

**Impact:** No current NullReferenceException at runtime because the Where filter guarantees non-null. But the null-forgiving operator is a code-smell that will silently fail if the filter is removed or weakened.

**Suggested Fix:** Change to `ratedEpisodes.Average(e => e.UserRating ?? 0.0) / 10.0` and remove the filter for null, or use `e.UserRating!.Value` with an explicit comment explaining why null is impossible here.

#### 21. `combinedCriticScore = Math.Clamp(entry.TmdbRating / 10.0, 0.0, 1.0)` diverges from `ContentScoring.ComputeCombinedCriticScore` â€” train/serve parity gap for the CombinedCriticScore feature in Phase 4
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/DiscoveryFeedbackExampleBuilder.cs | 198

**Description:** At line 198, the `CombinedCriticScore` for a Phase 4 (discovery feedback) training example is computed as `Math.Clamp(entry.TmdbRating / 10.0, 0.0, 1.0)`. The live inference path in `ExternalCandidateFeatureBuilder.Build` (line 76) uses `Math.Clamp(candidate.VoteAverage / 10.0, 0.0, 1.0)`. Both are just `tmdbRating / 10` and are numerically equivalent. However, `ContentScoring.ComputeCombinedCriticScore` (which is the shared helper used for library items) would also handle `NaN` and `Infinity` by returning 0.5, while `DiscoveryFeedbackExampleBuilder` does not guard for those cases. If `entry.TmdbRating` is `double.NaN` or `double.PositiveInfinity` (possible from deserialized JSON), `Math.Clamp(NaN, 0.0, 1.0)` returns `NaN` (IEEE 754 behavior), and `NaN` will propagate into the `CombinedCriticScore` feature and subsequently into `PopularityScore` computation.

**Impact:** If a persisted `DiscoveryFeedbackEntry` has a `TmdbRating` of `NaN` or `Infinity` (from malformed JSON or a stale entry), the resulting training example will contain `CombinedCriticScore = NaN`, which propagates to `PopularityScore = NaN`. The neural training step may produce corrupted weights or degenerate gradients.

**Suggested Fix:** Guard `entry.TmdbRating` before division: `var combinedCriticScore = double.IsFinite(entry.TmdbRating) && entry.TmdbRating >= 0 ? Math.Clamp(entry.TmdbRating / 10.0, 0.0, 1.0) : 0.5;`

#### 22. Phase 4 `preferredPeople` is built from `userProfile.TopPeople` (a count-filtered, top-20 subset) rather than `PreferenceBuilder.BuildPeoplePreferenceWeights` â€” diverges from live inference path for PeopleSimilarity computation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/DiscoveryFeedbackExampleBuilder.cs | 73

**Description:** At lines 73-79, the `preferredPeople` HashSet for discovery examples is built from `userProfile.TopPeople` (which is a cached list of names from `PeopleProfile` with count >= 2, max 20). The live `ExternalCandidateFeatureBuilder.Build` also uses `preferredPeople` passed in from the caller. But in `DiscoveryFeedbackExampleBuilder`, `ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames` is called with this `HashSet<string>`. The formula in `ComputePeopleSimilarityFromNames` uses `Math.Min(preferredPeople.Count, MinPeopleForFullScore)` as the denominator. If `TopPeople` has fewer than 5 entries and the live path also uses a small set, these are consistent. However, the `PeopleProfile` used to build `TopPeople` may be stale relative to `cachedPeopleLookup` used in Phases 1-3. More critically, Phase 4 uses the unweighted HashSet approach while Phases 1-3 use the weighted `BuildPeoplePreferenceWeights` dictionary with `SimilarityComputer.ComputePeopleSimilarity(HashSet, IReadOnlyDictionary)`. These are fundamentally different scoring formulas for the same feature slot.

**Impact:** PeopleSimilarity is computed with different formulas in Phase 4 (overlap-coefficient via `ComputePeopleSimilarityFromNames`) vs. Phases 1-3 (weighted-budget via `SimilarityComputer.ComputePeopleSimilarity`). The model trains on inconsistent PeopleSimilarity distributions across example sources, potentially learning confounded weights for this feature.

**Suggested Fix:** Phase 4 should either build `preferredPeopleWeights` via `PreferenceBuilder.BuildPeoplePreferenceWeights` and use `SimilarityComputer.ComputePeopleSimilarity(candidatePeopleSet, weights)` for consistency, or the discrepancy should be acknowledged and documented as intentional (discovery items use a simpler people-similarity because they were already scored that way at inference via `ExternalCandidateFeatureBuilder`).

#### 23. `IsPhantomSeriesRow` uses `ContainsKey` (not `TryGetValue`) on `seriesEpisodeCounts` â€” minor: works correctly but is a style inconsistency vs. the rest of the file where `TryGetValue` is used everywhere
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/PreferenceBuilder.cs | 901

**Description:** At line 901, `return !seriesEpisodeCounts.ContainsKey(sid)` uses `ContainsKey` rather than the more idiomatic `!seriesEpisodeCounts.TryGetValue(sid, out _)`. This is purely a style issue â€” both have O(1) cost and identical semantics â€” but the summary in the prompt states 'Zero ContainsKey calls remain. Everything is clean,' which is incorrect: `ContainsKey` appears here at line 901 and at line 944 in `BuildWatchedEpisodesPerSeries`.

**Impact:** No functional impact. The claim in the PR summary is incorrect â€” ContainsKey is still present in two locations.

**Suggested Fix:** Replace with `!seriesEpisodeCounts.TryGetValue(sid, out _)` if strict zero-ContainsKey policy is desired. Low priority.

#### 24. RemovalRegex does not use RegexOptions.IgnoreCase â€” misses mixed-case tags
**File:** Jellyfin.Plugin.JellyfinHelper/Services/FileTransformation/DiscoveryScriptTag.cs | 28

**Description:** The removal regex pattern matches `plugin=["']Jellyfin Helper["']` case-sensitively. If an older version of the plugin or a manual edit produced a tag with `Plugin=` or `PLUGIN=` (upper or mixed case), RemovalRegex.Replace() will silently fail to remove it, leaving a duplicate script tag in index.html.

**Impact:** On upgrade from a hypothetical older version that injected a mixed-case attribute, the old tag is not removed, causing the discovery sidebar script to be loaded twice.

**Suggested Fix:** Add RegexOptions.IgnoreCase to the RemovalRegex options flags.

#### 25. jTokenType resolved with null-forgiving operator but never null-checked
**File:** Jellyfin.Plugin.JellyfinHelper/Plugin.cs | 220

**Description:** On line 220, `newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JToken")!` uses the null-forgiving operator. GetType() returns null if the type is not found. The null-forgiving suppresses the compiler warning but does not prevent a NullReferenceException at runtime when `addMethod` is resolved on line 221 using jTokenType as an argument type.

**Impact:** If the Newtonsoft.Json assembly present at runtime lacks JToken (extremely unlikely but possible with a stripped or incompatible version), a NullReferenceException is thrown inside the reflection path. This is caught by the broad catch block, but the logged warning message says 'Failed to register' with no clue about which type was missing.

**Suggested Fix:** Add an explicit null check: `if (jTokenType == null) { _logger.LogWarning(...); return false; }` immediately after line 220, matching the pattern used for other reflection lookups in the same method.

#### 26. Seerr Discovery, User Activity, and Recommendations all share RecommendationsTaskMode â€” Deactivate skips all three
**File:** Jellyfin.Plugin.JellyfinHelper/ScheduledTasks/HelperCleanupTask.cs | 133

**Description:** User Watch Activity, Smart Recommendations, and Seerr Discovery all use `config.RecommendationsTaskMode` as their Mode in the subTasks array. If an operator sets RecommendationsTaskMode to Deactivate to stop recommendations from running, user activity data collection is also silently skipped. These are logically independent operations.

**Impact:** Operators who want activity tracking but not recommendations (or vice versa) have no way to control them independently. User activity data (useful for analytics) is silently not collected.

**Suggested Fix:** Introduce a separate UserActivityTaskMode configuration property, or at minimum document this coupling prominently in the UI and in the task description.

#### 27. SaveLatestResult accepts null without ArgumentNullException guard
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Statistics/StatisticsCacheService.cs | 46

**Description:** SaveLatestResult() takes a MediaStatisticsResult parameter but has no null guard. If null is passed, JsonSerializer.Serialize(null, options) produces the JSON literal `null`, which is then written to disk. On the next LoadLatestResult() call, Deserialize returns null and the service silently returns null, appearing to callers as if no data exists rather than surfacing the programming error.

**Impact:** A null result silently poisons the cache file with the JSON `null` token, erasing valid previously-cached data.

**Suggested Fix:** Add `ArgumentNullException.ThrowIfNull(result)` at the start of SaveLatestResult, matching the pattern already used in UserActivityCacheService.SaveResult().

#### 28. Train() checks validation loss quality gate BEFORE neural training result is available â€” neuralTrained is computed before the lock but evaluated inside
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 617-709

**Description:** Line 619 calls `((ITrainableStrategy)_learned).Train(examples, heldOutForMetrics)` and then line 623 calls `((ITrainableStrategy)_neural).Train(examples, heldOutForMetrics)`. Both calls happen outside the lock. But at line 628, `var validationLoss = _learned.LastValidationLoss` is read without a lock (it's outside the `lock (_syncRoot)` block). Then inside the lock at line 667, `var neuralValidationLoss = _neural.LastValidationLoss` is read while already inside the lock. The _learned.LastValidationLoss read at line 628 is correctly published under _syncRoot in LearnedScoringStrategy (line 489), so reading it without the ensemble's _syncRoot is technically a race â€” another concurrent Train() call could update it between the read and the lock acquisition.

**Impact:** In multi-threaded scenarios where Train() is called from multiple threads concurrently, validationLoss could reflect a different training run's result. In practice Train() is serialized by the TrainingService task gate, so impact is low but the lock discipline is inconsistent.

**Suggested Fix:** Read `_learned.LastValidationLoss` inside the `lock (_syncRoot)` block, or document that Train() is single-threaded by contract.

#### 29. dropoutRng seeded with `1337 + _trainingGeneration` â€” _trainingGeneration was already incremented at line 722, so the seed differs by 1 between what the comment says and what happens
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 819

**Description:** At line 721 `var rng = new Random(42 + _trainingGeneration)`. At line 722, `_trainingGeneration++`. At line 819, `var dropoutRng = new Random(1337 + _trainingGeneration)`. Since _trainingGeneration was incremented at 722, the dropout RNG is seeded with the POST-increment value while the shuffle RNG uses the PRE-increment value. The comment says 'Both RNGs are seeded off the same generation counter' but they actually use different values of that counter (N vs N+1). This is a minor off-by-one in reproducibility, not a correctness failure.

**Impact:** The dropout RNG seed comment is misleading. Reproducibility analysis using the generation counter will be off by one for the dropout dimension. No functional impact.

**Suggested Fix:** Capture the generation before incrementing: `var gen = _trainingGeneration; _trainingGeneration++; var rng = new Random(42 + gen); var dropoutRng = new Random(1337 + gen);`

#### 30. K-fold train/val split uses shuffled index positions as boundaries, not shuffled values â€” fold membership is not random
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/LearnedScoringStrategy.cs | 380-394

**Description:** At line 351-361, a Fisher-Yates shuffle produces `allIndices` with randomly ordered example indices. At line 375-376, the fold boundaries are computed as `valStart = fold * foldSize` and `valEnd`. Then at line 386, `foldValIndices = allIndices[valStart..valEnd]` slices the SHUFFLED array. This is correct â€” the slice picks random examples because allIndices was shuffled. However, the loop at lines 389-395 that builds foldTrainIndices iterates `j` over allIndices by POSITION (j < valStart || j >= valEnd) rather than by VALUE. Since allIndices[j] is the actual example index (not j itself), the split is based on position in the shuffled array, which is correct. There is a subtle issue: the shuffle at lines 357-361 uses `rng`, which is also used inside `TrainSingleSplit` (line 663 for per-epoch shuffles). The rng state is shared between these two uses but the fold-level split only runs once. This means the per-epoch shuffle seed depends on how many examples were in the fold split, making cross-fold comparisons non-deterministic relative to each other.

**Impact:** Cross-fold loss estimates are not reproducible for a given training generation because fold-level splits share RNG state with per-epoch shuffles. Minor impact on loss estimation accuracy.

**Suggested Fix:** Use a separate Random instance for fold-level splitting, seeded off a different constant.

#### 31. Improving trend branch computes sigmoidTarget but uses wrong midpoint â€” ignores _sigmoidMidpointOffset applied to the main alpha computation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 759

**Description:** In the improving-trend branch (line 754), `sigmoidTarget = ComputeSigmoidAlpha(_trainingExampleCount, DefaultSigmoidMidpoint + _sigmoidMidpointOffset, _alphaMin, _alphaMax)`. This correctly uses the adaptive midpoint. But the main alpha calculation at line 639 uses `effectiveMidpoint = DefaultSigmoidMidpoint + _sigmoidMidpointOffset` which was computed from the same offset â€” so they are consistent. The issue is that line 759 applies a boost: `_alpha = Math.Min(sigmoidTarget, _alpha + ((_alphaMax - _alpha) * (1.0 - TrendDegradationDamping)))`. `TrendDegradationDamping = 0.90`, so `(1.0 - 0.90) = 0.10`. This means on an improving trend, alpha is boosted by 10% of the remaining gap to alphaMax. But _alpha at this point was already set to `sigmoidAlpha * qualityFactor` or `sigmoidAlpha` (line 640 or 660) inside the first lock block. Then TrendImprovementBoost constant (0.15) defined at line 155 is never used â€” the boost is hardcoded as `(1.0 - TrendDegradationDamping) = 0.10`, not `TrendImprovementBoost`. The constant `TrendImprovementBoost = 1.15` is defined but unused.

**Impact:** Dead constant. The improving trend boost is 10% instead of the documented 15% (TrendImprovementBoost). The constant misleads maintainers about the actual behavior.

**Suggested Fix:** Replace `(1.0 - TrendDegradationDamping)` with `(TrendImprovementBoost - 1.0)` at line 759, or use `(_alphaMax - _alpha) * TrendImprovementBoost` capped at alphaMax.

#### 32. Validation split size calculation can produce valCount > examples.Count - MinTrainingExamples when examples.Count is small
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 716-717

**Description:** Line 716: `var valCount = Math.Max(MinValidationExamples, (int)(examples.Count * ValidationSplitRatio))`. Line 717: `valCount = Math.Min(valCount, examples.Count - MinTrainingExamples)`. The `Math.Max` on line 716 sets valCount to at least MinValidationExamples (2). Then the `Math.Min` clamps it to `examples.Count - MinTrainingExamples`. When examples.Count = MinTrainingExamples (12), this yields `Math.Min(max(2, floor(12*0.2)=2), 12-12=0) = Math.Min(2, 0) = 0`. Then line 718 checks `useEarlyStopping = valCount >= MinValidationExamples (2)` which is false (0 >= 2 = false), so early stopping is correctly disabled. However when examples.Count = 13 or 14 the math produces valCount = 1 or 2, and useEarlyStopping becomes true with only 1 validation example â€” which may not be statistically meaningful but MinValidationExamples = 2 should guard against valCount = 1. Actually Math.Min(2, 14-12=2) = 2, so it's fine. But when examples.Count = 13: Math.Min(2, 1) = 1. useEarlyStopping = 1 >= 2 = false. Correctly disabled. The logic appears correct but is needlessly subtle.

**Impact:** No actual bug, but the multi-step clamping logic is error-prone for future modifications. A comment explaining the invariant would reduce maintenance risk.

#### 33. WriteToVector computes normalizedGenreCount with integer division when GenreCount is int
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/CandidateFeatures.cs | 453

**Description:** Line 453: `var normalizedGenreCount = Math.Clamp(GenreCount / GenreCountNormalizationCeiling, 0.0, 1.0)`. GenreCount is an int and GenreCountNormalizationCeiling is a double (5.0). In C#, `int / double` produces a double (not integer division), so this is actually correct. However, if GenreCount were ever changed to be computed as `int / int`, the result would silently be 0 for all GenreCount < 5. No current bug, but worth noting.

**Impact:** No current impact. Defensive comment noting the type dependency would prevent future bugs.

#### 34. ApplyCohortFeedback: logger reads _sigmoidMidpointOffset OUTSIDE lock after writing it INSIDE lock
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 929-935

**Description:** Lines 923-927 write `_sigmoidMidpointOffset` inside `lock (_syncRoot)`. Lines 929-935 read `_sigmoidMidpointOffset` for the log message OUTSIDE the lock using the captured local read of the field directly (`_sigmoidMidpointOffset` in the format args). At line 935, the log uses `_sigmoidMidpointOffset` which is a field read without lock. While there's no real correctness issue (the log value may be stale by a few nanoseconds), the code pattern is inconsistent with the rest of the class and creates a false sense of correctness â€” other readers of _sigmoidMidpointOffset go through the SigmoidMidpointOffset property which locks. The same issue appears at lines 963-964.

**Impact:** Log messages may display a slightly stale midpoint value. No functional correctness impact, but creates inconsistent locking discipline.

#### 35. Metrics snapshot is added and trend computed in a second separate lock acquisition â€” stale _metricsHistory visible between locks
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 713-761

**Description:** Train() uses two separate `lock (_syncRoot)` blocks: one at line 632 updates alpha/neuralBeta, and a second at line 715 records metrics and applies trend adjustments. Between these two lock releases and re-acquisitions, the state is briefly inconsistent: alpha has been updated but the trend adjustment that should immediately follow hasn't been applied yet. A concurrent Score() call reading alpha in that window gets the pre-trend-adjusted value. The trend-adjusted alpha (line 739 or 759) may differ materially from the sigmoid alpha set at line 640/660.

**Impact:** Concurrent Score() calls during the window between the two lock blocks will use a different alpha than the final committed value. Given that Train() is meant to be serialized, the practical impact is low, but it represents a design inconsistency.

**Suggested Fix:** Merge both lock blocks into a single lock acquisition to ensure alpha is committed atomically after trend adjustment.

#### 36. Early stopping restores best weights unconditionally at line 1125, potentially overwriting weights improved by the final epochs
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 1123-1135

**Description:** After the epoch loop exits (either by break from patience or running to maxEpochs), lines 1123-1135 apply `if (useEarlyStopping && bestLoss < double.MaxValue)` and restore the best observed weights. This is correct when early stopping triggered via the patience counter (the weights at the patience-triggered epoch are worse than the best). But when training runs the full maxEpochs without triggering patience, the best-observed checkpoint may be from epoch 2 while epoch 50 weights might be better (the improvement was below EarlyStoppingMinDelta threshold). In this case, the unconditional restore at 1125 reverts to worse weights. The early-stopping patience branch inside the loop (lines 1102-1113) already restores and breaks, so the post-loop restore at 1125 is redundant for that case and potentially harmful for the maxEpochs case. LearnedScoringStrategy has the same logic (lines 728-733) but only restores when patience fires; the neural strategy adds a post-loop restore that the comment at line 1119 claims is needed for the maxEpochs case.

**Impact:** When training runs to maxEpochs, the model may revert to an earlier checkpoint even though later epochs produced equal or slightly better loss (within EarlyStoppingMinDelta noise tolerance). Net effect: the model uses slightly sub-optimal weights.

**Suggested Fix:** Only restore best weights when training was truncated by early stopping (i.e., the loop broke before maxEpochs). Track whether early-stop break occurred with a flag.

#### 37. ExtractOriginalName fails for items trashed in the same second with a collision suffix
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 534-548

**Description:** `ExtractOriginalName` checks `trashItemName[TimestampFormat.Length] == '_'` and then calls `TryParseTrashTimestamp`. However, after a collision is resolved, `ResolveCollision` appends a numeric suffix like `_2` to produce names like `20240101-120000_MovieName_2`. `ExtractOriginalName` will correctly extract `MovieName_2` (including the collision suffix) rather than `MovieName`. This means `GetTrashContents` returns `Name = "MovieName_2"` instead of `"MovieName"` for collided items. This is a cosmetic issue, but the `_2` suffix leaks into the display name shown to users.

**Impact:** Users see collision suffixes (`_2`, `_3`, etc.) in trash contents display rather than the original folder name.

**Suggested Fix:** After stripping the timestamp prefix, also strip a trailing `_\d+` pattern (e.g. `_2` through `_999`) from the extracted name, or strip only a single such suffix.

#### 38. ValidateGrowthBaseline stops checking all entries after first key-length or script-injection violation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupValidator.cs | 430-445

**Description:** In `ValidateGrowthBaseline` (line 400), the loop over `baseline.Directories` breaks as soon as the first key exceeding 1000 chars (line 435) or the first script injection (line 443) is found. This means a baseline with a malicious key at index 5000 and a legitimate violation at index 0 produces only one error. More importantly, the negative size/count warnings also early-exit on their first match. While intentional for warnings (to avoid log spam), for the key-length ERROR the break means subsequent entries with keys > 1000 chars are silently accepted. If the validator is used to gate whether a backup is safe to import, an attacker could place a benign key first followed by a malicious long key and only the benign check fires.

**Impact:** Validator under-reports errors in the baseline when the first entry passes but later entries fail key-length or injection checks. A crafted backup could pass validation and cause downstream issues when the long key is written to disk.

**Suggested Fix:** Remove the `break` after the key-length error (line 435) and after the script-injection error (line 443), or change them to `continue` so all entries are checked. Keep the early-exit only for warnings.

#### 39. AddEntry reads configuration outside the lock, creating a TOCTOU race
**File:** Jellyfin.Plugin.JellyfinHelper/Services/PluginLog/PluginLogService.cs | 269-296

**Description:** In `AddEntry` (line 269), `GetConfiguredMinLevel()` and `GetLevelIndex()` are called at lines 272-273 outside of the `lock (_lock)` block (which begins at line 287). Another thread could simultaneously call `Clear()` or add entries. More importantly, `GetConfiguredMinLevel()` calls `_configService.GetConfiguration()` which touches the `Plugin.Instance` singleton without synchronization. This is a TOCTOU (time-of-check-time-of-use) issue: the level check is done outside the lock and by the time the lock is acquired, the configured level may have changed. While the race window is tiny and the worst case is a log entry being included or excluded incorrectly, the pattern is inconsistent with the stated thread-safety goal.

**Impact:** A log entry could be written to the buffer even after its level has been raised (or suppressed after it should have been included) due to the config read happening outside the lock. Low risk in practice but violates the documented thread-safety contract.

**Suggested Fix:** Move the level check (`GetConfiguredMinLevel()` / `GetLevelIndex()` comparison) inside the `lock (_lock)` block, or document that the level filter is intentionally approximate.

#### 40. WriteAllText catch-all rethrows on OperationCanceledException from non-cancellable sync overload
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Common/AtomicFile.cs | 96-110

**Description:** The synchronous `WriteAllText` does not accept a `CancellationToken`. However, `File.WriteAllText` can in principle throw `OperationCanceledException` (on .NET 7+ if the underlying stream is cancelled by a linked token). The catch-all `catch` at line 103 calls `TryDeleteQuietly(tempPath)` then `throw`. This is correct behaviour. However, `Thread.Sleep` at line 101 is not cancellable â€” if a user-visible cancel arrives during the sync sleep, the thread is blocked for up to 80ms with no way out. This is a minor UX issue but documented as acceptable.

**Impact:** Low. The sync path is documented for background task use where thread blocking is acceptable.

**Suggested Fix:** No urgent fix required; the limitation is documented. Consider adding an overload accepting `CancellationToken` for future use.

#### 41. ValidateStringField checks value.Length (char count) against maxLength for all fields, but MaxApiKeyLength/MaxUrlLength are meant to be byte-safe
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupValidator.cs | 243-244

**Description:** `ValidateStringField` at line 243 checks `value.Length > maxLength`. `value.Length` is the UTF-16 char count, not byte count. For `MaxApiKeyLength = 200`, a string of 200 four-byte emoji characters passes validation (200 chars) but would occupy 800 bytes when serialized to JSON, far exceeding the intended limit. For practical API keys and URLs this is unlikely to matter (they are ASCII), but the limit semantics are inconsistent with `BackupSanitizer.TruncateString` which also truncates by char count.

**Impact:** Crafted multi-byte Unicode strings in API key or URL fields can bypass the intended size constraints. In practice, API keys and URLs are ASCII so the risk is negligible.

**Suggested Fix:** Either document that all size limits are in chars (consistent with .NET string length), or use `Encoding.UTF8.GetByteCount(value) > maxLength` for a byte-accurate check.

#### 42. HashSet comparer equality check uses Equals which may not work for all StringComparer types
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Arr/ArrIntegrationService.cs | 242-244

**Description:** At line 242, the code checks `jellyfinFolderNames.Comparer.Equals(StringComparer.OrdinalIgnoreCase)` to decide whether to create a new `HashSet`. `IEqualityComparer.Equals` here is called on the comparer object itself â€” it compares the comparer for reference equality unless overridden. `StringComparer.OrdinalIgnoreCase` is a singleton, so this comparison works for the `OrdinalIgnoreCase` singleton. However, if the caller passes a custom `OrdinalIgnoreCaseComparer` wrapper or any non-singleton OrdinalIgnoreCase comparer, the check returns false and a redundant new HashSet is created. This is a minor inefficiency, not a bug per se, but the intent (avoid allocating a copy when the HashSet already has the right comparer) can fail silently.

**Impact:** Unnecessary HashSet allocation when the caller passes a functionally equivalent but non-singleton OrdinalIgnoreCase comparer. No correctness impact.

**Suggested Fix:** Compare using `StringComparer.OrdinalIgnoreCase.Equals(jellyfinFolderNames.Comparer, StringComparer.OrdinalIgnoreCase)` or simply always create the new HashSet unconditionally since this is a cold path.

#### 43. Plugin.Instance?.Version.ToString() throws NullReferenceException when Version is null
**File:** Jellyfin.Plugin.JellyfinHelper/Services/FileTransformation/TransformationPatches.cs | 29

**Description:** At line 29, `Plugin.Instance?.Version.ToString() ?? "unknown"` uses a null-conditional on `Plugin.Instance` but not on `.Version`. If `Plugin.Instance` is non-null but `Plugin.Instance.Version` is null (theoretically possible), `.ToString()` throws a `NullReferenceException`. The correct form is `Plugin.Instance?.Version?.ToString() ?? "unknown"`.

**Impact:** If `Plugin.Instance.Version` is null (e.g. during startup or in a test environment), the index.html transformation throws an unhandled NullReferenceException, failing silently or crashing the transformation pipeline.

**Suggested Fix:** Change to `Plugin.Instance?.Version?.ToString() ?? "unknown"`.

#### 44. GetTrashSummary uses LINQ Sum over FileInfo for file sizes, allocating a FileInfo per file
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 276-282

**Description:** At line 282, `files.Sum(f => new FileInfo(f).Length)` creates a `FileInfo` object for every file in the trash to obtain its length. `Directory.GetFiles` returns strings, so this allocates `n` FileInfo objects. For a large trash folder this is memory-intensive. Additionally, `Directory.GetFiles` at line 280 materializes all file paths into a string array first, then the LINQ query iterates again. There are two full passes over the file list.

**Impact:** For a trash folder with thousands of files, this allocates one FileInfo per file plus the full path string array. Performance degrades linearly with trash size during the summary endpoint.

**Suggested Fix:** Use `new DirectoryInfo(trashBasePath).EnumerateFiles()` to enumerate lazily and access `.Length` directly on the `FileInfo` yielded by enumeration, avoiding the extra allocation per file.

#### 45. PathComparison property is computed on every call via OperatingSystem checks
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 60-62

**Description:** `PathComparison` is an instance-computed property (line 59) that calls `OperatingSystem.IsWindows()` and `OperatingSystem.IsMacOS()` on every access. While these calls are fast (likely inlined by the JIT), the property is called repeatedly in `MoveToTrash`, `MoveFileToTrash`, and `RelocateTrashContents`. The pattern throughout the file also uses `OperatingSystem.IsWindows()` inline in static methods. There is no caching of the OS check result.

**Impact:** Negligible performance impact, but inconsistent with the `MaxPathLimit` static field which correctly caches the OS-dependent value at initialization. A future developer may add expensive work in this property path by mistake.

**Suggested Fix:** Cache `PathComparison` as a `static readonly` field, similar to how `MaxPathLimit` is initialized, to be consistent with the existing pattern.

#### 46. StripWatchedItemsForResponse omits LanguageProfile, SubtitleLanguageProfile, and PeopleProfile fields
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/ReasonResolver.cs | 255-275

**Description:** StripWatchedItemsForResponse copies only a subset of UserWatchProfile fields: UserId, UserName, WatchedMovieCount, WatchedEpisodeCount, WatchedSeriesCount, TotalWatchTimeTicks, LastActivityDate, GenreDistribution, FavoriteCount, FavoriteSeriesIds, AverageCommunityRating, MaxParentalRating. It does not copy LanguageProfile, SubtitleLanguageProfile, or PeopleProfile. The stripped profile is attached to the returned RecommendationResult and may be passed back to callers that read those fields.

**Impact:** If any code path reads the profile attached to a RecommendationResult (e.g. training data builders that accept previousResults) and accesses LanguageProfile or PeopleProfile, it will receive empty defaults rather than the user's real data. This is a silent train/serve skew risk if training uses the profile embedded in results.

**Suggested Fix:** Copy LanguageProfile, SubtitleLanguageProfile, and PeopleProfile in StripWatchedItemsForResponse, or document explicitly that the attached profile must not be used for feature computation.

#### 47. GenreDistribution merge silently overwrites genres already in vector when TryAdd logic is inverted
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/PreferenceBuilder.cs | 230-243

**Description:** The merge loop at line 235 checks `vector.ContainsKey(genre)` and uses `continue` to skip genres already in the watch-derived vector â€” intentionally only merging distribution data for genres NOT covered by watch history. However, since max-normalization runs AFTER the merge, a genre that has weight 0 in the vector (normalized-to-zero after a prior run, or written as 0 by a zero-weight assignment elsewhere) would be treated as ContainsKey=true and skipped, even though the effective signal is absent. The count-based merge value would produce a better signal.

**Impact:** Users whose watch history contains a genre that later gets normalized to 0 (edge case) won't get the GenreDistribution fallback for that genre, leading to a weakly supported genre being absent from their preference vector when it should have a base weight.

**Suggested Fix:** Change the continue condition to `vector.TryGetValue(genre, out var existing) && existing > 0` so that zero-weight entries are treated the same as missing entries and the distribution can fill them in.

#### 48. NormalizeCriticRating returns 0.5 neutral for criticRating == 0 (a valid zero Tomatometer score)
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/ContentScoring.cs | 64-73

**Description:** The guard at line 66-67 treats criticRating.Value < 0 as invalid and returns 0.5. However, a value of exactly 0.0 passes this guard (0 is not < 0) and returns Math.Clamp(0.0/100.0, 0, 1) = 0.0 correctly. But the guard also checks !float.IsFinite which catches NaN and Infinity. The real issue is that 0 IS a valid score (0% on Rotten Tomatoes) and correctly returns 0.0 â€” but ComputeCombinedCriticScore's guard `criticRating.Value >= 0` at line 92 means a 0% Tomatometer IS included. This is correct but the dual-path (NormalizeCriticRating vs ComputeCombinedCriticScore) is inconsistent: NormalizeCriticRating would return 0.0 for a 0% score, while ComputeCombinedCriticScore line 109 also returns 0.0/100 = 0.0. No actual bug, but the inconsistent guard (< 0 in one, >= 0 in the other) is a maintenance hazard and the comment says 'zero' is a neutral fallback when it is not.

**Impact:** Low. No functional bug currently, but future maintainers may align guards incorrectly and either exclude legitimate 0% scores or include negative values.

**Suggested Fix:** Unify the guard to `criticRating.Value < 0` (exclude only negative) in both methods, or add a comment clarifying that 0% is a valid score that maps to 0.0.

#### 49. Cold-start check uses WatchedItems.Count == 0 but warm path filter uses HasMeaningfulInteraction
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 160

**Description:** The cold-start branch (line 160) is triggered when userProfile.WatchedItems.Count == 0. However, WatchedItems can contain IsFavorite=true rows with no playback (Played=false, PlayCount=0, PlaybackPositionTicks=0). A user with only favorited items (no actual watches) has WatchedItems.Count > 0 and falls into the warm path â€” but HasMeaningfulInteraction() returns true for IsFavorite=true items, so watchedIds will not be empty. Meanwhile, genrePreferences will be built from favorites only. The warm path will then exclude those items as 'watched'. This is arguably correct but creates an inconsistency: the cold-start threshold is Count==0 while the effective 'has preferences' threshold is whether any IsFavorite or played items exist. A user who favorited one item but never watched anything gets warm-path treatment with a very sparse preference vector.

**Impact:** Users who have only favorited items (no actual playback) receive warm-path recommendations with sparse preference vectors rather than the more robust cold-start popular-items approach. Recommendation quality for this user segment is degraded.

**Suggested Fix:** Consider using `userProfile.WatchedItems.Count(w => w.HasMeaningfulInteraction()) == 0` as the cold-start gate, or introduce a separate minimum-engagement threshold constant.

#### 50. LINQ Count() on HashSet in hot collaborative loop is O(N) instead of O(1)
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/CollaborativeFilter.cs | 244

**Description:** Line 244: `var overlap = smaller.Count(larger.Contains);` uses LINQ Count() with a predicate on a HashSet. This is O(|smaller|) which is correct complexity, but LINQ Count() creates an enumerator and does not benefit from HashSet's O(1) Contains â€” this part is fine. However the real issue is this is called in the innermost O(UÂ²) loop of BuildCollaborativeMap, and LINQ Count() allocates an enumerator per call. For U=100 users this means ~10,000 enumerator allocations per user, multiplied by all users in a batch.

**Impact:** Excess GC pressure in the collaborative filtering hot path during batch generation. On servers with many users this can cause GC pauses during scheduled recommendation runs.

**Suggested Fix:** Replace with a manual foreach loop: `var overlap = 0; foreach (var id in smaller) { if (larger.Contains(id)) overlap++; }` â€” this avoids the enumerator allocation and is idiomatic for hot paths.

#### 51. BuildCommunityPopularityForColdStart two-user gate is inconsistent with BuildCommunityPopularityMap
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 1861-1870

**Description:** BuildCommunityPopularityForColdStart (line 1864) returns null when allProfiles.Count < 2 â€” this checks total profile count including profiles with zero watch history. BuildCommunityPopularityMap (line 1901-1910) then re-checks whether at least two users have non-empty watch sets. When exactly two profiles exist but one has no watch history, BuildCommunityPopularityForColdStart passes the count check and calls BuildCommunityPopularityMap, which then returns null â€” so the double gate is consistent. However, when allProfiles.Count >= 2 the early-exit in BuildCommunityPopularityForColdStart is skipped even if all users except one have empty histories, incurring the full PrecomputeUserWatchSets O(UÃ—M) cost before BuildCommunityPopularityMap's gate fires. The outer guard is weaker than the inner one.

**Impact:** Unnecessary O(UÃ—M) scan for deployments with many empty-history users and only 1-2 active users. Minor performance waste, not a correctness issue.

**Suggested Fix:** Change the outer guard in BuildCommunityPopularityForColdStart to count profiles with non-empty WatchedItems before calling PrecomputeUserWatchSets, matching the inner gate's semantics.

#### 52. ComputeGenreSimilarity computes userNorm over entire genrePreferences vector including zero-weight entries
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/SimilarityComputer.cs | 270-279

**Description:** The userNorm calculation (lines 272-279) iterates ALL entries in genrePreferences and sums weight*weight including entries with weight==0 (from normalized-to-zero entries or GenreDistribution-sourced entries). Zero entries contribute 0 to userNormSq so they do not affect the norm value â€” the bug is actually absent here. However, the denominator also includes ALL genres in genrePreferences regardless of whether the candidate has them, which is correct for cosine similarity. The real concern is that after ExpandGenreProximity inserts new genres (potentially many proximity-derived genres), userNormSq can grow significantly, diluting scores for all candidates. With 50 base genres and 50 proximity-inserted genres, userNorm grows ~âˆš2 larger, uniformly scaling all genre similarity scores down by ~29%.

**Impact:** Genre similarity scores are systematically lower after ExpandGenreProximity inserts many secondary genres because they inflate userNorm without the candidate having matching genres. The effect is proportional to the number of proximity-inserted genres. This is a scoring-level train/serve skew if the training path uses a different genrePreferences vector (e.g. without ExpandGenreProximity).

**Suggested Fix:** Document this behaviour explicitly. Alternatively, compute userNorm only over the genres actually present in the candidate (dot product / candidate_norm * user_candidate_norm), but this changes the metric semantics. At minimum, ensure training calls ExpandGenreProximity under the same conditions as inference.

#### 53. ComputeDayOfWeekAffinity and ComputeHourOfDayAffinity use UTC timestamps for day/hour bucketing, creating systematic bias for non-UTC users
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/TemporalFeatures.cs | 41-78

**Description:** Both methods use DateTime.UtcNow and w.LastPlayedDate (stored in UTC by Jellyfin). The code comment acknowledges UTC-only usage. For a user in UTC-8 (PST), 8pm PST is 4am UTC â€” Saturday evening viewing is bucketed as Sunday night in UTC. This means the system learns 'this user watches drama on Sunday night' when they actually watch on Saturday evening, and serves drama recommendations on Tuesday UTC (Monday evening PST) instead of Saturday.

**Impact:** Temporal affinity features are systematically wrong for users not in UTC. Given that most Jellyfin deployments are in non-UTC time zones (US, Europe, Asia-Pacific), this feature produces misleading signals for the majority of users. Training on these signals propagates the timezone skew into the model weights.

**Suggested Fix:** Store the user's IANA timezone identifier in UserWatchProfile (even if just inferred from LastActivityDate patterns) or disable these features entirely. If UTC-only is accepted, document prominently that these features are only meaningful for UTC-resident users.

#### 54. MMR score can be negative; first selected item's mmrScore comparison is against double.MinValue but subsequent items with negative scores may still be incorrectly preferred
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/DiversityReranker.cs | 287

**Description:** The MMR score formula at line 287: `mmrScore = (MmrLambda * relevance) - ((1.0 - MmrLambda) * maxSimilarity)`. With MmrLambda=0.7, a candidate with relevance=0.1 and maxSimilarity=0.9 gets score = 0.07 - 0.27 = -0.20. Negative MMR scores can occur legitimately, but the selection loop will still pick the least-negative item. The first pick always has maxSimilarity=0 so it is always the highest-relevance item â€” correct. The issue is bestMmrScore is initialized to double.MinValue so a set of all-negative MMR scores will still pick items, which means in a homogeneous genre cluster, low-relevance items with negative MMR scores will be selected to fill MMR slots that could otherwise be left for exploration. There is no early-exit when all remaining candidates have MMR score < 0.

**Impact:** In libraries with a single dominant genre, MMR may select low-relevance items just to avoid genre repetition even when exploration slots could serve the same purpose more effectively. Minor quality impact.

**Suggested Fix:** Add a threshold (e.g. break or skip when bestMmrScore < 0 and exploration slots are available) so the exploration pool can absorb genuinely diverse picks rather than forcing MMR to pick poorly-scoring items.

#### 55. ComputeProgressionMultiplier returns 1.0 when playedEps <= 0, even if user has zero completed episodes â€” incorrectly neutral rather than floor
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/PreferenceBuilder.cs | 975-1037

**Description:** Lines 1024-1027: when watchedEpisodesPerSeries.TryGetValue returns false or playedEps <= 0 (the user has the series in their profile but no completed episodes counted), the method returns 1.0 (neutral). But the intent of progression multiplier is to DAMPEN preference for series with low completion â€” 0 completed episodes should return ProgressionFloor (0.3), not 1.0. The 1.0 fallback means a user who has the series in their profile (perhaps via a favorite series row with no episode completions) contributes a full neutral weight instead of a damped one.

**Impact:** Users with series favorited but no episodes watched get a 1.0 multiplier rather than 0.3 for all associated genres and people, slightly over-weighting those signals. This is a minor correctness issue.

**Suggested Fix:** When seriesEpisodeCounts contains the sid (line 1019 succeeds) but watchedEpisodesPerSeries does not (playedEps = 0), return ProgressionFloor rather than 1.0, consistent with the formula's intent.

#### 56. Child account movie candidates from Animation genre are missing StampMediaType call
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1219-1253

**Description:** For child accounts, Family movie items (lines 1225-1226) and Animation movie items (lines 1233-1235) are added without calling StampMediaType. Seerr /discover/movies/* endpoints should return mediaType='movie', but per the comment at line 1656, this stamp is a defensive guard for when the field is missing. Crucially, line 1409 checks string.Equals(item.MediaType, 'tv') to classify output â€” if any movie candidate arrives with null/missing mediaType, it deserialises to the default 'movie' (TmdbDiscoverItem.MediaType = 'movie') which is correct. However, the inconsistency vs TV items (all stamped) creates a maintenance risk.

**Impact:** No immediate bug given the default value, but if Seerr ever omits mediaType on movie responses, items would be mis-classified as 'movie' (which happens to be correct) but the exclusion set lookup uses a lowercase normalized key â€” if the deserialized value is not lowercase the exclusion check at line 1604 would miss items. Default 'movie' is already lowercase so current behaviour is correct.

#### 57. PeopleSimilarity denominator uses Min(preferredPeople.Count, 5) â€” score inflated for users with few preferred people
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/ExternalCandidateFeatureBuilder.cs | 163

**Description:** ComputePeopleSimilarityFromNames computes: overlap / Min(preferredPeople.Count, MinPeopleForFullScore=5). For a user who has exactly 1 preferred person and that person matches, the score is 1/Min(1,5) = 1/1 = 1.0 â€” a perfect PeopleSimilarity score from a single data point. This is disproportionately high and will cause DetermineReason to always choose 'reasonPersonNamed' for such users, potentially overriding a genuinely better GenreSimilarity signal.

**Impact:** Correctness: new users or users with sparse people profiles receive inflated PeopleSimilarity=1.0 from a single match, which skews reason labeling and may also affect ensemble scoring if PeopleSimilarity is weighted heavily.

#### 58. Pagination termination uses stale page arithmetic when pageInfo is missing
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 510-512

**Description:** Line 510: totalPages = pageResult.PageInfo?.Pages ?? 1. If Seerr ever returns PageInfo=null (e.g., on a future API version change), totalPages defaults to 1 and pagination stops after the first page regardless of how many users actually exist (pageResult.Results.Count < take check on line 512 still works as a fallback). Combined with the safety cap at maxPages=20, this is a silent data truncation.

**Impact:** Silent truncation of user list to the first 50 users when PageInfo is null. FetchSeerrUsersInternalAsync returns fetchComplete=true (no error flag set) so callers cache this incomplete result for the full 5-minute TTL, causing all users beyond the first page to be incorrectly treated as 'not linked to Seerr' for up to 5 minutes.

#### 59. ToJellyfinGenres checks MovieGenres then TvGenres â€” genre ID 16 (Animation) maps to 'Animation' from MovieGenres, never checking TvGenres for the same ID
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/TmdbGenreMap.cs | 98-112

**Description:** ToJellyfinGenres checks MovieGenres.TryGetValue first. For genre ID 16 (Animation), which exists in both MovieGenres and TvGenres with the same name 'Animation', the result is correct. However, TV-only genres that share IDs with movie genres would produce wrong names. Specifically, genre 80 'Crime' appears in both maps with the same name, as do 35 'Comedy', 99 'Documentary', 18 'Drama', 10751 'Family', 9648 'Mystery', 37 'Western'. This is currently harmless because the names are identical. But for TV-specific IDs that don't overlap (e.g. 10762 Kids, 10763 News) â€” those only appear in TvGenres so the fallback to TvGenres.TryGetValue is correctly needed. The current code is accidentally correct but fragile.

**Impact:** No current functional bug. If any future genre ID has different names in MovieGenres vs TvGenres, a TV item would get the wrong genre name, affecting GenreSimilarity scoring.

#### 60. Rate-limit delay in finally block runs even after an exception is thrown mid-request
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1486-1491

**Description:** ExecuteDiscoverQueryAsync's finally block (lines 1487-1491) applies the 500ms InterQueryDelay regardless of whether the request succeeded or failed. On error paths (e.g., HTTP 4xx, JsonException) this means each failed query also incurs the full delay. For child accounts which issue 6 discovery queries, a run where all queries fail (e.g., Seerr is down) wastes 3 seconds in delays before returning an empty result. The same issue exists in EnrichTopCandidatesWithCreditsAsync (lines 1793-1798) for up to 20 enrichment calls = 10 seconds of unnecessary delays.

**Impact:** Performance: when Seerr is unavailable, the full rate-limit delay is still applied for each failed request, causing the discovery task to run much longer than necessary during outages.

#### 61. Year-window logic computes minYear as integer cast from avgYear - 15, losing fractional precision silently
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1584-1594

**Description:** Line 1591: minYear = (int)avgYear - 15. The cast truncates the double: for avgYear=2005.9, (int)avgYear=2005, so minYear=1990. For avgYear=2005.1, minYear is also 1990. This is fine. However the comparison at line 1586 uses avgYear >= currentYear - 6 (a double vs int comparison) while avgYear is a double computed from ComputeAverageYear â€” no actual bug, just a minor precision note. The real issue is that if avgYear=0 is returned (no play history) but the user has favorites only (FavoriteCount>=3), the condition at line 1583 (!isChildAccount && avgYear > 0) correctly skips the year window. This is safe.

**Impact:** Negligible. Minor truncation with no practical impact on user experience.

#### 62. CreateClient does not validate that the base URL has a trailing slash effect correctly for all path formats
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1843-1845

**Description:** Line 1862: client.BaseAddress = new Uri(parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/"). This is correct for HttpClient relative URI resolution. However, the URL check on line 1843 uses parsedBaseUrl.Scheme checks only for http/https but does not validate the path is empty or a simple root. If a user configures a Seerr URL with a subpath like https://example.com/seerr/, the base address becomes https://example.com/seerr/ and relative URIs like api/v1/... would resolve to https://example.com/api/v1/... (ignoring the /seerr/ prefix) per standard HttpClient relative URI resolution rules.

**Impact:** Correctness: Seerr installations hosted at a subpath will have all API calls silently routed to the wrong URLs. No error is thrown; queries return 404s which are silently swallowed as empty results.

#### 63. CanSelectQualityProfile double-calls HasPermission(Admin) redundantly
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrPermissionExtensions.cs | 90-92

**Description:** CanSelectQualityProfile (lines 90-92) calls user.HasPermission(SeerrPermissions.Admin) directly, and then user.HasPermission(ManageRequests) â€” but HasPermission itself already checks for Admin internally (line 31-33 of the same file). So user.HasPermission(ManageRequests) returns true for admins anyway. The first explicit Admin check is redundant. This is a minor code clarity issue rather than a functional bug.

**Impact:** No functional impact. Redundant call adds negligible overhead.

### MEDIUM / PERFORMANCE

#### 1. Encoding.UTF8.GetByteCount called redundantly â€” full string re-scanned after already being encoded
**File:** Jellyfin.Plugin.JellyfinHelper/Api/BackupController.cs | 189

**Description:** At line 61, Encoding.UTF8.GetBytes(json) produces bytes with length bytes.LongLength. At line 189, after decoding the same bytes back to json via StreamReader, Encoding.UTF8.GetByteCount(json) is called again to get jsonLength. Since json is the same string, this is a redundant O(n) scan. Additionally, the actual byte count is already known from totalBytes (line 145) accumulated during the streaming read.

**Impact:** Minor: unnecessary CPU work proportional to backup size. On a large backup at the warning threshold this is an O(MB) string scan for no reason.

**Suggested Fix:** Use the already-accumulated totalBytes variable for the size check at line 189, or capture bytes.LongLength before the streaming path. Either way, remove the GetByteCount call.

#### 2. IsRecommendationsEnabled() calls GetConfiguration() on every request â€” no caching
**File:** Jellyfin.Plugin.JellyfinHelper/Api/RecommendationController.cs | 290

**Description:** IsRecommendationsEnabled() at line 290 calls _configService.GetConfiguration() to read RecommendationsTaskMode. All four public action methods call this check, and GetAllRecommendations() also calls _configService.GetConfiguration() immediately after at line 77 â€” resulting in two GetConfiguration() calls per GET /Recommendations request. If GetConfiguration() deserializes from disk or acquires a lock, this is wasteful.

**Impact:** Double configuration reads per request on the hot path. If GetConfiguration() is cheap (returns a cached in-memory object), the impact is low but the code is still redundant.

**Suggested Fix:** In methods that call IsRecommendationsEnabled() and then GetConfiguration(), read config once at the top of the method and pass it to a refactored IsRecommendationsEnabled(config) overload. Alternatively inline the check using the already-retrieved config object.

#### 3. Phase 2 builds `seriesWithOrgEpisodes` and `seriesEpisodeLookupOrganic` as two separate full-pass iterations over `userProfile.WatchedItems`
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 570

**Description:** For each user in Phase 2, `seriesEpisodeLookupOrganic` is built in one loop (lines 550-565) and `seriesWithOrgEpisodes` is built in a second loop (lines 572-580) â€” both iterate the entire `WatchedItems` collection. These could be merged into a single pass, halving the per-user scan cost. For users with large watch histories (thousands of items), this is a measurable hot-path inefficiency repeated per user per training run.

**Impact:** Performance: each user in Phase 2 pays two O(N_watchedItems) scans instead of one. With many users and large libraries this compounds across the training run.

**Suggested Fix:** Merge the two loops into one: while building `seriesEpisodeLookupOrganic`, simultaneously populate `seriesWithOrgEpisodes` using the same eligibility conditions.

#### 4. Phase 1 builds `watchedItemLookup` per-result-set (inside the outer `foreach (var prevResult in previousResults)` loop), not per-user
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 216

**Description:** At lines 216-219, `watchedItemLookup` is constructed fresh inside the Phase 1 `foreach (var prevResult in previousResults)` loop. Since the same `userProfile.WatchedItems` is used (accessed via `profileById`), and multiple `prevResult` entries can share the same `UserId`, this dictionary is rebuilt on every result for the same user. A user with 5 separate recommendation result entries has their `WatchedItemInfo` dictionary rebuilt 5 times.

**Impact:** Performance: redundant allocations and scans of `WatchedItems` for users with multiple previous recommendation result batches. Scales with (users Ã— result-sets-per-user).

**Suggested Fix:** Move `watchedItemLookup` construction into the `perUserCache` pre-computation block (lines 183-194) and store it in the tuple alongside `GenrePreferences`, `CoOccurrence`, etc.

#### 5. Phase 1 builds `seriesEpisodeLookup` per-result-set inside the outer loop â€” same user rebuilds it for every previous-result entry
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 222

**Description:** Identical pattern to `watchedItemLookup` above. `seriesEpisodeLookup` is built fresh inside `foreach (var prevResult in previousResults)` at lines 222-238. A user with N previous result batches rebuilds this dictionary N times.

**Impact:** Same as above â€” redundant per-user work multiplied by the number of result batches.

**Suggested Fix:** Move into `perUserCache`.

#### 6. Phase 1 builds `watchedGenreSets`, `watchedPeopleSets`, `watchedStudioSets` per-result-set, not per-user
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 242

**Description:** Lines 242-267 build three parallel watched-item set lists inside the outer `foreach (var prevResult in previousResults)` loop. For a user with multiple recommendation result batches, these three lists are rebuilt on every iteration even though the source data (`userProfile.WatchedItems`) does not change between iterations.

**Impact:** Three O(N_watchedItems) passes with allocation of N HashSet<string> objects per pass, multiplied by result-set count per user.

**Suggested Fix:** Move into `perUserCache`.

#### 7. Full library scan with no library exclusion filter applied â€” excluded libraries are scanned
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Activity/UserActivityInsightsService.cs | 60

**Description:** BuildActivityReport() calls GetItemList with MediaType.Video and IsFolder=false but does not apply the ExcludedLibraries config filter. All other cleanup tasks respect the exclusion list. User activity data is therefore collected for libraries the operator explicitly excluded, potentially including restricted-access libraries.

**Impact:** Activity data from excluded libraries (e.g. a shared family library that admins want to keep private) appears in the activity report and recommendation engine input, violating the intended exclusion semantics.

**Suggested Fix:** Apply the same ExcludedLibraries filter used by cleanup tasks (via ICleanupConfigHelper or a direct config check) to the GetItemList query.

#### 8. GetUsers().ToList() materializes all users into memory with no lazy enumeration
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Activity/UserActivityInsightsService.cs | 53

**Description:** The outer loop iterates over all users once to build the lookup dictionary and again in the nested item loop. The ToList() call on line 53 is necessary (used for Count logging and passed to BuildUserDataLookup), but the inner per-item-per-user loop at line 89 also calls users (the local list), so the full NÃ—M nested loop runs in memory. For installations with many users and large libraries this is O(users Ã— items) memory during execution and dominates the heap.

**Impact:** On large installations (100+ users, 50k+ items) the BuildActivityReport call holds several hundred MB in memory for the duration of the scan, risking GC pressure and potential OOM on memory-constrained servers.

**Suggested Fix:** The batch pre-fetch already addresses the DB side. Consider streaming results or chunking the item list. Document the O(users Ã— items) memory bound explicitly in the class summary (a partial note already exists in BuildUserDataLookup).

#### 9. ComputeMseLoss in Neural allocates 8 hidden-layer buffers on every validation loss computation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 1662-1701

**Description:** ComputeMseLoss (line 1662) allocates h1Pre, h1Act, h2Pre, h2Act, h3Pre, h3Act, h4Pre, h4Act (8 arrays totaling Hidden1Size+Hidden2Size+Hidden3Size+Hidden4Size = 62+96+48+24 = 230 doubles = 1840 bytes) on every call. This method is called once per epoch when early stopping is active (line 1081). With 50 epochs Ã— N training sessions, this creates significant GC pressure. The training loop already has pre-allocated epoch-level buffers (lines 781-788) but they are not passed to ComputeMseLoss.

**Impact:** Unnecessary heap allocations during training. With 50 epochs, allocates 8 Ã— 50 = 400 arrays per training run, each ~230 doubles. For a long-running server with frequent training, this can cause noticeable GC pauses.

**Suggested Fix:** Extract hidden-layer buffers as parameters to ComputeMseLoss and pass the pre-allocated buffers from the training loop.

#### 10. All count properties re-enumerate FileResults on every access
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Link/LinkRepairResult.cs | 19-39

**Description:** The computed properties `ValidCount`, `RepairedCount`, `BrokenCount`, `AmbiguousCount`, and `InvalidContentCount` each call `FileResults.Count(...)` independently. In the `RepairLinks` summary log at `LinkRepairService.cs` line 84, all five properties are accessed in a single interpolated string, causing five separate full enumerations of `FileResults`. For large libraries with thousands of link files, this is 5x O(n) work.

**Impact:** For libraries with e.g. 100,000 link files, the summary log triggers 500,000 operations. Not catastrophic but unnecessary for a result type whose counts are fixed by the time they are read.

**Suggested Fix:** Compute all counts once in a single pass. Either materialize counts as fields set during `RepairLinks`, or add a `GetSummary()` method that returns all counts from a single `foreach`.

#### 11. ExpandGenreProximity calls .Distinct() and .ToArray() per watched item inside hot loop
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/PreferenceBuilder.cs | 319-322

**Description:** Inside ExpandGenreProximity the loop at line 305 iterates over all watched items. For each item it calls `item.Genres.Where(...).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()` at lines 319-322. This allocates a new array per item plus a HashSet inside Distinct for deduplication. For a user with 500 watched items and 5 genres each, this is 500 array allocations and 500 HashSet allocations just for the co-occurrence matrix build phase.

**Impact:** Elevated GC pressure during preference vector construction, which runs once per user per recommendation request. In batch mode with 50 users this is 25,000+ allocations in this loop alone.

**Suggested Fix:** Reuse a single pre-allocated HashSet<string> to deduplicate genres per item inside the loop, clearing and re-filling it instead of constructing a new one each time.

#### 12. SaveInternal materialises a new list every time the per-user entry count exceeds MaxEntriesPerUser
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryFeedbackStore.cs | 457-476

**Description:** Line 471-474: OrderByDescending(...).Take(200).ToList() creates a new sorted list per user on every save that has an over-limit user. For a system with many users, each of which has close to 200 entries (e.g., active daily users), every single save triggers a sort+materialize per user. SaveInternal is called on every RecordDismissed and RecordRequested (i.e., on every user interaction).

**Impact:** Performance: on a multi-user system with active feedback, each UI interaction (dismiss/request) triggers O(N log N) sorting per over-limit user. Not catastrophic at 200 entries, but it scales with user count and interaction frequency.

### MEDIUM / INCOMPLETE

#### 1. SeriesProgressionBoost is hardcoded 0.0 but occupies a feature slot â€” dead feature signal with no mechanism to ever activate it
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 1108

**Description:** Line 1108: `const double seriesProgressionBoost = 0.0;` with comment 'hardcoded 0.0 at inference'. The feature slot is kept in CandidateFeatures so the network layout stays stable, but the value is always 0. If the training pipeline also always writes 0 for this feature, the network weight for this slot will converge to 0 (useless). If training ever writes non-zero values, there is a permanent train/serve skew.

**Impact:** Wasted feature slot in the ML model. If training writes non-zero SeriesProgressionBoost values, the model learns a weight for a feature that inference never uses, degrading model accuracy in proportion to the weight assigned to this signal.

**Suggested Fix:** Either remove the feature slot entirely (accepting the network layout change) or document explicitly in the training pipeline that this feature must also always be 0. Add an assertion in training that verifies it.

### MEDIUM / TEST-GAP

#### 1. InsufficientOverlap test hardcodes MinCollaborativeOverlap threshold assumption without referencing the constant
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/CollaborativeFilterTests.cs | 271-293

**Description:** BuildCollaborativeMap_InsufficientOverlap_ReturnsEmpty uses a user with 1 shared item and an other with 2 total items. This passes because the Jaccard overlap (1/2) is below whatever MinCollaborativeOverlap constant the implementation uses. However the test does not reference the actual constant name â€” if the threshold is ever lowered below the Jaccard of this construction, the test silently flips from testing 'insufficient overlap returns empty' to testing 'overlap produces something', returning a non-empty map that causes a spurious failure rather than catching a regression. Additionally, if MinCollaborativeOverlap is ever raised, this test would still pass but no longer exercise the boundary.

**Impact:** Brittle: test correctness is coupled to an implementation constant that is never referenced. Boundary behavior is untested.

**Suggested Fix:** Use CollaborativeFilter.MinCollaborativeOverlap (or whatever the constant is named) to construct the test case, or add an explicit comment with the current threshold value and a note to update the test if it changes.

#### 2. VersionMismatch_DiscardsWeights test compares stale score against a fresh strategy but both share the same ThreadStatic buffers, creating a potential ordering dependency
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/NeuralScoringStrategyTests.cs | 667-696

**Description:** The test constructs strategy (loaded from old-version file), calls strategy.Score(features), then constructs expectedFreshScore = new NeuralScoringStrategy(null).Score(features). Both use the same [ThreadStatic] scratch buffers (_tlsH1Pre etc.). Since Score() lazily initialises ThreadStatic buffers, the second call reuses the same buffers from the first call. While this is safe (buffers are fully overwritten each call), the test compares strategy.Score and expectedFreshScore on the same features. Both should produce identical deterministic output since they are freshly initialised with the same seed â€” the test is correct but the assert at precision=10 could theoretically differ if the ThreadStatic buffers are shared in a way that affects the result (they are not, but this is a latent fragility).

**Impact:** Low risk of actual failure. However the test is asserting that a version-mismatched file produces the same score as a fresh strategy, which is a correct invariant. Precision 10 (1e-10) may be unnecessarily tight given floating-point non-associativity.

**Suggested Fix:** Acceptable as-is, but consider using precision 8 to match the tolerance used elsewhere in the file.

#### 3. ScoreWithOffset_ZeroOffset_MatchesScore uses 1e-11 as 'zero offset' but the test does not verify that non-zero offsets actually change the score
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/EnsembleScoringStrategyAdvancedTests.cs | 105-115

**Description:** The test asserts that Score() == ScoreWithOffset(features, 0.0) and == ScoreWithOffset(features, 1e-11) and == ScoreWithOffset(features, -1e-11). While valid, there is no assertion in this test that ScoreWithOffset(features, 0.5) differs from Score(features). Without such an assertion, a trivial implementation of ScoreWithOffset that always ignores the offset parameter and delegates to Score() would pass this test. The intent is to test that zero offset is identity â€” but the identity property alone does not verify the method is wired up at all.

**Impact:** A broken ScoreWithOffset that ignores its offset parameter would pass this test entirely. The massive-offset tests (lines 118-135) partially close this gap by asserting the result is in [0,1], but they do not assert it differs from the zero-offset baseline.

**Suggested Fix:** Add Assert.NotEqual(baseline, ensemble.ScoreWithOffset(features, 0.5), 8) or verify that a positive offset shifts the score in the expected direction (higher alpha shift â†’ higher score for a high-quality item).

#### 4. SeerrNotConfigured and CrlfApiKey tests mutate Plugin.Instance.Configuration singleton â€” no cleanup if Plugin.Instance is null (configuration block is inside a null-guard)
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Seerr/Discovery/SeerrDiscoveryServiceTests.cs | 58-85

**Description:** SubmitRequestAsync_SeerrNotConfigured_ReturnsFalse and SubmitRequestAsync_ApiKeyWithCrlf_ReturnsFalse both check if Plugin.Instance?.Configuration != null before mutating. If Plugin.Instance is null (common in isolated test environments without the full host initialised), the if-block is skipped entirely. The test then calls SubmitRequestAsync and asserts false + 'not configured', which may still pass if the implementation also checks Plugin.Instance for null and returns 'not configured'. But the CRLF test expects failure due to 'CRLF guard fires inside CreateClient' â€” if Plugin.Instance is null, the CRLF never gets injected and the failure reason would be 'not configured' not 'CRLF'. The assert at line 105 only checks Assert.False(success) and Assert.False(string.IsNullOrEmpty(message)), which would pass regardless of which guard triggered.

**Impact:** If Plugin.Instance is null in CI, both tests pass vacuously (both assertions hold for any failure reason), but the CRLF header-injection guard is never actually exercised. The test provides false assurance that the CRLF check works.

**Suggested Fix:** Add a more specific message assertion for the CRLF test (e.g. Assert.Contains('header', message, OrdinalIgnoreCase)) to distinguish CRLF rejection from 'not configured'. Or use [Collection("ConfigOverride")] fixture to guarantee Plugin.Instance is initialized.

#### 5. TimelineWithTooManyPoints test asserts IsValid=true but does not verify the data-points list is actually trimmed by sanitizer/validator
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Backup/BackupServiceTests.cs | 430-445

**Description:** Validate_TimelineWithTooManyPoints_ReturnsWarning asserts result.IsValid is true and warnings contain 'trimmed' or 'data points'. However it only calls BackupValidator.Validate() â€” not BackupSanitizer.Sanitize(). The warning signals the problem was detected, but the actual trimming is the sanitizer's job. The test does not verify the timeline is trimmed to MaxTimelineDataPoints after sanitization. A regression where Sanitize() stops trimming but Validate() still warns would pass this test undetected.

**Impact:** Sanitization of oversized timelines is not end-to-end tested. If Sanitize() fails to trim, oversized timelines would be accepted into configuration.

**Suggested Fix:** Add a companion test that calls BackupSanitizer.Sanitize() on an oversized timeline backup and asserts DataPoints.Count <= BackupValidator.MaxTimelineDataPoints.

#### 6. ftp:// URL allowed in .strm files but ProcessLinkFile_Strm_StreamingUrlScheme_TreatedAsValid accepts it â€” this is a potential security gap that is tested as CORRECT behavior
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Link/LinkRepairSecurityTests.cs | 127-141

**Description:** The Theory at line 127 includes 'ftp://attacker.com/data' and asserts LinkFileStatus.Valid. FTP is an unencrypted plaintext protocol and its presence in a .strm file could redirect media playback to an attacker-controlled server over an insecure channel. Whether this is intentional behavior is a design question, but the test explicitly validates it as 'Valid' without any comment explaining why ftp:// is considered a legitimate streaming URL. The test description says 'URL schemes ... Streaming URLs allowed' but ftp is not a streaming protocol in the Jellyfin sense.

**Impact:** If ftp:// URLs in .strm files represent a security concern (unencrypted media streaming, SSRF via FTP), this test is asserting the wrong expected outcome. At minimum, an explanatory comment is needed.

**Suggested Fix:** Add a comment explaining why ftp:// is treated as valid (e.g., legacy IPTV streams, user choice). If ftp:// should be rejected, change the expected status to Broken and add a separate test case.

#### 7. ComputeProgressionMultiplier_AbandonedSeries test asserts Fringe weight is in [0.03, 0.07] but this range is derived from hard-coded constants not referenced in the test
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/PreferenceBuilderTests.cs | 856-918

**Description:** The test hardcodes the expected range 0.03â€“0.07 with a detailed comment explaining the arithmetic. However ProgressionFloor (0.3), ProgressionCeiling (1.5), and ProgressionSpan (1.2) are private constants in PreferenceBuilder. If any of these are tuned, the test range becomes stale without a compiler error. The comment explains how 0.048 is computed but does not reference the source constants by name â€” a future reader or CI bot cannot verify the derivation without re-reading the source.

**Impact:** Range becomes stale silently if progression constants change. Test may pass with wrong behavior or fail with correct behavior after a tuning pass.

**Suggested Fix:** The constants are private so they cannot be referenced directly. Add a comment noting the dependency on ProgressionFloor=0.3, ProgressionSpan=1.2, ProgressionCeiling=1.5 and that the range must be recalculated if those change.

#### 8. No test verifies that dropout gradients correctly zero out dropped neurons during backprop
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 878

**Description:** The dropout backpropagation logic (lines 896-955) has separate mask checks for h4Mask, h3Mask, h2Mask, h1Mask. The correctness depends on the mask values being written by ForwardPassTraining before backprop reads them. There are no visible tests (from the test files listed) that verify: (a) a zeroed-mask neuron produces zero gradient update, (b) a kept neuron produces the expected inverted-dropout-scaled gradient. The NeuralScoringStrategyTests.cs exists but given the complexity of the dropout backprop code, targeted unit tests are critical.

**Impact:** Undetected backprop errors in the dropout path would silently train a wrong model, with no observable crash.

#### 9. ComputeContentNearestNeighborScore parallel-array mismatch degrades silently with no unit test coverage for the mismatch path
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/ContentScoring.cs | 294-377

**Description:** The mismatch-handling code (lines 325-344) is a deliberate fail-safe that increments a counter and emits a one-shot TraceWarning. The code comment says 'Debug.Assert surfaces the bug in Debug builds / unit tests'. However, if no test actually constructs a mismatched input and asserts ParallelArrayMismatchCount increments, the entire fail-safe branch is untested. A future refactor that accidentally removes the graceful degradation would go unnoticed.

**Impact:** The fail-safe for a potential bug has no automated test coverage. If the graceful degradation is broken in a future refactor, the impact would be silent score degradation for all users.

**Suggested Fix:** Add a unit test that passes mismatched-length watched-item arrays to ComputeContentNearestNeighborScore, asserts ParallelArrayMismatchCount == 1, and verifies the score is still in [0,1] (not NaN/exception).

---

## LOW (75 findings)

### LOW / BUG

#### 1. Episode.SeriesId comparison against Guid.Empty â€” redundant check since the null-conditional already handles it
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 190

**Description:** At line 190: `SeriesId = item is Episode ep ? (ep.SeriesId != Guid.Empty ? ep.SeriesId : null) : null`. The inner ternary guards against Guid.Empty by returning null. Later at line 207 there is also `if (episode.SeriesId != Guid.Empty)` before adding to watchedSeriesIds. These two guards are consistent. However, in BuildPeopleProfile at line 549, the check is `if (watchedItem.SeriesId.HasValue && watchedItem.SeriesId.Value != Guid.Empty)`. Since the WatchedItemInfo.SeriesId was already set to null (not Guid.Empty) for the empty-GUID case, the `!= Guid.Empty` half of the check at line 549 is permanently unreachable â€” SeriesId is either null or a non-empty GUID. This is not a bug but is dead code that creates confusion.

**Impact:** Dead code / readability: the Guid.Empty check at line 549 is permanently false given the assignment at line 190.

**Suggested Fix:** Remove the `&& watchedItem.SeriesId.Value != Guid.Empty` condition from line 549 since it can never be true.

#### 2. AggregateException OCE unwrap in onFailure callback discards the original batch exception
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Common/BatchFallbackHelper.cs | 82

**Description:** In the onFailure catch block (line 82-86), if the callback itself throws an AggregateException containing an OperationCanceledException, the helper re-throws the inner OCE. However, the original batch exception (`ex`) that triggered the `onFailure` call is silently lost â€” it never gets logged because the callback threw before completing. This means a batch failure can be silently swallowed if the logger itself cancels.

**Impact:** Rare edge case: a failing batch call + a cancelling logger causes the batch failure to go completely unlogged, making post-mortem diagnosis impossible.

**Suggested Fix:** Log the original exception before invoking onFailure, or wrap the OCE rethrow to include the original exception as a cause.

### LOW / SECURITY

#### 1. Instance URL and Name values are logged verbatim in connection test warnings â€” potential log injection
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 399

**Description:** In TestArrInstanceGroupAsync() at lines 399 and 411, the warning message is built by embedding instance.Url and instance.Name directly into the log string without sanitization. If an admin configures a URL containing newline characters (e.g. 'http://host\nINFO API fakeEntry'), the log entry would contain a fabricated line.

**Impact:** Log injection: a malicious admin can forge log entries that appear to be legitimate plugin-log records. In a shared admin environment or when logs are forwarded to a SIEM, this could be used to spoof security events.

**Suggested Fix:** Sanitize instance.Url and instance.Name before including them in log messages: replace newlines and carriage returns with spaces or escape them. Apply the same sanitization to the Seerr URL in TestSeerrConnectionAsync.

#### 2. TrashFolderPath stored with path traversal characters possible on Windows via UNC or drive-letter paths
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 144

**Description:** ValidateTrashPathStrict rejects '.' and '..' as path segments, but on Windows an absolute path like 'C:\Windows\System32' or a UNC path '\\server\share' is accepted. Path.GetFullPath validation at line 146 will succeed for these. The path is then stored and used at runtime to create the trash directory.

**Impact:** An admin can configure the trash folder to point to system directories (e.g. C:\Windows\Temp) or network shares. While this requires admin access, it represents an unintended privilege escalation surface where the plugin's file deletion logic operates on sensitive paths.

**Suggested Fix:** Consider warning (not blocking) when an absolute Windows path points to a system directory prefix. At minimum, document that administrators should not configure system paths as the trash folder.

### LOW / CORRECTNESS

#### 1. Content-Length 0 silently bypasses the large-upload warning
**File:** Jellyfin.Plugin.JellyfinHelper/Api/BackupController.cs | 109

**Description:** The Content-Length check at line 109 uses 'Request.ContentLength ?? 0'. If a client sends a request without a Content-Length header (e.g. chunked transfer encoding), contentLength is 0, which falls through both switch arms silently. The actual body size is then enforced by the streaming check at line 146, but the up-front warning log is skipped.

**Impact:** Large chunked backup uploads will not produce the 'Large backup import detected' warning log entry, making it harder to diagnose issues from logs alone. The size limit itself is still enforced by the streaming check.

**Suggested Fix:** Log a note when Content-Length is absent (null) to indicate the upload size is unknown up-front. This aids diagnostics without blocking valid chunked requests.

#### 2. StackOverflowException cannot actually be caught â€” re-throw is dead code
**File:** Jellyfin.Plugin.JellyfinHelper/Api/DiscoveryController.cs | 229

**Description:** In BuildExcludedItemKeys() at lines 229-232, StackOverflowException is caught and rethrown. In .NET, a StackOverflowException terminates the process and cannot be caught in user code (the CLR terminates before the catch block runs). The same pattern appears in DiscoveryController's SubmitRequest() at lines 197-200.

**Impact:** The catch-and-rethrow for StackOverflowException is dead code. It gives a false impression of safety and adds noise. The real concern is that the broad 'catch (Exception)' below it swallows all other exceptions silently with no logging.

**Suggested Fix:** Remove the StackOverflowException catch block â€” it is uncatchable and misleading. The UserDiscoveryController version already uses the correct pattern: 'catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)' with explicit logging, which is preferable to the silent swallow in DiscoveryController.

#### 3. GetScript() does not dispose the manifest resource stream on NotFound path â€” but also not on success
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 264

**Description:** GetScript() at line 264 opens a manifest resource stream and either returns NotFound() (stream is null so no issue) or wraps it in FileStreamResult. FileStreamResult will dispose the stream after sending â€” that part is fine. However, if the FileStreamResult constructor throws (unlikely but possible), the stream leaks.

**Impact:** Negligible in practice since assembly resource streams are trivial to GC. However it is a structural pattern that would become a real leak if adapted for file system streams.

**Suggested Fix:** Wrap the stream in a try/catch or use a using declaration with transfer-of-ownership semantics: assign to FileStreamResult first, then return. No immediate action required for embedded resource streams.

#### 4. GetAllRecommendations generates and saves results using configuredMax but the on-demand path bypasses cache persistence for non-Activate mode silently
**File:** Jellyfin.Plugin.JellyfinHelper/Api/RecommendationController.cs | 91

**Description:** In GetAllRecommendations() at lines 91-98: when no cache exists, GetAllRecommendations(configuredMax) is called, then results are persisted only if TaskMode == Activate. In DryRun mode, results are returned but not persisted â€” consistent with documented behavior. However, if GetAllRecommendations() is an expensive operation (traversing all Jellyfin watch history), calling it on every GET /Recommendations request in DryRun mode when the cache is empty will be very slow. The comment says 'the UI caches them in the browser' but there is no server-side protection against concurrent or repeated cold-cache requests.

**Impact:** Under concurrent admin requests in DryRun mode with an empty cache, multiple expensive recommendation generation runs execute in parallel, all returning results and none persisting them. This could cause high CPU/memory spikes.

**Suggested Fix:** Add a short-lived in-memory request-deduplication guard (e.g. a SemaphoreSlim or a volatile bool flag) to prevent concurrent on-demand generation. Or document explicitly that the caller must not hit this endpoint repeatedly in DryRun mode.

#### 5. Synthetic series WatchedItemInfo has RuntimeTicks=0 â€” latent divide-by-zero if people filter logic changes
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 275-293

**Description:** Synthetic series entries added at line 275 have PlaybackPositionTicks=0 and RuntimeTicks=0. The BuildPeopleProfile 15% check at line 541 computes `(double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks`. Currently this is safe because IsFavorite=true short-circuits at line 537 before the ratio is evaluated. However the safety is entirely implicit â€” if the short-circuit logic is ever refactored or the IsFavorite check is moved after the ratio, the division executes with a zero denominator, producing NaN (not an exception, since double division by zero in C# yields +Infinity or NaN). NaN comparisons with `>= 0.15` return false, so the item would be silently excluded from PeopleProfile, which may be acceptable â€” but it is a logic trap.

**Impact:** Latent NaN propagation if the people filter is ever restructured. No current crash risk but the code is fragile.

**Suggested Fix:** Add an explicit `watchedItem.RuntimeTicks > 0` guard before the division, regardless of what other conditions are checked.

#### 6. WatchedSeriesCount assigned after BuildPeopleProfile completes â€” value is always 0 during that method
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 319

**Description:** Line 319 sets `profile.WatchedSeriesCount = watchedSeriesIds.Count` after BuildPeopleProfile has already run (line 317). BuildPeopleProfile receives `profile` by reference and could in theory read WatchedSeriesCount for its logic. Currently it does not, so this is not a bug. But the ordering creates a subtle invariant: any consumer of profile.WatchedSeriesCount inside BuildPeopleProfile will see 0. The field name suggests it should be set earlier.

**Impact:** No current bug. Future modifications to BuildPeopleProfile that read WatchedSeriesCount will silently see 0.

**Suggested Fix:** Move `profile.WatchedSeriesCount = watchedSeriesIds.Count` to immediately after the main item loop (before calling BuildLanguageProfiles) so the profile is fully populated before any downstream method receives it.

#### 7. LoadResults returns null for both 'file not found' and 'file contains JSON null' â€” indistinguishable states
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/RecommendationCacheService.cs | 112-121

**Description:** At line 106-108, file-not-found returns null. At line 113-119, JSON null deserialization also returns null (after logging a warning). Callers (RecommendationController lines 82-83 and 136-137) treat both cases identically â€” fall through to fresh generation. While this produces correct behaviour today, the two cases have different implications: file-not-found is expected (first run), while JSON-null indicates a write corruption bug. A caller who logs telemetry cannot distinguish the two states without inspecting logs. The interface contract (IRecommendationCacheService) is also ambiguous.

**Impact:** Observability/debuggability: a write-path bug that produces a null JSON literal is indistinguishable from a cold start. Introduces silent data corruption that appears as normal behaviour.

**Suggested Fix:** Return an empty list (not null) for the JSON-null case so callers can distinguish 'no file' (null) from 'file exists but is unusable' (empty or exception-handled). Alternatively, throw a specific exception type for corruption.

#### 8. BuildGenreExposureAnalysis_InsufficientHistory uses only 1 WatchedItem â€” but the threshold is MinWatchCountForGenreExposure which is not verified to be > 1
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/PreferenceBuilderTests.cs | 258-268

**Description:** The test constructs a profile with a single watched item and asserts IsValid is false. If MinWatchCountForGenreExposure is 1, this test would correctly pass. But if it is 0 (meaning any history is sufficient), the test would incorrectly pass because 1 >= 0 would make analysis Valid. The test does not check the boundary â€” it does not also assert that a profile with MinWatchCountForGenreExposure items returns IsValid=true.

**Impact:** Low. The companion test BuildGenreExposureAnalysis_SufficientHistory_ReturnsValid covers the true boundary, so the pair together provides adequate coverage. The single-item test is a reasonable lower bound.

**Suggested Fix:** Low priority. The test pair adequately covers both sides of the boundary even if the exact threshold is not pinned.

#### 9. `AddAggregatedSeriesExample`: `genreList = allGenres.ToList()` materialises the genre HashSet twice â€” once for `ComputeGenreSimilarity` and once for `ComputeGenreExposureFeatures` â€” but the HashSet `allGenres` itself is already passed directly to `ComputeTrainingTemporalAffinity`
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingFeatureComputer.cs | 290

**Description:** At line 290, `var genreList = allGenres.ToList()` is called. `genreList` is then passed to `ComputeGenreSimilarity` (line 294, takes `IReadOnlyList<string>`) and `ComputeGenreExposureFeatures` (line 338, takes `IReadOnlyList<string>`). Meanwhile, `allGenres` (the HashSet) is passed directly to both temporal affinity calls (lines 316-317). This means the method allocates both a `HashSet<string>` and a `List<string>` from the same data. `ComputeGenreSimilarity` internally creates yet another `HashSet<string>` from the list (line 231-233 in SimilarityComputer.cs). The ToList is therefore unavoidable for the IReadOnlyList-typed parameters, but the HashSet passed to temporal affinity is already the canonical form.

**Impact:** Minor extra allocation: one List<string> per series example. Not a correctness issue.

**Suggested Fix:** Low priority. Could overload `ComputeGenreSimilarity` to accept `HashSet<string>` directly, but the allocation is small and infrequent.

#### 10. Phase 2 organic standalone label: `{ Played: false, PlaybackPositionTicks: > 0 }` pattern does not check `PlayCount` â€” a `PlayCount > 0` item with `Played=false` and `PlaybackPositionTicks=0` will fall through to `ComputeEngagementLabel(0.0) = WatchedLabelFloor(0.5)`
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 703

**Description:** The organic standalone label switch at lines 764-771 covers: (1) abandoned: `Played=false, PlaybackPositionTicks > 0, completionRatio < threshold`, (2) favourite-only: `Played=false, PlaybackPositionTicks <= 0, IsFavorite=true`, (3) default: `ComputeEngagementLabel(completionRatio)`. An item with `Played=false, PlayCount > 0, PlaybackPositionTicks=0, IsFavorite=false` has `completionRatio = 0.0` (because `Played=false` and `PlaybackPositionTicks=0`). It falls to the default case and gets `ComputeEngagementLabel(0.0) = WatchedLabelFloor = 0.5`. This is arguably correct â€” a `PlayCount > 0` item was completed and then Jellyfin may have reset `PlaybackPositionTicks` to 0 and `Played` should be `true` in that case. However, `HasMeaningfulInteraction()` would include `PlayCount > 0` items in the organic loop, so this edge case is reachable.

**Impact:** A `PlayCount > 0, Played=false, PlaybackPositionTicks=0` item (unusual but valid Jellyfin data state) gets the minimum engagement label `WatchedLabelFloor=0.5` instead of a higher label proportional to actual plays. The model receives a weak positive signal for a clearly-liked item.

**Suggested Fix:** Add `PlayCount > 0` to the eligibility consideration for the label floor, or handle it as a separate case.

#### 11. Phase 2 organic standalone: `CriticRating` is hardcoded to `null` in `ComputeCombinedCriticScore` comment says 'not available on WatchedItemInfo' â€” but `WatchedItemInfo.CommunityRating` IS available and populated
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/TrainingDataBuilder.cs | 643

**Description:** At line 643-645, `ContentScoring.ComputeCombinedCriticScore(w.CommunityRating, null)` is called with an explicit `null` for criticRating. This is correct since `WatchedItemInfo` does not store `CriticRating`. However, `ComputeCombinedCriticScore(rating, null)` returns `rating/10.0` when only community rating is present â€” which is the correct fallback. No bug, but the comment at line 644 ('CriticRating not available on WatchedItemInfo') is accurate documentation.

**Impact:** No impact. Informational only.

**Suggested Fix:** No action needed.

#### 12. `BuildDiscoveryExamples` returns `(examples, examples.Count)` where `examples.Count` is the total from all users â€” not per the `Count` parameter name in the return tuple
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/DiscoveryFeedbackExampleBuilder.cs | 134

**Description:** The return type tuple is `(List<TrainingExample> Examples, int Count)`. The returned `examples.Count` is the total number of examples built across all users and all feedback entries. The caller in `TrainingDataBuilder` at line 1019 assigns this as `discoveryCount = phase4Count`. This is correct and consistent â€” `discoveryCount` is the total Phase 4 example count. No functional bug.

**Impact:** No impact. Informational note only.

**Suggested Fix:** No action needed.

#### 13. `BuildGenrePreferenceVector`: `GenreDistribution` merge skips genres already in `vector` via `ContainsKey` â€” but after `ExpandGenreProximity` runs, new genres may be added that were never in `GenreDistribution`
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/PreferenceBuilder.cs | 230

**Description:** At line 235, `if (...vector.ContainsKey(genre)) continue` prevents `GenreDistribution` from overwriting watch-derived weights. However, `ExpandGenreProximity` is called AFTER this merge (line 254). Genres inserted by `ExpandGenreProximity` (pure co-occurrence inferences) will never see their `GenreDistribution` count added â€” because `GenreDistribution` was already merged before proximity expansion. This means a genre known from `GenreDistribution` but not from `WatchedItems` (backward compat genres) will be inserted at `count/maxCount` weight, but a genre inserted purely by `ExpandGenreProximity` (never in `WatchedItems` or `GenreDistribution`) will only carry its proximity weight. This is by design â€” just noting the interaction is not documented.

**Impact:** No correctness bug. The ordering is intentional (GenreDistribution fills gaps, proximity boosts both existing and new entries). The comment at line 253-255 documents this ordering. Informational only.

**Suggested Fix:** No action required.

#### 14. NormalizeAlphaRange swaps backing fields directly, bypassing ClampAndReport
**File:** Jellyfin.Plugin.JellyfinHelper/Configuration/PluginConfiguration.cs | 267

**Description:** NormalizeAlphaRange() swaps `_ensembleAlphaMin` and `_ensembleAlphaMax` directly as backing fields. After the swap, if the new min value (formerly max) is outside [0,1] for any reason, it is stored without re-clamping. The clamp is only applied in the property setters. In practice the values were already clamped when set, so this is safe â€” but it is fragile because a future change that adds validation logic in the setters would not be triggered by the swap.

**Impact:** Low: in practice values are always clamped before NormalizeAlphaRange is called. But the pattern is fragile.

**Suggested Fix:** Swap via the public properties (EnsembleAlphaMin and EnsembleAlphaMax setters) rather than the backing fields, so the full setter validation pipeline is exercised.

#### 15. Version.ToString() called during index.html write â€” Guid.Parse called on every property access
**File:** Jellyfin.Plugin.JellyfinHelper/Plugin.cs | 363

**Description:** `public override Guid Id => Guid.Parse("0c737645-...")` on line 48 calls Guid.Parse on every access with no caching. This is called from InjectScript->UpdateIndexHtml at startup and on uninstall. While the overhead is negligible for a startup path, Guid.Parse is a computed allocation on every property read.

**Impact:** Trivial performance issue: a parse+allocation on every Id read instead of a cached value.

**Suggested Fix:** Cache the Guid as a static readonly field: `private static readonly Guid _id = new Guid("0c737645-5cbb-4bd8-80c7-d377b560aaa4"); public override Guid Id => _id;`

#### 16. CRLF validation in TestConnectionAsync is redundant with CreateClient's own validation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 55

**Description:** TestConnectionAsync validates the API key for CR/LF characters on lines 55-58, then calls CreateClient() which performs the identical check again on lines 399-402. The first check throws before entering the try/catch, which means an ArgumentException escapes to the caller unhandled (not converted to a (false, message) result), while CreateClient's same check inside the try/catch would be caught and returned as a connection failure.

**Impact:** Inconsistent error handling: a CRLF key in TestConnectionAsync throws an uncaught ArgumentException to the API layer, while all other invalid-key conditions return (false, message). This is a design inconsistency that could manifest as a 500 response instead of a user-facing validation message.

**Suggested Fix:** Remove the duplicate check from TestConnectionAsync and rely on CreateClient's validation within the try/catch, so all input validation errors are returned as (false, message) tuples consistently.

#### 17. mostRecent DateTime comparison uses DateTime? > DateTime? which silently handles null
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Activity/UserActivityInsightsService.cs | 158

**Description:** The comparison `lastPlayedUtc > mostRecent` on line 159 uses Nullable<DateTime> comparison. When mostRecent is null and lastPlayedUtc has a value, `lastPlayedUtc > mostRecent` returns false (null is treated as the smallest value in nullable comparisons), so the `!mostRecent.HasValue` guard on the same line covers this. However the combined condition `lastPlayedUtc.HasValue && (!mostRecent.HasValue || lastPlayedUtc > mostRecent)` is correct. The concern is that if lastPlayedUtc is `DateTime.MinValue` (a normalization artifact from DateTimeNormalization.ToUtc), it would still pass and set mostRecent to MinValue, which is semantically wrong.

**Impact:** If DateTimeNormalization.ToUtc returns DateTime.MinValue for items with no play date, MostRecentWatch on the summary could be set to DateTime.MinValue instead of remaining null, causing incorrect sort ordering.

**Suggested Fix:** Add a guard: only update mostRecent if lastPlayedUtc.Value > DateTime.MinValue (or have DateTimeNormalization.ToUtc return null for unplayed items instead of MinValue).

#### 18. Saturation guard condition has inverted semantic in the comments but correct code
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/LearnedScoringStrategy.cs | 688-689

**Description:** Lines 688-689: the saturation guard comment says `z <= 0 with error > 0 means predicted < label but score is floored - no escape possible`. This is correct: when z <= 0, predicted = clamp(z) = 0. error = (0 - label) which is negative when label > 0. But the code checks `error > 0` for the `z <= 0` branch. If z <= 0 and label > 0, then predicted = 0 and error = (0 - label)*sampleWeight which is negative (not positive). So the `z <= 0 && error > 0` branch would only fire when label < 0, but Label is always in [0,1]. This means the `z <= 0 && error > 0` case is unreachable with valid data. The truly unreachable-from-saturation case is `z <= 0 && error < 0` (predicted = 0, label > 0, gradient correctly tries to push score up â€” this is NOT a saturation trap and should not be skipped). The code is correctly written but the comment has the logic description inverted.

**Impact:** The saturation guard comment misleads maintainers. The code behavior is correct for valid data. Code review risk rather than runtime risk.

#### 19. Public ComputeSigmoidAlpha overload ignores its own midpoint parameter and always uses DefaultSigmoidMidpoint
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 1067-1074

**Description:** Line 1050-1056: the two-parameter overload `ComputeSigmoidAlpha(int trainingExampleCount, double alphaMin, double alphaMax)` delegates to the four-parameter version with `DefaultSigmoidMidpoint`. This is correct. However there is also a four-parameter overload at line 1067. The two-parameter version's summary says 'Uses DefaultSigmoidMidpoint as the midpoint' â€” but the signature includes `alphaMin` and `alphaMax` as parameters, not a midpoint. The method name and usage are fine; the summary just needs clarification that the midpoint is fixed.

**Impact:** No functional impact. Documentation clarity issue.

#### 20. IsAbandoned flag uses CompletionRatio > 0.0 as a guard but CompletionRatio defaults to 0.5 â€” abandoned detection unreliable for items with no explicit interaction data
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/CandidateFeatures.cs | 466

**Description:** Line 466: `HasUserInteraction && CompletionRatio > 0.0 && CompletionRatio < AbandonedThreshold`. The CompletionRatio default is 0.5 (neutral, no interaction). If HasUserInteraction is set to true but CompletionRatio is not updated (stays at 0.5), IsAbandoned = 1 if 0.5 < 0.25 = false. That's fine. But if a caller sets HasUserInteraction = true and CompletionRatio = 0.1 (meaning the item was started but barely watched), IsAbandoned = 1. The check `CompletionRatio > 0.0` is intended to exclude 'favorite-only items' per the doc. The logic is semantically correct per documentation, but the FeatureIndex doc says 'CompletionRatio=0, no playback are NOT flagged' â€” an item with CompletionRatio exactly = 0 passes the HasUserInteraction check but fails `> 0.0`, so it is not flagged. This is correct. The only gap is that CompletionRatio = 0.5 (neutral/unknown) with HasUserInteraction = false will not trigger abandonment, which is also correct.

**Impact:** No bug. Documentation-level observation for clarity.

#### 21. stateChanged flag in failed-training branch is always true â€” the variable is redundant
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 816-828

**Description:** Lines 797-834: the `stateChanged` variable is set to `false` at line 797, then unconditionally set to `true` at line 816 (always executed within the lock). The variable then gates `TrySaveState()` at line 831. Since `stateChanged` is always true after line 816, the variable serves no purpose â€” TrySaveState() is called unconditionally when training fails. The variable adds cognitive overhead without behavioral benefit.

**Impact:** No functional impact. Code clarity issue.

#### 22. He initialization uses sqrt(6/fan_in) (uniform) rather than the standard He normal sqrt(2/fan_in) or He uniform sqrt(2*6/fan_in)
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 1563-1608

**Description:** The code at line 1561 says 'He/Kaiming uniform for ReLU hidden layers: limit = sqrt(6/fan_in)'. Standard He/Kaiming uniform initialization uses limit = sqrt(6 / fan_in) when the activation is NOT ReLU â€” for ReLU, the correct He uniform bound is sqrt(6 / fan_in) only when combined with both fan_in and fan_out as in Xavier. He initialization (designed specifically for ReLU) uses variance = 2/fan_in, giving uniform limit = sqrt(6/fan_in) only if we assume fan_out â‰ˆ fan_in. The correct He uniform is sqrt(2) * sqrt(3/fan_in) = sqrt(6/fan_in), which coincidentally is the same formula as Xavier. For ReLU layers, many practitioners use sqrt(2/fan_in) as std for normal distribution. The uniform bound sqrt(6/fan_in) is equivalent to Xavier uniform and is slightly conservative for ReLU but widely accepted as 'good enough'. This is not a bug but a documentation mislabeling.

**Impact:** Negligible impact on model quality. The initialization is slightly conservative for deep ReLU networks but will still converge.

#### 23. Heuristic floor equality check at 1.0 uses exact floating-point equality which is fragile for caller-computed values
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/EnsembleScoringStrategy.cs | 232-237

**Description:** Line 232: `if (heuristic.GenrePenaltyFloor != 1.0)`. The comment explains this uses strict equality to prevent epsilon-padded values like 0.999 from slipping through. This is intentional and documented. However, if the caller computes `genrePenaltyFloor = 1.0 - someEpsilon` from a constant that rounds to exactly 1.0 in IEEE 754, it would pass the check while logically being 'penalty disabled'. This is a defense-in-depth concern, not a bug.

**Impact:** No current bug. The check is intentionally strict per the comment. Potentially fragile if callers compute the floor indirectly.

#### 24. RestoreConfiguration silently returns without setting ConfigurationRestored=false when plugin is not initialized
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupService.cs | 258-265

**Description:** When `!_configService.IsInitialized` (line 260), `RestoreConfiguration` logs a warning and returns without setting `summary.ConfigurationRestored`. The field defaults to `false`, so this is functionally correct. However, `RestoreBackup` (line 178) only checks `summary.ConfigurationRestored` implicitly through its return value. There is no signal to the caller that the configuration restore was skipped due to the plugin not being initialized â€” the timeline and baseline may still be restored successfully, resulting in a partial restore with `ConfigurationRestored=false` but `TimelineRestored=true`.

**Impact:** Callers receive a summary where the timeline is restored but configuration is not, with no explicit reason code. Operators may not understand why configuration was not restored.

**Suggested Fix:** Add a `SkipReason` or `ConfigurationSkipped` property to `BackupRestoreSummary`, or include the skip reason in the log at a higher level (WARNING is already logged, this is acceptable).

#### 25. ScriptPattern regex does not account for HTML entity encoding or Unicode escapes
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupValidator.cs | 97-100

**Description:** The `ScriptPattern` at line 97 matches literal `<script`, `javascript:`, etc. A crafted string using HTML entities (`&#60;script`, `&lt;script`) or Unicode escapes (`<script`) would bypass this check. Since the regex is used to gate backup import â€” which eventually writes values to plugin configuration that are later rendered in a web UI â€” HTML-entity-encoded payloads could still result in XSS when the configuration value is rendered without HTML encoding.

**Impact:** A crafted backup with HTML-entity-encoded script tags may pass the injection check and, if the Jellyfin UI renders the restored configuration value raw, result in stored XSS. Risk is mitigated if the UI always HTML-encodes configuration values on render.

**Suggested Fix:** Either normalize HTML entities before the regex check (using `System.Net.WebUtility.HtmlDecode`), or rely on the UI's output encoding as the primary XSS defence and document that the regex is a best-effort heuristic.

#### 26. PurgeExpiredTrash cutoff uses subtraction from utcNow, treating retentionDays=0 as purge-all
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 199

**Description:** At line 199, `cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-retentionDays)`. When `retentionDays = 0`, `cutoff` equals `utcNow`, and the condition `timestamp >= cutoff` means items trashed at exactly `utcNow` are NOT purged (timestamp == cutoff is kept). Items trashed any time in the past are purged because `timestamp < cutoff`. So `retentionDays=0` means 'purge everything trashed before now', which is aggressive but arguably correct. However, an item trashed in the same second as purge runs survives. This edge case is harmless but should be documented.

**Impact:** With `retentionDays=0`, items trashed in the same second as the purge run are not purged. No data loss risk.

**Suggested Fix:** Document the `retentionDays=0` semantics in the method XML doc.

#### 27. MaxVisitedDirectories guard is checked after Add, so the 50,001st directory is still visited
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Link/LinkRepairService.cs | 156-163

**Description:** In `FindLinkFilesRecursive` at line 150, `visited.Add(normalized)` is called first. Then at line 156, `visited.Count > MaxVisitedDirectories` is checked. This means when the count reaches 50,001, the guard fires and logs a warning for that directory â€” but the files in that 50,001st directory have already been enumerated (the files loop at line 167 runs before the guard check). Wait â€” actually no: the guard is at line 156 which is BEFORE the file enumeration loop at line 165. The Add at 150 adds to visited (incrementing count), then the guard fires at 156 and returns before processing files. So the 50,001st directory is skipped correctly. However, the log message says 'aborting deeper traversal at: {directory}' for the 50,001st entry, which is slightly misleading because that directory's files are also skipped (not just traversal into subdirectories).

**Impact:** Log message is slightly inaccurate. No functional issue.

**Suggested Fix:** Update the warning message to indicate that both files and subdirectories in the current directory are skipped.

#### 28. GetTrashContents silently swallows all IO errors, returning partial results with no indication
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 363-369

**Description:** In `GetTrashContents` (line 297), the outer `try` block at line 307 catches `IOException` and `UnauthorizedAccessException` with empty catch bodies (lines 363-369). If an error occurs mid-enumeration (e.g. after 100 directories have been read and a permissions error occurs on the 101st), the method returns a partial list sorted by date with no error indicator. The caller has no way to know the result is incomplete.

**Impact:** Users see a partial trash contents list with no error message. They may believe the trash is smaller than it actually is.

**Suggested Fix:** Return a result object that includes an optional error message, or add a boolean `IsComplete` indicator, consistent with how `GetTrashSummary` handles errors.

#### 29. GetConfiguredMinLevel swallows all exceptions from GetConfiguration()
**File:** Jellyfin.Plugin.JellyfinHelper/Services/PluginLog/PluginLogService.cs | 240-249

**Description:** At line 243, `GetConfiguredMinLevel` wraps `_configService.GetConfiguration().PluginLogLevel` in a bare `catch` (line 244) that swallows ALL exceptions and falls back to `"INFO"`. This means if a bug in `PluginConfigurationService` throws a non-transient exception (e.g. NullReferenceException, InvalidOperationException), logging silently continues at INFO level with no diagnostic. The comment says 'Plugin not initialized yet' but the catch is not scoped to that specific condition.

**Impact:** Configuration bugs in `PluginConfigurationService` are silently swallowed during logging, making them very hard to diagnose.

**Suggested Fix:** Narrow the catch to `catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)` or check `_configService.IsInitialized` before calling `GetConfiguration()` and return the fallback only in that case.

#### 30. LoadJsonFile does not handle InvalidDataException or ArgumentException from JsonDeserialize
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Backup/BackupService.cs | 451-461

**Description:** `LoadJsonFile<T>` at line 451 catches `IOException or JsonException or UnauthorizedAccessException`. `System.Text.Json.JsonSerializer.Deserialize` can also throw `ArgumentNullException` (if `json` is null, though `File.ReadAllText` cannot return null) and `NotSupportedException` (if the type is not serializable). These are not caught. While rare in practice, a corrupted timeline or baseline JSON file that triggers `NotSupportedException` would propagate an unhandled exception from `CreateBackup()` rather than returning null gracefully.

**Impact:** A JSON file containing a type that triggers `NotSupportedException` on deserialization would crash the backup creation rather than logging a warning and returning null.

**Suggested Fix:** Broaden the catch to `catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException)` or add a separate broad catch for all exceptions as a final safety net.

#### 31. Path.GetFileName with trailing slash stripped via TrimEnd handles empty paths but not root paths
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Arr/ArrIntegrationService.cs | 248

**Description:** In `CompareRadarrWithJellyfin` at line 248, `movie.Path.TrimEnd('/')` is called before `Path.GetFileName`. If `movie.Path` is a Unix root path `/media/movies/`, after trimming the slash it becomes `/media/movies`, and `Path.GetFileName` returns `movies` correctly. However, if `movie.Path` is `/` (just the root), after `TrimEnd('/')` it becomes an empty string, and `Path.GetFileName("")` returns `""`, which is then caught by the `IsNullOrEmpty` guard at line 249. This is correct. A path of `C:\` on Windows after `TrimEnd('\')` becomes `C:`, and `Path.GetFileName("C:")` returns `"C:"` â€” a volume label, not a folder name. This would produce a false `InBoth` or `InJellyfinOnly` match for anything named `C:`.

**Impact:** On Windows, a Radarr movie with a root drive path (e.g. `C:\`) would match a Jellyfin folder named `C:`. This is a pathological case but not guarded against.

**Suggested Fix:** After `Path.GetFileName`, additionally check that the result does not end with `:` (Windows volume label pattern) before adding to the comparison sets.

#### 32. ComputeAverageYear uses long sum but int year values â€” unnecessary widening and potential type confusion
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/ContentScoring.cs | 258-268

**Description:** Line 259: `long sum = 0;` accumulates production years. Production years are int (e.g. 2024). Long is unnecessary since even 10,000 items Ã— year 9999 = 99,990,000 which fits comfortably in int. While not a bug, the long widening introduces a subtle type inconsistency: the return type is double, the divisor is int count, and the dividend is long sum â€” C# will promote correctly but the widening to long is cargo-cult code.

**Impact:** No functional impact. Minor code quality issue.

**Suggested Fix:** Change `long sum = 0` to `int sum = 0` for type clarity, or use double accumulation directly.

#### 33. Collaborative co-occurrence includes series IDs from BuildCombinedWatchSet but ScoreCandidate never receives series-type items with matching IDs from episode rows
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/CollaborativeFilter.cs | 288-304

**Description:** BuildCombinedWatchSet adds w.SeriesId to the combined set. The co-occurrence loop (line 288) accumulates scores for ALL itemIds in otherCombinedIds including series IDs. At scoring time, candidates include Series objects whose Id is the series ID. So CollaborativeScore for a Series candidate can be non-zero â€” this is intentional per the comment 'series candidates get collaborative scores'. However, if the user watched episodes of a series (so the series ID is in their combined set), ComputeCollaborativeScore for that series candidate would look up the series ID in coOccurrence â€” but the collaborative loop skips items in userCombinedIds (line 288: '!userCombinedIds.Contains(itemId)'). So a series the user has watched episodes of will have its series ID in userCombinedIds and will get coOccurrence score = 0, even if neighbours also watched it. This interacts with the watchedSeriesIds filter: these series are filtered OUT before scoring anyway, so this is harmless â€” but it means the collaborative score for series the user partially watched (not yet filtered out) is 0, which is inconsistent.

**Impact:** No functional bug since watchedSeriesIds filtering removes those series before ScoreCandidate. Minor logical inconsistency.

**Suggested Fix:** Document this explicitly, or note that the watchedSeriesIds filter upstream makes the zero-score case unreachable.

#### 34. ApplyDiversityReranking performs two separate OrderByDescending sorts on the same input
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/DiversityReranker.cs | 114-117

**Description:** Line 114 sorts candidates into `ranked`. Lines 117-118 then call `ranked.Take(mmrPoolSize).ToList()` to form `remaining`. If candidates.Count <= count, the method returns early at line 103 with another sort. For the normal path, the full list is sorted once (line 114), then Take creates a second list from the front. This is correct but materializes two sub-lists from the same sorted result.

**Impact:** Minor: one extra allocation for the mmrPool sublist. No correctness issue.

**Suggested Fix:** Use ranked directly with an index boundary instead of Take().ToList() to avoid the second list allocation.

#### 35. Collection<RecommendationResult> wraps ConcurrentBag snapshot â€” redundant allocation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 452

**Description:** Line 452: `var results = new Collection<RecommendationResult>(concurrentResults.ToList());` The ConcurrentBag.ToList() creates a snapshot, and then Collection<T>(IList<T>) wraps it. The returned type is IReadOnlyList<RecommendationResult>, so the Collection wrapper is never needed â€” returning the ToList() result directly would avoid one extra allocation.

**Impact:** Trivial extra allocation per batch run.

**Suggested Fix:** Return concurrentResults.ToList() directly instead of wrapping in Collection<T>.

#### 36. GetStatus() priority order: Dismissed is evaluated after Requested, but a Requested+Dismissed item returns Requested status silently ignoring the dismiss
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryFeedbackEntry.cs | 113-128

**Description:** GetStatus() checks RequestedAtUtc before DismissedAtUtc. A user who requests an item and then dismisses it (or vice versa â€” both timestamps set) returns 'Requested' status, not 'Dismissed'. This means the dismiss signal is silently masked by the request signal. In training data, such an entry is a positive example even if the user later dismissed it.

**Impact:** Training data quality: rare edge case where a user requests then dismisses, or is marked as both. The item is treated as a positive signal in training, potentially degrading model quality slightly.

#### 37. Reason string concatenation format is inconsistent â€” ReasonKey is used both as the reason key and embedded in Reason string
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1415-1416

**Description:** Line 1416: Reason = relatedInfo != null ? $"{reasonKey}: {relatedInfo}" : reasonKey. For 'reasonPersonNamed' with a person name, Reason becomes 'reasonPersonNamed: Tom Hanks' â€” an i18n key mixed with human-readable data. If the frontend displays Reason as a fallback when the i18n key is not available, users see a raw key rather than a localized string.

**Impact:** UX: non-localized fallback reason text shown to users in unsupported locales. Minor display issue.

#### 38. userExcluded starts as a reference to the shared excludedTmdbIds; new copy only created when dismissed/requested items exist
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 1307-1325

**Description:** Line 1307: var userExcluded = excludedTmdbIds (reference copy). The user-specific copy is only created if dismissed.Count > 0 || requested.Count > 0. If a user has no dismissed/requested items, the shared set is passed directly to DeduplicateAndFilter. DeduplicateAndFilter only reads from this set (never mutates it), so there is no actual mutation risk. However, the code comment says 'Create a per-user copy to avoid mutating the shared set' â€” if a future change to DeduplicateAndFilter mutates the set, this guard silently fails for users with no prior interactions.

**Impact:** Current code: no bug. Future maintainability risk if DeduplicateAndFilter is modified to mutate its input.

#### 39. Max-pages safety cap logs a warning but marks fetchComplete=false only AFTER the loop exits â€” the break on line 512 can prevent the warning from ever firing
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 519-528

**Description:** The max-pages safety-cap warning at line 522-527 only fires when page == maxPages-1 (the last allowed iteration). However if the break condition at line 512 (currentPage >= totalPages || Results.Count < take) fires before the last page, the loop exits without ever reaching the warning, even though we might have been on the last safe page. The fetchComplete=false at line 527 inside the if(page==maxPages-1) block also only executes on that exact final iteration path. This means if pagination is truncated by the safety cap on exactly the last page, the warning fires; but if it terminates normally on page 19 out of 20, fetchComplete=true correctly.

**Impact:** Minor: no functional bug. The warning may be suppressed in edge cases, making it harder to diagnose large user rosters.

#### 40. DateTimeStyles.RoundtripKind with DateTime.TryParse may not correctly preserve UTC for non-ISO8601 date strings
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/NullableDateTimeConverter.cs | 40

**Description:** DateTimeStyles.RoundtripKind is designed for DateTime.ToString('O') round-trips. When applied to arbitrary TMDb date strings like '2024-01-15' (no time/zone suffix), DateTime.TryParse with RoundtripKind returns Kind=Unspecified. Downstream code comparing against DateTime.UtcNow uses EffectiveReleaseDate.Value.Year which is not affected by Kind. The RecencyScore computation in ContentScoring.ComputeRecencyScore would receive a DateTime with Unspecified kind, which is compared against DateTime.Now (not UtcNow) or DateTime.UtcNow depending on its implementation.

**Impact:** Potential off-by-hours RecencyScore if ContentScoring uses UtcNow and receives a Local-kind DateTime (or vice versa). On a server offset from UTC, date-only release dates parsed as Unspecified could appear as the wrong calendar day when converted.

#### 41. FindSeerrUserByJellyfinId ignores JellyfinUserId strings that are neither 32 nor 36 characters
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 997-1018

**Description:** The method only handles exactly 32-char (no hyphens) and exactly 36-char (standard hyphenated GUID) strings. Any other length (e.g. a malformed or partially stored GUID from a Seerr database inconsistency) is silently skipped. A 35-char string (one hyphen stripped from a corrupt record) would never match.

**Impact:** Minor: malformed Seerr JellyfinUserId values are silently ignored, resulting in 'user not linked to Seerr' rather than a match. Given the comment that Seerr stores IDs inconsistently, other formats could exist.

### LOW / PERFORMANCE

#### 1. Subtitle availableSubLanguages count uses same Distinct+Count pattern â€” repeated NormalizeLanguage calls
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 444-451

**Description:** The subtitle analysis block (lines 444-451) repeats the same LINQ query pattern as audio: `subtitleStreams.Select(s => NormalizeLanguage(s.Language)).Where(l => !string.IsNullOrEmpty(l)).Distinct(OrdinalIgnoreCase).Count()`. This enumerates subtitleStreams and calls NormalizeLanguage once per subtitle stream purely to determine if there is more than one distinct language. This count is computed even if subtitleStreams.Count == 1 (when the answer is trivially 1 and chosen vs. forced is determined by that alone). A simpler check `subtitleStreams.Count(s => NormalizeLanguage(s.Language) != null) > 1` is both cheaper and avoids the Distinct allocation for the common single-subtitle case.

**Impact:** Minor performance: unnecessary Distinct allocation and multiple NormalizeLanguage calls per item in the subtitle analysis hot path.

**Suggested Fix:** Replace the Distinct().Count() pattern with a short-circuit approach that stops after finding a second distinct language.

#### 2. Permutation importance computation materializes .OrderByDescending() with LINQ in debug logging path
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs | 1217

**Description:** Line 1217: `var sorted = importance.OrderByDescending(kv => Math.Abs(kv.Value))` creates an IOrderedEnumerable. Line 1219: `.Select(kv => ...)` materializes it. This uses LINQ which allocates enumerator objects and is executed inline inside `if (_logger?.IsEnabled(LogLevel.Debug) == true)`. The impact is confined to Debug log level only. No production impact.

**Impact:** Minor allocation overhead in debug builds. No production impact.

#### 3. TryBuildPeopleLookupBatch materializes candidates.Select(c => c.Id).ToList() for every batch call
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/SimilarityComputer.cs | 103-106

**Description:** Line 106: `var itemIds = candidates.Select(c => c.Id).ToList();` creates a new List<Guid> per call inside the batch lambda. This allocation is proportional to the candidate count (potentially 5000+ items) and is created even before the library API call succeeds.

**Impact:** One extra O(N) List<Guid> allocation per recommendation batch. Minor GC pressure.

**Suggested Fix:** Accept IReadOnlyList<Guid> as parameter or pre-extract IDs before entering the lambda.

#### 4. GetServiceInfoAsync and GetServiceInfoWithStatusAsync are near-identical duplicated implementations
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 613-656

**Description:** GetServiceInfoAsync (lines 550-674) and GetServiceInfoWithStatusAsync (lines 682-802) share ~90% of their implementation. The only difference is the return type signature and a few extra return ([], true/false) branches. This duplication means any bug fix must be applied twice and it's already drifted slightly: the enrichedServers.Add(server) at line 655 in GetServiceInfoAsync is inside the foreach but OUTSIDE the try/catch, meaning a server is always added even if the detail fetch throws (caught by the inner catch). Both methods have this same shape, so the logic is at least consistently duplicated.

**Impact:** Maintainability: any future change to the enrichment logic must be applied to both methods.

### LOW / INCOMPLETE

#### 1. GetTopKIndices sorts all N indices even for small K â€” O(N log N) when O(N log K) is available
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/RankingMetrics.cs | 256-272

**Description:** Line 256-272: GetTopKIndices creates a full index array of size N, sorts all of it (O(N log N)), then copies top K. The comment acknowledges this and mentions a min-heap would be better for K << N. In the recommendation context where N can be hundreds to thousands of training examples and K = 10, a partial sort (Array.Sort + take first K, or a heap) would be meaningfully faster. This is acknowledged but not implemented.

**Impact:** O(N log N) vs O(N log K) sorting overhead during ranking metrics computation. For N=500, K=10: full sort ~4500 comparisons vs partial ~500. Acceptable for current scale but worth fixing before the dataset grows.

### LOW / TEST-GAP

#### 1. TrashRetentionDays lower bound of 0 conflicts with documentation comment claiming 'must be 0â€“3650'
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationRequestValidator.cs | 34

**Description:** The validator at line 34 checks 'request.TrashRetentionDays is < 0 or > MaxDays'. So 0 is valid. But PluginConfiguration.TrashRetentionDays setter clamps to [0, 3650] with the same comment. A TrashRetentionDays of 0 means files in the trash are permanently deleted immediately on the next cleanup run â€” this is a semantically dangerous value that silently enables immediate permanent deletion even when UseTrash is true.

**Impact:** An admin who accidentally sets TrashRetentionDays to 0 (e.g. by sending a default-initialized request) will have their trash immediately purged on the next scheduled run, defeating the safety purpose of the trash feature.

**Suggested Fix:** When UseTrash is true, enforce TrashRetentionDays >= 1 in the validator. When UseTrash is false, the value is irrelevant. Add a comment explaining why 0 is only valid when trash is disabled.

#### 2. Test locates cache file via Directory.GetFiles glob â€” fragile against second .json file in temp dir
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/RecommendationCacheServiceTests.cs | 207

**Description:** In LoadResults_CorruptedFile_ReturnsNull (line 207), the test uses `Directory.GetFiles(_tempDir, "*.json").Single()` to find the cache file. If a parallel test or OS process creates a second .json file in the same temp dir (e.g. due to a collision in the GUID suffix of `_tempDir`), `.Single()` will throw `InvalidOperationException` and the test fails in a confusing way unrelated to the code under test. The same pattern is replicated in RecommendationCacheServiceExtendedTests.cs at lines 94 and 109.

**Impact:** Test reliability: non-deterministic test failure under parallel execution or file system interference.

**Suggested Fix:** Construct the expected file path from the known constant: `Path.Join(_tempDir, "jellyfin-helper-recommendations-latest.json")`. Expose `CacheFileName` as `internal const` and reference it from the test assembly via InternalsVisibleTo.

#### 3. 15% threshold boundary tests do not cover the exact boundary value (15.0%)
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/WatchHistory/WatchHistoryServiceTests.cs | 604-671

**Description:** BuildProfile_PartiallyWatchedItem_BelowThreshold_ExcludedFromPeople tests 4% progress. BuildProfile_PartiallyWatchedItem_AboveThreshold_IncludedInPeople tests 30% progress. Neither test covers exactly 15% progress (PlaybackPositionTicks/RuntimeTicks == 0.15 exactly), nor 14.9% (just below) nor 15.1% (just above). The `>= 0.15` operator means 15.0% must be included, but this is unverified. Off-by-one errors in the threshold (e.g. a future change to `> 0.15`) would not be caught.

**Impact:** Missing boundary coverage for the 15% threshold in PeopleProfile gating.

**Suggested Fix:** Add parameterized tests at exactly 15%, 14.999% (excluded), and 15.001% (included) to pin the boundary behavior.

#### 4. NormalizeLanguage has no unit tests despite 35 explicit branches feeding ML features
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/WatchHistory/WatchHistoryServiceTests.cs | 1

**Description:** NormalizeLanguage (WatchHistoryService.cs lines 670-723) has 35 explicit language code mappings plus a 2-letter passthrough and a catch-all. It is declared `internal static` which makes it directly accessible from the test assembly. None of the existing WatchHistoryServiceTests call it. Since the method's output feeds LanguageProfile and SubtitleLanguageProfile which are used as ML inference features, correctness of each mapping matters. For example, the mapping `'nor' or 'nob' or 'nno' => 'no'` vs. the mapping for 'mul', 'und', 'zxx' (which currently pass through instead of being nulled) are untested assumptions.

**Impact:** Regressions in language code normalisation (e.g. a typo in a mapping, or a missing code added from a new metadata source) will not be caught by the test suite.

**Suggested Fix:** Add a `[Theory][InlineData]` test for NormalizeLanguage covering all 35 explicit mappings, the 2-letter passthrough, null input, whitespace input, and the catch-all.

#### 5. BuildLanguageProfiles has zero unit tests â€” chosen vs. forced distinction is unverified
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/WatchHistory/WatchHistoryServiceTests.cs | 1

**Description:** BuildLanguageProfiles is a private method but its effects are visible through UserWatchProfile.LanguageProfile and SubtitleLanguageProfile. No test in WatchHistoryServiceTests exercises the audio or subtitle language profile logic â€” neither the ChosenCount vs. ForcedCount distinction (the core value of the feature), nor the fallback to audioStreams[0] when AudioStreamIndex does not match any stream, nor the subtitle -1 sentinel exclusion, nor the case where GetMediaStreams throws.

**Impact:** The chosen/forced language distinction, which drives PrimaryLanguage and the PreferredLanguages/ToleratedLanguages computed properties, is completely uncovered by tests. A regression in the logic could silently produce wrong language signals.

**Suggested Fix:** Add tests for: (1) single audio track â†’ ForcedCount, (2) multiple tracks, user picks non-default â†’ ChosenCount, (3) SubtitleStreamIndex = -1 â†’ no subtitle entry, (4) GetMediaStreams throws â†’ item skipped gracefully.

#### 6. GetAllRecommendations_MultipleUsers only asserts results.Count == 2, not that both users have distinct recommendations
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/EngineFullPipelineTests.cs | 194-225

**Description:** The test verifies the batch loop produces one RecommendationResult per user but does not assert the results are keyed to the correct users (UserId matching), nor that the results are distinct. A bug that returned the same result object twice (e.g. a thread-safety issue in Parallel.ForEach writing to a shared result) would still produce Count==2 and pass the test.

**Impact:** Low â€” thread-safety issues in Parallel.ForEach would likely manifest as exceptions rather than duplicate results, but the test provides minimal behavioral coverage of the batch output.

**Suggested Fix:** Add Assert.Equal(2, results.Select(r => r.UserId).Distinct().Count()) to verify both results belong to distinct users.

#### 7. Score_DuringTraining_DoesNotThrow uses Task.Run without ConfigureAwait and does not validate that Score() returns in-range values during a concurrent Train()
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/NeuralScoringStrategyTests.cs | 1280-1298

**Description:** The concurrency test launches training and scoring concurrently but only asserts no exception is thrown (via Assert.InRange inside the score loop, but that assertion runs in the background Task.Run without being awaited in a way that propagates exceptions before WhenAll completes). If a concurrent read of weight arrays produces NaN or Infinity (due to a race on weight arrays during Train's gradient update), Assert.InRange would throw inside the Task.Run, but the exception would be properly re-thrown by await Task.WhenAll â€” so the test is actually structured correctly. The concern is that the assertion only checks [0.0, 1.0] which does not verify correctness, only absence of crash.

**Impact:** Very low. Thread-safety is asserted in terms of non-exception, which is the primary concern for concurrent Score/Train. Value correctness during training is inherently undefined (weights are mid-update).

**Suggested Fix:** Acceptable as-is. This is the standard pattern for concurrency smoke tests.

#### 8. Constructor_HeuristicWithDefaultPenaltyFloor_Throws verifies exception message contains 'genrePenaltyFloor' but does not verify the paramName is correct
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/EnsembleScoringStrategyAdvancedTests.cs | 43-53

**Description:** The test asserts ex.Message contains 'genrePenaltyFloor'. ArgumentException(string message, string paramName) produces a message formatted as '<message> (Parameter '<paramName>')'. The test does not also verify ex.ParamName == 'heuristic' (the actual parameter name from the source). A copy-paste error that throws ArgumentException on the wrong parameter would pass this test.

**Impact:** Very low. The message check is sufficient for behavior validation. ParamName correctness is a secondary concern.

**Suggested Fix:** Optionally add Assert.Equal('heuristic', ex.ParamName) to make the test more precise.

#### 9. DryRun test for .strm reads file back with _fileSystem.File.ReadAllText to verify no mutation, but ProcessLinkFile status is Repaired â€” the test does not assert the file content equals the original after actual repair would overwrite
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Link/LinkRepairSecurityTests.cs | 329-342

**Description:** ProcessLinkFile_Strm_DryRunMode_DoesNotModifyFiles creates a .strm pointing at originalTarget ('/series/Show1/old_target.mkv', non-existent) and adds '/series/Show1/actual_episode.mkv' as the real file. Result.Status is asserted as Repaired (dry-run signal) and file content is asserted to still be originalTarget. This correctly validates dry-run safety. However the comment 'Dry-run: Repaired signals would repair' means the file WOULD be updated if dryRun=false. The test does not have a companion test with dryRun=false asserting that the file IS modified, so the actual repair path is untested in this file.

**Impact:** The repair path (non-dry-run) is not tested in LinkRepairSecurityTests. If repair logic is broken, only this test file would not catch it.

**Suggested Fix:** Add a companion test with dryRun=false asserting the file content is updated to the new target. This would complete the safety/repair pair.

#### 10. Validate_ValidTaskModes_NoWarnings asserts Assert.Empty(result.Warnings) but the valid backup from CreateValidBackup may already have non-empty warnings for other reasons
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Backup/BackupServiceTests.cs | 163-180

**Description:** The test calls CreateValidBackup() and then overwrites all task modes with the valid mode, then asserts Assert.Empty(result.Warnings). CreateValidBackup sets a specific CreatedAt (DateTime.UtcNow), specific language 'en', and other fields. In theory all fields are valid so no warnings should arise. However if ValidateTimestamp has logic based on the current time and the backup's timestamp is slightly in the future (clock skew, very fast test run), it could add a warning that fails the Assert.Empty assertion spuriously.

**Impact:** Very low â€” CreateValidBackup uses DateTime.UtcNow which should never trigger the future-timestamp warning (future means > a few seconds ahead). The risk is negligible.

**Suggested Fix:** Acceptable as-is. Clock skew risk is minimal.

#### 11. TrainingDataBuilderTests has only one test covering Phase 3 determinism â€” Phase 1, Phase 2 organic, Phase 4 discovery, and the perUserCache caching logic have zero direct test coverage
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/Training/TrainingDataBuilderTests.cs | 18

**Description:** The test file contains a single test (`BuildExamples_Phase3RandomNegatives_AreDeterministicAcrossRuns`). There are no tests for: Phase 1 label correctness (abandoned, favourite-only, recommendation-influenced boost), Phase 2 organic watched items (series aggregation, standalone), Phase 4 discovery examples, the `perUserCache` caching logic, the `BuildWatchedIdSet` helper, or `ComputeCollectionProgressionBoostWithCounts`. The series aggregation test coverage is split into `TrainingFeatureComputerTests` which only tests `AddAggregatedSeriesExample` directly, leaving the integration path (Phase 2 calling into the aggregation) untested end-to-end.

**Impact:** Regressions in Phase 1 label assignment, Phase 2 standalone example construction, Phase 4 feature computation, or the perUserCache refactoring would not be caught by the current test suite.

**Suggested Fix:** Add tests for: (a) Phase 1 recommendation-influenced label boost, (b) Phase 1 abandoned label for partial-watch, (c) Phase 2 organic series aggregation calling into BuildExamples, (d) Phase 4 discovery examples with known entries, (e) ComputeCollectionProgressionBoostWithCounts edge cases (empty boxSetIds, empty watchedCounts).

#### 12. `AddAggregatedSeriesExample` tests do not cover the `LibraryAddedRecency` feature: episodes with mixed null/non-null `DateCreated` values
**File:** Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/Training/TrainingFeatureComputerTests.cs | 760

**Description:** Line 322-327 in `TrainingFeatureComputer.cs` computes `LibraryAddedRecency` as the minimum `DateCreated` across all episodes. The test fixture in `BuildSeriesEpisodes` always sets `DateCreated = new DateTime(2020, 6, 1, ...)` uniformly. There is no test for: (a) all `DateCreated` null â†’ should return 0.5, (b) mixed null/non-null â†’ min of non-null values. The LINQ expression `episodes.Select(e => e.DateCreated).Where(d => d.HasValue).Min() is { } minDate` returns `null` (no value) when all are null, which correctly falls to the `: 0.5` branch, but this is untested.

**Impact:** Edge case for all-null DateCreated in episodes is untested. The current code handles it correctly via the null pattern, but a refactor could break it silently.

**Suggested Fix:** Add a test case with all-null DateCreated and one with mixed null/non-null DateCreated to pin the LibraryAddedRecency fallback behavior.

#### 13. No test coverage for the infinite-pagination guard (missing) and the pageInfo.Results=0 edge case
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/SeerrIntegrationService.cs | 130

**Description:** The pagination loop has no test for the scenario where pageInfo.Results is 0 initially (empty Seerr instance) or where Results decreases between pages (concurrent deletions). The do-while loop will execute at least once even when Results=0 is known upfront from the first page's pageInfo.

**Impact:** An empty Seerr instance makes one unnecessary GET request to page 1 before seeing 0 results and breaking. Not a bug but an inefficiency and a missing test case.

**Suggested Fix:** Add a test for the empty-Seerr case. Consider checking pageInfo.Results == 0 before the loop starts to skip it entirely.

#### 14. AggregateException OCE rethrow uses First() which throws if flattening yields no OCE
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Common/BatchFallbackHelper.cs | 65

**Description:** At line 65, `agg.Flatten().InnerExceptions.OfType<OperationCanceledException>().First()` is called. This is guarded by the `when (ContainsOperationCanceled(agg))` filter at line 57, which calls the same `Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException)`. There is a logical guarantee that `First()` will succeed because `ContainsOperationCanceled` returned true. However, `Flatten()` is called twice on the same exception object â€” once in the filter and once in the body. `AggregateException.Flatten()` creates a new flat `AggregateException` each time. For an immutable exception object this is deterministic, so no race exists here. The same double-call pattern repeats at lines 82-86 in the `onFailure` callback's catch block.

**Impact:** No functional bug â€” the `when` guard guarantees `First()` won't throw. Minor efficiency issue: `Flatten()` allocates a new AggregateException twice per catch.

**Suggested Fix:** Cache the result of `agg.Flatten()` in a local variable to avoid the double allocation.

#### 15. No test coverage for ParentalRatingHelper boundary values maxParentalRating=60, 61, 100, 101, 140, 141
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/ParentalRatingHelper.cs | 79-122

**Description:** The boundary values (60 â†’ child path, 61 â†’ not child, 100 â†’ still teen-restricted, 101 â†’ not teen-restricted, 140 â†’ not adult-unrestricted, 141 â†’ unrestricted) are critical correctness boundaries. An off-by-one in any comparison would silently misclassify content for an entire user tier. No test files were found in the reviewed domain.

**Impact:** A single character change (e.g., < to <=) at any boundary would expose children to teen content or over-restrict teen users, with no automated regression catch.

#### 16. ReinsertAtOriginalIndices is only exercised via rollback paths that require IO failure simulation
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryCacheService.cs | 478-491

**Description:** The rollback logic for RemoveItemLocked and MarkAsRequestedLocked is only reachable when AtomicFile.WriteAllText throws or OperationCanceledException fires mid-async-write. These paths are not unit-testable without fault-injection capabilities, so the ascending-order rollback correctness (which has the bug described above) cannot be verified by automated tests.

**Impact:** The rollback logic, which has an ordering bug, cannot be regression-tested, increasing the risk that the bug goes undetected.

### LOW / DESIGN

#### 1. IsDiscoveryUserAccessEnabled reads from Plugin.Instance static â€” untestable and fragile singleton access
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 605

**Description:** IsDiscoveryUserAccessEnabled() at line 605 accesses Plugin.Instance?.Configuration directly as a static singleton. This pattern is repeated in GetExternalLinksConfig() at line 243. The controller already receives IPluginConfigurationService or similar services via DI for other controllers, but UserDiscoveryController does not inject a config service â€” instead it uses the static singleton.

**Impact:** The static access makes unit testing impossible without a real plugin instance. It also means configuration changes do not benefit from any DI lifecycle guarantees. If Plugin.Instance is null during initialization or teardown, all these guards silently return false/empty, potentially causing unexpected 403 responses during startup.

**Suggested Fix:** Inject IPluginConfigurationService (already used in ConfigurationController and RecommendationController) into UserDiscoveryController and read DiscoveryUserAccessEnabled through it, consistent with the rest of the codebase.

#### 2. ApiKeyMask constant is internal â€” cannot be used in tests or by external validation logic without reflection
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationResponse.cs | 8

**Description:** ApiKeyMask is declared internal at line 8. It is referenced in ConfigurationController (same assembly, fine) but the sentinel value '***' is a cross-cutting concern: any code that needs to detect 'this is a masked key' must either know the literal string or use reflection to access the internal constant. If a test assembly or a client library needs to check for the mask, they must hardcode '***'.

**Impact:** Low immediate impact since the sentinel is only used within the plugin assembly. However, if the mask value ever needs to change, callers who hardcoded '***' will silently break.

**Suggested Fix:** Consider making ApiKeyMask public, or expose a public static bool IsApiKeyMasked(string value) helper method on ConfigurationResponse. This makes the contract explicit and testable.

#### 3. Magic constant 0.15 (15% progress threshold) is inlined with no named constant
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs | 530

**Description:** The literal `0.15` at line 541 is the only occurrence of this threshold in the codebase. It controls which partially-watched items contribute to PeopleProfile. It has no accompanying named constant (`MinSignificantProgressRatio` or similar) and no reference to the `maxActorsPerItem` constant nearby that would encourage grouping related configuration values. A future tuning of the threshold requires knowing this literal exists and finding it.

**Impact:** Maintainability: if the threshold is ever adjusted, there is no single authoritative location to change. The value is also not documented in the interface contract.

**Suggested Fix:** Extract `private const double MinSignificantProgressRatio = 0.15;` near the `maxActorsPerItem` constant and reference it in the comparison.

#### 4. SaveResults serialises full UserWatchProfile including all WatchedItems â€” unbounded cache file size
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/RecommendationCacheService.cs | 58

**Description:** RecommendationResult.Profile is of type UserWatchProfile? which contains WatchedItems (a Collection<WatchedItemInfo>). With a library of 5000 movies and 50000 episodes across 10 users, each UserWatchProfile.WatchedItems could contain thousands of entries. The entire structure is serialised via JsonSerializer.Serialize (line 58) with no size limit or truncation. The profile data is redundant in the cache (it is re-computed on demand) and the RecommendationController's GetAllWatchProfiles endpoint already returns lean copies without WatchedItems (line 235). Caching the full profile makes the JSON file unnecessarily large.

**Impact:** The cache file can grow to tens of MB for large libraries, increasing serialisation time, disk I/O, and deserialisation time on load. The full WatchedItems list is never surfaced directly from the cache to API callers.

**Suggested Fix:** Consider adding [JsonIgnore] to UserWatchProfile.WatchedItems for cache serialisation, or use a dedicated DTO that omits it. Alternatively, set Profile = null before caching if Profile data is re-generated on demand.

#### 5. Plugin.Instance null-conditional access in DI factory lambdas creates a silent startup dependency
**File:** Jellyfin.Plugin.JellyfinHelper/PluginServiceRegistrator.cs | 69

**Description:** The DI factory lambdas use `Plugin.Instance?.DataFolderPath` and `Plugin.Instance?.Configuration`. If Plugin.Instance is null at DI container build time (e.g. in tests or if the plugin ctor threw), all three scoring strategies silently use null paths (no weights loaded) and default config values. There is no log message emitted when this happens.

**Impact:** Scoring strategies silently start with no learned weights and no config tuning, with no indication in logs that this happened. The system degrades without any observable signal.

**Suggested Fix:** Add an explicit null check and warning log when Plugin.Instance is null during the factory lambda execution.

#### 6. OnUninstalling calls both UnregisterFileTransformation and UpdateIndexHtml(false) unconditionally
**File:** Jellyfin.Plugin.JellyfinHelper/Plugin.cs | 65

**Description:** OnUninstalling always calls both UnregisterFileTransformation() and UpdateIndexHtml(false). When the FileTransformation plugin is installed, UnregisterFileTransformation correctly removes the runtime transformation. UpdateIndexHtml(false) then runs unnecessarily (the script was never written to disk in this mode) and reads, parses, compares, and potentially rewrites index.html for no effect. This is harmless but wasteful and logs misleading debug messages.

**Impact:** Minor: unnecessary I/O on uninstall when running with the FileTransformation plugin. Could trigger a spurious file-write if the removal regex matches unexpected content.

**Suggested Fix:** Track whether fallback mode was used (e.g. a bool field set in InjectScript) and only call UpdateIndexHtml(false) when fallback mode was active.

#### 7. CanWriteDirectory probe file is not deleted if File.Delete throws
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/TrashService.cs | 941-955

**Description:** In `CanWriteDirectory` (line 939), the probe file is created with `File.Create` and then `File.Delete(probePath)` is called. If `File.Delete` throws an exception that is not `UnauthorizedAccessException` or `IOException` (which is caught at line 953), the probe file is orphaned. More importantly, the current catch does not distinguish between 'create failed' and 'delete failed'. If `File.Create` succeeds but `File.Delete` fails with a caught exception, `CanWriteDirectory` returns `false` (write access denied) even though write access was confirmed. The probe file is also orphaned in this case.

**Impact:** False 'cannot write' result if probe file deletion fails for a non-permission reason (e.g. file locked by AV scanner immediately after creation). Probe file may be orphaned in the trash directory.

**Suggested Fix:** Use a `try/finally` to ensure probe deletion: `File.Create(probePath).Dispose(); return true; finally { TryDeleteProbe(probePath); }` pattern, and separate the create-succeeded path from the delete-failed path.

#### 8. _batchGeneration is int but _publicationSequence is long â€” asymmetric overflow risk documentation gap
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs | 78-88

**Description:** _batchGeneration is an int (max 2,147,483,647) incremented via Interlocked.Increment. With one batch per day, this overflows in ~5.9 million years â€” irrelevant. However, _publicationSequence is a long, which is also fine. The asymmetry is harmless but undocumented. ComputeStableSeed takes int suffix, so batchGeneration is passed as int â€” if batchGeneration ever overflowed (hypothetically), the seed computation would silently use a wrapped negative value.

**Impact:** No real impact. Documentation gap only.

**Suggested Fix:** Add a comment acknowledging the int overflow is unreachable in practice, or use uint to make the wrap-around semantics explicit.

#### 9. DiscoveryFeedbackStore uses synchronous Lock while DiscoveryCacheService uses SemaphoreSlim â€” inconsistent threading model for equivalent concerns
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/DiscoveryFeedbackStore.cs | 42

**Description:** DiscoveryCacheService switched to SemaphoreSlim to support async callers. DiscoveryFeedbackStore still uses a plain Lock and all operations are synchronous. If the feedback store ever needs to support async callers (e.g., async RecordShown on a request path), the same sync-over-async problem that motivated the SemaphoreSlim switch in the cache service would apply. Currently RecordShown is called from an async context (GenerateDiscoveryRecommendationsAsync) but it is always synchronous â€” acceptable because it runs on a background task thread, not a request thread.

**Impact:** Design: inconsistency creates a future maintenance trap. No current functional issue.

---

## INFO (6 findings)

### INFO / CORRECTNESS

#### 1. DismissItem validates dto null AFTER extracting userId â€” null dto would cause NRE before the check
**File:** Jellyfin.Plugin.JellyfinHelper/Api/UserDiscoveryController.cs | 493

**Description:** In DismissItem() at lines 485-496: GetCurrentUserId() is called at line 485, userId is validated at line 486, then currentUserId is assigned at line 491, and THEN dto null is checked at line 493. Because [ApiController] with [FromBody] will typically reject a null body before the action runs, this ordering is safe in practice. However, if the null check is ever triggered (e.g. filter removed), the parameter 'dto' is already used to extract dto.TmdbId at line 498 after the null check passes â€” the ordering is fine. But the dto null check at line 493 comes after userId extraction â€” a minor style inconsistency with SubmitMyRequest where dto null is checked first.

**Impact:** No runtime defect given [ApiController] behavior. Style inconsistency that could confuse reviewers into thinking the null check is misplaced.

**Suggested Fix:** Move the dto null check to line 480 (right after the DiscoveryUserAccessEnabled check), before GetCurrentUserId(). This is consistent with SubmitMyRequest's ordering and avoids the confusing late-null-check pattern.

#### 2. TestSeerrConnectionAsync skips test when SeerrApiKey is whitespace â€” but stored key may be non-empty
**File:** Jellyfin.Plugin.JellyfinHelper/Api/ConfigurationController.cs | 302

**Description:** TestSeerrConnectionAsync() at line 302 returns early when request.SeerrApiKey is whitespace. But if the client sent an empty SeerrApiKey (meaning 'clear the key'), ApplyRequestToConfig() would have already stored an empty key. The connection test is skipped, but the previously stored valid key has now been cleared. The admin sees no warning that the key was cleared and no connection test failure.

**Impact:** If an admin accidentally clears the SeerrApiKey field in the UI and submits, the key is erased with no warning and no failed connection test to alert them. The silence makes the error hard to diagnose.

**Suggested Fix:** After saving config, if config.SeerrUrl is non-empty but config.SeerrApiKey is now empty (because the client cleared it), add a warning to the response: 'Seerr URL is configured but API key has been cleared. Seerr integration will not function until a key is provided.'

#### 3. GenerateDiscoveryRecommendationsAsync does not pass the CancellationToken to _cache.Save()
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 142-269

**Description:** Line 234: _cache.Save(allResults) is synchronous and does not accept a CancellationToken. If the task is cancelled immediately after all users are processed but before Save completes, the write cannot be interrupted. This is likely acceptable (the write is fast) but is inconsistent with the overall cancellation design of the method.

**Impact:** Informational: no functional bug. The sync save path completes quickly relative to the overall task duration.

#### 4. seerrUserId guard uses pattern > 0 but serverId and profileId guards use >= 0 â€” inconsistent minimum-value semantics
**File:** Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/SeerrDiscoveryService.cs | 350-351

**Description:** seerrUserId is only included in the payload when seerrUserId is > 0 (line 350). serverId and profileId are validated to be >= 0 (lines 304-312) and included when HasValue (lines 355-362). A serverId or profileId of 0 is thus a valid value that gets sent to Seerr. Whether Seerr server ID 0 is valid is API-specific, but the inconsistency between > 0 and >= 0 guards is a potential source of confusion.

**Impact:** Informational: if Seerr uses 0 as a valid server ID, the current code is correct. If 0 is invalid, profileId=0 or serverId=0 would be sent to Seerr and may cause a server-side error.

### INFO / DESIGN

#### 1. RadarrInstances and SonarrInstances use init-only setter â€” prevents post-construction modification
**File:** Jellyfin.Plugin.JellyfinHelper/Configuration/PluginConfiguration.cs | 147

**Description:** The `init` accessor on RadarrInstances and SonarrInstances prevents any code path from replacing the list after construction. The comment explains this is to survive System.Text.Json deserialization. However, the XmlSerializer used by BasePlugin<T> ignores init-only properties in some .NET versions, potentially silently failing to deserialize these collections on XML config reads.

**Impact:** If XmlSerializer cannot write to init-only properties (behavior is runtime-version-dependent), RadarrInstances and SonarrInstances will always deserialize to their default empty lists, silently losing all configured Arr instances on plugin restart.

**Suggested Fix:** Verify XmlSerializer behavior with init-only properties on the target .NET version. If XmlSerializer requires a set accessor for deserialization, change init to set (or use a custom XmlSerializer constructor pattern).

#### 2. Sub-task instances created fresh on every ExecuteAsync call â€” loggerFactory.CreateLogger called per run
**File:** Jellyfin.Plugin.JellyfinHelper/ScheduledTasks/HelperCleanupTask.cs | 282

**Description:** RunTrickplayCleanup, RunEmptyMediaFolderCleanup, RunOrphanedSubtitleCleanup, and RunLinkRepair instantiate their task objects on every ExecuteAsync call via `new CleanXxxTask(...)`. Each call also invokes `_loggerFactory.CreateLogger<T>()`. While not expensive, it allocates new objects per task run instead of reusing singletons.

**Impact:** Minor per-run allocation. No functional issue.

**Suggested Fix:** Consider caching the task instances as constructor-injected singletons or lazy fields, especially if the tasks ever grow stateful initialization logic.

---

## Summary Table

| # | Severity | Category | File | Title |
|---|----------|----------|------|-------|
| 1 | HIGH | security | ConfigurationRequestValidator.cs | Seerr API key accepted as empty when URL is set via mask sentinel path |
| 2 | HIGH | security | UserDiscoveryController.cs | AllowAnonymous script endpoint serves JavaScript with no Content-Secur |
| 3 | HIGH | security | ConfigurationRequestValidator.cs | SSRF: Arr and Seerr URLs validated for scheme only, no private/loopbac |
| 4 | HIGH | security | ConfigurationController.cs | Language field accepted without allowlist validation â€” stored verbat |
| 5 | HIGH | correctness | WatchHistoryService.cs | ToLookup + two ToList() calls materialise all streams twice on every p |
| 6 | HIGH | correctness | WatchHistoryService.cs | availableAudioLanguages count computed before the null-language guard  |
| 7 | HIGH | bug | WatchHistoryService.cs | Episode-to-series people aggregation falls back to episode item when s |
| 8 | HIGH | test-gap | EngineFullPipelineTests.cs | Assert.All on potentially empty collection vacuously passes â€” no gua |
| 9 | HIGH | test-gap | EngineFullPipelineTests.cs | Warm-path test only asserts Cohort and non-empty ScoringStrategy strin |
| 10 | HIGH | correctness | PreferenceBuilderTests.cs | HeavyRewatcher test asserts eRatio > mRatio but 'Anchor' item has Play |
| 11 | HIGH | test-gap | PreferenceBuilderTests.cs | ProximityExpansion test relies on baseline profile with single-genre r |
| 12 | HIGH | correctness | TrainingDataBuilder.cs | Phase 1 abandoned-check fires on neutralised CompletionRatio=0.5 for u |
| 13 | HIGH | correctness | TrainingDataBuilder.cs | Phase 1 watched-genre/people/studio sets built with `w.Played || w.IsF |
| 14 | HIGH | correctness | TrainingDataBuilder.cs | Phase 1 `BuildWatchedIdSet` uses `watchedIds` (filtered by `HasMeaning |
| 15 | HIGH | correctness | TrainingFeatureComputer.cs | IReadOnlyList overload of `ComputeTrainingTemporalAffinity` returns ne |
| 16 | HIGH | security | DiscoveryScriptTag.cs | XSS via unescaped version string injected into HTML attribute |
| 17 | HIGH | security | SeerrIntegrationService.cs | SSRF via operator-supplied Seerr base URL with no host/IP restriction |
| 18 | HIGH | security | Plugin.cs | API key logged in clear text at Warning level on invalid-config path |
| 19 | HIGH | bug | PluginConfiguration.cs | SeerrCleanupAgeDays clamp minimum is 0 but service enforces minimum of |
| 20 | HIGH | correctness | PluginServiceRegistrator.cs | HeuristicScoringStrategy registered with hardcoded genrePenaltyFloor=1 |
| 21 | HIGH | correctness | PluginServiceRegistrator.cs | ML strategy configuration frozen at DI build time â€” config changes r |
| 22 | HIGH | correctness | NeuralScoringStrategy.cs | Two separate lock objects (_rwLock and _syncRoot) protect overlapping  |
| 23 | HIGH | correctness | NeuralScoringStrategy.cs | Score() allocates a new double[] on every call despite claiming zero-a |
| 24 | HIGH | correctness | NeuralScoringStrategy.cs | Gradient loss formula uses sigmoid derivative of training-forward outp |
| 25 | HIGH | correctness | NeuralScoringStrategy.cs | Input-gradient attribution in ScoreWithExplanation uses standardized v |
| 26 | HIGH | bug | LearnedScoringStrategy.cs | K-fold cross-validation restores savedWeights after all folds, but sav |
| 27 | HIGH | correctness | BackupService.cs | Sonarr key lookup built from already-cleared list |
| 28 | HIGH | correctness | BackupService.cs | Radarr credential-change detection compares truncated new key against  |
| 29 | HIGH | security | FolderBrowserService.cs | ValidatePath allows browsing non-existent paths, only errors on existi |
| 30 | HIGH | correctness | TrashService.cs | MoveFileToTrash creates trash directory AFTER ResolveCollision, causin |
| 31 | HIGH | bug | DiversityReranker.cs | DeduplicateSeries: stale index after in-place replacement corrupts loo |
| 32 | HIGH | correctness | Engine.cs | ComputeStableSeed uses Guid.GetHashCode() which is NOT process-stable  |
| 33 | HIGH | correctness | Engine.cs | ExceedsMaxRating blocks ALL unrated items for restricted profiles, inc |
| 34 | HIGH | performance | Engine.cs | N+1 database query per series candidate in ResolveMediaLanguages via S |
| 35 | HIGH | correctness | Engine.cs | coOccurrence.Values.Max() called via LINQ enumeration on Dictionary<Gu |
| 36 | HIGH | bug | DiscoveryCacheService.cs | Sync-over-async deadlock potential: RemoveItem calls async method via  |
| 37 | HIGH | correctness | DiscoveryCacheService.cs | ReinsertAtOriginalIndices ascending-order rollback is logically incorr |
| 38 | HIGH | security | SeerrDiscoveryService.cs | rootFolder value passed to Seerr without path sanitisation â€” potenti |
| 39 | HIGH | security | SeerrDiscoveryService.cs | GetServiceInfoAsync serviceType path parameter is not sanitised before |
| 40 | MEDIUM | security | BackupController.cs | Export backup includes API keys in plaintext |
| 41 | MEDIUM | security | DiscoveryController.cs | Admin request endpoint allows arbitrary SeerrUserId â€” impersonation  |
| 42 | MEDIUM | security | UserDiscoveryController.cs | GetExternalLinksConfig leaks Seerr base URL to all authenticated users |
| 43 | MEDIUM | bug | BackupController.cs | MemoryStream disposed before StreamReader finishes reading â€” potenti |
| 44 | MEDIUM | correctness | ConfigurationController.cs | UpdateLogLevel does not call SaveConfiguration after mutating config â |
| 45 | MEDIUM | correctness | UserDiscoveryController.cs | Profile validation allows rootFolder=null when matchedProfile.RootFold |
| 46 | MEDIUM | correctness | ConfigurationController.cs | Arr instance key restoration uses Name+Url equality with no collision  |
| 47 | MEDIUM | correctness | DiscoveryController.cs | GetDiscoveryResults normalizes MediaType inconsistently with UserDisco |
| 48 | MEDIUM | correctness | ConfigurationRequestValidator.cs | ValidateArrInstances allows an instance with only an API key and no UR |
| 49 | MEDIUM | performance | BackupController.cs | Encoding.UTF8.GetByteCount called redundantly â€” full string re-scann |
| 50 | MEDIUM | performance | RecommendationController.cs | IsRecommendationsEnabled() calls GetConfiguration() on every request â |
| 51 | MEDIUM | correctness | ConfigurationRequestValidator.cs | ValidateTrashPathStrict redundantly checks control characters â€” logi |
| 52 | MEDIUM | correctness | WatchHistoryService.cs | AverageCommunityRating accumulates ratings for favorited-but-unplayed  |
| 53 | MEDIUM | correctness | WatchHistoryService.cs | People 15% threshold creates undocumented asymmetry with genre/languag |
| 54 | MEDIUM | correctness | RecommendationCacheService.cs | LoadResults exception filter narrower than SaveResults â€” SecurityExc |
| 55 | MEDIUM | correctness | WatchHistoryService.cs | GetUserWatchProfile performs two full library scans per call â€” O(N)  |
| 56 | MEDIUM | bug | WatchHistoryService.cs | seenPeople.Contains() + seenPeople.Add() is redundant â€” the Contains |
| 57 | MEDIUM | correctness | WatchHistoryService.cs | SubtitleStreamIndex guard checks >= 0 but subtitle stream index can le |
| 58 | MEDIUM | correctness | WatchHistoryService.cs | NormalizeLanguage catch-all returns unmapped 3-letter codes as-is â€”  |
| 59 | MEDIUM | security | WatchHistoryService.cs | UserName logged without sanitisation â€” potential log injection |
| 60 | MEDIUM | test-gap | CollaborativeFilterTests.cs | InsufficientOverlap test hardcodes MinCollaborativeOverlap threshold a |
| 61 | MEDIUM | correctness | NeuralScoringStrategyTests.cs | XavierInit_IsDeterministic compares two independently-constructed stra |
| 62 | MEDIUM | correctness | NeuralScoringStrategyTests.cs | Train_MultipleTimes_ProducesFiniteLoss asserts loss2 <= loss1 + 0.05 b |
| 63 | MEDIUM | test-gap | NeuralScoringStrategyTests.cs | VersionMismatch_DiscardsWeights test compares stale score against a fr |
| 64 | MEDIUM | test-gap | EnsembleScoringStrategyAdvancedTests.cs | ScoreWithOffset_ZeroOffset_MatchesScore uses 1e-11 as 'zero offset' bu |
| 65 | MEDIUM | correctness | EnsembleScoringStrategyAdvancedTests.cs | ApplyCohortFeedback_InsufficientControlSamples_NoOp: controlResult has |
| 66 | MEDIUM | test-gap | SeerrDiscoveryServiceTests.cs | SeerrNotConfigured and CrlfApiKey tests mutate Plugin.Instance.Configu |
| 67 | MEDIUM | test-gap | BackupServiceTests.cs | TimelineWithTooManyPoints test asserts IsValid=true but does not verif |
| 68 | MEDIUM | test-gap | LinkRepairSecurityTests.cs | ftp:// URL allowed in .strm files but ProcessLinkFile_Strm_StreamingUr |
| 69 | MEDIUM | correctness | PreferenceBuilderTests.cs | PhantomRowsForDeletedSeries test asserts live weight is 1.0 in BOTH pr |
| 70 | MEDIUM | test-gap | PreferenceBuilderTests.cs | ComputeProgressionMultiplier_AbandonedSeries test asserts Fringe weigh |
| 71 | MEDIUM | correctness | TrainingDataBuilder.cs | Phase 1 series switch: `case true when wasWatched && watchedItemForRec |
| 72 | MEDIUM | correctness | TrainingDataBuilder.cs | `organicFallbackTimestamp` uses `previousResults.Min(r => r.GeneratedA |
| 73 | MEDIUM | correctness | TrainingDataBuilder.cs | Phase 2 series aggregation: `seriesEpisodeLookupOrganic` is built from |
| 74 | MEDIUM | correctness | TrainingFeatureComputer.cs | `AddAggregatedSeriesExample`: `ratedEpisodes.Average(e => e.UserRating |
| 75 | MEDIUM | correctness | DiscoveryFeedbackExampleBuilder.cs | `combinedCriticScore = Math.Clamp(entry.TmdbRating / 10.0, 0.0, 1.0)`  |
| 76 | MEDIUM | correctness | DiscoveryFeedbackExampleBuilder.cs | Phase 4 `preferredPeople` is built from `userProfile.TopPeople` (a cou |
| 77 | MEDIUM | correctness | PreferenceBuilder.cs | `IsPhantomSeriesRow` uses `ContainsKey` (not `TryGetValue`) on `series |
| 78 | MEDIUM | performance | TrainingDataBuilder.cs | Phase 2 builds `seriesWithOrgEpisodes` and `seriesEpisodeLookupOrganic |
| 79 | MEDIUM | performance | TrainingDataBuilder.cs | Phase 1 builds `watchedItemLookup` per-result-set (inside the outer `f |
| 80 | MEDIUM | performance | TrainingDataBuilder.cs | Phase 1 builds `seriesEpisodeLookup` per-result-set inside the outer l |
| 81 | MEDIUM | performance | TrainingDataBuilder.cs | Phase 1 builds `watchedGenreSets`, `watchedPeopleSets`, `watchedStudio |
| 82 | MEDIUM | bug | Plugin.cs | Static Instance field set in constructor â€” not thread-safe and leaks |
| 83 | MEDIUM | bug | SeerrIntegrationService.cs | Task.Delay with CancellationToken.None ignores cancellation between DE |
| 84 | MEDIUM | bug | SeerrIntegrationService.cs | SeerrCleanupResult.Failed initialized to 0 then set to 1 (not incremen |
| 85 | MEDIUM | bug | SeerrIntegrationService.cs | Pagination can loop infinitely if API returns inconsistent pageInfo.Re |
| 86 | MEDIUM | bug | UserActivityInsightsService.cs | itemTotalPlays uses PlayCount (cumulative) but viewerCount is a unique |
| 87 | MEDIUM | bug | HelperCleanupTask.cs | Path traversal risk in trash purge: GetFullPath can be manipulated via |
| 88 | MEDIUM | performance | UserActivityInsightsService.cs | Full library scan with no library exclusion filter applied â€” exclude |
| 89 | MEDIUM | performance | UserActivityInsightsService.cs | GetUsers().ToList() materializes all users into memory with no lazy en |
| 90 | MEDIUM | correctness | DiscoveryScriptTag.cs | RemovalRegex does not use RegexOptions.IgnoreCase â€” misses mixed-cas |
| 91 | MEDIUM | correctness | Plugin.cs | jTokenType resolved with null-forgiving operator but never null-checke |
| 92 | MEDIUM | bug | SeerrIntegrationService.cs | CreateClient mutates a shared named HttpClient from IHttpClientFactory |
| 93 | MEDIUM | correctness | HelperCleanupTask.cs | Seerr Discovery, User Activity, and Recommendations all share Recommen |
| 94 | MEDIUM | correctness | StatisticsCacheService.cs | SaveLatestResult accepts null without ArgumentNullException guard |
| 95 | MEDIUM | correctness | EnsembleScoringStrategy.cs | Train() checks validation loss quality gate BEFORE neural training res |
| 96 | MEDIUM | correctness | NeuralScoringStrategy.cs | dropoutRng seeded with `1337 + _trainingGeneration` â€” _trainingGener |
| 97 | MEDIUM | correctness | LearnedScoringStrategy.cs | K-fold train/val split uses shuffled index positions as boundaries, no |
| 98 | MEDIUM | correctness | EnsembleScoringStrategy.cs | Improving trend branch computes sigmoidTarget but uses wrong midpoint  |
| 99 | MEDIUM | correctness | NeuralScoringStrategy.cs | Validation split size calculation can produce valCount > examples.Coun |
| 100 | MEDIUM | security | LearnedScoringStrategy.cs | Path traversal risk: _weightsPath is used without sanitization in TryL |
| 101 | MEDIUM | correctness | CandidateFeatures.cs | WriteToVector computes normalizedGenreCount with integer division when |
| 102 | MEDIUM | correctness | EnsembleScoringStrategy.cs | ApplyCohortFeedback: logger reads _sigmoidMidpointOffset OUTSIDE lock  |
| 103 | MEDIUM | performance | NeuralScoringStrategy.cs | ComputeMseLoss in Neural allocates 8 hidden-layer buffers on every val |
| 104 | MEDIUM | correctness | EnsembleScoringStrategy.cs | Metrics snapshot is added and trend computed in a second separate lock |
| 105 | MEDIUM | correctness | NeuralScoringStrategy.cs | Early stopping restores best weights unconditionally at line 1125, pot |
| 106 | MEDIUM | test-gap | NeuralScoringStrategy.cs | No test verifies that dropout gradients correctly zero out dropped neu |
| 107 | MEDIUM | correctness | TrashService.cs | ExtractOriginalName fails for items trashed in the same second with a  |
| 108 | MEDIUM | correctness | BackupValidator.cs | ValidateGrowthBaseline stops checking all entries after first key-leng |
| 109 | MEDIUM | correctness | PluginLogService.cs | AddEntry reads configuration outside the lock, creating a TOCTOU race |
| 110 | MEDIUM | performance | LinkRepairResult.cs | All count properties re-enumerate FileResults on every access |
| 111 | MEDIUM | correctness | AtomicFile.cs | WriteAllText catch-all rethrows on OperationCanceledException from non |
| 112 | MEDIUM | security | ArrIntegrationService.cs | Arr base URL reflected into log and error message without sanitization |
| 113 | MEDIUM | correctness | BackupValidator.cs | ValidateStringField checks value.Length (char count) against maxLength |
| 114 | MEDIUM | correctness | ArrIntegrationService.cs | HashSet comparer equality check uses Equals which may not work for all |
| 115 | MEDIUM | correctness | TransformationPatches.cs | Plugin.Instance?.Version.ToString() throws NullReferenceException when |
| 116 | MEDIUM | correctness | TrashService.cs | GetTrashSummary uses LINQ Sum over FileInfo for file sizes, allocating |
| 117 | MEDIUM | correctness | TrashService.cs | PathComparison property is computed on every call via OperatingSystem  |
| 118 | MEDIUM | correctness | ReasonResolver.cs | StripWatchedItemsForResponse omits LanguageProfile, SubtitleLanguagePr |
| 119 | MEDIUM | correctness | PreferenceBuilder.cs | GenreDistribution merge silently overwrites genres already in vector w |
| 120 | MEDIUM | correctness | ContentScoring.cs | NormalizeCriticRating returns 0.5 neutral for criticRating == 0 (a val |
| 121 | MEDIUM | correctness | Engine.cs | Cold-start check uses WatchedItems.Count == 0 but warm path filter use |
| 122 | MEDIUM | correctness | CollaborativeFilter.cs | LINQ Count() on HashSet in hot collaborative loop is O(N) instead of O |
| 123 | MEDIUM | correctness | Engine.cs | BuildCommunityPopularityForColdStart two-user gate is inconsistent wit |
| 124 | MEDIUM | performance | PreferenceBuilder.cs | ExpandGenreProximity calls .Distinct() and .ToArray() per watched item |
| 125 | MEDIUM | correctness | SimilarityComputer.cs | ComputeGenreSimilarity computes userNorm over entire genrePreferences  |
| 126 | MEDIUM | correctness | TemporalFeatures.cs | ComputeDayOfWeekAffinity and ComputeHourOfDayAffinity use UTC timestam |
| 127 | MEDIUM | correctness | DiversityReranker.cs | MMR score can be negative; first selected item's mmrScore comparison i |
| 128 | MEDIUM | security | Engine.cs | ResolveBatchGenerationFilePath uses Path.Join without canonicalization |
| 129 | MEDIUM | correctness | PreferenceBuilder.cs | ComputeProgressionMultiplier returns 1.0 when playedEps <= 0, even if  |
| 130 | MEDIUM | test-gap | ContentScoring.cs | ComputeContentNearestNeighborScore parallel-array mismatch degrades si |
| 131 | MEDIUM | incomplete | Engine.cs | SeriesProgressionBoost is hardcoded 0.0 but occupies a feature slot â€ |
| 132 | MEDIUM | bug | SeerrDiscoveryService.cs | Double enumeration of enrichmentCandidates in log statement |
| 133 | MEDIUM | bug | DiscoveryFeedbackStore.cs | RecordDismissed and RecordRequested do not update UserName on existing |
| 134 | MEDIUM | correctness | SeerrDiscoveryService.cs | Child account movie candidates from Animation genre are missing StampM |
| 135 | MEDIUM | correctness | ExternalCandidateFeatureBuilder.cs | PeopleSimilarity denominator uses Min(preferredPeople.Count, 5) â€” sc |
| 136 | MEDIUM | correctness | SeerrDiscoveryService.cs | Pagination termination uses stale page arithmetic when pageInfo is mis |
| 137 | MEDIUM | correctness | TmdbGenreMap.cs | ToJellyfinGenres checks MovieGenres then TvGenres â€” genre ID 16 (Ani |
| 138 | MEDIUM | bug | DiscoveryCacheService.cs | Outer IOException/JsonException catch in RemoveItemLocked sets _memory |
| 139 | MEDIUM | performance | DiscoveryFeedbackStore.cs | SaveInternal materialises a new list every time the per-user entry cou |
| 140 | MEDIUM | correctness | SeerrDiscoveryService.cs | Rate-limit delay in finally block runs even after an exception is thro |
| 141 | MEDIUM | correctness | SeerrDiscoveryService.cs | Year-window logic computes minYear as integer cast from avgYear - 15,  |
| 142 | MEDIUM | security | DiscoveryFeedbackStore.cs | Constructor falls back to empty string path when Plugin.Instance is nu |
| 143 | MEDIUM | correctness | SeerrDiscoveryService.cs | CreateClient does not validate that the base URL has a trailing slash  |
| 144 | MEDIUM | correctness | SeerrPermissionExtensions.cs | CanSelectQualityProfile double-calls HasPermission(Admin) redundantly |
| 145 | LOW | security | ConfigurationController.cs | Instance URL and Name values are logged verbatim in connection test wa |
| 146 | LOW | security | ConfigurationController.cs | TrashFolderPath stored with path traversal characters possible on Wind |
| 147 | LOW | correctness | BackupController.cs | Content-Length 0 silently bypasses the large-upload warning |
| 148 | LOW | correctness | DiscoveryController.cs | StackOverflowException cannot actually be caught â€” re-throw is dead  |
| 149 | LOW | correctness | UserDiscoveryController.cs | GetScript() does not dispose the manifest resource stream on NotFound  |
| 150 | LOW | design | UserDiscoveryController.cs | IsDiscoveryUserAccessEnabled reads from Plugin.Instance static â€” unt |
| 151 | LOW | correctness | RecommendationController.cs | GetAllRecommendations generates and saves results using configuredMax  |
| 152 | LOW | test-gap | ConfigurationRequestValidator.cs | TrashRetentionDays lower bound of 0 conflicts with documentation comme |
| 153 | LOW | design | ConfigurationResponse.cs | ApiKeyMask constant is internal â€” cannot be used in tests or by exte |
| 154 | LOW | correctness | WatchHistoryService.cs | Synthetic series WatchedItemInfo has RuntimeTicks=0 â€” latent divide- |
| 155 | LOW | correctness | WatchHistoryService.cs | WatchedSeriesCount assigned after BuildPeopleProfile completes â€” val |
| 156 | LOW | performance | WatchHistoryService.cs | Subtitle availableSubLanguages count uses same Distinct+Count pattern  |
| 157 | LOW | bug | WatchHistoryService.cs | Episode.SeriesId comparison against Guid.Empty â€” redundant check sin |
| 158 | LOW | correctness | RecommendationCacheService.cs | LoadResults returns null for both 'file not found' and 'file contains  |
| 159 | LOW | test-gap | RecommendationCacheServiceTests.cs | Test locates cache file via Directory.GetFiles glob â€” fragile agains |
| 160 | LOW | test-gap | WatchHistoryServiceTests.cs | 15% threshold boundary tests do not cover the exact boundary value (15 |
| 161 | LOW | test-gap | WatchHistoryServiceTests.cs | NormalizeLanguage has no unit tests despite 35 explicit branches feedi |
| 162 | LOW | test-gap | WatchHistoryServiceTests.cs | BuildLanguageProfiles has zero unit tests â€” chosen vs. forced distin |
| 163 | LOW | design | WatchHistoryService.cs | Magic constant 0.15 (15% progress threshold) is inlined with no named  |
| 164 | LOW | design | RecommendationCacheService.cs | SaveResults serialises full UserWatchProfile including all WatchedItem |
| 165 | LOW | test-gap | EngineFullPipelineTests.cs | GetAllRecommendations_MultipleUsers only asserts results.Count == 2, n |
| 166 | LOW | test-gap | NeuralScoringStrategyTests.cs | Score_DuringTraining_DoesNotThrow uses Task.Run without ConfigureAwait |
| 167 | LOW | test-gap | EnsembleScoringStrategyAdvancedTests.cs | Constructor_HeuristicWithDefaultPenaltyFloor_Throws verifies exception |
| 168 | LOW | correctness | PreferenceBuilderTests.cs | BuildGenreExposureAnalysis_InsufficientHistory uses only 1 WatchedItem |
| 169 | LOW | test-gap | LinkRepairSecurityTests.cs | DryRun test for .strm reads file back with _fileSystem.File.ReadAllTex |
| 170 | LOW | test-gap | BackupServiceTests.cs | Validate_ValidTaskModes_NoWarnings asserts Assert.Empty(result.Warning |
| 171 | LOW | correctness | TrainingFeatureComputer.cs | `AddAggregatedSeriesExample`: `genreList = allGenres.ToList()` materia |
| 172 | LOW | correctness | TrainingDataBuilder.cs | Phase 2 organic standalone label: `{ Played: false, PlaybackPositionTi |
| 173 | LOW | correctness | TrainingDataBuilder.cs | Phase 2 organic standalone: `CriticRating` is hardcoded to `null` in ` |
| 174 | LOW | test-gap | TrainingDataBuilderTests.cs | TrainingDataBuilderTests has only one test covering Phase 3 determinis |
| 175 | LOW | test-gap | TrainingFeatureComputerTests.cs | `AddAggregatedSeriesExample` tests do not cover the `LibraryAddedRecen |
| 176 | LOW | correctness | DiscoveryFeedbackExampleBuilder.cs | `BuildDiscoveryExamples` returns `(examples, examples.Count)` where `e |
| 177 | LOW | correctness | PreferenceBuilder.cs | `BuildGenrePreferenceVector`: `GenreDistribution` merge skips genres a |
| 178 | LOW | correctness | PluginConfiguration.cs | NormalizeAlphaRange swaps backing fields directly, bypassing ClampAndR |
| 179 | LOW | correctness | Plugin.cs | Version.ToString() called during index.html write â€” Guid.Parse calle |
| 180 | LOW | correctness | SeerrIntegrationService.cs | CRLF validation in TestConnectionAsync is redundant with CreateClient' |
| 181 | LOW | bug | BatchFallbackHelper.cs | AggregateException OCE unwrap in onFailure callback discards the origi |
| 182 | LOW | design | PluginServiceRegistrator.cs | Plugin.Instance null-conditional access in DI factory lambdas creates  |
| 183 | LOW | correctness | UserActivityInsightsService.cs | mostRecent DateTime comparison uses DateTime? > DateTime? which silent |
| 184 | LOW | test-gap | SeerrIntegrationService.cs | No test coverage for the infinite-pagination guard (missing) and the p |
| 185 | LOW | design | Plugin.cs | OnUninstalling calls both UnregisterFileTransformation and UpdateIndex |
| 186 | LOW | correctness | LearnedScoringStrategy.cs | Saturation guard condition has inverted semantic in the comments but c |
| 187 | LOW | correctness | EnsembleScoringStrategy.cs | Public ComputeSigmoidAlpha overload ignores its own midpoint parameter |
| 188 | LOW | correctness | CandidateFeatures.cs | IsAbandoned flag uses CompletionRatio > 0.0 as a guard but CompletionR |
| 189 | LOW | performance | NeuralScoringStrategy.cs | Permutation importance computation materializes .OrderByDescending() w |
| 190 | LOW | correctness | EnsembleScoringStrategy.cs | stateChanged flag in failed-training branch is always true â€” the var |
| 191 | LOW | correctness | NeuralScoringStrategy.cs | He initialization uses sqrt(6/fan_in) (uniform) rather than the standa |
| 192 | LOW | correctness | EnsembleScoringStrategy.cs | Heuristic floor equality check at 1.0 uses exact floating-point equali |
| 193 | LOW | incomplete | RankingMetrics.cs | GetTopKIndices sorts all N indices even for small K â€” O(N log N) whe |
| 194 | LOW | correctness | BackupService.cs | RestoreConfiguration silently returns without setting ConfigurationRes |
| 195 | LOW | correctness | BackupValidator.cs | ScriptPattern regex does not account for HTML entity encoding or Unico |
| 196 | LOW | correctness | TrashService.cs | PurgeExpiredTrash cutoff uses subtraction from utcNow, treating retent |
| 197 | LOW | correctness | LinkRepairService.cs | MaxVisitedDirectories guard is checked after Add, so the 50,001st dire |
| 198 | LOW | correctness | TrashService.cs | GetTrashContents silently swallows all IO errors, returning partial re |
| 199 | LOW | correctness | PluginLogService.cs | GetConfiguredMinLevel swallows all exceptions from GetConfiguration() |
| 200 | LOW | design | TrashService.cs | CanWriteDirectory probe file is not deleted if File.Delete throws |
| 201 | LOW | correctness | BackupService.cs | LoadJsonFile does not handle InvalidDataException or ArgumentException |
| 202 | LOW | correctness | ArrIntegrationService.cs | Path.GetFileName with trailing slash stripped via TrimEnd handles empt |
| 203 | LOW | test-gap | BatchFallbackHelper.cs | AggregateException OCE rethrow uses First() which throws if flattening |
| 204 | LOW | correctness | ContentScoring.cs | ComputeAverageYear uses long sum but int year values â€” unnecessary w |
| 205 | LOW | correctness | CollaborativeFilter.cs | Collaborative co-occurrence includes series IDs from BuildCombinedWatc |
| 206 | LOW | performance | SimilarityComputer.cs | TryBuildPeopleLookupBatch materializes candidates.Select(c => c.Id).To |
| 207 | LOW | correctness | DiversityReranker.cs | ApplyDiversityReranking performs two separate OrderByDescending sorts  |
| 208 | LOW | correctness | Engine.cs | Collection<RecommendationResult> wraps ConcurrentBag snapshot â€” redu |
| 209 | LOW | design | Engine.cs | _batchGeneration is int but _publicationSequence is long â€” asymmetri |
| 210 | LOW | correctness | DiscoveryFeedbackEntry.cs | GetStatus() priority order: Dismissed is evaluated after Requested, bu |
| 211 | LOW | correctness | SeerrDiscoveryService.cs | Reason string concatenation format is inconsistent â€” ReasonKey is us |
| 212 | LOW | correctness | SeerrDiscoveryService.cs | userExcluded starts as a reference to the shared excludedTmdbIds; new  |
| 213 | LOW | correctness | SeerrDiscoveryService.cs | Max-pages safety cap logs a warning but marks fetchComplete=false only |
| 214 | LOW | performance | SeerrDiscoveryService.cs | GetServiceInfoAsync and GetServiceInfoWithStatusAsync are near-identic |
| 215 | LOW | correctness | NullableDateTimeConverter.cs | DateTimeStyles.RoundtripKind with DateTime.TryParse may not correctly  |
| 216 | LOW | correctness | SeerrDiscoveryService.cs | FindSeerrUserByJellyfinId ignores JellyfinUserId strings that are neit |
| 217 | LOW | test-gap | ParentalRatingHelper.cs | No test coverage for ParentalRatingHelper boundary values maxParentalR |
| 218 | LOW | test-gap | DiscoveryCacheService.cs | ReinsertAtOriginalIndices is only exercised via rollback paths that re |
| 219 | LOW | design | DiscoveryFeedbackStore.cs | DiscoveryFeedbackStore uses synchronous Lock while DiscoveryCacheServi |
| 220 | INFO | correctness | UserDiscoveryController.cs | DismissItem validates dto null AFTER extracting userId â€” null dto wo |
| 221 | INFO | correctness | ConfigurationController.cs | TestSeerrConnectionAsync skips test when SeerrApiKey is whitespace â€” |
| 222 | INFO | design | PluginConfiguration.cs | RadarrInstances and SonarrInstances use init-only setter â€” prevents  |
| 223 | INFO | design | HelperCleanupTask.cs | Sub-task instances created fresh on every ExecuteAsync call â€” logger |
| 224 | INFO | correctness | SeerrDiscoveryService.cs | GenerateDiscoveryRecommendationsAsync does not pass the CancellationTo |
| 225 | INFO | correctness | SeerrDiscoveryService.cs | seerrUserId guard uses pattern > 0 but serverId and profileId guards u |