#!/usr/bin/env bash
# Download and stage the two external plugins the Discovery custom tab depends on
# (Custom Tabs + File Transformation) into the e2e config volume, using the same
# "<Name>_<Version>" folder layout Jellyfin's loader requires.
#
# Always tracks the LATEST release of each repo (resolved via the GitHub API) and
# picks that release's highest Jellyfin-12 asset, so CI exercises the current
# plugins without any pin to bump. Overridable for offline/debug runs:
#   CUSTOMTABS_RELEASE, FILETRANSFORMATION_RELEASE  (force a specific tag)
#   CUSTOMTABS_ASSET, FILETRANSFORMATION_ASSET      (force a specific asset name
#     for one plugin; EXTERNAL_PLUGIN_JF_ASSET remains as a blanket fallback
#     for both)
#   GITHUB_TOKEN                                    (lifts the API rate limit)
set -euo pipefail

STAGE_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGINS_DIR="${1:?usage: stage-external-plugins.sh <plugins-dir>}"

# Jellyfin major the test image runs (compose.yml: 12.2). Asset selection prefers
# the highest Release-12.* zip, so a future 12.2/12.3 asset is picked up for free.
EXTERNAL_PLUGIN_JF_MAJOR="${EXTERNAL_PLUGIN_JF_MAJOR:-12}"

# Optional pins (empty = track latest). Keep these UNSET in CI so coverage always
# runs against the newest published plugins.
CUSTOMTABS_RELEASE="${CUSTOMTABS_RELEASE:-}"
FILETRANSFORMATION_RELEASE="${FILETRANSFORMATION_RELEASE:-}"

# Single allowed curl protocol: every curl call below pins both the initial
# request (--proto) and any -L redirect (--proto-redir) to this value so a
# redirect can never downgrade to plain HTTP.
readonly HTTPS_PROTO='=https'

# curl with an auth header when a token is present (CI rate-limit relief), plain
# otherwise. GITHUB_TOKEN is read from the environment; never logged.
gh_curl() {
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    curl -fsSL --proto "$HTTPS_PROTO" --proto-redir "$HTTPS_PROTO" -H "Authorization: Bearer ${GITHUB_TOKEN}" -H "X-GitHub-Api-Version: 2022-11-28" "$@"
  else
    curl -fsSL --proto "$HTTPS_PROTO" --proto-redir "$HTTPS_PROTO" "$@"
  fi
}

# Resolve the release tag to stage: an explicit pin wins, else the repo's latest.
resolve_tag() {
  local repo="$1" pin="$2"
  if [[ -n "$pin" ]]; then
    echo "$pin"
    return 0
  fi
  # releases/latest returns the newest non-draft, non-prerelease release.
  gh_curl "https://api.github.com/repos/${repo}/releases/latest" \
    | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -n1 \
    | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/'
}

# Pick the asset to download for a given tag: a per-plugin override wins, then the
# blanket EXTERNAL_PLUGIN_JF_ASSET fallback, else the highest-sorted
# "Release-<major>.*.zip" asset on that release.
resolve_asset() {
  local repo="$1" tag="$2" asset_override="${3:-}"
  if [[ -n "$asset_override" ]]; then
    echo "$asset_override"
    return 0
  fi
  if [[ -n "${EXTERNAL_PLUGIN_JF_ASSET:-}" ]]; then
    echo "$EXTERNAL_PLUGIN_JF_ASSET"
    return 0
  fi
  gh_curl "https://api.github.com/repos/${repo}/releases/tags/${tag}" \
    | grep -o '"name"[[:space:]]*:[[:space:]]*"Release-'"${EXTERNAL_PLUGIN_JF_MAJOR}"'[^"]*\.zip"' \
    | sed -E 's/.*"(Release-[^"]*)".*/\1/' \
    | sort -V \
    | tail -n1
}

# Exit codes: 0 = both staged; 2 = a tag/asset could not be resolved (caller may
# skip the custom-tab spec); any other non-zero = a genuine download/staging
# failure (set -e aborts), which the caller must treat as a hard run failure.
stage_one() {
  local name="$1" guid="$2" repo="$3" pin="$4" asset_override="${5:-}"

  local tag
  tag="$(resolve_tag "$repo" "$pin" || true)"
  if [[ -z "$tag" ]]; then
    echo "[stage-external] SKIP ${name}: could not resolve a release tag for ${repo} (API rate limit? no releases?)" >&2
    return 2
  fi

  local asset
  asset="$(resolve_asset "$repo" "$tag" "$asset_override" || true)"
  if [[ -z "$asset" ]]; then
    echo "[stage-external] SKIP ${name}: no Release-${EXTERNAL_PLUGIN_JF_MAJOR}.* asset on ${repo}@${tag}" >&2
    return 2
  fi

  local url="https://github.com/${repo}/releases/download/${tag}/${asset}"
  local tmp_zip
  tmp_zip="$(mktemp)"

  echo "[stage-external] Downloading ${name} ${tag} (${asset})"
  # -f: fail on HTTP >=400 instead of saving the error page; a missing asset is a
  # skip (exit 2), consistent with the old "source absent" behaviour.
  if ! curl -fsSL --proto "$HTTPS_PROTO" --proto-redir "$HTTPS_PROTO" -o "$tmp_zip" "$url"; then
    rm -f "$tmp_zip"
    echo "[stage-external] SKIP ${name}: release asset not reachable at ${url}" >&2
    return 2
  fi

  # Jellyfin's loader needs the folder named "<Name>_<Version>".
  local dest="${PLUGINS_DIR}/${name}_${tag}"
  mkdir -p "$dest"

  # Release zips are a flat layout (DLL + deps.json + pdb + logo, no nested dir,
  # no meta.json). The dll guard below catches an unexpected shape.
  unzip -o -q "$tmp_zip" -d "$dest" || { rm -f "$tmp_zip"; return 1; }
  rm -f "$tmp_zip"

  local dll_count
  dll_count="$(find "$dest" -maxdepth 1 -name '*.dll' | wc -l)"
  [[ "$dll_count" -ge 1 ]] || { echo "[stage-external] ${name}: no dll in release zip" >&2; return 1; }

  bash "$STAGE_SCRIPT_DIR/write-meta.sh" "$dest" "$tag" "$name" "$guid" || return 1
  echo "[stage-external] staged ${name} (${dll_count} dll(s)) -> ${dest}"
  return 0
}

# File Transformation must be present for Custom Tabs to inject on a read-only
# web dir. Both are independent plugin folders - load order is resolved by
# Jellyfin at startup. A missing asset makes the whole set unusable, so skip
# (exit 2) if either cannot be resolved/downloaded; a staging failure aborts hard.
any_absent=0

stage_one "File Transformation" "5e87cc92-571a-4d8d-8d98-d2d4147f9f90" \
  "IAmParadox27/jellyfin-plugin-file-transformation" \
  "$FILETRANSFORMATION_RELEASE" \
  "${FILETRANSFORMATION_ASSET:-}" \
  || { [[ $? -eq 2 ]] && any_absent=1 || exit 1; }

stage_one "Custom Tabs" "fbacd0b6-fd46-4a05-b0a4-2045d6a135b0" \
  "IAmParadox27/jellyfin-plugin-custom-tabs" \
  "$CUSTOMTABS_RELEASE" \
  "${CUSTOMTABS_ASSET:-}" \
  || { [[ $? -eq 2 ]] && any_absent=1 || exit 1; }

if [[ "$any_absent" -eq 1 ]]; then
  echo "[stage-external] one or more external plugin assets absent - custom-tab coverage will be skipped" >&2
  exit 2
fi
exit 0
