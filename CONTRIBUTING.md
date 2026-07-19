<!--
  CONTRIBUTING.md - Contributor guidelines for the Jellyfin Helper plugin.
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

### Test Structure

Tests mirror the source structure:

```text
Jellyfin.Plugin.JellyfinHelper.Tests/
├── PluginServiceRegistratorTests.cs      # DI-container smoke test: every service the plugin depends on must resolve (catches renamed/removed registrations before runtime)
├── PluginTests.cs                        # Plugin bootstrap: constructor Instance publishing, GetPages menu entry, UpdateIndexHtml inject/remove fast-path + idempotency (BUG GUARD against double-injection and mtime churn), OnUninstalling data-file + playlist cleanup with strict prefix + extension guards (must not delete unrelated files)
├── Api/                           # Controller tests
│   ├── BackupControllerExtendedTests.cs               # Branch coverage beyond BackupControllerTests: malformed JSON (must return 400 not 500), JSON literal "null" (null-guard after DeserializeBackup), actual chunk-loop body exceeds MaxSize when Content-Length lies (memory-exhaustion defence), whitespace-only body (IsNullOrWhiteSpace vs IsNullOrEmpty guard)
│   ├── DiscoveryControllerTests.cs
│   ├── DiscoveryControllerExtendedTests.cs           # GetSeerrUsers/GetServiceInfo happy paths, SubmitRequest + MarkAsRequestedAsync integration, dismissed/requested filter logic, error paths when the feedback store throws
│   ├── UserDiscoveryControllerTests.cs
│   ├── UserDiscoveryControllerAccessEnabledTests.cs  # Access gate ENABLED - request validation and permission surfaces (complements gate-disabled default)
│   ├── UserDiscoveryControllerSubmitTests.cs         # SubmitMyRequest + DismissItem with gate ENABLED (per-user Seerr identity mapping + feedback-store side effects)
│   ├── FolderBrowserControllerTests.cs               # Thin wrapper over IFolderBrowserService + ILibraryManager; root/list/validate flows and library-path resolution
│   ├── RecommendationControllerTests.cs
│   ├── UserActivityControllerTests.cs
│   ├── TrashControllerTests.cs
│   └── ...
├── Configuration/                 # Config serialization tests
│   ├── PluginConfigurationSerializationTests.cs
│   └── TaskModeTests.cs
├── PluginPages/                   # HTML composition tests
│   ├── ConfigPageTestBase.cs      # Shared base loading configPage.html
│   ├── DiscoverHtmlTests.cs       # Recommendations tab HTML tests
│   └── ...
├── ScheduledTasks/                # Task execution tests
│   ├── CleanTrickplayTrashExclusionTests.cs              # Trash folder exclusion from recursive scan
│   ├── CleanOrphanedSubtitlesTaskProcessLocationTests.cs # Integration test for CleanOrphanedSubtitlesTask.ProcessLocation: library setup, recursive GetDirectories, file-leaf-name enumeration wiring
│   ├── RepairLinksTaskTests.cs                           # Thin orchestration of RepairLinksTask around ILinkRepairService (dry-run flag propagation, cancellation token, progress reporting) - no filesystem I/O
│   ├── RecommendationsTaskTests.cs
│   ├── UserActivityUpdateTaskTests.cs
│   └── ...
├── Services/
│   ├── DateTimeNormalizationTests.cs      # Every branch of the UTC coercion helper used by all result-DTO timestamp setters (guards against Local->SpecifyKind bugs that would silently corrupt cache timestamps)
│   ├── Activity/                  # User activity service tests
│   ├── Arr/                       # Arr integration tests
│   ├── Backup/                    # Backup/restore tests
│   │   ├── BackupServiceTests.cs
│   │   ├── BackupServicePerformanceTests.cs
│   │   └── BackupServiceRestoreConfigTests.cs  # RestoreBackup + RestoreConfiguration when IPluginConfigurationService.IsInitialized=true (invalid language -> "en" fallback, out-of-range clamping, garbage task-mode rejection, Arr-instance list reset)
│   ├── Cleanup/                   # Cleanup task tests
│   │   ├── TrashControllerAccessTests.cs  # CheckAccess API endpoint tests (permission probing)
│   │   ├── TrashControllerRelocateTests.cs # Trash path relocation API endpoint tests
│   │   ├── TrashServiceAccessTests.cs     # CheckPathAccess permission probing tests
│   │   ├── TrashServiceGuardTests.cs      # Defense-in-depth: prevent re-trashing items already in trash
│   │   ├── TrashServicePathLengthTests.cs # ResolveCollision stays within OS MAX_PATH (Windows 259 / Linux 4095)
│   │   ├── TrashServiceRelocateTests.cs   # RelocateTrashContents unit tests (move, collision, safety)
│   │   └── TrashServiceInternalHelpersTests.cs # Edge-case coverage of the internal static helpers TruncateToSize / MeasureString / ExtractOriginalName / TryParseTrashTimestamp / PathComparison (NAME_MAX=255 on Unix, surrogate pairs, empty budget, mojibake avoidance)
│   ├── Common/                    # Shared cross-service helper tests
│   │   ├── AtomicFileTests.cs             # Atomic-write helper contract: UTF-8 no-BOM payload, temp-file cleanup on success/failure, transient-IO retry with backoff, async overload honours CancellationToken (per-test tmp dirs for isolation)
│   │   └── BatchFallbackHelperTests.cs    # try-batch/fall-back contract: cancellation propagates, non-fatal exceptions degrade to fallback
│   ├── ConfigAccess/              # Configuration access tests
│   ├── FileTransformation/        # File Transformation plugin integration tests
│   │   ├── DiscoveryScriptTagTests.cs      # Single source of truth for the sidebar script tag: Build() produces well-formed HTML with URL-escaped version, RemovalRegex round-trips every Build() output, edge cases (null/empty/whitespace/special chars), regex must NOT eat unrelated <script> tags
│   │   ├── PatchRequestPayloadTests.cs     # JSON DTO contract: property must (de)serialize to "contents" (lowercase-camel) so File Transformation callback payloads round-trip correctly
│   │   └── TransformationPatchesTests.cs   # TransformationPatches.IndexHtml callback: null/empty-Contents tolerance, no-op when </body> missing, single insert before first </body>, idempotent re-serving (removes previous script tag before re-inserting), case-insensitive </BODY>
│   ├── FolderBrowser/             # Server-side folder browsing tests
│   │   ├── FolderBrowserDtoTests.cs        # DTO defaults, mutability, reference-equality semantics for FolderEntry / FolderBrowseResult (guards against accidental conversion to `record` which would break identity-based caching)
│   │   └── FolderBrowserServiceTests.cs    # Full-behaviour coverage of FolderBrowserService: GetRoots per-OS branches (forced via internal ctor overload), ValidatePath (empty / traversal / null-byte / relative / non-existent / file-vs-dir / access-denied), GetChildren (empty dir, HasChildren, hidden dirs, symlinks, broken symlinks, UNC)
│   ├── Link/                      # Link repair tests
│   │   └── SymlinkHelperTests.cs           # Integration tests for the production SymlinkHelper against a real filesystem (per-test isolated temp dirs; graceful skip when symlink privileges are unavailable); a meta-test guarantees at least Linux/macOS CI actually exercises the symlink branch
│   ├── PluginLog/                 # Plugin log tests
│   ├── Seerr/                     # Seerr integration tests
│   │   ├── SeerrIntegrationServiceTests.cs
│   │   ├── SeerrMediaDetailsTests.cs
│   │   └── Discovery/            # Seerr Discovery tests
│   │       ├── DiscoveryCacheServiceTests.cs            # DiscoveryCacheService disk + memory persistence contract; uses Plugin.Instance.DataFolderPath so each test writes to a real (per-test) file to avoid cross-test contamination
│   │       ├── DiscoveryFeedbackStoreTests.cs
│   │       ├── DiscoveryRegressionTests.cs              # v2.1.0.3 regression tests (ServerId=0, profile dedup, MissingMethodException)
│   │       ├── ExternalCandidateFeatureBuilderTests.cs  # inference↔training feature parity (genre-exposure + popularity skew guards)
│   │       ├── NullableDateTimeConverterTests.cs        # TMDb/Seerr empty-string / malformed release_date / first_air_date values must degrade to null instead of throwing JsonException (whole response would otherwise be dropped)
│   │       ├── ParentalRatingHelperTests.cs
│   │       ├── SeerrDiscoveryDtoTests.cs                # Wire contract for the Seerr / TMDb DTO family used by the discovery pipeline: lower-camel property names, defaults, round-trip
│   │       ├── SeerrDiscoveryServiceTests.cs
│   │       ├── SeerrDiscoveryServiceHelperTests.cs      # Pure-static internal helpers on SeerrDiscoveryService: StampMediaType, BuildGenreIdList, GetPrimaryLanguageForDiscovery, etc.
│   │       ├── SeerrDiscoveryServiceHttpTests.cs        # HTTP-driven public surface via scripted HttpMessageHandler: SubmitRequestAsync, GetServiceInfoAsync, GetSeerrUsersAsync, ResolveSeerrUserIdAsync, GetUserRequestPermissionsAsync (mutates Plugin.Instance.Configuration → serialised via ConfigOverride collection)
│   │       ├── SeerrDiscoveryServiceUserResolutionTests.cs # User-resolution + quality-profile-list helpers: FindSeerrUserByJellyfinId, BuildAllowedProfileList
│   │       ├── SeerrDiscoveryServiceReasonTests.cs      # Pure-static DetermineReason: reasonPersonNamed/reasonGenre/reasonTrending/reasonPopular branch selection, boundary-guards on 0.3/0.7/0.8 gates, priority ordering, preferred-people intersection tie-break
│   │       ├── SeerrPermissionExtensionsTests.cs        # SECURITY: authorization gate for Discovery requests - HasPermission zero-flag guard, admin bypass, granular per-media-type flags, unknown-media-type rejection, null-user throws
│   │       └── TmdbDiscoverItemTests.cs                 # TmdbDiscoverItem DTO parsing surface between Seerr/TMDb JSON and the recommendation engine: GenreIds null-coalesce to empty list (TMDb sometimes emits null), DisplayTitle Title→Name→"Unknown" fallback chain (BUG GUARD: only NULL falls through, not empty string, else TV shows silently rename), EffectiveReleaseDate ReleaseDate→FirstAirDate for TV items (recency-scoring depends on this), defaults (Adult=false safe posture, MediaType="movie", KnownPeople=null to distinguish "not enriched yet" vs "enriched empty"), JSON round-trip via NullableDateTimeConverter (empty-string releaseDate must not blow up the whole batch)
│   ├── Statistics/                # Statistics service tests
│   ├── Timeline/                  # Growth timeline tests
│   │   └── GrowthTimelineSymlinkTests.cs  # ReparsePoint guard prevents StackOverflow on circular symlinks/junctions
│   └── Recommendation/            # Recommendation engine tests
│       ├── Engine/                # Core engine logic tests
│       │   ├── CollaborativeFilterTests.cs
│       │   ├── ContentScoringTests.cs
│       │   ├── DiversityRerankerTests.cs
│       │   ├── EngineBoxSetTests.cs                   # BoxSet pure-static helpers on Engine: BuildWatchedBoxSetCounts, ComputeCollectionProgressionBoostLive (train/serve parity guard)
│       │   ├── EngineCommunityPopularityTests.cs      # Engine.BuildCommunityPopularityMap - shared cold-start community-popularity computation used by both the batch path and the live path (identical output required)
│       │   ├── EngineExceedsMaxRatingTests.cs         # SECURITY-CRITICAL parental-rating gate (private-static ExceedsMaxRating): null max = unrestricted, missing item rating on a restricted profile = REJECT (fail-safe against unrated adult content leaking into child profiles), inclusive boundary (=max allowed, max+1 rejected), zero-max edge case, reflection-based invocation with backing-field fallback so tests survive BaseItem API drift
│       │   ├── EngineHelperTests.cs                   # Pure-static internal helper methods on Engine that cannot be exercised end-to-end without spinning up the full recommendation pipeline
│       │   ├── PreferenceBuilderTests.cs
│       │   ├── ReasonResolverTests.cs                 # Every branch of ReasonResolver.DetermineReason + private resolvers + StripWatchedItemsForResponse; EngineConstants thresholds are treated as contract
│       │   ├── SimilarityComputerTests.cs             # People-batch (GetPeopleNamesByItems) + per-item fallback; weighted PeopleSimilarity overload (Roadmap v3 C2)
│       │   ├── TemporalFeaturesTests.cs               # Day-of-week / hour-of-day / weekend affinity computation
│       │   ├── TrainingServiceTests.cs                # TrainingService uses a process-wide static gate (TrainGate) - tests must be serialised via the ConfigOverride collection
│       │   └── Training/
│       │       ├── CollectionProgressionBoostTests.cs # Locks the diminishing-returns formula 0.3+(n-1)×0.2 (Roadmap v3 C3) - guards train/serve parity
│       │       ├── TrainingDataBuilderTests.cs        # F-01 regression: Phase 3 cross-user random negatives must be deterministic across runs (guards against Random.Shared reintroduction)
│       │       └── TrainingFeatureComputerTests.cs    # Shared training-time feature helpers must remain in lock-step with the live scoring path (feature-parity guard)
│       ├── Playlist/              # Playlist sync tests
│       │   └── RecommendationPlaylistServiceTests.cs
│       ├── Scoring/               # Strategy-specific tests
│       │   ├── ScoringStrategyTests.cs
│       │   ├── NeuralScoringStrategyTests.cs
│       │   ├── EnsembleScoringStrategyAdvancedTests.cs # Previously uncovered branches on EnsembleScoringStrategy: ScoreWithOffset, ScoreWithExplanationAndOffset, ApplyCohortFeedback, constructor guards
│       │   ├── StrategySelectorTests.cs                # Cohort router: exploration-gate activation, deterministic user-hash bucketing, strategy routing
│       │   ├── NeuralFeatureImportanceTests.cs         # Permutation-based feature importance analyzer for the neural model - which features drive the predictions
│       │   ├── ScoreExplanationTests.cs
│       │   ├── TrainingExampleTests.cs
│       │   └── RankingMetricsTests.cs
│       ├── WatchHistory/          # Watch history service tests
│       │   ├── LanguageAffinityTests.cs
│       │   ├── UserWatchProfileTests.cs        # Cache invalidation for lazy props, case-insensitive dictionary re-assignment (guards case-sensitive cache-deserialisation from silently regressing genre/language matching), null-safe setters, TopPeople boundaries (min-count filter, tie-break, cap at 20)
│       │   ├── WatchHistoryCompatTests.cs      # IUserManager API compatibility (MissingMethodException handling)
│       │   └── WatchHistoryServiceTests.cs
│       ├── RecommendationCacheServiceTests.cs
│       ├── RecommendationCacheServiceExtendedTests.cs  # Defensive branches missed by RecommendationCacheServiceTests: null-argument guard, directory auto-creation when DataPath does not exist, load of a file containing literal "null"
│       ├── RecommendationDtoTests.cs
│       └── RecommendationEngineTests.cs
└── TestFixtures/                  # Shared test helpers
```

