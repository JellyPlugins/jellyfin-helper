# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses 4-part versioning (`x.x.x.x`) consistent with the Jellyfin plugin ecosystem.


## [Unreleased] — 2.0.0.0

### Added
- **Discover Tab** — New 8th dashboard tab "Discover" combining ML-powered smart recommendations and user activity insights in a single view. Includes `Recommendations.js`, `Recommendations.css` frontend modules with user selector, recommendation cards, activity summaries, and genre distribution charts.
- **Smart Recommendation Engine** — ML-based per-user recommendation system (`Services/Recommendation/`) with four-tier scoring architecture:
  - `HeuristicScoringStrategy` — rule-based weighted scoring with centralized genre-mismatch penalty.
  - `LearnedScoringStrategy` — gradient-descent ML with Z-score standardization, `ArrayPool`-based scoring, and per-feature weight importance logging (Debug level). Weights persisted to disk (JSON, schema v11).
  - `NeuralScoringStrategy` — three-hidden-layer MLP (26→32→16→8→1, 1537 params) with Adam optimizer, Xavier/Glorot initialization per layer, three-layer backpropagation, sigmoid output, early stopping with validation split, temporal sample weighting, weight clamping, `ReaderWriterLockSlim` thread-safety, `[ThreadStatic]` scratch buffers, input-gradient attribution for `ScoreWithExplanation()`, and per-feature importance logging (L2 norm, Debug level). Weights persisted to disk (JSON, schema v6).
  - `EnsembleScoringStrategy` — adaptive 3-way blend (Heuristic + Learned + Neural) with sigmoid-based alpha transition, beta ramp for neural activation, centralized soft genre-mismatch penalty, and state persistence.
- **Playlist Sync** — Optional feature to sync recommendations to Jellyfin playlists (`IRecommendationPlaylistService`) with intelligent naming, creation, updating, and cleanup of recommendation playlists. Sync can be triggered automatically after generation or manually via API.
- **User Watch Profiles** — `WatchHistoryService` analyzes user watch history to build detailed watch profiles (`UserWatchProfile`) with genre/studio/people affinity scores, completion ratio distributions, time-based activity patterns, and favorites detection. These profiles feed into the recommendation engine for personalized scoring and are displayed as insights in the Discover tab.
- **Series-level favorites** — `UserWatchProfile.FavoriteSeriesIds` captures series the user has favorited at the series level (not just individual episodes). `WatchHistoryService` detects these via `IUserDataManager` on `Series` items. The recommendation engine treats series-level favorites as positive signals for genre/studio/people preferences and excludes favorited series from candidate scoring (the user already knows them).
- **Plugin data cleanup on uninstall** — `Plugin.OnUninstalling()` now calls `CleanupDataFiles()` which deletes all `jellyfin-helper-*.json` and `.tmp` files from the Jellyfin data directory, and `CleanupRecommendationPlaylists()` which removes all recommendation playlist folders (identified by the `PlaylistNamePrefix`) from `{DataPath}/playlists/`. ML weight files (`ml_weights.json`, `neural_weights.json`, `ensemble_state.json`) are cleaned up automatically by Jellyfin when it removes the plugin's `DataFolderPath`.
- **CancellationToken in TrainStrategy** — `IRecommendationEngine.TrainStrategy()` now accepts a `CancellationToken` for cooperative cancellation during long training runs.
- **Parental rating enforcement** — Recommendations now respect Jellyfin's per-user `MaxParentalRating` setting. Candidates exceeding the user's rating limit are excluded before scoring in both `GenerateForUser()` and `GenerateColdStartRecommendations()`, ensuring children with restricted profiles only receive age-appropriate recommendations. Works dynamically with all rating systems (FSK, MPAA, BBFC, etc.) via Jellyfin's numeric `InheritedParentalRatingValue`. Users still receive the full `maxResults` count from the eligible candidate pool. `UserWatchProfile.MaxParentalRating` populated from `WatchHistoryService`.
- **Recommendations Configuration** — New plugin settings: `RecommendationsTaskMode` (DryRun/Activate/Deactivate, default: DryRun), `MaxRecommendationsPerUser` (default: 20, range: 1–100), `EnsembleAlphaMin` (default: 0.3), `EnsembleAlphaMax` (default: 0.75), `EnsembleGenrePenaltyFloor` (default: 0.10), `SyncRecommendationsToPlaylist` (default: false, opt-in).
- **Recommendations Scheduled Task** — Integrated into `HelperCleanupTask` to generate recommendations and activity data on the weekly cleanup schedule.
- **Recommendations Service Registration** — `PluginServiceRegistrator` registers 6 new services (`IWatchHistoryService`, `IRecommendationEngine`, `IRecommendationCacheService`, `IUserActivityInsightsService`, `IUserActivityCacheService`, `IRecommendationPlaylistService`) and 4 scoring strategies (Heuristic, Learned, Neural, Ensemble) with Ensemble always active as the default strategy.
- **i18n — Discover Tab** — All 7 language files (en, de, fr, es, pt, zh, tr) updated with Discover tab translations (tab label, recommendation cards, activity summaries, empty states).
- **Genre Exposure Features** — Three new scoring features (`GenreUnderexposure`, `GenreDominanceRatio`, `GenreAffinityGap`) that detect genre distribution patterns in user watch history. Items containing genres the user rarely watches receive soft penalties, while items matching the user's core genres receive a boost. Activates only for users with ≥30 watched items to avoid false conclusions from sparse data. Applied in scoring, training (Phase 1 + Phase 2), heuristic, learned, and neural strategies.
- **Tests** — New test classes: `RecommendationControllerTests`, `UserActivityControllerTests`, `RecommendationEngineTests`, `WatchHistoryServiceTests`, `ScoringStrategyTests`, `NeuralScoringStrategyTests`, `ScoreExplanationTests`, `TrainingExampleTests`, `RankingMetricsTests`, `RecommendationCacheServiceTests`, `RecommendationDtoTests`, `UserActivityCacheServiceTests`, `UserActivityInsightsServiceTests`. `RecommendationPlaylistServiceTests` (8 tests), `RecommendationsTaskTests` (3 playlist-sync tests). Includes concurrency tests (parallel `Score()` + `Train()`), three-hidden-layer architecture tests, k-fold constant verification, ranking evaluation metric tests (Precision@K, Recall@K, NDCG@K), and genre exposure feature tests (underexposure, dominance, affinity gap, edge cases). Total: **1932 tests**.

