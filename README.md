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
[![Tests](https://img.shields.io/badge/unit%20tests-6558-2ea043?style=flat-square&logo=checkmarx&logoColor=white&labelColor=2d333b)](Jellyfin.Plugin.JellyfinHelper.Tests/)<br>
[![E2E](https://img.shields.io/badge/e2e%20tests-361-2ea043?style=flat-square&logo=docker&logoColor=white&labelColor=2d333b)](test/e2e/)

</td>
<td style="vertical-align: top; text-align: center;">

**Stack**

[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.2+-00A4DC?style=flat-square&logo=jellyfin&logoColor=white&labelColor=2d333b)](https://jellyfin.org/)<br>
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

| Feature                    | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **8-Tab Dashboard**        | Overview, Codecs, Health, Trends, Discover, Settings, Arr, Logs, all accessible directly from the Jellyfin sidebar as a single plugin page. Full UI in 8 languages (English, German, French, Spanish, Portuguese, Chinese, Turkish, Swedish)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Library Analysis**       | Read-only insight into what is actually in your library, from Jellyfin's MediaStream metadata: <br>• **Codecs:** video/audio codec, resolution, dynamic range (HDR10/HDR10+/Dolby Vision/HLG/SDR), and container breakdowns <br>• **Library Explorer:** combine resolution, codecs, bitrate (7 tiers), languages, watched-by-user and formats into one search, scoped to chosen libraries, with include/exclude filters and drill-down to the exact files <br>• **Per-library storage:** disk usage split by type, eBook (Book) libraries included <br>• **Growth Timeline:** cumulative growth stored losslessly at daily resolution; scroll/pinch to zoom, drag to pan, scale adapts from day to year, hover for exact count and size delta <br>• **Library Insights:** top-10 largest directories and items added/changed in the last 30 days, per library (15-min cache) <br>• **Health checks:** videos missing subtitles (including embedded streams), artwork, or NFO files, plus orphaned metadata directories <br>• **User activity:** per-item and per-user play count, completion percentage, favorites, and genre distribution                                                                                        |
| **Maintenance & Cleanup**  | Five safe, independently-togglable cleaners (each **Dry Run** by default; files move to a timestamped trash with configurable auto-purge instead of being deleted): <br>• **Trickplay:** removes orphaned `.trickplay` folders whose media is gone <br>• **Empty folders:** deletes now-empty media folders (skips Radarr/Sonarr placeholders, metadata-only folders, music) <br>• **Subtitles:** removes orphaned `.srt`/`.ass`/`.vtt` with no matching video (ISO 639 detection avoids false positives) <br>• **Link repair:** fixes broken `.strm` files and symlinks by relocating the renamed target <br>• **Seerr cleanup:** removes Overseerr/Jellyseerr/Seerr requests whose media is no longer available                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Discovery & Recommendations** | Per-user suggestions from a pure-C# ensemble (**heuristic + learned + neural MLP**, 38 features over genre/actor/director/studio affinity; age-appropriate only for restricted child profiles; incremental training only when TaskMode=Activate). Three surfaces: <br>• **Playlists:** optional sync of results to native Jellyfin playlists <br>• **Seerr Discovery:** not-yet-in-library TMDb titles with one-click requests, parental enforcement, and exclusion of anything already in your library or Radarr/Sonarr <br>• **Trakt Discovery:** personal + global-trending sub-tabs, sourced read-only through the [official Jellyfin Trakt plugin](https://github.com/jellyfin/jellyfin-plugin-trakt) (no second app; shown only when that plugin is installed, the user has linked it there, and **Enable Trakt discovery** is on in Settings) <br>Library exclusion is deliberately whole-library (ignores the *Excluded Libraries* setting), so Discovery never offers titles you already own. Shown on the home screen as a Custom Tab with the [Custom Tabs](https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs) and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugins |
| **External Integrations**  | Connect the plugin to your wider stack: <br>• **Arr:** compare your Jellyfin library with up to 3 Radarr + 3 Sonarr instances to find items only in Arr, only in Jellyfin, or in both <br>• **Seerr:** Overseerr/Jellyseerr/Seerr for request cleanup and Discovery requests <br>• **Trakt:** per-user recommendations and trending via the official [Jellyfin Trakt plugin](https://github.com/jellyfin/jellyfin-plugin-trakt) (install it, link your Trakt account there, then enable **Trakt discovery** in Helper Settings)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| **Operations & Safety**    | Running the plugin with confidence: <br>• **Backup & restore:** export/import the full plugin state (configuration, growth timeline, baseline, Arr instances) as a validated JSON file; secrets are encrypted at rest and stay portable across hosts <br>• **Log viewer:** plugin-only logs with level/source filtering, 10s auto-refresh, and `.log` download, isolated from Jellyfin's main log <br>• **Unsaved-changes guard:** warns before navigating away from a dirty settings form (Discard / Save & Continue / Cancel) <br>• **Security hardening:** Data Protection for secrets at rest, statistics cache, per-user rate limiting, path-traversal protection, XSS escaping, backup payload validation with injection detection                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |

All tasks default to **Dry Run** mode. Nothing is deleted until you explicitly activate them.

**Compatibility:** Jellyfin **12.2+** · .NET **10.0**

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