### Test Guidelines

- Use `Moq` for mocking Jellyfin interfaces
- Test both happy path and edge cases
- Scheduled task tests should verify all three modes: Activate, DryRun, Deactivate
- Backup tests should cover round-trip (create → serialize → deserialize → restore)
- Recommendation tests should verify scoring determinism and feature vector consistency

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
│   ├── DiscoveryController.cs           # Seerr Discovery API - admin (all users, services, requests)
│   ├── UserDiscoveryController.cs       # Seerr Discovery API - user-facing (own results, requests)
│   ├── DiscoveryRequestDto.cs           # Request submission DTO (TmdbId, MediaType, overrides)
│   ├── DiscoveryDismissDto.cs           # Dismiss request DTO (TmdbId, MediaType)
│   ├── FolderBrowserController.cs       # Folder browser API (server-side directory listing)
│   ├── RequestResult.cs                 # Generic success/failure response model
│   ├── GrowthTimelineController.cs      # Library growth timeline API
│   ├── LibraryInsightsController.cs     # Library insights API
│   ├── LogsController.cs               # Plugin logs API
│   ├── MediaStatisticsController.cs     # Media statistics API
│   ├── RecommendationController.cs      # ML recommendations API
│   ├── SeerrController.cs              # Jellyseerr/Overseerr integration API
│   ├── TranslationsController.cs        # i18n translations API
│   ├── TrashController.cs               # Trash bin API
│   ├── TrashPathQueryRequest.cs         # DTO for querying trash folders at a specific path
│   ├── TrashRelocateRequest.cs          # DTO for relocating trash between paths
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
│   │   ├── BackupData.cs        # Backup data model
│   │   ├── BackupService.cs     # Create/restore backup
│   │   ├── BackupValidator.cs   # Comprehensive input validation
│   │   └── BackupSanitizer.cs   # Clamp/normalize values
│   ├── Common/                      # Shared cross-service helpers
│   │   ├── AtomicFile.cs            # Atomic text-file write (temp+move) with bounded retry on transient AV/indexer sharing violations
│   │   └── BatchFallbackHelper.cs   # try-batch/fall-back-per-item wrapper (Jellyfin 12+ batch APIs)
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
│   │   │   │   └── DiscoveryFeedbackExampleBuilder.cs # Phase 4: training from discovery interactions
│   │   │   ├── PreferenceBuilder.cs # Genre/studio/tag/people preference extraction
│   │   │   ├── DiversityReranker.cs # MMR-based diversity reranking
│   │   │   ├── TemporalFeatures.cs  # Day-of-week/hour-of-day affinity computation
│   │   │   ├── ReasonResolver.cs    # Human-readable recommendation explanations
│   │   │   ├── SimilarityComputer.cs # Genre/people/tag similarity
│   │   │   ├── CollaborativeFilter.cs # Jaccard + IDF co-occurrence
│   │   │   ├── ContentScoring.cs    # Recency, rating, engagement scoring
│   │   │   └── EngineConstants.cs   # Shared constants (thresholds, windows)
│   │   ├── Scoring/                 # Pluggable scoring strategies
│   │   │   ├── IScoringStrategy.cs
│   │   │   ├── ITrainableStrategy.cs
│   │   │   ├── HeuristicScoringStrategy.cs  # Fixed weights (rule-based)
│   │   │   ├── LearnedScoringStrategy.cs    # Adaptive ML (SGD linear)
│   │   │   ├── NeuralScoringStrategy.cs     # MLP with Adam optimizer
│   │   │   ├── EnsembleScoringStrategy.cs   # Blends heuristic + learned + neural
│   │   │   ├── StrategySelector.cs          # A/B testing: deterministic user→strategy routing
│   │   │   ├── NeuralFeatureImportance.cs   # Permutation-based feature importance for MLP
│   │   │   ├── CandidateFeatures.cs         # 31-feature vector with FeatureIndex enum
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
│   │   ├── PatchRequestPayload.cs    # Payload model for transformation callbacks
│   │   └── TransformationPatches.cs  # index.html script injection (on-the-fly via File Transformation plugin)
│   ├── Seerr/                   # Jellyseerr/Overseerr integration
│   │   ├── ISeerrIntegrationService.cs   # Seerr cleanup (request removal)
│   │   ├── SeerrIntegrationService.cs
│   │   └── Discovery/               # Seerr Discovery (external recommendations)
│   │       ├── ISeerrDiscoveryService.cs
│   │       ├── SeerrDiscoveryService.cs  # Orchestrator: profiles → TMDb query → scoring → results
│   │       ├── DiscoveryCacheService.cs  # Disk + memory persistence
│   │       ├── ExternalCandidateFeatureBuilder.cs  # Builds 31-feature vector for TMDb items
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
    └── js/                      # Per-tab JS modules + .eslintrc.json
        ├── Shared.js, Overview.js, Codecs.js, Health.js
        ├── Trends.js, Settings.js, ArrIntegration.js, Logs.js
        ├── Recommendations.js    # Discover tab logic
        ├── FolderBrowser.js      # Folder browser UI (path picker for settings)
        └── Main.js               # Tab routing, IIFE close