### Changed
- **8-Tab Dashboard** — Dashboard expanded from 7 to 8 tabs: Overview, Codecs, Health, Trends, **Discover**, Settings, Arr, Logs (Discover Tab is only visible in Dry Run or Activate mode).
- **HelperCleanupTask** — Extended to run recommendation generation and user activity aggregation alongside existing cleanup tasks.
- **Series exclusion in recommendations** — All known series (favorited, partially watched, fully watched) are now excluded from recommendation candidates. Jellyfin natively shows "Next Up" for in-progress series, so recommending them again wastes a slot. Their genre/studio/people signals still flow into user preferences.
- **Score calculation consistency** — Optimized scoring strategies for consistent score computation across heuristic, learned, and neural paths.
- **Documentation** — Updated README.md, CONTRIBUTING.md, manifest.json, build.yaml, and CHANGELOG.md for the new Discover tab and all associated features.

### Fixed
- **Trends Tab** — "Largest" and "Recent" sections in the Trends tab were displaying the total size of the library in the tree view instead of the sum of the displayed objects.
- **Trends Tab** — The "Largest" section in the Trends tab was showing first shows instead of movies - now correctly shows movies first.
- **Plugin Log** — More precise logs if trash is activated.
- **Plugin Uninstall** — Uninstalling the plugin did not remove the plugin's data files, which could lead to stale data if the plugin was reinstalled later. Now all plugin-related data files and recommendation playlists are cleaned up on uninstallation.
---

## [1.2.1.0] — 2026-04-20

