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
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Exceptions;

// Both libraries have a Chapter type; the one this program means is the container's.
using Chapter = CodeBrix.VideoPlayback.Chapters.Chapter;

// The playback core describes the streams of a file it PLAYS; the master flavour reads its source through the
// video-processing contract, whose stream descriptions these are.
using AudioStreamInfo = CodeBrix.VideoProcessing.Core.Media.AudioStreamInfo;
using VideoStreamInfo = CodeBrix.VideoProcessing.Core.Media.VideoStreamInfo;

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
/// failed. With <see cref="AuthoringVideoEncoder.Av1Package" /> - or as the fallback when the ffmpeg path is
/// not available - the bespoke flavour's two passes run IN PROCESS instead, like the master flavour's, and
/// write the same two intermediates. The WebM-profile flavour follows the same rule: in process, its picture is
/// encoded by the in-process AV1 encoder, its sound by the in-process Opus encoder, and the file written by the
/// core's managed WebM muxer (<c>MatroskaMuxer</c>) with the captions, the chapters and the index in front -
/// no FFmpeg pass at all unless a prepare pass is needed.
/// </para>
/// <para>
/// The master flavour (<c>.cbvmaster</c>, Mode3) runs IN PROCESS: its source is decoded through the
/// video-processing engine the application registered (the desktop ffmpeg engine when it registered none),
/// the picture is encoded to AV1 by an in-process encoder - fully transparent pixels first given the colour of
/// the nearest visible one - then, when the file carries alpha, the alpha channel as a monochrome AV1 stream
/// whose key frames are forced onto the picture's, and the sound as lossless FLAC packets - then the same
/// muxer. The two video streams are checked for lock step before they are muxed. Only when the request asks
/// for something the engine's decoded frames cannot give - a frame size, a frame rate, a colour grade, or a
/// rotated source turned upright - does an FFmpeg PREPARE pass run first, writing a lossless intermediate the
/// in-process passes read instead. That pass needs the ffmpeg engine; on any other engine the same steps run
/// IN PROCESS, in the same order, through the core's managed scaler, rotator, frame-rate resampler and LUT
/// grader, and the result lists them as "prepare pass" lines that ran no FFmpeg (<c>rotate 90</c>,
/// <c>scale 1280x720 lanczos3</c>, <c>fps 30/1 drop/dup</c>, <c>grade "..." 33-node tetrahedral</c>). A master
/// may be 10-bit (<c>Video.BitDepth</c>).
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
/// accident: authoring a <c>.cbv</c> file must be possible with CodeBrix software plus the one encoder. The
/// master flavour needs less still: FFmpeg only as the desktop engine that decodes its source, and nothing
/// at all on an engine of its own, such as Android's.
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
    /// <returns>
    /// One command for a WebM-profile file, two for a bespoke one. For a master file, one entry per in-process
    /// pass - video, alpha (unless the request excludes alpha), audio (unless it excludes sound) - whose
    /// <see cref="AuthoringCommand.RunsFFmpeg" /> is false and whose arguments are the pass's settings line,
    /// preceded by the FFmpeg command of the prepare pass when the request's own settings need one - or, on an
    /// engine other than ffmpeg, by one "prepare pass" line per in-process step (<c>scale ...</c>,
    /// <c>fps ...</c>, <c>grade ...</c>) whose <see cref="AuthoringCommand.RunsFFmpeg" /> is false. A rotated
    /// source needs one too, and only a real run, which reads the source, can see that. A bespoke or
    /// WebM-profile file encoded in process is listed the same way - video, then audio - and the WebM-profile
    /// one ends with its mux, <c>mux webm ...</c>.
    /// </returns>
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
            // Judged like the bespoke flavour's path: the package encoder always runs in process, an FFmpeg
            // encoder only when the registered engine is not the ffmpeg one.
            if (!InProcessAuthoring.UsesFFmpegPath(request, false, out _))
            {
                return RenderInProcessWebMCommands(request, lut, temporaryFolder);
            }

            return new[]
            {
                new AuthoringCommand("one pass", AuthoringCommandFactory.BuildWebMProfile(request, lut).Arguments),
            };
        }

        if (request.Flavour == VideoAuthoringFlavour.Master)
        {
            return RenderMasterCommands(request, lut, temporaryFolder);
        }

        // A dry run runs nothing, so it judges the bespoke path from the request and the registered engine
        // alone; a real run also checks that ffmpeg can be run, and says so in its notes when it falls back.
        if (!InProcessAuthoring.UsesFFmpegPath(request, false, out _))
        {
            return RenderInProcessBespokeCommands(request, lut, temporaryFolder);
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

        // The master flavour decodes through whichever engine the application registered, and needs FFmpeg only
        // when that engine IS the ffmpeg one; the other two flavours are FFmpeg command lines.
        // The bespoke flavour does too when it encodes in process - by request, or as the fallback when the
        // ffmpeg path is not available.
        // The WebM-profile flavour follows the bespoke flavour's rule, with the managed WebM muxer in FFmpeg's place.
        IVideoProcessor masterEngine = null;
        string fallbackReason = null;
        bool inProcess = request.Flavour == VideoAuthoringFlavour.Master
            || !InProcessAuthoring.UsesFFmpegPath(request, true, out fallbackReason);

        if (inProcess)
        {
            if (request.Flavour == VideoAuthoringFlavour.WebMProfile) RefuseInProcessWebMSound(request);

            masterEngine = InProcessAuthoring.ResolveEngine();
            if (!InProcessAuthoring.TryVerifyEngine(masterEngine, out string problem))
            {
                throw new VideoAuthoringException(problem);
            }

            InProcessAuthoring.RequireBitDepthSupport(masterEngine, request);
        }
        else
        {
            AuthoringTools.Verify();
        }

        Stopwatch clock = Stopwatch.StartNew();
        string temporaryFolder = ResolveTemporaryFolder(request);
        List<string> notes = new List<string>();
        List<AuthoringCommand> commands = new List<AuthoringCommand>(2);
        List<string> temporaryFiles = new List<string>(3);

        // One ffprobe of the source, for the text this library does NOT carry: the source's own subtitle
        // streams and chapters. The run says what it left behind rather than letting the caller find out
        // from the finished file. A master run on an engine other than ffmpeg has no ffprobe to ask; it says
        // that instead.
        cancellationToken.ThrowIfCancellationRequested();
        SourceTextSurvey survey = masterEngine == null || InProcessAuthoring.IsFFmpegEngine(masterEngine)
            ? SurveySourceText(request, notes)
            : SkipSourceTextSurvey(masterEngine, notes);
        NoteUncarriedSourceText(request, survey, notes);
        NoteTitleWithoutField(request, notes);

        Directory.CreateDirectory(temporaryFolder);
        string outputFolder = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputFolder)) Directory.CreateDirectory(outputFolder);

        CbvAuthoringResult mux = null;
        MatroskaAuthoringResult matroska = null;
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

            if (request.Flavour == VideoAuthoringFlavour.WebMProfile && inProcess)
            {
                if (fallbackReason != null) notes.Add(fallbackReason);
                matroska = WriteInProcessWebM(
                    request, masterEngine, lut, temporaryFolder, temporaryFiles, commands, notes);
            }
            else if (request.Flavour == VideoAuthoringFlavour.WebMProfile)
            {
                NoteCollapsedChapterTitles(request, notes);
                NoteDroppedCaptionFlags(request, notes);

                FFMpegArgumentProcessor pass = AuthoringCommandFactory.BuildWebMProfile(request, lut);
                Run(pass, request, "one pass", 1, 1, commands, notes);
            }
            else if (request.Flavour == VideoAuthoringFlavour.Master)
            {
                mux = WriteMaster(request, masterEngine, lut, survey, temporaryFolder, temporaryFiles, commands, notes);
            }
            else if (inProcess)
            {
                if (fallbackReason != null) notes.Add(fallbackReason);
                mux = WriteInProcessBespoke(request, masterEngine, lut, temporaryFolder, temporaryFiles, commands, notes);
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
            // The managed WebM muxer has already judged the file it wrote, when it was asked to.
            profile = matroska != null && matroska.ProfileReport != null
                ? matroska.ProfileReport
                : StreamableProfile.EvaluateFile(request.OutputPath);

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
            lut.KeptPath,
            matroska);
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

    // The bespoke container (Mode2 and the Mode3 master) has chapter titles but no file title, and the format is
    // not changed for it - so a Title asked of either flavour is said to be dropped rather than silently lost.
    private static void NoteTitleWithoutField(VideoAuthoringRequest request, IList<string> notes)
    {
        if (string.IsNullOrEmpty(request.Title) || request.Flavour == VideoAuthoringFlavour.WebMProfile) return;

        notes.Add(
            "Title has no field in the bespoke container: the " + (request.Flavour == VideoAuthoringFlavour.Master
                ? "master (Mode3)"
                : "bespoke (Mode2)")
            + " file carries chapter titles but no file title, so Title '" + request.Title + "' was NOT written. "
            + "The WebM-profile flavour writes it.");
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

        // SILENT IS A MASTER-FILE FLAG. Only the master flavour's bespoke container has a header flag to carry it;
        // a WebM-profile or bespoke (Mode2) file without sound is authored with Audio.Include = false.
        if (request.Audio.Silent && request.Flavour != VideoAuthoringFlavour.Master)
        {
            throw new VideoAuthoringException(
                "Audio.Silent is a master-file flag: it is carried by the master ('.cbvmaster', Mode3) container's "
                + "header, and the " + (request.Flavour == VideoAuthoringFlavour.Bespoke
                    ? "bespoke (Mode2)"
                    : "WebM-profile (Mode1)")
                + " flavour has no field for it. Set Audio.Include to false for a file without sound, or author "
                + "VideoAuthoringFlavour.Master.");
        }

        // TEN BITS IS A MASTER-FILE SETTING. The WebM-profile (Mode1) and bespoke (Mode2) files are 8-bit 4:2:0 by
        // decision - the profile every player decodes without a conversion.
        if (request.Video.BitDepth != AuthoringVideoSettings.DefaultBitDepth && request.Flavour != VideoAuthoringFlavour.Master)
        {
            throw new VideoAuthoringException(
                "Video.BitDepth = " + request.Video.BitDepth.ToString(CultureInfo.InvariantCulture) + " is a master-file "
                + "setting: the " + (request.Flavour == VideoAuthoringFlavour.Bespoke
                    ? "bespoke (Mode2) '.cbv'"
                    : "WebM-profile (Mode1)")
                + " flavour is 8-bit 4:2:0 by decision, the profile every player decodes without a conversion. Set "
                + "Video.BitDepth to 8, or author VideoAuthoringFlavour.Master ('.cbvmaster') for a 10-bit file.");
        }

        if (forRunning && !File.Exists(request.SourcePath))
        {
            throw new VideoAuthoringException("There is no source file at '" + request.SourcePath + "'.");
        }

        string audioEncoder = EncodesSound(request)
            ? AuthoringCommandFactory.AudioEncoderNameFor(request.Audio.Codec, request.Flavour)
            : null;

        bool opus = string.Equals(audioEncoder, AuthoringEncoderNames.LibOpus, StringComparison.Ordinal);
        bool flac = string.Equals(audioEncoder, AuthoringEncoderNames.Flac, StringComparison.Ordinal);

        // THE MASTER FILE'S SOUND IS LOSSLESS. FLAC is its only codec, and FLAC is refused everywhere else:
        // the WebM profile and the bespoke Mode2 file promise Opus or Vorbis to every player, and a FLAC track
        // needs a CodeBrix.Audio.Core with FLAC packet support on the playing machine.
        if (EncodesSound(request) && request.Flavour == VideoAuthoringFlavour.Master && !flac)
        {
            throw new VideoAuthoringException(
                "The request asks for " + audioEncoder + " audio in a master ('.cbvmaster') file. A master file's "
                + "sound is lossless: FLAC is the only codec it carries. Leave Audio.Codec at Default or set it to "
                + "AuthoringAudioCodec.Flac, or author VideoAuthoringFlavour.Bespoke for Vorbis.");
        }

        if (EncodesSound(request) && request.Flavour != VideoAuthoringFlavour.Master && flac)
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

        if (EncodesSound(request) && request.RequireNoExtraPlaybackPackages && opus)
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
        if (EncodesSound(request)
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

    private static IReadOnlyList<AuthoringCommand> RenderMasterCommands(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string temporaryFolder)
    {
        IVideoProcessor engine = InProcessAuthoring.ResolveEngine();
        InProcessAuthoring.RequireBitDepthSupport(engine, request);

        // Whether an Auto request carries alpha depends on the source, which a dry run does not read - so the
        // alpha pass (and the transparent-pixel fill) is listed unless the request rules it out. A rotated
        // source adds a prepare pass (or an in-process turn) that only a real run can see.
        bool alpha = request.Video.Alpha != AuthoringAlphaMode.Exclude;
        List<AuthoringCommand> commands = new List<AuthoringCommand>(4);
        string input = AddDryRunPreparation(request, lut, temporaryFolder, alpha, engine, commands);

        string ivf = IvfPathFor(request, temporaryFolder);
        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(
                request,
                input,
                ivf,
                InProcessAuthoring.FillsTransparentPixels(request.Video, alpha),
                InProcessAuthoring.DryRunSize(request)),
            false));

        if (alpha)
        {
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AlphaPassLabel,
                InProcessAuthoring.DescribeAlphaPass(
                    request, input, AlphaIvfPathFor(request, temporaryFolder), InProcessAuthoring.DryRunSize(request)),
                false));
        }

        if (request.Audio.Silent)
        {
            commands.Add(new AuthoringCommand(InProcessAuthoring.AudioPassLabel, InProcessAuthoring.SilentAudioPass, false));
        }
        else if (request.Audio.Include)
        {
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AudioPassLabel,
                InProcessAuthoring.DescribeAudioPass(request, request.SourcePath),
                false));
        }

        return commands;
    }

    // MODE1 IS WEBM WITH OPUS. The in-process WebM-profile path writes Opus in a WebM document and nothing else;
    // a Matroska document (Container = Matroska) takes what the FFmpeg path takes there - Opus or Vorbis. The
    // in-process Opus encoder codes mono and stereo only (channel mapping family 0), where libopus through FFmpeg
    // also codes surround. Both refusals name the way round them. FLAC was refused before this point already.
    private static void RefuseInProcessWebMSound(VideoAuthoringRequest request)
    {
        if (!request.Audio.Include) return;

        bool opus = InProcessAuthoring.IsOpus(request);
        if (!opus && request.Container == AuthoringContainerFormat.WebM)
        {
            throw new VideoAuthoringException(
                "The request asks for Vorbis audio in a WebM-profile (Mode1) file encoded in process. Mode1 is WebM "
                + "with Opus, and the in-process path writes exactly that: leave Audio.Codec at Default or set it to "
                + "AuthoringAudioCodec.LibOpus, set Container to AuthoringContainerFormat.Matroska for Vorbis in a "
                + "Matroska document, or author through FFmpeg (Video.Encoder LibSvtAv1 or LibAomAv1 on a machine "
                + "with ffmpeg).");
        }

        if (opus && request.Audio.Channels > InProcessAuthoring.MaximumOpusChannels)
        {
            throw new VideoAuthoringException(
                "The request asks for Opus audio in " + request.Audio.Channels.ToString(CultureInfo.InvariantCulture)
                + " channels encoded in process, and the in-process Opus encoder codes mono and stereo only. Set "
                + "Audio.Channels to 1 or 2, or author through FFmpeg (Video.Encoder LibSvtAv1 or LibAomAv1 on a "
                + "machine with ffmpeg), whose libopus codes surround.");
        }
    }

    private static IReadOnlyList<AuthoringCommand> RenderInProcessWebMCommands(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string temporaryFolder)
    {
        RefuseInProcessWebMSound(request);

        IVideoProcessor engine = InProcessAuthoring.ResolveEngine();
        string ivf = IvfPathFor(request, temporaryFolder);
        List<AuthoringCommand> commands = new List<AuthoringCommand>(4);
        string input = AddDryRunPreparation(request, lut, temporaryFolder, false, engine, commands);

        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(request, input, ivf, false, InProcessAuthoring.DryRunSize(request)),
            false));

        string audio = null;
        if (request.Audio.Include)
        {
            bool opus = InProcessAuthoring.IsOpus(request);
            audio = opus ? "packets" : OggPathFor(request, temporaryFolder);
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AudioPassLabel,
                opus
                    ? InProcessAuthoring.DescribeOpusPass(request, request.SourcePath)
                    : InProcessAuthoring.DescribeVorbisPass(request, request.SourcePath, audio),
                false));
        }

        commands.Add(new AuthoringCommand(
            InProcessAuthoring.MuxPassLabel,
            InProcessAuthoring.DescribeMux(request, ivf, audio),
            false));

        return commands;
    }

    // The WebM-profile flavour's in-process path: the bespoke flavour's picture pass, the sound as Opus packets
    // (or, in a Matroska document that asked for it, the Vorbis pass's Ogg file), then the managed WebM /
    // Matroska muxer with the captions, the chapters, the index in front and the profile judged on the way out.
    private static MatroskaAuthoringResult WriteInProcessWebM(
        VideoAuthoringRequest request,
        IVideoProcessor engine,
        ResolvedLutChain lut,
        string temporaryFolder,
        List<string> temporaryFiles,
        List<AuthoringCommand> commands,
        List<string> notes)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        string ivf = IvfPathFor(request, temporaryFolder);
        string ogg = OggPathFor(request, temporaryFolder);
        string prepared = PreparedPathFor(request, temporaryFolder);
        bool opus = request.Audio.Include && InProcessAuthoring.IsOpus(request);

        NoteSettingsWithoutInProcessMeaning(request, opus, notes);

        MediaInfo media = AnalyseSource(engine, request.SourcePath, cancellationToken);
        VideoStreamInfo stream = media.PrimaryVideo;
        if (stream == null)
        {
            throw new VideoAuthoringException("'" + request.SourcePath + "' has no video stream.");
        }

        ManagedFrameShaping shaping = ChoosePreparation(request, engine, lut, stream, notes, out bool prepare);
        int passCount = (prepare ? 1 : 0) + 1 + (request.Audio.Include ? 1 : 0);
        int pass = 1;
        string input = request.SourcePath;
        VideoStreamInfo pictureStream = stream;

        if (prepare)
        {
            temporaryFiles.Add(prepared);
            FFMpegArgumentProcessor preparePass = AuthoringCommandFactory.BuildPreparePass(request, lut, prepared, false);
            Run(preparePass, request, InProcessAuthoring.PreparePassLabel, pass++, passCount, commands, notes);

            input = prepared;
            pictureStream = AnalyseSource(engine, prepared, cancellationToken).PrimaryVideo ?? stream;
        }

        cancellationToken.ThrowIfCancellationRequested();
        temporaryFiles.Add(ivf);
        VideoPassResult picture = RunInProcess(
            request,
            InProcessAuthoring.VideoPassLabel,
            pass++,
            passCount,
            progress => InProcessAuthoring.EncodeVideo(
                engine, request, input, false, pictureStream, ivf, progress, cancellationToken, shaping));
        AddShapingCommands(picture, commands);
        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(request, input, ivf, false, InProcessAuthoring.EncodedSize(picture)),
            false));
        NoteCrop(picture, notes);

        MatroskaPacketAudioInput packets = null;
        string audioToken = null;
        if (request.Audio.Include)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AudioStreamInfo sound = media.PrimaryAudio;
            if (sound == null)
            {
                throw new VideoAuthoringException(
                    "'" + request.SourcePath + "' has no audio stream. Set Audio.Include to false to author a file "
                    + "without sound.");
            }

            TimeSpan duration = sound.Duration > TimeSpan.Zero ? sound.Duration : media.Duration;
            if (opus)
            {
                packets = RunInProcess(
                    request,
                    InProcessAuthoring.AudioPassLabel,
                    pass,
                    passCount,
                    progress => InProcessAuthoring.EncodeOpus(
                        engine, request, request.SourcePath, duration, progress, cancellationToken));
                audioToken = "packets";
                commands.Add(new AuthoringCommand(
                    InProcessAuthoring.AudioPassLabel,
                    InProcessAuthoring.DescribeOpusPass(request, request.SourcePath),
                    false));
            }
            else
            {
                temporaryFiles.Add(ogg);
                RunInProcess(
                    request,
                    InProcessAuthoring.AudioPassLabel,
                    pass,
                    passCount,
                    progress =>
                    {
                        InProcessAuthoring.EncodeVorbis(
                            engine, request, request.SourcePath, ogg, duration, progress, cancellationToken);
                        return true;
                    });
                audioToken = ogg;
                commands.Add(new AuthoringCommand(
                    InProcessAuthoring.AudioPassLabel,
                    InProcessAuthoring.DescribeVorbisPass(request, request.SourcePath, ogg),
                    false));
            }
        }

        // The mux is managed code over finished intermediates; like the bespoke mux it is not interrupted part-way.
        cancellationToken.ThrowIfCancellationRequested();
        MatroskaAuthoringRequest muxRequest = new MatroskaAuthoringRequest
        {
            OutputPath = request.OutputPath,
            DocType = request.Container == AuthoringContainerFormat.Matroska ? MatroskaDocType.Matroska : MatroskaDocType.WebM,
            VideoIvfPath = ivf,
            VideoName = request.Video.TrackName,
            AudioOggPath = request.Audio.Include && !opus ? ogg : null,
            PacketAudio = packets,
            AudioName = request.Audio.Name,
            AudioLanguage = request.Audio.Language,
            ChaptersPath = string.IsNullOrWhiteSpace(request.ChaptersPath) ? null : request.ChaptersPath,
            Title = string.IsNullOrEmpty(request.Title) ? null : request.Title,
            CuesToFront = request.CuesToFront,
            EvaluateProfile = request.ValidateProfile,
        };

        foreach (AuthoringCaptionInput caption in request.Captions)
        {
            muxRequest.Captions.Add(new MatroskaCaptionInput(caption.Path, caption.Language, caption.Name, caption.Flags));
        }

        MatroskaAuthoringResult result;
        try
        {
            result = MatroskaMuxer.Write(muxRequest);
        }
        catch (Exception ex) when (ex is not VideoAuthoringException)
        {
            throw new VideoAuthoringException(
                "The encoded streams could not be muxed into '" + request.OutputPath + "': " + ex.Message,
                ex);
        }

        commands.Add(new AuthoringCommand(
            InProcessAuthoring.MuxPassLabel,
            InProcessAuthoring.DescribeMux(request, ivf, audioToken),
            false));

        foreach (string notice in result.Notices) notes.Add("mux pass: " + notice);
        return result;
    }

    // Two of the request's settings shape the FFmpeg command line only - on the in-process WebM-profile (Mode1)
    // path and the in-process bespoke (Mode2) path alike. The in-process path reads the source's
    // FIRST video and FIRST audio stream - which is what SelectStreamsExplicitly asks of FFmpeg - and copies none
    // of its metadata. Opus also codes at 48000 Hz whatever the request's sample rate says. Each is said rather
    // than silently ignored.
    private static void NoteSettingsWithoutInProcessMeaning(VideoAuthoringRequest request, bool opus, List<string> notes)
    {
        if (request.CopySourceMetadata)
        {
            notes.Add(
                "CopySourceMetadata has no in-process meaning: the source's own metadata (its title, creation time, "
                + "device strings) was NOT copied. "
                + (request.Flavour == VideoAuthoringFlavour.WebMProfile
                    ? "The file's title is Title when set, else the chapter file's 'title=' line when ChaptersPath "
                        + "names one, and there is none otherwise."
                    : "The bespoke container has no file-level metadata to copy it into in any case."));
        }

        if (!request.SelectStreamsExplicitly)
        {
            notes.Add(
                "SelectStreamsExplicitly = false has no in-process meaning: the in-process path always reads the "
                + "source's first video stream and first audio stream, which is what SelectStreamsExplicitly = true "
                + "asks of FFmpeg.");
        }

        if (opus && request.Audio.SampleRateHz != InProcessAuthoring.OpusSampleRate)
        {
            notes.Add(
                "Audio.SampleRateHz = " + request.Audio.SampleRateHz.ToString(CultureInfo.InvariantCulture)
                + " has no in-process meaning for Opus: Opus codes at "
                + InProcessAuthoring.OpusSampleRate.ToString(CultureInfo.InvariantCulture)
                + " Hz, so the sound was decoded and encoded at that rate.");
        }
    }

    private static IReadOnlyList<AuthoringCommand> RenderInProcessBespokeCommands(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string temporaryFolder)
    {
        IVideoProcessor engine = InProcessAuthoring.ResolveEngine();
        List<AuthoringCommand> commands = new List<AuthoringCommand>(3);
        string input = AddDryRunPreparation(request, lut, temporaryFolder, false, engine, commands);

        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(
                request, input, IvfPathFor(request, temporaryFolder), false, InProcessAuthoring.DryRunSize(request)),
            false));

        if (request.Audio.Include)
        {
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AudioPassLabel,
                InProcessAuthoring.DescribeVorbisPass(request, request.SourcePath, OggPathFor(request, temporaryFolder)),
                false));
        }

        return commands;
    }

    // The bespoke flavour's in-process path: the master flavour's colour pass without alpha and without the
    // fill, the sound through the in-process Vorbis encoder into the Ogg file the muxer has always taken, then
    // the same mux as the FFmpeg path - captions and chapters included.
    private static CbvAuthoringResult WriteInProcessBespoke(
        VideoAuthoringRequest request,
        IVideoProcessor engine,
        ResolvedLutChain lut,
        string temporaryFolder,
        List<string> temporaryFiles,
        List<AuthoringCommand> commands,
        List<string> notes)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        string ivf = IvfPathFor(request, temporaryFolder);
        string ogg = OggPathFor(request, temporaryFolder);
        string prepared = PreparedPathFor(request, temporaryFolder);

        MediaInfo media = AnalyseSource(engine, request.SourcePath, cancellationToken);
        VideoStreamInfo stream = media.PrimaryVideo;
        if (stream == null)
        {
            throw new VideoAuthoringException("'" + request.SourcePath + "' has no video stream.");
        }

        NoteSettingsWithoutInProcessMeaning(request, false, notes);

        ManagedFrameShaping shaping = ChoosePreparation(request, engine, lut, stream, notes, out bool prepare);
        int passCount = (prepare ? 1 : 0) + 1 + (request.Audio.Include ? 1 : 0);
        int pass = 1;
        string input = request.SourcePath;
        VideoStreamInfo pictureStream = stream;

        if (prepare)
        {
            temporaryFiles.Add(prepared);
            FFMpegArgumentProcessor preparePass = AuthoringCommandFactory.BuildPreparePass(request, lut, prepared, false);
            Run(preparePass, request, InProcessAuthoring.PreparePassLabel, pass++, passCount, commands, notes);

            input = prepared;
            pictureStream = AnalyseSource(engine, prepared, cancellationToken).PrimaryVideo ?? stream;
        }

        cancellationToken.ThrowIfCancellationRequested();
        temporaryFiles.Add(ivf);
        VideoPassResult picture = RunInProcess(
            request,
            InProcessAuthoring.VideoPassLabel,
            pass++,
            passCount,
            progress => InProcessAuthoring.EncodeVideo(
                engine, request, input, false, pictureStream, ivf, progress, cancellationToken, shaping));
        AddShapingCommands(picture, commands);
        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(request, input, ivf, false, InProcessAuthoring.EncodedSize(picture)),
            false));
        NoteCrop(picture, notes);

        if (request.Audio.Include)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AudioStreamInfo sound = media.PrimaryAudio;
            if (sound == null)
            {
                throw new VideoAuthoringException(
                    "'" + request.SourcePath + "' has no audio stream. Set Audio.Include to false to author a file "
                    + "without sound.");
            }

            TimeSpan duration = sound.Duration > TimeSpan.Zero ? sound.Duration : media.Duration;
            temporaryFiles.Add(ogg);
            RunInProcess(
                request,
                InProcessAuthoring.AudioPassLabel,
                pass,
                passCount,
                progress =>
                {
                    InProcessAuthoring.EncodeVorbis(
                        engine, request, request.SourcePath, ogg, duration, progress, cancellationToken);
                    return true;
                });
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AudioPassLabel,
                InProcessAuthoring.DescribeVorbisPass(request, request.SourcePath, ogg),
                false));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Mux(request, ivf, request.Audio.Include ? ogg : null);
    }

    // What a dry run lists ahead of the video pass when the request's own settings shape the picture: the ffmpeg
    // prepare pass's command line on the ffmpeg engine (and the video pass then reads its intermediate), or the
    // deterministic in-process lines on any other engine (and the video pass reads the source). Returns what the
    // video pass reads.
    private static string AddDryRunPreparation(
        VideoAuthoringRequest request,
        ResolvedLutChain lut,
        string temporaryFolder,
        bool alpha,
        IVideoProcessor engine,
        List<AuthoringCommand> commands)
    {
        List<string> needs = InProcessAuthoring.SettingsNeedingPreparePass(request, lut.HasGrade);
        if (needs.Count == 0) return request.SourcePath;

        if (InProcessAuthoring.IsFFmpegEngine(engine))
        {
            string prepared = PreparedPathFor(request, temporaryFolder);
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.PreparePassLabel,
                AuthoringCommandFactory.BuildPreparePass(request, lut, prepared, alpha).Arguments));
            return prepared;
        }

        foreach (string line in ManagedFrameShaping.Create(request, lut, false).DescribeDryRun())
        {
            commands.Add(new AuthoringCommand(InProcessAuthoring.PreparePassLabel, line, false));
        }

        return request.SourcePath;
    }

    // Decides how a real run shapes the picture once the source has been analysed: by the ffmpeg prepare pass on
    // the ffmpeg engine (prepare = true), in process on any other (the shaping returned), or not at all.
    private static ManagedFrameShaping ChoosePreparation(
        VideoAuthoringRequest request,
        IVideoProcessor engine,
        ResolvedLutChain lut,
        VideoStreamInfo stream,
        List<string> notes,
        out bool prepare)
    {
        List<string> needs = InProcessAuthoring.SettingsNeedingPreparePass(request, lut.HasGrade);
        if (request.Video.AutoRotate && stream.RotationDegrees % 360 != 0) needs.Add("AutoRotate");

        prepare = false;
        if (needs.Count == 0) return null;

        if (InProcessAuthoring.IsFFmpegEngine(engine))
        {
            prepare = true;
            return null;
        }

        ManagedFrameShaping shaping = ManagedFrameShaping.Create(request, lut, true);
        notes.AddRange(shaping.DescribeIgnoredOptions(request));
        return shaping;
    }

    // The in-process shaping's lines, listed where the ffmpeg prepare pass's command would be: ahead of the video
    // pass, in the order the steps ran, none of them an FFmpeg command.
    private static void AddShapingCommands(VideoPassResult picture, List<AuthoringCommand> commands)
    {
        if (picture.Shaping == null) return;

        foreach (string line in picture.Shaping.DescribeRun(picture.Rotation, picture.TurnedWidth, picture.TurnedHeight))
        {
            commands.Add(new AuthoringCommand(InProcessAuthoring.PreparePassLabel, line, false));
        }
    }

    // A 10-bit master from an 8-bit source is widened, not improved - said rather than left to be found out.
    private static void NoteWidenedPicture(VideoAuthoringRequest request, VideoPassResult picture, List<string> notes)
    {
        if (request.Video.BitDepth <= 8) return;

        int sourceDepth = picture.Info.SourceBitDepth;
        if (!picture.Widened && (sourceDepth <= 0 || sourceDepth >= request.Video.BitDepth)) return;

        notes.Add(
            "the source's picture is " + (sourceDepth > 0 ? sourceDepth.ToString(CultureInfo.InvariantCulture) : "8")
            + "-bit and was WIDENED to " + request.Video.BitDepth.ToString(CultureInfo.InvariantCulture)
            + " bits for this master" + (picture.Widened
                ? " here (picture value << 2, alpha value << 2 | value >> 6 so fully opaque stays 1023)"
                : " by the engine")
            + "; a 10-bit file of an 8-bit picture holds nothing an 8-bit master would not.");
    }

    private static CbvAuthoringResult WriteMaster(
        VideoAuthoringRequest request,
        IVideoProcessor engine,
        ResolvedLutChain lut,
        SourceTextSurvey survey,
        string temporaryFolder,
        List<string> temporaryFiles,
        List<AuthoringCommand> commands,
        List<string> notes)
    {
        CancellationToken cancellationToken = request.CancellationToken;
        string ivf = IvfPathFor(request, temporaryFolder);
        string alphaIvf = AlphaIvfPathFor(request, temporaryFolder);
        string prepared = PreparedPathFor(request, temporaryFolder);

        if (!string.Equals(
                Path.GetExtension(request.OutputPath), CbvFormat.MasterFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(
                "the output is a master (Mode3) file and its name does not end in '" + CbvFormat.MasterFileExtension
                + "'. Readers sniff the content, so it plays either way; the extension is the convention.");
        }

        MediaInfo media = AnalyseSource(engine, request.SourcePath, cancellationToken);
        VideoStreamInfo stream = media.PrimaryVideo;
        if (stream == null)
        {
            throw new VideoAuthoringException(
                "'" + request.SourcePath + "' has no video stream, and a master file is a picture first.");
        }

        bool alpha = ResolveAlpha(request, engine, stream, survey, notes);

        NoteSettingsWithoutInProcessMeaning(request, false, notes);
        ManagedFrameShaping shaping = ChoosePreparation(request, engine, lut, stream, notes, out bool prepare);
        int passCount = (prepare ? 1 : 0) + 1 + (alpha ? 1 : 0) + (EncodesSound(request) ? 1 : 0);
        int pass = 1;

        string input = request.SourcePath;
        VideoStreamInfo pictureStream = stream;

        if (prepare)
        {
            temporaryFiles.Add(prepared);
            FFMpegArgumentProcessor preparePass =
                AuthoringCommandFactory.BuildPreparePass(request, lut, prepared, alpha);
            Run(preparePass, request, InProcessAuthoring.PreparePassLabel, pass++, passCount, commands, notes);

            input = prepared;
            pictureStream = AnalyseSource(engine, prepared, cancellationToken).PrimaryVideo ?? stream;
        }

        cancellationToken.ThrowIfCancellationRequested();
        temporaryFiles.Add(ivf);
        VideoPassResult picture = RunInProcess(
            request,
            InProcessAuthoring.VideoPassLabel,
            pass++,
            passCount,
            progress => InProcessAuthoring.EncodeVideo(
                engine, request, input, alpha, pictureStream, ivf, progress, cancellationToken, shaping));
        AddShapingCommands(picture, commands);
        commands.Add(new AuthoringCommand(
            InProcessAuthoring.VideoPassLabel,
            InProcessAuthoring.DescribeVideoPass(
                request, input, ivf, picture.Filled, InProcessAuthoring.EncodedSize(picture)),
            false));
        NoteCrop(picture, notes);
        NoteWidenedPicture(request, picture, notes);

        if (alpha)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A source with no alpha channel - Alpha set to Include - yields a fully opaque plane, made without
            // decoding anything a second time.
            string alphaInput = picture.Info.HasAlpha ? input : null;
            temporaryFiles.Add(alphaIvf);
            RunInProcess(
                request,
                InProcessAuthoring.AlphaPassLabel,
                pass++,
                passCount,
                progress =>
                {
                    InProcessAuthoring.EncodeAlpha(
                        engine, request, alphaInput, picture, alphaIvf, progress, cancellationToken);
                    return true;
                });
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AlphaPassLabel,
                InProcessAuthoring.DescribeAlphaPass(
                    request, alphaInput, alphaIvf, InProcessAuthoring.EncodedSize(picture)),
                false));
        }

        CbvPacketAudioInput audio = null;
        if (request.Audio.Silent)
        {
            commands.Add(new AuthoringCommand(InProcessAuthoring.AudioPassLabel, InProcessAuthoring.SilentAudioPass, false));
        }
        else if (request.Audio.Include)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AudioStreamInfo sound = media.PrimaryAudio;
            if (sound == null)
            {
                throw new VideoAuthoringException(
                    "'" + request.SourcePath + "' has no audio stream. Set Audio.Include to false to author a master "
                    + "file without sound.");
            }

            TimeSpan duration = sound.Duration > TimeSpan.Zero ? sound.Duration : media.Duration;
            audio = RunInProcess(
                request,
                InProcessAuthoring.AudioPassLabel,
                pass,
                passCount,
                progress => InProcessAuthoring.EncodeAudio(
                    engine, request, request.SourcePath, duration, progress, cancellationToken));
            commands.Add(new AuthoringCommand(
                InProcessAuthoring.AudioPassLabel,
                InProcessAuthoring.DescribeAudioPass(request, request.SourcePath),
                false));
        }

        cancellationToken.ThrowIfCancellationRequested();

        CbvAuthoringRequest muxRequest = BuildMuxRequest(request, ivf, null);
        muxRequest.AlphaIvfPath = alpha ? alphaIvf : null;
        muxRequest.PacketAudio = audio;
        muxRequest.Silent = request.Audio.Silent;

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

    // True when the run encodes the source's sound: asked for, and not ruled out by the master flavour's Silent flag.
    private static bool EncodesSound(VideoAuthoringRequest request) => request.Audio.Include && !request.Audio.Silent;

    private static void NoteCrop(VideoPassResult picture, List<string> notes)
    {
        string note = InProcessAuthoring.DescribeCrop(picture.Info.Width, picture.Info.Height);
        if (note != null) notes.Add(note);
    }

    private static MediaInfo AnalyseSource(IVideoProcessor engine, string path, CancellationToken cancellationToken)
    {
        try
        {
            return engine.Analyse(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VideoAuthoringException(
                "'" + path + "' could not be read by the '" + engine.Name + "' engine: " + FirstLine(ex.Message),
                ex);
        }
    }

    // Runs one in-process pass: progress as a percentage of the pass, and every failure that is not already
    // this library's own - an engine that cannot decode, an encoder that refuses - reported as which pass
    // failed and why.
    private static T RunInProcess<T>(
        VideoAuthoringRequest request,
        string label,
        int passNumber,
        int passCount,
        Func<Action<int>, T> body)
    {
        Action<int> progress = null;
        if (request.ProgressCallback != null)
        {
            progress = percent => request.ProgressCallback(new AuthoringProgress(label, passNumber, passCount, percent));
        }

        try
        {
            return body(progress);
        }
        catch (VideoAuthoringException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            throw new VideoAuthoringException("The " + label + " failed: " + FirstLine(ex.Message), ex);
        }
    }

    private static SourceTextSurvey SkipSourceTextSurvey(IVideoProcessor engine, IList<string> notes)
    {
        notes.Add(
            "the source was not checked for subtitle streams and chapters of its own: that check needs ffprobe, and "
            + "this run decoded through the '" + engine.Name + "' engine. If it carries any, they were not carried - "
            + "captions come only from Captions and chapters only from ChaptersPath.");
        return new SourceTextSurvey();
    }

    private static bool ResolveAlpha(
        VideoAuthoringRequest request,
        IVideoProcessor engine,
        VideoStreamInfo stream,
        SourceTextSurvey survey,
        List<string> notes)
    {
        bool engineDeliversAlpha = engine.Supports(VideoProcessorCapabilities.AlphaFrameSource);

        // The engine's own description first; the ffprobe pixel format, when there is one, can only add to it.
        bool sourceHasAlpha = stream.HasAlpha || (survey.Probed && PixelFormatHasAlpha(survey.PixelFormat));
        string pixelFormat = !string.IsNullOrEmpty(stream.PixelFormatName) ? stream.PixelFormatName : survey.PixelFormat;

        switch (request.Video.Alpha)
        {
            case AuthoringAlphaMode.Include:
                if (sourceHasAlpha && !engineDeliversAlpha)
                {
                    throw new VideoAuthoringException(
                        "The request includes the source's alpha channel, and the '" + engine.Name + "' engine cannot "
                        + "deliver an alpha plane, so the plane would come out fully opaque. Author on an engine that "
                        + "can, or set Video.Alpha to Exclude.");
                }

                return true;

            case AuthoringAlphaMode.Exclude:
                return false;
        }

        if (!sourceHasAlpha)
        {
            notes.Add(
                "the source's pixel format '" + (pixelFormat ?? "unknown") + "' has no alpha channel, so no alpha "
                + "plane was written and the file plays as an opaque picture. Set Video.Alpha to Include to carry a "
                + "fully opaque plane regardless.");
            return false;
        }

        if (!engineDeliversAlpha)
        {
            notes.Add(
                "the source has an alpha channel and the '" + engine.Name + "' engine cannot deliver it, so no alpha "
                + "plane was written and the file plays as an opaque picture.");
            return false;
        }

        return true;
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

    private static string ResolveTemporaryFolder(VideoAuthoringRequest request) =>
        string.IsNullOrWhiteSpace(request.TemporaryFolder) ? Path.GetTempPath() : request.TemporaryFolder;

    private static string IvfPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".video.ivf");

    private static string OggPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".audio.ogg");

    private static string AlphaIvfPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".alpha.ivf");

    private static string PreparedPathFor(VideoAuthoringRequest request, string temporaryFolder) =>
        Path.Combine(temporaryFolder, BaseNameFor(request) + ".prepared.mkv");

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
