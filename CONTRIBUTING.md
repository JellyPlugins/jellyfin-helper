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
â”œâ”€â”€ Api/                           # Controller tests
â”‚   â”œâ”€â”€ DiscoveryControllerTests.cs
â”‚   â”œâ”€â”€ UserDiscoveryControllerTests.cs
â”‚   â”œâ”€â”€ RecommendationControllerTests.cs
â”‚   â”œâ”€â”€ UserActivityControllerTests.cs
â”‚   â”œâ”€â”€ TrashControllerTests.cs
â”‚   â””â”€â”€ ...
â”œâ”€â”€ Configuration/                 # Config serialization tests
â”‚   â”œâ”€â”€ PluginConfigurationSerializationTests.cs
â”‚   â””â”€â”€ TaskModeTests.cs
â”œâ”€â”€ PluginPages/                   # HTML composition tests
â”‚   â”œâ”€â”€ ConfigPageTestBase.cs      # Shared base loading configPage.html
â”‚   â”œâ”€â”€ DiscoverHtmlTests.cs       # Recommendations tab HTML tests
â”‚   â””â”€â”€ ...
â”œâ”€â”€ ScheduledTasks/                # Task execution tests
â”‚   â”œâ”€â”€ CleanTrickplayTrashExclusionTests.cs  # Trash folder exclusion from recursive scan
â”‚   â”œâ”€â”€ RecommendationsTaskTests.cs
â”‚   â”œâ”€â”€ UserActivityUpdateTaskTests.cs
â”‚   â””â”€â”€ ...
â”œâ”€â”€ Services/
â”‚   â”œâ”€â”€ Activity/                  # User activity service tests
â”‚   â”œâ”€â”€ Arr/                       # Arr integration tests
â”‚   â”œâ”€â”€ Backup/                    # Backup/restore tests
â”‚   â”œâ”€â”€ Cleanup/                   # Cleanup task tests
â”‚   â”‚   â”œâ”€â”€ TrashControllerAccessTests.cs  # CheckAccess API endpoint tests (permission probing)
â”‚   â”‚   â”œâ”€â”€ TrashControllerRelocateTests.cs # Trash path relocation API endpoint tests
â”‚   â”‚   â”œâ”€â”€ TrashServiceAccessTests.cs     # CheckPathAccess permission probing tests
â”‚   â”‚   â”œâ”€â”€ TrashServiceGuardTests.cs      # Defense-in-depth: prevent re-trashing items already in trash
â”‚   â”‚   â”œâ”€â”€ TrashServicePathLengthTests.cs # ResolveCollision stays within OS MAX_PATH (Windows 259 / Linux 4095)
â”‚   â”‚   â””â”€â”€ TrashServiceRelocateTests.cs   # RelocateTrashContents unit tests (move, collision, safety)
â”‚   â”œâ”€â”€ ConfigAccess/              # Configuration access tests
â”‚   â”œâ”€â”€ Link/                      # Link repair tests
â”‚   â”œâ”€â”€ PluginLog/                 # Plugin log tests
â”‚   â”œâ”€â”€ Seerr/                     # Seerr integration tests
â”‚   â”‚   â”œâ”€â”€ SeerrIntegrationServiceTests.cs
â”‚   â”‚   â”œâ”€â”€ SeerrMediaDetailsTests.cs
â”‚   â”‚   â””â”€â”€ Discovery/            # Seerr Discovery tests
â”‚   â”‚       â”œâ”€â”€ DiscoveryFeedbackStoreTests.cs
â”‚   â”‚       â”œâ”€â”€ DiscoveryRegressionTests.cs  # v2.1.0.3 regression tests (ServerId=0, profile dedup, MissingMethodException)
â”‚   â”‚       â”œâ”€â”€ SeerrDiscoveryServiceTests.cs
â”‚   â”‚       â””â”€â”€ ParentalRatingHelperTests.cs
â”‚   â”œâ”€â”€ Statistics/                # Statistics service tests
â”‚   â”œâ”€â”€ Timeline/                  # Growth timeline tests
â”‚   â”‚   â””â”€â”€ GrowthTimelineSymlinkTests.cs  # ReparsePoint guard prevents StackOverflow on circular symlinks/junctions
â”‚   â””â”€â”€ Recommendation/            # Recommendation engine tests
â”‚       â”œâ”€â”€ Engine/                # Core engine logic tests
â”‚       â”‚   â”œâ”€â”€ CollaborativeFilterTests.cs
â”‚       â”‚   â”œâ”€â”€ ContentScoringTests.cs
â”‚       â”‚   â””â”€â”€ PreferenceBuilderTests.cs
â”‚       â”œâ”€â”€ Playlist/              # Playlist sync tests
â”‚       â”‚   â””â”€â”€ RecommendationPlaylistServiceTests.cs
â”‚       â”œâ”€â”€ Scoring/               # Strategy-specific tests
â”‚       â”‚   â”œâ”€â”€ ScoringStrategyTests.cs
â”‚       â”‚   â”œâ”€â”€ NeuralScoringStrategyTests.cs
â”‚       â”‚   â”œâ”€â”€ ScoreExplanationTests.cs
â”‚       â”‚   â”œâ”€â”€ TrainingExampleTests.cs
â”‚       â”‚   â””â”€â”€ RankingMetricsTests.cs
â”‚       â”œâ”€â”€ WatchHistory/          # Watch history service tests
â”‚       â”‚   â”œâ”€â”€ LanguageAffinityTests.cs
â”‚       â”‚   â”œâ”€â”€ WatchHistoryCompatTests.cs  # IUserManager API compatibility (MissingMethodException handling)
â”‚       â”‚   â””â”€â”€ WatchHistoryServiceTests.cs
â”‚       â”œâ”€â”€ RecommendationCacheServiceTests.cs
â”‚       â”œâ”€â”€ RecommendationDtoTests.cs
â”‚       â””â”€â”€ RecommendationEngineTests.cs
â””â”€â”€ TestFixtures/                  # Shared test helpers
```

### Test Guidelines

- Use `Moq` for mocking Jellyfin interfaces
- Test both happy path and edge cases
- Scheduled task tests should verify all three modes: Activate, DryRun, Deactivate
- Backup tests should cover round-trip (create â†’ serialize â†’ deserialize â†’ restore)
- Recommendation tests should verify scoring determinism and feature vector consistency

## Architecture Overview

### Project Structure

```text
Jellyfin.Plugin.JellyfinHelper/
â”œâ”€â”€ BuildTasks/
â”‚   â””â”€â”€ ComposeConfigPage.cs     # MSBuild task for config page composition
â”œâ”€â”€ i18n/                        # Internationalization files (en, de, fr, es, pt, sv, zh, tr)
â”œâ”€â”€ Plugin.cs                    # Entry point, web page registration, script injection
â”œâ”€â”€ PluginServiceRegistrator.cs  # DI registration for all services
â”œâ”€â”€ MediaExtensions.cs           # Extension methods for media analysis
â”œâ”€â”€ js/
â”‚   â””â”€â”€ discovery-sidebar.js     # Discovery Custom Tab + sidebar script (embedded resource, injected into index.html)
â”œâ”€â”€ Api/
â”‚   â”œâ”€â”€ ArrIntegrationController.cs      # Radarr/Sonarr integration API
â”‚   â”œâ”€â”€ BackupController.cs              # Backup/restore API
â”‚   â”œâ”€â”€ CleanupStatisticsController.cs   # Cleanup statistics API
â”‚   â”œâ”€â”€ ConfigurationController.cs       # Plugin configuration API
â”‚   â”œâ”€â”€ DiscoveryController.cs           # Seerr Discovery API - admin (all users, services, requests)
â”‚   â”œâ”€â”€ UserDiscoveryController.cs       # Seerr Discovery API - user-facing (own results, requests)
â”‚   â”œâ”€â”€ DiscoveryRequestDto.cs           # Request submission DTO (TmdbId, MediaType, overrides)
â”‚   â”œâ”€â”€ DiscoveryDismissDto.cs           # Dismiss request DTO (TmdbId, MediaType)
â”‚   â”œâ”€â”€ FolderBrowserController.cs       # Folder browser API (server-side directory listing)
â”‚   â”œâ”€â”€ RequestResult.cs                 # Generic success/failure response model
â”‚   â”œâ”€â”€ GrowthTimelineController.cs      # Library growth timeline API
â”‚   â”œâ”€â”€ LibraryInsightsController.cs     # Library insights API
â”‚   â”œâ”€â”€ LogsController.cs               # Plugin logs API
â”‚   â”œâ”€â”€ MediaStatisticsController.cs     # Media statistics API
â”‚   â”œâ”€â”€ RecommendationController.cs      # ML recommendations API
â”‚   â”œâ”€â”€ SeerrController.cs              # Jellyseerr/Overseerr integration API
â”‚   â”œâ”€â”€ TranslationsController.cs        # i18n translations API
â”‚   â”œâ”€â”€ TrashController.cs               # Trash bin API
â”‚   â”œâ”€â”€ TrashPathQueryRequest.cs         # DTO for querying trash folders at a specific path
â”‚   â”œâ”€â”€ TrashRelocateRequest.cs          # DTO for relocating trash between paths
â”‚   â””â”€â”€ UserActivityController.cs        # User activity insights API
â”œâ”€â”€ Configuration/
â”‚   â”œâ”€â”€ PluginConfiguration.cs   # All config properties with defaults
â”‚   â”œâ”€â”€ TaskMode.cs              # Deactivate / DryRun / Activate enum
â”‚   â””â”€â”€ ArrInstanceConfig.cs     # Per-instance Arr configuration
â”œâ”€â”€ Services/
â”‚   â”œâ”€â”€ Activity/                    # User watch activity tracking
â”‚   â”‚   â”œâ”€â”€ IUserActivityInsightsService.cs
â”‚   â”‚   â”œâ”€â”€ UserActivityInsightsService.cs
â”‚   â”‚   â”œâ”€â”€ IUserActivityCacheService.cs
â”‚   â”‚   â”œâ”€â”€ UserActivityCacheService.cs
â”‚   â”‚   â”œâ”€â”€ UserActivityResult.cs
â”‚   â”‚   â”œâ”€â”€ UserActivitySummary.cs
â”‚   â”‚   â””â”€â”€ UserItemActivity.cs
â”‚   â”œâ”€â”€ Backup/
â”‚   â”‚   â”œâ”€â”€ BackupData.cs        # Backup data model
â”‚   â”‚   â”œâ”€â”€ BackupService.cs     # Create/restore backup
â”‚   â”‚   â”œâ”€â”€ BackupValidator.cs   # Comprehensive input validation
â”‚   â”‚   â””â”€â”€ BackupSanitizer.cs   # Clamp/normalize values
â”‚   â”œâ”€â”€ FolderBrowser/               # Server-side folder browsing
â”‚   â”‚   â”œâ”€â”€ IFolderBrowserService.cs # Interface for folder listing
â”‚   â”‚   â”œâ”€â”€ FolderBrowserService.cs  # Implementation: lists directories with safety guards
â”‚   â”‚   â”œâ”€â”€ FolderBrowseResult.cs    # Browse result container (entries + current path)
â”‚   â”‚   â””â”€â”€ FolderEntry.cs           # Single folder/file entry DTO
â”‚   â”œâ”€â”€ Recommendation/              # ML recommendation system
â”‚   â”‚   â”œâ”€â”€ Engine/                  # Core recommendation logic
â”‚   â”‚   â”‚   â”œâ”€â”€ Engine.cs            # Orchestrator: profiles â†’ candidates â†’ scoring â†’ results
â”‚   â”‚   â”‚   â”œâ”€â”€ TrainingService.cs   # Implicit feedback training pipeline
â”‚   â”‚   â”‚   â”œâ”€â”€ Training/            # Training sub-components (refactored from TrainingService)
â”‚   â”‚   â”‚   â”‚   â”œâ”€â”€ TrainingDataBuilder.cs      # Builds labeled training examples from watch history
â”‚   â”‚   â”‚   â”‚   â”œâ”€â”€ TrainingFeatureComputer.cs  # Computes feature vectors for training candidates
â”‚   â”‚   â”‚   â”‚   â””â”€â”€ DiscoveryFeedbackExampleBuilder.cs # Phase 4: training from discovery interactions
â”‚   â”‚   â”‚   â”œâ”€â”€ PreferenceBuilder.cs # Genre/studio/tag/people preference extraction
â”‚   â”‚   â”‚   â”œâ”€â”€ DiversityReranker.cs # MMR-based diversity reranking
â”‚   â”‚   â”‚   â”œâ”€â”€ TemporalFeatures.cs  # Day-of-week/hour-of-day affinity computation
â”‚   â”‚   â”‚   â”œâ”€â”€ ReasonResolver.cs    # Human-readable recommendation explanations
â”‚   â”‚   â”‚   â”œâ”€â”€ SimilarityComputer.cs # Genre/people/tag similarity
â”‚   â”‚   â”‚   â”œâ”€â”€ CollaborativeFilter.cs # Jaccard + IDF co-occurrence
â”‚   â”‚   â”‚   â”œâ”€â”€ ContentScoring.cs    # Recency, rating, engagement scoring
â”‚   â”‚   â”‚   â””â”€â”€ EngineConstants.cs   # Shared constants (thresholds, windows)
â”‚   â”‚   â”œâ”€â”€ Scoring/                 # Pluggable scoring strategies
â”‚   â”‚   â”‚   â”œâ”€â”€ IScoringStrategy.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ ITrainableStrategy.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ HeuristicScoringStrategy.cs  # Fixed weights (rule-based)
â”‚   â”‚   â”‚   â”œâ”€â”€ LearnedScoringStrategy.cs    # Adaptive ML (SGD linear)
â”‚   â”‚   â”‚   â”œâ”€â”€ NeuralScoringStrategy.cs     # MLP with Adam optimizer
â”‚   â”‚   â”‚   â”œâ”€â”€ EnsembleScoringStrategy.cs   # Blends heuristic + learned + neural
â”‚   â”‚   â”‚   â”œâ”€â”€ StrategySelector.cs          # A/B testing: deterministic userâ†’strategy routing
â”‚   â”‚   â”‚   â”œâ”€â”€ NeuralFeatureImportance.cs   # Permutation-based feature importance for MLP
â”‚   â”‚   â”‚   â”œâ”€â”€ CandidateFeatures.cs         # 31-feature vector with FeatureIndex enum
â”‚   â”‚   â”‚   â”œâ”€â”€ DefaultWeights.cs            # Centralized default weights
â”‚   â”‚   â”‚   â”œâ”€â”€ ScoringHelper.cs             # Shared scoring utilities
â”‚   â”‚   â”‚   â”œâ”€â”€ ScoreExplanation.cs          # Per-feature score breakdown
â”‚   â”‚   â”‚   â”œâ”€â”€ TrainingExample.cs           # Training data container
â”‚   â”‚   â”‚   â””â”€â”€ RankingMetrics.cs            # P@K, R@K, NDCG@K evaluation
â”‚   â”‚   â”œâ”€â”€ WatchHistory/            # User watch profile building
â”‚   â”‚   â”‚   â”œâ”€â”€ IWatchHistoryService.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ WatchHistoryService.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ UserWatchProfile.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ LanguageAffinity.cs
â”‚   â”‚   â”‚   â””â”€â”€ WatchedItemInfo.cs
â”‚   â”‚   â”œâ”€â”€ Playlist/                # Recommendation â†’ Jellyfin playlist sync
â”‚   â”‚   â”‚   â”œâ”€â”€ IRecommendationPlaylistService.cs
â”‚   â”‚   â”‚   â”œâ”€â”€ RecommendationPlaylistService.cs
â”‚   â”‚   â”‚   â””â”€â”€ PlaylistSyncResult.cs
â”‚   â”‚   â”œâ”€â”€ IRecommendationEngine.cs
â”‚   â”‚   â”œâ”€â”€ IRecommendationCacheService.cs
â”‚   â”‚   â”œâ”€â”€ RecommendationCacheService.cs
â”‚   â”‚   â”œâ”€â”€ RecommendedItem.cs
â”‚   â”‚   â””â”€â”€ RecommendationResult.cs
â”‚   â”œâ”€â”€ Arr/                     # Radarr/Sonarr integration
â”‚   â”œâ”€â”€ Cleanup/                 # File cleanup services
â”‚   â”‚   â”œâ”€â”€ ITrashService.cs            # Trash bin interface (move, purge, relocate, access check)
â”‚   â”‚   â”œâ”€â”€ TrashService.cs             # Trash bin implementation
â”‚   â”‚   â”œâ”€â”€ TrashItemInfo.cs            # Trash item metadata DTO
â”‚   â”‚   â”œâ”€â”€ TrashPathAccessResult.cs    # Permission check result (read/write/exists)
â”‚   â”‚   â”œâ”€â”€ ICleanupConfigHelper.cs     # Cleanup configuration interface
â”‚   â”‚   â”œâ”€â”€ CleanupConfigHelper.cs      # Library filtering, trash path resolution
â”‚   â”‚   â”œâ”€â”€ ICleanupTrackingService.cs  # Cleanup statistics tracking interface
â”‚   â”‚   â””â”€â”€ CleanupTrackingService.cs   # Persists bytes-freed/items-deleted counters
â”‚   â”œâ”€â”€ ConfigAccess/            # Plugin configuration access
â”‚   â”œâ”€â”€ Link/                    # .strm/symlink repair
â”‚   â”œâ”€â”€ PluginLog/               # Structured plugin logging
â”‚   â”œâ”€â”€ FileTransformation/      # File Transformation plugin integration
â”‚   â”‚   â”œâ”€â”€ DiscoveryScriptTag.cs     # Shared script tag builder + removal regex (single source of truth)
â”‚   â”‚   â”œâ”€â”€ PatchRequestPayload.cs    # Payload model for transformation callbacks
â”‚   â”‚   â””â”€â”€ TransformationPatches.cs  # index.html script injection (on-the-fly via File Transformation plugin)
â”‚   â”œâ”€â”€ Seerr/                   # Jellyseerr/Overseerr integration
â”‚   â”‚   â”œâ”€â”€ ISeerrIntegrationService.cs   # Seerr cleanup (request removal)
â”‚   â”‚   â”œâ”€â”€ SeerrIntegrationService.cs
â”‚   â”‚   â””â”€â”€ Discovery/               # Seerr Discovery (external recommendations)
â”‚   â”‚       â”œâ”€â”€ ISeerrDiscoveryService.cs
â”‚   â”‚       â”œâ”€â”€ SeerrDiscoveryService.cs  # Orchestrator: profiles â†’ TMDb query â†’ scoring â†’ results
â”‚   â”‚       â”œâ”€â”€ DiscoveryCacheService.cs  # Disk + memory persistence
â”‚   â”‚       â”œâ”€â”€ ExternalCandidateFeatureBuilder.cs  # Builds 31-feature vector for TMDb items
â”‚   â”‚       â”œâ”€â”€ NullableDateTimeConverter.cs  # Graceful DateTime? JSON deserialization (handles empty strings from TMDb)
â”‚   â”‚       â”œâ”€â”€ ParentalRatingHelper.cs   # Child-safe content filtering
â”‚   â”‚       â”œâ”€â”€ TmdbGenreMap.cs           # Jellyfin â†” TMDb genre ID mapping
â”‚   â”‚       â”œâ”€â”€ TmdbDiscoverItem.cs       # TMDb candidate DTO
â”‚   â”‚       â”œâ”€â”€ TmdbDiscoverResponse.cs   # TMDb API page response
â”‚   â”‚       â”œâ”€â”€ DiscoveryResult.cs        # Per-user result container
â”‚   â”‚       â”œâ”€â”€ DiscoveryRecommendation.cs # Single recommendation DTO
â”‚   â”‚       â”œâ”€â”€ SeerrUser.cs             # Seerr user model (with JellyfinUserId mapping + Permissions)
â”‚   â”‚       â”œâ”€â”€ SeerrUserPage.cs         # Paginated user list response
â”‚   â”‚       â”œâ”€â”€ SeerrPermissions.cs      # [Flags] enum of all Overseerr/Jellyseerr permission bits
â”‚   â”‚       â”œâ”€â”€ SeerrPermissionExtensions.cs # Permission evaluation (HasPermission, CanRequest, CanSelectQualityProfile)
â”‚   â”‚       â”œâ”€â”€ UserRequestPermissionResult.cs # Permission check result (CanRequest + allowed profiles)
â”‚   â”‚       â”œâ”€â”€ AllowedQualityProfile.cs # Single quality profile the user may select
â”‚   â”‚       â”œâ”€â”€ SeerrServiceInfo.cs      # Radarr/Sonarr service config from Seerr
â”‚   â”‚       â”œâ”€â”€ SeerrQualityProfile.cs   # Quality profile DTO
â”‚   â”‚       â”œâ”€â”€ SeerrRootFolder.cs       # Root folder DTO
â”‚   â”‚       â”œâ”€â”€ SeerrCredits.cs          # TMDb credits response (cast + crew)
â”‚   â”‚       â”œâ”€â”€ SeerrCastMember.cs       # Cast member DTO
â”‚   â”‚       â”œâ”€â”€ SeerrCrewMember.cs       # Crew member DTO
â”‚   â”‚       â”œâ”€â”€ SeerrMediaDetailResponse.cs # Detailed media info from Seerr
â”‚   â”‚       â”œâ”€â”€ IDiscoveryFeedbackStore.cs  # Training feedback persistence interface
â”‚   â”‚       â”œâ”€â”€ DiscoveryFeedbackStore.cs   # File-based feedback store (shown/dismissed/requested/watched)
â”‚   â”‚       â”œâ”€â”€ DiscoveryFeedbackEntry.cs   # Per-item interaction tracking model
â”‚   â”‚       â”œâ”€â”€ DiscoveryFeedbackResult.cs  # Per-user feedback container
â”‚   â”‚       â””â”€â”€ DiscoveryInteractionStatus.cs # Enum: Shown/Dismissed/Requested/RequestedAndWatched
â”‚   â”œâ”€â”€ Statistics/              # Media statistics
â”‚   â””â”€â”€ Timeline/                # Library growth tracking
â”œâ”€â”€ ScheduledTasks/
â”‚   â”œâ”€â”€ HelperCleanupTask.cs         # Main orchestrator task
â”‚   â”œâ”€â”€ CleanTrickplayTask.cs
â”‚   â”œâ”€â”€ CleanEmptyMediaFoldersTask.cs
â”‚   â”œâ”€â”€ CleanOrphanedSubtitlesTask.cs
â”‚   â”œâ”€â”€ RepairLinksTask.cs            # Repairs broken .strm/symlink references
â”‚   â”œâ”€â”€ RecommendationsTask.cs        # ML recommendation generation sub-task
â”‚   â””â”€â”€ UserActivityUpdateTask.cs     # User activity aggregation sub-task
â””â”€â”€ PluginPages/
    â”œâ”€â”€ configPage.template.html # HTML shell (build-time composition)
    â”œâ”€â”€ configPage.html          # Generated output (do not edit)
    â”œâ”€â”€ css/                     # Per-tab CSS modules
    â”‚   â”œâ”€â”€ Shared.css, Overview.css, Codecs.css, Health.css
    â”‚   â”œâ”€â”€ Trends.css, Settings.css, ArrIntegration.css, Logs.css
    â”‚   â””â”€â”€ Recommendations.css  # Discover tab styles
    â””â”€â”€ js/                      # Per-tab JS modules + .eslintrc.json
        â”œâ”€â”€ Shared.js, Overview.js, Codecs.js, Health.js
        â”œâ”€â”€ Trends.js, Settings.js, ArrIntegration.js, Logs.js
        â”œâ”€â”€ Recommendations.js    # Discover tab logic
        â”œâ”€â”€ FolderBrowser.js      # Folder browser UI (path picker for settings)
        â””â”€â”€ Main.js               # Tab routing, IIFE close
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
User Watch History â†’ Feature Extraction (31 features) â†’ Scoring Strategy â†’ Ranked Results
                                                              â†‘
                                                    â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”´â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
                                                    â”‚  EnsembleScoringStrategy  â”‚
                                                    â”‚                          â”‚
                                                    â”‚  Î± Ã— Learned (SGD)       â”‚
                                                    â”‚  + (1-Î±) Ã— Heuristic     â”‚
                                                    â”‚  + Î² Ã— Neural (MLP)      â”‚
                                                    â”‚  Ã— genre penalty          â”‚
                                                    â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
