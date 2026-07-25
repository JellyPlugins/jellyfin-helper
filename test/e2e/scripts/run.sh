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
# docker compose, dotnet SDK 10, node/npm. ffmpeg is NOT required on the host —
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

COMPOSE="docker compose -f $E2E_DIR/compose.yml"

log() { printf '\n\033[1;36m=== %s ===\033[0m\n' "$*"; }

cleanup() {
  local code=$?
  if [ "$KEEP" -eq 1 ]; then
    log "Leaving stack running (--keep). Tear down with: $COMPOSE down -v"
  else
    log "Tearing down stack"
    $COMPOSE down -v --remove-orphans 2>/dev/null || true
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

# --- 2. fresh runtime dirs (wipe BEFORE staging the plugin into config) -----
log "Resetting runtime state"
rm -rf "$RUNTIME/config" "$RUNTIME/cache" "$RUNTIME/media"
mkdir -p "$RUNTIME/config/plugins" "$RUNTIME/cache" "$RUNTIME/media/.gen"
cp "$E2E_DIR/fixtures/gen-media.sh" "$RUNTIME/media/.gen/gen-media.sh"

# The container may run as any UID; make config writable so Jellyfin can create
# its plugin-dir markers and databases regardless of user mapping.
chmod -R 777 "$RUNTIME/config" "$RUNTIME/cache" 2>/dev/null || true

# --- 3. stage the plugin into the config volume ----------------------------
log "Staging plugin DLLs"
mkdir -p "$PLUGIN_STAGE"
cp "$RUNTIME/publish/Jellyfin.Plugin.JellyfinHelper.dll" "$PLUGIN_STAGE/"
cp "$RUNTIME/publish/System.IO.Abstractions.dll" "$PLUGIN_STAGE/" 2>/dev/null || true
cp "$RUNTIME/publish/logo.png" "$PLUGIN_STAGE/" 2>/dev/null || true
# meta.json so Jellyfin shows a clean plugin entry (name/version/guid).
# Invoked via `bash` so it works regardless of the file's execute bit.
bash "$SCRIPT_DIR/write-meta.sh" "$PLUGIN_STAGE" "$PLUGIN_VERSION"

# Run the container as the invoking user where possible (Linux/CI); on other
# hosts the image's default user + the 777 above keep /config writable.
if [ "$(uname -s)" = "Linux" ]; then
  export JELLYFIN_UID="$(id -u)"
  export JELLYFIN_GID="$(id -g)"
fi

# --- 3. bring up the stack --------------------------------------------------
log "Starting stack (Jellyfin 12.0-rc3 + mock Arr/Seerr)"
$COMPOSE up -d --build

log "Waiting for Jellyfin to become healthy"
# compose healthcheck drives readiness; poll the container health state.
for i in $(seq 1 60); do
  status="$($COMPOSE ps jellyfin --format '{{.Health}}' 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  sleep 3
  [ "$i" -eq 60 ] && { echo "Jellyfin did not become healthy" >&2; $COMPOSE logs jellyfin; exit 1; }
done
echo "Jellyfin healthy."

# --- 4. generate the fake media library (inside the container) -------------
log "Generating fake media library"
$COMPOSE exec -T jellyfin bash /media/.gen/gen-media.sh /media

# --- 5. install Playwright deps (first run only) ---------------------------
log "Installing test dependencies"
cd "$E2E_DIR"
[ -d node_modules ] || npm ci --no-audit --no-fund || npm install --no-audit --no-fund
npx playwright install --with-deps chromium >/dev/null 2>&1 || npx playwright install chromium

# --- 6. run the tests -------------------------------------------------------
# Setup (wizard + scan) runs as a Playwright global-setup, so the tests get a
# ready server + admin token via storage state / env.
log "Running E2E tests"
if [ "$UI_MODE" -eq 1 ]; then
  npx playwright test --ui
else
  npx playwright test
fi
