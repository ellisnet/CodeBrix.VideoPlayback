using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Internal;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoProcessing;

// Both libraries have a Chapter type; the one this program means is the container's.
using Chapter = CodeBrix.VideoPlayback.Chapters.Chapter;

namespace CodeBrix.VideoPlayback.Authoring;

/// <summary>
/// Writes <c>.cbv</c> files, in either flavour, from one media file plus the text that rides along with it.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole front door. <see cref="RenderCommands" /> shows what would be run and touches nothing;
/// <see cref="Write" /> runs it, muxes where muxing is needed, checks the result against the streamable
/// profile and hands back everything it learned.
/// </para>
/// <para>
/// WHAT RUNS. The WebM-profile flavour is ONE FFmpeg pass: the picture, the sound and every caption file go
/// in together and FFmpeg's own WebM muxer writes the file with its seek index moved to the front. The
/// bespoke flavour is TWO FFmpeg passes into temporary files - the picture as an AV1 elementary stream in an
/// IVF wrapper, the sound as an Ogg stream - which the core's own muxer then turns into a <c>CBVF</c> file
/// together with the caption and chapter text. The temporary files are deleted whether the run succeeded or
/// failed.
/// </para>
/// <para>
/// WHAT IS TAKEN FROM THE SOURCE. Its picture and its sound - the first video and the first audio stream, or
/// FFmpeg's own choice with <c>SelectStreamsExplicitly</c> off. NEVER its own subtitle streams and NEVER its
/// own chapters, in either flavour: captions come from <c>Captions</c> and chapters from <c>ChaptersPath</c>,
/// and a source that carries text of its own earns a note in the result naming what was left behind and how
/// to bring it along. A chapter file always wins over the source's chapters.
/// </para>
/// <para>
/// WHAT HAS TO BE INSTALLED. FFmpeg, and nothing else. That is a rule of this program rather than an
/// accident: authoring a <c>.cbv</c> file must be possible with CodeBrix software plus the one encoder.
/// </para>
/// <para>
/// THIS IS A DEVELOPER-MACHINE LIBRARY. It launches a child process and it expects an encoder to be sitting
/// on the machine. It has no place inside a shipped application, and the packages an application needs to
/// PLAY what this writes do not include it.
/// </para>
/// </remarks>
public static class CbvAuthor
{
    /// <summary>Renders every command line a request would run, without running or writing anything.</summary>
    /// <param name="request">The request to render.</param>
    /// <returns>One command for a WebM-profile file, two for a bespoke one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="VideoAuthoringException">The request cannot be honoured as it stands.</exception>
    /// <exception cref="OperationCanceledException">
    /// <c>request.CancellationToken</c> was already cancelled when this was called. Nothing was rendered and
    /// nothing was touched.
    /// </exception>
    /// <remarks>
    /// Nothing is read from the disk and no file is written, so this works on a machine with no FFmpeg
    /// installed and with the source file absent. Where a colour-grade chain would be composed into one
    /// table, the command names the file that composing WOULD write - the temporary one, or the one
    /// <c>request.Video.ComposedLutPath</c> asks to keep - which is the same path a real run uses, so the
    /// two render identically. Where ONE table is used at full strength the command names that table, whether
    /// or not a kept path is set, because that is the file FFmpeg reads; the copy a kept path asks for
    /// appears only when <see cref="Write" /> runs.
    /// </remarks>
    public static IReadOnlyList<AuthoringCommand> RenderCommands(VideoAuthoringRequest request)
    {
        Validate(request, false);

        // A dry run starts no process and touches no disk, so one look at the token is all it can honour -
        // and all it needs to, because there is nothing here to interrupt.
        request.CancellationToken.ThrowIfCancellationRequested();

        string temporaryFolder = ResolveTemporaryFolder(request);
        ResolvedLutChain lut = LutChainResolver.Resolve(
            request.Video.Luts,
            request.OutputPath,
            temporaryFolder,
            false,
            null,
            request.Video.ComposedLutPath);

        if (request.Flavour == VideoAuthoringFlavour.WebMProfile)
        {
            return new[]
            {
                new AuthoringCommand("one pass", AuthoringCommandFactory.BuildWebMProfile(request, lut).Arguments),
            };
        }

        List<AuthoringCommand> commands = new List<AuthoringCommand>(2)
        {
            new AuthoringCommand(
                "video pass",
                AuthoringCommandFactory.BuildBespokeVideo(request, lut, IvfPathFor(request, temporaryFolder)).Arguments),
        };

        if (request.Audio.Include)
        {
            commands.Add(new AuthoringCommand(
                "audio pass",
                AuthoringCommandFactory.BuildBespokeAudio(request, OggPathFor(request, temporaryFolder)).Arguments));
        }

        return commands;
    }

