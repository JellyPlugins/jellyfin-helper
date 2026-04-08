# Jellyfin Helper

A [Jellyfin](https://jellyfin.org/) plugin that provides automated cleanup tasks and media library statistics for your media library.

## Features

### 🧹 Trickplay Folder Cleaner
Automatically deletes orphaned `.trickplay` folders that no longer have a corresponding media file. This typically happens when media files are renamed, moved, or deleted while the trickplay data remains behind.

### 📁 Empty Media Folder Cleaner
Automatically deletes top-level media folders whose entire directory tree contains files but absolutely **no video files**. This targets the common scenario where a movie or episode is deleted but the surrounding folder with metadata (`.nfo`), artwork (`.jpg`), subtitles (`.srt`), etc. remains as an orphaned folder.

**Important behaviors:**
- **Completely empty folders are skipped** — they are often pre-created by tools like Radarr/Sonarr for upcoming/"wanted" media
- **TV show folders are checked as a whole** — if at least one video exists anywhere in the tree (even in a deeply nested subdirectory), the entire show folder is kept untouched
- **`.trickplay` folders are skipped** — they are handled by the Trickplay Folder Cleaner task

### 📊 Media Library Statistics
A settings page that provides an overview of your media library disk usage, broken down by category:
- **Video Data in Movies** / **Video Data in Series** / **Audio Data in Music**
- **Trickplay Data**, **Subtitle Data**, **Image Data**, **NFO/Metadata Data**
- Per-library breakdown with file counts

### 🔍 Dry Run Mode
Both cleanup tasks have a corresponding **Dry Run** variant that logs what *would* be deleted without actually deleting anything. Use these to verify the cleanup behavior before enabling the actual cleanup tasks.

## Scheduled Tasks

| Task | Description | Default Schedule |
|------|-------------|-----------------|
| **Trickplay Folder Cleaner** | Deletes orphaned `.trickplay` folders | Weekly, Sunday 2:00 AM |
| **Trickplay Folder Cleaner (Dry Run)** | Logs orphaned `.trickplay` folders without deleting | No default trigger |
| **Empty Media Folder Cleaner** | Deletes media folders with no video files | Weekly, Sunday 3:00 AM |
| **Empty Media Folder Cleaner (Dry Run)** | Logs empty media folders without deleting | No default trigger |

All tasks appear under the **Jellyfin Helper** category in the Jellyfin scheduled tasks dashboard.

## Supported Video Extensions

The plugin recognizes the following video file extensions:

`.3g2` `.3gp` `.asf` `.avi` `.divx` `.dvr-ms` `.f4v` `.flv` `.hevc` `.img` `.iso` `.m2ts` `.m2v` `.m4v` `.mk3d` `.mkv` `.mov` `.mp4` `.mpeg` `.mpg` `.mts` `.ogg` `.ogm` `.ogv` `.rec` `.rm` `.rmvb` `.ts` `.vob` `.webm` `.wmv` `.wtv`

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
6. Visit the plugin's **Settings** page to view media library statistics

## Building from Source

```bash
dotnet build
dotnet test
```

## Origin

This project is based on the original [jellyfin-trickplay-folder-cleaner](https://github.com/Noir1992/jellyfin-trickplay-folder-cleaner) by [@Noir1992](https://github.com/Noir1992), which was inspired by [this community script](https://github.com/jellyfin/jellyfin/issues/12818#issuecomment-2712783498).

This fork evolved into an independent project with significant additions including empty media folder cleanup, media library statistics, comprehensive test coverage, performance optimizations, CI/CD pipeline with integration tests, and Dependabot/CodeRabbit integration.

## License

This project is licensed under the GNU General Public License v3.0 - see the [LICENSE](LICENSE) file for details.

## Acknowledgements
[@Noir1992](https://github.com/Noir1992) — Original plugin author<br />
[@K-Money](https://github.com/K-Money) — Initial Testing