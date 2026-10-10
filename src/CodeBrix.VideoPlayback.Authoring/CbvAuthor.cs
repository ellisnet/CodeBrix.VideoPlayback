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
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Exceptions;

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
/// The master flavour (<c>.cbvmaster</c>, Mode3) is the bespoke flavour's video pass unchanged, an ALPHA pass
/// when the source has an alpha channel (a monochrome AV1 stream whose key frames are forced onto the video
/// pass's), and a lossless FLAC audio pass whose frames are split out by managed code - then the same muxer.
/// The two video streams are checked for lock step before they are muxed.
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

        List<AuthoringCommand> commands = new List<AuthoringCommand>(3)
        {
            new AuthoringCommand(
                "video pass",
                AuthoringCommandFactory.BuildBespokeVideo(request, lut, IvfPathFor(request, temporaryFolder)).Arguments),
        };

        if (request.Flavour == VideoAuthoringFlavour.Master)
        {
            // Whether an Auto request carries alpha depends on the source, which a dry run does not read - so
            // the alpha pass is listed unless the request rules it out, and its key-frame list, which only the
            // video pass can produce, is shown as a placeholder.
            if (request.Video.Alpha != AuthoringAlphaMode.Exclude)
            {
                commands.Add(new AuthoringCommand(
                    "alpha pass",
                    AuthoringCommandFactory.BuildMasterAlpha(
                        request,
                        AlphaIvfPathFor(request, temporaryFolder),
                        AuthoringCommandFactory.KeyFramesOfVideoPass).Arguments));
            }

            if (request.Audio.Include)
            {
                commands.Add(new AuthoringCommand(
                    "audio pass",
                    AuthoringCommandFactory.BuildMasterAudio(request, FlacPathFor(request, temporaryFolder)).Arguments));
            }

            return commands;
        }

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
                Run(pass, request, "one pass", 1, 1, commands, notes);
            }
            else if (request.Flavour == VideoAuthoringFlavour.Master)
            {
                mux = WriteMaster(request, lut, survey, temporaryFolder, temporaryFiles, commands, notes);
            }
            else
            {
                string ivf = IvfPathFor(request, temporaryFolder);
                string ogg = OggPathFor(request, temporaryFolder);
                temporaryFiles.Add(ivf);
                int passCount = request.Audio.Include ? 2 : 1;

                FFMpegArgumentProcessor videoPass = AuthoringCommandFactory.BuildBespokeVideo(request, lut, ivf);
                Run(videoPass, request, "video pass", 1, passCount, commands, notes);

                if (request.Audio.Include)
                {
                    temporaryFiles.Add(ogg);
                    FFMpegArgumentProcessor audioPass = AuthoringCommandFactory.BuildBespokeAudio(request, ogg);
                    Run(audioPass, request, "audio pass", 2, passCount, commands, notes);
                }

                // The mux is managed code over two finished files and takes a fraction of a second, so it is
                // not interrupted part-way; it is only started when the token still allows it.
                cancellationToken.ThrowIfCancellationRequested();
                mux = Mux(request, ivf, request.Audio.Include ? ogg : null);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (VideoAuthoringException) when (request.Flavour == VideoAuthoringFlavour.Master)
        {
            // A master run can fail AFTER the muxer has created the output - a lock-step refusal - and an
            // empty or half-made file must not be left looking like a result.
            DeleteQuietly(request.OutputPath);
            throw;
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
    /// <remarks>
    /// This checks the binaries only. The overload that also hands back <c>warnings</c> checks the encoders
    /// in the build as well.
    /// </remarks>
    public static bool TryVerifyTools(out string problem) => AuthoringTools.TryVerify(out problem);

    /// <summary>
    /// Reports whether the one tool authoring needs is installed, and warns about any encoder this library can
    /// ask for that the installed build does not have.
    /// </summary>
    /// <param name="problem">
    /// What is missing and where it was looked for, or an empty string when nothing is missing.
    /// </param>
    /// <param name="warnings">
    /// One sentence per missing encoder, saying what will fail and what to do about it - most importantly
    /// when the default AV1 encoder, SVT-AV1 (<c>libsvtav1</c>), is absent, which is when
    /// <see cref="VideoAuthoringRequest.AllowAv1EncoderFallback" /> matters. Empty when every encoder is
    /// there, and empty when <paramref name="problem" /> is set, because nothing was asked then.
    /// </param>
    /// <returns>
    /// True when <c>ffmpeg</c> and <c>ffprobe</c> can both be run. A missing encoder is a WARNING and never
    /// makes this false: a machine without SVT-AV1 can still author with libaom.
    /// </returns>
    public static bool TryVerifyTools(out string problem, out IReadOnlyList<string> warnings) =>
        AuthoringTools.TryVerify(out problem, out warnings);

    /// <summary>Throws unless the one tool authoring needs is installed.</summary>
    /// <exception cref="VideoAuthoringException">
    /// <c>ffmpeg</c> or <c>ffprobe</c> could not be run; the message names both and says where they were
    /// looked for.
    /// </exception>
    /// <remarks>
    /// This checks the binaries only. The overload that also hands back <c>warnings</c> checks the encoders
    /// in the build as well.
    /// </remarks>
    public static void VerifyTools() => AuthoringTools.Verify();

    /// <summary>
    /// Throws unless the one tool authoring needs is installed, and warns about any encoder this library can
    /// ask for that the installed build does not have.
    /// </summary>
    /// <param name="warnings">
    /// One sentence per missing encoder, saying what will fail and what to do about it - most importantly
    /// when the default AV1 encoder, SVT-AV1 (<c>libsvtav1</c>), is absent. Empty when every encoder is there.
    /// </param>
    /// <exception cref="VideoAuthoringException">
    /// <c>ffmpeg</c> or <c>ffprobe</c> could not be run; the message names both and says where they were
    /// looked for. A missing ENCODER never throws: it is a warning.
    /// </exception>
    public static void VerifyTools(out IReadOnlyList<string> warnings) => AuthoringTools.Verify(out warnings);

    /// <summary>
    /// The message a failed FFmpeg pass is reported with: which pass failed, an explanation when FFmpeg did
    /// not have an encoder the pass asked for, and the command line.
    /// </summary>
    /// <param name="label">The pass - "one pass", "video pass" or "audio pass".</param>
    /// <param name="arguments">The arguments the pass ran with.</param>
    /// <param name="fallbackOn">Whether the AV1 encoder fallback was on for the pass, by request or globally.</param>
    /// <param name="failure">What the pass threw.</param>
    /// <returns>
    /// "The video pass failed. The command was: ffmpeg ..." - with, between the two sentences, the explanation
    /// of a missing encoder when <paramref name="failure" /> or anything inside it is an
    /// <see cref="FFMpegEncoderNotFoundException" />.
    /// </returns>
    internal static string DescribePassFailure(string label, string arguments, bool fallbackOn, Exception failure)
    {
        string explanation = ExplainMissingEncoder(FindMissingEncoder(failure), fallbackOn);

        return "The " + label + " failed. "
            + (explanation.Length == 0 ? string.Empty : explanation + " ")
            + "The command was: ffmpeg " + arguments;
    }

    private static void Run(
        FFMpegArgumentProcessor processor,
        VideoAuthoringRequest request,
        string label,
        int passNumber,
        int passCount,
        List<AuthoringCommand> commands,
        List<string> notes)
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

        // The request can turn the AV1 encoder fallback ON for its own passes. It never turns it OFF: a process
        // that switched it on through GlobalFFOptions keeps it, because this only adds to the run's options.
        if (request.AllowAv1EncoderFallback)
        {
            processor.Configure(options => options.AllowAv1EncoderFallback = true);
        }

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
                DescribePassFailure(label, processor.Arguments, IsAv1EncoderFallbackOn(request), ex), ex);
        }

        // And a pass that was killed cleanly enough to report success is still a cancelled pass.
        request.CancellationToken.ThrowIfCancellationRequested();

        // Recorded AFTER the pass, from the processor itself. When the AV1 encoder fallback rewrote the line
        // before FFmpeg started, this is the line that really ran - not the one the request rendered.
        commands.Add(new AuthoringCommand(label, processor.Arguments));

        foreach (string note in processor.Av1EncoderFallbackNotes)
        {
            notes.Add(label + ": " + note);
        }
    }

    private static bool IsAv1EncoderFallbackOn(VideoAuthoringRequest request) =>
        request.AllowAv1EncoderFallback || GlobalFFOptions.Current.AllowAv1EncoderFallback;

    private static FFMpegEncoderNotFoundException FindMissingEncoder(Exception failure)
    {
        for (Exception current = failure; current != null; current = current.InnerException)
        {
            if (current is FFMpegEncoderNotFoundException missing) return missing;
        }

        return null;
    }

    // What the video-processing wrapper's own failure message has always begun with. Anything IN FRONT of it
    // in an FFMpegEncoderNotFoundException is the wrapper's explanation of the missing encoder.
    private const string HistoricalFFMpegFailureText = "ffmpeg exited with non-zero exit-code";

    private static string ExplainMissingEncoder(FFMpegEncoderNotFoundException missing, bool fallbackOn)
    {
        if (missing == null) return string.Empty;

        if (!string.Equals(missing.EncoderName, AuthoringEncoderNames.LibSvtAv1, StringComparison.Ordinal))
        {
            return "The ffmpeg being used has no '" + missing.EncoderName + "' encoder. Install an ffmpeg build "
                + "that includes it, or ask the request for an encoder this build has.";
        }

        if (!fallbackOn) return AuthoringTools.MissingSvtAv1 + " " + AuthoringTools.SvtAv1Remedies;

        // The fallback was on and still could not help - in practice because this build has no libaom-av1
        // either. The wrapper's own explanation names the reason, and it is everything before the text its
        // failure message has always carried.
        int historical = missing.Message.IndexOf(HistoricalFFMpegFailureText, StringComparison.Ordinal);
        string wrapperExplanation = historical > 0 ? missing.Message.Substring(0, historical).Trim() : string.Empty;

        return wrapperExplanation.Length > 0
            ? wrapperExplanation
            : AuthoringTools.MissingSvtAv1 + " " + AuthoringTools.FallbackCouldNotHelp;
    }

    private static CbvAuthoringRequest BuildMuxRequest(VideoAuthoringRequest request, string ivfPath, string oggPath)
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

        return muxRequest;
    }

    private static CbvAuthoringResult Mux(VideoAuthoringRequest request, string ivfPath, string oggPath)
    {
        CbvAuthoringRequest muxRequest = BuildMuxRequest(request, ivfPath, oggPath);

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
        public string PixelFormat;
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
        survey.PixelFormat = analysis.PrimaryVideoStream?.PixelFormat;

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
        bool flac = string.Equals(audioEncoder, AuthoringEncoderNames.Flac, StringComparison.Ordinal);

        // THE MASTER FILE'S SOUND IS LOSSLESS. FLAC is its only codec, and FLAC is refused everywhere else:
        // the WebM profile and the bespoke Mode2 file promise Opus or Vorbis to every player, and a FLAC track
        // needs a CodeBrix.Audio.Core with FLAC packet support on the playing machine.
        if (request.Audio.Include && request.Flavour == VideoAuthoringFlavour.Master && !flac)
        {
            throw new VideoAuthoringException(
                "The request asks for " + audioEncoder + " audio in a master ('.cbvmaster') file. A master file's "
                + "sound is lossless: FLAC is the only codec it carries. Leave Audio.Codec at Default or set it to "
                + "AuthoringAudioCodec.Flac, or author VideoAuthoringFlavour.Bespoke for Vorbis.");
        }

        if (request.Audio.Include && request.Flavour != VideoAuthoringFlavour.Master && flac)
        {
            throw new VideoAuthoringException(
                "The request asks for FLAC audio in a " + (request.Flavour == VideoAuthoringFlavour.Bespoke
                    ? "bespoke (Mode2)"
                    : "WebM-profile (Mode1)")
                + " file. FLAC is carried only by the master flavour (VideoAuthoringFlavour.Master, '.cbvmaster'); "
                + "this flavour promises Opus or Vorbis to every player.");
        }

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

    private static CbvAuthoringResult WriteMaster(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        SourceTextSurvey survey,
        string temporaryFolder,
        List<string> temporaryFiles,
        List<AuthoringCommand> commands,
        List<string> notes)
    {
        string ivf = IvfPathFor(request, temporaryFolder);
        string alphaIvf = AlphaIvfPathFor(request, temporaryFolder);
        string flac = FlacPathFor(request, temporaryFolder);

        bool alpha = ResolveAlpha(request, survey, notes);
        int passCount = 1 + (alpha ? 1 : 0) + (request.Audio.Include ? 1 : 0);
        int pass = 1;

        if (!string.Equals(
                Path.GetExtension(request.OutputPath), CbvFormat.MasterFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(
                "the output is a master (Mode3) file and its name does not end in '" + CbvFormat.MasterFileExtension
                + "'. Readers sniff the content, so it plays either way; the extension is the convention.");
        }

        temporaryFiles.Add(ivf);
        FFMpegArgumentProcessor videoPass = AuthoringCommandFactory.BuildBespokeVideo(request, lut, ivf);
        Run(videoPass, request, "video pass", pass++, passCount, commands, notes);

        if (alpha)
        {
            request.CancellationToken.ThrowIfCancellationRequested();

            IvfKeyFrames picture = IvfKeyFrames.Read(ivf);
            temporaryFiles.Add(alphaIvf);
            FFMpegArgumentProcessor alphaPass =
                AuthoringCommandFactory.BuildMasterAlpha(request, alphaIvf, picture.FormatForceKeyFrames());
            Run(alphaPass, request, "alpha pass", pass++, passCount, commands, notes);

            IvfKeyFrames plane = IvfKeyFrames.Read(alphaIvf);
            string mismatch = picture.DescribeMismatch(plane);
            if (mismatch != null)
            {
                throw new VideoAuthoringException(
                    "The alpha pass did not come out in lock step with the video pass: " + mismatch + " A master "
                    + "file pairs the two frame for frame, so nothing was written.");
            }
        }

        CbvPacketAudioInput audio = null;
        if (request.Audio.Include)
        {
            temporaryFiles.Add(flac);
            FFMpegArgumentProcessor audioPass = AuthoringCommandFactory.BuildMasterAudio(request, flac);
            Run(audioPass, request, "audio pass", pass, passCount, commands, notes);
            request.CancellationToken.ThrowIfCancellationRequested();
            audio = SplitFlac(flac);
        }

        request.CancellationToken.ThrowIfCancellationRequested();

        CbvAuthoringRequest muxRequest = BuildMuxRequest(request, ivf, null);
        muxRequest.AlphaIvfPath = alpha ? alphaIvf : null;
        muxRequest.PacketAudio = audio;

        try
        {
            return CbvAuthoring.Write(muxRequest);
        }
        catch (Exception ex) when (ex is not VideoAuthoringException)
        {
            throw new VideoAuthoringException(
                "The encoded streams could not be muxed into '" + request.OutputPath + "': " + ex.Message,
                ex);
        }
    }

    private static bool ResolveAlpha(VideoAuthoringRequest request, SourceTextSurvey survey, List<string> notes)
    {
        switch (request.Video.Alpha)
        {
            case AuthoringAlphaMode.Include:
                return true;

            case AuthoringAlphaMode.Exclude:
                return false;
        }

        if (!survey.Probed)
        {
            notes.Add(
                "the source could not be probed, so whether it has an alpha channel is unknown and no alpha plane "
                + "was written. Set Video.Alpha to Include to carry one regardless.");
            return false;
        }

        bool hasAlpha = PixelFormatHasAlpha(survey.PixelFormat);
        if (!hasAlpha)
        {
            notes.Add(
                "the source's pixel format '" + (survey.PixelFormat ?? "unknown") + "' has no alpha channel, so no "
                + "alpha plane was written and the file plays as an opaque picture. (A VP9 or VP8 WebM whose alpha "
                + "rides in BlockAdditions reports a plain format here; set Video.Alpha to Include for such a "
                + "source.)");
        }

        return hasAlpha;
    }

    /// <summary>True when an FFmpeg pixel format name carries an alpha channel.</summary>
    /// <param name="pixelFormat">The name FFprobe reports, such as <c>yuva420p</c> or <c>rgba</c>.</param>
    /// <returns>True for a format with an alpha component.</returns>
    internal static bool PixelFormatHasAlpha(string pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat)) return false;
        string name = pixelFormat.ToLowerInvariant();

        return name.StartsWith("yuva", StringComparison.Ordinal)
            || name.StartsWith("gbrap", StringComparison.Ordinal)
            || name.StartsWith("rgba", StringComparison.Ordinal)
            || name.StartsWith("bgra", StringComparison.Ordinal)
            || name.StartsWith("argb", StringComparison.Ordinal)
            || name.StartsWith("abgr", StringComparison.Ordinal)
            || name.StartsWith("ya", StringComparison.Ordinal)
            || name.StartsWith("ayuv", StringComparison.Ordinal)
            || name.StartsWith("vuya", StringComparison.Ordinal);
    }

    private static CbvPacketAudioInput SplitFlac(string flacPath)
    {
        FlacFrameScanner scanner = FlacFrameScanner.ScanFile(flacPath);
        CbvPacketAudioInput audio = new CbvPacketAudioInput(
            VideoCodecIds.Flac,
            scanner.CodecPrivate,
            scanner.SampleRate,
            scanner.Channels);

        foreach (FlacFrame frame in scanner.Frames)
        {
            audio.Packets.Add(new CbvAudioPacket(
                scanner.CopyFrame(frame),
                scanner.TimestampOf(frame),
                scanner.DurationOf(frame)));
        }

        return audio;
    }

    private static string ResolveTemporaryFolder(VideoAuthoringRequest request) =>
        string.IsNullOrWhiteSpace(request.TemporaryFolder) ? Path.GetTempPath() : request.TemporaryFolder;

    private static string IvfPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".video.ivf");

    private static string OggPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".audio.ogg");

    private static string AlphaIvfPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".alpha.ivf");

    private static string FlacPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".audio.flac");

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
