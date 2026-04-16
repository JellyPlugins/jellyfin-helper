# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] — 2026-04-16

### Added
- **ARCHITECTURE.md** — New comprehensive architecture documentation covering project structure, service layers, and design patterns.
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

## [1.0.9] — 2026-04-14

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

## [1.0.8] — 2026-04-12

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

## [1.0.7] — 2026-04-12

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

## [1.0.6] — 2026-04-11

### Fixed
- **Trash exclusion in statistics** — Trash folders are now explicitly excluded from media statistics calculations to avoid distorted results.
- **TV show metadata false positives** — Fixed a bug where empty metadata directories of TV shows were incorrectly marked as orphaned.
- **Trash dialog in UI** — Fix for the confirmation dialog when disabling the trash, which was not showing under certain conditions.

---

## [1.0.5] — 2026-04-11

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
- **Dashboard Architecture** — Migrated from monolithic config page to modular 6-tab architecture with shared utilities (`shared.js`, `shared.css`).
- **README** — Comprehensive rewrite reflecting all new features, API endpoints, configuration options, and architecture.
- **Test Count** — Increased from 315 to **572 tests** covering multi-instance Arr, connection testing, persisted statistics, embedded subtitles, codec path tracking, trash contents, modular build, and all new UI features.

---

## [1.0.4] — 2026-04-10

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

## [1.0.3] — 2026-04-09

### Fixed
- **Plugin Logo 404** — `logo.png` now included as physical file in release ZIP alongside `meta.json` with `"imagePath": "logo.png"`. Jellyfin 10.11 serves plugin images from disk, not from embedded resources.
- **SanitizeFileName Null-Char** — `PathValidator.SanitizeFileName` now correctly replaces `\0` (null byte) and all invalid filename characters.
- **Duplicate Config UI** — Removed duplicated "Cleanup Statistics" section in `configPage.html`.

### Changed
- **Plugin.cs** — Removed unused `GetThumbImage()` method and `System.IO`/`System.Reflection` imports.
- **Release ZIP** — Now contains `logo.png` + auto-generated `meta.json` (with guid, version, imagePath, assembly).

---

## [1.0.2] — 2026-04-09

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

## [1.0.1] — 2026-04-09

### Fixed
- **Config Page** — `<style>` and `<script>` tags moved inside `<div data-role="page">` wrapper; Jellyfin's web client now properly loads the settings page JavaScript and styles.
- **Sidebar Visibility** — Plugin now appears in the Jellyfin dashboard sidebar menu (`EnableInMainMenu = true`, `DisplayName = "Jellyfin Helper"`).

### Changed
- **Test Count** — Increased from 196 to **212 tests** covering additional edge cases for empty media folder cleanup (metadata-only folders, boxset/collection skip, nested audio detection, subtitle-only orphans, various audio/subtitle extensions).

---

## [1.0.0] — 2026-04-09

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
- **Library Whitelist / Blacklist** — Include or exclude specific libraries from cleanup tasks.
- **Orphan Minimum Age** — Configurable minimum age (days) before orphaned items are eligible for deletion.
- **Music & Boxset Protection** — Music libraries and Boxset/Collection folders are automatically excluded from cleanup.

#### 🔐 Security / Robustness
- **Rate Limiting** — Statistics endpoint protected (min 30s between scans, HTTP 429).
- **Input Validation** — Path traversal protection, null-byte check, filename sanitization.
- **Caching** — Statistics cached for 5 minutes with `IMemoryCache`.

#### 🔧 Code Quality
- **196 tests** covering all services, tasks, and edge cases.
- **Automated GitHub Releases** — Pipeline creates ZIP, checksums, and metadata PR.