using System;
using System.Globalization;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoProcessing.Core.Media;

using PlaybackCodecIds = CodeBrix.VideoPlayback.Decoding.VideoCodecIds;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// The source of an export, opened through the core's container readers: which flavour it is, which tracks it
/// carries, and the nominal frame rate it states.
/// </summary>
/// <remarks>
/// The tracks are picked exactly as a playback session picks them: the first picture track, the first
/// alpha-plane track, the first audio track.
/// </remarks>
internal sealed class ExportSource : IDisposable
{
    /// <summary>The rates a stated frame duration is snapped to when it is within a tick of one.</summary>
    private static readonly (int Numerator, int Denominator)[] CommonRates =
    {
        (24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (48, 1), (50, 1), (60000, 1001), (60, 1),
        (120, 1), (15, 1), (12, 1), (10, 1), (5, 1), (1, 1),
    };

    private ExportSource(string path, IMediaContainerReader reader)
    {
        Path = path;
        Reader = reader;

        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == MediaTrackKind.Video && track.IsAlphaPlane)
            {
                if (AlphaTrack == null) AlphaTrack = track;
            }
            else if (track.Kind == MediaTrackKind.Video && VideoTrack == null) VideoTrack = track;
            else if (track.Kind == MediaTrackKind.Audio && AudioTrack == null) AudioTrack = track;
        }

        if (VideoTrack == null) AlphaTrack = null;

        if (reader is CbvReader cbv)
        {
            Mode = cbv.Version >= CbvFormat.MasterVersion ? "mode3" : "mode2";
            IsSilent = cbv.IsSilent;
        }
        else
        {
            Mode = "mode1";
            if (reader is MatroskaReader matroska && !string.IsNullOrWhiteSpace(matroska.Title)) Title = matroska.Title;
        }

        HasCaptionsOrChapters = reader.CaptionTracks.Count > 0 || reader.Chapters.Count > 0;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>The open reader.</summary>
    public IMediaContainerReader Reader { get; }

    /// <summary><c>mode1</c> (WebM profile), <c>mode2</c> (bespoke) or <c>mode3</c> (master).</summary>
    public string Mode { get; }

    /// <summary>The picture track, or null.</summary>
    public MediaTrackInfo VideoTrack { get; }

    /// <summary>The alpha-plane track, or null.</summary>
    public MediaTrackInfo AlphaTrack { get; }

    /// <summary>The first audio track, or null.</summary>
    public MediaTrackInfo AudioTrack { get; }

    /// <summary>True when the file is a master flagged silent.</summary>
    public bool IsSilent { get; }

    /// <summary>The file's own title, or null.</summary>
    public string Title { get; }

    /// <summary>True when the file carries caption tracks or chapters, neither of which an MP4 export carries.</summary>
    public bool HasCaptionsOrChapters { get; }

    /// <summary>True when the picture composites over a backdrop: the file has an alpha-plane track.</summary>
    public bool HasAlpha => AlphaTrack != null;

    /// <summary>Opens a source.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The source.</returns>
    /// <exception cref="VideoAuthoringException">The file cannot be read as a CodeBrix video, or has no picture.</exception>
    public static ExportSource Open(string path)
    {
        IMediaContainerReader reader;
        try
        {
            reader = MediaContainers.Open(path);
        }
        catch (VideoPlaybackException ex)
        {
            throw new VideoAuthoringException("The export source '" + path + "' cannot be read: " + ex.Message, ex);
        }

        ExportSource source = new ExportSource(path, reader);
        if (source.VideoTrack == null)
        {
            reader.Dispose();
            throw new VideoAuthoringException(
                "The export source '" + path + "' has no picture track, so there is nothing to write as an MP4 video.");
        }

        return source;
    }

    /// <summary>
    /// The bit depth the picture track states, or - for an AV1 track that states none - the one its
    /// <c>av1C</c> configuration record carries; 0 when neither says.
    /// </summary>
    public int StatedBitDepth
    {
        get
        {
            if (VideoTrack.BitDepth > 0) return VideoTrack.BitDepth;

            ReadOnlySpan<byte> av1C = VideoTrack.CodecPrivate.Span;
            if (string.Equals(VideoTrack.CodecId, PlaybackCodecIds.Av1, StringComparison.OrdinalIgnoreCase)
                && av1C.Length >= 4
                && (av1C[0] & 0x80) != 0)
            {
                bool high = (av1C[2] & 0x40) != 0;
                bool twelve = (av1C[2] & 0x20) != 0;
                return high ? (twelve ? 12 : 10) : 8;
            }

            return 0;
        }
    }

    /// <summary>The nominal frame rate the track states, or <see cref="FrameRate.Unspecified" /> when it states none.</summary>
    public FrameRate StatedFrameRate => FrameRateFor(VideoTrack.DefaultDuration);

    /// <summary>
    /// The nominal frame rate: the stated one, or - when the track states none - the one its first two picture
    /// timestamps (or its first packet's duration) show, read through a second reader so this one is untouched.
    /// </summary>
    /// <param name="fromTimestamps">True when the rate had to be read from the timestamps.</param>
    /// <returns>The rate; never unspecified.</returns>
    /// <exception cref="VideoAuthoringException">The picture track's timing cannot be told.</exception>
    public FrameRate ResolveFrameRate(out bool fromTimestamps)
    {
        fromTimestamps = false;
        FrameRate stated = StatedFrameRate;
        if (stated.IsSpecified) return stated;

        fromTimestamps = true;
        using IMediaContainerReader scan = MediaContainers.Open(Path);
        TimeSpan? first = null;
        while (scan.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != VideoTrack.Id) continue;

            if (first == null)
            {
                if (packet.Duration > TimeSpan.Zero) return FrameRateFor(packet.Duration);
                first = packet.Timestamp;
                continue;
            }

            if (packet.Timestamp > first.Value) return FrameRateFor(packet.Timestamp - first.Value);
        }

        throw new VideoAuthoringException(
            "The picture track of '" + Path + "' states no frame duration and its timestamps do not show one, so "
            + "the MP4's constant frame rate cannot be chosen.");
    }

    /// <summary>
    /// Turns a frame duration into an exact rate: a common rate when the duration is within a tick of it,
    /// otherwise exactly ten million over the duration's ticks.
    /// </summary>
    /// <param name="frameDuration">The duration of one frame.</param>
    /// <returns>The rate, or unspecified for a duration that is not positive.</returns>
    internal static FrameRate FrameRateFor(TimeSpan frameDuration)
    {
        long ticks = frameDuration.Ticks;
        if (ticks <= 0) return FrameRate.Unspecified;

        foreach ((int numerator, int denominator) in CommonRates)
        {
            long exact = TimeSpan.TicksPerSecond * denominator / numerator;
            if (Math.Abs(exact - ticks) <= 1) return new FrameRate(numerator, denominator);
        }

        if (ticks > int.MaxValue) return new FrameRate(1, (int)Math.Min(int.MaxValue, ticks / TimeSpan.TicksPerSecond));
        return new FrameRate((int)TimeSpan.TicksPerSecond, (int)ticks);
    }

    /// <summary>A rate as the dry run and the notes write it: <c>30</c>, <c>30000/1001</c>.</summary>
    /// <param name="rate">The rate.</param>
    /// <returns>The text.</returns>
    internal static string Describe(FrameRate rate) =>
        rate.Denominator == 1
            ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
            : rate.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + rate.Denominator.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Dispose() => Reader.Dispose();
}
