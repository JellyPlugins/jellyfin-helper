#!/usr/bin/env bash
# =============================================================================
# Generate a fake media library for the E2E tests. Runs INSIDE the Jellyfin container (which ships ffmpeg) so no host ffmpeg is required - works identically locally and in CI.
set -euo pipefail

ROOT="${1:-/media}"
MOVIES="$ROOT/Movies"
SHOWS="$ROOT/Shows"
BOOKS="$ROOT/Books"

echo "[gen-media] target root: $ROOT"
rm -rf "$MOVIES" "$SHOWS" "$BOOKS"
mkdir -p "$MOVIES" "$SHOWS" "$BOOKS"

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
printf '%s\n' "$ROOT/Movies/Does Not Exist (2020)/Does Not Exist (2020).mkv" \
  > "$MOVIES/Broken Link (2020)/Broken Link (2020).strm"

# (d) Repairable .strm link: target exists but under a renamed file in same dir.
mkdir -p "$MOVIES/Repairable Link (2020)"
make_clip "$MOVIES/Repairable Link (2020)/Actual File (2020).mkv" 640 480 libx264
printf '%s\n' "$ROOT/Movies/Repairable Link (2020)/Old Name (2020).mkv" \
  > "$MOVIES/Repairable Link (2020)/Repairable Link (2020).strm"

# ============================================================================= Behavioral / adversarial fixtures (added for the hardened E2E suite).

# ---- Trickplay discrimination --------------------------------------------
# VALID: a .trickplay WITH a matching video to must SURVIVE cleanup.
make_clip "$MOVIES/Valid Trick (2020)/Valid Trick (2020).mkv" 640 480 libx264
mkdir -p "$MOVIES/Valid Trick (2020)/Valid Trick (2020).trickplay"
printf 'valid tile' > "$MOVIES/Valid Trick (2020)/Valid Trick (2020).trickplay/tile_0.jpg"

# NESTED: outer .trickplay kept by a matching video; inner nested .trickplay must
# not be independently deleted (nested-skip branch).
make_clip "$MOVIES/Nested (2020)/Nested (2020).mkv" 640 480 libx264
mkdir -p "$MOVIES/Nested (2020)/Nested (2020).trickplay/inner.trickplay"
printf 'inner tile' > "$MOVIES/Nested (2020)/Nested (2020).trickplay/inner.trickplay/tile_0.jpg"

# NON-VIDEO companion must NOT save a .trickplay (extension-aware match): a
# same-basename .srt but no video to orphan .trickplay must be removed.
mkdir -p "$MOVIES/Sub Only (2020)/Sub Only (2020).trickplay"
printf 'orphan tile' > "$MOVIES/Sub Only (2020)/Sub Only (2020).trickplay/tile_0.jpg"
printf '1\n00:00:00,000 --> 00:00:01,000\nX\n' > "$MOVIES/Sub Only (2020)/Sub Only (2020).en.srt"

# ---- Subtitle discrimination ---------------------------------------------
# GENUINE ORPHAN in a dir that ALSO contains a video (the branch the subtitle stage actually deletes; the existing "Lonely Sub" fixture is video-less and gets SKIPPED by the subtitle stage).
make_clip "$MOVIES/Mixed Bag (2018)/Mixed Bag (2018).mkv" 1280 720 libx264
printf '1\n00:00:00,000 --> 00:00:01,000\nkeep\n' > "$MOVIES/Mixed Bag (2018)/Mixed Bag (2018).en.srt"
printf '1\n00:00:00,000 --> 00:00:01,000\norphan\n' > "$MOVIES/Mixed Bag (2018)/Ghost Subtitle (2001).en.srt"

# MULTI-LANGUAGE valid subs (allowlist survivors) + one non-language orphan.
make_clip "$MOVIES/Polyglot (2016)/Polyglot (2016).mkv" 1280 720 libx264
for suf in en "es.forced" "de.sdh" "zh-Hans"; do
  printf '1\n00:00:00,000 --> 00:00:01,000\n%s\n' "$suf" \
    > "$MOVIES/Polyglot (2016)/Polyglot (2016).$suf.srt"
done
printf 'sub' > "$MOVIES/Polyglot (2016)/Polyglot (2016).pt-BR.ass"
# ".DTS" is NOT a language/flag to treated as orphan and removed.
printf '1\n00:00:00,000 --> 00:00:01,000\ndts\n' > "$MOVIES/Polyglot (2016)/Polyglot (2016).DTS.srt"

# FALSE-ORPHAN fallback: a title literally ending in a language token; naive
# stripping would mis-base it, the fallback keeps it valid to must survive.
make_clip "$MOVIES/Interview with the en (2004)/Interview with the en (2004).mkv" 640 480 libx264
printf '1\n00:00:00,000 --> 00:00:01,000\nkeep\n' \
  > "$MOVIES/Interview with the en (2004)/Interview with the en (2004).srt"

