# VideoAuthoringAndroidProof

A one-screen Android app that proves CodeBrix.VideoPlayback.Authoring runs **on a phone**, with no ffmpeg
anywhere: it authors all three CodeBrix video modes in process, exports them to MP4, and plays every file it
wrote. It is a device proof, not an application template - the authoring library belongs in tools, not in
shipped apps (see the library's AGENT-README.txt).

## What it proves

One `PASS` / `PASSED-WITH-NOTE` / `FAIL` line per step goes to the screen, to logcat under the tag `VAProof`,
and to `vaproof-log.txt` beside the outputs; a final `VAPROOF RESULT:` line sums them up.

| Step | What runs on the device |
|------|-------------------------|
| 01 | Registers the Android video-processing engine (`CodeBrixVideoProcessingAndroid.Register()`), the AV1 decoder (`CodeBrixVideoPlaybackDav1d.Register()`) and Opus (`CodeBrixAudioOpus.Register()`); prints the engine name and capabilities |
| 02 | Authors **Mode1** (WebM, AV1 + Opus) from the bundled H.264/AAC clip, in process; every pass has `RunsFFmpeg == false`; read back with `MatroskaReader` |
| 03 | Authors **Mode2** (CBVF, AV1 + Vorbis) in process; read back with `CbvReader` |
| 04 | Authors **Mode3** 8-bit masters: one without alpha (FLAC sound), and one with `AuthoringAlphaMode.Include` (a fully opaque plane - the clip has no alpha) and `Audio.Silent = true`; checks the bundled host-authored alpha master |
| 05 | Authors a **10-bit** Mode3 master (`Video.BitDepth = 10`) when the engine reports `HighBitDepthFrameSource` (else a note) |
| 06 | Managed shaping on a rotated source: `FrameSize` 160x90, `FrameRate` 15, a `.cube` grade and `AutoRotate`; the dry run lists `scale`, `fps`, `grade` (and the real run `rotate`), none of them ffmpeg |
| 07-08 | Exports Mode1 and Mode2 to MP4 (H.264 + AAC) through the engine |
| 09 | Exports the alpha master over the default grey, over a PNG and over a longer backdrop MP4 (whose tail plays on alone) |
| 10 | Exports the Silent master over the backdrop MP4: the MP4's sound is the backdrop's (`VideoExportAudioSource.Backdrop`) |
| 11 | Exports to HEVC when the engine reports `HevcOutput` (else a note) |
| 12 | Plays every authored file headless with `VideoPlaybackSession` (no presenter, no audio device) and checks `HasAlpha`, `IsSilent` and the decoded bit depth |
| 13 | Cancels an authoring run part-way (the partial output is deleted) and checks that `Audio.Silent` on a Mode1 request is refused |

Alpha masters are authored where an alpha-capable source exists: the Android engine delivers no alpha
channel, so the alpha master the export steps use is made on the host and bundled (see
`Assets/PROVENANCE.txt`). Every input is synthetic and listed there with the command that made it.

## How to run

It has its own solution, `VideoAuthoringAndroidProof.slnx`; the repository's main solution does not include it,
because it needs the .NET Android workload.

```bash
cd samples/VideoAuthoringAndroidProof
dotnet build VideoAuthoringAndroidProof.csproj -c Release
adb -s <serial> install -r bin/Release/net10.0-android36.1/com.codebrix.videoplayback.authoringproof-Signed.apk
adb -s <serial> logcat -c
adb -s <serial> shell monkey -p com.codebrix.videoplayback.authoringproof -c android.intent.category.LAUNCHER 1
adb -s <serial> logcat -s VAProof
```

The proof starts by itself a second after launch (the button runs it again). Its outputs - every authored
file, every MP4 and `vaproof-log.txt` - are written to the app-specific external files folder, so a Release
build's results can be pulled without root:

```bash
adb -s <serial> pull /sdcard/Android/data/com.codebrix.videoplayback.authoringproof/files/vaproof/ .
```

## Packages only

Like every sample in this repository, this one consumes published NuGet packages and nothing else: the
authoring library and the playback core, the Android engine and the video-processing core, the AV1 decoder,
Opus and the audio core all come from nuget.org, and the AV1 encoder, FLAC, Vorbis and AV1 decoder natives ride
into the APK inside their packages. It has no project references and no path outside the package feed.
