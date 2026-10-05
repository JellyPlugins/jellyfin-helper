# Jellyfin Helper

![Jellyfin Helper Logo](media/logo.png)

A [Jellyfin](https://jellyfin.org/) plugin that provides automated cleanup tasks, media library statistics, ML-powered recommendations with Seerr and Trakt discovery, user activity insights, health checks, and Arr stack integration, all from a single, multi-tab dashboard.

> **AI disclosure:** This project is AI-assisted but not vibe-coded. AI is used as a partner for post-change reviews, discussing specific problems, quick analysis, suggestions, and spell checking, never as a substitute for the logic, regression testing, code coverage, and static checks that gate every change. The i18n and parts of the UI are the main AI-generated portions. It was built with a great deal of effort and care, and we deliberately do not trust AI blindly.

<table>
<tr>
<td style="vertical-align: top; text-align: center;">

**Project**

[![Live Demo](https://img.shields.io/badge/demo-live%20preview-ff69b4?style=flat-square&logo=githubpages&logoColor=white&labelColor=2d333b)](https://jellyplugins.github.io/jellyfin-helper/)<br>
[![Languages](https://img.shields.io/badge/i18n-8%20languages-1f8feb?style=flat-square&logo=googletranslate&logoColor=white&labelColor=2d333b)](Jellyfin.Plugin.JellyfinHelper/i18n/)<br>
[![GitHub Release](https://img.shields.io/github/v/release/JellyPlugins/jellyfin-helper?style=flat-square&logo=github&logoColor=white&labelColor=2d333b)](https://github.com/JellyPlugins/jellyfin-helper/releases)

</td>
<td style="vertical-align: top; text-align: center;">

**Quality**

[![Quality gate](https://img.shields.io/sonar/quality_gate/JellyPlugins_jellyfin-helper?server=https%3A%2F%2Fsonarcloud.io&style=flat-square&logo=sonarcloud&logoColor=white&labelColor=2d333b)](https://sonarcloud.io/summary/new_code?id=JellyPlugins_jellyfin-helper)<br>
[![codecov](https://img.shields.io/codecov/c/github/JellyPlugins/jellyfin-helper?style=flat-square&logo=codecov&logoColor=white&labelColor=2d333b)](https://codecov.io/gh/JellyPlugins/jellyfin-helper)<br>
[![Tests](https://img.shields.io/badge/unit%20tests-6332-2ea043?style=flat-square&logo=checkmarx&logoColor=white&labelColor=2d333b)](Jellyfin.Plugin.JellyfinHelper.Tests/)<br>
[![E2E](https://img.shields.io/badge/e2e%20tests-357-2ea043?style=flat-square&logo=docker&logoColor=white&labelColor=2d333b)](test/e2e/)

</td>
<td style="vertical-align: top; text-align: center;">

**Stack**

[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.1+-00A4DC?style=flat-square&logo=jellyfin&logoColor=white&labelColor=2d333b)](https://jellyfin.org/)<br>
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white&labelColor=2d333b)](https://dotnet.microsoft.com/)<br>
[![License](https://img.shields.io/github/license/JellyPlugins/jellyfin-helper?style=flat-square&logo=gnu&logoColor=white&labelColor=2d333b)](LICENSE)

</td>
</tr>
</table>

[![Ko-Fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/jellyfinhelper)

## Live Demo

**[Try the interactive demo →](https://jellyplugins.github.io/jellyfin-helper/)**

Explore the full 8-tab dashboard with realistic sample data. No Jellyfin server required.

---

## Features

| Feature                    | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|----------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **8-Tab Dashboard**        | Overview, Codecs, Health, Trends, Discover, Settings, Arr, Logs, all accessible directly from the Jellyfin sidebar as a single plugin page                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| **Cleanup Tasks**          | Five safe, independently-togglable cleaners: orphaned `.trickplay` folders; now-empty media folders (skips Radarr/Sonarr placeholders, metadata-only folders, music); orphaned `.srt`/`.ass`/`.vtt` subtitles (ISO 639 language detection to avoid false positives); broken `.strm` files and symlinks (auto-repaired by relocating the renamed target); and Overseerr/Jellyseerr/Seerr requests whose media is no longer available. Each runs **Dry Run** by default and can move files to the trash instead of deleting                                                        |
| **Discovery & Recommendations** | Per-user suggestions scored by a pure-C# ensemble (heuristic + learned + neural MLP blend over 38 features from genre/actor/director/studio affinity; children with restricted profiles see only age-appropriate titles; incremental training only when TaskMode=Activate). Results drive three surfaces: optional sync to native Jellyfin **playlists**; **Seerr Discovery** of not-yet-in-library TMDb titles with one-click request submission, parental enforcement, and exclusion of anything already in your library or Radarr/Sonarr; and optional **Trakt Discovery** (personal + global trending sub-tabs, each user self-links via a one-time device code against one admin-registered OAuth app, tokens encrypted at rest). The library exclusion is deliberately whole-library and ignores the *Excluded Libraries* setting, so Discovery never offers titles you already own. Shown on the Jellyfin home screen as a Custom Tab with the [Custom Tabs](https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs) and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugins |
| **Statistics & Trends**    | Per-library disk usage, video codec, audio codec, resolution, dynamic range, and container format analysis, extracted from Jellyfin MediaStream metadata. eBook (Book) libraries are included in the storage overview and totals                                                                                                                                                                                                                                                                                                                                             |
| **Growth Timeline**        | Cumulative media growth stored losslessly at daily resolution. Scroll or pinch to zoom, drag or swipe to pan; the scale adapts between day/week/month/year and the size axis rescales to the visible window. Hover any point to see the exact file count and size delta since that date                                                                                                                                                                                                                                                                                       |
| **Library Insights**       | Top-10 largest media directories and recently added/changed items (last 30 days) per library, with per-library size breakdown. Displayed in the Trends tab with 15-min in-memory cache                                                                                                                                                                                                                                                                                                                                                                                        |
| **User Activity**          | Per-item and per-user watch tracking with play count, completion percentage, favorites detection, and genre distribution, shown in the Discover tab                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Health Checks**          | Detects videos without subtitles (including embedded streams), missing artwork, missing NFO files, and orphaned metadata directories                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Arr Integration**        | Compare your Jellyfin library with up to 3 Radarr + 3 Sonarr instances to find items only in Arr, only in Jellyfin, or in both                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| **Trash / Recycle Bin**    | Cleanup tasks move files to a timestamped trash folder instead of permanently deleting them. Configurable retention period auto-purges expired items                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Backup & Restore**       | Export/import the full plugin state (configuration, growth timeline, baseline data, Arr instances) as a validated JSON file with XSS/injection protection. Secrets are encrypted at rest and stay portable across hosts                                                                                                                                                                                                                                                                                                                                                       |
| **Log Viewer**             | Plugin-specific logs with level/source filtering, auto-refresh (10s), and download as `.log` file. Isolated from Jellyfin's main log to reduce noise                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Security**               | Secrets encrypted at rest (Data Protection), statistics cache, per-user rate limiting, path-traversal protection, XSS escaping, backup payload validation with size limits and injection detection, and parental-rating enforcement in recommendations                                                                                                                                                                                                                                                                                                                       |
| **8 Languages**            | Full UI translations: English, German, French, Spanish, Portuguese, Chinese, Turkish, Swedish                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Unsaved Settings Alert** | Warns before navigating away when the settings form has unsaved changes (dirty-tracking via snapshot comparison), with Discard / Save & Continue / Cancel options                                                                                                                                                                                                                                                                                                                                                                                                             |

All tasks default to **Dry Run** mode. Nothing is deleted until you explicitly activate them.

**Compatibility:** Jellyfin **12.1+** · .NET **10.0**

> **Using Jellyfin 10.x?** Stay on plugin version **v2.1.0.6**, which remains available in the repository. Version 3.x targets Jellyfin 12.x and will not install on older servers.

---

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

1. Download the latest release package from [Releases](https://github.com/JellyPlugins/jellyfin-helper/releases)
2. Extract the package and copy all files into your Jellyfin plugin directory (e.g. `/config/plugins/JellyfinHelper/`)
3. Restart Jellyfin

---

## Quick Start

1. Open **Jellyfin Helper** from the sidebar. The last scan loads automatically
2. Go to the **Settings** tab to configure tasks, libraries, trash, and language
3. Review **Dry Run** results in the Jellyfin scheduled tasks log
4. Switch tasks to **Activate** when ready
5. The **Helper Cleanup** scheduled task runs weekly (Sunday 3:00 AM) or trigger it manually

---

## Documentation

| Resource                                                     | Description                                                                        |
|--------------------------------------------------------------|------------------------------------------------------------------------------------|
| [CONTRIBUTING.md](CONTRIBUTING.md)                           | Architecture, design patterns, build system, API reference, configuration, testing |
| [CHANGELOG.md](CHANGELOG.md)                                 | Detailed version history                                                           |
| [Live Demo](https://jellyplugins.github.io/jellyfin-helper/) | Interactive dashboard demo                                                         |

---

## Origin

Based on [jellyfin-trickplay-folder-cleaner](https://github.com/Noir1992/jellyfin-trickplay-folder-cleaner) by [@Noir1992](https://github.com/Noir1992), which was inspired by [this community script](https://github.com/jellyfin/jellyfin/issues/12818#issuecomment-2712783498) by [@S2ciOnur](https://github.com/S2ciOnur). 
This fork evolved into an independent project with significant additions.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).

## Acknowledgements

| Who                                          | Contribution                                                                                                                              |
|----------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|
| [@Noir1992](https://github.com/Noir1992)     | Original plugin author & Contributor. [jellyfin-trickplay-folder-cleaner](https://github.com/Noir1992/jellyfin-trickplay-folder-cleaner) |
| [@n00bcodr](https://github.com/n00bcodr)     | Inspiration for plugin features. [Jellyfin-Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced)                                      |