    /// <summary>Authors one file.</summary>
    /// <param name="request">What to write and how.</param>
    /// <returns>The file, the commands that made it, the profile report and any notes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <exception cref="VideoAuthoringException">
    /// The request cannot be honoured, FFmpeg is not installed, an encode failed, or the finished file does
    /// not pass the streamable profile and the request asked to be told so.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <c>request.CancellationToken</c> was cancelled. The partly written output file has been deleted and so
    /// have the intermediate files; an effective colour table the request asked to keep is left where it is,
    /// because it was written whole before any encoding began.
    /// </exception>
    public static VideoAuthoringResult Write(VideoAuthoringRequest request)
    {
        Validate(request, true);

        CancellationToken cancellationToken = request.CancellationToken;

        // Before the tool check, so a request cancelled before it started never even looks for FFmpeg.
        cancellationToken.ThrowIfCancellationRequested();
        AuthoringTools.Verify();

        Stopwatch clock = Stopwatch.StartNew();
        string temporaryFolder = ResolveTemporaryFolder(request);
        List<string> notes = new List<string>();
        List<AuthoringCommand> commands = new List<AuthoringCommand>(2);
        List<string> temporaryFiles = new List<string>(3);

        // One ffprobe of the source, for the text this library does NOT carry: the source's own subtitle
        // streams and chapters. The run says what it left behind rather than letting the caller find out
        // from the finished file.
        cancellationToken.ThrowIfCancellationRequested();
        SourceTextSurvey survey = SurveySourceText(request, notes);
        NoteUncarriedSourceText(request, survey, notes);

        Directory.CreateDirectory(temporaryFolder);
        string outputFolder = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputFolder)) Directory.CreateDirectory(outputFolder);

        CbvAuthoringResult mux = null;
        ResolvedLutChain lut = ResolvedLutChain.None;

        try
        {
            lut = LutChainResolver.Resolve(
                request.Video.Luts,
                request.OutputPath,
                temporaryFolder,
                true,
                notes,
                request.Video.ComposedLutPath);

            if (!string.IsNullOrEmpty(lut.TemporaryPath)) temporaryFiles.Add(lut.TemporaryPath);

            cancellationToken.ThrowIfCancellationRequested();

            if (request.Flavour == VideoAuthoringFlavour.WebMProfile)
            {
                NoteCollapsedChapterTitles(request, notes);
                NoteDroppedCaptionFlags(request, notes);

                FFMpegArgumentProcessor pass = AuthoringCommandFactory.BuildWebMProfile(request, lut);
                commands.Add(new AuthoringCommand("one pass", pass.Arguments));
                Run(pass, request, "one pass", 1, 1);
            }
            else
            {
                string ivf = IvfPathFor(request, temporaryFolder);
                string ogg = OggPathFor(request, temporaryFolder);
                temporaryFiles.Add(ivf);
                int passCount = request.Audio.Include ? 2 : 1;

                FFMpegArgumentProcessor videoPass = AuthoringCommandFactory.BuildBespokeVideo(request, lut, ivf);
                commands.Add(new AuthoringCommand("video pass", videoPass.Arguments));
                Run(videoPass, request, "video pass", 1, passCount);

                if (request.Audio.Include)
                {
                    temporaryFiles.Add(ogg);
                    FFMpegArgumentProcessor audioPass = AuthoringCommandFactory.BuildBespokeAudio(request, ogg);
                    commands.Add(new AuthoringCommand("audio pass", audioPass.Arguments));
                    Run(audioPass, request, "audio pass", 2, passCount);
                }

                // The mux is managed code over two finished files and takes a fraction of a second, so it is
                // not interrupted part-way; it is only started when the token still allows it.
                cancellationToken.ThrowIfCancellationRequested();
                mux = Mux(request, ivf, request.Audio.Include ? ogg : null);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // A cancelled run leaves a partly written file behind, and a partly written video file is worse
            // than no file at all: it looks playable. The intermediate files go in the finally below, exactly
            // as they do after a successful run and after a failed one.
            DeleteQuietly(request.OutputPath);
            throw;
        }
        finally
        {
            foreach (string file in temporaryFiles) DeleteQuietly(file);
        }

        StreamableProfileReport profile = null;
        if (request.ValidateProfile)
        {
            profile = StreamableProfile.EvaluateFile(request.OutputPath);

            if (!profile.Passes && request.FailWhenProfileFails)
            {
                throw new VideoAuthoringException(BuildProfileFailureMessage(request.OutputPath, profile));
            }
        }

        clock.Stop();

        return new VideoAuthoringResult(
            request.OutputPath,
            new FileInfo(request.OutputPath).Length,
            request.Flavour,
            commands,
            profile,
            mux,
            notes,
            clock.Elapsed,
            lut.WasComposed ? lut.Title : null,
            lut.WasComposed ? lut.Size : 0,
            lut.KeptPath);
    }

    /// <summary>Reports whether the one tool authoring needs is installed.</summary>
    /// <param name="problem">
    /// What is missing and where it was looked for, or an empty string when nothing is missing.
    /// </param>
    /// <returns>True when <c>ffmpeg</c> and <c>ffprobe</c> can both be run.</returns>
    public static bool TryVerifyTools(out string problem) => AuthoringTools.TryVerify(out problem);

    /// <summary>Throws unless the one tool authoring needs is installed.</summary>
    /// <exception cref="VideoAuthoringException">
    /// <c>ffmpeg</c> or <c>ffprobe</c> could not be run; the message names both and says where they were
    /// looked for.
    /// </exception>
    public static void VerifyTools() => AuthoringTools.Verify();

    private static void Run(
        FFMpegArgumentProcessor processor,
        VideoAuthoringRequest request,
        string label,
        int passNumber,
        int passCount)
    {
        if (request.ProgressCallback != null && request.SourceDuration > TimeSpan.Zero)
        {
            int lastReported = -1;

            processor.NotifyOnProgress(
                percent =>
                {
                    int whole = (int)percent;
                    if (whole <= lastReported) return;
                    lastReported = whole;
                    request.ProgressCallback(new AuthoringProgress(label, passNumber, passCount, whole));
                },
                request.SourceDuration);
        }

        // The default timeout of zero KILLS the child outright instead of asking it to quit tidily first.
        // That is deliberate: FFmpeg's graceful "q" quit hangs on the machine this library is developed on,
        // and the half-written file is being thrown away anyway, so there is nothing a tidy finish protects.
        processor.CancellableThrough(request.CancellationToken);

        try
        {
            processor.ProcessSynchronously();
        }
        catch (Exception ex)
        {
            // A cancelled pass comes back looking like any other failed process, because that is what a
            // killed process is. The token is the authority on which it was.
            request.CancellationToken.ThrowIfCancellationRequested();

            throw new VideoAuthoringException(
                "The " + label + " failed. The command was: ffmpeg " + processor.Arguments, ex);
        }

        // And a pass that was killed cleanly enough to report success is still a cancelled pass.
        request.CancellationToken.ThrowIfCancellationRequested();
    }

    private static CbvAuthoringResult Mux(VideoAuthoringRequest request, string ivfPath, string oggPath)
    {
        CbvAuthoringRequest muxRequest = new CbvAuthoringRequest
        {
            OutputPath = request.OutputPath,
            VideoIvfPath = ivfPath,
            AudioOggPath = oggPath,
            ChaptersPath = request.ChaptersPath,
            AudioLanguage = request.Audio.Language,
            AudioName = request.Audio.Name,
            VideoName = request.Video.TrackName,
        };

        foreach (AuthoringCaptionInput caption in request.Captions)
        {
            muxRequest.Captions.Add(new CbvCaptionInput(caption.Path, caption.Language, caption.Name, caption.Flags));
        }

        try
        {
            return CbvAuthoring.Write(muxRequest);
        }
        catch (VideoAuthoringException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VideoAuthoringException(
                "The two encoded streams could not be muxed into '" + request.OutputPath + "': " + ex.Message,
                ex);
        }
    }

    // ffmpeg's Matroska muxer writes ONE untagged ChapterDisplay per chapter, so a chapter file that names a
    // title per language loses all but the untagged one in the WebM-profile flavour. That is a real
    // limitation of the container path and it is reported rather than hidden; the bespoke flavour keeps
    // every language, and the two end-to-end tests assert exactly that difference.
    private static void NoteCollapsedChapterTitles(VideoAuthoringRequest request, IList<string> notes)
    {
        if (string.IsNullOrWhiteSpace(request.ChaptersPath)) return;

        List<string> languages = new List<string>();

        foreach (Chapter chapter in FfMetadataChapters.ReadFile(request.ChaptersPath))
        {
            foreach (KeyValuePair<string, string> title in chapter.Titles)
            {
                if (title.Key.Length == 0) continue;
                if (!languages.Contains(title.Key)) languages.Add(title.Key);
            }
        }

        if (languages.Count == 0) return;

        notes.Add(
            "chapter titles in " + string.Join(", ", languages) + " were DROPPED: FFmpeg's Matroska muxer writes "
            + "one untagged chapter title per chapter, so the WebM-profile flavour keeps only the untagged "
            + "'title=' value. The bespoke flavour authored from the same chapter file keeps every language.");
    }

    // A WebM document has no FlagHearingImpaired element - Matroska gained one, WebM's element list never
    // did - so ffmpeg's webm muxer silently drops the disposition even though it is on the command line. The
    // default and forced flags DO survive. Found and measured 2026-08-29; reported rather than hidden, and
    // the bespoke flavour keeps all three.
    private static void NoteDroppedCaptionFlags(VideoAuthoringRequest request, IList<string> notes)
    {
        List<string> tracks = new List<string>();

        for (int i = 0; i < request.Captions.Count; i++)
        {
            AuthoringCaptionInput caption = request.Captions[i];
            if ((caption.Flags & CaptionTrackFlags.HearingImpaired) == 0) continue;

            tracks.Add(caption.Name.Length > 0 ? caption.Name : caption.Language);
        }

        if (tracks.Count == 0) return;

        notes.Add(
            "the hearing-impaired flag on caption track(s) " + string.Join(", ", tracks) + " was DROPPED: a WebM "
            + "document has no element for it, so FFmpeg's WebM muxer writes the default and forced flags and "
            + "not that one. Author the bespoke flavour, or set Container to Matroska, to keep it.");
    }

    // What a probe of the source found of the text this library does NOT carry. Captions come from the
    // request's Captions inputs and chapters from ChaptersPath, in BOTH flavours; a source's own subtitle
    // streams and chapters are left behind. Measured 2026-09-02 on a Matroska source with a subrip track and
    // two chapters: the WebM-profile pass dropped the track and - until -map_chapters was named - carried the
    // chapters with their titles stripped; the bespoke passes carried neither. Now both flavours agree (the
    // command factory keeps a source's chapters out unconditionally), and the run says so.
    private sealed class SourceTextSurvey
    {
        public bool Probed;
        public List<string> SubtitleStreams = new List<string>();
        public int ChapterCount;
    }

    private static SourceTextSurvey SurveySourceText(VideoAuthoringRequest request, IList<string> notes)
    {
        SourceTextSurvey survey = new SourceTextSurvey();

        IMediaAnalysis analysis;
        try
        {
            analysis = FFProbe.Analyse(request.SourcePath);
        }
        catch (Exception exception)
        {
            // The encode is the judge of whether the source can be read at all. A probe that fails costs the
            // caller this one piece of information, and the note says so.
            notes.Add(
                "the source could not be probed for subtitle streams and chapters of its own ("
                + FirstLine(exception.Message) + "); if it carries any, they were not carried.");
            return survey;
        }

        survey.Probed = true;
        survey.ChapterCount = analysis.Chapters != null ? analysis.Chapters.Count : 0;

        if (analysis.SubtitleStreams != null)
        {
            foreach (SubtitleStream stream in analysis.SubtitleStreams)
            {
                string description = "#" + stream.Index.ToString(CultureInfo.InvariantCulture) + " "
                    + (string.IsNullOrEmpty(stream.CodecName) ? "unknown" : stream.CodecName);
                if (!string.IsNullOrEmpty(stream.Language)) description += " (" + stream.Language + ")";
                survey.SubtitleStreams.Add(description);
            }
        }

        return survey;
    }

    private static void NoteUncarriedSourceText(
        VideoAuthoringRequest request,
        SourceTextSurvey survey,
        IList<string> notes)
    {
        if (!survey.Probed) return;

        if (survey.SubtitleStreams.Count > 0)
        {
            notes.Add(
                "the source's own subtitle stream(s) " + string.Join(", ", survey.SubtitleStreams) + " were NOT "
                + "carried: this library encodes a source's picture and sound and takes captions only from the "
                + "request's Captions inputs. To keep a text track, extract it to WebVTT (ffmpeg -i <source> "
                + "-map 0:<index> -c:s webvtt <track>.vtt) and add it as an AuthoringCaptionInput; an "
                + "image-based track has no text form and cannot be carried at all.");
        }

        if (survey.ChapterCount == 0) return;

        string count = survey.ChapterCount.ToString(CultureInfo.InvariantCulture) + " chapter(s)";
        if (string.IsNullOrWhiteSpace(request.ChaptersPath))
        {
            notes.Add(
                "the source's own " + count + " were NOT carried: chapters come only from ChaptersPath. To keep "
                + "them, export them to a chapter file (ffmpeg -i <source> -f ffmetadata <chapters>.txt) and "
                + "pass it as ChaptersPath.");
        }
        else
        {
            int fromFile = FfMetadataChapters.ReadFile(request.ChaptersPath).Count;
            notes.Add(
                "the source's own " + count + " were REPLACED by the "
                + fromFile.ToString(CultureInfo.InvariantCulture) + " chapter(s) in '" + request.ChaptersPath
                + "': chapters come only from ChaptersPath.");
        }
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int newline = text.IndexOfAny(new[] { '\r', '\n' });
        return newline < 0 ? text : text.Substring(0, newline);
    }

    private static string BuildProfileFailureMessage(string path, StreamableProfileReport profile)
    {
        List<string> failures = new List<string>();
        foreach (StreamableProfileRule rule in profile.FailedRules()) failures.Add(rule.ToString());

        return "'" + path + "' was written but does not pass the streamable profile: "
            + string.Join("; ", failures)
            + ". Set FailWhenProfileFails to false to author a file that deliberately misses a rule.";
    }

    private static void Validate(VideoAuthoringRequest request, bool forRunning)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new VideoAuthoringException("The request states no source file to encode from.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new VideoAuthoringException("The request states no output path to write to.");
        }

        if (forRunning && !File.Exists(request.SourcePath))
        {
            throw new VideoAuthoringException("There is no source file at '" + request.SourcePath + "'.");
        }

        string audioEncoder = request.Audio.Include
            ? AuthoringCommandFactory.AudioEncoderNameFor(request.Audio.Codec, request.Flavour)
            : null;

        bool opus = string.Equals(audioEncoder, AuthoringEncoderNames.LibOpus, StringComparison.Ordinal);

        // THE BESPOKE FILE'S REASON TO EXIST. A ".cbv" in the bespoke flavour must play with
        // CodeBrix.VideoPlayback - whose CodeBrix.Audio dependency has Vorbis built in - plus a video decoder
        // package, and nothing else. Opus would need a third package on the playing machine, so it never goes
        // into a bespoke file through this surface. This refusal is UNCONDITIONAL: it is not what
        // RequireNoExtraPlaybackPackages is for, and that switch stays what it was - an opt-in for
        // WebM-profile authors who want Vorbis-only output. Opus itself is fully supported, and is the
        // default, in the WebM-profile flavour.
        if (opus && request.Flavour == VideoAuthoringFlavour.Bespoke)
        {
            throw new VideoAuthoringException(
                "The request asks for Opus audio in a bespoke '.cbv' file. A bespoke file has to play with "
                + "CodeBrix.VideoPlayback and a video decoder package and NOTHING else, and Opus needs the "
                + "application to reference CodeBrix.Audio.Opus and call CodeBrixAudioOpus.Register(); Vorbis "
                + "plays with the core package alone. Choose AuthoringAudioCodec.LibVorbis, which is what the "
                + "bespoke flavour uses by default, or author VideoAuthoringFlavour.WebMProfile, where Opus is "
                + "the default and is fully supported.");
        }

        if (request.Audio.Include && request.RequireNoExtraPlaybackPackages && opus)
        {
            throw new VideoAuthoringException(
                "The request asks for Opus audio and also asks that the finished file need no extra package to "
                + "play. Opus needs the application to reference CodeBrix.Audio.Opus and call "
                + "CodeBrixAudioOpus.Register(); Vorbis needs neither. Choose AuthoringAudioCodec.LibVorbis, or "
                + "clear RequireNoExtraPlaybackPackages.");
        }

        // libvorbis opens only inside a BAND of bit rates that depends on BOTH the sample rate and the
        // channel count, and it refuses at setup - so a request naming a bit rate outside that band would die
        // part-way through the encode with FFmpeg's own message. The bands are measured rather than assumed;
        // see VorbisBitrateBands. Above 48 kHz the bit-rate mode mostly does not open at all, which is the
        // third refusal below. The quality path is untouched by every one of them: -q:a has no band.
        if (request.Audio.Include
            && !request.Audio.VorbisQuality.HasValue
            && string.Equals(audioEncoder, AuthoringEncoderNames.LibVorbis, StringComparison.Ordinal)
            && VorbisBitrateBands.TryGetBand(
                request.Audio.SampleRateHz, request.Audio.Channels, out int floor, out int ceiling))
        {
            string asked = request.Audio.BitrateKilobitsPerSecond.ToString(CultureInfo.InvariantCulture);
            string rate = request.Audio.SampleRateHz.ToString(CultureInfo.InvariantCulture);
            string channels = request.Audio.Channels.ToString(CultureInfo.InvariantCulture);

            if (floor == 0)
            {
                // Measured, and libvorbis accepted no bit rate whatsoever there. This is a statement about
                // the SAMPLE RATE rather than about the number asked for, so the message says so and does not
                // suggest a different bit rate - there isn't one.
                throw new VideoAuthoringException(
                    "The request asks libvorbis for " + asked + " kbit/s at " + rate + " Hz in " + channels
                    + " channel(s), and libvorbis's bit-rate mode does not open at that sample rate at all - "
                    + "no bit rate in this library's whole range was accepted there - so the encode would fail "
                    + "as the encoder was being set up. Set VorbisQuality to rate-control by quality instead, "
                    + "which does open at every sample rate measured, or choose a sample rate of 48000 Hz or "
                    + "below.");
            }

            if (request.Audio.BitrateKilobitsPerSecond < floor)
            {
                throw new VideoAuthoringException(
                    "The request asks libvorbis for " + asked + " kbit/s at " + rate + " Hz in " + channels
                    + " channel(s), and libvorbis will not open below "
                    + floor.ToString(CultureInfo.InvariantCulture)
                    + " kbit/s for that rate and channel count - the encode would fail as the encoder was being "
                    + "set up. Ask for " + floor.ToString(CultureInfo.InvariantCulture)
                    + " kbit/s or more, set VorbisQuality to rate-control by quality instead, which has no such "
                    + "floor, or use fewer channels.");
            }

            if (request.Audio.BitrateKilobitsPerSecond > ceiling)
            {
                throw new VideoAuthoringException(
                    "The request asks libvorbis for " + asked + " kbit/s at " + rate + " Hz in " + channels
                    + " channel(s), and libvorbis opens only between "
                    + floor.ToString(CultureInfo.InvariantCulture) + " and "
                    + ceiling.ToString(CultureInfo.InvariantCulture)
                    + " kbit/s for that rate and channel count - the encode would fail as the encoder was being "
                    + "set up. Ask for " + ceiling.ToString(CultureInfo.InvariantCulture)
                    + " kbit/s or less, set VorbisQuality to rate-control by quality instead, which has no such "
                    + "ceiling, or use a higher sample rate.");
            }
        }

        if (request.Audio.Language != null
            && request.Audio.Language.Length > 0
            && !BcpLanguageTag.IsWellFormed(request.Audio.Language))
        {
            throw new VideoAuthoringException(
                "'" + request.Audio.Language + "' is not a well-formed BCP 47 language tag for the audio track. "
                + "A tag looks like 'en', 'en-GB' or 'zh-Hant-TW' - letters and digits separated by hyphens, "
                + "never an underscore.");
        }

        for (int i = 0; i < request.Captions.Count; i++)
        {
            AuthoringCaptionInput caption = request.Captions[i];

            if (caption == null)
            {
                throw new VideoAuthoringException(
                    "Caption track " + i.ToString(CultureInfo.InvariantCulture) + " is null.");
            }

            if (caption.Language.Length == 0 || !BcpLanguageTag.IsWellFormed(caption.Language))
            {
                throw new VideoAuthoringException(
                    "Caption track " + i.ToString(CultureInfo.InvariantCulture) + " ('" + caption.Path + "') has "
                    + (caption.Language.Length == 0 ? "no language tag" : "the language tag '" + caption.Language + "'")
                    + ". Every caption track needs a well-formed BCP 47 tag - 'en', 'en-GB', 'zh-Hant-TW' - "
                    + "because that is how a player's subtitle menu names it.");
            }

            if (request.Flavour == VideoAuthoringFlavour.WebMProfile && !caption.IsWebVtt)
            {
                throw new VideoAuthoringException(
                    "Caption track " + i.ToString(CultureInfo.InvariantCulture) + " ('" + caption.Path + "') is not "
                    + "a '.vtt' file. The WebM-profile flavour copies caption tracks into the container "
                    + "unaltered, and a WebM document carries WebVTT only. Convert it, or author the bespoke "
                    + "flavour, which reads SubRip too.");
            }

            if (forRunning && !File.Exists(caption.Path))
            {
                throw new VideoAuthoringException("There is no caption file at '" + caption.Path + "'.");
            }
        }

        if (forRunning
            && !string.IsNullOrWhiteSpace(request.ChaptersPath)
            && !File.Exists(request.ChaptersPath))
        {
            throw new VideoAuthoringException("There is no chapter file at '" + request.ChaptersPath + "'.");
        }
    }

    private static string ResolveTemporaryFolder(VideoAuthoringRequest request) =>
        string.IsNullOrWhiteSpace(request.TemporaryFolder) ? Path.GetTempPath() : request.TemporaryFolder;

    private static string IvfPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".video.ivf");

    private static string OggPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".audio.ogg");

    private static string BaseNameFor(VideoAuthoringRequest request) =>
        string.IsNullOrWhiteSpace(request.OutputPath)
            ? "authoring"
            : Path.GetFileNameWithoutExtension(request.OutputPath);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary file that cannot be deleted is not a reason to lose an encode; the folder is the
            // system's temporary one and it will be swept.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
