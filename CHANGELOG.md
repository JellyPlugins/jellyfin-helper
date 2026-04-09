# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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