#!/usr/bin/env bash
#
# generate-assets.sh - regenerate the CodeBrix.VideoPlayback golden test-asset corpus.
#
# Every media file in this folder is produced from ffmpeg's synthetic lavfi sources
# (testsrc2 / sine). Nothing here is third-party media. Nothing is downloaded.
#
# Requires only: ffmpeg, ffprobe, mkvmerge (plus coreutils).
#
set -euo pipefail

cd "$(dirname "$(readlink -f "$0")")"
HERE="$(pwd)"

FFMPEG=${FFMPEG:-ffmpeg}
FFPROBE=${FFPROBE:-ffprobe}
MKVMERGE=${MKVMERGE:-mkvmerge}

for t in "$FFMPEG" "$FFPROBE" "$MKVMERGE"; do
    command -v "$t" >/dev/null 2>&1 || { echo "ERROR: required tool not found: $t" >&2; exit 1; }
done

FF="$FFMPEG -hide_banner -loglevel error -y"
MM="$MKVMERGE -q --engage no_variable_data"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

say() { printf '  [assets] %s\n' "$*"; }
sz()  { stat -c %s "$1"; }

# ---------------------------------------------------------------------------
# Shared source / encoder settings.
#
#   video : testsrc2 96x54 @ 12 fps for 1.0 s  = 12 frames
#           libaom-av1, -cpu-used 8 (fastest), -crf 50, -g 4
#           -> key frames at frames 0 / 4 / 8, i.e. t = 0.000 / 0.333 / 0.667 s
#   audio : sine 440 Hz, 48 kHz mono, 1.0 s, libopus @ 24 kb/s (or libvorbis @ 48 kb/s)
#   -cluster_time_limit 250 forces four Matroska clusters instead of one, so the
#   Cues element has real content and seek tests have somewhere to land.
# ---------------------------------------------------------------------------
VSRC='testsrc2=size=96x54:rate=12:duration=1'
ASRC='sine=frequency=440:sample_rate=48000:duration=1'
VENC=(-c:v libaom-av1 -cpu-used 8 -crf 50 -b:v 0 -g 4 -pix_fmt yuv420p)
AENC_OPUS=(-c:a libopus -b:a 24k -ac 1)
AENC_VORBIS=(-c:a libvorbis -b:a 48k -ac 1 -ar 48000)

echo "== CodeBrix.VideoPlayback test-asset generation =="
echo "   ffmpeg   : $($FFMPEG -version | head -1)"
echo "   mkvmerge : $($MKVMERGE --version | head -1)"
echo "   folder   : $HERE"
echo

# ---------------------------------------------------------------------------
# 0. Hand-authored muxer inputs (committed; rewritten here so the script is
#    genuinely self-contained).
# ---------------------------------------------------------------------------
say "writing captions-en.vtt"
cat > captions-en.vtt <<'VTT'
WEBVTT

NOTE Synthetic captions for the CodeBrix.VideoPlayback test corpus.

intro
00:00:00.000 --> 00:00:00.250 line:90% align:center
Bar pattern, top left.

00:00:00.250 --> 00:00:00.500
Colour ramp sweeps right.

third-cue
00:00:00.500 --> 00:00:00.750 align:start position:10%
Timecode digits roll over.

00:00:00.750 --> 00:00:01.000
End of the synthetic clip.
VTT

say "writing srt-captions.srt"
cat > srt-captions.srt <<'SRT'
1
00:00:00,000 --> 00:00:00,250
Bar pattern, top left.

2
00:00:00,250 --> 00:00:00,500
Colour ramp sweeps right.

3
00:00:00,500 --> 00:00:00,750
Timecode digits roll over.

4
00:00:00,750 --> 00:00:01,000
End of the synthetic clip.
SRT

say "writing chapters.ffmeta"
cat > chapters.ffmeta <<'META'
;FFMETADATA1
title=CodeBrix VideoPlayback synthetic clip
artist=ffmpeg lavfi testsrc2

[CHAPTER]
TIMEBASE=1/1000
START=0
END=333
title=Opening bars
title-fr=Mesures d'ouverture

[CHAPTER]
TIMEBASE=1/1000
START=333
END=667
title=Colour ramp

[CHAPTER]
TIMEBASE=1/1000
START=667
END=1000
title=Closing frames
META

