#!/usr/bin/env bash
# =============================================================================
# One-command E2E runner for the Jellyfin Helper plugin.
#
#   test/e2e/scripts/run.sh              # full run: build -> up -> setup -> test -> teardown
#   test/e2e/scripts/run.sh --keep       # leave the stack running after tests (for debugging)
#   test/e2e/scripts/run.sh --no-build   # reuse the already-staged plugin (faster iteration)
#   test/e2e/scripts/run.sh --ui         # open the Playwright UI runner instead of headless
#
# Works locally (Windows/Git-Bash, macOS, Linux) and in CI. Requires: docker,
# docker compose, dotnet SDK 10, node/npm. ffmpeg is NOT required on the host -
# media is generated inside the Jellyfin container.
#
# Exit code is the Playwright exit code (0 = all green), so CI can gate on it.
# =============================================================================
set -euo pipefail

# --- locate ourselves ------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$E2E_DIR/../.." && pwd)"
PLUGIN_PROJ="$REPO_ROOT/Jellyfin.Plugin.JellyfinHelper"
RUNTIME="$E2E_DIR/runtime"

# Plugin version is read from the single source of truth (Directory.Build.props)
# so version bumps need no changes here. Falls back to a sane default if absent.
PROPS_FILE="$REPO_ROOT/Directory.Build.props"
PLUGIN_VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROPS_FILE" 2>/dev/null | head -1)"
PLUGIN_VERSION="${PLUGIN_VERSION:-3.0.0.0}"
export PLUGIN_VERSION
export PLUGIN_NAME="Jellyfin Helper"

# The plugin is staged INTO the config volume's plugins dir (folder name must be
# "<Name>_<Version>" for Jellyfin's loader). No separate read-only mount.
PLUGIN_STAGE="$RUNTIME/config/plugins/${PLUGIN_NAME}_${PLUGIN_VERSION}"

# --- flags -----------------------------------------------------------------
KEEP=0; BUILD=1; UI_MODE=0
# In CI we keep the stack up on exit so the workflow can collect container logs;
# CI tears the whole runner down anyway.
[ "${CI:-}" = "true" ] && KEEP=1
for arg in "$@"; do
  case "$arg" in
    --keep)     KEEP=1 ;;
    --no-build) BUILD=0 ;;
    --ui)       UI_MODE=1 ;;
    *) echo "unknown flag: $arg" >&2; exit 2 ;;
  esac
done

# Array form so paths containing spaces (common on Windows/Git-Bash under
# "Program Files") survive without word-splitting surprises.
COMPOSE=(docker compose -f "$E2E_DIR/compose.yml")

log() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }

cleanup() {
  local code=$?
  if [ "$KEEP" -eq 1 ]; then
    log "Leaving stack running (--keep). Tear down with: ${COMPOSE[*]} down -v"
  else
    log "Tearing down stack"
    "${COMPOSE[@]}" down -v --remove-orphans 2>/dev/null || true
  fi
  exit $code
}
trap cleanup EXIT

log "Plugin: ${PLUGIN_NAME} v${PLUGIN_VERSION} (from Directory.Build.props)"

# --- 1. build the plugin ---------------------------------------------------
if [ "$BUILD" -eq 1 ]; then
  log "Building plugin (Release)"
  dotnet publish "$PLUGIN_PROJ/Jellyfin.Plugin.JellyfinHelper.csproj" \
    -c Release -o "$RUNTIME/publish" --nologo
else
  log "Skipping build (--no-build)"
  [ -f "$RUNTIME/publish/Jellyfin.Plugin.JellyfinHelper.dll" ] || {
    echo "No prior build found in $RUNTIME/publish. Run without --no-build first." >&2; exit 1; }
fi

