# Jellyfin Helper

A [Jellyfin](https://jellyfin.org/) plugin that provides automated cleanup tasks and media library statistics for your media library.

## Screenshots

> **📸 Placeholder** — Screenshots of the settings page will be added after the next release.

<!-- TODO: Add screenshots or GIFs of the settings page here
![Dashboard Overview](docs/screenshots/dashboard-overview.png)
![Audio Codec Chart](docs/screenshots/audio-codec-chart.png)
![Trend Graph](docs/screenshots/trend-graph.png)
![Export Buttons](docs/screenshots/export-buttons.png)
-->

## Features

### 🧹 Trickplay Folder Cleaner
Automatically deletes orphaned `.trickplay` folders that no longer have a corresponding media file. This typically happens when media files are renamed, moved, or deleted while the trickplay data remains behind.

### 📁 Empty Media Folder Cleaner
Automatically deletes top-level media folders whose entire directory tree contains files but absolutely **no video files**. This targets the common scenario where a movie or episode is deleted but the surrounding folder with metadata (`.nfo`), artwork (`.jpg`), subtitles (`.srt`), etc. remains as an orphaned folder.

**Important behaviors:**
- **Completely empty folders are skipped** — they are often pre-created by tools like Radarr/Sonarr for upcoming/"wanted" media
- **TV show folders are checked as a whole** — if at least one video exists anywhere in the tree (even in a deeply nested subdirectory), the entire show folder is kept untouched
- **Metadata-only folders are skipped** — folders containing only `.nfo` and image files (common for Radarr/Sonarr wanted-list placeholders) are not deleted
- **`.trickplay` folders are skipped** — they are handled by the Trickplay Folder Cleaner task

### 🧹 Orphaned Subtitle Cleaner
Automatically detects and removes orphaned subtitle files (`.srt`, `.sub`, `.ssa`, `.ass`, `.vtt`, etc.) that no longer have a corresponding video file in the same directory. This commonly occurs when media files are replaced, moved, or deleted while leftover subtitles remain behind.

### 📊 Media Library Statistics
A settings page that provides a comprehensive overview of your media library disk usage:
- **Video Data in Movies** / **Video Data in Series** / **Audio Data in Music**
- **Trickplay Data**, **Subtitle Data**, **Image Data**, **NFO/Metadata Data**
- Per-library breakdown with file counts
- **Video Codec Analysis** — HEVC, H.264, AV1, VP9, XviD, DivX, MPEG parsed from filenames
- **Audio Codec Analysis** — AAC, FLAC, MP3, Opus, DTS, AC3, TrueHD, Vorbis, ALAC, PCM, WMA, APE, WavPack, DSD displayed as donut chart
- **Container Formats** — MKV, MP4, AVI, WebM etc. with file count and size
- **Resolution Distribution** — 4K, 1080p, 720p, 480p, 576p
- **Health Check** — Detection of videos without subtitles, without artwork, without NFO, and orphaned metadata directories
- **Cleanup Statistics** — Track lifetime bytes freed, items deleted, and last cleanup timestamp

### 📈 Export & History
- **Export as JSON** — Download complete statistics as a JSON file
- **Export as CSV** — Download per-library breakdown as a CSV file
- **Historical Trend** — Statistics are saved as a snapshot on every scan (max. 365 entries) and displayed as a trend graph

### 🗑️ Trash / Recycle Bin
Instead of permanently deleting files, the plugin can move them to a configurable trash folder with timestamped names. Items in the trash are automatically purged after a configurable retention period (default: 30 days). This provides a safety net before permanent deletion.

### 🔗 Arr Stack Integration
Compare your Jellyfin library with Radarr and Sonarr to identify:
- Items present in both systems
- Items in the Arr app but not in Jellyfin (with or without files)
- Items in Jellyfin but missing from the Arr app

### 🌐 Internationalization (i18n)
The dashboard UI supports **7 languages**: English, German, French, Spanish, Portuguese, Chinese, and Turkish. The language can be configured in the plugin settings.

### ⚙️ Configurable Library Filtering
- **Whitelist / Blacklist** — Include or exclude specific libraries from cleanup tasks
- **Orphan Minimum Age** — Protect recently created items from premature deletion (race condition protection for active downloads)
- **Dry-Run by Default** — Configure cleanup tasks to log-only mode by default

### 🔐 Security & Performance
- **5-Minute Cache** — Statistics are cached with `IMemoryCache`; repeated clicks do not trigger a new scan
- **Rate Limiting** — Minimum 30 seconds between scans; HTTP 429 returned on excessive requests
- **Input Validation** — Path traversal protection with null-byte detection and base directory validation
- **Graceful Handling** — `IOException` and `UnauthorizedAccessException` are logged and skipped per directory

### 🔍 Dry Run Mode
All cleanup tasks have a corresponding **Dry Run** variant that logs what *would* be deleted without actually deleting anything. Use these to verify the cleanup behavior before enabling the actual cleanup tasks.

## Scheduled Tasks

| Task | Description | Default Schedule |
|------|-------------|-----------------|
| **Trickplay Folder Cleaner** | Deletes orphaned `.trickplay` folders | Weekly, Sunday 2:00 AM |
| **Trickplay Folder Cleaner (Dry Run)** | Logs orphaned `.trickplay` folders without deleting | No default trigger |
| **Empty Media Folder Cleaner** | Deletes media folders with no video files | Weekly, Sunday 3:00 AM |
| **Empty Media Folder Cleaner (Dry Run)** | Logs empty media folders without deleting | No default trigger |
| **Orphaned Subtitle Cleaner** | Deletes orphaned subtitle files | Weekly, Sunday 4:00 AM |
| **Orphaned Subtitle Cleaner (Dry Run)** | Logs orphaned subtitles without deleting | No default trigger |

All tasks appear under the **Jellyfin Helper** category in the Jellyfin scheduled tasks dashboard.

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/JellyfinHelper/Statistics` | GET | Retrieve statistics (cached for 5 min; use `?forceRefresh=true` to bypass cache) |
| `/JellyfinHelper/Statistics/Export/Json` | GET | Download statistics as a JSON file |
| `/JellyfinHelper/Statistics/Export/Csv` | GET | Download statistics as a CSV file |
| `/JellyfinHelper/Statistics/History` | GET | Retrieve historical snapshots for trend graph |
| `/JellyfinHelper/Translations` | GET | Get UI translations for specified language |
| `/JellyfinHelper/Configuration` | GET/POST | Get or update plugin settings |
| `/JellyfinHelper/Arr/Radarr/Compare` | GET | Compare Jellyfin movies with Radarr |
| `/JellyfinHelper/Arr/Sonarr/Compare` | GET | Compare Jellyfin TV shows with Sonarr |

All endpoints require admin authorization (`RequiresElevation`).

## Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| **Included Libraries** | Whitelist of library names (comma-separated) | Empty (all) |
| **Excluded Libraries** | Blacklist of library names (comma-separated) | Empty (none) |
| **Orphan Minimum Age** | Minimum age (days) before an item is considered orphaned | 0 |
| **Dry-Run by Default** | Cleanup tasks log-only without deleting | Off |
| **Enable Subtitle Cleaner** | Enable the orphaned subtitle cleanup task | On |
| **Use Trash** | Move to trash instead of permanent delete | Off |
| **Trash Folder Path** | Relative or absolute path to the trash folder | `.jellyfin-trash` |
| **Trash Retention** | Days to keep items in trash before purging | 30 |
| **Dashboard Language** | UI language (en, de, fr, es, pt, zh, tr) | en |
| **Radarr URL / API Key** | Radarr connection for library comparison | Empty |
| **Sonarr URL / API Key** | Sonarr connection for library comparison | Empty |

## Supported File Extensions

### Video
`.3g2` `.3gp` `.asf` `.avi` `.divx` `.dvr-ms` `.f4v` `.flv` `.hevc` `.img` `.iso` `.m2ts` `.m2v` `.m4v` `.mk3d` `.mkv` `.mov` `.mp4` `.mpeg` `.mpg` `.mts` `.ogg` `.ogm` `.ogv` `.rec` `.rm` `.rmvb` `.ts` `.vob` `.webm` `.wmv` `.wtv`

### Audio
`.flac` `.mp3` `.ogg` `.opus` `.wav` `.wma` `.m4a` `.aac` `.ape` `.wv` `.dsf` `.dff` `.mka`

### Subtitle
`.srt` `.sub` `.ssa` `.ass` `.vtt` `.idx` `.smi` `.pgs` `.sup`

### Image
`.jpg` `.jpeg` `.png` `.gif` `.bmp` `.webp` `.tbn` `.ico` `.svg`

## Installation

### From Repository (Recommended)

1. In Jellyfin, go to **Dashboard** → **Plugins** → **Repositories**
2. Add this repository URL:
   ```
   https://raw.githubusercontent.com/JellyPlugins/jellyfin-helper/main/manifest.json
   ```
3. Go to **Catalog** and install **Jellyfin Helper**
4. Restart Jellyfin

### Manual Installation

1. Download the latest release from the [Releases](https://github.com/JellyPlugins/jellyfin-helper/releases) page
2. Extract the `.dll` file into your Jellyfin plugins directory (e.g., `/config/plugins/JellyfinHelper/`)
3. Restart Jellyfin

## Usage

1. After installation, go to **Dashboard** → **Scheduled Tasks**
2. Look for tasks under the **Jellyfin Helper** category
3. **Recommended:** Run the **Dry Run** tasks first to review what would be deleted
4. Check the Jellyfin logs to see the results
5. Once satisfied, enable the actual cleanup tasks or run them manually
6. The plugin appears in the **Jellyfin sidebar menu** — click **Jellyfin Helper** to open the dashboard directly
7. Visit the plugin's **Settings** page to view media library statistics, export data, and review trends
8. Optionally configure **Arr Integration** to compare your library with Radarr/Sonarr

## Building from Source

```bash
dotnet build
dotnet test
```

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for a detailed version history.

## Origin

This project is based on the original [jellyfin-trickplay-folder-cleaner](https://github.com/Noir1992/jellyfin-trickplay-folder-cleaner) by [@Noir1992](https://github.com/Noir1992), which was inspired by [this community script](https://github.com/jellyfin/jellyfin/issues/12818#issuecomment-2712783498).

This fork evolved into an independent project with significant additions including empty media folder cleanup, orphaned subtitle cleanup, media library statistics, audio codec analysis, export/history features, trash/recycle bin, Arr stack integration, multi-language dashboard, caching, rate limiting, comprehensive test coverage, CI/CD pipeline with integration tests, and Dependabot/CodeRabbit integration.

## License

This project is licensed under the GNU General Public License v3.0 - see the [LICENSE](LICENSE) file for details.

## Acknowledgements
[@Noir1992](https://github.com/Noir1992) — Original plugin author<br />
[@K-Money](https://github.com/K-Money) — Initial Testing