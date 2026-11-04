# Code Quality audit — 2026-11-04

Baseline: `main` at `7134a5b564c049c1315552e31e55623062cda66a` (PR #346).
Work branch: `fix/code-quality-backlog-2026-11-04`.

## Separate analysis inventories

The live APIs reported:

| Analysis | Baseline result |
| --- | --- |
| SonarCloud | Quality gate OK; 0 bugs, vulnerabilities, code smells and security hotspots |
| CodeQL security scanning | 0 open alerts; latest main analysis completed without errors |
| GitHub Code Quality standard findings | 217 open findings: 12 warnings and 205 notes |

GitHub Code Quality uses its own quality queries and finding inventory. A green
SonarCloud gate or successful security scan does not clear that inventory.
The Code Quality scheduled scan also ran successfully on the baseline commit.

All 217 standard finding locations were checked against the baseline source.
207 locations receive code changes; the other 10 are documented below as
intentional behavior or transferred resource ownership. This is an audit of
the code, **not a claim that GitHub has closed 207 findings**. The standard
dashboard analyzes the default branch, so changes must be merged and scanned
before that status can be verified. No findings were dismissed and no analysis
rules, severities, exclusions or thresholds were weakened.

## Changes

| Rule | Count | Disposition |
| --- | ---: | --- |
| `cs/path-combine` | 181 | Use `Path.Join` for the audited relative test-path segments |
| `cs/static-field-written-by-instance` | 17 | Centralize thread-owned buffer preparation in static helpers |
| `cs/equality-on-floats` | 7 | Make exact sentinel/label equality explicit; use the binary dropout mask's inactive range |
| `cs/local-not-disposed` | 2 | Explicit test-scope disposal for the backup request body and mocked Seerr page response |

### Test paths

Every reported `Path.Combine` call was inspected. Child segments are relative
file names, relative traversal test inputs, known constants, or generated
relative names. None requires a rooted later argument to override its base.
`Path.Join` preserves these test paths while expressing the intended
non-overriding concatenation. It does not validate containment; the existing
traversal, malformed-path, symlink and relocation assertions remain in place.
No production path resolution was changed.

The following paths are relative to `Jellyfin.Plugin.JellyfinHelper.Tests/`:

| File | Count | Finding numbers |
| --- | ---: | --- |
| `Api/DiscoveryControllerExtendedTests.cs` | 1 | 1237 |
| `ContributingDocCoverageTests.cs` | 4 | 1238, 1556, 2019–2020 |
| `PluginTests.cs` | 33 | 2021–2053 |
| `ScheduledTasks/CleanEmptyMediaFoldersTaskTests.cs` | 3 | 2054–2056 |
| `ScheduledTasks/CleanTrickplayTaskTests.cs` | 2 | 2057–2058 |
| `ScheduledTasks/HelperCleanupTaskErrorHandlingTests.cs` | 8 | 1852–1859 |
| `Services/Cleanup/CleanupConfigHelperTests.cs` | 1 | 2059 |
| `Services/Cleanup/TrashControllerRelocateTests.cs` | 33 | 1306–1322, 1334–1338, 2060–2070 |
| `Services/Cleanup/TrashServiceRelocateTests.cs` | 45 | 398–427, 1339–1353 |
| `Services/Common/AtomicFileTests.cs` | 2 | 1354–1355 |
| `Services/FileSystemHelperErrorHandlingTests.cs` | 8 | 1356–1363 |
| `Services/FolderBrowser/FolderBrowserServiceSecurityTests.cs` | 11 | 1379, 2071–2080 |
| `Services/PathValidatorTests.cs` | 17 | 2081–2097 |
| `Services/Recommendation/Engine/EngineLibraryMetadataTests.cs` | 1 | 1533 |
| `Services/Recommendation/Playlist/RecommendationPlaylistServiceDeletionFallbackTests.cs` | 6 | 1474–1479 |
| `Services/Recommendation/Playlist/RecommendationPlaylistServiceSecurityTests.cs` | 2 | 1480–1481 |
| `Services/Recommendation/Scoring/EnsembleScoringStrategyStateTests.cs` | 1 | 2098 |
| `Services/Recommendation/Scoring/LearnedScoringStrategyRobustnessTests.cs` | 1 | 1483 |
| `Services/Recommendation/Scoring/NeuralScoringStrategyTests.cs` | 2 | 2099–2100 |

### Neural scratch buffers

Findings 1736–1752 refer to `[ThreadStatic]` arrays in
`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/NeuralScoringStrategy.cs`.
These buffers belong to the current thread, not an individual model. They must
not become shared instance buffers or lose their thread-local storage.
Static helpers now express that ownership and share initialization/clearing
between `Score` and `ScoreVector`. The calculation, lock boundaries and buffer
clearing remain unchanged. Tests compare both paths against fresh-buffer
explanations, including alternating differently trained models on one thread.

### Floating-point values

Findings 1187–1189 concern feedback metadata whose exact zero means missing.
`Equals(0.0)` deliberately preserves exact equality, including negative zero.
An epsilon comparison would wrongly overwrite valid small positive values.
Regression cases cover zero, negative zero, `1e-12` and `double.Epsilon`.

Findings 1190–1191 concern assigned training labels, not approximate numerical
results; exact label equality remains appropriate. Finding 1676 similarly
checks the exact disabled-neural sentinel. Their equality intent is explicit.
Finding 2014 concerns a generated binary dropout mask (0 or 1); its inactive
test now matches the `<= 0.0` checks used by the other hidden layers without
changing the backpropagation arithmetic.

### Test-owned resources

Finding 1154: `BackupControllerExtendedTests` now owns its request-body stream
with `using`; a standalone `DefaultHttpContext` does not run the server's
response-completion pipeline.

Finding 2101: `SeerrIntegrationServiceTests` now owns the successful first-page
response in the test scope, including exceptional pagination outcomes. The
mock returns the live response; it is never disposed inside the callback
before the service can read it.

## Findings intentionally retained

These are **still potentially open in GitHub**, not silently dismissed or
suppressed. Review them individually if a dashboard dismissal is desired.

| Finding | Rule / file | Reason |
| --- | --- | --- |
| 1844–1845 | `cs/local-not-disposed`; `Jellyfin.Plugin.JellyfinHelper.Tests/Services/Seerr/Discovery/SeerrDiscoveryServicePersistenceFailureTests.cs`, `DetailThrowingHttpHandler.SendAsync` | A fresh response is returned through `Task.FromResult` to the HTTP caller. Disposing it in `SendAsync` would return unusable content. `GetServiceInfoWithStatusAsync` and `EnrichServerDetailAsync` in `SeerrDiscoveryService.cs` own their responses with `using`. |
| 2102 | `cs/local-not-disposed`; `Jellyfin.Plugin.JellyfinHelper.Tests/TestFixtures/ControllerTestFactory.cs`, `AddJsonBodyToController` | The returned controller owns the live request body; response disposal is registered. Standalone tests must dispose `controller.Request.Body`, as `FixtureFactoryTests` does. The factory contract now states this explicitly. A local `using` would break every caller. |
| 2015 | `cs/linq/missed-where`; `Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Engine.cs`, `ComputeCollaborativeMax` | The loop updates a running maximum; filtering against changing accumulator state is not a clearer query. It already filters non-finite inputs and preserves the zero floor. |
| 2016 | `cs/linq/missed-where`; `Engine.cs`, `ComputeFacetIdf` | The guard performs case-insensitive deduplication with `seen.Add`. It is stateful document-frequency accounting, not just a pure filter. |
| 2017 | `cs/linq/missed-where`; `Engine.cs`, `ResolveWatchedSiblingCount` | Each iteration performs one `TryGetValue` and updates a running maximum. Keep the single-pass lookup and zero floor instead of filtering against mutable state. |
| 2018 | `cs/linq/missed-where`; `Engine.cs`, `BuildWatchedTmdbItems` | `TryGetValue` both filters and obtains the value consumed by the loop. A separate `Where`/indexer would repeat the dictionary lookup. |
| 1198 | `cs/linq/missed-where`; `Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/SimilarityComputer.cs`, `ComputePeopleSimilarity` | The hot scoring loop uses a single lookup and accumulates positive weights in encounter order. Extra projections/filter iterators provide no correctness improvement. |
| 1713 | `cs/linq/missed-where`; `Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/WatchHistoryService.cs`, `BuildSeriesTmdbLookup` | `TryGetTmdbId` validates and parses the value used by the body. Retain one parse per series instead of splitting the guard and consumption. |
| 1975 | `js/unneeded-defensive-code`; `Jellyfin.Plugin.JellyfinHelper/js/discovery-sidebar.js`, `waitForApi` | `onTimeout` is optional. Removing its type guard throws on timeout for an omitted or non-callable callback. Regression cases explicitly exercise undefined, null, false, zero, empty string and an object. |

## The three quoted AI suggestions

The pasted May code snippets do not match the November baseline. The standard
findings REST API does not establish the current state of those AI entries.

* `Api/DiscoveryRequestDto.cs`: model validation happens **before** controller
  normalization. Removing validation or requiring lowercase would regress
  invalid-input rejection or the supported `Movie`/`MOVIE`/`TV` inputs.
  The baseline already uses culture-independent ASCII casing and regression
  tests, including Turkish culture. Keep this validation.
* `ScheduledTasks/CleanTrickplayTask.cs`: already uses
  `MoveToTrashWithResult`, separating success from bytes moved. The baseline
  tests both successful empty moves and failed moves, including a failure
  reporting nonzero bytes. The quoted counting bug is already fixed.
* `Services/Seerr/Discovery/SeerrDiscoveryService.cs`: the baseline controller
  no longer contains the quoted negative-ID checks. DTO annotations validate
  HTTP inputs, while the public service retains its own boundary guard.
  Profile authorization is a separate controller responsibility; extracting
  another validator merely to satisfy the old suggestion is unnecessary.

## Verification

* All 181 path changes were mechanically compared with the baseline finding
  locations; only those `Path.Combine` calls were replaced.
* New regression cases preserve exact metadata sentinels, thread-local model
  isolation and optional timeout handling.
* Release build: passed with 0 warnings and 0 errors.
* Targeted backend tests: 365 passed, none failed or skipped.
* Full backend suite: 6,951 passed, none failed or skipped, including security
  and performance tests. Both runs used xUnit's supported in-process DLL runner
  through `dotnet`; Windows denied execution of the generated native app host.
  No project or CI runner settings were changed.
* UI coverage: the initial run exceeded the two-minute command limit; a rerun
  with a longer limit was still pending when the first push was requested.
* Docker E2E: not run locally because the Docker Linux engine was unavailable.
  The PR pipeline must verify UI coverage, E2E, SonarCloud and Code Quality.

The first push was explicitly requested after code review and successful local
backend verification, without waiting for the remaining UI run. GitHub finding
closure remains unverified until these changes reach the default branch.

References:
[Code Quality inventories](https://docs.github.com/en/code-security/how-tos/maintain-quality-code/fix-backlog-findings),
[AI findings](https://docs.github.com/en/code-security/how-tos/maintain-quality-code/fix-findings-in-recent-merges),
[Code Quality REST API](https://docs.github.com/en/rest/code-quality/code-quality).