### Added
- **Library Insights** — New "Insights" section in the Trends tab showing the largest media directories and recently added/changed items (last 30 days). Includes summary cards with expandable tree views grouped by library, type badges, and change indicators. New backend service (`ILibraryInsightsService`, `LibraryInsightsService`) with filesystem scanning, new API endpoint (`GET /JellyfinHelper/LibraryInsights`) with 15-minute in-memory caching, and new data models (`LibraryInsightEntry`, `LibraryInsightsResult`).
- **Dynamic Range Mock Data** — Added dynamic range mock data (`DynamicRanges`, `DynamicRangeSizes`, `DynamicRangePaths`) to the live demo for Movies and TV Shows libraries.
- **Library Insights Mock Data** — Added library insights mock data and API route to the live demo.

### Changed
- **Statistics Refactored to MediaStream** — Video codecs, resolutions, and dynamic range are now extracted from Jellyfin `MediaStream` metadata, while audio codec detection is `MediaStream`-first with filename/extension fallback where metadata is missing. Supports differentiated audio codecs (TrueHD Atmos, DTS-X, DTS-HD MA, EAC3 Atmos, etc.).
- **Dynamic Range Detection** — New per-library dynamic range statistics (`HDR10`, `HDR10+`, `Dolby Vision`, `HLG`, `SDR`) with `VideoRangeType` → `VideoRange` fallback chain.
- **Resolution Classification** — Extended to 8K, 4K, 1440p, 1080p, 720p, 576p, 480p, SD with width+height-based classification.
- **Donut Chart Enhancements** — Added dynamic range donut chart, improved codec icon mapping, animation support for all donut charts.
- **Documentation** — Updated CONTRIBUTING.md and README.md to reflect MediaStream-based extraction, dynamic range feature, and library insights.

### Fixed
- **Performance** — Video streams cached per-item to avoid redundant `GetMediaStreams()` calls during statistics scan.

---

## [1.2.0.0] — 2026-04-16

### Added
- **Seerr Cleanup Task** — New scheduled task (`SeerrCleanupTask`) to automatically clean up unavailable media requests from Overseerr/Jellyseerr. New `Services/Seerr/` domain with `ISeerrIntegrationService`, `SeerrIntegrationService`, and Seerr DTOs. Added `Api/SeerrController.cs` for connection testing (`/JellyfinHelper/Seerr/Test`).
- **Unsaved Settings Alert** — The settings page now warns users before navigating away with unsaved changes (dirty-tracking via JSON snapshot comparison). Offers "Discard", "Save & Continue", or "Cancel" options.
- **Collapsible Arr Sections** — Radarr, Sonarr, and Seerr configuration sections are now collapsible with chevron animation, dynamic instance count display (`✔` / `(n)`), and full localization support.
- **Auto-Save Dropdowns** — Task mode selects (Trickplay, Empty Folders, Subtitles, Link Repair, Seerr) and the Language dropdown now auto-save on change with inline `✔`/`✘` indicator, eliminating the need to click "Save Settings" for quick changes.
- **Auto-Init Scan** — The Overview page now automatically triggers an initial media scan when no cached statistics are available, eliminating the need to manually click "Scan Libraries" on first visit. The scan button has been redesigned as a compact icon button with a spinning animation during scans.
- **Scroll Position Restore** — Language change and backup import now preserve the scroll position after UI rebuild, preventing the page from jumping to the top.

### Fixed
- **Plugin Logo** — Fixed `imagePath` in `meta.json` to use absolute `/config/plugins/` path matching Jellyfin's expected format.
- **meta.json Structure** — Replaced invalid `assembly` field with `assemblies: []`, added missing `changelog`, `timestamp`, and `imageUrl` fields.
- **meta.json Generation** — Switched from heredoc to `jq` for safe JSON generation (prevents broken JSON from special characters in changelog).

