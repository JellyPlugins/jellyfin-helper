#!/usr/bin/env bash
# =============================================================================
# Generate a fake media library for the E2E tests.
#
# Runs INSIDE the Jellyfin container (which ships ffmpeg) so no host ffmpeg is
# required — works identically locally and in CI. Invoked by scripts/run.sh via
# `docker compose exec jellyfin bash /media/.gen/gen-media.sh`.
#
# Produces tiny (~1s) real clips with varied codecs / resolutions / dynamic
# range so the Codecs/Health/Overview statistics get REAL data, plus fixtures
# that the cleanup tasks are meant to act on:
#   - orphaned .trickplay folder (no matching video)     -> Trickplay cleanup
#   - orphaned subtitle (.srt with no matching video)    -> Subtitle cleanup
#   - broken .strm file (points at a missing target)     -> Link repair
#   - empty-ish media folder (subtitle but no video)     -> Empty folder cleanup
#
# Idempotent: wipes and recreates the library roots each run.
# =============================================================================
set -euo pipefail

ROOT="${1:-/media}"
MOVIES="$ROOT/Movies"
SHOWS="$ROOT/Shows"

echo "[gen-media] target root: $ROOT"
rm -rf "$MOVIES" "$SHOWS"
mkdir -p "$MOVIES" "$SHOWS"

# ffmpeg lives in the Jellyfin image; fall back to jellyfin-ffmpeg path if needed.
FFMPEG="$(command -v ffmpeg || echo /usr/lib/jellyfin-ffmpeg/ffmpeg)"
echo "[gen-media] using ffmpeg: $FFMPEG"

# ---- helpers --------------------------------------------------------------

# make_clip <output> <width> <height> <vcodec> [extra ffmpeg args...]
# Generates a ~1 second silent test-pattern clip. Small but real.
make_clip() {
  local out="$1" w="$2" h="$3" vcodec="$4"; shift 4
  mkdir -p "$(dirname "$out")"
  "$FFMPEG" -nostdin -loglevel error -y \
    -f lavfi -i "testsrc=size=${w}x${h}:rate=24:duration=1" \
    -f lavfi -i "sine=frequency=440:duration=1" \
    -c:v "$vcodec" -c:a aac -shortest "$@" "$out"
  echo "[gen-media]   wrote $(basename "$out") (${w}x${h}, $vcodec)"
}

# ---- Movies: varied codecs / resolutions / dynamic range ------------------
# Names follow Jellyfin's expected "Title (Year)/Title (Year).ext" layout.

# 1080p H.264 SDR
make_clip "$MOVIES/Aurora Skies (2019)/Aurora Skies (2019).mkv" 1920 1080 libx264

# 4K HEVC HDR10 (real HDR metadata so DynamicRange analysis has something)
make_clip "$MOVIES/Nebula Drift (2021)/Nebula Drift (2021).mkv" 3840 2160 libx265 \
  -pix_fmt yuv420p10le \
  -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc

# 720p H.264 SDR, WITH an external subtitle that has a matching video (valid, must NOT be cleaned)
make_clip "$MOVIES/Copper Canyon (2015)/Copper Canyon (2015).mkv" 1280 720 libx264
printf '1\n00:00:00,000 --> 00:00:01,000\nHello\n' \
  > "$MOVIES/Copper Canyon (2015)/Copper Canyon (2015).en.srt"

# 480p MPEG-4 SDR (codec variety for the Codecs donut)
make_clip "$MOVIES/Old Reel (1998)/Old Reel (1998).mp4" 640 480 mpeg4

# ---- Shows: a couple of episodes -----------------------------------------
make_clip "$SHOWS/Test Show/Season 01/Test Show S01E01.mkv" 1920 1080 libx264
make_clip "$SHOWS/Test Show/Season 01/Test Show S01E02.mkv" 1280 720 libx265

# ---- Cleanup fixtures (deliberately broken/orphaned) ----------------------

# (a) Orphaned .trickplay folder: named after a video that does NOT exist.
mkdir -p "$MOVIES/Ghost Movie (2010)/Ghost Movie (2010).trickplay"
printf 'fake trickplay tile' > "$MOVIES/Ghost Movie (2010)/Ghost Movie (2010).trickplay/tile_0.jpg"

# (b) Orphaned subtitle: .srt with no matching video in the same dir.
mkdir -p "$MOVIES/Lonely Sub (2012)"
printf '1\n00:00:00,000 --> 00:00:01,000\nOrphan\n' \
  > "$MOVIES/Lonely Sub (2012)/Lonely Sub (2012).en.srt"

# (c) Broken .strm link: points at a non-existent target file.
mkdir -p "$MOVIES/Broken Link (2020)"
printf '/media/Movies/Does Not Exist (2020)/Does Not Exist (2020).mkv' \
  > "$MOVIES/Broken Link (2020)/Broken Link (2020).strm"

# (d) Repairable .strm link: target exists but under a renamed file in same dir.
mkdir -p "$MOVIES/Repairable Link (2020)"
make_clip "$MOVIES/Repairable Link (2020)/Actual File (2020).mkv" 640 480 libx264
printf '/media/Movies/Repairable Link (2020)/Old Name (2020).mkv' \
  > "$MOVIES/Repairable Link (2020)/Repairable Link (2020).strm"

echo "[gen-media] done. Library tree:"
find "$ROOT" -maxdepth 3 -type f | sort | sed 's/^/[gen-media]   /'
