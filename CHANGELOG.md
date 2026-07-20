# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses 4-part versioning (`x.x.x.x`) consistent with the Jellyfin plugin ecosystem.

## [3.0.0.0] - 2026-07-08

### Added
- **Smarter recommendation engine** - The neural network behind your recommendations is now four times bigger and uses dropout regularisation, so it learns your taste more reliably and generalises better beyond what you've already watched.

### Improved
- **Better recommendations from day one** - Re-watching a favourite nudges the algorithm noticeably now. Actors and directors you love outrank cameo overlaps. Box-set suggestions ("finish the trilogy") stay consistent between what you see and what the model learned from.
- **Fairer cold-start** - Brand-new users get community-blended suggestions (top-rated + trending) instead of pure recency, so the first list feels curated rather than random.
- **More diverse top picks** - Ranking now balances genre, studio and release era — no more ten Marvel films in a row.
- **Faster scans on big libraries** - Watch-history and recommendation scans use Jellyfin 12's batch APIs; on large libraries this shaves seconds off every scheduled run.
- **Cleaner Settings page** - Reorganised into four clear cards (General, Tasks & Trash, Integrations, Backup) with a sticky save bar and an unsaved-changes indicator, so nothing gets lost.
- **Health tab now in tidy cards** - Library health checks and trash contents each live in their own card, so the page feels calmer and easier to scan.
- **Arr tab redesigned** - Instead of one button per instance, each Arr type (Radarr, Sonarr) now has a single dropdown to pick which instance to compare, with a live "reachable" indicator right on the dropdown (green tick, red cross, or a spinner while checking). Fewer buttons, no more layout jumping when instance names differ in length, and you see straight away whether the selected instance is online.
- **Better on phones** - The Health, Arr and Settings tabs now shrink their padding on small screens and keep long library names and file paths from breaking the layout.
- **Compact weight serialization** - Persisted recommendation weights now use compact (non-indented) JSON, roughly a third the size of the equivalent indented form. Note: the v3 neural architecture has ~4.5× more parameters than v2, so on-disk files are larger than v2 files even in compact form.

### Fixed
- **Recommendations sometimes silently drifted** - Four subtle bugs where training and live scoring used slightly different formulas (weekend detection, popularity, box-set progression, discovery feedback). Your recommendations are now trained on exactly the same signals they're scored on.
- **Rare "lost save" on Windows** - Cache and state files could occasionally be dropped when an antivirus scanner briefly held the target file. All writes now retry automatically.

### Breaking
- **Requires Jellyfin 12.0+** - v3.x will not install on Jellyfin 10.x. If you're still on Jellyfin 10.x, stay on v2.1.0.5 (served from the same plugin repository).

### Tests
- Total: **3779 tests** (+1461 vs. v2.1.0.5). New tests cover the JF 12 batch fallback paths, weighted `PeopleSimilarity`, and the progression multiplier in `PreferenceBuilderTests` locking the shared `[ProgressionFloor, ProgressionCeiling]` formula (`0.3 + rawRatio * 1.2`, clamped to `[0.3, 1.5]`). Round 9 hardens the `Engine` pipeline (cold-start + warm-user + batch loop) and the `SeerrDiscoveryService.GenerateDiscoveryRecommendationsAsync` guards (Deactivate/DryRun/config-missing/no-active-users/cancellation). Round 10 pins value-type setter guards on `DiscoveryRecommendation` (NaN / ±Infinity / clamp on `Score`, `TmdbRating`, `Popularity`), `RecommendedItem` (7 collection setters × `null` and non-`null` branches), `LibraryInsightsResult` (3 collections + reassignment), and `SeerrRequestPage` (`Results` null-coalesce) so a regression removing any `?? []` / `IsFinite(...) ? … : 0.0` guard surfaces immediately. Round 11 covers `ArrIntegrationController` uncovered branches: index parameter (negative / out-of-range / valid), the 502-Bad-Gateway path with named failed instance, `IOException` / `UnauthorizedAccessException` swallow in `GetJellyfinFolderNames`, and the trash-folder exclusion — plus a design-contract test that locks the current filter-aware "no configured instances" semantic in `GetEffectiveRadarrInstances` / `GetEffectiveSonarrInstances`.

---

## [2.1.0.5] - 2026-05-26

### Fixed
- **Symlink / Junction Infinite Recursion** - `GrowthTimelineService.GetDirectorySize()` now skips any subdirectory carrying `FileAttributes.ReparsePoint` (symlinks and NTFS junction points on Windows, symlinks on Linux/macOS). Previously a circular directory structure (A → B → A) caused unbounded recursion and a `StackOverflowException`.
- **Trash Path Length Overflow** - `TrashService.ResolveCollision()` now enforces the OS path-length limit on every returned path (259 chars on Windows, 4 095 on Linux). Long directory names were never truncated before, causing an `IOException` when the combined timestamp prefix + original name exceeded `MAX_PATH`.