# ---------------------------------------------------------------------------
# 1. av1-opus.webm - AV1 + Opus, WebM, Cues written BEFORE the first Cluster.
# ---------------------------------------------------------------------------
say "av1-opus.webm  (AV1 + Opus, WebM, cues_to_front)"
$FF -f lavfi -i "$VSRC" -f lavfi -i "$ASRC" \
    "${VENC[@]}" "${AENC_OPUS[@]}" \
    -cluster_time_limit 250 -f webm -cues_to_front 1 av1-opus.webm

# ---------------------------------------------------------------------------
# 2. av1-vorbis.webm - AV1 + Vorbis (libvorbis, never the built-in encoder).
# ---------------------------------------------------------------------------
say "av1-vorbis.webm  (AV1 + Vorbis, WebM, cues_to_front)"
$FF -f lavfi -i "$VSRC" -f lavfi -i "$ASRC" \
    "${VENC[@]}" "${AENC_VORBIS[@]}" \
    -cluster_time_limit 250 -f webm -cues_to_front 1 av1-vorbis.webm

# ---------------------------------------------------------------------------
# 3. av1-opus-cues-at-end.webm - identical content, Cues AFTER the clusters.
# ---------------------------------------------------------------------------
say "av1-opus-cues-at-end.webm  (AV1 + Opus, WebM, cues at tail)"
$FF -f lavfi -i "$VSRC" -f lavfi -i "$ASRC" \
    "${VENC[@]}" "${AENC_OPUS[@]}" \
    -cluster_time_limit 250 -f webm av1-opus-cues-at-end.webm

# ---------------------------------------------------------------------------
# 9. av1-video-only.ivf - AV1 elementary stream (also the mkvmerge video input).
# 10./11. Ogg Opus and Ogg Vorbis (also the mkvmerge audio inputs).
# ---------------------------------------------------------------------------
say "av1-video-only.ivf  (AV1 elementary stream)"
$FF -f lavfi -i "$VSRC" "${VENC[@]}" -f ivf av1-video-only.ivf

say "opus-audio.ogg  (Ogg Opus)"
$FF -f lavfi -i "$ASRC" "${AENC_OPUS[@]}" -f ogg opus-audio.ogg

say "vorbis-audio.ogg  (Ogg Vorbis)"
$FF -f lavfi -i "$ASRC" "${AENC_VORBIS[@]}" -f ogg vorbis-audio.ogg

# Hard-CBR Opus: every packet is the same number of bytes, which is what makes
# mkvmerge choose FIXED lacing. Intermediate only - not part of the corpus.
$FF -f lavfi -i "$ASRC" -c:a libopus -b:a 24k -vbr off -ac 1 -f ogg "$WORK/opus-cbr.ogg"

# ---------------------------------------------------------------------------
# 4. av1-opus.mkv - the SAME elementary streams remuxed by mkvmerge, so the
#    cluster layout, lacing and Cues placement come from a different muxer.
# ---------------------------------------------------------------------------
say "av1-opus.mkv  (mkvmerge remux of the IVF + Ogg Opus)"
$MM -o av1-opus.mkv av1-video-only.ivf opus-audio.ogg

# ---------------------------------------------------------------------------
# 5. raw-opus.mkv - UNCOMPRESSED video (V_UNCOMPRESSED) + Opus, Matroska.
#    Kept to 64x36 x 6 frames because raw video is bulky.
# ---------------------------------------------------------------------------
say "raw-opus.mkv  (V_UNCOMPRESSED + Opus, Matroska)"
$FF -f lavfi -i 'testsrc2=size=64x36:rate=12:duration=0.5' \
    -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=0.5' \
    -c:v rawvideo -pix_fmt yuv420p "${AENC_OPUS[@]}" \
    -f matroska raw-opus.mkv

# ---------------------------------------------------------------------------
# 6. av1-opus-captions-chapters.mkv - AV1 + Opus + WebVTT + chapters.
#    -c:s copy (NOT -c:s webvtt): the webvtt *encoder* throws the cue identifier
#    and the cue settings list away, stream-copy keeps both.
# ---------------------------------------------------------------------------
say "av1-opus-captions-chapters.mkv  (AV1 + Opus + WebVTT + chapters)"
$FF -f lavfi -i "$VSRC" -f lavfi -i "$ASRC" -i captions-en.vtt -i chapters.ffmeta \
    -map 0:v -map 1:a -map 2:s -map_metadata 3 -map_chapters 3 \
    "${VENC[@]}" "${AENC_OPUS[@]}" -c:s copy \
    -metadata:s:s:0 language=eng -metadata:s:s:0 title=English -disposition:s:0 default \
    -cluster_time_limit 250 -f matroska av1-opus-captions-chapters.mkv