### Changed
- **4-Part Versioning** — All versions now use 4-part format (`x.x.x.x`) consistent with other Jellyfin plugins (e.g. Jellyfin Enhanced, Intro Skipper).
- **Link Repair** — Renamed "STRM Repair" task to "Link Repair". The task now scans for both broken `.strm` files and broken symlinks, repairing them by locating renamed/moved target files. Refactored `Services/Strm/` to `Services/Link/` with Strategy pattern (`ILinkHandler` → `StrmLinkHandler`, `SymlinkHandler`).
- **Configuration** — `StrmRepairTaskMode` renamed to `LinkRepairTaskMode`.
- **Scheduled Task** — `RepairStrmFilesTask` renamed to `RepairLinksTask`.
- **Save Workflow** — `doSaveSettings()` now supports a `quiet` mode with `{ quiet: true, element: el }` options for auto-save (no button animation, inline indicator instead). Language change no longer triggers a full-page reload (`PluginPages/js/Main.js`).
- **Log Level Auto-Save** — Log level dropdown in the Logs tab now uses the shared `showAutoSaveIndicator()` function from `Shared.js` for consistent UX across all auto-save controls.
- **Documentation** — Updated CONTRIBUTING.md, README.md, manifest.json, and build.yaml to reflect Link Repair naming, symlink support, Seerr integration, and UI improvements.

---

## [1.1.0.0] — 2026-04-16

### Added
- **Trends UI Enhancements** — Improved CSS and JS for the Trends tab with better chart rendering and responsiveness.

### Changed
- **GrowthTimelineService Performance** — Optimized performance of timeline aggregation and bucketing logic.
- **BackupService Performance** — Optimized backup service methods for better efficiency.
- **Service Refactoring** — Refactored monolith `BackupService.cs` and `GrowthTimelineService.cs` into smaller, focused components (`TimelineAggregator`, `BackupSanitizer`).
- **Cross-Platform Compatibility** — Improved case handling in tests for cross-platform compatibility.
- **CI/CD** — Updated PR workflow, bumped `softprops/action-gh-release` from v2 to v3.
- **CONTRIBUTING.md** — Updated with new test count and fixture architecture details.

### Removed
- **Legacy History Cleanup** — Removed legacy history file cleanup method (preparation for this version).

---

## [1.0.9.0] — 2026-04-14

### Removed
- **Statistics History** — Removed legacy scan-based snapshot system (`StatisticsHistoryService`, `StatisticsSnapshot`), replaced entirely by the growth timeline. The `/Statistics/History` API endpoint has been removed.
- **Export Endpoints** — Removed `/Statistics/Export/Json` and `/Statistics/Export/Csv` API endpoints (data export is handled via Backup/Restore).
- **History in Backup** — Removed `StatisticsHistory` from backup data and `HistorySnapshotsRestored` from restore summary.

### Added
- **`StatisticsCacheService`** — New focused service replacing `StatisticsHistoryService`, responsible solely for caching the latest scan result to disk.
- **Legacy History File Cleanup** — `HelperCleanupTask` now automatically deletes the legacy `jellyfin-helper-statistics-history.json` file from previous versions.
- **Growth Timeline Interpolation** — Frontend now interpolates missing intermediate buckets between sparse data points for a continuous chart line with granularity-aware bucket advancement.
- **Growth Timeline Deduplication** — Backend deduplicates consecutive identical timeline data points for compact storage.
- **Backup & Restore in Demo** — Live demo (docs/) now includes the full Backup & Restore UI in the Settings tab.

### Changed
- **Backup Size Limit** — Client-side backup import size check reduced from 50 MB to 10 MB.
- **Growth Timeline Service** — Significantly expanded with deduplication logic, improved bucketing, and granularity validation.
- **i18n** — Updated all 7 language files with new/revised translation keys.
- **Test Count** — Updated to **957 tests** (removed export/history tests, added growth timeline deduplication and interpolation tests).

---

## [1.0.8.0] — 2026-04-12

### Added
- **Backup & Restore** — New backup/restore functionality to export and import plugin configuration and historical data as JSON.
- **Growth Timeline** — New growth timeline visualization displaying cumulative media growth over time with granular bucketing (daily/weekly/monthly/quarterly/yearly).

### Changed
- **Project Restructure** — Reorganized project structure for better maintainability and modularity.
- **TrendChart Enhancements** — Improved scaling, labels, and mobile responsiveness for the trend chart visualization.
- **Download Buttons Relocated** — JSON/CSV download buttons moved for better UX placement.
- **Responsive Data Tree** — Data tree component made responsive for touch devices.

### Fixed
- **Jellyfin Compatibility** — Fixed bug preventing plugin usage on Jellyfin versions below 10.11.8 by downgrading Jellyfin.Controller and Jellyfin.Model package versions to 10.11.0.

