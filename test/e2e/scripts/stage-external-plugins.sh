#!/usr/bin/env bash
# Build and stage the two external plugins the Discovery custom tab depends on
# (Custom Tabs + File Transformation) into the e2e config volume, using the same
# "<Name>_<Version>" folder layout Jellyfin's loader requires. Built from local
# source checkouts for Jellyfin 12.x so the ABI matches the 12.1 test image.
#
# Source checkouts default to siblings of the repo and are overridable:
#   CUSTOMTABS_SRC, FILETRANSFORMATION_SRC
set -euo pipefail

STAGE_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGINS_DIR="${1:?usage: stage-external-plugins.sh <plugins-dir>}"

# Build these against the same Jellyfin major as the test image (compose.yml: 12.1).
JELLYFIN_BUILD_VERSION="${JELLYFIN_BUILD_VERSION:-12.1.0}"

# Default to sibling checkouts next to this repo's parent.
REPO_PARENT="$(cd "$STAGE_SCRIPT_DIR/../../../.." && pwd)"
CUSTOMTABS_SRC="${CUSTOMTABS_SRC:-$REPO_PARENT/jellyfin-plugin-custom-tabs-main}"
FILETRANSFORMATION_SRC="${FILETRANSFORMATION_SRC:-$REPO_PARENT/jellyfin-plugin-file-transformation-main}"

# Host-provided assemblies the Jellyfin image already ships; staging our own
# copies risks assembly-identity conflicts. Newtonsoft.Json is host-provided too.
host_provided='^(Jellyfin\.|MediaBrowser\.|Microsoft\.|System\.|netstandard|Newtonsoft\.Json)'

# Exit codes: 0 = both staged; 2 = a source checkout is absent (caller may skip
# the custom-tab spec); any other non-zero = a genuine build/staging failure
# (set -e aborts), which the caller must treat as a hard run failure.
stage_one() {
  local name="$1" guid="$2" src="$3" csproj="$4" dll="$5"

  if [[ ! -d "$src" ]]; then
    echo "[stage-external] SKIP ${name}: source not found at ${src}" >&2
    echo "[stage-external]   set CUSTOMTABS_SRC / FILETRANSFORMATION_SRC to override." >&2
    return 2
  fi

  echo "[stage-external] Building ${name} (Jellyfin ${JELLYFIN_BUILD_VERSION})"
  local publish_dir="${src}/.e2e-publish"
  rm -rf "$publish_dir"
  # Explicit || return: set -e is ignored inside the caller's || list, so a bare
  # failing command would fall through and stage stale DLLs instead of aborting.
  dotnet publish "${src}/${csproj}" \
    -c Release -o "$publish_dir" --nologo \
    -p:JellyfinVersion="${JELLYFIN_BUILD_VERSION}" || return 1

  # Jellyfin's loader needs the folder named "<Name>_<Version>".
  local dest="${PLUGINS_DIR}/${name}_${JELLYFIN_BUILD_VERSION}"
  mkdir -p "$dest"

  local staged=0 base
  for f in "$publish_dir"/*.dll; do
    base="$(basename "$f")"
    if [[ "$base" != "$dll" && "$base" =~ $host_provided ]]; then
      continue
    fi
    cp "$f" "$dest/" || return 1
    staged=$((staged + 1))
  done
  [[ "$staged" -ge 1 ]] || { echo "[stage-external] ${name}: build produced no dlls" >&2; return 1; }

  bash "$STAGE_SCRIPT_DIR/write-meta.sh" "$dest" "$JELLYFIN_BUILD_VERSION" "$name" "$guid"
  echo "[stage-external] staged ${name} (${staged} dll(s)) -> ${dest}"
  return 0
}

# File Transformation must be present for Custom Tabs to inject on a read-only
# web dir; stage it first. Both are independent plugin folders - load order is
# resolved by Jellyfin at startup. A missing source makes the whole set
# unusable, so skip (exit 2) if either is absent; a build failure aborts hard.
any_absent=0

stage_one "File Transformation" "5e87cc92-571a-4d8d-8d98-d2d4147f9f90" \
  "$FILETRANSFORMATION_SRC" \
  "src/Jellyfin.Plugin.FileTransformation/Jellyfin.Plugin.FileTransformation.csproj" \
  "Jellyfin.Plugin.FileTransformation.dll" || { [[ $? -eq 2 ]] && any_absent=1 || exit 1; }

stage_one "Custom Tabs" "fbacd0b6-fd46-4a05-b0a4-2045d6a135b0" \
  "$CUSTOMTABS_SRC" \
  "src/Jellyfin.Plugin.CustomTabs/Jellyfin.Plugin.CustomTabs.csproj" \
  "Jellyfin.Plugin.CustomTabs.dll" || { [[ $? -eq 2 ]] && any_absent=1 || exit 1; }

if [[ "$any_absent" -eq 1 ]]; then
  echo "[stage-external] one or more external plugin sources absent - custom-tab coverage will be skipped" >&2
  exit 2
fi
exit 0
