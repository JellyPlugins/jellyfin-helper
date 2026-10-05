#!/usr/bin/env bash
# Write a minimal meta.json into a staged plugin folder so Jellyfin shows a clean
# plugin entry. Defaults to the Jellyfin Helper identity for backward
# compatibility; pass name/guid to stage an external plugin with the same layout.
set -euo pipefail
OUT_DIR="${1:?usage: write-meta.sh <plugin-stage-dir> [version] [name] [guid]}"
VERSION="${2:-3.0.0.0}"
NAME="${3:-Jellyfin Helper}"
GUID="${4:-0c737645-5cbb-4bd8-80c7-d377b560aaa4}"

cat > "$OUT_DIR/meta.json" <<JSON
{
  "category": "General",
  "guid": "${GUID}",
  "name": "${NAME}",
  "overview": "E2E test build",
  "owner": "JellyPlugins",
  "targetAbi": "12.1.0.0",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON
echo "[write-meta] wrote $OUT_DIR/meta.json (${NAME} v${VERSION})"