---

## [1.0.7.0] — 2026-04-12

### Added
- **Plugin Log Viewer** — New **Logs** tab in the dashboard providing real-time access to plugin-specific log entries with level filtering (DEBUG/INFO/WARN/ERROR), source component filtering, auto-refresh (10s), download as `.log` file, and clear with confirmation dialog.
- **Log API Endpoints** — `GET /Logs` (with `?limit`, `?minLevel`, `?source` query params), `GET /Logs/Download`, `DELETE /Logs`.
- **Log Level Persistence** — Selected log level is persisted to the plugin configuration (`PluginLogLevel`) and restored on page load.
- **Enhanced Backend Logging** — `MediaStatisticsService` now logs scan start/end summaries, per-library file counts, and detailed breakdowns at DEBUG level.
- **Dedicated Per-Tab CSS Modules** — Each tab now has its own CSS file: `Overview.css`, `Codecs.css`, `Health.css`, `Trends.css`, `Settings.css`, `ArrIntegration.css`, `Logs.css`.

### Changed
- **7-Tab Dashboard** — Dashboard expanded from 6 to 7 tabs with the addition of the Logs tab.
- **Log Level Moved to Logs Tab** — The log level dropdown was removed from the Settings tab and is now exclusively in the Logs tab for direct context.
- **README** — Updated to reflect 7-tab dashboard, new Logs feature section, 3 new API endpoints, Plugin Log Level configuration option, complete folder structure with all CSS/JS modules, and updated test count.
- **Test Count** — Increased from 669 to **737 tests** with new `LogsHtmlTests` (68 tests) covering all Logs tab UI elements, API calls, i18n keys, auto-refresh, download mechanism, and log level persistence.

---

## [1.0.6.0] — 2026-04-11

### Fixed
- **Trash exclusion in statistics** — Trash folders are now explicitly excluded from media statistics calculations to avoid distorted results.
- **TV show metadata false positives** — Fixed a bug where empty metadata directories of TV shows were incorrectly marked as orphaned.
- **Trash dialog in UI** — Fix for the confirmation dialog when disabling the trash, which was not showing under certain conditions.

---

## [1.0.5.0] — 2026-04-11

