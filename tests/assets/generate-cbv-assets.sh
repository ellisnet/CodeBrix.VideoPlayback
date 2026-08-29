#!/usr/bin/env bash
#
# Rebuilds the bespoke .cbv samples in this folder from the FFmpeg-produced inputs beside them.
#
# Unlike generate-assets.sh, which needs FFmpeg and mkvmerge, this script needs only the .NET SDK: the
# bespoke container is written by this repository's own muxer, through the cbvmux verb of the headless
# tools project. That is the whole point of the format - authoring it requires nothing that is not either
# an encoder or CodeBrix code.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "${here}/../.." && pwd)"
tools="${repo}/tools/CodeBrix.VideoPlayback.Tools/CodeBrix.VideoPlayback.Tools.csproj"

run_mux() {
    dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvmux "$@"
}

echo "building the tools"
dotnet build "${tools}" -c Release -v quiet --nologo >/dev/null

echo "av1-opus.cbv        AV1 video + Opus audio + English captions + chapters"
run_mux \
    --output "${here}/av1-opus.cbv" \
    --video "${here}/av1-video-only.ivf" \
    --audio "${here}/opus-audio.ogg" \
    --chapters "${here}/chapters.ffmeta" \
    --audio-language en \
    --video-name "picture" \
    --audio-name "sound" \
    --captions "${here}/captions-en.vtt:en:English:default" \
    --captions "${here}/srt-captions.srt:fr:Francais:sdh"

echo "av1-vorbis.cbv      AV1 video + Vorbis audio, no captions, no chapters"
run_mux \
    --output "${here}/av1-vorbis.cbv" \
    --video "${here}/av1-video-only.ivf" \
    --audio "${here}/vorbis-audio.ogg" \
    --audio-language en

echo "raw-synthetic.cbv   uncompressed video only - decodable with no codec package at all"
run_mux \
    --output "${here}/raw-synthetic.cbv" \
    --synthetic-video 60x64x36@25 \
    --chapters "${here}/chapters.ffmeta" \
    --video-name "uncompressed test pattern"

echo "raw-vorbis.cbv      uncompressed video + Vorbis audio - plays with NO codec package at all"
run_mux \
    --output "${here}/raw-vorbis.cbv" \
    --synthetic-video 25x64x36@25 \
    --audio "${here}/vorbis-audio.ogg" \
    --audio-language en \
    --audio-name "sound" \
    --video-name "uncompressed test pattern"

echo "verifying with cbvinfo"
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvinfo "${here}/av1-opus.cbv" >/dev/null
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvinfo "${here}/av1-vorbis.cbv" >/dev/null
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvinfo "${here}/raw-synthetic.cbv" >/dev/null
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvinfo "${here}/raw-vorbis.cbv" >/dev/null

echo "verifying with cbvdecode (the uncompressed samples only - the others need an AV1 decoder)"
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvdecode --headless --quiet "${here}/raw-synthetic.cbv"
dotnet run --project "${tools}" -c Release --no-launch-profile -- cbvdecode --headless --quiet "${here}/raw-vorbis.cbv"

echo
ls -l "${here}"/*.cbv
echo
echo "done. Note that the .cbv samples carry an AV1 video track, so cbvdecode can only decode them"
echo "once an AV1 decoder package is registered; cbvinfo reads them with nothing installed."