```

- **HeuristicScoringStrategy**: Fixed hand-tuned weights, always available
- **LearnedScoringStrategy**: Linear model trained via SGD on implicit feedback
- **NeuralScoringStrategy**: 4-hidden-layer MLP (31â†’48â†’24â†’12â†’6â†’1) with Adam optimizer
- **EnsembleScoringStrategy**: Blends all three with dynamic Î±/Î² weighting

Training uses implicit feedback: previously recommended items are compared against current watch data to generate labeled training examples. The EnsembleScoringStrategy records a rolling history of training quality metrics (validation loss, P@K, R@K, NDCG@K) that are persisted across server restarts for future trend analysis.

### Seerr Discovery Architecture

Seerr Discovery extends the recommendation system to suggest external (not-yet-in-library) content by querying the configured Overseerr/Jellyseerr instance:

```text
UserWatchProfiles â†’ Genre/People/Language preferences
                         â†“
         TMDb Discovery via Seerr API (genre + language endpoints)
                         â†“
         Deduplication + Parental Rating Filter + Arr Exclusion
                         â†“
         Phase 1: Pre-score all candidates (genre/rating/recency only)
                         â†“
         Phase 2: Enrich top-20 with credits (actors/directors via Seerr)
                         â†“
         Phase 3: Final score with EnsembleScoringStrategy (full 31 features)
                         â†“
         Top-10 per user â†’ DiscoveryCacheService â†’ Frontend