### Added
- **Swedish Language (sv)** - Full Swedish translation with 352 localized strings, available in the dashboard language selector as "Svenska".

### Changed
- **Minimum Jellyfin Version** - Raised to **10.11.10** (NuGet `Jellyfin.Controller 10.11.10`, `Jellyfin.Model 10.11.10`).
- **Excluded Libraries Widget** - The library exclusion setting now uses a multi-select dropdown with checkboxes instead of a free-text input, providing a clearer overview and preventing typos.

### Improved
- **Trash Folder Path Browser** - Integrated an interactive folder browser dialog for the trash path setting, allowing admins to visually navigate the filesystem and select the target directory instead of typing paths manually.
- **Trash Path Change Dialog** - When the trash folder path is changed while trash is enabled, a dialog prompts the admin to either move existing trash content to the new location or delete it and start fresh. This prevents orphaned trash folders from accumulating on disk. The dialog appears consistently across all three save paths (Save button, auto-save, and unsaved-changes prompt).

### Removed
- **Include Libraries Setting** - Removed the redundant "Include Libraries" configuration option. The existing "Excluded Libraries" setting already provides the same functionality in a more intuitive way (exclude = everything else is included).

### Tests
- Total: **2318 tests** (`GrowthTimelineSymlinkTests`, `TrashServicePathLengthTests`).

---

## [2.1.0.4] - 2026-05-24

### Fixed
- **IUserManager API Upgrade** - Upgraded from deprecated `IUserManager.Users` property (removed in Jellyfin 10.11.8) to the stable `IUserManager.GetUsers()` method (10.11.9+ API). Resolves `MissingMethodException: Method not found 'IUserManager.get_Users()'` on all Jellyfin 10.11.8+ installations. Zero reflection — direct compile-time API call. Also fixed the same issue in `UserActivityInsightsService.BuildActivityReport()`.
- **Trickplay Trash Re-Trashing Loop** - Fixed a critical bug where `CleanTrickplayTask` would recursively scan into the trash folder, re-detect previously trashed `.trickplay` directories as orphans, and move them to trash again on every scheduled run. Each cycle prepended a new timestamp prefix (`yyyyMMdd-HHmmss_`) to the folder name, eventually exceeding the OS path length limit (PATH_MAX) and causing an `IOException`. The task now excludes the configured trash folder (including custom paths) from its directory scan. A defense-in-depth guard in `TrashService.MoveToTrash()` additionally rejects any source path that already resides inside the trash folder.

### Changed
- **Minimum Jellyfin Version** - Raised to **10.11.9** (NuGet `Jellyfin.Controller 10.11.9`). The deprecated `Users` property was removed in 10.11.8; `GetUsers()` stabilized in 10.11.9.

### Tests
- Total: **2219 tests** (`CleanTrickplayTrashExclusionTests`, `TrashServiceGuardTests`, `WatchHistoryCompatTests`).

---

## [2.1.0.3] - 2026-05-21

### Added
- **Discovery External Links** - Flipping a discovery card now shows TMDB and Seerr deep links on the back side (above the description). Links open the system browser on mobile to avoid WebView navigation issues. A new backend endpoint (`GET /JellyfinHelper/Discovery/My/ExternalLinks`) provides the configured Seerr base URL to the frontend.

### Fixed
- **Discovery Request Submission** - Fixed 400 Bad Request when submitting discovery requests on Seerr instances where the first configured Radarr/Sonarr server has ID 0. The DTO validation now correctly accepts 0-based server and profile IDs used by Seerr.
- **Quality Profile Popup** - Fixed duplicate quality profiles appearing in the selection popup for users with advanced permissions when multiple root folders are configured on the same server. Profiles are now deduplicated by ID before being served to the frontend.
- **Discovery Sidebar Visibility** - The "Discover New Content" sidebar navigation item is now only shown when Discovery is active and has recommendations. When `DiscoveryUserAccessEnabled` is disabled or the recommendation task is deactivated/dry-run, the sidebar item is no longer injected.
- **Watch History Collection** - Added graceful error handling for `MissingMethodException` when the plugin encounters an incompatible Jellyfin runtime. A concise warning with exact incompatibility details is logged instead of crashing the scheduled task.

### Tests
- Total: **2206 tests**.

---

## [2.1.0.2] - 2026-05-18

### Improved
- **Discovery Sidebar Navigation** - The tab layout can show more than 5 recommendations in one row now. Automatically reloading next recommendation after one was dismissed or requested.

---

## [2.1.0.1] - 2026-05-18