### Added
- **Multi-Instance Arr Support** — Up to 3 Radarr and 3 Sonarr instances simultaneously (e.g. "Radarr 4K", "Radarr Anime") with per-instance comparison and merged views. Automatic migration from legacy single-instance configuration.
- **Arr Connection Test** — New `/Arr/TestConnection` endpoint with a test button in the Settings UI to validate URL + API key before saving.
- **Persisted Latest Scan Result** — Statistics are now persisted to disk via `StatisticsHistoryService.SaveLatestResult()` and loaded on dashboard open via `/Statistics/Latest` without requiring a new scan. Results survive server restarts.
- **Post-Cleanup Statistics Scan** — After each `HelperCleanupTask` run, a statistics scan is automatically executed and persisted.
- **Embedded Subtitle Detection** — Health check "Videos without subtitles" now considers embedded subtitle streams (via Jellyfin's `MediaStream` data), not just external `.srt` files.
- **Video vs Music Audio Codecs** — Audio codec analysis is now split into two categories: codecs parsed from video filenames (`VideoAudioCodecs`) and codecs from music files (`MusicAudioCodecs`) with extension-based fallback.
- **Codec File Path Tracking** — Each codec, container format, and resolution entry now tracks individual file paths (`VideoCodecPaths`, `VideoAudioCodecPaths`, `MusicAudioCodecPaths`, `ContainerFormatPaths`, `ResolutionPaths`) for drill-down inspection in the UI.
- **Trash Contents Detail API** — New `/Trash/Contents` endpoint returning per-library trash items with original name, size, trashed date, and expected purge date. New `/Trash/Folders` GET/DELETE endpoints for trash folder management.
- **Trash Disable Dialog** — When unchecking "Use Trash" in Settings, a dialog shows which trash folders exist and offers to delete them.
- **Other File Tracking** — Statistics now track unrecognized/other files (`OtherSize`, `OtherFileCount`) per library.
- **6-Tab Dashboard** — Refactored into modular tabs: Overview, Codecs, Health, Trends, Settings, Arr Integration.
- **Modular CSS/JS Build** — New `ComposeConfigPage` MSBuild task that concatenates separate CSS and JS modules into the final `configPage.html` at build time. Each tab has its own `.css` and `.js` file.
- **XSS Protection** — HTML escaping in badge rendering and configuration page inputs.
- **Boxset/Collection Skipping** — Health checks automatically skip boxset/collection libraries.

### Changed
- **Dashboard Architecture** — Migrated from monolithic config page to modular 6-tab architecture with shared utilities (`Shared.js`, `Shared.css`).
- **README** — Comprehensive rewrite reflecting all new features, API endpoints, configuration options, and architecture.
- **Test Count** — Increased from 315 to **572 tests** covering multi-instance Arr, connection testing, persisted statistics, embedded subtitles, codec path tracking, trash contents, modular build, and all new UI features.

---

## [1.0.4.0] — 2026-04-10

### Added
- **STRM File Repair** — New task that detects and repairs broken `.strm` files whose referenced media file has been renamed or moved. Searches the parent directory for a matching media file and updates the path. URL-based `.strm` files are left untouched.
- **TaskMode System** — Unified `TaskMode` enum (`Activate`, `DryRun`, `Deactivate`) replaces the previous individual boolean flags (`DryRunTrickplay`, `EnableSubtitleCleaner`, etc.). Each cleanup/repair task can now be independently configured.
- **Master HelperCleanupTask** — A single orchestrating `IScheduledTask` that runs all sub-tasks (Trickplay, Empty Folders, Orphaned Subtitles, STRM Repair) sequentially, respecting each task's configured mode. Replaces the previous separate scheduled tasks.
- **Config Migration** — Automatic one-time migration from legacy boolean flags to the new `TaskMode` values via `ConfigVersion`.

### Fixed
- **ConfigVersion Not Preserved** — `UpdateConfiguration` in the API controller now preserves `ConfigVersion` from the current config, preventing the legacy migration from re-running after every settings save.
- **DI Resolution** — Removed `System.IO.Abstractions.IFileSystem` from `HelperCleanupTask` constructor (not registered in Jellyfin's DI container). Now instantiated directly in `RunStrmRepair()`.

### Changed
- **README** — Updated to reflect new STRM Repair feature, TaskMode system, master task architecture, new API endpoints, and revised configuration options.
- **Test Count** — Increased from 244 to **315 tests** covering STRM repair, TaskMode, HelperCleanupTask orchestration, and config migration.

---

## [1.0.3.0] — 2026-04-09

### Fixed
- **Plugin Logo 404** — `logo.png` now included as physical file in release ZIP alongside `meta.json` with `"imagePath": "logo.png"`. Jellyfin 10.11 serves plugin images from disk, not from embedded resources.
- **SanitizeFileName Null-Char** — `PathValidator.SanitizeFileName` now correctly replaces `\0` (null byte) and all invalid filename characters.
- **Duplicate Config UI** — Removed duplicated "Cleanup Statistics" section in `configPage.html`.

### Changed
- **Plugin.cs** — Removed unused `GetThumbImage()` method and `System.IO`/`System.Reflection` imports.
- **Release ZIP** — Now contains `logo.png` + auto-generated `meta.json` (with guid, version, imagePath, assembly).

---

## [1.0.2.0] — 2026-04-09

### Fixed
- **Subtitle False Positives** — `IsSubtitleSuffix` used a naive "2-3 letter" heuristic that incorrectly matched non-language tokens like "DTS", "HDR", "S01", "720p". Replaced with explicit ISO 639-1/639-2 allowlists (`MediaExtensions.KnownLanguageCodes`, `MediaExtensions.SubtitleFlags`).
- **Exception Handling** — Broadened `catch (Exception)` blocks in `ArrIntegrationService` narrowed to specific types (`HttpRequestException`, `JsonException`, `TaskCanceledException`).
- **`_ = ex;` Anti-Pattern** — Removed meaningless `_ = ex;` assignments in `TrashService`, replaced with descriptive comments.
- **Inconsistent StringComparison** — `CompareRadarrWithJellyfin` / `CompareSonarrWithJellyfin` now enforce `OrdinalIgnoreCase` regardless of caller-supplied `HashSet` comparer.

### Changed
- **Subtitle Allowlists → MediaExtensions** — `SubtitleFlags` and `KnownLanguageCodes` moved from `CleanOrphanedSubtitlesTask` to `MediaExtensions` for central, reusable access.
- **ArrIntegrationService DI** — Now receives `HttpClient` via constructor (from `IHttpClientFactory`) instead of a static instance.
- **CleanupTrackingService Thread-Safety** — `RecordCleanup` and `ResetStatistics` now use `lock` around config read/write/save to prevent race conditions.

### Added
- **New Tests** — `CleanOrphanedSubtitlesTaskTests` (subtitle base name parsing, false-positive regression), `PathValidatorTests` (IsSafePath, SanitizeFileName). Test count increased from 212 to **244**.

---

## [1.0.1.0] — 2026-04-09

### Fixed
- **Config Page** — `<style>` and `<script>` tags moved inside `<div data-role="page">` wrapper; Jellyfin's web client now properly loads the settings page JavaScript and styles.
- **Sidebar Visibility** — Plugin now appears in the Jellyfin dashboard sidebar menu (`EnableInMainMenu = true`, `DisplayName = "Jellyfin Helper"`).

### Changed
- **Test Count** — Increased from 196 to **212 tests** covering additional edge cases for empty media folder cleanup (metadata-only folders, boxset/collection skip, nested audio detection, subtitle-only orphans, various audio/subtitle extensions).

---

## [1.0.0.0] — 2026-04-09

### Added

#### 📊 Dashboard & Statistics
- **Media Library Statistics** — Per-library breakdown with video codec, resolution, container format detection.
- **Audio Codec Analysis** — Audio codecs (AAC, FLAC, MP3, Opus, DTS, AC3, TrueHD, Vorbis, ALAC, PCM, WMA, APE, WavPack, DSD) parsed from filenames and extensions, displayed as donut chart.
- **Export as JSON/CSV** — Download complete statistics as file.
- **Historical Trend** — Statistics snapshots saved on every scan (max 365 entries), trend graph shows library growth over time.
- **Cleanup Statistics** — Dashboard shows lifetime bytes freed, total items deleted, last cleanup timestamp.

#### 🧹 Cleanup Tasks
- **Trickplay Folder Cleanup** — Detects and removes orphaned `.trickplay` folders.
- **Empty Media Folder Cleanup** — Removes media folders that no longer contain video or audio files.
- **Orphaned Subtitle Cleaner** — Detects and removes orphaned subtitle files (`.srt`, `.sub`, `.ssa`, `.ass`, `.vtt`, etc.).
- **Dry-run modes** for all cleanup tasks.

#### 🗑️ Trash / Recycle Bin
- **Trash Service** — Files and folders moved to timestamped trash folder instead of permanent deletion. Expired items auto-purged after configurable retention period.

#### 🔗 Arr Stack Integration
- **Radarr/Sonarr Comparison** — Compare Jellyfin library with Radarr/Sonarr to find items in both, only in Arr, or only in Jellyfin.

#### 🌐 Internationalization
- **Multi-language Dashboard** — UI translations for 7 languages: English, German, French, Spanish, Portuguese, Chinese, Turkish.

#### ⚙️ Configuration
- **Library Include / Exclude Lists** — Include or exclude specific libraries from cleanup tasks.
- **Orphan Minimum Age** — Configurable minimum age (days) before orphaned items are eligible for deletion.
- **Music & Boxset Protection** — Music libraries and Boxset/Collection folders are automatically excluded from cleanup.

#### 🔐 Security / Robustness
- **Rate Limiting** — Statistics endpoint protected (min 30s between scans, HTTP 429).
- **Input Validation** — Path traversal protection, null-byte check, filename sanitization.
- **Caching** — Statistics cached for 5 minutes with `IMemoryCache`.

#### 🔧 Code Quality
- **196 tests** covering all services, tasks, and edge cases.
- **Automated GitHub Releases** — Pipeline creates ZIP, checksums, and metadata PR.
