using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Media;
using CodeBrix.VideoProcessing.Core.Output;

// The playback core and the video-processing contract both name these; the writer's are the Core ones.
using CoreFrame = CodeBrix.VideoProcessing.Core.Frames.VideoFrameData;
using CoreLayout = CodeBrix.VideoProcessing.Core.Frames.VideoPixelLayout;
using CorePlane = CodeBrix.VideoProcessing.Core.Frames.VideoPlane;
using CoreRange = CodeBrix.VideoProcessing.Core.Media.VideoColorRange;
using PlaybackLayout = CodeBrix.VideoPlayback.Decoding.VideoPixelLayout;
using PlaybackRange = CodeBrix.VideoPlayback.Decoding.VideoColorRange;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// One export run: read the source's packets in file order, decode the picture (and its alpha plane) and the
/// sound with the registered decoders, fit the pictures to the nominal frame rate, composite them over the
/// backdrop when they carry alpha, and hand pictures and sound to the engine's MP4 writer in step.
/// </summary>
/// <remarks>
/// Memory stays flat: decoded frames come from one frame pool and are released as soon as they are written; at
/// most one picture is held back (the frame-rate fit answers one frame late); a few frames wait in the alpha pairing.
/// </remarks>
internal sealed class ExportPipeline : IDisposable
{
    private readonly VideoExportRequest request;
    private readonly IVideoProcessor engine;
    private readonly ExportBackdrop backdrop;
    private readonly List<string> notes = new List<string>();
    private readonly Queue<VideoFrame> pictures = new Queue<VideoFrame>();
    private readonly Queue<VideoFrame> alphas = new Queue<VideoFrame>();
    private readonly CancellationToken cancellationToken;

    private ExportSource source;
    private PinnedFrameBufferPool pool;
    private IVideoDecoder videoDecoder;
    private IVideoDecoder alphaDecoder;
    private ExportAudioFeed audio;
    private ExportAudioFeed.Ours ourAudio;
    private VideoExportAudioSource audioSource;
    private ExportBackdropSource backdropSource;
    private ExportBackdropSource.Solid grey;
    private IStandardVideoWriter writer;
    private FrameRate rate;
    private FrameRateResampler resampler;
    private VideoFrame held;
    private bool composite;
    private bool rgbaInput;
    private int width;
    private int height;
    private long written;
    private long sequence;
    private long expectedFrames;
    private int lastPercent = -1;
    private byte[] bgra;
    private byte[] rgba;
    private byte[] planeY;
    private byte[] planeU;
    private byte[] planeV;
    private int dropped;
    private int duplicated;
    private int withoutAlpha;
    private long framesOverGrey;
    private long tailFrames;

    /// <summary>Prepares a run.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="engine">The engine to write with.</param>
    public ExportPipeline(VideoExportRequest request, IVideoProcessor engine)
    {
        this.request = request;
        this.engine = engine;
        backdrop = request.Backdrop ?? new ExportBackdrop();
        cancellationToken = request.CancellationToken;
    }

    /// <summary>Runs the export.</summary>
    /// <returns>What was written.</returns>
    public VideoExportResult Run()
    {
        source = ExportSource.Open(request.SourcePath);
        composite = source.HasAlpha;

        if (!composite && !VideoExporter.IsDefault(backdrop)) notes.Add(VideoExporter.BackdropIgnoredNote);
        if (composite && !engine.Supports(VideoProcessorCapabilities.RgbaInput))
        {
            throw new VideoAuthoringException(
                "The source carries an alpha plane, which an export composites over its backdrop and hands to the writer "
                + "as RGBA - and the '" + engine.Name + "' video-processing engine does not report RgbaInput.");
        }

        if (composite && backdrop.Kind == ExportBackdropKind.Video)
        {
            if (!File.Exists(backdrop.VideoPath))
            {
                throw new VideoAuthoringException("The backdrop video '" + backdrop.VideoPath + "' does not exist.");
            }
        }
        else if (composite && backdrop.Kind == ExportBackdropKind.Image && backdrop.ImageRgba == null
            && !File.Exists(backdrop.ImagePath))
        {
            throw new VideoAuthoringException("The backdrop image '" + backdrop.ImagePath + "' does not exist.");
        }

        if (source.HasCaptionsOrChapters) notes.Add(VideoExporter.CaptionsNote);

        rate = source.ResolveFrameRate(out bool fromTimestamps);
        if (fromTimestamps)
        {
            notes.Add("the picture track states no frame duration; its timestamps show " + ExportSource.Describe(rate)
                + " fps, which the MP4 is written at");
        }

        resampler = new FrameRateResampler(rate.Numerator, rate.Denominator, rate.Numerator, rate.Denominator);
        ChooseAudio();
        expectedFrames = rate.FrameCountFor(source.Reader.Duration);

        pool = new PinnedFrameBufferPool();
        VideoDecoderOptions options = new VideoDecoderOptions { BufferPool = pool };
        videoDecoder = ExportDecoding.CreateVideoDecoder(source.VideoTrack, options);
        if (composite) alphaDecoder = ExportDecoding.CreateVideoDecoder(source.AlphaTrack, options);

        Decode();
        return Complete();
    }