### Fixed
- **Discovery Sidebar Navigation** - The sidebar "Seerr Discovery" link now finds the Discovery tab dynamically by its container's `data-index` attribute, regardless of the user-configured tab name. Previously, it relied on an exact text match against the i18n title, which failed when users named their Custom Tab differently (e.g. "Discover" instead of "Seerr Discovery"). Also added fallback navigation to the home page when clicked from a non-home route.

---

## [2.1.0.0] - 2026-05-09

### Added
- **Seerr Discovery** - Personalized content discovery via Overseerr/Jellyseerr. Scores TMDb candidates per user using the ensemble ML strategy, suggests not-yet-in-library media with one-click request submission, parental rating enforcement, language-based discovery, and automatic Arr library exclusion. Displayed in the Discover tab and optionally on the Jellyfin home screen.
- **Discovery Custom Tab & Sidebar** - Discovery recommendations can be rendered directly on the Jellyfin home page via the Custom Tab plugin (poster flip-cards, score bars, instant request buttons). A sidebar navigation link provides quick access.
- **File Transformation Support** - Script injection into Jellyfin's `index.html` uses the File Transformation plugin when available (Docker-compatible, no filesystem write). Falls back to direct file modification otherwise.
- **User Discovery API** - Authenticated users can view their own recommendations and submit requests via their linked Seerr account (`/JellyfinHelper/Discovery/My`).
- **Admin Discovery API** - Endpoints for viewing all user results, listing Seerr users, querying Radarr/Sonarr service info, and submitting requests with server/profile overrides (`/JellyfinHelper/Discovery`).
- **Seerr User Mapping** - Jellyfin user IDs are automatically resolved to Seerr user IDs so requests appear under the correct account.
- **Discovery User Access Toggle** - New `DiscoveryUserAccessEnabled` setting allows admins to control whether regular users can access Discovery.
- **Discovery Feedback Loop for ML Training** - User interactions with Discovery recommendations (shown, dismissed, requested, watched) are persisted as training data. The recommendation engine uses this feedback in Phase 4 of training: items that were requested and later watched produce strong positive signals, dismissed items produce negative signals, and merely shown items serve as weak negatives — continuously improving recommendation quality over time.

### Tests
- Total: **2190 tests**.

---

## [2.0.0.3] - 2026-05-08

### Refactored
- **Arr Settings** - Settings now use instance-based Radarr/Sonarr configuration; legacy single-instance fields removed. Language and plugin log level persist across saves. ARR instance resolution returns explicit instance lists only.

### Added
- **Recommendation Metadata** - Recommendations include audio language metadata and item creation dates; watched/exclusion now uses "meaningful interaction" predicates.
- **New Scoring Signals** - `SubtitleLanguageAffinity` and `CollectionProgressionBoost` added to the 31-feature candidate vector, boosting recommendations where subtitle language preferences and box-set progression are strong signals.

### Improved
- **Scoring & Training** - Centralized popularity scoring, richer training feature construction, series aggregation, and weight schema version bumped. A/B testing cohort infrastructure with deterministic user bucketing and adaptive sigmoid midpoint calibration via cohort watch-rate feedback.
- **NeuralScoringStrategy** – Upgraded MLP architecture from 3 to 4 hidden layers (31→48→24→12→6→1, ~3,097 parameters). Deeper representation captures more complex feature interactions while keeping inference lightweight.

### Tests
- Total: **2124 tests**.

---

## [2.0.0.2] - 2026-05-02

### Changed
- **Material Symbols** - Replaced all decorative emojis with Material Symbols font icons for consistent cross-platform rendering.
- **Dash cleanup** - Standardized dash punctuation across all files.

### Fixed
- **CleanOrphanedSubtitlesTask** - Improved subtitle filename parsing to avoid false deletions.

---

## [2.0.0.1] - 2026-04-28

### Improved
- **Discover Tab** - Recommendations and Watch Activity sections are now collapsible (default: collapsed). Both use the same toggle pattern for consistent UX.
- **User Switch UX** - Collapsible state (open/closed) is preserved when switching between users in the Discover tab. Content updates in-place without forcing re-open.

---

## [2.0.0.0] - 2026-04-27

### Added
- **Discover Tab** - New 8th dashboard tab "Discover" combining ML-powered smart recommendations and user activity insights in a single view. Includes `Recommendations.js`, `Recommendations.css` frontend modules with user selector, recommendation cards, activity summaries, and genre distribution charts.
- **Smart Recommendation Engine** - ML-based per-user recommendation system (`Services/Recommendation/`) with four-tier scoring architecture:
  - `HeuristicScoringStrategy` - rule-based weighted scoring.
  - `LearnedScoringStrategy` - gradient-descent ML with Z-score standardization.
  - `NeuralScoringStrategy` - three-hidden-layer MLP (29→32→16→8→1, 1633 params).
  - `EnsembleScoringStrategy` - adaptive 3-way blend (Heuristic + Learned + Neural).