```

- Coupled to **Seerr configuration** (URL + API Key) - independent of Seerr Cleanup task mode
- Runs as part of `HelperCleanupTask` when `RecommendationsTaskMode != Deactivate`
- Uses `ExternalCandidateFeatureBuilder` to construct the same 31-feature vector used for internal recommendations
- Results persisted to `jellyfin-helper-discovery-results.json` with in-memory cache
- Request submission via `POST /JellyfinHelper/Discovery/Request` with optional Seerr user/server/profile mapping

### Discovery Custom Tab & Script Injection

Discovery results are also displayed on the Jellyfin home screen via a separate script (`js/discovery-sidebar.js`) that is injected into Jellyfin's `index.html`:

```text
Plugin starts â†’ Plugin.InjectScript()
                    â†“
    â”Œâ”€â”€â”€ File Transformation plugin available? â”€â”€â”€â”
    â”‚ YES                                         â”‚ NO
    â”‚ Register callback via reflection            â”‚ Direct index.html write
    â”‚ (no filesystem write needed)                â”‚ (requires writable filesystem)
    â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
                    â†“
    index.html serves <script src="/JellyfinHelper/Discovery/My/script">
                    â†“
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

**Task Mode Coupling:** Discovery generation shares the `RecommendationsTaskMode` setting â€” there is no separate toggle. When `RecommendationsTaskMode` is set to `Deactivate`, no Discovery recommendations are generated. This is intentional: Discovery depends on the same watch profile data that the Recommendations engine produces.

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
    â”œâ”€â”€ css/*.css           â†’ injected into <style> block
    â””â”€â”€ js/*.js             â†’ injected into <script> block
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    â†’ configPage.html       (generated, do not edit directly)
```

The `ComposeConfigPage` MSBuild task (`BuildTasks/ComposeConfigPage.cs`) runs during build:

1. Reads `configPage.template.html`
2. Finds `/* __CSS_MODULES__ */` placeholder â†’ injects all CSS files (ordered)
3. Finds `/* __JS_MODULES__ */` placeholder â†’ injects all JS files (ordered)
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