# ---------------------------------------------------------------------------
# 6b. webvtt-blockadditions.mkv - the OTHER WebVTT-in-Matroska layout.
#     mkvmerge writes CodecID S_TEXT/WEBVTT and puts the cue settings and cue
#     identifier in a BlockAddition; ffmpeg writes D_WEBVTT/SUBTITLES and puts
#     them inline in the Block. The reader has to cope with both.
# ---------------------------------------------------------------------------
say "webvtt-blockadditions.mkv  (S_TEXT/WEBVTT + BlockAdditions, mkvmerge)"
$MM -o webvtt-blockadditions.mkv av1-video-only.ivf opus-audio.ogg \
    --language 0:eng --track-name 0:English --default-track-flag 0:yes captions-en.vtt

# ---------------------------------------------------------------------------
# 6c. raw-vorbis-nocues.mkv - a file whose SOUND ENDS BEFORE ITS PICTURE, with
#     NO CUES anywhere in it.
#
#     This is the shape that used to stop the player dead, and it is here to
#     prove that it no longer does. Matroska records nothing about where a track
#     stops - Cues index key frames, usually of the video track alone - so a
#     reader cannot say "the audio has finished" until it has read the whole
#     file. Removing the cues as well takes away the only other index there is.
#
#     Three seconds of uncompressed video over one second of Vorbis, chosen so
#     that fifty video packets follow the last audio packet: comfortably more
#     than the thirty-two the default video queue holds, so the demultiplexer
#     genuinely has to park the overflow and keep reading to reach the end.
#     Uncompressed video and Vorbis audio also mean the whole file plays with no
#     codec package installed at all.
# ---------------------------------------------------------------------------
say "raw-vorbis-nocues.mkv  (V_UNCOMPRESSED 3s + Vorbis 1s, no cues)"
$FF -f lavfi -i 'testsrc2=size=64x36:rate=25:duration=3.0' \
    -c:v rawvideo -pix_fmt yuv420p -f matroska "$WORK/rawvideo-3s.mkv"
$MM --no-cues -o raw-vorbis-nocues.mkv "$WORK/rawvideo-3s.mkv" vorbis-audio.ogg

# ---------------------------------------------------------------------------
# 7. Lacing fixtures. mkvmerge laces audio; ffmpeg never does.
#      lacing-vorbis.mkv  video + Vorbis, mkvmerge default        -> EBML lacing
#      lacing-ebml.mkv    Vorbis audio only, mkvmerge default     -> EBML lacing
#      lacing-xiph.mkv    Vorbis audio only, --engage lacing_xiph -> Xiph lacing
#      lacing-fixed.mkv   hard-CBR Opus audio only                -> fixed lacing
#    (mkvmerge has no lacing_fixed hack; it picks fixed lacing by itself when
#     every frame in the lace is the same size, which hard-CBR Opus guarantees.)
# ---------------------------------------------------------------------------
say "lacing-vorbis.mkv  (AV1 + Vorbis, mkvmerge, EBML-laced audio)"
$MM -o lacing-vorbis.mkv av1-video-only.ivf vorbis-audio.ogg

say "lacing-ebml.mkv  (Vorbis only, EBML lacing)"
$MM -o lacing-ebml.mkv --engage lacing_ebml vorbis-audio.ogg

say "lacing-xiph.mkv  (Vorbis only, Xiph lacing)"
$MM -o lacing-xiph.mkv --engage lacing_xiph vorbis-audio.ogg

say "lacing-fixed.mkv  (hard-CBR Opus only, fixed lacing)"
$MM -o lacing-fixed.mkv "$WORK/opus-cbr.ogg"

# ---------------------------------------------------------------------------
# ffprobe oracles - one <name>.probe.json beside every media file.
# ---------------------------------------------------------------------------
MEDIA=(
    av1-opus.webm
    av1-vorbis.webm
    av1-opus-cues-at-end.webm
    av1-opus.mkv
    raw-opus.mkv
    av1-opus-captions-chapters.mkv
    webvtt-blockadditions.mkv
    raw-vorbis-nocues.mkv
    lacing-vorbis.mkv
    lacing-ebml.mkv
    lacing-xiph.mkv
    lacing-fixed.mkv
    av1-video-only.ivf
    opus-audio.ogg
    vorbis-audio.ogg
)

echo
for f in "${MEDIA[@]}"; do
    say "probing $f -> $f.probe.json"
    "$FFPROBE" -v quiet -print_format json \
        -show_format -show_streams -show_chapters -show_frames "$f" > "$f.probe.json"
