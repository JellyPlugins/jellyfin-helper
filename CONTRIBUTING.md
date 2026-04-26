<!--
  CONTRIBUTING.md — Contributor guidelines for the Jellyfin Helper plugin.
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

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Jellyfin Server 10.11.x](https://jellyfin.org/docs/general/administration/installing) (for runtime testing)
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
cp Jellyfin.Plugin.JellyfinHelper/bin/Debug/net9.0/Jellyfin.Plugin.JellyfinHelper.dll \
   ~/.local/share/jellyfin/plugins/JellyfinHelper/

# Linux (system service / package install — path may vary by distro)
# sudo cp Jellyfin.Plugin.JellyfinHelper/bin/Debug/net9.0/Jellyfin.Plugin.JellyfinHelper.dll \
#    /var/lib/jellyfin/plugins/JellyfinHelper/

# Windows
copy Jellyfin.Plugin.JellyfinHelper\bin\Debug\net9.0\Jellyfin.Plugin.JellyfinHelper.dll ^
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
├── Api/                           # Controller tests
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
│   ├── RecommendationsTaskTests.cs
│   ├── UserActivityUpdateTaskTests.cs
│   └── ...
├── Services/
│   ├── Activity/                  # User activity service tests
│   ├── Backup/                    # Backup/restore tests
│   └── Recommendation/            # Recommendation engine tests
│       ├── Engine/                # Core engine logic tests
│       │   ├── CollaborativeFilterTests.cs
│       │   └── PreferenceBuilderTests.cs
│       ├── Playlist/              # Playlist sync tests
│       │   └── RecommendationPlaylistServiceTests.cs
│       ├── Scoring/               # Strategy-specific tests
│       │   ├── ScoringStrategyTests.cs
│       │   ├── NeuralScoringStrategyTests.cs
│       │   ├── ScoreExplanationTests.cs
│       │   ├── TrainingExampleTests.cs
│       │   └── RankingMetricsTests.cs
│       ├── WatchHistory/          # Watch history service tests
│       │   └── WatchHistoryServiceTests.cs
│       ├── RecommendationCacheServiceTests.cs
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
├── i18n/                        # Internationalization files (en, de, fr, es, pt, zh, tr)
├── Plugin.cs                    # Entry point, web page registration
├── PluginServiceRegistrator.cs  # DI registration for all services
├── MediaExtensions.cs           # Extension methods for media analysis
├── Api/
│   ├── ArrIntegrationController.cs      # Radarr/Sonarr integration API
│   ├── BackupController.cs              # Backup/restore API
│   ├── CleanupStatisticsController.cs   # Cleanup statistics API
│   ├── ConfigurationController.cs       # Plugin configuration API
│   ├── GrowthTimelineController.cs      # Library growth timeline API
│   ├── LibraryInsightsController.cs     # Library insights API
│   ├── LogsController.cs               # Plugin logs API
│   ├── MediaStatisticsController.cs     # Media statistics API
│   ├── RecommendationController.cs      # ML recommendations API
│   ├── SeerrController.cs              # Jellyseerr/Overseerr integration API
│   ├── TranslationsController.cs        # i18n translations API
│   ├── TrashController.cs               # Trash bin API
│   └── UserActivityController.cs        # User activity insights API
├── Configuration/
│   ├── PluginConfiguration.cs   # All config properties with defaults
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
│   │   ├── BackupService.cs     # Create/restore backup
│   │   ├── BackupValidator.cs   # Comprehensive input validation
│   │   └── BackupSanitizer.cs   # Clamp/normalize values
│   ├── Recommendation/              # ML recommendation system
│   │   ├── Engine/                  # Core recommendation logic
│   │   │   ├── Engine.cs            # Orchestrator: profiles → candidates → scoring → results
│   │   │   ├── TrainingService.cs   # Implicit feedback training pipeline
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
│   │   │   ├── CandidateFeatures.cs         # 27-feature vector with FeatureIndex enum
│   │   │   ├── DefaultWeights.cs            # Centralized default weights
│   │   │   ├── ScoringHelper.cs             # Shared scoring utilities
│   │   │   ├── ScoreExplanation.cs          # Per-feature score breakdown
│   │   │   ├── TrainingExample.cs           # Training data container
│   │   │   └── RankingMetrics.cs            # P@K, R@K, NDCG@K evaluation
│   │   ├── WatchHistory/            # User watch profile building
│   │   │   ├── IWatchHistoryService.cs
│   │   │   ├── WatchHistoryService.cs
│   │   │   ├── UserWatchProfile.cs
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
│   ├── Cleanup/                 # File cleanup services
│   ├── Statistics/              # Media statistics
│   ├── Timeline/                # Library growth tracking
│   └── PluginLog/               # Structured plugin logging
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
// ... etc
```

### TaskMode Pattern

All cleanup tasks follow the three-mode pattern:

```csharp
public enum TaskMode
{
    Deactivate,  // Skip entirely — no work done
    DryRun,      // Analyze and report — no changes made
    Activate     // Full execution — changes applied
}
```

Each task receives its mode from `PluginConfiguration` and logs differently based on mode.

### Recommendation System Architecture

The ML recommendation system uses a layered scoring approach:

```text
User Watch History → Feature Extraction (27 features) → Scoring Strategy → Ranked Results
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
- **NeuralScoringStrategy**: 3-hidden-layer MLP with Adam optimizer
- **EnsembleScoringStrategy**: Blends all three with dynamic α/β weighting

Training uses implicit feedback: previously recommended items are compared against current watch data to generate labeled training examples. The EnsembleScoringStrategy records a rolling history of training quality metrics (validation loss, P@K, R@K, NDCG@K) that are persisted across server restarts for future trend analysis.

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

The `HtmlComposer` MSBuild task (`BuildTasks/HtmlComposer.cs`) runs during build:

1. Reads `configPage.template.html`
2. Finds `/* __CSS_MODULES__ */` placeholder → injects all CSS files (ordered)
3. Finds `/* __JS_MODULES__ */` placeholder → injects all JS files (ordered)
4. Writes the composed `configPage.html`

### File Ordering

CSS and JS files are injected in a specific order defined in `HtmlComposer.cs`:

```csharp
// CSS order
"Shared.css", "Overview.css", "Codecs.css", "Health.css",
"Trends.css", "Settings.css", "ArrIntegration.css", "Logs.css",
"Recommendations.css"

// JS order  
"Shared.js", "Overview.js", "Codecs.js", "Health.js",
"Trends.js", "Settings.js", "ArrIntegration.js", "Logs.js",
"Recommendations.js", "Main.js"
```

`Shared.css`/`Shared.js` must be first (shared utilities), `Main.js` must be last (tab routing + IIFE close).

### Adding a New Tab

1. Create `css/YourTab.css` and `js/YourTab.js`
2. Add the filenames to the ordering arrays in `HtmlComposer.cs`
3. Add the tab button and content div to `configPage.template.html`
4. Register the init function in `Main.js`'s tab routing
5. Build to regenerate `configPage.html`

### Important Rules

- **Never edit `configPage.html` directly** — it's overwritten on every build
- **Always edit the source files** in `css/`, `js/`, or `configPage.template.html`
- The `docs/` folder contains a **copy** of the plugin pages for the documentation site
- After changing plugin pages, copy updated files to `docs/` as well

### JavaScript Guidelines

- All JS runs inside an IIFE (Immediately Invoked Function Expression) — no global pollution
- Prefer `var` for broader compatibility; `const`/`let` and arrow functions are acceptable
  in utility/helper code (e.g., `Shared.js`) where Jellyfin web client supports ES6+
- Use `T('key', 'fallback')` for all user-visible strings (i18n support)
- Use `apiGet()` / `apiPost()` helpers for API calls (handles auth headers)
- Use `escHtml()` for any user-provided content inserted into HTML

### CSS Guidelines

- Prefix all classes with the tab name (e.g., `recs-*` for Recommendations)
- Support both dark and light modes via `@media (prefers-color-scheme: light)`
- Use relative units (`em`, `%`) for responsive layouts
- Keep specificity low — avoid `!important`

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