    // Decides whose sound the MP4 carries: ours, unless the master is silent or has no audio track - then a backdrop
    // VIDEO's sound; a colour or image backdrop has none.
    private void ChooseAudio()
    {
        audioSource = VideoExportAudioSource.None;
        if (!request.IncludeAudio) return;

        bool silentMaster = composite && (source.IsSilent || source.AudioTrack == null);
        if (!silentMaster)
        {
            if (source.AudioTrack == null) return;

            ourAudio = new ExportAudioFeed.Ours(ExportDecoding.CreateAudioDecoder(source.AudioTrack), source.AudioTrack);
            audio = ourAudio;
            audioSource = VideoExportAudioSource.Ours;
            if (ourAudio.IsResampled)
            {
                notes.Add("the sound's " + ourAudio.DecoderRate.ToString(CultureInfo.InvariantCulture) + " Hz is outside "
                    + "what AAC takes, so it was resampled to " + ourAudio.SampleRate.ToString(CultureInfo.InvariantCulture)
                    + " Hz");
            }

            return;
        }

        string why = source.IsSilent ? "the source is flagged silent" : "the source has no audio track";
        if (backdrop.Kind != ExportBackdropKind.Video)
        {
            notes.Add(why + " and " + (backdrop.Kind == ExportBackdropKind.Image ? "an image" : "a colour")
                + " backdrop has no sound, so the MP4 has no audio track");
            return;
        }

        MediaInfo info = engine.Analyse(backdrop.VideoPath, cancellationToken);
        if (!info.HasAudio)
        {
            notes.Add(why + " and the backdrop video has no sound either, so the MP4 has no audio track");
            return;
        }

        audio = new ExportAudioFeed.Backdrop(engine, backdrop.VideoPath);
        audioSource = VideoExportAudioSource.Backdrop;
        notes.Add(why + ", so the backdrop video's sound was used");
    }

    private void Decode()
    {
        MediaTrackInfo videoTrack = source.VideoTrack;
        MediaTrackInfo alphaTrack = source.AlphaTrack;
        MediaTrackInfo audioTrack = ourAudio != null ? source.AudioTrack : null;

        while (source.Reader.TryReadPacket(out MediaPacket packet))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (packet.TrackId == videoTrack.Id)
            {
                Send(videoDecoder, packet, pictures);
            }
            else if (alphaTrack != null && packet.TrackId == alphaTrack.Id)
            {
                Send(alphaDecoder, packet, alphas);
            }
            else if (audioTrack != null && packet.TrackId == audioTrack.Id)
            {
                ourAudio.Decode(packet);
            }

            Pair(false);
        }

        videoDecoder.Drain();
        Pull(videoDecoder, pictures);
        if (alphaDecoder != null)
        {
            alphaDecoder.Drain();
            Pull(alphaDecoder, alphas);
        }