done

# ---------------------------------------------------------------------------
# Sanity checks - fail loudly rather than commit a broken corpus.
# ---------------------------------------------------------------------------
echo
say "verifying codecs"
check() {
    local file=$1 spec=$2 want=$3 got
    got=$("$FFPROBE" -v error -select_streams "$spec" -show_entries stream=codec_name \
          -of default=nw=1:nk=1 "$file" | head -1)
    if [[ "$got" != "$want" ]]; then
        echo "ERROR: $file stream $spec is '$got', expected '$want'" >&2
        exit 1
    fi
}
check av1-opus.webm                  v:0 av1
check av1-opus.webm                  a:0 opus
check av1-vorbis.webm                v:0 av1
check av1-vorbis.webm                a:0 vorbis
check av1-opus-cues-at-end.webm      v:0 av1
check av1-opus-cues-at-end.webm      a:0 opus
check av1-opus.mkv                   v:0 av1
check av1-opus.mkv                   a:0 opus
check raw-opus.mkv                   v:0 rawvideo
check raw-opus.mkv                   a:0 opus
check av1-opus-captions-chapters.mkv v:0 av1
check av1-opus-captions-chapters.mkv a:0 opus
check av1-opus-captions-chapters.mkv s:0 webvtt
# NOTE: ffmpeg 7.1.5's Matroska demuxer has no entry for S_TEXT/WEBVTT (its table
# only carries the D_WEBVTT/* family), so ffprobe reports mkvmerge's caption track
# as codec_name=unknown. Assert the track TYPE instead, and pin the CodecID below.
check_type() {
    local file=$1 spec=$2 want=$3 got
    got=$("$FFPROBE" -v error -select_streams "$spec" -show_entries stream=codec_type \
          -of default=nw=1:nk=1 "$file" | head -1)
    if [[ "$got" != "$want" ]]; then
        echo "ERROR: $file stream $spec has codec_type '$got', expected '$want'" >&2
        exit 1
    fi
}
check_type webvtt-blockadditions.mkv s:0 subtitle
check lacing-vorbis.mkv              v:0 av1
check lacing-vorbis.mkv              a:0 vorbis
check lacing-ebml.mkv                a:0 vorbis
check lacing-xiph.mkv                a:0 vorbis
check lacing-fixed.mkv               a:0 opus
check av1-video-only.ivf             v:0 av1
check opus-audio.ogg                 a:0 opus
check vorbis-audio.ogg               a:0 vorbis

# V_UNCOMPRESSED must really be in the Matroska header of raw-opus.mkv.
if ! LC_ALL=C grep -qa 'V_UNCOMPRESSED' raw-opus.mkv; then
    echo "ERROR: raw-opus.mkv does not carry the V_UNCOMPRESSED CodecID" >&2
    exit 1
fi
# The two WebVTT-in-Matroska dialects must each carry their own CodecID.
if ! LC_ALL=C grep -qa 'D_WEBVTT/SUBTITLES' av1-opus-captions-chapters.mkv; then
    echo "ERROR: av1-opus-captions-chapters.mkv is missing D_WEBVTT/SUBTITLES" >&2
    exit 1
fi
if ! LC_ALL=C grep -qa 'S_TEXT/WEBVTT' webvtt-blockadditions.mkv; then
    echo "ERROR: webvtt-blockadditions.mkv is missing S_TEXT/WEBVTT" >&2
    exit 1
fi
say "codec checks passed"

# ---------------------------------------------------------------------------
# ASSETS.txt
# ---------------------------------------------------------------------------
echo
say "writing ASSETS.txt"

FFVER=$($FFMPEG -version | head -1)
MKVVER=$($MKVMERGE --version | head -1)
TODAY=$(date +%Y-%m-%d)

