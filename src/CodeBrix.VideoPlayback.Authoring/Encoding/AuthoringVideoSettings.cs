using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Authoring.Effects;

namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>
/// How the picture is encoded: which AV1 encoder, how hard it works, how big the frame is, how fast it runs,
/// and what colour grade is baked into it.
/// </summary>
/// <remarks>
/// <para>
/// The pixel format is NOT a setting. Every file this library authors is 8-bit 4:2:0
/// (<see cref="PixelFormat" />), which is what the streamable profile recommends and what every decoder and
/// every display path handles without a conversion nobody asked for. The chain pins it twice - once as the
/// last filter in the chain and once as the encoder's own <c>-pix_fmt</c> - so neither a source in some
/// other format nor a filter that hands on RGB can change it.
/// </para>
/// <para>THE ORDER OF THE ONE FILTER CHAIN, and why it is that order:</para>
/// <list type="number">
///   <item><description>
///     <c>scale</c> - the resample runs FIRST, so everything after it works on the smaller picture.
///   </description></item>
///   <item><description>
///     <c>fps</c> - only when <see cref="FrameRateMode" /> is
///     <see cref="AuthoringFrameRateMode.Filter" />. Frames are dropped here, before the expensive per-pixel
///     work below, so a grade is never computed for a frame that is about to be discarded.
///   </description></item>
///   <item><description>
///     <c>lut3d</c> - the colour grade. FFmpeg's lookup filter works in RGB and inserts the conversion
///     itself; the playback presenter applies its lookup table to RGB after the same conversion, so the two
///     stages grade the same numbers.
///   </description></item>
///   <item><description>
///     <c>format</c> - LAST, so whatever the chain did, the encoder is handed 8-bit 4:2:0.
///   </description></item>
/// </list>
/// </remarks>
public sealed class AuthoringVideoSettings
{
    /// <summary>The one pixel format this library authors: 8-bit 4:2:0.</summary>
    public const string PixelFormat = "yuv420p";

    private int speedPreset = 6;
    private int constantRateFactor = 30;
    private AuthoringFrameSize frameSize = AuthoringFrameSize.Source;
    private string scalerFlags = "lanczos";

    /// <summary>Which AV1 encoder to ask FFmpeg for. SVT-AV1 by default.</summary>
    public AuthoringVideoEncoder Encoder { get; set; } = AuthoringVideoEncoder.LibSvtAv1;

    /// <summary>
    /// How hard the encoder works: SVT-AV1's <c>-preset</c>, 0 (slowest, best) to 13 (fastest, worst), or
    /// libaom's <c>-cpu-used</c>, 0 (slowest) to 8 (fastest). Six by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 13.</exception>
    public int SpeedPreset
    {
        get => speedPreset;
        set
        {
            if (value < 0 || value > 13)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A speed preset runs 0 to 13 for SVT-AV1 and 0 to 8 for libaom.");
            }

            speedPreset = value;
        }
    }

    /// <summary>
    /// The constant rate factor - lower is better and bigger. Thirty by default. AV1's rate factor is
    /// resolution-relative: the same number looks better the more pixels are hiding the error, so a rung
    /// table normally lowers it as the frame gets smaller.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 63.</exception>
    public int ConstantRateFactor
    {
        get => constantRateFactor;
        set
        {
            if (value < 0 || value > 63)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A constant rate factor runs 0 to 63.");
            }

            constantRateFactor = value;
        }
    }

    /// <summary>The frame size to scale to. The source's own size by default, which emits no scale filter.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public AuthoringFrameSize FrameSize
    {
        get => frameSize;
        set => frameSize = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The resampler the scale filter is asked for. Lanczos by default, because the plain scale filter's own
    /// default is bicubic and a downscale of real video is exactly where the difference shows.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null or blank.</exception>
    public string ScalerFlags
    {
        get => scalerFlags;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A scaler flag is required; 'lanczos' is the default.", nameof(value));
            }

            scalerFlags = value;
        }
    }

    /// <summary>The frame rate to conform the output to, or 0 to leave the source's own rate alone.</summary>
    public double FrameRate { get; set; }

    /// <summary>Where <see cref="FrameRate" /> is applied. At the encoder by default.</summary>
    public AuthoringFrameRateMode FrameRateMode { get; set; } = AuthoringFrameRateMode.Encoder;

    /// <summary>
    /// The keyframe interval in FRAMES, or 0 to leave the encoder's own default alone. Set it: every AV1
    /// encoder's default interval is far longer than a scrub wants, and a cue can only exist where a
    /// keyframe does.
    /// </summary>
    public int KeyframeIntervalFrames { get; set; }

    /// <summary>
    /// True to let FFmpeg apply the source's rotation metadata while decoding, so the frames that reach the
    /// filter chain are already the right way up and the finished file needs no rotation at all. True by
    /// default, which is what makes a portrait phone recording come out as TRUE portrait pixels.
    /// </summary>
    public bool AutoRotate { get; set; } = true;

    /// <summary>
    /// The colour-grade chain to bake into the picture, in order. Empty by default.
    /// </summary>
    /// <remarks>
    /// One entry applied at 100 percent is handed to FFmpeg as it stands. Anything else - two entries, or one
    /// at some other percentage - is folded into ONE effective table by the core's LutComposer and written to
    /// a temporary ".cube" file, so FFmpeg still runs exactly one lookup. That is the same arithmetic the
    /// playback presenter uses to compose its own chain, so the graded video and the graded playback agree.
    /// </remarks>
    public IList<AuthoringLutInput> Luts { get; } = new List<AuthoringLutInput>();

    /// <summary>
    /// Where to KEEP the one effective ".cube" table this video is graded with, or null - the default - to
    /// keep nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it and the run leaves that table behind for you to inspect, to diff against a previous grade, or
    /// to commit beside the video it produced. <see cref="VideoAuthoringResult.ComposedLutPath" /> records
    /// where it went. Any missing folder in the path is created.
    /// </para>
    /// <para>
    /// IT IS POPULATED WHENEVER THERE IS A GRADE AT ALL, and it means the same thing both times - "the table
    /// this file was graded with" - but it gets there by two different routes, and the difference is
    /// visible:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     WHEN THE CHAIN COMPOSES - two or more tables, or one at any percentage other than 100 - the folded
    ///     table is written to exactly this path and FFmpeg's lookup reads it FROM there. What is left on the
    ///     disk is byte for byte the file the encode consumed, not a copy of it, and the rendered command line
    ///     names this path.
    ///   </description></item>
    ///   <item><description>
    ///     WHEN ONE TABLE IS USED AT FULL STRENGTH there is nothing to fold: FFmpeg reads the caller's own
    ///     file and the command line goes on naming THAT file. A byte-for-byte COPY of it is placed here, so
    ///     the property still holds the table the picture was graded with.
    ///   </description></item>
    /// </list>
    /// <para>
    /// AN EMPTY CHAIN KEEPS NOTHING. With no lookup table in <see cref="Luts" /> there is no grade to record,
    /// nothing is written here whatever this is set to, and the result's path is null.
    /// </para>
    /// <para>
    /// <see cref="CbvAuthor.RenderCommands" /> honours the path in the command line it renders where the
    /// command names it at all - the composing case - but writes nothing in either case, because a dry run
    /// touches no disk. The table appears only when <see cref="CbvAuthor.Write" /> runs.
    /// </para>
    /// </remarks>
    public string ComposedLutPath { get; set; }

    /// <summary>A name for the video track, or null. Carried by the bespoke flavour only.</summary>
    public string TrackName { get; set; }
}
