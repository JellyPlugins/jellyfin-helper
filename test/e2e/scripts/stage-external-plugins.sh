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

stage_one() {
  local name="$1" guid="$2" src="$3" csproj="$4" dll="$5"

  if [[ ! -d "$src" ]]; then
    echo "[stage-external] SKIP ${name}: source not found at ${src}" >&2
    echo "[stage-external]   set CUSTOMTABS_SRC / FILETRANSFORMATION_SRC to override." >&2
    return 1
  fi

  echo "[stage-external] Building ${name} (Jellyfin ${JELLYFIN_BUILD_VERSION})"
  local publish_dir="${src}/.e2e-publish"
  rm -rf "$publish_dir"
  dotnet publish "${src}/${csproj}" \
    -c Release -o "$publish_dir" --nologo \
    -p:JellyfinVersion="${JELLYFIN_BUILD_VERSION}"

  # Jellyfin's loader needs the folder named "<Name>_<Version>".
  local dest="${PLUGINS_DIR}/${name}_${JELLYFIN_BUILD_VERSION}"
  mkdir -p "$dest"

  local staged=0 base
  for f in "$publish_dir"/*.dll; do
    base="$(basename "$f")"
    if [[ "$base" != "$dll" && "$base" =~ $host_provided ]]; then
      continue
    fi
    cp "$f" "$dest/"
    staged=$((staged + 1))
  done
  [[ "$staged" -ge 1 ]] || { echo "[stage-external] ${name}: no dlls staged" >&2; return 1; }

  bash "$STAGE_SCRIPT_DIR/write-meta.sh" "$dest" "$JELLYFIN_BUILD_VERSION" "$name" "$guid"
  echo "[stage-external] staged ${name} (${staged} dll(s)) -> ${dest}"
}

# File Transformation must be present for Custom Tabs to inject on a read-only
# web dir; stage it first. Both are independent plugin folders - load order is
# resolved by Jellyfin at startup.
stage_one "File Transformation" "5e87cc92-571a-4d8d-8d98-d2d4147f9f90" \
  "$FILETRANSFORMATION_SRC" \
  "src/Jellyfin.Plugin.FileTransformation/Jellyfin.Plugin.FileTransformation.csproj" \
  "Jellyfin.Plugin.FileTransformation.dll"

stage_one "Custom Tabs" "fbacd0b6-fd46-4a05-b0a4-2045d6a135b0" \
  "$CUSTOMTABS_SRC" \
  "src/Jellyfin.Plugin.CustomTabs/Jellyfin.Plugin.CustomTabs.csproj" \
  "Jellyfin.Plugin.CustomTabs.dll"