- **Playlist Sync** - Optional feature to sync recommendations to Jellyfin playlists (`IRecommendationPlaylistService`) with intelligent naming, creation, updating, and cleanup of recommendation playlists. Sync can be triggered automatically after generation or manually via API.
- **User Watch Profiles** - `WatchHistoryService` analyzes user watch history to build detailed watch profiles (`UserWatchProfile`) with genre/studio/people affinity scores, completion ratio distributions, time-based activity patterns, and favorites detection. These profiles feed into the recommendation engine for personalized scoring and are displayed as insights in the Discover tab.
- **Tests** - New test classes: `RecommendationControllerTests`, `UserActivityControllerTests`, `RecommendationEngineTests`, `WatchHistoryServiceTests`, `ScoringStrategyTests`, `NeuralScoringStrategyTests`, `ScoreExplanationTests`, `TrainingExampleTests`, `RankingMetricsTests`, `RecommendationCacheServiceTests`, `RecommendationDtoTests`, `UserActivityCacheServiceTests`, `UserActivityInsightsServiceTests`. `RecommendationPlaylistServiceTests` (8 tests), `RecommendationsTaskTests` (3 playlist-sync tests). Includes concurrency tests (parallel `Score()` + `Train()`), three-hidden-layer architecture tests, k-fold constant verification, ranking evaluation metric tests (Precision@K, Recall@K, NDCG@K), and genre exposure feature tests (underexposure, dominance, affinity gap, edge cases). Total: **2093 tests**.

### Changed
- **8-Tab Dashboard** - Dashboard expanded from 7 to up to 8 tabs: Overview, Codecs, Health, Trends, **Discover**, Settings, Arr, Logs (Discover tab is only visible when Recommendations is set to Dry Run or Activate).
- **HelperCleanupTask** - Extended to run recommendation generation and user activity aggregation alongside existing cleanup tasks.
- **Documentation** - Updated README.md, CONTRIBUTING.md, manifest.json, build.yaml, and CHANGELOG.md for the new Discover tab and all associated features.

### Fixed
- **Trends Tab** - "Largest" and "Recent" sections in the Trends tab were displaying the total size of the library in the tree view instead of the sum of the displayed objects.
- **Trends Tab** - The "Largest" section in the Trends tab was showing first shows instead of movies - now correctly shows movies first.
- **Plugin Log** - More precise logs when trash is enabled.
- **Plugin Uninstall** - Uninstalling the plugin did not remove the plugin's data files, which could lead to stale data if the plugin was reinstalled later. Now all plugin-related data files and recommendation playlists are cleaned up on uninstallation.
---

## [1.2.1.0] - 2026-04-20

### Added
- **Library Insights** - New "Insights" section in the Trends tab showing the largest media directories and recently added/changed items (last 30 days). Includes summary cards with expandable tree views grouped by library, type badges, and change indicators. New backend service (`ILibraryInsightsService`, `LibraryInsightsService`) with filesystem scanning, new API endpoint (`GET /JellyfinHelper/LibraryInsights`) with 15-minute in-memory caching, and new data models (`LibraryInsightEntry`, `LibraryInsightsResult`).
- **Dynamic Range Mock Data** - Added dynamic range mock data (`DynamicRanges`, `DynamicRangeSizes`, `DynamicRangePaths`) to the live demo for Movies and TV Shows libraries.
- **Library Insights Mock Data** - Added library insights mock data and API route to the live demo.

### Changed
- **Statistics Refactored to MediaStream** - Video codecs, resolutions, and dynamic range are now extracted from Jellyfin `MediaStream` metadata, while audio codec detection is `MediaStream`-first with filename/extension fallback where metadata is missing. Supports differentiated audio codecs (TrueHD Atmos, DTS-X, DTS-HD MA, EAC3 Atmos, etc.).
- **Dynamic Range Detection** - New per-library dynamic range statistics (`HDR10`, `HDR10+`, `Dolby Vision`, `HLG`, `SDR`) with `VideoRangeType` → `VideoRange` fallback chain.
- **Resolution Classification** - Extended to 8K, 4K, 1440p, 1080p, 720p, 576p, 480p, SD with width+height-based classification.
- **Donut Chart Enhancements** - Added dynamic range donut chart, improved codec icon mapping, animation support for all donut charts.
- **Documentation** - Updated CONTRIBUTING.md and README.md to reflect MediaStream-based extraction, dynamic range feature, and library insights.

### Fixed
- **Performance** - Video streams cached per-item to avoid redundant `GetMediaStreams()` calls during statistics scan.

---

## [1.2.0.0] - 2026-04-16

