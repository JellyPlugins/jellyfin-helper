# Jellyfin Helper

![Jellyfin Helper Logo](media/logo.png)

A [Jellyfin](https://jellyfin.org/) plugin that provides automated cleanup tasks, media library statistics, health checks, and Arr stack integration — all from a single, multi-tab dashboard.

[![GitHub Release](https://img.shields.io/github/v/release/JellyPlugins/jellyfin-helper?style=flat-square)](https://github.com/JellyPlugins/jellyfin-helper/releases)
[![Tests](https://img.shields.io/badge/tests-737%20passing-brightgreen?style=flat-square)](Jellyfin.Plugin.JellyfinHelper.Tests/)
[![License](https://img.shields.io/github/license/JellyPlugins/jellyfin-helper?style=flat-square)](LICENSE)

## 🎮 Live Demo

**[Try the interactive demo →](https://jellyplugins.github.io/jellyfin-helper/)**

Explore the full 7-tab dashboard with realistic sample data — no Jellyfin server required.

---

## ✨ Features

| Feature | Description |
|---------|-------------|
| **📊 7-Tab Dashboard** | Overview, Codecs, Health, Trends, Settings, Arr, Logs — directly in the Jellyfin sidebar |
| **🧹 Trickplay Cleaner** | Removes orphaned `.trickplay` folders with no corresponding media file |
| **📁 Empty Folder Cleaner** | Deletes media folders containing no video files (skips empty & metadata-only folders) |
| **🧹 Subtitle Cleaner** | Removes orphaned subtitle files with smart language-code detection |
| **🔧 STRM Repair** | Fixes broken `.strm` files by locating renamed/moved media |
| **📈 Statistics & Trends** | Per-library disk usage, codec/resolution/container analysis, historical growth graphs |
| **🩺 Health Checks** | Detects missing subtitles, artwork, NFO, and orphaned metadata directories |
| **🔗 Arr Integration** | Compare Jellyfin with up to 3 Radarr + 3 Sonarr instances |
| **📋 Log Viewer** | Plugin-specific logs with level/source filtering, auto-refresh, and download |
| **🗑️ Trash / Recycle Bin** | Move-to-trash with configurable retention instead of permanent deletion |
| **🌐 7 Languages** | English, German, French, Spanish, Portuguese, Chinese, Turkish |
| **🔐 Security** | 5-min cache, rate limiting, path traversal protection, XSS escaping |

All cleanup tasks default to **Dry Run** mode — nothing is deleted until you explicitly activate them.

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

1. Download the latest `.dll` from [Releases](https://github.com/JellyPlugins/jellyfin-helper/releases)
2. Place it in your Jellyfin plugins directory (e.g. `/config/plugins/JellyfinHelper/`)
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
| [CONTRIBUTING.md](CONTRIBUTING.md) | Architecture, build system, API reference, configuration details, testing |
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