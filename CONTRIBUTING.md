<!--
  CONTRIBUTING.md. Contributor guidelines for the Jellyfin Helper plugin.
  This file uses UTF-8 encoding and may contain emoji characters.
  If your editor shows garbled characters, ensure UTF-8 is set.
-->

# Contributing to Jellyfin Helper

Thank you for your interest in contributing! This guide covers everything you need to get started.

## Table of Contents

- [Development Setup](#development-setup)
- [Building the Plugin](#building-the-plugin)
- [Testing](#testing)
- [Architecture Overview](#architecture-overview)
- [Configuration Page Build System](#configuration-page-build-system)
- [Adding a New Feature](#adding-a-new-feature)

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Jellyfin Server 12.0.x](https://jellyfin.org/docs/general/administration/installing) (for runtime testing)
- Recommended: [JetBrains Rider](https://www.jetbrains.com/rider/) or [Visual Studio 2022+](https://visualstudio.microsoft.com/)
- Recommended: [Node.js 18+](https://nodejs.org/) (for JavaScript linting)

### Clone and Build

```bash
git clone https://github.com/JellyPlugins/jellyfin-helper.git
cd jellyfin-helper
dotnet build
```

### Install for Testing

After building, copy the output DLL to your Jellyfin plugin directory:

```bash
# Linux/macOS (local user install)
cp Jellyfin.Plugin.JellyfinHelper/bin/Debug/net10.0/Jellyfin.Plugin.JellyfinHelper.dll \
   ~/.local/share/jellyfin/plugins/JellyfinHelper/

# Linux (system service / package install - path may vary by distro)
# sudo cp Jellyfin.Plugin.JellyfinHelper/bin/Debug/net10.0/Jellyfin.Plugin.JellyfinHelper.dll \
#    /var/lib/jellyfin/plugins/JellyfinHelper/

# Windows
copy Jellyfin.Plugin.JellyfinHelper\bin\Debug\net10.0\Jellyfin.Plugin.JellyfinHelper.dll ^
     %LOCALAPPDATA%\jellyfin\plugins\JellyfinHelper\
```

Restart Jellyfin after copying.

## Building the Plugin

```bash
# Debug build
dotnet build

# Release build (used for distribution)
dotnet build -c Release

# Build and run tests
dotnet test
```

### Build Output

The build produces:

- `Jellyfin.Plugin.JellyfinHelper.dll` (plugin assembly with embedded resources)
- `configPage.html` (generated configuration page, embedded in the DLL at build time)


See [Configuration Page Build System](#configuration-page-build-system) for how the config page is composed.

## Testing

### Running Tests

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test -v normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~BackupServiceTests"

# Run specific test
dotnet test --filter "FullyQualifiedName~CreateBackup_IncludesAllSettings"
```

### End-to-End Tests

The `dotnet test` suite above covers logic in isolation. A separate
**end-to-end suite** (`test/e2e/`) runs the built plugin inside a real
Jellyfin 12 container with mock Radarr/Sonarr/Seerr servers, and drives it the
way a user would: settings, scheduled-task modes, backup import/export, trends,
trash, and every dashboard tab (including the unsaved-changes dialog and log
download). It also covers hardening / edge cases (broken backups, invalid URLs,
traversal guards, out-of-range values).

Requires Docker + Docker Compose and Node 20+ (no host ffmpeg needed, media is
generated inside the container).

```bash
# One command: build plugin → start stack → set up → run all tests → tear down
bash test/e2e/scripts/run.sh

# Faster iteration (reuse the last build)
bash test/e2e/scripts/run.sh --no-build

# Leave the stack running afterwards to poke around (http://localhost:8096)
bash test/e2e/scripts/run.sh --keep
```

It runs automatically on every PR via `.github/workflows/e2e.yml`. See
[`test/e2e/README.md`](test/e2e/README.md) for architecture and
[`test/e2e/COVERAGE.md`](test/e2e/COVERAGE.md) for the coverage matrix.

### Test Structure

Tests mirror the source structure:

```text
Jellyfin.Plugin.JellyfinHelper.Tests/
├── PluginServiceRegistratorTests.cs      # DI-container smoke test: every registered service must resolve
├── PluginTests.cs                        # Bootstrap: Instance publishing, GetPages, UpdateIndexHtml idempotency, OnUninstalling cleanup guards
├── Api/                           # Controller tests
│   ├── ArrIntegrationControllerTests.cs
│   ├── ArrIntegrationControllerExtendedTests.cs      # Index bounds, 502 with named instance, trash exclusion, partial-config 400 contract
│   ├── BackupControllerTests.cs
│   ├── BackupControllerExtendedTests.cs               # Malformed JSON → 400, null body, chunk-loop MaxSize defence, whitespace-only body
│   ├── BackupControllerErrorHandlingTests.cs          # Service-layer failures surfaced as the right status codes (mocked IBackupService)
│   ├── ConfigurationControllerTests.cs               # Key-masking: non-empty → fixed-length mask sentinel, empty stays empty, sentinel preserves stored key, PluginLogLevel TOCTOU
│   ├── ConfigurationResponseTests.cs                 # ConfigurationResponse.FromConfig + MaskedArrInstanceConfig: masking, field pass-through, real key never in response
│   ├── DiscoveryControllerTests.cs
│   ├── DiscoveryControllerExtendedTests.cs           # Seerr users/services, request submission, filter logic, feedback-store error paths
│   ├── FolderBrowserControllerTests.cs               # Root/list/validate flows and library-path resolution
│   ├── ResponseDtoTests.cs                           # Default values, round-trip, null-safety and collection-default coverage for all 17 typed response DTOs
│   ├── ModelBindingLogFilterTests.cs                 # IAsyncActionFilter contract; Order = int.MinValue lock; drives filter directly
│   ├── PingControllerTests.cs                        # 200 with { ok, plugin, version }
│   ├── RecommendationControllerTests.cs
│   ├── RecommendationControllerDiagnosticsTests.cs      # GET /Recommendations/Diagnostics/Ensemble: 200 populated DTO, Available=false on null, 503 when deactivated
│   ├── TrashControllerTests.cs
│   ├── UserActivityControllerTests.cs
│   ├── UserDiscoveryControllerTests.cs
│   ├── UserDiscoveryControllerAccessEnabledTests.cs  # Access gate ENABLED - request validation and permission surfaces
│   ├── UserDiscoveryControllerSubmitTests.cs         # SubmitMyRequest + DismissItem with gate ENABLED
│   └── ...
├── Configuration/                 # Config serialization tests
│   ├── PluginConfigurationSerializationTests.cs
│   └── TaskModeTests.cs
├── PluginPages/                   # HTML composition tests
│   ├── ConfigPageTestBase.cs      # Shared base loading configPage.html + README
│   ├── ConfigPageHtmlTests.cs     # Top-level page structure + README hygiene
│   ├── ConfigPageTemplateTests.cs # Template shell / placeholder / metadata
│   ├── MainHtmlTests.cs           # Main.js: bootstrap, tab switching, page lifecycle
│   ├── SharedHtmlTests.cs         # Shared.js: API wrappers, i18n, formatting, tree
│   ├── OverviewHtmlTests.cs       # Overview tab
│   ├── CodecsHtmlTests.cs         # Codecs tab (donut charts, path map)
│   ├── HealthHtmlTests.cs         # Health tab (orphan/missing detection)
│   ├── TrendsHtmlTests.cs         # Trends tab (growth timeline + insights)
│   ├── SettingsHtmlTests.cs       # Settings tab (task modes, trash, Seerr)
│   ├── LogsHtmlTests.cs           # Logs tab (viewer, auto-refresh, download)
│   ├── ArrIntegrationHtmlTests.cs # Arr tab (Radarr/Sonarr/Lidarr wiring)
│   ├── DiscoverHtmlTests.cs       # Recommendations tab surface structure
│   ├── RecommendationsHtmlTests.cs# Recommendations.js: cache, XSS, popup, TTL
│   └── FolderBrowserHtmlTests.cs  # Server-side folder picker dialog
├── ScheduledTasks/                # Task execution tests
│   ├── CleanTrickplayTrashExclusionTests.cs              # Trash folder excluded from trickplay scan
│   ├── CleanOrphanedSubtitlesTaskProcessLocationTests.cs # ProcessLocation: library setup, GetDirectories, file-leaf enumeration
│   ├── CleanOrphanedSubtitlesTaskDirectDeleteTests.cs   # Direct File.Delete path: success, failure, trash-path fallthrough, nested enumeration errors
│   ├── HelperCleanupTaskErrorHandlingTests.cs           # Per-step failure isolation: one failing cleanup step must not abort the rest
│   ├── RecommendationsTaskErrorHandlingTests.cs         # Engine/training failures logged and swallowed so the task run completes
│   ├── RepairLinksTaskTests.cs                           # Dry-run flag, cancellation, progress reporting; no filesystem I/O
│   ├── RecommendationsTaskTests.cs
│   ├── UserActivityUpdateTaskTests.cs
│   ├── CleanupCancellationTests.cs             # CancellationToken propagates: pre-cancelled token throws OperationCanceledException in all three cleanup task types (EmptyFolders, Trickplay, OrphanedSubtitles); plus mid-enumeration cancellation aborts the directory walk promptly instead of after full traversal
│   └── ...
├── Services/
│   ├── DateTimeNormalizationTests.cs      # UTC coercion helper: guards against Local→SpecifyKind bugs in cache timestamps
│   ├── FileSystemHelperErrorHandlingTests.cs # Directory-size walk skips entries whose Length throws (Windows-gated) and swallows access-denied
│   ├── LibraryPathResolverErrorHandlingTests.cs # GetFullPath fallback returns the original path when normalization throws
│   ├── Activity/                  # User activity service tests
│   ├── Arr/                       # Arr integration tests
│   │   └── ArrIntegrationServiceTests.cs               # Timeout → LogWarning (not LogError); parity with TestConnectionAsync
│   ├── Backup/                    # Backup/restore tests
│   │   ├── BackupServiceTests.cs                       # Validation, sanitization + credential contracts: ContainsSecrets, CredentialsChanged, audit Warning
│   │   ├── BackupServicePerformanceTests.cs
│   │   ├── BackupServiceRestoreConfigTests.cs          # RestoreBackup round-trip: language fallback, clamping, task-mode rejection, credential preserve/overwrite
│   │   ├── BackupServiceErrorHandlingTests.cs          # Redaction loops, partial-apply warn+rethrow, JSON save/load dir-create and size/corrupt guards
│   │   ├── BackupSanitizerArrInstancesTests.cs         # Arr-instance credential redaction across multiple configured instances
│   │   ├── BackupValidatorSecurityTests.cs             # SECURITY: trash-path null-byte/newline injection and script-pattern guards
│   │   ├── BackupSanitizerTests.cs                     # Timeline-trimming path: under-limit no-op, over-limit trims to MaxTimelineDataPoints, newest points kept, result sorted ascending
│   │   └── BackupValidatorTests.cs                     # SeerrCleanupAgeDays range (null=absent, 0=immediate, negative=error, >Max=error); CreatedAt timezone warnings (Unspecified emits warning, Utc/Local silent)
│   ├── Cleanup/                   # Cleanup task tests
│   │   ├── TrashControllerAccessTests.cs  # CheckAccess API endpoint tests (permission probing)
│   │   ├── TrashControllerRelocateTests.cs # Trash path relocation API endpoint tests
│   │   ├── TrashServiceAccessTests.cs     # CheckPathAccess permission probing tests
│   │   ├── TrashServiceGuardTests.cs      # Defense-in-depth: prevent re-trashing items already in trash
│   │   ├── TrashServicePathLengthTests.cs # ResolveCollision stays within OS MAX_PATH (Windows 259 / Linux 4095)
│   │   ├── TrashServiceRelocateTests.cs   # RelocateTrashContents unit tests (move, collision, safety)
│   │   ├── TrashServiceFailureTests.cs    # Delete failures counted as Failed not Deleted (Windows-gated IO error simulation)
│   │   ├── TrashServiceInternalHelpersTests.cs # TruncateToSize / MeasureString / ExtractOriginalName / TryParseTrashTimestamp edge cases
│   │   └── TrashServicePathAccessTests.cs # Permission-denied branches of CheckPathAccess/GetTrashSummary/GetTrashContents (deny ACL / UnixFileMode, root-bypass probe)
│   ├── Common/                    # Shared cross-service helper tests
│   │   ├── AtomicFileTests.cs             # UTF-8 no-BOM, temp-file cleanup, transient-IO retry, async CancellationToken
│   │   ├── BatchFallbackHelperTests.cs    # try-batch/fall-back: cancellation propagates, non-fatal exceptions degrade gracefully
│   │   ├── ExceptionExtensionsTests.cs    # IsFatal: OOM + StackOverflow → true; all other exception types → false
│   │   ├── HttpResponseReaderTests.cs     # Size-bounded read: under/at/over limit (EOF probe at exact limit), Content-Length fast-reject, null, cancellation
│   │   ├── LimitedStreamTests.cs          # Direct stream tests: capability flags, sync/async read paths, over-limit throw, NotSupported members
│   │   ├── SsrfGuardTests.cs              # Cloud metadata hosts blocked (incl. IPv6/case-insensitive); LAN/loopback/public allowed
│   │   └── ReparsePointGuardTests.cs      # Fail-closed guard: non-existent/real-dir throws, delete action never invoked, entry left unchanged
│   ├── ConfigAccess/              # Configuration access tests
│   ├── FileTransformation/        # File Transformation plugin integration tests
│   │   ├── DiscoveryScriptTagTests.cs      # Build() well-formed HTML, RemovalRegex round-trips, must not eat unrelated script tags
│   │   ├── DiscoverySidebarInjectionServiceTests.cs # Startup hosted service: StartAsync re-injects, idempotent (no stacked tags), null Plugin.Instance is a no-op, StopAsync completes
│   │   ├── PatchRequestPayloadTests.cs     # "contents" (lowercase-camel) round-trip for File Transformation payloads
│   │   └── TransformationPatchesTests.cs   # IndexHtml callback: null/empty-Contents, idempotent re-serving, case-insensitive </BODY>
│   ├── FolderBrowser/             # Server-side folder browsing tests
│   │   ├── FolderBrowserDtoTests.cs        # DTO defaults, mutability, reference-equality (guards against accidental record conversion)
│   │   ├── FolderBrowserServiceTests.cs    # GetRoots per-OS, ValidatePath (traversal/null-byte/access-denied), GetChildren (symlinks)
│   │   └── FolderBrowserServiceSecurityTests.cs # SECURITY: directory-traversal and access-denied enumeration guards
│   ├── Link/                      # Link repair tests
│   │   └── SymlinkHelperTests.cs           # Real-filesystem integration; graceful skip without privileges; meta-test ensures Linux CI runs the branch
│   ├── PluginLog/                 # Plugin log tests
│   ├── Seerr/                     # Seerr integration tests
│   │   ├── SeerrIntegrationServiceTests.cs             # Connection/cleanup contract; FormatException guard: non-ASCII/spaced API keys must not throw
│   │   ├── SeerrIntegrationServiceErrorHandlingTests.cs # Cancellation-vs-timeout paths, null-results fail-closed, CRLF header-injection guard
│   │   ├── SeerrMediaDetailsTests.cs
│   │   ├── SeerrRequestPageTests.cs                    # Null-coalescing on Results; non-null same-reference contract; reassignment clears to empty
│   │   └── Discovery/            # Seerr Discovery tests
│   │       ├── DiscoveryCacheServiceTests.cs            # Disk + memory persistence; per-test real file to avoid cross-test contamination
│   │       ├── DiscoveryCacheServiceConstructionTests.cs # Constructor guards and directory-creation behaviour
│   │       ├── DiscoveryFeedbackStoreTests.cs
│   │       ├── DiscoveryFeedbackStoreConstructionTests.cs # NormalizeMediaType defaults, directory creation, graceful save degradation
│   │       ├── DiscoveryFeedbackExampleBuilderLabelTests.cs # Label assignment, latest-interaction timestamp, people-similarity parity
│   │       ├── SeerrDiscoveryServiceGenerationTests.cs   # GenerateDiscoveryRecommendationsAsync pipeline: guards, child/language routing, exclusions, credits, Arr exclusion
│   │       ├── SeerrDiscoveryServicePersistenceFailureTests.cs # Feedback/cache persistence failures degrade gracefully
│   │       ├── TmdbGenreMapReverseLookupTests.cs         # Reverse id→name lookup: known ids, unknown fallthrough
│   │       ├── DiscoveryRecommendationTests.cs         # DTO setter guards: Score/TmdbRating/Popularity clamp, non-finite→0
│   │       ├── DiscoveryRegressionTests.cs              # v2.1.0.3 regressions (ServerId=0, profile dedup, MissingMethodException)
│   │       ├── ExternalCandidateFeatureBuilderTests.cs  # inference↔training feature parity (genre-exposure + popularity skew)
│   │       ├── ExternalCandidateFeatureBuilderExtendedTests.cs # Null guards, case-insensitive people matching, null EffectiveReleaseDate → 0.5 RecencyScore, TV/movie branch coverage
│   │       ├── ExternalCandidateFeatureImputationTests.cs # Gap A mean-imputation: continuous placeholders → training means, bools stay false, computed features untouched, null/wrong-length means = legacy constants
│   │       ├── NullableDateTimeConverterTests.cs        # Empty-string / malformed TMDb dates degrade to null instead of JsonException
│   │       ├── ParentalRatingHelperTests.cs
│   │       ├── SeerrDiscoveryDtoTests.cs                # DTO wire contract: property names, defaults, round-trip
│   │       ├── SeerrDiscoveryServiceTests.cs
│   │       ├── SeerrDiscoveryServiceHelperTests.cs      # Pure-static helpers: StampMediaType, BuildGenreIdList, GetPrimaryLanguageForDiscovery
│   │       ├── SeerrDiscoveryServiceHttpTests.cs        # HTTP surface via scripted HttpMessageHandler: SubmitRequestAsync, GetServiceInfoAsync, user resolution, permissions
│   │       ├── SeerrDiscoveryGenerationTests.cs         # Task-mode orchestration: Deactivate short-circuits, DryRun never writes feedback, cancellation propagates
│   │       ├── SeerrDiscoveryServiceUserResolutionTests.cs # FindSeerrUserByJellyfinId, BuildAllowedProfileList
│   │       ├── SeerrDiscoveryReconcileTests.cs         # ReconcileRequestedItemsAsync: records+marks cached items also requested out-of-band, per-user, media-type normalization, all fail-safe/pagination branches
│   │       ├── SeerrDiscoveryReconcileFailureTests.cs  # Reconcile fail-safe catches: throwing feedback store (read and write) and an invalid Seerr URL reached after the roster was cached
│   │       ├── SeerrDiscoveryServiceReasonTests.cs      # DetermineReason branches, threshold gates, priority ordering
│   │       ├── SeerrPermissionExtensionsTests.cs        # SECURITY: HasPermission zero-flag, admin bypass, per-media-type flags, null-user throws
│   │       └── TmdbDiscoverItemTests.cs                 # GenreIds null-coalesce, DisplayTitle fallback chain, EffectiveReleaseDate TV/movie, JSON round-trip
│   ├── Statistics/                # Statistics service tests
│   │   └── MediaStatisticsServiceTrashPathResolutionTests.cs # Trash-path resolution and BuildItemLookup case-insensitive keying / empty-path skip
│   ├── Timeline/                  # Growth timeline tests
│   │   ├── GrowthTimelineSymlinkTests.cs  # ReparsePoint guard prevents StackOverflow on circular symlinks
│   │   ├── GrowthTimelinePersistenceFailureTests.cs # Baseline save/load failures degrade gracefully without throwing
│   │   ├── LibraryInsightsServiceTraversalTests.cs  # Directory-walk over a real tree with a scripted IFileSystem for sizes/timestamps
│   │   ├── LibraryInsightsResultTests.cs  # Null-coalescing setters; defaults safe to enumerate; reassignment-to-null clears to empty
│   │   └── TimelineAggregatorTests.cs     # Unit tests for DetermineGranularity boundary conditions (daily/weekly/monthly/quarterly/yearly thresholds) and GenerateBucketStarts bucket spacing.
│   └── Recommendation/            # Recommendation engine tests
│       ├── Engine/                # Core engine logic tests
│       │   ├── CollaborativeFilterTests.cs
│       │   ├── ContentAffinityResolverTests.cs         # ResolveSeriesStatus train/serve parity; non-fatal exception fallbacks
│       │   ├── ContentScoringTests.cs
│       │   ├── ContentScoringGenreEngagementTests.cs   # ComputeGenreEngagement: empty genres/history → neutral; familiarity/completion/abandon rate with matching genre history; per-genre confidence shrinkage (single sample damped, many samples trusted); cached GenreEngagementContext bit-identical to direct (engagement + genre rating); ComputeSeriesAffinity: non-series → 0
│       │   ├── DiversityRerankerTests.cs
│       │   ├── EngineDiscoveryWatchedStatusTests.cs     # TrainStrategy marks favorited movies/series watched with the correct media type
│       │   ├── EngineIdfRarityTests.cs                  # IDF rarity weighting: rare genres/studios contribute more than common ones
│       │   ├── EngineLibraryMetadataTests.cs           # Gap 2 BuildLibraryItemMetadata (Movie/Series studios/tags, empty-skip) via TrainStrategy; Gap 5 GetEnsembleDiagnostics (ensemble non-null, non-ensemble null); featureMeans switch arms
│       │   ├── EngineBoxSetTests.cs                   # BuildWatchedBoxSetCounts, ComputeCollectionProgressionBoostLive (train/serve parity)
│       │   ├── EngineBoxSetLookupTests.cs             # Sparsity guarantee, fail-soft on corrupted metadata, mutability contract
│       │   ├── EngineStaticHelpersTests.cs           # Pure static helpers: episode counting, language parse/dedupe, billing-weight filtering
│       │   ├── EngineCommunityPopularityTests.cs      # BuildCommunityPopularityMap: batch and live paths produce identical output
│       │   ├── EngineEpisodicWatchHistoryTests.cs     # Episodic watch history must contribute people/studio signals via SeriesId fallback when ItemId is absent from peopleLookup/candidateLookup
│       │   ├── EngineExceedsMaxRatingTests.cs         # Parental-rating gate - null max = unrestricted, missing rating = REJECT, inclusive boundary
│       │   ├── EngineHelperTests.cs                   # Pure-static internal helpers untestable end-to-end
│       │   ├── EngineFullPipelineTests.cs             # Cold-start and warm paths with real Movie instances; ghost-id, empty-library, two-user-gate coverage
│       │   ├── EngineInstanceTests.cs                 # GetRecommendations/TrainStrategy contract: user-not-found=null, cancellation, Math.Clamp guards, empty deployment
│       │   ├── EngineLanguageAffinityTests.cs         # ComputeLanguageAffinity/SubtitleLanguageAffinity: empty profile → 0.5 neutral; cross-feature isolation
│       │   ├── FeatureAffinityComputerTests.cs        # Shared content-affinity helpers (franchise/country/inherited-tag/writer/billing/IDF): empty/null → neutral, no divide-by-zero
│       │   ├── FeatureParityTests.cs                  # Train/serve parity for the 7 content features; SeriesCompletability + BillingWeight canonical formulas
│       │   ├── GenreExposureRampTests.cs              # Gap E cold-start confidence ramp: 15-watch features == 0.5× 30-watch, saturates at threshold, empty vector → invalid/zero
│       │   ├── PreferenceBuilderTests.cs
│       │   ├── ReasonResolverTests.cs                 # All DetermineReason branches + StripWatchedItemsForResponse; EngineConstants as contract
│       │   ├── SimilarityComputerTests.cs             # People-batch + per-item fallback; weighted PeopleSimilarity
│       │   ├── TemporalFeaturesTests.cs               # Day-of-week / hour-of-day / weekend affinity
│       │   ├── TrainingServiceTests.cs                # Process-wide TrainGate; tests serialised via ConfigOverride collection
│       │   └── Training/
│       │       ├── CollectionProgressionBoostTests.cs # Diminishing-returns formula 0.3+(n-1)×0.2; train/serve parity
│       │       ├── TrainingDataBuilderTests.cs        # Phase 3 negatives must be deterministic
│       │       ├── TrainingDataBuilderOrganicTests.cs # Organic-example construction: label/weighting and per-example feature derivation
│       │       ├── PerUserTrainingDataBuilderTests.cs # Per-user isolation and leakage regression: UserId propagation, neutral interaction features, and series genre-engagement excluding its own watched episodes
│       │       ├── TrainingDataBuilderMetadataParityTests.cs # Gap 2: watched-item studios/tags resolve from the live library (merged over cache) so training matches the serve path; null-map back-compat; series self-exclusion
│       │       └── TrainingFeatureComputerTests.cs    # Training features must stay in lock-step with live scoring path
│       ├── Playlist/              # Playlist sync tests
│       │   ├── RecommendationPlaylistServiceTests.cs
│       │   ├── RecommendationPlaylistServiceDeletionFallbackTests.cs # Fallback delete path + OperationCanceled rethrow
│       │   └── RecommendationPlaylistServiceSecurityTests.cs # SECURITY: path-escape guard on managed playlist folders
│       ├── Scoring/               # Strategy-specific tests
│       │   ├── ScoringStrategyTests.cs
│       │   ├── NeuralScoringStrategyTests.cs
│       │   ├── EnsembleScoringStrategyAdvancedTests.cs # ScoreWithOffset, ApplyCohortFeedback, constructor guards
│       │   ├── EnsembleScoringStrategyNeuralTests.cs   # Neural sub-strategy blending and post-dispose degradation
│       │   ├── EnsembleScoringStrategyStateTests.cs    # State-file persistence: save-failure swallowed and logged (cross-platform IO error)
│       │   ├── EnsembleScoringStrategyTrainingTests.cs # Validation-loss dampening of alpha; quality-factor mapping
│       │   ├── EnsembleDiagnosticsTests.cs             # GetDiagnosticsSnapshot coherence: alpha within bounds, counts match, neural-enabled flag, frozen-gate case
│       │   ├── LearnedScoringStrategyStandardizationTests.cs # Feature standardization: mean/variance normalization and guards
│       │   ├── LearnedScoringStrategyLoggingTests.cs   # Training log lines and level selection
│       │   ├── LearnedScoringStrategyRobustnessTests.cs # NaN/degenerate inputs discarded, not applied
│       │   ├── StandardizationTransitionTests.cs      # Gap C warm-start: crossing the standardization threshold rescales weights (not reset), finite weights/scores, learned ranking preserved, zero-variance guard
│       │   ├── StrategySelectorTests.cs                # Cohort router: exploration gate, deterministic hash bucketing, routing
│       │   ├── NeuralFeatureImportanceTests.cs         # Permutation-based feature importance for MLP
│       │   ├── ScoreExplanationTests.cs
│       │   ├── TrainingExampleTests.cs
│       │   ├── RankingMetricsTests.cs
│       │   ├── PerUserRankingMetricsTests.cs        # Per-user macro averaging: equal weight per user regardless of library size
│       │   ├── ScoringAblationEvalTests.cs          # Offline ablation eval: synthetic taste-driven population, NDCG@10 with vs without genre-engagement + SeriesAffinity across Heuristic / Heuristic+Learned / full Ensemble tiers
│       │   └── ScoringGoldenLockTests.cs            # Behavior-lock test: pins deterministic digest of Heuristic+Learned+Neural scoring output
│       ├── WatchHistory/          # Watch history service tests
│       │   ├── LanguageAffinityTests.cs
│       │   ├── WatchHistoryServiceLanguageProfileTests.cs # Language-profile aggregation from watch history; NormalizeLanguage rows
│       │   ├── UserWatchProfileTests.cs        # Cache invalidation for lazy props, case-insensitive dictionary re-assignment (guards case-sensitive cache-deserialisation from silently regressing genre/language matching), null-safe setters, TopPeople boundaries (min-count filter, tie-break, cap at 20)
│       │   ├── WatchHistoryCompatTests.cs      # IUserManager API compatibility (MissingMethodException handling)
│       │   └── WatchHistoryServiceTests.cs
│       ├── RecommendationCacheServiceTests.cs
│       ├── RecommendationCacheServiceErrorHandlingTests.cs # Save/load IO failures degrade gracefully under an exclusive lock (cross-platform)
│       ├── RecommendationCacheServiceExtendedTests.cs  # Defensive branches missed by RecommendationCacheServiceTests: null-argument guard, directory auto-creation when DataPath does not exist, load of a file containing literal "null"
│       ├── RecommendationDtoTests.cs
│       ├── RecommendationEngineTests.cs
│       └── RecommendedItemTests.cs                     # Setter null-coalescing on the RecommendedItem DTO: SEVEN collection properties (Genres, PeopleNames, Studios, Tags, AudioLanguages, SubtitleLanguages, BoxSetIds) MUST swallow a null assignment and expose an empty list instead. Cache round-trips through JsonSerializer can null any of them; downstream training/scoring code iterates with foreach without null-guards. Reassignment (non-null → null) must actively replace the backing field so a re-clear doesn't leak the previous list.
└── TestFixtures/                  # Shared test helpers
    ├── EngineTestFactory.cs       # Centralised builder for a fully-mocked recommendation Engine (7 constructor dependencies wired to sensible empty-collection defaults + a strategy override hook). Returns an EngineHarness record bundling the engine with all Moq references so tests can override a single collaborator without re-wiring the other six; keeps the Engine-tests suite resilient to future constructor-signature changes (one-line fix here vs. shotgun surgery across N test files)
    └── PluginSingletonLifecycleTests.cs  # Verifies Plugin.Instance singleton lifecycle (InitializePluginInstance, TeardownPluginInstance, ResetPluginConfiguration): null/non-null state transitions, idempotency, and fresh-config guarantee after teardown+reinit
```

### Test Guidelines

- Use `Moq` for mocking Jellyfin interfaces
- Test both happy path and edge cases
- Scheduled task tests should verify all three modes: Activate, DryRun, Deactivate
- Backup tests should cover round-trip (create → serialize → deserialize → restore)
- Recommendation tests should verify scoring determinism and feature vector consistency

#### Plugin singleton isolation

Several controllers and services read `Plugin.Instance` directly. Tests that need the singleton must manage its lifecycle explicitly:

```csharp
// In constructor / test setup:
ControllerTestFactory.InitializePluginInstance();
ControllerTestFactory.ResetPluginConfiguration(); // start from known defaults

// In Dispose() / teardown:
ControllerTestFactory.TeardownPluginInstance();   // null the static field so the next class starts clean
```

- Always call `TeardownPluginInstance()` in `IDisposable.Dispose()`, not just `ResetPluginConfiguration()`.  
  `Reset` only overwrites the config object; the next test class that calls `Initialize` will silently skip re-init (the guard `if (Plugin.Instance != null) return`) and inherit whatever state the previous class left behind.
- Tests that mutate `Plugin.Instance.Configuration` must be placed in the `[Collection("ConfigOverride")]` collection so xUnit serialises them and prevents cross-class races.
- Never depend on `Plugin.Instance` being non-null in a test that does not call `InitializePluginInstance()`. The singleton is not set up by the xUnit runner.

## Architecture Overview

### Project Structure

```text
Jellyfin.Plugin.JellyfinHelper/
├── BuildTasks/
│   └── ComposeConfigPage.cs     # MSBuild task for config page composition
├── i18n/                        # Internationalization files (en, de, fr, es, pt, sv, zh, tr)
├── Plugin.cs                    # Entry point, web page registration, script injection
├── PluginServiceRegistrator.cs  # DI registration for all services
├── MediaExtensions.cs           # Extension methods for media analysis
├── js/
│   └── discovery-sidebar.js     # Discovery Custom Tab + sidebar script (embedded resource, injected into index.html)
├── Api/
│   ├── ArrIntegrationController.cs      # Radarr/Sonarr integration API
│   ├── BackupController.cs              # Backup/restore API
│   ├── CleanupStatisticsController.cs   # Cleanup statistics API
│   ├── ConfigurationController.cs       # Plugin configuration API
│   ├── ConfigurationResponse.cs         # Read-only masked projection of PluginConfiguration returned by GET /Configuration - all API key fields replaced with a fixed-length mask sentinel (ApiKeyMask); empty string when no key is stored. Static factory method FromConfig(PluginConfiguration) keeps the mapping in one place.
│   ├── MaskedArrInstanceConfig.cs       # Arr-instance view model used inside ConfigurationResponse (Name, Url, masked ApiKey). Separate from ArrInstanceConfig so the real key never appears in the serialized GET response.
│   ├── ApiKeyMaskResolver.cs            # Shared logic for the ApiKeyMask sentinel: IsMask(candidate) + ResolveArrKey(incoming, url, name, stored). Used by the save path (ConfigurationController) AND the stateless Test-Connection endpoints (ArrIntegrationController/SeerrController) so a masked key echoed back is resolved to the real stored key server-side and the mask is never forwarded upstream. Unresolvable mask → empty string (caller must not test).
│   ├── DiscoveryController.cs           # Seerr Discovery API - admin (all users, services, requests)
│   ├── UserDiscoveryController.cs       # Seerr Discovery API - user-facing (own results, requests)
│   ├── DiscoverySupport.cs              # Shared helpers for both discovery controllers: GetCurrentUserId(ClaimsPrincipal) claim resolution and BuildExcludedItemKeys(store, userId, onError) union of dismissed+requested items. onError is a callback so each controller keeps its own static log template (CA2254).
│   ├── DiscoveryRequestDto.cs           # Request submission DTO (TmdbId, MediaType, overrides)
│   ├── DiscoveryDismissDto.cs           # Dismiss request DTO (TmdbId, MediaType)
│   ├── FolderBrowserController.cs       # Folder browser API (server-side directory listing)
│   ├── RequestResult.cs                 # Generic success/failure response model
│   ├── GrowthTimelineController.cs      # Library growth timeline API
│   ├── LibraryInsightsController.cs     # Library insights API
│   ├── LogsController.cs               # Plugin logs API
│   ├── MediaStatisticsController.cs     # Media statistics API
│   ├── ModelBindingLogFilter.cs        # IAsyncActionFilter (Order = int.MinValue) attached to endpoints via [ServiceFilter]. Surfaces model-binding failures (invalid field types, null request body) into IPluginLogService BEFORE [ApiController]'s auto-400 short-circuits the request - without this filter, the auto-400 makes it out but no plugin-log entry is written, leaving admins with a bare HTTP 400 and no server-side trace to debug against. Registered as Scoped in PluginServiceRegistrator; do NOT register globally (would rewrite responses of other Jellyfin controllers that have their own error contracts).
│   ├── PingController.cs               # /JellyfinHelper/Ping liveness endpoint - no dependencies, returns { ok, plugin, version }. The Settings save flow probes this after a failed save to distinguish "backend unreachable" (Ping also fails) from "backend reachable, request rejected" (Ping succeeds). Uses the same [Authorize(RequiresElevation)] policy as the other admin endpoints so a successful ping proves the entire auth + routing + reverse-proxy chain is intact for admins.
│   ├── RecommendationController.cs      # ML recommendations API
│   ├── SeerrController.cs              # Jellyseerr/Overseerr integration API
│   ├── TranslationsController.cs        # i18n translations API
│   ├── TrashController.cs               # Trash bin API
│   ├── ConfigurationSaveResponse.cs     # PUT /Configuration response: message + warnings list
│   ├── ConnectionTestResponse.cs        # Arr/Seerr connection-test response: success flag + message
│   ├── EnsembleDiagnosticsResponse.cs   # GET /Recommendations/Diagnostics/Ensemble response: live ensemble state (alpha, neural beta, quality gate, sigmoid midpoint, trend, counts) + Available flag. Static factory FromDiagnostics(EnsembleDiagnostics).
│   ├── FolderBrowserResponse.cs         # GET /Configuration/LibraryPaths response: list of LibraryPathEntry
│   ├── LibraryEntry.cs                  # Library name + collectionType entry (used in LibraryListResponse)
│   ├── LibraryListResponse.cs           # GET /Configuration/Libraries response: list of LibraryEntry
│   ├── LibraryPathEntry.cs              # Library name + filesystem path entry (used in FolderBrowserResponse)
│   ├── LogLevelResponse.cs              # PUT /Configuration/LogLevel response: message + active log level
│   ├── PingResponse.cs                  # GET /Ping response: ok flag, plugin name, version string
│   ├── SeerrUrlResponse.cs              # GET /UserDiscovery/ExternalLinks response: Seerr base URL
│   ├── TrashAccessEntry.cs              # Per-path access result entry (used in TrashAccessResponse)
│   ├── TrashAccessResponse.cs           # POST /Trash/CheckAccess response: allAccessible flag + results
│   ├── TrashConfigResponse.cs           # GET /Trash/Contents response: useTrash, retentionDays, libraries
│   ├── TrashDeleteResponse.cs           # DELETE /Trash/Folders response: deleted + failed counts
│   ├── TrashFoldersResponse.cs          # GET /Trash/Folders and POST /Trash/FoldersForPath response: paths + isAbsolute
│   ├── TrashLibraryInfo.cs              # Per-library trash info entry (used in TrashConfigResponse)
│   ├── TrashPathQueryRequest.cs         # DTO for querying trash folders at a specific path
│   ├── TrashRelocateRequest.cs          # DTO for relocating trash between paths
│   ├── TrashRelocateResponse.cs         # POST /Trash/Relocate response: moved + failed counts
│   ├── TrashSizeResponse.cs             # GET /Trash/Summary response: totalSize + totalItems
│   └── UserActivityController.cs        # User activity insights API
├── Configuration/
│   ├── PluginConfiguration.cs   # All config properties with defaults
│   ├── ClampReportEntry.cs      # Record for reporting clamped config values at startup
│   ├── TaskMode.cs              # Deactivate / DryRun / Activate enum
│   └── ArrInstanceConfig.cs     # Per-instance Arr configuration
├── Services/
│   ├── Activity/                    # User watch activity tracking
│   │   ├── IUserActivityInsightsService.cs
│   │   ├── UserActivityInsightsService.cs
│   │   ├── IUserActivityCacheService.cs
│   │   ├── UserActivityCacheService.cs
│   │   ├── UserActivityResult.cs
│   │   ├── UserActivitySummary.cs
│   │   └── UserItemActivity.cs
│   ├── Backup/
│   │   ├── BackupData.cs              # Backup data model - `ContainsSecrets` flag (true when any API key is included in the export) so callers can warn the user before download
│   │   ├── BackupRestoreSummary.cs    # Restore outcome DTO - `CredentialsChanged` flag (true when any API key was overwritten with a different value from the backup); set by RestoreConfiguration alongside a WARN log entry
│   │   ├── BackupService.cs           # Create/restore backup
│   │   ├── BackupValidator.cs         # Comprehensive input validation
│   │   └── BackupSanitizer.cs         # Clamp/normalize values
│   ├── Common/                      # Shared cross-service helpers
│   │   ├── AtomicFile.cs            # Atomic text-file write (temp+move) with bounded retry on transient AV/indexer sharing violations
│   │   ├── BatchFallbackHelper.cs   # try-batch/fall-back-per-item wrapper (Jellyfin 12+ batch APIs)
│   │   ├── ExceptionExtensions.cs   # IsFatal() catch-filter: OOM + StackOverflow must never be swallowed
│   │   ├── HttpResponseReader.cs    # Size-bounded HTTP body reader (LimitedStream) shared by Arr/Seerr; guards against OOM from unbounded responses
│   │   ├── LimitedStream.cs         # Read-only Stream wrapper that throws ResponseTooLargeException past a byte cap (EOF probe at exact limit)
│   │   ├── ResponseTooLargeException.cs # Typed exception thrown by HttpResponseReader when a body exceeds the size limit
│   │   ├── SsrfGuard.cs             # Shared SSRF guard: blocks cloud metadata hosts on every Arr/Seerr outbound path (controller + config-save)
│   │   └── ReparsePointGuard.cs    # Shared fail-closed primitives for reparse-point detection and safe link-node deletion (used by cleanup tasks + TrashService)
│   ├── FolderBrowser/               # Server-side folder browsing
│   │   ├── IFolderBrowserService.cs # Interface for folder listing
│   │   ├── FolderBrowserService.cs  # Implementation: lists directories with safety guards
│   │   ├── FolderBrowseResult.cs    # Browse result container (entries + current path)
│   │   └── FolderEntry.cs           # Single folder/file entry DTO
│   ├── Recommendation/              # ML recommendation system
│   │   ├── Engine/                  # Core recommendation logic
│   │   │   ├── Engine.cs            # Orchestrator: profiles → candidates → scoring → results
│   │   │   ├── TrainingService.cs   # Implicit feedback training pipeline
│   │   │   ├── Training/            # Training sub-components (refactored from TrainingService)
│   │   │   │   ├── TrainingDataBuilder.cs      # Builds labeled training examples from watch history
│   │   │   │   ├── TrainingFeatureComputer.cs  # Computes feature vectors for training candidates
│   │   │   │   ├── DiscoveryFeedbackExampleBuilder.cs # Phase 4: training from discovery interactions
│   │   │   │   └── LibraryItemMetadata.cs       # Live-library item→(studios/tags/BoxSet ids) map threaded into training for serve parity
│   │   │   ├── PreferenceBuilder.cs # Genre/studio/tag/people preference extraction
│   │   │   ├── DiversityReranker.cs # MMR-based diversity reranking
│   │   │   ├── TemporalFeatures.cs  # Day-of-week/hour-of-day affinity computation
│   │   │   ├── ReasonResolver.cs    # Human-readable recommendation explanations
│   │   │   ├── SimilarityComputer.cs # Genre/people/tag similarity
│   │   │   ├── CollaborativeFilter.cs # Jaccard + IDF co-occurrence
│   │   │   ├── ContentAffinityResolver.cs # Shared library-free resolvers for content-affinity source data
│   │   │   ├── ContentScoring.cs    # Recency, rating, engagement scoring
│   │   │   └── EngineConstants.cs   # Shared constants (thresholds, windows)
│   │   ├── Scoring/                 # Pluggable scoring strategies
│   │   │   ├── IScoringStrategy.cs
│   │   │   ├── ITrainableStrategy.cs
│   │   │   ├── HeuristicScoringStrategy.cs  # Fixed weights (rule-based)
│   │   │   ├── LearnedScoringStrategy.cs    # Adaptive ML (SGD linear)
│   │   │   ├── NeuralScoringStrategy.cs     # MLP with Adam optimizer
│   │   │   ├── EnsembleScoringStrategy.cs   # Blends heuristic + learned + neural
│   │   │   ├── EnsembleDiagnostics.cs        # Immutable read-only snapshot of the ensemble's live state (alpha, neural beta, quality gate, sigmoid midpoint, trend, counts, bounds, neural-enabled)
│   │   │   ├── StrategySelector.cs          # A/B testing: deterministic user→strategy routing
│   │   │   ├── NeuralFeatureImportance.cs   # Permutation-based feature importance for MLP
│   │   │   ├── CandidateFeatures.cs         # 38-feature vector with FeatureIndex enum
│   │   │   ├── DefaultWeights.cs            # Centralized default weights
│   │   │   ├── ScoringHelper.cs             # Shared scoring utilities
│   │   │   ├── ScoreExplanation.cs          # Per-feature score breakdown
│   │   │   ├── TrainingExample.cs           # Training data container
│   │   │   └── RankingMetrics.cs            # P@K, R@K, NDCG@K evaluation
│   │   ├── WatchHistory/            # User watch profile building
│   │   │   ├── IWatchHistoryService.cs
│   │   │   ├── WatchHistoryService.cs
│   │   │   ├── UserWatchProfile.cs
│   │   │   ├── LanguageAffinity.cs
│   │   │   └── WatchedItemInfo.cs
│   │   ├── Playlist/                # Recommendation → Jellyfin playlist sync
│   │   │   ├── IRecommendationPlaylistService.cs
│   │   │   ├── RecommendationPlaylistService.cs
│   │   │   └── PlaylistSyncResult.cs
│   │   ├── IRecommendationEngine.cs
│   │   ├── IRecommendationCacheService.cs
│   │   ├── RecommendationCacheService.cs
│   │   ├── RecommendedItem.cs
│   │   └── RecommendationResult.cs
│   ├── Arr/                     # Radarr/Sonarr integration
│   ├── Cleanup/                 # File cleanup services
│   │   ├── ITrashService.cs            # Trash bin interface (move, purge, relocate, access check)
│   │   ├── TrashService.cs             # Trash bin implementation
│   │   ├── TrashItemInfo.cs            # Trash item metadata DTO
│   │   ├── TrashPathAccessResult.cs    # Permission check result (read/write/exists)
│   │   ├── ICleanupConfigHelper.cs     # Cleanup configuration interface
│   │   ├── CleanupConfigHelper.cs      # Library filtering, trash path resolution
│   │   ├── ICleanupTrackingService.cs  # Cleanup statistics tracking interface
│   │   └── CleanupTrackingService.cs   # Persists bytes-freed/items-deleted counters
│   ├── ConfigAccess/            # Plugin configuration access
│   ├── Link/                    # .strm/symlink repair
│   ├── PluginLog/               # Structured plugin logging
│   ├── FileTransformation/      # File Transformation plugin integration
│   │   ├── DiscoveryScriptTag.cs     # Shared script tag builder + removal regex (single source of truth)
│   │   ├── DiscoverySidebarInjectionService.cs  # IHostedService that re-runs Plugin.InjectScript() at server startup (post-DI, web root mounted) - self-heals the disk-write fallback after a Jellyfin web update; idempotent alongside the ctor injection
│   │   ├── PatchRequestPayload.cs    # Payload model for transformation callbacks
│   │   └── TransformationPatches.cs  # index.html script injection (on-the-fly via File Transformation plugin)
│   ├── Seerr/                   # Jellyseerr/Overseerr integration
│   │   ├── ISeerrIntegrationService.cs   # Seerr cleanup (request removal)
│   │   ├── SeerrIntegrationService.cs
│   │   └── Discovery/               # Seerr Discovery (external recommendations)
│   │       ├── ISeerrDiscoveryService.cs
│   │       ├── SeerrDiscoveryService.cs  # Orchestrator: profiles → TMDb query → scoring → results
│   │       ├── DiscoveryCacheService.cs  # Disk + memory persistence
│   │       ├── ExternalCandidateFeatureBuilder.cs  # Builds 38-feature vector for TMDb items
│   │       ├── NullableDateTimeConverter.cs  # Graceful DateTime? JSON deserialization (handles empty strings from TMDb)
│   │       ├── ParentalRatingHelper.cs   # Child-safe content filtering
│   │       ├── TmdbGenreMap.cs           # Jellyfin ↔ TMDb genre ID mapping
│   │       ├── TmdbDiscoverItem.cs       # TMDb candidate DTO
│   │       ├── TmdbDiscoverResponse.cs   # TMDb API page response
│   │       ├── DiscoveryResult.cs        # Per-user result container
│   │       ├── DiscoveryRecommendation.cs # Single recommendation DTO
│   │       ├── SeerrUser.cs             # Seerr user model (with JellyfinUserId mapping + Permissions)
│   │       ├── SeerrUserPage.cs         # Paginated user list response
│   │       ├── SeerrPermissions.cs      # [Flags] enum of all Overseerr/Jellyseerr permission bits
│   │       ├── SeerrPermissionExtensions.cs # Permission evaluation (HasPermission, CanRequest, CanSelectQualityProfile)
│   │       ├── UserRequestPermissionResult.cs # Permission check result (CanRequest + allowed profiles)
│   │       ├── AllowedQualityProfile.cs # Single quality profile the user may select
│   │       ├── SeerrServiceInfo.cs      # Radarr/Sonarr service config from Seerr
│   │       ├── SeerrQualityProfile.cs   # Quality profile DTO
│   │       ├── SeerrRootFolder.cs       # Root folder DTO
│   │       ├── SeerrCredits.cs          # TMDb credits response (cast + crew)
│   │       ├── SeerrCastMember.cs       # Cast member DTO
│   │       ├── SeerrCrewMember.cs       # Crew member DTO
│   │       ├── SeerrMediaDetailResponse.cs # Detailed media info from Seerr
│   │       ├── IDiscoveryFeedbackStore.cs  # Training feedback persistence interface
│   │       ├── DiscoveryFeedbackStore.cs   # File-based feedback store (shown/dismissed/requested/watched)
│   │       ├── DiscoveryFeedbackEntry.cs   # Per-item interaction tracking model
│   │       ├── DiscoveryFeedbackResult.cs  # Per-user feedback container
│   │       └── DiscoveryInteractionStatus.cs # Enum: Shown/Dismissed/Requested/RequestedAndWatched
│   ├── Statistics/              # Media statistics
│   └── Timeline/                # Library growth tracking
│       ├── IGrowthTimelineService.cs   # Interface for timeline generation
│       ├── GrowthTimelineService.cs    # Orchestrator: scans library directories, builds incremental entries, writes result JSON
│       ├── TimelineAggregator.cs       # Pure stateless aggregation: DetermineGranularity (daily/weekly/monthly/quarterly/yearly by span), GenerateBucketStarts, BuildIncrementalEntries, ConsolidateToGranularity - all internal static, no I/O
│       ├── GrowthTimelineBaseline.cs   # Baseline snapshot DTO (first-scan directory sizes + timestamps)
│       ├── BaselineDirectoryEntry.cs   # Single directory entry in the baseline
│       ├── GrowthTimelineResult.cs     # Timeline result DTO (buckets + granularity label)
│       ├── GrowthTimelinePoint.cs      # Single data point in the timeline (date + size + count)
│       ├── ILibraryInsightsService.cs  # Interface for library insights aggregation
│       ├── LibraryInsightsService.cs   # Aggregates growth data into per-library insights
│       ├── LibraryInsightsResult.cs    # Insights result DTO
│       └── LibraryInsightEntry.cs      # Per-library insight entry
├── ScheduledTasks/
│   ├── HelperCleanupTask.cs         # Main orchestrator task
│   ├── CleanTrickplayTask.cs
│   ├── CleanEmptyMediaFoldersTask.cs
│   ├── CleanOrphanedSubtitlesTask.cs
│   ├── RepairLinksTask.cs            # Repairs broken .strm/symlink references
│   ├── RecommendationsTask.cs        # ML recommendation generation sub-task
│   └── UserActivityUpdateTask.cs     # User activity aggregation sub-task
└── PluginPages/
    ├── configPage.template.html # HTML shell (build-time composition)
    ├── configPage.html          # Generated output (do not edit)
    ├── css/                     # Per-tab CSS modules
    │   ├── Shared.css, Overview.css, Codecs.css, Health.css
    │   ├── Trends.css, Settings.css, ArrIntegration.css, Logs.css
    │   └── Recommendations.css  # Discover tab styles
    └── js/                      # Per-tab JS modules + eslint.config.js
        ├── Shared.js, Overview.js, Codecs.js, Health.js
        ├── Trends.js, Settings.js, ArrIntegration.js, Logs.js
        ├── Recommendations.js    # Discover tab logic
        ├── FolderBrowser.js      # Folder browser UI (path picker for settings)
        └── Main.js               # Tab routing, IIFE close
```

### Complete File Index

The trees above are a curated, commented overview. **This index is the authoritative,
complete listing** of every tracked source and test file (`.cs` / `.html` / `.css` /
`.js`) in the two projects, enforced by the `ContributingDocCoverageTests` drift
guard, which fails the build if any tracked file is missing here. Generated build
artifacts (`bin/`, `obj/`) and the composed `PluginPages/configPage.html` (git-ignored)
are intentionally excluded. When you add a file, add a line for it here.

`Jellyfin.Plugin.JellyfinHelper/`

- `MediaExtensions.cs`
- `Plugin.cs`
- `PluginServiceRegistrator.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/`

- `MediaExtensionsTests.cs` - Tests MediaExtensions video/subtitle/image/audio/nfo sets, codec map, and language codes
- `ContributingDocCoverageTests.cs` - Drift guard: every tracked source/test file must be listed in this index
- `PluginServiceRegistratorTests.cs`
- `PluginTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Api/`

- `ArrIntegrationControllerExtendedTests.cs`
- `ArrIntegrationControllerTests.cs`
- `ApiKeyMaskResolverTests.cs` - Tests the ApiKeyMask sentinel resolver (IsMask + ResolveArrKey: passthrough, Name+URL / URL-only matching, duplicate-name collisions, no-match, null-guard)
- `BackupControllerExtendedTests.cs`
- `BackupControllerTests.cs`
- `CleanupStatisticsControllerTests.cs` - Tests CleanupStatisticsController returns cleanup stats payload (bytes freed, items, timestamp)
- `ConfigurationControllerTests.cs`
- `ConfigurationRequestValidatorTests.cs` - Tests ConfigurationRequestValidator: age/retention bounds, Arr/Seerr rules, trash-path traversal guards
- `ConfigurationResponseTests.cs`
- `DiscoveryControllerExtendedTests.cs`
- `DiscoveryControllerTests.cs`
- `DiscoverySupportTests.cs` - Tests DiscoverySupport GetCurrentUserId claim resolution and BuildExcludedItemKeys feedback/dismiss union
- `FolderBrowserControllerTests.cs`
- `GrowthTimelineControllerTests.cs` - Tests GrowthTimelineController computed/cached timeline and 429 refresh rate-limiting
- `LibraryInsightsControllerTests.cs` - Tests LibraryInsightsController compute-and-cache behavior and recompute on cache expiry
- `LogsControllerTests.cs` - Tests LogsController get/download/clear logs and min-level/source input validation
- `MediaStatisticsControllerTests.cs` - Tests MediaStatisticsController scan, cache persistence, and latest-result retrieval
- `ModelBindingLogFilterTests.cs`
- `PingControllerTests.cs`
- `RecommendationControllerTests.cs`
- `RecommendationControllerDiagnosticsTests.cs`
- `ResponseDtoTests.cs`
- `SeerrControllerTests.cs` - Tests SeerrController TestConnection input validation and success/failure/timeout responses
- `TranslationsControllerTests.cs` - Tests TranslationsController language lookup, config-default fallback, and lang-code validation
- `TrashControllerTests.cs`
- `UserActivityControllerTests.cs`
- `UserDiscoveryControllerAccessEnabledTests.cs`
- `UserDiscoveryControllerSubmitTests.cs`
- `UserDiscoveryControllerTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Configuration/`

- `PluginConfigurationSerializationTests.cs`
- `TaskModeTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/PluginPages/`

- `ArrIntegrationHtmlTests.cs`
- `CodecsHtmlTests.cs`
- `ConfigPageHtmlTests.cs`
- `ConfigPageTemplateTests.cs`
- `ConfigPageTestBase.cs`
- `DiscoverHtmlTests.cs`
- `FolderBrowserHtmlTests.cs`
- `HealthHtmlTests.cs`
- `LogsHtmlTests.cs`
- `MainHtmlTests.cs`
- `OverviewHtmlTests.cs`
- `RecommendationsHtmlTests.cs`
- `SettingsHtmlTests.cs`
- `SharedHtmlTests.cs`
- `TrendsHtmlTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/ScheduledTasks/`

- `CleanEmptyMediaFoldersTaskTests.cs` - Tests CleanEmptyMediaFoldersTask orphan detection, placeholder/library-type skips (incl. Book/eBook libraries which must never be scanned or deleted), and byte accounting
- `CleanupTaskReparseGuardTests.cs` - Tests cleanup tasks skip reparse-point/symlink directories to prevent traversal outside libraries
- `CleanOrphanedSubtitlesTaskProcessLocationTests.cs`
- `CleanOrphanedSubtitlesTaskTests.cs` - Tests CleanOrphanedSubtitlesTask base-name parsing and BCP-47 language/flag suffix stripping
- `CleanTrickplayTaskTests.cs` - Tests CleanTrickplayTask orphaned .trickplay folder detection, media-match keeps, and error handling
- `CleanTrickplayTrashExclusionTests.cs`
- `HelperCleanupTaskTests.cs` - Tests HelperCleanupTask orchestration: sub-task activate/dry-run/skip, Seerr, progress, trash purge
- `RecommendationsTaskTests.cs`
- `RepairLinksTaskTests.cs`
- `UserActivityUpdateTaskTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/`

- `DateTimeNormalizationTests.cs`
- `FileSystemHelperTests.cs` - Tests FileSystemHelper directory-size calc and dictionary count/accumulate/path helpers
- `I18nServiceTests.cs` - Tests I18nService translations, config-page key sync, and Lazy load concurrency
- `PathValidatorTests.cs` - Tests PathValidator safe-path, filename sanitization, and sensitive-system-path checks

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Activity/`

- `UserActivityCacheServiceTests.cs` - Tests JSON cache save/load round-trip, corruption recovery, and directory auto-creation
- `UserActivityDtoTests.cs` - Tests activity DTO defaults, UTC normalization, and reference-equality semantics
- `UserActivityInsightsServiceTests.cs` - Tests activity report building, completion math, and batch user-data fallback contract

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Arr/`

- `ArrComparisonResultTests.cs` - Tests ArrComparisonResult collection defaults, item addition, and ordering
- `ArrIntegrationServiceTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Backup/`

- `BackupSanitizerTests.cs`
- `BackupServicePerformanceTests.cs`
- `BackupServiceRestoreConfigTests.cs`
- `BackupServiceTests.cs`
- `BackupValidatorTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Cleanup/`

- `CleanupConfigHelperTests.cs` - Tests cleanup config helpers: task modes, trash-path resolution, library filtering (incl. IsCleanupEligibleCollectionType excluding books/music/boxsets), age guards
- `CleanupTrackingServiceTests.cs` - Tests cleanup statistics recording and accumulation when Plugin.Instance is null
- `TrashControllerAccessTests.cs`
- `TrashControllerRelocateTests.cs`
- `TrashControllerSecurityTests.cs` - Security tests: TrashController rejects unsafe delete paths outside libraries
- `TrashServiceAccessTests.cs`
- `TrashServiceGuardTests.cs`
- `TrashServiceInternalHelpersTests.cs`
- `TrashServicePathAccessTests.cs` - Permission-denied branches: existing-dir-not-writable and non-existent-path-parent-not-writable in CheckPathAccess, plus unreadable-folder enumeration catch in GetTrashSummary/GetTrashContents (real OS denial with root-bypass probe)
- `TrashServicePathLengthTests.cs`
- `TrashServiceRelocateTests.cs`
- `TrashServiceReparseAndRaceTests.cs` - Tests TrashService reparse-point guard and concurrent-move race safety
- `TrashServiceSecurityTests.cs` - Security tests: TrashService resists path traversal, null bytes, and malicious names
- `TrashServiceTests.cs` - Tests trash move, timestamp parsing, retention purge, and contents/summary listing

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Common/`

- `AtomicFileTests.cs`
- `BatchFallbackHelperTests.cs`
- `ExceptionExtensionsTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/ConfigAccess/`

- `PluginConfigurationServiceTests.cs` - Tests PluginConfigurationService via fake accessor: init state, version, get/save config

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/FileTransformation/`

- `DiscoveryScriptTagTests.cs`
- `DiscoverySidebarInjectionServiceTests.cs`
- `PatchRequestPayloadTests.cs`
- `TransformationPatchesTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/FolderBrowser/`

- `FolderBrowserDtoTests.cs`
- `FolderBrowserServiceTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Link/`

- `LinkRepairPerformanceTests.cs` - Performance tests for LinkRepairService on large .strm/symlink/mixed directory trees
- `LinkRepairSecurityTests.cs` - Security tests: path traversal, injection, oversized/null-byte link content stay safe
- `LinkRepairServiceTests.cs` - Unit tests for LinkRepairService find/process/repair logic across strm and symlink handlers
- `StrmLinkHandlerTests.cs` - Unit tests for StrmLinkHandler CanHandle, ReadTarget, and WriteTarget behavior
- `SymlinkHandlerTests.cs` - Unit tests for SymlinkHandler including atomic temp-then-replace WriteTarget path
- `SymlinkHelperTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/PluginLog/`

- `PluginLogEntryTests.cs` - Unit tests for PluginLogEntry model defaults, init properties, and edge cases
- `PluginLogServiceTests.cs` - Unit tests for PluginLogService logging, level filtering, ring buffer, and export

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/`

- `RecommendationCacheServiceExtendedTests.cs`
- `RecommendationCacheServiceTests.cs`
- `RecommendationDtoTests.cs`
- `RecommendationEngineTests.cs`
- `RecommendedItemTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/`

- `CollaborativeFilterTests.cs`
- `ContentScoringTests.cs`
- `DiversityRerankerTests.cs`
- `EngineBoxSetLookupTests.cs`
- `EngineBoxSetTests.cs`
- `EngineCommunityPopularityTests.cs`
- `EngineEpisodicWatchHistoryTests.cs`
- `EngineExceedsMaxRatingTests.cs`
- `EngineFullPipelineTests.cs`
- `EngineHelperTests.cs`
- `EngineInstanceTests.cs`
- `EngineLanguageAffinityTests.cs`
- `EngineLibraryMetadataTests.cs`
- `GenreExposureRampTests.cs`
- `PreferenceBuilderTests.cs`
- `ReasonResolverTests.cs`
- `SimilarityComputerTests.cs`
- `TemporalFeaturesTests.cs`
- `TrainingServiceTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Engine/Training/`

- `CollectionProgressionBoostTests.cs`
- `TrainingDataBuilderTests.cs`
- `PerUserTrainingDataBuilderTests.cs`
- `TrainingDataBuilderMetadataParityTests.cs`
- `TrainingFeatureComputerTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Playlist/`

- `RecommendationPlaylistServiceTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/Scoring/`

- `EnsembleScoringStrategyAdvancedTests.cs`
- `EnsembleDiagnosticsTests.cs`
- `NeuralFeatureImportanceTests.cs`
- `NeuralScoringStrategyTests.cs`
- `RankingMetricsTests.cs`
- `PerUserRankingMetricsTests.cs`
- `ScoreExplanationTests.cs`
- `ScoringAblationEvalTests.cs`
- `ScoringStrategyTests.cs`
- `StrategySelectorTests.cs`
- `TrainingExampleTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Recommendation/WatchHistory/`

- `LanguageAffinityTests.cs`
- `UserWatchProfileTests.cs`
- `WatchHistoryCompatTests.cs`
- `WatchHistoryServiceTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Seerr/`

- `SeerrIntegrationServiceTests.cs`
- `SeerrMediaDetailsTests.cs`
- `SeerrRequestPageTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Seerr/Discovery/`

- `DiscoveryCacheServiceTests.cs`
- `DiscoveryFeedbackStoreTests.cs`
- `DiscoveryRecommendationTests.cs`
- `DiscoveryRegressionTests.cs`
- `ExternalCandidateFeatureBuilderExtendedTests.cs`
- `ExternalCandidateFeatureBuilderTests.cs`
- `ExternalCandidateFeatureImputationTests.cs`
- `NullableDateTimeConverterTests.cs`
- `ParentalRatingHelperTests.cs`
- `SeerrDiscoveryDtoTests.cs`
- `SeerrDiscoveryGenerationTests.cs`
- `SeerrDiscoveryServiceCacheStampedeTests.cs` - Concurrency tests for SeerrDiscoveryService user-cache stampede correctness
- `SeerrDiscoveryServiceCacheTests.cs` - Tests for SeerrDiscoveryService TTL user cache: warm/cold hits and non-caching of failures
- `SeerrDiscoveryServiceHelperTests.cs`
- `SeerrDiscoveryServiceHttpTests.cs`
- `SeerrDiscoveryServiceReasonTests.cs`
- `SeerrDiscoveryServiceTests.cs`
- `SeerrDiscoveryServiceUserResolutionTests.cs`
- `SeerrPermissionExtensionsTests.cs`
- `TmdbDiscoverItemTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Statistics/`

- `MediaStatisticsResultTests.cs` - Unit tests for MediaStatisticsResult aggregate totals and dictionary rollups
- `MediaStatisticsServiceTests.cs` - Unit tests for MediaStatisticsService library scanning and statistics calculation
- `MediaStatisticsServiceTvShowTests.cs` - Unit tests for MediaStatisticsService TV show structure and orphaned-metadata handling
- `StatisticsCacheServiceTests.cs` - Unit tests for StatisticsCacheService persisting and loading cached statistics results

`Jellyfin.Plugin.JellyfinHelper.Tests/Services/Timeline/`

- `GrowthTimelineModelTests.cs` - Unit tests for growth timeline models and their JSON serialization
- `GrowthTimelinePerformanceTests.cs` - Performance tests for TimelineAggregator cumulative-timeline computation on large datasets
- `GrowthTimelineServiceTests.cs` - Unit tests for GrowthTimelineService building growth timelines from library files
- `GrowthTimelineSymlinkTests.cs`
- `LibraryInsightsResultTests.cs`
- `LibraryInsightsServiceTests.cs` - Unit tests for LibraryInsightsService change-type classification and insights logic
- `TimelineAggregatorTests.cs`

`Jellyfin.Plugin.JellyfinHelper.Tests/TestFixtures/`

- `CleanupTaskTestBase.cs` - Base class for cleanup task tests providing mocked config/tracking/trash and log helpers
- `ConfigOverrideCollection.cs` - xUnit collection definition serializing tests that mutate shared plugin configuration
- `ControllerTestFactory.cs` - Factory building API controllers and plugin instances with mocked dependencies for tests
- `EngineTestFactory.cs`
- `PluginSingletonLifecycleTests.cs`
- `TestDataGenerator.cs` - Central generator for test data objects like VirtualFolderInfo and LibraryStatistics
- `TestMockFactory.cs` - Central factory for commonly used mocks and PluginLogService instances across tests

`Jellyfin.Plugin.JellyfinHelper/Api/`

- `ArrIntegrationController.cs`
- `ApiKeyMaskResolver.cs` - Shared ApiKeyMask sentinel logic (IsMask + ResolveArrKey) used by the save path and the Test-Connection endpoints so a masked key is resolved to the real stored key server-side and never forwarded upstream
- `ArrTestConnectionRequest.cs` - Request DTO carrying URL, API key, and optional Name for testing a Radarr/Sonarr connection (Name disambiguates the stored key when the API key is the masked sentinel)
- `BackupController.cs`
- `CleanupStatisticsController.cs`
- `ConfigurationController.cs`
- `ConfigurationRequestValidator.cs` - Validates config-update fields: ranges, Arr instances, Seerr URL, trash path safety
- `ConfigurationResponse.cs`
- `ConfigurationSaveResponse.cs`
- `ConfigurationUpdateRequest.cs` - Request DTO for updating the full plugin configuration via the API
- `ConnectionTestResponse.cs`
- `EnsembleDiagnosticsResponse.cs`
- `DiscoveryController.cs`
- `DiscoveryDismissDto.cs`
- `DiscoveryRequestDto.cs`
- `DiscoverySupport.cs` - Shared GetCurrentUserId + BuildExcludedItemKeys helpers for the two discovery controllers
- `FolderBrowserController.cs`
- `FolderBrowserResponse.cs`
- `GrowthTimelineController.cs`
- `LibraryEntry.cs`
- `LibraryInsightsController.cs`
- `LibraryListResponse.cs`
- `LibraryPathEntry.cs`
- `LogLevelResponse.cs`
- `LogLevelUpdateRequest.cs` - Request DTO for updating only the plugin log level via PUT /Configuration/LogLevel
- `LogsController.cs`
- `MaskedArrInstanceConfig.cs`
- `MediaStatisticsController.cs`
- `ModelBindingLogFilter.cs`
- `PingController.cs`
- `PingResponse.cs`
- `RecommendationController.cs`
- `RequestResult.cs`
- `SeerrController.cs`
- `SeerrTestRequest.cs` - Request DTO carrying URL and API key for testing a Seerr connection
- `SeerrUrlResponse.cs`
- `TranslationsController.cs`
- `TrashAccessEntry.cs`
- `TrashAccessResponse.cs`
- `TrashConfigResponse.cs`
- `TrashController.cs`
- `TrashDeleteResponse.cs`
- `TrashFoldersResponse.cs`
- `TrashLibraryInfo.cs`
- `TrashPathQueryRequest.cs`
- `TrashRelocateRequest.cs`
- `TrashRelocateResponse.cs`
- `TrashSizeResponse.cs`
- `UserActivityController.cs`
- `UserDiscoveryController.cs`

`Jellyfin.Plugin.JellyfinHelper/BuildTasks/`

- `ComposeConfigPage.cs`

`Jellyfin.Plugin.JellyfinHelper/Configuration/`

- `ArrInstanceConfig.cs`
- `ClampReportEntry.cs`
- `PluginConfiguration.cs`
- `TaskMode.cs`

`Jellyfin.Plugin.JellyfinHelper/PluginPages/`

- `configPage.template.html`

`Jellyfin.Plugin.JellyfinHelper/PluginPages/css/`

- `ArrIntegration.css`
- `Codecs.css`
- `Health.css`
- `Logs.css`
- `Overview.css`
- `Recommendations.css`
- `Settings.css`
- `Shared.css`
- `Trends.css`

`Jellyfin.Plugin.JellyfinHelper/PluginPages/js/`

- `ArrIntegration.js`
- `Codecs.js`
- `FolderBrowser.js`
- `Health.js`
- `Logs.js`
- `Main.js`
- `Overview.js`
- `Recommendations.js`
- `Settings.js`
- `Shared.js`
- `Trends.js`

`Jellyfin.Plugin.JellyfinHelper/ScheduledTasks/`

- `BaseLibraryCleanupTask.cs` - Abstract Template Method base for library cleanup tasks: iterate locations, delete, log, record
- `CleanEmptyMediaFoldersTask.cs`
- `CleanOrphanedSubtitlesTask.cs`
- `CleanTrickplayTask.cs`
- `HelperCleanupTask.cs`
- `RecommendationsTask.cs`
- `RepairLinksTask.cs`
- `UserActivityUpdateTask.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/`

- `DateTimeNormalization.cs` - Shared UTC DateTime normalization helpers for DTOs
- `FileSystemHelper.cs` - Best-effort filesystem helpers: directory sizing and dictionary accumulation
- `I18nService.cs` - i18n translation loader from embedded JSON resources with caching
- `JsonDefaults.cs` - Shared JSON serializer options (camelCase, indented, case-insensitive)
- `LibraryPathResolver.cs` - Resolves and deduplicates library folder paths from the library manager
- `PathValidator.cs` - Path validation guarding traversal, sensitive system roots, and safe deletion

`Jellyfin.Plugin.JellyfinHelper/Services/Activity/`

- `IUserActivityCacheService.cs`
- `IUserActivityInsightsService.cs`
- `UserActivityCacheService.cs`
- `UserActivityInsightsService.cs`
- `UserActivityResult.cs`
- `UserActivitySummary.cs`
- `UserItemActivity.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Arr/`

- `ArrComparisonResult.cs` - Result of comparing an Arr app with Jellyfin: InBoth, InArrOnly, InArrOnlyMissing, InJellyfinOnly
- `ArrIntegrationService.cs` - Radarr/Sonarr API client: test connection, fetch movies/series, compare against Jellyfin folders
- `ArrMovie.cs` - DTO representing a Radarr movie (title, year, IMDb/TMDb ID, HasFile, path)
- `ArrSeries.cs` - DTO representing a Sonarr series (title, year, IDs, path, episode file/total counts)
- `IArrIntegrationService.cs` - Interface for the Radarr/Sonarr integration service (connection test, fetch movies/series)

`Jellyfin.Plugin.JellyfinHelper/Services/Backup/`

- `BackupArrInstance.cs` - Plain DTO for an Arr instance in backup data (name, url, apiKey) for safe deserialization
- `BackupData.cs`
- `BackupRestoreSummary.cs`
- `BackupSanitizer.cs`
- `BackupService.cs`
- `BackupValidationResult.cs` - Result of validating a backup payload: Errors, Warnings, and IsValid flag
- `BackupValidator.cs`
- `IBackupService.cs` - Interface for creating and restoring plugin backups (oversize check, create, restore)

`Jellyfin.Plugin.JellyfinHelper/Services/Cleanup/`

- `CleanupConfigHelper.cs`
- `CleanupTrackingService.cs`
- `ICleanupConfigHelper.cs`
- `ICleanupTrackingService.cs`
- `ITrashService.cs`
- `TrashItemInfo.cs`
- `TrashPathAccessResult.cs`
- `TrashService.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Common/`

- `AtomicFile.cs`
- `BatchFallbackHelper.cs`
- `ExceptionExtensions.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/ConfigAccess/`

- `IPluginConfigurationService.cs` - Testable abstraction for reading/mutating/saving plugin configuration
- `PluginConfigurationService.cs` - Config service backed by Plugin.Instance with lock-guarded read-mutate-save

`Jellyfin.Plugin.JellyfinHelper/Services/FileTransformation/`

- `DiscoveryScriptTag.cs`
- `DiscoverySidebarInjectionService.cs`
- `PatchRequestPayload.cs`
- `TransformationPatches.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/FolderBrowser/`

- `FolderBrowseResult.cs`
- `FolderBrowserService.cs`
- `FolderEntry.cs`
- `IFolderBrowserService.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Link/`

- `ILinkHandler.cs` - Strategy interface for reading/writing a single link type (.strm, symlink)
- `ILinkRepairService.cs` - Interface for scanning libraries and repairing broken link references
- `ISymlinkHelper.cs` - Abstraction over symlink filesystem ops to enable testing without real symlinks
- `LinkFileResult.cs` - Result model for a single inspected link file (paths and status)
- `LinkFileStatus.cs` - Enum of link inspection outcomes: Valid, Repaired, Broken, Ambiguous, InvalidContent
- `LinkRepairResult.cs` - Aggregate result of a repair run with per-status counts over file results
- `LinkRepairService.cs` - Scans libraries, validates link targets, and repairs broken links via handlers
- `StrmLinkHandler.cs` - Link handler reading/writing .strm text files (supports URL targets)
- `SymlinkHandler.cs` - Link handler for symlinks; rewrites atomically via temp-link plus replace
- `SymlinkHelper.cs` - Production ISymlinkHelper using real File APIs; detects links via reparse+LinkTarget

`Jellyfin.Plugin.JellyfinHelper/Services/PluginLog/`

- `IPluginLogService.cs` - Interface for the in-memory ring-buffer plugin log service with dual-logging support
- `PluginLogEntry.cs` - Model for a single plugin log entry (timestamp, level, source, message, exception)
- `PluginLogService.cs` - Thread-safe ring-buffer plugin log service with dual-logging, filtering, and text export

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/`

- `IRecommendationCacheService.cs`
- `IRecommendationEngine.cs`
- `RecommendationCacheService.cs`
- `RecommendationResult.cs`
- `RecommendedItem.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/`

- `CollaborativeFilter.cs`
- `ContentAffinityResolver.cs`
- `ContentScoring.cs`
- `DiversityReranker.cs`
- `Engine.cs`
- `EngineConstants.cs`
- `PreferenceBuilder.cs`
- `ReasonResolver.cs`
- `SimilarityComputer.cs`
- `TemporalFeatures.cs`
- `TrainingService.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Engine/Training/`

- `DiscoveryFeedbackExampleBuilder.cs`
- `LibraryItemMetadata.cs`
- `TrainingDataBuilder.cs`
- `TrainingFeatureComputer.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Playlist/`

- `IRecommendationPlaylistService.cs`
- `PlaylistSyncResult.cs`
- `RecommendationPlaylistService.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/Scoring/`

- `CandidateFeatures.cs`
- `DefaultWeights.cs`
- `EnsembleScoringStrategy.cs`
- `EnsembleDiagnostics.cs`
- `HeuristicScoringStrategy.cs`
- `IScoringStrategy.cs`
- `LearnedScoringStrategy.cs`
- `NeuralFeatureImportance.cs`
- `NeuralScoringStrategy.cs`
- `RankingMetrics.cs`
- `ScoreExplanation.cs`
- `ScoringHelper.cs`
- `StrategySelector.cs`
- `TrainingExample.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Recommendation/WatchHistory/`

- `IWatchHistoryService.cs`
- `LanguageProfileEntry.cs` - Tracks chosen vs forced audio-language counts with a weighted preference score
- `UserWatchProfile.cs`
- `WatchHistoryService.cs`
- `WatchedItemInfo.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Seerr/`

- `ISeerrIntegrationService.cs`
- `SeerrCleanupResult.cs` - Result model with checked/expired/deleted/failed counts for a Seerr cleanup run
- `SeerrIntegrationService.cs`
- `SeerrMainSettings.cs` - Model of Seerr main settings response used for connection testing
- `SeerrMedia.cs` - Model of media info (type, TMDB ID, status) attached to a Seerr request
- `SeerrMediaDetails.cs` - Model of Seerr movie/TV detail response resolving a display title from title or name
- `SeerrPageInfo.cs` - Pagination metadata model (page, pages, results, pageSize) from the Seerr API
- `SeerrRequest.cs` - Model of a single Seerr media request (id, createdAt, status, media)
- `SeerrRequestPage.cs` - Model of a paginated Seerr /api/v1/request response with null-safe results list

`Jellyfin.Plugin.JellyfinHelper/Services/Seerr/Discovery/`

- `AllowedQualityProfile.cs`
- `DiscoveryCacheService.cs`
- `DiscoveryFeedbackEntry.cs`
- `DiscoveryFeedbackResult.cs`
- `DiscoveryFeedbackStore.cs`
- `DiscoveryInteractionStatus.cs`
- `DiscoveryRecommendation.cs`
- `DiscoveryResult.cs`
- `ExternalCandidateFeatureBuilder.cs`
- `IDiscoveryFeedbackStore.cs`
- `ISeerrDiscoveryService.cs`
- `NullableDateTimeConverter.cs`
- `ParentalRatingHelper.cs`
- `SeerrCastMember.cs`
- `SeerrCredits.cs`
- `SeerrCrewMember.cs`
- `SeerrDiscoveryService.cs`
- `SeerrMediaDetailResponse.cs`
- `SeerrPermissionExtensions.cs`
- `SeerrPermissions.cs`
- `SeerrQualityProfile.cs`
- `SeerrRootFolder.cs`
- `SeerrServiceInfo.cs`
- `SeerrUser.cs`
- `SeerrUserPage.cs`
- `SeerrUserPageInfo.cs` - Pagination metadata model for paginated Seerr user API responses
- `TmdbDiscoverItem.cs`
- `TmdbDiscoverResponse.cs`
- `TmdbGenreMap.cs`
- `UserRequestPermissionResult.cs`

`Jellyfin.Plugin.JellyfinHelper/Services/Statistics/`

- `IMediaStatisticsService.cs` - Interface for the service that calculates media file statistics per library type
- `IStatisticsCacheService.cs` - Interface for persisting and loading the latest full statistics scan to/from disk
- `LibraryStatistics.cs` - Per-library statistics model: file sizes/counts, codec/quality breakdowns, health checks
- `MediaStatisticsResult.cs` - Aggregated media scan result grouping libraries by type with computed totals
- `MediaStatisticsService.cs` - Recursively scans libraries computing size, codec, resolution, and health statistics
- `StatisticsCacheService.cs` - Persists the latest statistics result to disk as JSON via atomic write

`Jellyfin.Plugin.JellyfinHelper/Services/Timeline/`

- `BaselineDirectoryEntry.cs`
- `GrowthTimelineBaseline.cs`
- `GrowthTimelinePoint.cs`
- `GrowthTimelineResult.cs`
- `GrowthTimelineService.cs`
- `IGrowthTimelineService.cs`
- `ILibraryInsightsService.cs`
- `LibraryInsightEntry.cs`
- `LibraryInsightsResult.cs`
- `LibraryInsightsService.cs`
- `TimelineAggregator.cs`

`Jellyfin.Plugin.JellyfinHelper/js/`

- `discovery-sidebar.js`


### Service Registration

Most services are registered as **singletons** in `PluginServiceRegistrator.cs`:

```csharp
serviceCollection.AddSingleton<ICleanupConfigHelper, CleanupConfigHelper>();
serviceCollection.AddSingleton<ICleanupTrackingService, CleanupTrackingService>();
serviceCollection.AddSingleton<ITrashService, TrashService>();
serviceCollection.AddSingleton<IPluginConfigurationService, PluginConfigurationService>();
serviceCollection.AddSingleton<IPluginLogService, PluginLogService>();
serviceCollection.AddSingleton<IMediaStatisticsService, MediaStatisticsService>();
serviceCollection.AddSingleton<IFolderBrowserService, FolderBrowserService>();
// (additional services omitted for brevity - see PluginServiceRegistrator.cs for the complete list)
```

A few registrations use other lifetimes where appropriate:

```csharp
serviceCollection.AddScoped<ModelBindingLogFilter>();                    // per-request action filter
serviceCollection.AddHostedService<DiscoverySidebarInjectionService>();  // startup re-injection of the sidebar script
serviceCollection.AddHttpClient("ArrIntegration", /* ... */);            // named HttpClient factories
```

### TaskMode Pattern

All cleanup tasks follow the three-mode pattern:

```csharp
public enum TaskMode
{
    Deactivate,  // Skip entirely - no work done
    DryRun,      // Analyze and report - no changes made
    Activate     // Full execution - changes applied
}
```

Each task receives its mode from `PluginConfiguration` and logs differently based on mode.

### Recommendation System Architecture

The ML recommendation system uses a layered scoring approach:

```text
User Watch History → Feature Extraction (38 features) → Scoring Strategy → Ranked Results
                                                              ↑
                                                    ┌─────────┴──────────┐
                                                    │  EnsembleScoringStrategy  │
                                                    │                          │
                                                    │  α × Learned (SGD)       │
                                                    │  + (1-α) × Heuristic     │
                                                    │  + β × Neural (MLP)      │
                                                    │  × genre penalty          │
                                                    └──────────────────────────┘
```

- **HeuristicScoringStrategy**: Fixed hand-tuned weights, always available
- **LearnedScoringStrategy**: Linear model trained via SGD on implicit feedback
- **NeuralScoringStrategy**: 4-hidden-layer MLP (38→76→96→48→24→1) with Adam optimizer
- **EnsembleScoringStrategy**: Blends all three with dynamic α/β weighting

Training uses implicit feedback: previously recommended items are compared against current watch data to generate labeled training examples. The EnsembleScoringStrategy records a rolling history of training quality metrics (validation loss, P@K, R@K, NDCG@K) that are persisted across server restarts for future trend analysis.

### Seerr Discovery Architecture

Seerr Discovery extends the recommendation system to suggest external (not-yet-in-library) content by querying the configured Overseerr/Jellyseerr instance:

```text
UserWatchProfiles → Genre/People/Language preferences
                         ↓
         TMDb Discovery via Seerr API (genre + language endpoints)
                         ↓
         Deduplication + Parental Rating Filter + Arr Exclusion
                         ↓
         Phase 1: Pre-score all candidates (genre/rating/recency only)
                         ↓
         Phase 2: Enrich top-20 with credits (actors/directors via Seerr)
                         ↓
         Phase 3: Final score with EnsembleScoringStrategy (full 38 features)
                         ↓
         Top-10 per user → DiscoveryCacheService → Frontend
```

- Coupled to **Seerr configuration** (URL + API Key) - independent of Seerr Cleanup task mode
- Runs as part of `HelperCleanupTask` when `RecommendationsTaskMode != Deactivate`
- Uses `ExternalCandidateFeatureBuilder` to construct the same 38-feature vector used for internal recommendations
- Results persisted to `jellyfin-helper-discovery-results.json` with in-memory cache
- Request submission via `POST /JellyfinHelper/Discovery/Request` with optional Seerr user/server/profile mapping

**Out-of-band request reconciliation:** `SeerrDiscoveryService.ReconcileRequestedItemsAsync(jellyfinUserId, ct)` folds requests a user made outside the discovery UI (e.g. directly in Jellyseerr) back into discovery. It resolves the Jellyfin user to their Seerr user id, paginates `GET /api/v1/request?requestedBy={seerrUserId}`, intersects the returned `(tmdbId, mediaType)` keys with the user's cached recommendations, and for each match records a positive `Requested` feedback signal (`IDiscoveryFeedbackStore.RecordRequested`) and marks it in the cache (`DiscoveryCacheService.MarkAsRequestedAsync`), so the item leaves the visible pool and the next backfill item takes its slot, exactly like an in-discovery request. It is **fail-safe**: an unresolvable user, any Seerr HTTP/JSON error, or an incomplete pagination returns 0 and touches neither the feedback store nor the cache. It runs from two callers: lazily on the view-load path (`UserDiscoveryController.GetMyDiscoveryResults`, throttled per-user via `BuildReconcileKey` for `ReconcileTtl`) for instant UX, and authoritatively at the top of `GenerateForUserAsync` during the scheduled run so training benefits even for users who never open the sidebar.

### Discovery Custom Tab & Script Injection

Discovery results are also displayed on the Jellyfin home screen via a separate script (`js/discovery-sidebar.js`) that is injected into Jellyfin's `index.html`:

```text
Plugin starts → Plugin.InjectScript()  (from the ctor AND again from
                    ↓                    DiscoverySidebarInjectionService at server startup)
    ┌─── File Transformation plugin available? ───┐
    │ YES                                         │ NO
    │ Register callback via reflection            │ Direct index.html write (fallback)
    │ (no filesystem write needed)                │ (requires writable filesystem)
    └─────────────────────────────────────────────┘
                    ↓
    index.html serves <script src="/JellyfinHelper/Discovery/My/script">
                    ↓
    discovery-sidebar.js runs in browser:
      1. Waits for ApiClient to be available
      2. Loads i18n strings from /JellyfinHelper/Translations
      3. Observes DOM for Custom Tab container (.jellyfinhelper.discovery)
      4. Renders discovery cards when container appears
      5. Injects sidebar navigation link (only when the discovery API returns results)
```

**Injection runs unconditionally, twice per server start:** once from the `Plugin` constructor (very early during plugin discovery) and once from `DiscoverySidebarInjectionService` (an `IHostedService`) after DI is built and the web root is mounted. The second run is the robust one: it also self-heals the disk-write fallback after a Jellyfin web update overwrites `index.html`. Injection is idempotent (`DiscoveryScriptTag.RemovalRegex` strips any prior tag; `UpdateIndexHtml` skips the write when the file already matches), so running it twice never stacks tags or churns the file. Whether the user actually *sees* the sidebar item is a separate, client-side decision in `discovery-sidebar.js` (see below). The `<script>` tag is always injected regardless of `RecommendationsTaskMode`.

**Companion plugins (optional):**
- [Custom Tabs Plugin](https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs) - Provides the `.jellyfinhelper.discovery` container on the home page
- [File Transformation Plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) - On-the-fly `index.html` patching without write access

**Deployment Scenarios & Graceful Degradation:**

| Scenario | Behavior |
|----------|----------|
| Both plugins installed | Best experience: Custom Tab shows Discovery on home; File Transformation injects script without filesystem write |
| Only File Transformation | Sidebar navigation link appears, clicking it navigates to `/JellyfinHelper/discoveryPage` (full-page fallback) |
| Only Custom Tabs | Script injection falls back to direct `index.html` write (requires writable filesystem); Custom Tab container renders Discovery |
| Neither plugin installed | Script injection writes to `index.html` (requires writable filesystem); sidebar link navigates to fallback page URL |
| Read-only filesystem + no File Transformation | Script injection cannot write to disk; the plugin logs **one** actionable warning per server start recommending the File Transformation plugin. Discovery is still reachable via the direct URL `/JellyfinHelper/discoveryPage`, but no automatic injection occurs until File Transformation is installed |

**Task Mode Coupling:** Discovery generation shares the `RecommendationsTaskMode` setting. There is no separate toggle. When `RecommendationsTaskMode` is set to `Deactivate`, no Discovery recommendations are generated. This is intentional: Discovery depends on the same watch profile data that the Recommendations engine produces.

The File Transformation registration uses reflection to avoid a hard dependency: the plugin loads the assembly at runtime and constructs a Newtonsoft.Json `JObject` payload with `id`, `fileNamePattern`, `callbackAssembly`, `callbackClass`, and `callbackMethod`.

### Discovery API Endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `GET` | `/JellyfinHelper/Discovery` | Admin | All users' cached discovery results |
| `GET` | `/JellyfinHelper/Discovery/Users` | Admin | List Seerr users (for request attribution) |
| `GET` | `/JellyfinHelper/Discovery/Services/{type}` | Admin | Radarr/Sonarr service info (profiles, root folders) |
| `POST` | `/JellyfinHelper/Discovery/Request` | Admin | Submit request with server/profile/rootFolder overrides |
| `GET` | `/JellyfinHelper/Discovery/My` | User | Current user's own discovery results |
| `GET` | `/JellyfinHelper/Discovery/My/script` | Anonymous | Serves `discovery-sidebar.js` embedded resource |
| `POST` | `/JellyfinHelper/Discovery/My/Request` | User | Submit request as linked Seerr user (no overrides) |
| `POST` | `/JellyfinHelper/Discovery/My/Dismiss` | User | Dismiss a discovery item (training feedback signal) |

## Configuration Page Build System

### Overview

The plugin's configuration page is a **single HTML file** (`configPage.html`) that Jellyfin serves as an embedded resource. To keep development manageable, the HTML is composed at build time from modular source files.

### Build Process

```text
configPage.template.html (shell with placeholders)
    ├── css/*.css           → injected into <style> block
    └── js/*.js             → injected into <script> block
    ═══════════════════════
    → configPage.html       (generated, do not edit directly)
```

The `ComposeConfigPage` MSBuild task (`BuildTasks/ComposeConfigPage.cs`) runs during build:

1. Reads `configPage.template.html`
2. Finds `/* __CSS_MODULES__ */` placeholder → injects all CSS files (ordered)
3. Finds `/* __JS_MODULES__ */` placeholder → injects all JS files (ordered)
4. Writes the composed `configPage.html`

### File Ordering

CSS and JS files are injected in a specific order defined in `ComposeConfigPage.cs`:

```csharp
// CSS order
"Shared.css", "Overview.css", "Codecs.css", "Health.css",
"Trends.css", "Settings.css", "ArrIntegration.css", "Logs.css",
"Recommendations.css"

// JS order  
"Shared.js", "Overview.js", "Codecs.js", "Health.js",
"Trends.js", "Settings.js", "ArrIntegration.js", "Logs.js",
"Recommendations.js", "FolderBrowser.js", "Main.js"
```

`Shared.css`/`Shared.js` must be first (shared utilities), `Main.js` must be last (tab routing + IIFE close).

### Adding a New Tab

1. Create `css/YourTab.css` and `js/YourTab.js`
2. Add the filenames to the ordering arrays in `ComposeConfigPage.cs`
3. Add the tab button and content div to `configPage.template.html`
4. Register the init function in `Main.js`'s tab routing
5. Build to regenerate `configPage.html`

### Important Rules

- **Never edit `configPage.html` directly.** It's overwritten on every build
- **Always edit the source files** in `css/`, `js/`, or `configPage.template.html`
- The `docs/` folder contains a **copy** of the plugin pages for the documentation site
- After changing plugin pages, copy updated files to `docs/` as well

### JavaScript Guidelines

- All JS runs inside an IIFE (Immediately Invoked Function Expression), no global pollution
- Prefer `var` for broader compatibility; `const`/`let` and arrow functions are acceptable
  in utility/helper code (e.g., `Shared.js`) where Jellyfin web client supports ES6+
- Use `T('key', 'fallback')` for all user-visible strings (i18n support)
- Use `apiGet()` / `apiPost()` helpers for API calls (handles auth headers)
- Use `escHtml()` for any user-provided content inserted into HTML

### CSS Guidelines

- Prefix all classes with the tab name (e.g., `recs-*` for Recommendations)
- Support both dark and light modes via `@media (prefers-color-scheme: light)`
- Use relative units (`em`, `%`) for responsive layouts
- Keep specificity low, avoid `!important`

## Adding a New Feature

### New Cleanup Task

1. Create the task class in `ScheduledTasks/` implementing the task pattern
2. Add a `TaskMode` property to `PluginConfiguration`
3. Register the task in `HelperCleanupTask.cs`'s execution pipeline
4. Add UI controls in `js/Settings.js` and `configPage.template.html`
5. Add backup support in `BackupData.cs`, `BackupService.cs`, `BackupValidator.cs`, `BackupSanitizer.cs`
6. Add i18n keys to all language files in `i18n/`
7. Write tests covering all three modes

### New API Endpoint

1. Create or extend a controller in `Api/`
2. Use `[Authorize(Policy = "RequiresElevation")]` for admin-only endpoints where applicable
3. Add request/response DTOs and validation
4. Register required services in `PluginServiceRegistrator.cs`
5. Add integration/unit tests for success and failure paths