{
cat <<HDR
==============================================================================
tests/assets - generated container test fixtures for CodeBrix.VideoPlayback
==============================================================================

These files are NOT third-party media. Every one of them is synthesized locally:
the picture is ffmpeg's lavfi "testsrc2" pattern generator and the sound is
ffmpeg's lavfi "sine" tone generator. No sample, clip, still or soundtrack from
any outside source is present, and nothing is downloaded during generation.

Generated by : tests/assets/generate-assets.sh
Generated on : $TODAY
ffmpeg       : $FFVER
mkvmerge     : $MKVVER

Shared encode settings for the AV1 files:
  source  testsrc2, 96x54, 12 fps, 1.0 s  ->  12 frames
  video   libaom-av1 -cpu-used 8 -crf 50 -b:v 0 -g 4 -pix_fmt yuv420p
          -g 4 puts key frames at frames 0 / 4 / 8, i.e. t = 0.000 / 0.333 / 0.667 s,
          so every file has THREE seek targets, not one.
  audio   sine 440 Hz, 48 kHz mono, 1.0 s, libopus 24 kb/s or libvorbis 48 kb/s
  layout  -cluster_time_limit 250 forces four Matroska clusters instead of one
          (five in av1-vorbis.webm, whose packets are cut differently), which
          is what makes the Cues element worth reading.

REPRODUCIBILITY: the mkvmerge-produced files are byte-identical run to run
(that is what --engage no_variable_data buys: fixed segment/track UIDs and no
multiplexing date). The ffmpeg-produced files are NOT byte-identical run to
run - ffmpeg picks random Matroska track UIDs and random Ogg stream serial
numbers - so the SHA256 list at the bottom identifies the committed bytes, it
is not a reproducibility check for the ffmpeg outputs.

------------------------------------------------------------------------------
CONTAINERS
------------------------------------------------------------------------------

av1-opus.webm ($(sz av1-opus.webm) bytes)
  ffmpeg -f lavfi -i testsrc2=size=96x54:rate=12:duration=1 \\
         -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 \\
         -c:v libaom-av1 -cpu-used 8 -crf 50 -b:v 0 -g 4 -pix_fmt yuv420p \\
         -c:a libopus -b:a 24k -ac 1 \\
         -cluster_time_limit 250 -f webm -cues_to_front 1 av1-opus.webm
  EXERCISES: the everyday WebM path - V_AV1 + A_OPUS, Cues written BEFORE the
  first Cluster, so a reader can index the file from the head alone with no
  tail seek. Its CodecPrivate is the reference av1C the .cbv muxer's own av1C
  synthesis is compared against.

av1-vorbis.webm ($(sz av1-vorbis.webm) bytes)
  ...same, but -c:a libvorbis -b:a 48k -ac 1 -ar 48000
  EXERCISES: A_VORBIS in WebM. Vorbis CodecPrivate is the three-packet Xiph
  header blob (identification / comment / setup) with a Xiph lacing length
  prefix, which is a completely different shape from OpusHead - it is the case
  that catches a reader that assumes CodecPrivate is always a fixed struct.

av1-opus-cues-at-end.webm ($(sz av1-opus-cues-at-end.webm) bytes)
  ...exactly av1-opus.webm without -cues_to_front 1
  EXERCISES: Cues sitting AFTER the last Cluster. Forces the tail-range read
  path - a reader that only looks at the head finds no index here and must
  either follow the SeekHead or read backwards from the end of the Segment.

av1-opus.mkv ($(sz av1-opus.mkv) bytes)
  mkvmerge --engage no_variable_data -o av1-opus.mkv av1-video-only.ivf opus-audio.ogg
  EXERCISES: the same two elementary streams laid out by a DIFFERENT muxer.
  mkvmerge laces the Opus audio (EBML lacing, 8 frames per block), writes three
  clusters, leaves large EbmlVoid padding after the SeekHead and after Tracks,
  and puts Cues and then Tags at the end. ffmpeg does none of that.

raw-opus.mkv ($(sz raw-opus.mkv) bytes)
  ffmpeg -f lavfi -i testsrc2=size=64x36:rate=12:duration=0.5 \\
         -f lavfi -i sine=frequency=440:sample_rate=48000:duration=0.5 \\
         -c:v rawvideo -pix_fmt yuv420p -c:a libopus -b:a 24k -ac 1 \\
         -f matroska raw-opus.mkv
  EXERCISES: a V_UNCOMPRESSED video track. The Matroska CodecID stored in the
  file is the exact string  V_UNCOMPRESSED  (no trailing fourcc), and the pixel
  format is carried separately in the ColourSpace element (0x2EB524) as the
  four ASCII bytes  I420  (49 34 32 30). ffprobe reports codec_name=rawvideo,
  codec_tag_string=I420. Six 64x36 yuv420p frames = 6 * 3456 = 20736 bytes of
  picture data, every frame a key frame.

av1-opus-captions-chapters.mkv ($(sz av1-opus-captions-chapters.mkv) bytes)
  ffmpeg -f lavfi -i testsrc2=... -f lavfi -i sine=... \\
         -i captions-en.vtt -i chapters.ffmeta \\
         -map 0:v -map 1:a -map 2:s -map_metadata 3 -map_chapters 3 \\
         -c:v libaom-av1 -cpu-used 8 -crf 50 -b:v 0 -g 4 -pix_fmt yuv420p \\
         -c:a libopus -b:a 24k -ac 1 -c:s copy \\
         -metadata:s:s:0 language=eng -metadata:s:s:0 title=English \\
         -disposition:s:0 default -cluster_time_limit 250 \\
         -f matroska av1-opus-captions-chapters.mkv
  EXERCISES: a caption track plus a flat chapter edition.
    * CodecID is  D_WEBVTT/SUBTITLES  - ffmpeg's Matroska muxer does NOT write
      S_TEXT/WEBVTT. Track Name = "English", Language = "eng", default flag set.
    * Each cue is a BlockGroup/Block (not a SimpleBlock) whose payload is
          <cue identifier> LF <cue settings list> LF <cue payload>
      identifier FIRST, settings SECOND. Both lines are present but empty for
      the cues that carry neither, so cue 2's block payload literally starts
      with two LF bytes. -c:s copy is essential: -c:s webvtt re-encodes and
      throws the identifier and the settings away, leaving every block "\\n\\n...".
    * Chapters land as a single default EditionEntry with three ChapterAtoms
      (UIDs 1/2/3) at 0.000-0.333, 0.333-0.667, 0.667-1.000 s, each with one
      ChapterDisplay whose ChapterLanguage is "und".
    * The chapters.ffmeta  title-fr=  line does NOT become a second
      ChapterDisplay. ffmpeg turns it into a Tag targeting ChapterUID 1 with
      TagName TITLE and TagLanguage "fre"; ffprobe surfaces it as the chapter
      tag "TITLE-fre". That is the shape a reader has to look for.

webvtt-blockadditions.mkv ($(sz webvtt-blockadditions.mkv) bytes)
  mkvmerge --engage no_variable_data -o webvtt-blockadditions.mkv \\
           av1-video-only.ivf opus-audio.ogg \\
           --language 0:eng --track-name 0:English --default-track-flag 0:yes \\
           captions-en.vtt
  EXERCISES: the OTHER WebVTT-in-Matroska dialect, the one the Matroska spec
  actually describes, so the reader is written against both and not just against
  ffmpeg. CodecID is  S_TEXT/WEBVTT . The Block holds ONLY the cue payload; the
  cue settings and the cue identifier live in a BlockAddition as
          <cue settings list> LF <cue identifier> LF
  settings FIRST, identifier SECOND - the reverse of ffmpeg's inline order - in
  a BlockMore whose BlockAddID is omitted (default 1). BlockDuration is written
  per cue (0xFA = 250 ms). Cues with neither setting nor identifier get no
  BlockAddition at all.
  WATCH OUT: ffmpeg 7.1.5 cannot READ this track. libavformat's Matroska codec
  table carries only D_WEBVTT/CAPTIONS, D_WEBVTT/DESCRIPTIONS, D_WEBVTT/METADATA
  and D_WEBVTT/SUBTITLES - there is no S_TEXT/WEBVTT entry - so this file's
  probe oracle honestly records  codec_name = "unknown"  for stream 2 and
  ffprobe prints "Unsupported codec with id 0 for input stream 2". The track is
  still a perfectly valid Matroska subtitle track; the library must not use
  ffprobe's codec_name as its authority here, it must read the CodecID.

lacing-vorbis.mkv ($(sz lacing-vorbis.mkv) bytes)
  mkvmerge --engage no_variable_data -o lacing-vorbis.mkv av1-video-only.ivf vorbis-audio.ogg
  EXERCISES: laced audio next to unlaced video in one file. The V_AV1 track's
  blocks all have lacing bits 00; the A_VORBIS track's blocks have flags 0x86,
  i.e. EBML lacing with 8 frames per block. TimestampScale is the ordinary
  1000000 ns because the file has a video track.

lacing-ebml.mkv ($(sz lacing-ebml.mkv) bytes)
  mkvmerge --engage no_variable_data -o lacing-ebml.mkv --engage lacing_ebml vorbis-audio.ogg
  EXERCISES: EBML lacing on its own - SimpleBlock flags 0x86, 8 frames per
  block, frame sizes coded as one EBML-vint absolute size followed by signed
  vint deltas. Audio-only, so mkvmerge drops TimestampScale to 20832 ns
  (1/48000 s) for sample-accurate timestamps - a reader that hard-codes
  1000000 gets every timestamp wrong by a factor of 48.

lacing-xiph.mkv ($(sz lacing-xiph.mkv) bytes)
  mkvmerge --engage no_variable_data -o lacing-xiph.mkv --engage lacing_xiph vorbis-audio.ogg
  EXERCISES: Xiph lacing - SimpleBlock flags 0x82, 8 frames per block, frame
  sizes coded as chains of 255-bytes terminated by a byte < 255. Same content
  and same TimestampScale (20832) as lacing-ebml.mkv, so the two files differ
  only in the lacing scheme.

lacing-fixed.mkv ($(sz lacing-fixed.mkv) bytes)
  ffmpeg ... -c:a libopus -b:a 24k -vbr off -ac 1 -f ogg <tmp>/opus-cbr.ogg
  mkvmerge --engage no_variable_data -o lacing-fixed.mkv <tmp>/opus-cbr.ogg
  EXERCISES: fixed lacing - SimpleBlock flags 0x84, 8 frames per block, NO size
  table at all; the reader must divide the remaining block bytes by the frame
  count. mkvmerge has no lacing_fixed hack, it selects fixed lacing on its own
  when every frame in a lace is the same size, which is exactly what hard-CBR
  Opus (-vbr off) produces. TimestampScale 20832 ns as above.

------------------------------------------------------------------------------
ELEMENTARY STREAMS
------------------------------------------------------------------------------

av1-video-only.ivf ($(sz av1-video-only.ivf) bytes)
  ffmpeg -f lavfi -i testsrc2=size=96x54:rate=12:duration=1 \\
         -c:v libaom-av1 -cpu-used 8 -crf 50 -b:v 0 -g 4 -pix_fmt yuv420p \\
         -f ivf av1-video-only.ivf
  EXERCISES: input for the library's own .cbv muxer, and the source the
  synthesized av1C configOBU is checked against the CodecPrivate in
  av1-opus.webm. 32-byte IVF file header + 12-byte frame headers, 12 frames.

opus-audio.ogg ($(sz opus-audio.ogg) bytes)
  ffmpeg -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 \\
         -c:a libopus -b:a 24k -ac 1 -f ogg opus-audio.ogg
  EXERCISES: Ogg Opus - OpusHead/OpusTags packets and the pre-skip, against the
  A_OPUS CodecPrivate in the WebM/Matroska files, which is the same OpusHead.
  Also the audio input to the mkvmerge remuxes.

vorbis-audio.ogg ($(sz vorbis-audio.ogg) bytes)
  ffmpeg -f lavfi -i sine=frequency=440:sample_rate=48000:duration=1 \\
         -c:a libvorbis -b:a 48k -ac 1 -ar 48000 -f ogg vorbis-audio.ogg
  EXERCISES: Ogg Vorbis - the three Xiph header packets in their native Ogg
  framing, against the same three packets Xiph-lacing-packed into the A_VORBIS
  CodecPrivate in av1-vorbis.webm. Also the audio input to the lacing remuxes.

------------------------------------------------------------------------------
MUXER INPUTS (hand-authored text, no probe oracle)
------------------------------------------------------------------------------

captions-en.vtt ($(sz captions-en.vtt) bytes)
  Hand-authored. Four cues; cue 1 carries both an identifier ("intro") and a
  settings list ("line:90% align:center"); cue 3 carries an identifier
  ("third-cue") and settings ("align:start position:10%"); cues 2 and 4 carry
  neither. There is also a NOTE block before the first cue.
  EXERCISES: the WebVTT parser, and it is the muxer input for both caption
  containers above.

srt-captions.srt ($(sz srt-captions.srt) bytes)
  Hand-authored. Four SubRip cues, comma decimal separator, numeric cue index.
  EXERCISES: the SubRip parser. Muxer input only - there is no SubRip container
  in this corpus.

chapters.ffmeta ($(sz chapters.ffmeta) bytes)
  Hand-authored ffmetadata. Three [CHAPTER] blocks, TIMEBASE=1/1000, START/END
  in milliseconds, title= on all three and an extra title-fr= on the first.
  EXERCISES: the chapter authoring path; see av1-opus-captions-chapters.mkv for
  what actually survives the mux.

------------------------------------------------------------------------------
FFPROBE ORACLES
------------------------------------------------------------------------------

Every media file has a <name>.probe.json beside it, recorded with

  ffprobe -v quiet -print_format json -show_format -show_streams \\
          -show_chapters -show_frames <file>

These are the cross-check oracles: the library's own reader is compared against
them stream by stream and frame by frame. They are regenerated by this script,
so a change in ffmpeg's reported values shows up as a diff rather than as a
silent test drift. -show_chapters is empty for the .ivf and .ogg files.

------------------------------------------------------------------------------
PINNED CodecPrivate / extradata
------------------------------------------------------------------------------
The AV1 CodecPrivate (the av1C box: marker+version, seq_profile/level/tier,
bit-depth and colour flags, then the sequence-header OBU) is BYTE-IDENTICAL in
av1-opus.webm, av1-vorbis.webm, av1-opus-cues-at-end.webm, av1-opus.mkv,
webvtt-blockadditions.mkv, lacing-vorbis.mkv and av1-opus-captions-chapters.mkv,
because they all carry the same encode. That single blob is the reference the
.cbv muxer's own av1C synthesis from av1-video-only.ivf is compared against.
The A_OPUS CodecPrivate is the 19-byte OpusHead, identical in every Opus file
and identical to the OpusHead packet in opus-audio.ogg.
The A_VORBIS CodecPrivate is the three Xiph header packets Xiph-lacing-packed
into one blob (lead byte 0x02 = "two lengths follow"), which is why the Tracks
element of av1-vorbis.webm is ~4 KB while the Opus one is under 700 bytes.

------------------------------------------------------------------------------
MATROSKA CodecIDs PRESENT IN THE CORPUS
------------------------------------------------------------------------------
(read straight out of the file bytes, not from ffprobe. Note when grepping these
yourself: the CodecID string is NOT null-terminated, so a greedy pattern such as
'A_[A-Z_]+' picks up the first byte of the following EBML id and reports the
Opus track as "A_OPUSV". The real stored string is A_OPUS, six bytes.)
HDR

for f in av1-opus.webm av1-vorbis.webm av1-opus-cues-at-end.webm av1-opus.mkv \
         raw-opus.mkv av1-opus-captions-chapters.mkv webvtt-blockadditions.mkv \
         lacing-vorbis.mkv lacing-ebml.mkv lacing-xiph.mkv lacing-fixed.mkv; do
    ids=$(LC_ALL=C grep -aoE '(V_AV1|V_UNCOMPRESSED|A_OPUS|A_VORBIS|S_TEXT/WEBVTT|D_WEBVTT/SUBTITLES)' "$f" \
          | sort -u | tr '\n' ' ')
    printf '  %-34s %s\n' "$f" "$ids"
done

echo
echo "  --- av1C (video extradata) ---"
"$FFPROBE" -v error -select_streams v:0 -show_streams -show_data -of default av1-opus.webm \
    | sed -n '/^extradata=$/,/^extradata_size=/p' | sed 's/^/  /'
echo "  --- OpusHead (audio extradata) ---"
"$FFPROBE" -v error -select_streams a:0 -show_streams -show_data -of default av1-opus.webm \
    | sed -n '/^extradata=$/,/^extradata_size=/p' | sed 's/^/  /'

cat <<'FTR'

------------------------------------------------------------------------------
LACING COVERAGE
------------------------------------------------------------------------------
  Xiph lacing  (flags bits 01, 0x82)  YES - lacing-xiph.mkv
  EBML lacing  (flags bits 11, 0x86)  YES - lacing-ebml.mkv, lacing-vorbis.mkv,
                                            av1-opus.mkv, webvtt-blockadditions.mkv
  fixed lacing (flags bits 10, 0x84)  YES - lacing-fixed.mkv
  no lacing    (flags bits 00)        YES - every ffmpeg-muxed file; ffmpeg's
                                            Matroska/WebM muxer never laces.
  All three lacing schemes were confirmed by reading the SimpleBlock flags byte
  out of the files, not inferred from the muxer options.

FTR

echo "------------------------------------------------------------------------------"
echo "SHA256"
echo "------------------------------------------------------------------------------"
sha256sum av1-opus.webm av1-vorbis.webm av1-opus-cues-at-end.webm av1-opus.mkv \
          raw-opus.mkv av1-opus-captions-chapters.mkv webvtt-blockadditions.mkv \
          lacing-vorbis.mkv lacing-ebml.mkv lacing-xiph.mkv lacing-fixed.mkv \
          av1-video-only.ivf opus-audio.ogg vorbis-audio.ogg \
          captions-en.vtt srt-captions.srt chapters.ffmeta
} > ASSETS.txt

echo
say "done - $(ls -1 | wc -l) files in $HERE"