### Added
- **Seerr Cleanup Task** - New scheduled task (`SeerrCleanupTask`) to automatically clean up unavailable media requests from Overseerr/Jellyseerr. New `Services/Seerr/` domain with `ISeerrIntegrationService`, `SeerrIntegrationService`, and Seerr DTOs. Added `Api/SeerrController.cs` for connection testing (`/JellyfinHelper/Seerr/Test`).
- **Unsaved Settings Alert** - The settings page now warns users before navigating away with unsaved changes (dirty-tracking via JSON snapshot comparison). Offers "Discard", "Save & Continue", or "Cancel" options.
- **Collapsible Arr Sections** - Radarr, Sonarr, and Seerr configuration sections are now collapsible with chevron animation, dynamic instance count display (checkmark / (n)), and full localization support.
- **Auto-Save Dropdowns** - Task mode selects (Trickplay, Empty Folders, Subtitles, Link Repair, Seerr) and the Language dropdown now auto-save on change with inline checkmark/cross indicator, eliminating the need to click "Save Settings" for quick changes.
- **Auto-Init Scan** - The Overview page now automatically triggers an initial media scan when no cached statistics are available, eliminating the need to manually click "Scan Libraries" on first visit. The scan button has been redesigned as a compact icon button with a spinning animation during scans.
- **Scroll Position Restore** - Language change and backup import now preserve the scroll position after UI rebuild, preventing the page from jumping to the top.

### Fixed
- **Plugin Logo** - Fixed `imagePath` in `meta.json` to use absolute `/config/plugins/` path matching Jellyfin's expected format.
- **meta.json Structure** - Replaced invalid `assembly` field with `assemblies: []`, added missing `changelog`, `timestamp`, and `imageUrl` fields.
- **meta.json Generation** - Switched from heredoc to `jq` for safe JSON generation (prevents broken JSON from special characters in changelog).

### Changed
- **4-Part Versioning** - All versions now use 4-part format (`x.x.x.x`) consistent with other Jellyfin plugins (e.g. Jellyfin Enhanced, Intro Skipper).
- **Link Repair** - Renamed "STRM Repair" task to "Link Repair". The task now scans for both broken `.strm` files and broken symlinks, repairing them by locating renamed/moved target files. Refactored `Services/Strm/` to `Services/Link/` with Strategy pattern (`ILinkHandler` → `StrmLinkHandler`, `SymlinkHandler`).
- **Configuration** - `StrmRepairTaskMode` renamed to `LinkRepairTaskMode`.
- **Scheduled Task** - `RepairStrmFilesTask` renamed to `RepairLinksTask`.
- **Save Workflow** - `doSaveSettings()` now supports a `quiet` mode with `{ quiet: true, element: el }` options for auto-save (no button animation, inline indicator instead). Language change no longer triggers a full-page reload (`PluginPages/js/Main.js`).
- **Log Level Auto-Save** - Log level dropdown in the Logs tab now uses the shared `showAutoSaveIndicator()` function from `Shared.js` for consistent UX across all auto-save controls.
- **Documentation** - Updated CONTRIBUTING.md, README.md, manifest.json, and build.yaml to reflect Link Repair naming, symlink support, Seerr integration, and UI improvements.

---

## [1.1.0.0] - 2026-04-16

### Added
- **Trends UI Enhancements** - Improved CSS and JS for the Trends tab with better chart rendering and responsiveness.

### Changed
- **GrowthTimelineService Performance** - Optimized performance of timeline aggregation and bucketing logic.
- **BackupService Performance** - Optimized backup service methods for better efficiency.
- **Service Refactoring** - Refactored monolith `BackupService.cs` and `GrowthTimelineService.cs` into smaller, focused components (`TimelineAggregator`, `BackupSanitizer`).
- **Cross-Platform Compatibility** - Improved case handling in tests for cross-platform compatibility.
- **CI/CD** - Updated PR workflow, bumped `softprops/action-gh-release` from v2 to v3.
- **CONTRIBUTING.md** - Updated with new test count and fixture architecture details.

### Removed
- **Legacy History Cleanup** - Removed legacy history file cleanup method (preparation for this version).

---

## [1.0.9.0] - 2026-04-14

### Removed
- **Statistics History** - Removed legacy scan-based snapshot system (`StatisticsHistoryService`, `StatisticsSnapshot`), replaced entirely by the growth timeline. The `/Statistics/History` API endpoint has been removed.
- **Export Endpoints** - Removed `/Statistics/Export/Json` and `/Statistics/Export/Csv` API endpoints (data export is handled via Backup/Restore).
- **History in Backup** - Removed `StatisticsHistory` from backup data and `HistorySnapshotsRestored` from restore summary.

