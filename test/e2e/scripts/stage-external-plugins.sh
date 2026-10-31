#!/usr/bin/env bash
# Download and stage the official Trakt plugin the e2e suite depends on into the
# config volume, using the same "<Name>_<Version>" folder layout Jellyfin's loader
# requires. The Discovery tab is built into the Helper and needs no external plugin.
#
# Always tracks the LATEST release (resolved via the GitHub API) and picks that
# release's highest matching asset, so CI exercises the current plugin without any
# pin to bump. Overridable for offline/debug runs:
#   TRAKT_RELEASE, TRAKT_ASSET  (force a tag or asset name)
#   GITHUB_TOKEN                (lifts the API rate limit)
set -euo pipefail

STAGE_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGINS_DIR="${1:?usage: stage-external-plugins.sh <plugins-dir>}"

# Jellyfin major the test image runs. Asset selection prefers
# the highest Release-12.* zip, so a future 12.x asset is picked up for free.
EXTERNAL_PLUGIN_JF_MAJOR="${EXTERNAL_PLUGIN_JF_MAJOR:-12}"

# Optional pin (empty = track latest). Keep it UNSET in CI so coverage always
# runs against the newest published plugin.
TRAKT_RELEASE="${TRAKT_RELEASE:-}"

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
# blanket EXTERNAL_PLUGIN_JF_ASSET fallback, else the highest-sorted asset matching
# the plugin's asset glob (defaults to "Release-<major>.*.zip" for our own-schema
# plugins; the official Trakt plugin passes "trakt_*.zip" since it uses a different
# naming convention).
resolve_asset() {
  local repo="$1" tag="$2" asset_override="${3:-}" asset_glob="${4:-}"
  if [[ -n "$asset_override" ]]; then
    echo "$asset_override"
    return 0
  fi
  if [[ -n "${EXTERNAL_PLUGIN_JF_ASSET:-}" ]]; then
    echo "$EXTERNAL_PLUGIN_JF_ASSET"
    return 0
  fi
  # Translate a shell glob (e.g. "trakt_*.zip" or "Release-12.*.zip") into a regex
  # for the asset-name match, then pick the highest version-sorted match.
  local pattern="${asset_glob:-Release-${EXTERNAL_PLUGIN_JF_MAJOR}.*.zip}"
  local regex
  regex="$(printf '%s' "$pattern" | sed -e 's/[.]/\\./g' -e 's/[*]/[^"]*/g')"
  gh_curl "https://api.github.com/repos/${repo}/releases/tags/${tag}" \
    | grep -oE '"name"[[:space:]]*:[[:space:]]*"'"${regex}"'"' \
    | sed -E 's/.*"([^"]*)".*/\1/' \
    | sort -V \
    | tail -n1
}

# Exit codes: 0 = staged; non-zero = staging failed. The caller (run.sh) aborts on
# any non-zero. The official Trakt plugin is a required prerequisite for the Trakt
# layout spec, so an unresolvable latest asset is a real signal, not a reason to
# drop coverage.
stage_one() {
  local name="$1" guid="$2" repo="$3" pin="$4" asset_override="${5:-}" asset_glob="${6:-}"

  local tag
  tag="$(resolve_tag "$repo" "$pin" || true)"
  if [[ -z "$tag" ]]; then
    echo "[stage-external] SKIP ${name}: could not resolve a release tag for ${repo} (API rate limit? no releases?)" >&2
    return 2
  fi

  local asset
  asset="$(resolve_asset "$repo" "$tag" "$asset_override" "$asset_glob" || true)"
  if [[ -z "$asset" ]]; then
    echo "[stage-external] SKIP ${name}: no matching release asset on ${repo}@${tag}" >&2
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

any_absent=0

# Official Jellyfin Trakt plugin: lets the e2e stack exercise the Helper's
# "source Trakt through the official plugin" mode. Its release assets are named
# "trakt_<ver>.zip" (not our Release-12.*.zip schema), so pass that asset glob.
# The Helper reads this plugin's config file (configurations/Trakt.xml), which the
# Playwright setup seeds with a known per-user token.
stage_one "Trakt" "4fe3201e-d6ae-4f2e-8917-e12bda571281" \
  "jellyfin/jellyfin-plugin-trakt" \
  "${TRAKT_RELEASE:-}" \
  "${TRAKT_ASSET:-}" \
  "trakt_*.zip" \
  || { [[ $? -eq 2 ]] && any_absent=1 || exit 1; }

if [[ "$any_absent" -eq 1 ]]; then
  echo "[stage-external] could not resolve the plugin from its latest release - the caller aborts (the official Trakt plugin is required)" >&2
  exit 2
fi
exit 0