# --- 1b. tear down any pre-existing stack BEFORE wiping host state ----------
# A prior --keep/CI run (or a crash) can leave containers running. `up -d --build`
# below would REUSE them, so the host-side `rm -rf runtime/media` would race a
# container that still holds /media open - on Windows Docker Desktop bind mounts
# this desyncs the container's view from the host and makes gen-media see phantom
# "File exists" symlinks. Removing the stack first releases the mounts before the
# wipe and guarantees `up` creates fresh containers against fresh bind mounts.
log "Removing any pre-existing stack"
"${COMPOSE[@]}" down -v --remove-orphans 2>/dev/null || true

# --- 2. fresh runtime dirs (wipe BEFORE staging the plugin into config) -----
log "Resetting runtime state"
# Guard against an unset/empty RUNTIME expanding to rm -rf /config etc. (SC2115).
rm -rf "${RUNTIME:?}/config" "${RUNTIME:?}/cache" "${RUNTIME:?}/media"
mkdir -p "$RUNTIME/config/plugins" "$RUNTIME/cache" "$RUNTIME/media/.gen"
cp "$E2E_DIR/fixtures/gen-media.sh" "$RUNTIME/media/.gen/gen-media.sh"

# The container may run as any UID; make config writable so Jellyfin can create
# its plugin-dir markers and databases regardless of user mapping. On Linux the
# container already runs as the invoking user (JELLYFIN_UID/GID exported below),
# so a group/user-writable bit is enough - no world-writable state dirs on CI.
# Elsewhere (Docker Desktop, unknown UID mapping) fall back to the blanket 777.
if [ "$(uname -s)" = "Linux" ]; then
  chmod -R u+rwX,g+rwX "$RUNTIME/config" "$RUNTIME/cache" 2>/dev/null || true
else
  chmod -R 777 "$RUNTIME/config" "$RUNTIME/cache" 2>/dev/null || true
fi