        Pair(true);
    }

    private void Send(IVideoDecoder decoder, in MediaPacket packet, Queue<VideoFrame> into)
    {
        VideoPacket videoPacket = new VideoPacket(packet.Data, packet.Timestamp, packet.IsKeyFrame, packet.Duration, sequence++);
        while (!decoder.SendPacket(videoPacket))
        {
            if (!Pull(decoder, into))
            {
                throw new VideoAuthoringException(
                    "The '" + decoder.CodecId + "' decoder refused a packet and had no frame to hand back.");
            }

            Pair(false);
        }

        Pull(decoder, into);
    }

    private static bool Pull(IVideoDecoder decoder, Queue<VideoFrame> into)
    {
        bool any = false;
        while (decoder.TryReceiveFrame(out VideoFrame frame))
        {
            into.Enqueue(frame);
            any = true;
        }

        return any;
    }

    // Pairs each picture with the alpha frame of the same timestamp, as a playback session does: older alpha frames
    // are released; a picture whose alpha frame never comes is written opaque.
    private void Pair(bool final)
    {
        while (pictures.Count > 0)
        {
            VideoFrame picture = pictures.Peek();
            if (!composite)
            {
                Accept(pictures.Dequeue());
                continue;
            }

            while (alphas.Count > 0 && alphas.Peek().Timestamp < picture.Timestamp) alphas.Dequeue().Dispose();

            if (alphas.Count > 0)
            {
                pictures.Dequeue();
                if (alphas.Peek().Timestamp == picture.Timestamp)
                {
                    picture.AttachAlpha(alphas.Dequeue(), source.AlphaTrack.IsAlphaPremultiplied);
                }
                else
                {
                    withoutAlpha++;
                }

                Accept(picture);
                continue;
            }

            if (!final) return;

            pictures.Dequeue();
            withoutAlpha++;
            Accept(picture);
        }

        if (final)
        {
            while (alphas.Count > 0) alphas.Dequeue().Dispose();
        }
    }

    // Takes ownership of a picture. The frame-rate fit answers for the PREVIOUS picture, so one is always held.
    private void Accept(VideoFrame picture)
    {
        try
        {
            if (writer == null) Start(picture);

            FrameRateDecision decision = resampler.Push(picture.Timestamp);
            if (decision.HasFrame && held != null) Emit(held, decision);
        }
        catch
        {
            picture.Dispose();
            throw;
        }

        held?.Dispose();
        held = picture;
    }

    // The first picture fixes the output size and layout, so the writer and the backdrop are made here.
    private void Start(VideoFrame first)
    {
        width = first.Width & ~1;
        height = first.Height & ~1;
        if (width == 0 || height == 0)
        {
            throw new VideoAuthoringException(
                "The source's picture is " + first.Width + "x" + first.Height + "; an MP4 picture needs at least 2x2.");
        }

        if (width != first.Width || height != first.Height)
        {
            notes.Add("the picture is " + first.Width + "x" + first.Height + "; 4:2:0 MP4 needs even sizes, so it was "
                + "cropped to " + width + "x" + height);
        }

        if (first.BitDepth > 8)
        {
            notes.Add("the picture is " + first.BitDepth + "-bit; MP4 exports are 8-bit, so it was reduced to 8 bits");
        }

        rgbaInput = composite || first.Layout != PlaybackLayout.I420;
        if (rgbaInput && !engine.Supports(VideoProcessorCapabilities.RgbaInput))
        {
            throw new VideoAuthoringException(
                "The source's picture is " + first.Layout + ", which the export hands to the writer as RGBA - and the '"
                + engine.Name + "' video-processing engine does not report RgbaInput.");
        }

        if (!composite && first.Layout != PlaybackLayout.I420)
        {
            notes.Add("the picture is " + first.Layout + "; it was converted to RGBA for the writer, which takes 4:2:0");
        }

        if (rgbaInput)
        {
            bgra = new byte[width * height * 4];
            rgba = new byte[width * height * 4];
        }
        else
        {
            planeY = new byte[width * height];
            planeU = new byte[(width / 2) * (height / 2)];
            planeV = new byte[(width / 2) * (height / 2)];
        }

        if (composite)
        {
            grey = new ExportBackdropSource.Solid(width, height, ExportBackdrop.DefaultColour);
            backdropSource = CreateBackdrop();
            if (backdropSource is ExportBackdropSource.Moving moving)
            {
                expectedFrames = Math.Max(expectedFrames, rate.FrameCountFor(moving.SourceDuration));
                if (moving.IsScaled) notes.Add("the backdrop video is " + moving.SourceSize + " and was scaled to " + width + "x" + height);
            }
        }

        VideoColorInfo color = first.Color.Resolve(first.Height);
        StandardVideoRequest writerRequest = new StandardVideoRequest
        {
            OutputPath = request.OutputPath,
            Profile = request.Codec == VideoExportCodec.Hevc ? StandardVideoProfile.Mp4HevcAac : StandardVideoProfile.Mp4H264Aac,
            Width = width,
            Height = height,
            FrameRate = rate,
            InputPixelLayout = rgbaInput ? CoreLayout.Rgba : CoreLayout.I420,
            Quality = request.Quality,
            KeyframeIntervalFrames = request.KeyframeIntervalFrames,
            ColorRange = color.Range == PlaybackRange.Full ? CoreRange.Full : CoreRange.Limited,
            ColorMatrix = MatrixFor(color.Matrix),
            RotationDegrees = 0,
            FastStart = request.FastStart,
            HasAudio = audio != null,
            AudioSampleRate = audio != null ? audio.SampleRate : 48000,
            AudioChannels = audio != null ? audio.Channels : 2,
            AudioBitrateKilobitsPerSecond = request.AudioBitrateKilobitsPerSecond,
            Title = VideoExporter.ResolveTitle(request, source),
            CreationTime = DateTimeOffset.UtcNow,
        };

        writer = engine.CreateWriter(writerRequest);
    }

    private ExportBackdropSource CreateBackdrop()
    {
        switch (backdrop.Kind)
        {
            case ExportBackdropKind.Image:
                if (backdrop.ImageRgba != null)
                {
                    return new ExportBackdropSource.Still(width, height, backdrop.ImageRgba, backdrop.ImageWidth, backdrop.ImageHeight);
                }

                byte[] pixels = ExportBackdropSource.Still.Load(backdrop.ImagePath, out int imageWidth, out int imageHeight);
                if (imageWidth != width || imageHeight != height)
                {
                    notes.Add("the backdrop image is " + imageWidth + "x" + imageHeight + " and was scaled to " + width + "x" + height);
                }

                return new ExportBackdropSource.Still(width, height, pixels, imageWidth, imageHeight);
            case ExportBackdropKind.Video:
                return new ExportBackdropSource.Moving(engine, backdrop.VideoPath, width, height);
            default:
                return new ExportBackdropSource.Solid(width, height, backdrop.Colour);
        }
    }

    // Writes one source picture as many times as the frame-rate fit says (0 = dropped, 2+ = repeated).
    private void Emit(VideoFrame frame, FrameRateDecision decision)
    {
        if (decision.OutputCount == 0)
        {
            dropped++;
            return;
        }

        if (decision.OutputCount > 1) duplicated++;

        if (rgbaInput)
        {
            ExportPixels.ToBgra(frame, width, height, bgra);
        }
        else
        {
            CopyI420(frame);
        }

        for (long i = 0; i < decision.OutputCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan time = rate.TimestampOf(written);

            if (composite)
            {
                byte[] behind = backdropSource.PictureAt(time);
                if (behind == null)
                {
                    behind = grey.PictureAt(time);
                    framesOverGrey++;
                }

                ExportPixels.CompositeOver(
                    bgra,
                    frame.HasAlpha ? frame.A : VideoFramePlane.Empty,
                    frame.BitDepth,
                    frame.IsAlphaPremultiplied,
                    behind,
                    rgba,
                    width,
                    height);
                WriteRgba(time);
            }
            else if (rgbaInput)
            {
                ExportPixels.BgraToRgba(bgra, rgba);
                WriteRgba(time);
            }
            else
            {
                CoreFrame data = CoreFrame.CreateI420(
                    width,
                    height,
                    CorePlane.FromArray(planeY, width, height),
                    CorePlane.FromArray(planeU, width / 2, height / 2),
                    CorePlane.FromArray(planeV, width / 2, height / 2),
                    time,
                    written);
                writer.WriteFrame(data);
                Advance();
            }
        }
    }

    private void WriteRgba(TimeSpan time)
    {
        CoreFrame data = CoreFrame.CreateRgba(width, height, CorePlane.FromArray(rgba, width * 4, height), time, written);
        writer.WriteFrame(data);
        Advance();
    }

    private void Advance()
    {
        written++;
        audio?.Pump(writer, AudioFramesFor(written));

        if (request.ProgressCallback == null || expectedFrames <= 0) return;

        int percent = (int)Math.Min(99, written * 100 / expectedFrames);
        if (percent <= lastPercent) return;
        lastPercent = percent;
        request.ProgressCallback(new AuthoringProgress(VideoExporter.PassLabel, 1, 1, percent));
    }

    private long AudioFramesFor(long frames) =>
        audio == null ? 0 : (long)((Int128)frames * rate.Denominator * audio.SampleRate / rate.Numerator);

    // An 8-bit I420 picture is copied as it is; a deeper one is reduced to 8 bits by rounding.
    private void CopyI420(VideoFrame frame)
    {
        CopyPlane(frame.Y, planeY, width, height, frame.BitDepth);
        CopyPlane(frame.U, planeU, width / 2, height / 2, frame.BitDepth);
        CopyPlane(frame.V, planeV, width / 2, height / 2, frame.BitDepth);
    }

    private static void CopyPlane(in VideoFramePlane plane, byte[] target, int columns, int rows, int bitDepth)
    {
        int shift = bitDepth - 8;
        for (int row = 0; row < rows; row++)
        {
            ReadOnlySpan<byte> line = plane.GetRowBytes(row);
            Span<byte> output = target.AsSpan(row * columns, columns);
            if (shift == 0)
            {
                line.Slice(0, columns).CopyTo(output);
                continue;
            }

            int round = 1 << (shift - 1);
            for (int x = 0; x < columns; x++)
            {
                int sample = line[2 * x] | (line[(2 * x) + 1] << 8);
                int value = (sample + round) >> shift;
                output[x] = value > 255 ? (byte)255 : (byte)value;
            }
        }
    }

    private VideoExportResult Complete()
    {
        if (writer == null || held == null)
        {
            throw new VideoAuthoringException(
                "The export decoded no pictures from '" + request.SourcePath + "', so there is nothing to write.");
        }

        FrameRateDecision last = resampler.Complete();
        if (last.HasFrame) Emit(held, last);
        held.Dispose();
        held = null;

        long ours = written;
        if (backdropSource is ExportBackdropSource.Moving)
        {
            // The backdrop outlasts ours: it plays on alone until it ends.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan time = rate.TimestampOf(written);
                byte[] behind = backdropSource.PictureAt(time);
                if (behind == null) break;

                behind.AsSpan().CopyTo(rgba);
                WriteRgba(time);
            }

            tailFrames = written - ours;
        }

        if (audio != null) audio.Finish(writer, AudioFramesFor(written));

        writer.Finish();
        request.ProgressCallback?.Invoke(new AuthoringProgress(VideoExporter.PassLabel, 1, 1, 100));

        AddClosingNotes();

        return new VideoExportResult(
            request.OutputPath,
            engine.Name,
            written,
            rate.TimestampOf(written),
            audioSource,
            notes,
            request.Codec,
            width,
            height,
            rate);
    }

    private void AddClosingNotes()
    {
        if (dropped > 0 || duplicated > 0)
        {
            notes.Add("the source's timing is not a steady " + ExportSource.Describe(rate) + " fps: "
                + dropped.ToString(CultureInfo.InvariantCulture) + " picture(s) dropped and "
                + duplicated.ToString(CultureInfo.InvariantCulture) + " repeated to fit it");
        }

        if (withoutAlpha > 0)
        {
            notes.Add(withoutAlpha.ToString(CultureInfo.InvariantCulture) + " picture(s) had no alpha frame and were "
                + "written opaque");
        }

        if (framesOverGrey > 0)
        {
            notes.Add("the backdrop video ended first; the last " + framesOverGrey.ToString(CultureInfo.InvariantCulture)
                + " picture(s) were composited over 50% grey");
        }

        if (tailFrames > 0)
        {
            notes.Add("the backdrop video outlasts the source; it plays on alone for "
                + tailFrames.ToString(CultureInfo.InvariantCulture) + " more picture(s)");
        }

        if (audio != null && audio.FramesCut > 0 && audioSource == VideoExportAudioSource.Ours
            && audio.FramesCut > AudioFramesFor(1))
        {
            notes.Add("the sound runs longer than the picture; it was cut where the picture ends");
        }
    }

    private static VideoColorMatrix MatrixFor(VideoMatrixCoefficients matrix) => matrix switch
    {
        VideoMatrixCoefficients.Bt470Bg => VideoColorMatrix.Bt601,
        VideoMatrixCoefficients.Smpte170M => VideoColorMatrix.Bt601,
        VideoMatrixCoefficients.Bt2020NonConstantLuminance => VideoColorMatrix.Bt2020NonConstant,
        VideoMatrixCoefficients.Bt2020ConstantLuminance => VideoColorMatrix.Bt2020Constant,
        _ => VideoColorMatrix.Bt709,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        // A writer disposed without Finish deletes what it wrote - a failed or cancelled export leaves nothing.
        try
        {
            writer?.Dispose();
        }
        finally
        {
            held?.Dispose();
            while (pictures.Count > 0) pictures.Dequeue().Dispose();
            while (alphas.Count > 0) alphas.Dequeue().Dispose();
            videoDecoder?.Dispose();
            alphaDecoder?.Dispose();
            audio?.Dispose();
            backdropSource?.Dispose();
            pool?.Dispose();
            source?.Dispose();
        }
    }
}