### Added
- **`StatisticsCacheService`** - New focused service replacing `StatisticsHistoryService`, responsible solely for caching the latest scan result to disk.
- **Legacy History File Cleanup** - `HelperCleanupTask` now automatically deletes the legacy `jellyfin-helper-statistics-history.json` file from previous versions.
- **Growth Timeline Interpolation** - Frontend now interpolates missing intermediate buckets between sparse data points for a continuous chart line with granularity-aware bucket advancement.
- **Growth Timeline Deduplication** - Backend deduplicates consecutive identical timeline data points for compact storage.
- **Backup & Restore in Demo** - Live demo (docs/) now includes the full Backup & Restore UI in the Settings tab.

### Changed
- **Backup Size Limit** - Client-side backup import size check reduced from 50 MB to 10 MB.
- **Growth Timeline Service** - Significantly expanded with deduplication logic, improved bucketing, and granularity validation.
- **i18n** - Updated all 7 language files with new/revised translation keys.
- **Test Count** - Updated to **957 tests** (removed export/history tests, added growth timeline deduplication and interpolation tests).

---

## [1.0.8.0] - 2026-04-12

### Added
- **Backup & Restore** - New backup/restore functionality to export and import plugin configuration and historical data as JSON.
- **Growth Timeline** - New growth timeline visualization displaying cumulative media growth over time with granular bucketing (daily/weekly/monthly/quarterly/yearly).

### Changed
- **Project Restructure** - Reorganized project structure for better maintainability and modularity.
- **TrendChart Enhancements** - Improved scaling, labels, and mobile responsiveness for the trend chart visualization.
- **Download Buttons Relocated** - JSON/CSV download buttons moved for better UX placement.
- **Responsive Data Tree** - Data tree component made responsive for touch devices.

### Fixed
- **Jellyfin Compatibility** - Fixed bug preventing plugin usage on Jellyfin versions below 10.11.8 by downgrading Jellyfin.Controller and Jellyfin.Model package versions to 10.11.0.

---

## [1.0.7.0] - 2026-04-12

### Added
- **Plugin Log Viewer** - New **Logs** tab in the dashboard providing real-time access to plugin-specific log entries with level filtering (DEBUG/INFO/WARN/ERROR), source component filtering, auto-refresh (10s), download as `.log` file, and clear with confirmation dialog.
- **Log API Endpoints** - `GET /Logs` (with `?limit`, `?minLevel`, `?source` query params), `GET /Logs/Download`, `DELETE /Logs`.
- **Log Level Persistence** - Selected log level is persisted to the plugin configuration (`PluginLogLevel`) and restored on page load.
- **Enhanced Backend Logging** - `MediaStatisticsService` now logs scan start/end summaries, per-library file counts, and detailed breakdowns at DEBUG level.
- **Dedicated Per-Tab CSS Modules** - Each tab now has its own CSS file: `Overview.css`, `Codecs.css`, `Health.css`, `Trends.css`, `Settings.css`, `ArrIntegration.css`, `Logs.css`.

### Changed
- **7-Tab Dashboard** - Dashboard expanded from 6 to 7 tabs with the addition of the Logs tab.
- **Log Level Moved to Logs Tab** - The log level dropdown was removed from the Settings tab and is now exclusively in the Logs tab for direct context.
- **README** - Updated to reflect 7-tab dashboard, new Logs feature section, 3 new API endpoints, Plugin Log Level configuration option, complete folder structure with all CSS/JS modules, and updated test count.
- **Test Count** - Increased from 669 to **737 tests** with new `LogsHtmlTests` (68 tests) covering all Logs tab UI elements, API calls, i18n keys, auto-refresh, download mechanism, and log level persistence.

---

## [1.0.6.0] - 2026-04-11

### Fixed
- **Trash exclusion in statistics** - Trash folders are now explicitly excluded from media statistics calculations to avoid distorted results.
- **TV show metadata false positives** - Fixed a bug where empty metadata directories of TV shows were incorrectly marked as orphaned.
- **Trash dialog in UI** - Fix for the confirmation dialog when disabling the trash, which was not showing under certain conditions.

---

## [1.0.5.0] - 2026-04-11

