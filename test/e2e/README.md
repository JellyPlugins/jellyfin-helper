# Jellyfin Helper - End-to-End Test Harness

Full end-to-end tests that run the plugin inside a **real Jellyfin 12.0
server** in Docker, drive it exactly the way a human would (settings, tasks,
backup/restore, every dashboard tab), and assert it all works, including
hardening / edge-case behaviour.

This complements the ~4200 xUnit unit tests: the unit tests verify logic in
isolation; this harness verifies the plugin **loads and behaves in a live
server**.

## What it covers

| Area | Examples |
|---|---|
| Plugin load | Plugin appears in `/Plugins` as **Active** (not Malfunctioned); server boots |
| Every API endpoint | All 17 controllers under `JellyfinHelper/` respond (no 404/500) |
| All task modes | `HelperCleanup` run with each stage set to `DryRun` / `Activate` / `Deactivate`; assert the right side effects (delete vs report vs skip; recs-Deactivate purges playlists) |
| Settings persistence | Flip each setting → save → reload → assert it stuck and took effect |
| Backup round-trip | Export → tamper with JSON → import → assert validation & restore |
| Trends integrity | Assert timeline/insights contain no garbage (negative sizes, false zeroes) |
| Arr / Seerr | Against **mock servers**: connection test, Compare, reachability indicator, masked-key Test Connection resolves the stored key after reload |
| Discovery exclusion | A title Seerr reports as already available (`mediaInfo.status`) is dropped from generated recommendations even when no Arr instance or library entry tracks it |
| UI: all 8 tabs | Overview, Codecs, Health, Trends, Settings, Arr, Recommendations, Logs |
| UI interactions | Codec collapsible trees expand/collapse, Logs arrive + download, unsaved-changes dialog appears when leaving dirty settings |
| Hardening | Empty library, broken backup XML/JSON, invalid Arr URLs, out-of-range numbers, Unicode paths, concurrent task runs: must degrade cleanly, never crash |

## Architecture

```text
test/e2e/
├── compose.yml            # Jellyfin 12.0 + mock-arr + mock-seerr
├── playwright.config.ts   # two projects: "api" (HTTP) and "ui" (browser)
├── package.json
├── scripts/
│   ├── run.sh             # ONE COMMAND: build → up → setup → test → teardown
│   └── write-meta.sh      # stages plugin meta.json
├── fixtures/
│   └── gen-media.sh       # generates tiny real clips (runs inside container; uses its ffmpeg)
├── mocks/                 # Node http mock servers (no deps)
│   ├── Dockerfile
│   ├── arr-server.js      # fake Radarr + Sonarr
│   └── seerr-server.js    # fake Jellyseerr/Overseerr
├── setup/
│   ├── global-setup.ts    # completes first-run wizard, gets admin token, builds library, scans
│   └── api-client.ts      # shared Jellyfin/plugin API helpers + task runner
├── tests/
│   ├── *.api.spec.ts      # HTTP-level assertions
│   └── *.ui.spec.ts       # browser assertions
└── runtime/               # generated, git-ignored (config, cache, media, staged plugin)
```

## Running it

```bash
# Full run (build plugin, start stack, set up, test, tear down):
test/e2e/scripts/run.sh

# Faster iteration (reuse last build):
test/e2e/scripts/run.sh --no-build

# Leave the stack up afterwards to poke around (http://localhost:8096):
test/e2e/scripts/run.sh --keep

# Interactive Playwright UI runner:
test/e2e/scripts/run.sh --ui
```

**Requirements:** Docker + Docker Compose, .NET SDK 10, Node 20+. **No host
ffmpeg needed.** Media is generated inside the Jellyfin container.

## Why the image tag is pinned

The plugin targets ABI `12.0.0.0` (built against `Jellyfin.Controller
12.0.0`). Jellyfin 12 is currently **release-candidate only**: the
stable `latest` / `10.x` line would refuse to load the plugin. `compose.yml`
pins `jellyfin/jellyfin:12.0`. When 12.0 goes stable, bump that tag.

## CI

Runs on every PR via `.github/workflows/e2e.yml`. On failure it uploads the
Playwright HTML report, traces, and screenshots as artifacts, and prints the
Jellyfin/mock container logs into the CI job log.
