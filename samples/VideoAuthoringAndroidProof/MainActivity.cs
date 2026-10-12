using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Authoring;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing.Android;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Media;
using global::Android.App;
using global::Android.OS;
using global::Android.Widget;
using AndroidBuild = global::Android.OS.Build;
using AndroidLog = global::Android.Util.Log;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace VideoAuthoringAndroidProof;

/// <summary>
/// One-screen device proof for CodeBrix.VideoPlayback.Authoring on Android: registers the Android video-processing
/// engine, the AV1 decoder and Opus, then AUTHORS all three CodeBrix video modes in process from a bundled H.264/AAC
/// clip (Mode1 WebM + Opus, Mode2 CBVF + Vorbis, Mode3 masters at 8 and 10 bits, Silent, with an alpha plane),
/// runs the managed shaping (rotate, scale, fps, grade), EXPORTS the results to MP4 through the engine (H.264, HEVC
/// when offered, a shipped alpha master composited over a colour, an image and a video), PLAYS every authored file
/// headless with VideoPlaybackSession, and checks cancellation and a refusal. One PASS / PASSED-WITH-NOTE / FAIL
/// line per step and a final "VAPROOF RESULT:" line go to the screen, to logcat under the tag VAProof, and to
/// vaproof-log.txt beside the outputs.
/// </summary>
[Activity(Label = "CodeBrix Video Authoring Proof", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private const string LogTag = "VAProof";
    private const string ClipName = "clip-h264-aac.mp4";
    private const string RotatedClipName = "clip-rotated90.mp4";
    private const string BackdropImageName = "backdrop.png";
    private const string BackdropVideoName = "backdrop-3s.mp4";
    private const string LutName = "twist.cube";
    private const string AlphaMasterName = "alpha-master.cbvmaster";

    private static readonly string[] AssetNames =
    {
        ClipName, RotatedClipName, BackdropImageName, BackdropVideoName, LutName, AlphaMasterName,
    };

    private TextView _log = null;
    private ScrollView _scroll = null;
    private Button _run = null;
    private int _running;
    private string _work = null;
    private string _temp = null;
    private StreamWriter _file = null;
    private readonly object _fileGate = new object();
    private readonly List<string> _failures = new List<string>();
    private readonly List<string> _notes = new List<string>();
    private int _passed;

    /// <summary>Builds the one-screen UI and starts the proof one second after launch (so adb needs no taps).</summary>
    /// <param name="savedInstanceState">Unused saved state.</param>
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var panel = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        _run = new Button(this) { Text = "Run video authoring proof" };
        _run.Click += (_, _) => StartRun();
        _log = new TextView(this) { Text = "" };
        _log.SetTextIsSelectable(true);
        _scroll = new ScrollView(this);
        _scroll.AddView(_log);
        panel.AddView(_run);
        panel.AddView(_scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, 0, 1f));
        SetContentView(panel);

        new Handler(Looper.MainLooper).PostDelayed(StartRun, 1000);
    }

    private void StartRun()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _run.Enabled = false;
        Task.Run(() =>
        {
            try { RunProof(); }
            catch (Exception ex) { Log("VAPROOF RESULT: FAIL: unexpected " + Describe(ex)); }
            finally
            {
                lock (_fileGate)
                {
                    _file?.Dispose();
                    _file = null;
                }

                Volatile.Write(ref _running, 0);
                RunOnUiThread(() => _run.Enabled = true);
            }
        });
    }

    private void Log(string line)
    {
        AndroidLog.Info(LogTag, line);
        lock (_fileGate)
        {
            _file?.WriteLine(line);
            _file?.Flush();
        }

        RunOnUiThread(() =>
        {
            _log.Append(line + "\n");
            _scroll.Post(() => _scroll.FullScroll(global::Android.Views.FocusSearchDirection.Down));
        });
    }

    private void Step(string id, string name, Func<string> body)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            string detail = body();
            if (detail != null && detail.StartsWith("NOTE:", StringComparison.Ordinal))
            {
                _notes.Add(id);
                Log($"STEP {id} {name}: PASSED-WITH-NOTE ({watch.ElapsedMilliseconds} ms) - {detail.Substring(5).Trim()}");
            }
            else
            {
                _passed++;
                Log($"STEP {id} {name}: PASS ({watch.ElapsedMilliseconds} ms) - {detail}");
            }
        }
        catch (Exception ex)
        {
            _failures.Add(id);
            Log($"STEP {id} {name}: FAIL ({watch.ElapsedMilliseconds} ms) - {Describe(ex)}");
        }
    }

    private static string Describe(Exception ex)
    {
        var text = new StringBuilder(ex.GetType().Name + ": " + ex.Message);
        for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            text.Append(" <- " + inner.GetType().Name + ": " + inner.Message);
        }

        return text.ToString();
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private string Out(string name) => Path.Combine(_work, name);

    private void Detail(string what, IEnumerable<string> lines)
    {
        foreach (string line in lines) Log("    " + what + ": " + line);
    }

    private void RunProof()
    {
        _failures.Clear();
        _notes.Clear();
        _passed = 0;

        // The app-specific EXTERNAL files dir, so the outputs can be pulled with adb from a Release (non-debuggable)
        // build; the internal files dir when there is none.
        Java.IO.File external = GetExternalFilesDir(null);
        string root = external != null ? external.AbsolutePath : FilesDir.AbsolutePath;
        _work = Path.Combine(root, "vaproof");
        if (Directory.Exists(_work)) Directory.Delete(_work, true);
        Directory.CreateDirectory(_work);
        _temp = Path.Combine(CacheDir.AbsolutePath, "vaproof-temp");
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
        Directory.CreateDirectory(_temp);
        lock (_fileGate) _file = new StreamWriter(Out("vaproof-log.txt"), false, new UTF8Encoding(false));

        Log($"VAProof start: {AndroidBuild.Manufacturer} {AndroidBuild.Model}, API {(int)AndroidBuild.VERSION.SdkInt}, ABI {string.Join(",", AndroidBuild.SupportedAbis ?? Array.Empty<string>())}");
        Log("VAProof outputs: " + _work);

        foreach (string name in AssetNames)
        {
            using Stream asset = Assets.Open(name);
            using FileStream file = File.Create(Out(name));
            asset.CopyTo(file);
        }

        IVideoProcessor engine = null;
        bool highBitDepth = false;
        bool hevc = false;

        Step("01", "Register", () =>
        {
            CodeBrixVideoProcessingAndroid.Register();
            CodeBrixVideoPlaybackDav1d.Register();
            CodeBrixAudioOpus.Register();
            engine = VideoProcessors.Current;
            Check(engine != null && CodeBrixVideoProcessingAndroid.IsRegistered, "the Android engine is the current engine");
            Check(CodeBrixVideoPlaybackDav1d.IsRegistered, "Dav1d registered");
            Check(CodeBrixAudioOpus.IsRegistered, "Opus registered");
            Check(engine.Name == "android-mediacodec", "engine name android-mediacodec (got " + engine.Name + ")");
            highBitDepth = engine.Supports(VideoProcessorCapabilities.HighBitDepthFrameSource);
            hevc = engine.Supports(VideoProcessorCapabilities.HevcOutput);
            IReadOnlyList<string> p010 = CodeBrixVideoProcessingAndroid.Processor.HighBitDepthDecoders;
            return $"engine {engine.Name}, capabilities [{engine.Capabilities}]; HighBitDepthFrameSource {(highBitDepth ? "SET" : "not set")} (P010 decoders: {(p010.Count == 0 ? "none" : string.Join(", ", p010))}); HevcOutput {(hevc ? "SET" : "not set")}";
        });
        if (engine == null)
        {
            Report();
            return;
        }

        string mode1 = Out("mode1.webm");
        string mode2 = Out("mode2.cbv");
        string master8 = Out("master-8bit.cbvmaster");
        string masterSilent = Out("master-silent-alpha.cbvmaster");
        string master10 = Out("master-10bit.cbvmaster");
        string shaped = Out("shaped.cbv");
        string alphaMaster = Out(AlphaMasterName);
        bool mode1Ok = false, mode2Ok = false, master8Ok = false, silentOk = false, master10Ok = false, shapedOk = false;

        Step("02", "Author Mode1 (WebM + Opus) in process", () =>
        {
            VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, Out(ClipName), mode1);
            request.Audio.BitrateKilobitsPerSecond = 96;
            CheckNoFFmpeg(CbvAuthor.RenderCommands(request), "dry run");
            VideoAuthoringResult result = CbvAuthor.Write(request);
            CheckNoFFmpeg(result.Commands, "real run");
            Detail("02 command", result.Commands.Select(c => c.ToString()));
            Detail("02 note", result.Notes);
            Check(result.MatroskaMux != null, "an in-process Matroska mux summary");
            Check(result.PassesProfile, "passes the streamable profile");

            using MatroskaReader reader = new MatroskaReader(MediaSources.OpenFile(mode1));
            MediaTrackInfo video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Video);
            MediaTrackInfo audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio);
            Check(reader.DocType == "webm", "doc type webm (got " + reader.DocType + ")");
            Check(video != null && video.CodecId == VideoCodecIds.Av1, "an av01 video track");
            Check(audio != null && audio.CodecId == VideoCodecIds.Opus, "an opus audio track");
            Check(video.Width == 320 && video.Height == 240, "320x240 (got " + video.Width + "x" + video.Height + ")");
            Check(Math.Abs(reader.Duration.TotalSeconds - 4.0) < 0.15, "duration ~4 s (got " + reader.Duration + ")");
            Check(reader.CuesPrecedeFirstCluster, "cues before the first cluster");
            mode1Ok = true;
            return $"{new FileInfo(mode1).Length} bytes in {result.Elapsed.TotalSeconds:0.0} s; doc {reader.DocType}, {reader.Tracks.Count} tracks (video {video.CodecId} {video.Width}x{video.Height}, audio {audio.CodecId} {audio.SampleRate} Hz x{audio.Channels}), duration {reader.Duration.TotalSeconds:0.000} s, {reader.Cues.Count} cue points; RunsFFmpeg false on all {result.Commands.Count} passes";
        });

        Step("03", "Author Mode2 (CBVF + Vorbis) in process", () =>
        {
            VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, Out(ClipName), mode2);
            CheckNoFFmpeg(CbvAuthor.RenderCommands(request), "dry run");
            VideoAuthoringResult result = CbvAuthor.Write(request);
            CheckNoFFmpeg(result.Commands, "real run");
            Detail("03 command", result.Commands.Select(c => c.ToString()));
            Detail("03 note", result.Notes);

            CbvSummary cbv = ReadCbv(mode2);
            Check(cbv.Video != null && cbv.Video.CodecId == VideoCodecIds.Av1, "an av01 video track");
            Check(cbv.Audio != null && cbv.Audio.CodecId == VideoCodecIds.Vorbis, "a vorbis audio track");
            Check(cbv.Alpha == null, "no alpha plane");
            Check(Math.Abs(cbv.Duration.TotalSeconds - 4.0) < 0.15, "duration ~4 s (got " + cbv.Duration + ")");
            Check(result.Mux != null && result.Mux.VideoFrameCount == 120, "120 frames muxed (got " + result.Mux?.VideoFrameCount + ")");
            mode2Ok = true;
            return $"{new FileInfo(mode2).Length} bytes in {result.Elapsed.TotalSeconds:0.0} s; {cbv}";
        });

        Step("04", "Author Mode3 masters 8-bit (no alpha; Include + Silent)", () =>
        {
            VideoAuthoringRequest plain = NewRequest(VideoAuthoringFlavour.Master, Out(ClipName), master8);
            plain.Video.Alpha = AuthoringAlphaMode.Exclude;
            VideoAuthoringResult a = CbvAuthor.Write(plain);
            CheckNoFFmpeg(a.Commands, "plain master");
            Detail("04a command", a.Commands.Select(c => c.ToString()));
            Detail("04a note", a.Notes);
            CbvSummary p = ReadCbv(master8);
            Check(p.Version == 1, "format version 1 (got " + p.Version + ")");
            Check(p.Video != null && p.Video.CodecId == VideoCodecIds.Av1 && p.Video.BitDepth == 8, "an 8-bit av01 picture");
            Check(p.Audio != null && p.Audio.CodecId == VideoCodecIds.Flac, "a flac track");
            Check(p.Alpha == null && !p.IsSilent, "no alpha plane, not flagged silent");
            master8Ok = true;

            // The silent master carries an alpha plane (Include on a source without alpha = a fully opaque plane),
            // so that the export (step 10) composites it over a backdrop and takes the backdrop video's sound.
            VideoAuthoringRequest silent = NewRequest(VideoAuthoringFlavour.Master, Out(ClipName), masterSilent);
            silent.Video.Alpha = AuthoringAlphaMode.Include;
            silent.Audio.Silent = true;
            List<AuthoringCommand> dry = CbvAuthor.RenderCommands(silent).ToList();
            Check(dry.Any(c => c.Arguments.Contains("silent", StringComparison.OrdinalIgnoreCase)), "the dry run lists the silent audio pass");
            VideoAuthoringResult b = CbvAuthor.Write(silent);
            CheckNoFFmpeg(b.Commands, "silent master");
            Detail("04b command", b.Commands.Select(c => c.ToString()));
            Detail("04b note", b.Notes);
            CbvSummary s = ReadCbv(masterSilent);
            Check(s.Version == 1, "format version 1");
            Check(s.IsSilent, "flagged silent");
            Check(s.Audio == null, "no audio track");
            Check(s.Alpha != null && s.Alpha.BitDepth == 8, "an 8-bit alpha plane");
            Check(b.Mux != null && b.Mux.AlphaFrameCount == b.Mux.VideoFrameCount, "alpha frames = picture frames");
            silentOk = true;

            CbvSummary shipped = ReadCbv(alphaMaster);
            Check(shipped.Alpha != null && shipped.Audio != null, "the shipped host-authored master carries alpha and sound");
            return $"plain: {new FileInfo(master8).Length} bytes in {a.Elapsed.TotalSeconds:0.0} s, {p}; silent+alpha: {new FileInfo(masterSilent).Length} bytes in {b.Elapsed.TotalSeconds:0.0} s, {s}; shipped alpha master: {shipped}";
        });

        Step("05", "Author Mode3 master 10-bit", () =>
        {
            if (!highBitDepth)
            {
                return "NOTE: the engine does not report HighBitDepthFrameSource on this device; a 10-bit master is refused by design";
            }

            VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Master, Out(ClipName), master10);
            request.Video.Alpha = AuthoringAlphaMode.Exclude;
            request.Video.BitDepth = 10;
            VideoAuthoringResult result = CbvAuthor.Write(request);
            CheckNoFFmpeg(result.Commands, "10-bit master");
            Detail("05 command", result.Commands.Select(c => c.ToString()));
            Detail("05 note", result.Notes);
            Check(result.Commands.Any(c => c.Arguments.Contains("10-bit", StringComparison.Ordinal)), "the video pass ran at 10 bits");
            CbvSummary cbv = ReadCbv(master10);
            Check(cbv.Video != null && cbv.Video.BitDepth == 10, "the picture track reads back at 10 bits (got " + cbv.Video?.BitDepth + ")");
            Check(cbv.Audio != null && cbv.Audio.CodecId == VideoCodecIds.Flac, "a flac track");
            master10Ok = true;
            return $"{new FileInfo(master10).Length} bytes in {result.Elapsed.TotalSeconds:0.0} s; {cbv}";
        });

        Step("06", "Author with managed shaping (rotate, scale, fps, grade)", () =>
        {
            MediaInfo info = engine.Analyse(Out(RotatedClipName));
            Check(info.PrimaryVideo != null && info.PrimaryVideo.RotationDegrees != 0, "the engine reports the rotated clip's rotation (got " + info.PrimaryVideo?.RotationDegrees + ")");

            VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, Out(RotatedClipName), shaped);
            request.Video.FrameSize = AuthoringFrameSize.Exact(160, 90);
            request.Video.FrameRate = 15;
            request.Video.AutoRotate = true;
            request.Video.Luts.Add(new AuthoringLutInput(Out(LutName)));
            List<AuthoringCommand> dry = CbvAuthor.RenderCommands(request).ToList();
            CheckNoFFmpeg(dry, "dry run");
            Detail("06 dry run", dry.Select(c => c.ToString()));
            foreach (string word in new[] { "scale", "fps", "grade" })
            {
                Check(dry.Any(c => c.Arguments.StartsWith(word + " ", StringComparison.Ordinal) || c.Arguments.Contains(" " + word + " ", StringComparison.Ordinal) || c.Label.Contains(word, StringComparison.Ordinal)), "the dry run lists '" + word + "'");
            }

            VideoAuthoringResult result = CbvAuthor.Write(request);
            CheckNoFFmpeg(result.Commands, "real run");
            Detail("06 command", result.Commands.Select(c => c.ToString()));
            Detail("06 note", result.Notes);
            foreach (string word in new[] { "rotate", "scale", "fps", "grade" })
            {
                Check(result.Commands.Any(c => c.Arguments.StartsWith(word + " ", StringComparison.Ordinal) || c.Arguments.Contains(" " + word + " ", StringComparison.Ordinal)), "the real run lists '" + word + "'");
            }

            CbvSummary cbv = ReadCbv(shaped);
            Check(cbv.Video != null && cbv.Video.Width == 160 && cbv.Video.Height == 90, "160x90 (got " + cbv.Video?.Width + "x" + cbv.Video?.Height + ")");
            Check(result.Mux != null && result.Mux.VideoFrameCount == 60, "60 frames at 15 fps over 4 s (got " + result.Mux?.VideoFrameCount + ")");
            shapedOk = true;
            return $"source rotation {info.PrimaryVideo.RotationDegrees}; dry run lists scale/fps/grade (rotate is decided by the source on the real run), real run lists rotate/scale/fps/grade, no ffmpeg; {cbv}, {result.Mux.VideoFrameCount} frames, LUT '{result.ComposedLutTitle}'";
        });

        Step("07", "Export Mode1 -> MP4 (H.264)", () =>
        {
            Check(mode1Ok, "step 02 produced the Mode1 file");
            return Export(engine, mode1, Out("export-mode1.mp4"), VideoExportCodec.H264, null, VideoExportAudioSource.Ours, 120, null);
        });

        Step("08", "Export Mode2 -> MP4 (H.264)", () =>
        {
            Check(mode2Ok, "step 03 produced the Mode2 file");
            return Export(engine, mode2, Out("export-mode2.mp4"), VideoExportCodec.H264, null, VideoExportAudioSource.Ours, 120, null);
        });

        Step("09", "Export the alpha master over colour / PNG / backdrop MP4", () =>
        {
            string grey = Export(engine, alphaMaster, Out("export-alpha-over-grey.mp4"), VideoExportCodec.H264, null, VideoExportAudioSource.Ours, 12, null);
            string image = Export(engine, alphaMaster, Out("export-alpha-over-png.mp4"), VideoExportCodec.H264, ExportBackdrop.FromImage(Out(BackdropImageName)), VideoExportAudioSource.Ours, 12, null);
            string video = Export(engine, alphaMaster, Out("export-alpha-over-video.mp4"), VideoExportCodec.H264, ExportBackdrop.FromVideo(Out(BackdropVideoName)), VideoExportAudioSource.Ours, -1, 2.9);
            return "grey: " + grey + " | png: " + image + " | video: " + video;
        });

        Step("10", "Export the Silent master over the backdrop MP4", () =>
        {
            Check(silentOk, "step 04 produced the silent master");
            return Export(engine, masterSilent, Out("export-silent-over-video.mp4"), VideoExportCodec.H264, ExportBackdrop.FromVideo(Out(BackdropVideoName)), VideoExportAudioSource.Backdrop, 120, null);
        });

        Step("11", "Export with HEVC", () =>
        {
            if (!hevc)
            {
                return "NOTE: the engine does not report HevcOutput on this device (no HEVC encoder offered); H.264 only";
            }

            Check(mode2Ok, "step 03 produced the Mode2 file");
            return Export(engine, mode2, Out("export-mode2-hevc.mp4"), VideoExportCodec.Hevc, null, VideoExportAudioSource.Ours, 120, null);
        });

        Step("12", "Play back every authored file (headless session)", () =>
        {
            var lines = new List<string>();
            if (mode1Ok) lines.Add(Play("mode1", mode1, false, false, 8));
            if (mode2Ok) lines.Add(Play("mode2", mode2, false, false, 8));
            if (master8Ok) lines.Add(Play("master-8bit", master8, false, false, 8));
            if (silentOk) lines.Add(Play("master-silent-alpha", masterSilent, true, true, 8));
            if (master10Ok) lines.Add(Play("master-10bit", master10, false, false, 10));
            if (shapedOk) lines.Add(Play("shaped", shaped, false, false, 8));
            lines.Add(Play("shipped-alpha-master", alphaMaster, true, false, 8));
            Check(mode1Ok && mode2Ok && master8Ok && silentOk && shapedOk, "every authored file was there to play");
            if (highBitDepth) Check(master10Ok, "the 10-bit master was there to play");
            string text = string.Join(" | ", lines);
            return highBitDepth ? text : "NOTE: no 10-bit master on this device (no HighBitDepthFrameSource); " + text;
        });

        Step("13", "Cancellation + a refusal", () =>
        {
            string cancelled = Out("cancelled.cbv");
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, Out(ClipName), cancelled);
            request.CancellationToken = cancellation.Token;
            int progressCalls = 0;
            request.ProgressCallback = p =>
            {
                if (Interlocked.Increment(ref progressCalls) == 1) cancellation.CancelAfter(300);
            };
            bool threw = false;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                CbvAuthor.Write(request);
            }
            catch (System.OperationCanceledException)
            {
                threw = true;
            }

            watch.Stop();
            Check(threw, "the cancelled run threw OperationCanceledException");
            Check(!File.Exists(cancelled), "the part-written output was deleted");

            VideoAuthoringRequest refused = NewRequest(VideoAuthoringFlavour.WebMProfile, Out(ClipName), Out("refused.webm"));
            refused.Audio.Silent = true;
            string message = null;
            try
            {
                CbvAuthor.RenderCommands(refused);
            }
            catch (VideoAuthoringException ex)
            {
                message = ex.Message;
            }

            Check(message != null, "Silent on a Mode1 request is refused");
            Check(!File.Exists(Out("refused.webm")), "nothing written for the refused request");
            return $"cancelled after {watch.ElapsedMilliseconds} ms ({progressCalls} progress calls), output deleted; refusal: \"{message}\"";
        });

        Report();
    }

    private VideoAuthoringRequest NewRequest(VideoAuthoringFlavour flavour, string source, string output)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = _temp,
        };

        request.Video.Encoder = AuthoringVideoEncoder.Av1Package;
        request.Video.SpeedPreset = 8;
        request.Video.ConstantRateFactor = 40;
        request.Video.KeyframeIntervalFrames = 30;
        return request;
    }

    private static void CheckNoFFmpeg(IEnumerable<AuthoringCommand> commands, string what)
    {
        List<AuthoringCommand> list = commands.ToList();
        Check(list.Count > 0, what + ": at least one pass");
        AuthoringCommand ffmpeg = list.FirstOrDefault(c => c.RunsFFmpeg);
        Check(ffmpeg == null, what + ": no pass runs ffmpeg (found " + ffmpeg + ")");
    }

    private sealed class CbvSummary
    {
        internal ushort Version;
        internal bool IsSilent;
        internal TimeSpan Duration;
        internal MediaTrackInfo Video;
        internal MediaTrackInfo Alpha;
        internal MediaTrackInfo Audio;
        internal int TrackCount;

        public override string ToString() =>
            $"CBVF v{Version}{(IsSilent ? " SILENT" : "")}, {TrackCount} tracks: video {Video?.CodecId} {Video?.Width}x{Video?.Height} {Video?.BitDepth}-bit"
            + (Alpha == null ? "" : $", alpha {Alpha.CodecId} {Alpha.BitDepth}-bit")
            + (Audio == null ? ", no audio" : $", audio {Audio.CodecId} {Audio.SampleRate} Hz x{Audio.Channels}")
            + $", duration {Duration.TotalSeconds:0.000} s";
    }

    private static CbvSummary ReadCbv(string path)
    {
        using CbvReader reader = new CbvReader(MediaSources.OpenFile(path));
        Check(reader.HeaderChecksumVerified, "header checksum verified (" + Path.GetFileName(path) + ")");
        return new CbvSummary
        {
            Version = reader.Version,
            IsSilent = reader.IsSilent,
            Duration = reader.Duration,
            Video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Video && !t.IsAlphaPlane),
            Alpha = reader.AlphaPlaneTrack,
            Audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio),
            TrackCount = reader.Tracks.Count,
        };
    }

    private string Export(IVideoProcessor engine, string source, string output, VideoExportCodec codec, ExportBackdrop backdrop,
        VideoExportAudioSource expectedAudio, int expectedFrames, double? minimumSeconds)
    {
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = source,
            OutputPath = output,
            Codec = codec,
        };
        if (backdrop != null) request.Backdrop = backdrop;

        string tag = Path.GetFileNameWithoutExtension(output);
        Log("    " + tag + " dry run: " + VideoExporter.DryRun(request).Replace("\n", " / "));
        VideoExportResult result = VideoExporter.Run(request);
        Detail(tag + " note", result.Notes);
        Check(File.Exists(output) && new FileInfo(output).Length > 0, "the MP4 was written");
        Check(result.CodecUsed == codec, "codec used " + result.CodecUsed);
        Check(result.AudioSource == expectedAudio, "audio source " + expectedAudio + " (got " + result.AudioSource + ")");
        if (expectedFrames > 0) Check(result.FramesWritten == expectedFrames, expectedFrames + " frames (got " + result.FramesWritten + ")");
        if (minimumSeconds.HasValue) Check(result.Duration.TotalSeconds >= minimumSeconds.Value, "at least " + minimumSeconds + " s - the backdrop plays on (got " + result.Duration + ")");

        MediaInfo info = engine.Analyse(output);
        string wantCodec = codec == VideoExportCodec.Hevc ? "hevc" : "h264";
        Check(info.PrimaryVideo != null && info.PrimaryVideo.CodecName == wantCodec, "the engine reads back " + wantCodec + " (got " + info.PrimaryVideo?.CodecName + ")");
        Check(info.PrimaryVideo.Width == result.Width && info.PrimaryVideo.Height == result.Height, "the engine reads back the size");
        if (expectedAudio == VideoExportAudioSource.None) Check(info.PrimaryAudio == null, "no audio stream");
        else Check(info.PrimaryAudio != null && info.PrimaryAudio.CodecName == "aac", "an aac stream (got " + info.PrimaryAudio?.CodecName + ")");

        return $"{Path.GetFileName(output)} {new FileInfo(output).Length} bytes: {result.CodecUsed} {result.Width}x{result.Height} {result.FrameRate} {result.FramesWritten} frames {result.Duration.TotalSeconds:0.000} s, audio {result.AudioSource}, engine {result.EngineName}; read back {info.PrimaryVideo.CodecName}/{info.PrimaryAudio?.CodecName ?? "none"} {info.Duration.TotalSeconds:0.000} s";
    }

    private string Play(string label, string path, bool expectAlpha, bool expectSilent, int expectBitDepth)
    {
        const int wanted = 10;
        using VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        string failure = null;
        int ended = 0;
        session.MediaFailed += (s, e) => failure = e.Exception == null ? "media failed" : e.Exception.Message;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);
        session.Open(path);
        Check(session.HasAlpha == expectAlpha, label + ": HasAlpha " + expectAlpha + " (got " + session.HasAlpha + ")");
        Check(session.IsSilent == expectSilent, label + ": IsSilent " + expectSilent + " (got " + session.IsSilent + ")");
        session.Play();

        int frames = 0;
        int depth = 0;
        bool alphaOnFrames = true;
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30) && frames < wanted && failure == null)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame)
                {
                    frames++;
                    depth = frame.BitDepth;
                    if (expectAlpha && !frame.HasAlpha) alphaOnFrames = false;
                }
            }

            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;
            Thread.Sleep(2);
        }

        session.Stop();
        Check(failure == null, label + ": no media failure (" + failure + ")");
        Check(frames >= wanted, label + ": " + wanted + " frames decoded (got " + frames + ")");
        Check(depth == expectBitDepth, label + ": frames at " + expectBitDepth + " bits (got " + depth + ")");
        Check(alphaOnFrames, label + ": every frame carries its alpha plane");
        return $"{label}: {frames} frames, {depth}-bit, HasAlpha {session.HasAlpha}, IsSilent {session.IsSilent}";
    }

    private void Report()
    {
        string verdict = _failures.Count == 0 ? "PASS" : "FAIL";
        Log($"VAPROOF RESULT: {verdict}: {_passed} passed, {_notes.Count} passed with a note{(_notes.Count == 0 ? "" : " (" + string.Join(", ", _notes) + ")")}, {_failures.Count} failed{(_failures.Count == 0 ? "" : " (" + string.Join(", ", _failures) + ")")}");
    }
}