```

### Service Registration

All services are registered as **singletons** in `PluginServiceRegistrator.cs`:

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
User Watch History → Feature Extraction (31 features) → Scoring Strategy → Ranked Results
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
- **NeuralScoringStrategy**: 4-hidden-layer MLP (31→48→24→12→6→1) with Adam optimizer
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
         Phase 3: Final score with EnsembleScoringStrategy (full 31 features)
                         ↓
         Top-10 per user → DiscoveryCacheService → Frontend
```

- Coupled to **Seerr configuration** (URL + API Key) - independent of Seerr Cleanup task mode
- Runs as part of `HelperCleanupTask` when `RecommendationsTaskMode != Deactivate`
- Uses `ExternalCandidateFeatureBuilder` to construct the same 31-feature vector used for internal recommendations
- Results persisted to `jellyfin-helper-discovery-results.json` with in-memory cache
- Request submission via `POST /JellyfinHelper/Discovery/Request` with optional Seerr user/server/profile mapping

### Discovery Custom Tab & Script Injection

Discovery results are also displayed on the Jellyfin home screen via a separate script (`js/discovery-sidebar.js`) that is injected into Jellyfin's `index.html`:

```text
Plugin starts → Plugin.InjectScript()
                    ↓
    ┌─── File Transformation plugin available? ───┐
    │ YES                                         │ NO
    │ Register callback via reflection            │ Direct index.html write
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
      5. Injects sidebar navigation link
```