# --- 3. stage the plugin into the config volume ----------------------------
# Copy EVERY dll from the publish output except the ones the Jellyfin host
# already provides at runtime. Hand-picking individual dlls silently drops
# transitive dependencies (System.IO.Abstractions pulls Testably.* /
# TestableIO.*), which makes the loader throw FileNotFoundException during
# service registration and Jellyfin disables the whole plugin - every
# JellyfinHelper/ route then 404s. Bundling the plugin's own dependency
# closure is what the release manifest does; the E2E staging must match it.
log "Staging plugin DLLs"
mkdir -p "$PLUGIN_STAGE"
# Host-provided assemblies live in the Jellyfin image already; bundling our
# copies risks assembly-identity conflicts. Everything else in publish/ is a
# plugin-private dependency and must ship. The plugin's own assembly starts
# with "Jellyfin.Plugin." and must NEVER be excluded by the host filter.
host_provided='^(Jellyfin\.(Controller|Model|Data|Api|Common|Networking|Database|Server|Extensions|Naming|MediaEncoding|Drawing|Providers|LiveTv|Dlna|Api\.)|MediaBrowser\.|Microsoft\.|System\.(Text|Threading|Collections|Linq|Runtime|Net|Memory|Buffers|Diagnostics|Reflection|Security|Globalization|ComponentModel|Private)|netstandard)'
staged=0
for dll in "$RUNTIME"/publish/*.dll; do
  base="$(basename "$dll")"
  # Never drop our own plugin assembly, whatever the host filter says.
  if [[ "$base" != Jellyfin.Plugin.JellyfinHelper.dll && "$base" =~ $host_provided ]]; then
    continue
  fi
  cp "$dll" "$PLUGIN_STAGE/"
  staged=$((staged + 1))
done
echo "[stage] copied $staged plugin dll(s) to $PLUGIN_STAGE"
[ "$staged" -ge 1 ] || { echo "No plugin dlls staged - publish output missing?" >&2; exit 1; }
cp "$RUNTIME/publish/logo.png" "$PLUGIN_STAGE/" 2>/dev/null || true
# meta.json so Jellyfin shows a clean plugin entry (name/version/guid).
# Invoked via `bash` so it works regardless of the file's execute bit.
bash "$SCRIPT_DIR/write-meta.sh" "$PLUGIN_STAGE" "$PLUGIN_VERSION"

# Run the container as the invoking user where possible (Linux/CI); on other
# hosts the image's default user + the 777 above keep /config writable.
if [ "$(uname -s)" = "Linux" ]; then
  # Declare then export separately so the command substitution's exit status isn't
  # masked by the export builtin (shellcheck SC2155).
  JELLYFIN_UID="$(id -u)"; export JELLYFIN_UID
  JELLYFIN_GID="$(id -g)"; export JELLYFIN_GID
fi

# --- 4. bring up the stack --------------------------------------------------
log "Starting stack (Jellyfin 12.0-rc4 + mock Arr/Seerr)"
"${COMPOSE[@]}" up -d --build

log "Waiting for Jellyfin to become healthy"
# Poll the container's health via `docker inspect` - more portable than relying
# on `compose ps --format {{.Health}}`, which varies across Compose versions.
# The compose file defines the actual healthcheck; we just read its result.
JELLYFIN_CONTAINER="jfh-e2e-jellyfin"
healthy=0
for _ in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$JELLYFIN_CONTAINER" 2>/dev/null || echo starting)"
  if [ "$status" = "healthy" ]; then healthy=1; break; fi
  # Bail early if the container died outright.
  running="$(docker inspect -f '{{.State.Running}}' "$JELLYFIN_CONTAINER" 2>/dev/null || echo false)"
  if [ "$running" = "false" ] && [ "$status" != "starting" ]; then break; fi
  sleep 3
done
if [ "$healthy" -ne 1 ]; then
  echo "Jellyfin did not become healthy (last status: ${status:-unknown})" >&2
  "${COMPOSE[@]}" logs jellyfin || true
  exit 1
fi
echo "Jellyfin healthy."

# --- 5. generate the fake media library (inside the container) -------------
# On Git-Bash (Windows) MSYS rewrites Unix-looking arguments into host paths.
# We must keep that conversion ON for the host-side compose file path
# (-f "$E2E_DIR/compose.yml") but OFF for the container-side "/media" argument,
# so scope the exclusion to just that prefix. A no-op on Linux/macOS CI. Verify
# files actually landed - an empty library would make every stats/scan test
# pass vacuously, which is worse than a hard failure here.
log "Generating fake media library"
MSYS2_ARG_CONV_EXCL='/media' \
  "${COMPOSE[@]}" exec -T jellyfin bash /media/.gen/gen-media.sh /media
media_count="$("${COMPOSE[@]}" exec -T jellyfin sh -c 'find /media -name "*.mkv" -o -name "*.mp4" 2>/dev/null | wc -l' | tr -d '[:space:]')"
if [ "${media_count:-0}" -lt 1 ]; then
  echo "Media generation produced no video files - aborting before tests run on an empty library." >&2
  exit 1
fi
echo "Media generated (${media_count} video files)."

# --- 6. install Playwright deps (first run only) ---------------------------
log "Installing test dependencies"
cd "$E2E_DIR"
[ -d node_modules ] || npm ci --no-audit --no-fund || npm install --no-audit --no-fund
# Try the full "--with-deps" install first (needs sudo/apt for OS libs). If that
# fails - common on hosts without root/apt - log why and fall back to a
# browser-only install so a real network/permission error isn't hidden behind
# an opaque browser-launch failure later.
npx playwright install --with-deps chromium >/dev/null || {
  echo "[run] '--with-deps' install failed (likely no sudo/apt); retrying browser-only." >&2
  npx playwright install chromium
}

# --- 7. run the tests -------------------------------------------------------
# Setup (wizard + scan) runs as a Playwright global-setup, so the tests get a
# ready server + admin token via storage state / env.
log "Running E2E tests"
if [ "$UI_MODE" -eq 1 ]; then
  npx playwright test --ui
else
  npx playwright test
fi
