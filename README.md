# Jellyfin Helper

![Jellyfin Helper Logo](media/logo.png)

A [Jellyfin](https://jellyfin.org/) plugin that provides automated cleanup tasks, media library statistics, health checks, and Arr stack integration — all from a single, multi-tab dashboard.

[![GitHub Release](https://img.shields.io/github/v/release/JellyPlugins/jellyfin-helper?style=flat-square)](https://github.com/JellyPlugins/jellyfin-helper/releases)
[![Tests](https://img.shields.io/badge/tests-comprehensive%20suite-brightgreen?style=flat-square)](Jellyfin.Plugin.JellyfinHelper.Tests/)
[![License](https://img.shields.io/github/license/JellyPlugins/jellyfin-helper?style=flat-square)](LICENSE)

## 🎮 Live Demo

**[Try the interactive demo →](https://jellyplugins.github.io/jellyfin-helper/)**

Explore the full 7-tab dashboard with realistic sample data — no Jellyfin server required.

---

## ✨ Features

| Feature | Description                                                                                                                                                                                 |
|---------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **📊 7-Tab Dashboard** | Overview, Codecs, Health, Trends, Settings, Arr, Logs — all accessible directly from the Jellyfin sidebar as a single plugin page                                                           |
| **🧹 Trickplay Cleaner** | Automatically finds and removes orphaned `.trickplay` folders whose corresponding media file no longer exists. Frees disk space from stale image extraction data                            |
| **📁 Empty Folder Cleaner** | Deletes media folders that lost their video files (e.g. after manual cleanup). Skips empty folders (Radarr/Sonarr placeholders), metadata-only folders, and music libraries                 |
| **🧹 Subtitle Cleaner** | Detects and removes orphaned `.srt`/`.ass`/`.vtt` subtitle files that no longer have a matching video. Uses ISO 639 language-code detection to avoid false positives                        |
| **🔧 Link Repair** | Scans for broken `.strm` files and broken symlinks, then automatically repairs them by locating the renamed or moved target media file in the same directory tree                           |
| **🧹 Seerr Cleanup** | Connects to your Overseerr/Jellyseerr/Seerr instance and removes media requests whose underlying files are no longer available. Keeps your request list clean and in sync with actual media |
| **📈 Statistics & Trends** | Per-library disk usage, video codec, audio codec, resolution, and container format analysis.                                                                                                |
| **📈 Growth Timeline** | Cumulative media growth visualization with daily/weekly/monthly/quarterly/yearly bucketing. Hover any point to see the exact file count and size delta since that date                      |
| **🩺 Health Checks** | Detects videos without subtitles (including embedded streams), missing artwork, missing NFO files, and orphaned metadata directories                                                        |
| **🔗 Arr Integration** | Compare your Jellyfin library with up to 3 Radarr + 3 Sonarr instances to find items only in Arr, only in Jellyfin, or in both                                                              |
| **💾 Backup & Restore** | Export/import the full plugin state (configuration, growth timeline, baseline data, Arr instances) as a validated JSON file with XSS/injection protection                                   |
| **📋 Log Viewer** | Plugin-specific logs with level/source filtering, auto-refresh (10s), and download as `.log` file. Isolated from Jellyfin's main log to reduce noise                                        |
| **🗑️ Trash / Recycle Bin** | Cleanup tasks move files to a timestamped trash folder instead of permanently deleting them. Configurable retention period auto-purges expired items                                        |
| **🌐 7 Languages** | Full UI translations: English, German, French, Spanish, Portuguese, Chinese, Turkish                                                                                                        |
| **🔐 Security** | 5-min statistics cache, 30s rate limiting, path traversal protection, XSS escaping, backup payload validation with size limits and injection detection                                      |
| **🛡️ Unsaved Settings Alert** | Warns before navigating away when the settings form has unsaved changes, preventing accidental configuration loss                                                                           |

All cleanup tasks default to **Dry Run** mode — nothing is deleted until you explicitly activate them.

**Compatibility:** Jellyfin **10.11.0+** · .NET **9.0**

---

## 📦 Installation

### From Repository (Recommended)

1. In Jellyfin, go to **Dashboard** → **Plugins** → **Repositories**
2. Add this repository URL:
   ```
   https://raw.githubusercontent.com/JellyPlugins/jellyfin-helper/main/manifest.json
   ```
3. Go to **Catalog** and install **Jellyfin Helper**
4. Restart Jellyfin

### Manual Installation

1. Download the latest release package from [Releases](https://github.com/JellyPlugins/jellyfin-helper/releases)
2. Extract the package and copy all files into your Jellyfin plugin directory (e.g. `/config/plugins/JellyfinHelper/`)
3. Restart Jellyfin

---

## 🚀 Quick Start

1. Open **Jellyfin Helper** from the sidebar — the last scan loads automatically
2. Go to the **Settings** tab to configure tasks, libraries, trash, and language
3. Review **Dry Run** results in the Jellyfin scheduled tasks log
4. Switch tasks to **Activate** when ready
5. The **Helper Cleanup** scheduled task runs weekly (Sunday 3:00 AM) or trigger it manually

---

## 📖 Documentation

| Resource | Description |
|----------|-------------|
| [CONTRIBUTING.md](CONTRIBUTING.md) | Architecture, design patterns, build system, API reference, configuration, testing |
| [CHANGELOG.md](CHANGELOG.md) | Detailed version history |
| [Live Demo](https://jellyplugins.github.io/jellyfin-helper/) | Interactive dashboard demo |

---

## Origin

Based on [jellyfin-trickplay-folder-cleaner](https://github.com/Noir1992/jellyfin-trickplay-folder-cleaner) by [@Noir1992](https://github.com/Noir1992), inspired by [this community script](https://github.com/jellyfin/jellyfin/issues/12818#issuecomment-2712783498). This fork evolved into an independent project with significant additions.

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).

## Acknowledgements

| Who | Contribution |
|-----|-------------|
| [@Noir1992](https://github.com/Noir1992) | Original plugin author |
| [@K-Money](https://github.com/K-Money) | Initial testing |