**Companion plugins (optional):**
- [Custom Tab Plugin](https://github.com/JellyPlugins/jellyfin-plugin-custom-tabs) - Provides the `.jellyfinhelper.discovery` container on the home page
- [File Transformation Plugin](https://github.com/JellyPlugins/jellyfin-plugin-file-transformation) - On-the-fly `index.html` patching without write access

**Deployment Scenarios & Graceful Degradation:**

| Scenario | Behavior |
|----------|----------|
| Both plugins installed | Best experience: Custom Tab shows Discovery on home; File Transformation injects script without filesystem write |
| Only File Transformation | Sidebar navigation link appears, clicking it navigates to `/JellyfinHelper/discoveryPage` (full-page fallback) |
| Only Custom Tabs | Script injection falls back to direct `index.html` write (requires writable filesystem); Custom Tab container renders Discovery |
| Neither plugin installed | Script injection writes to `index.html` (requires writable filesystem); sidebar link navigates to fallback page URL |
| Read-only filesystem + no File Transformation | Script injection fails silently (logged at Debug level); Discovery is still accessible via direct URL `/JellyfinHelper/discoveryPage` but no automatic injection occurs |

**Task Mode Coupling:** Discovery generation shares the `RecommendationsTaskMode` setting — there is no separate toggle. When `RecommendationsTaskMode` is set to `Deactivate`, no Discovery recommendations are generated. This is intentional: Discovery depends on the same watch profile data that the Recommendations engine produces.

The File Transformation registration uses reflection to avoid a hard dependency - the plugin loads the assembly at runtime and constructs a Newtonsoft.Json `JObject` payload with `id`, `fileNamePattern`, `callbackAssembly`, `callbackClass`, and `callbackMethod`.

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

- **Never edit `configPage.html` directly** - it's overwritten on every build
- **Always edit the source files** in `css/`, `js/`, or `configPage.template.html`
- The `docs/` folder contains a **copy** of the plugin pages for the documentation site
- After changing plugin pages, copy updated files to `docs/` as well

### JavaScript Guidelines

- All JS runs inside an IIFE (Immediately Invoked Function Expression) - no global pollution
- Prefer `var` for broader compatibility; `const`/`let` and arrow functions are acceptable
  in utility/helper code (e.g., `Shared.js`) where Jellyfin web client supports ES6+
- Use `T('key', 'fallback')` for all user-visible strings (i18n support)
- Use `apiGet()` / `apiPost()` helpers for API calls (handles auth headers)
- Use `escHtml()` for any user-provided content inserted into HTML

### CSS Guidelines

- Prefix all classes with the tab name (e.g., `recs-*` for Recommendations)
- Support both dark and light modes via `@media (prefers-color-scheme: light)`
- Use relative units (`em`, `%`) for responsive layouts
- Keep specificity low - avoid `!important`

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