### Added
- **Multi-Instance Arr Support** - Up to 3 Radarr and 3 Sonarr instances simultaneously (e.g. "Radarr 4K", "Radarr Anime") with per-instance comparison and merged views. Automatic migration from legacy single-instance configuration.
- **Arr Connection Test** - New `/Arr/TestConnection` endpoint with a test button in the Settings UI to validate URL + API key before saving.
- **Persisted Latest Scan Result** - Statistics are now persisted to disk via `StatisticsHistoryService.SaveLatestResult()` and loaded on dashboard open via `/Statistics/Latest` without requiring a new scan. Results survive server restarts.
- **Post-Cleanup Statistics Scan** - After each `HelperCleanupTask` run, a statistics scan is automatically executed and persisted.
- **Embedded Subtitle Detection** - Health check "Videos without subtitles" now considers embedded subtitle streams (via Jellyfin's `MediaStream` data), not just external `.srt` files.
- **Video vs Music Audio Codecs** - Audio codec analysis is now split into two categories: codecs parsed from video filenames (`VideoAudioCodecs`) and codecs from music files (`MusicAudioCodecs`) with extension-based fallback.
- **Codec File Path Tracking** - Each codec, container format, and resolution entry now tracks individual file paths (`VideoCodecPaths`, `VideoAudioCodecPaths`, `MusicAudioCodecPaths`, `ContainerFormatPaths`, `ResolutionPaths`) for drill-down inspection in the UI.
- **Trash Contents Detail API** - New `/Trash/Contents` endpoint returning per-library trash items with original name, size, trashed date, and expected purge date. New `/Trash/Folders` GET/DELETE endpoints for trash folder management.
- **Trash Disable Dialog** - When unchecking "Use Trash" in Settings, a dialog shows which trash folders exist and offers to delete them.
- **Other File Tracking** - Statistics now track unrecognized/other files (`OtherSize`, `OtherFileCount`) per library.
- **6-Tab Dashboard** - Refactored into modular tabs: Overview, Codecs, Health, Trends, Settings, Arr Integration.
- **Modular CSS/JS Build** - New `ComposeConfigPage` MSBuild task that concatenates separate CSS and JS modules into the final `configPage.html` at build time. Each tab has its own `.css` and `.js` file.
- **XSS Protection** - HTML escaping in badge rendering and configuration page inputs.
- **Boxset/Collection Skipping** - Health checks automatically skip boxset/collection libraries.

### Changed
- **Dashboard Architecture** - Migrated from monolithic config page to modular 6-tab architecture with shared utilities (`Shared.js`, `Shared.css`).
- **README** - Comprehensive rewrite reflecting all new features, API endpoints, configuration options, and architecture.
- **Test Count** - Increased from 315 to **572 tests** covering multi-instance Arr, connection testing, persisted statistics, embedded subtitles, codec path tracking, trash contents, modular build, and all new UI features.

---

## [1.0.4.0] - 2026-04-10

### Added
- **STRM File Repair** - New task that detects and repairs broken `.strm` files whose referenced media file has been renamed or moved. Searches the parent directory for a matching media file and updates the path. URL-based `.strm` files are left untouched.
- **TaskMode System** - Unified `TaskMode` enum (`Activate`, `DryRun`, `Deactivate`) replaces the previous individual boolean flags (`DryRunTrickplay`, `EnableSubtitleCleaner`, etc.). Each cleanup/repair task can now be independently configured.
- **Master HelperCleanupTask** - A single orchestrating `IScheduledTask` that runs all sub-tasks (Trickplay, Empty Folders, Orphaned Subtitles, STRM Repair) sequentially, respecting each task's configured mode. Replaces the previous separate scheduled tasks.
- **Config Migration** - Automatic one-time migration from legacy boolean flags to the new `TaskMode` values via `ConfigVersion`.

### Fixed
- **ConfigVersion Not Preserved** - `UpdateConfiguration` in the API controller now preserves `ConfigVersion` from the current config, preventing the legacy migration from re-running after every settings save.
- **DI Resolution** - Removed `System.IO.Abstractions.IFileSystem` from `HelperCleanupTask` constructor (not registered in Jellyfin's DI container). Now instantiated directly in `RunStrmRepair()`.

### Changed
- **README** - Updated to reflect new STRM Repair feature, TaskMode system, master task architecture, new API endpoints, and revised configuration options.
- **Test Count** - Increased from 244 to **315 tests** covering STRM repair, TaskMode, HelperCleanupTask orchestration, and config migration.

---

## [1.0.3.0] - 2026-04-09

### Fixed
- **Plugin Logo 404** - `logo.png` now included as physical file in release ZIP alongside `meta.json` with `"imagePath": "logo.png"`. Jellyfin 10.11 serves plugin images from disk, not from embedded resources.
- **SanitizeFileName Null-Char** - `PathValidator.SanitizeFileName` now correctly replaces `\0` (null byte) and all invalid filename characters.
- **Duplicate Config UI** - Removed duplicated "Cleanup Statistics" section in `configPage.html`.

### Changed
- **Plugin.cs** - Removed unused `GetThumbImage()` method and `System.IO`/`System.Reflection` imports.
- **Release ZIP** - Now contains `logo.png` + auto-generated `meta.json` (with guid, version, imagePath, assembly).

---

## [1.0.2.0] - 2026-04-09

### Fixed
- **Subtitle False Positives** - `IsSubtitleSuffix` used a naive "2-3 letter" heuristic that incorrectly matched non-language tokens like "DTS", "HDR", "S01", "720p". Replaced with explicit ISO 639-1/639-2 allowlists (`MediaExtensions.KnownLanguageCodes`, `MediaExtensions.SubtitleFlags`).
- **Exception Handling** - Broadened `catch (Exception)` blocks in `ArrIntegrationService` narrowed to specific types (`HttpRequestException`, `JsonException`, `TaskCanceledException`).
- **`_ = ex;` Anti-Pattern** - Removed meaningless `_ = ex;` assignments in `TrashService`, replaced with descriptive comments.
- **Inconsistent StringComparison** - `CompareRadarrWithJellyfin` / `CompareSonarrWithJellyfin` now enforce `OrdinalIgnoreCase` regardless of caller-supplied `HashSet` comparer.

### Changed
- **Subtitle Allowlists → MediaExtensions** - `SubtitleFlags` and `KnownLanguageCodes` moved from `CleanOrphanedSubtitlesTask` to `MediaExtensions` for central, reusable access.
- **ArrIntegrationService DI** - Now receives `HttpClient` via constructor (from `IHttpClientFactory`) instead of a static instance.
- **CleanupTrackingService Thread-Safety** - `RecordCleanup` and `ResetStatistics` now use `lock` around config read/write/save to prevent race conditions.

### Added
- **New Tests** - `CleanOrphanedSubtitlesTaskTests` (subtitle base name parsing, false-positive regression), `PathValidatorTests` (IsSafePath, SanitizeFileName). Test count increased from 212 to **244**.

---

## [1.0.1.0] - 2026-04-09

### Fixed
- **Config Page** - `<style>` and `<script>` tags moved inside `<div data-role="page">` wrapper; Jellyfin's web client now properly loads the settings page JavaScript and styles.
- **Sidebar Visibility** - Plugin now appears in the Jellyfin dashboard sidebar menu (`EnableInMainMenu = true`, `DisplayName = "Jellyfin Helper"`).

### Changed
- **Test Count** - Increased from 196 to **212 tests** covering additional edge cases for empty media folder cleanup (metadata-only folders, boxset/collection skip, nested audio detection, subtitle-only orphans, various audio/subtitle extensions).

---

## [1.0.0.0] - 2026-04-09

### Added

#### Dashboard & Statistics
- **Media Library Statistics** - Per-library breakdown with video codec, resolution, container format detection.
- **Audio Codec Analysis** - Audio codecs (AAC, FLAC, MP3, Opus, DTS, AC3, TrueHD, Vorbis, ALAC, PCM, WMA, APE, WavPack, DSD) parsed from filenames and extensions, displayed as donut chart.
- **Export as JSON/CSV** - Download complete statistics as file.
- **Historical Trend** - Statistics snapshots saved on every scan (max 365 entries), trend graph shows library growth over time.
- **Cleanup Statistics** - Dashboard shows lifetime bytes freed, total items deleted, last cleanup timestamp.

#### Cleanup Tasks
- **Trickplay Folder Cleanup** - Detects and removes orphaned `.trickplay` folders.
- **Empty Media Folder Cleanup** - Removes media folders that no longer contain video or audio files.
- **Orphaned Subtitle Cleaner** - Detects and removes orphaned subtitle files (`.srt`, `.sub`, `.ssa`, `.ass`, `.vtt`, etc.).
- **Dry-run modes** for all cleanup tasks.

#### Trash / Recycle Bin
- **Trash Service** - Files and folders moved to timestamped trash folder instead of permanent deletion. Expired items auto-purged after configurable retention period.

#### Arr Stack Integration
- **Radarr/Sonarr Comparison** - Compare Jellyfin library with Radarr/Sonarr to find items in both, only in Arr, or only in Jellyfin.

#### Internationalization
- **Multi-language Dashboard** - UI translations for 7 languages: English, German, French, Spanish, Portuguese, Chinese, Turkish.

#### Configuration
- **Library Include / Exclude Lists** - Include or exclude specific libraries from cleanup tasks.
- **Orphan Minimum Age** - Configurable minimum age (days) before orphaned items are eligible for deletion.
- **Music & Boxset Protection** - Music libraries and Boxset/Collection folders are automatically excluded from cleanup.

#### Security / Robustness
- **Rate Limiting** - Statistics endpoint protected (min 30s between scans, HTTP 429).
- **Input Validation** - Path traversal protection, null-byte check, filename sanitization.
- **Caching** - Statistics cached for 5 minutes with `IMemoryCache`.

#### Code Quality
- **196 tests** covering all services, tasks, and edge cases.
- **Automated GitHub Releases** - Pipeline creates ZIP, checksums, and metadata PR.