# ---- Empty-folder discrimination ----------------------------------------- Nested video protects the whole top-level folder (extras/notes.txt present but a video lives deeper) to entire folder must SURVIVE.
mkdir -p "$MOVIES/Mixed Keep (2016)/extras"
printf 'notes' > "$MOVIES/Mixed Keep (2016)/extras/notes.txt"
make_clip "$MOVIES/Mixed Keep (2016)/Season 01/Mixed Keep S01E01.mkv" 640 480 libx264

# Metadata-only folder (poster + nfo, no media) to wanted-placeholder, SURVIVES.
mkdir -p "$MOVIES/Wanted Placeholder (2027)"
printf 'jpg' > "$MOVIES/Wanted Placeholder (2027)/poster.jpg"
printf '<movie/>' > "$MOVIES/Wanted Placeholder (2027)/movie.nfo"

# Audio-only folder inside a video library to music guard, SURVIVES.
mkdir -p "$MOVIES/Soundtrack Only (2016)"
printf 'ID3' > "$MOVIES/Soundtrack Only (2016)/track.mp3"

# ---- Unlisted-codec false-orphan probe ----------------------------------- A real movie whose container is NOT in the video allowlist (.mxf).
mkdir -p "$MOVIES/Odd Codec (2003)"
printf 'MXF' > "$MOVIES/Odd Codec (2003)/Odd Codec (2003).mxf"
mkdir -p "$MOVIES/Odd Codec (2003)/Odd Codec (2003).trickplay"
printf 'tile' > "$MOVIES/Odd Codec (2003)/Odd Codec (2003).trickplay/tile_0.jpg"

# ---- Link-repair adversarial fixtures ------------------------------------
# AMBIGUOUS: 2+ candidate videos in the broken target's dir to must NOT guess.
mkdir -p "$MOVIES/Ambiguous Link (2020)"
make_clip "$MOVIES/Ambiguous Link (2020)/Candidate A (2020).mkv" 320 240 libx264
make_clip "$MOVIES/Ambiguous Link (2020)/Candidate B (2020).mkv" 320 240 libx264
printf '%s\n' "$ROOT/Movies/Ambiguous Link (2020)/Old (2020).mkv" \
  > "$MOVIES/Ambiguous Link (2020)/Ambiguous Link (2020).strm"

# ESCAPE: a relative .strm target that climbs out of the library to InvalidContent.
mkdir -p "$MOVIES/Escape Link (2020)"
printf '%s\n' "../../../../../../etc/passwd" \
  > "$MOVIES/Escape Link (2020)/Escape Link (2020).strm"

# ABSOLUTE-ESCAPE: an absolute .strm target outside every library to InvalidContent.
mkdir -p "$MOVIES/Abs Escape (2020)"
printf '%s\n' "/etc/passwd" \
  > "$MOVIES/Abs Escape (2020)/Abs Escape (2020).strm"

# URL: an http(s) .strm target is inert (Valid, never repaired).
mkdir -p "$MOVIES/Stream Link (2020)"
printf '%s\n' "http://example.com/stream.m3u8" \
  > "$MOVIES/Stream Link (2020)/Stream Link (2020).strm"

# ---- Symlink fixtures (Linux container supports ln -s) --------------------
# VALID symlink to existing sibling (must stay untouched). BROKEN symlink to missing target with exactly one lone renamed sibling (must be repaired).
mkdir -p "$MOVIES/Valid Symlink (2020)"
make_clip "$MOVIES/Valid Symlink (2020)/Real Target (2020).mkv" 320 240 libx264
if ln -sf "Real Target (2020).mkv" "$MOVIES/Valid Symlink (2020)/Valid Symlink (2020).mkv"; then
  echo "[gen-media]   created valid symlink"
else
  echo "[gen-media] ERROR: symlink creation failed (filesystem may not support symlinks)" >&2
  exit 1
fi
mkdir -p "$MOVIES/Broken Symlink (2020)"
make_clip "$MOVIES/Broken Symlink (2020)/Renamed Actual (2020).mkv" 320 240 libx264
ln -sf "Missing Original (2020).mkv" "$MOVIES/Broken Symlink (2020)/Broken Symlink (2020).mkv"

# =============================================================================
# Books (eBook) library , the DATA-LOSS regression fixture.
mkdir -p "$BOOKS/Some Novel"
printf 'EPUB fake bytes' > "$BOOKS/Some Novel/Some Novel.epub"
mkdir -p "$BOOKS/A Manual"
printf '%%PDF-1.4 fake bytes' > "$BOOKS/A Manual/A Manual.pdf"
# A second EPUB so the EPUB format count is > 1 and the folder-count is plural.
mkdir -p "$BOOKS/Another Story"
printf 'EPUB fake bytes 2' > "$BOOKS/Another Story/Another Story.epub"

echo "[gen-media] done. Library tree:"
find "$ROOT" -maxdepth 3 -type f | sort | sed 's/^/[gen-media]   /'
