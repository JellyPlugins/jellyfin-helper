#!/usr/bin/env bash
# Write a minimal meta.json into the staged plugin folder so Jellyfin shows a
# clean plugin entry (name/version/guid) matching the release pipeline.
# Values mirror generate-meta.ps1 / build.yaml.
set -euo pipefail
OUT_DIR="${1:?usage: write-meta.sh <plugin-stage-dir>}"

cat > "$OUT_DIR/meta.json" <<'JSON'
{
  "category": "General",
  "guid": "0c737645-5cbb-4bd8-80c7-d377b560aaa4",
  "name": "Jellyfin Helper",
  "overview": "E2E test build",
  "owner": "JellyPlugins",
  "targetAbi": "12.0.0.0",
  "version": "3.0.0.0",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON
echo "[write-meta] wrote $OUT_DIR/meta.json"
