using System;
using System.Collections.Generic;
using System.Globalization;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core.Frames;

namespace CodeBrix.VideoPlayback.Authoring.Internal;

/// <summary>
/// The in-process stand-in for the ffmpeg prepare pass: what an in-process run does to the decoded picture when
/// the engine is NOT the ffmpeg one and the request asks for a frame size, a frame rate, a colour grade or a
/// rotated source turned upright - served by the core's managed processing primitives
/// (<c>CodeBrix.VideoPlayback.Processing</c>).
/// </summary>
/// <remarks>
/// <para>
/// THE ORDER is the prepare pass's own, so the two paths agree frame for frame: the rotation first (FFmpeg
/// applies it while decoding, ahead of every filter), then <c>scale</c>, then <c>fps</c>, then <c>lut3d</c>. A
/// frame rate given for <see cref="AuthoringFrameRateMode.Encoder" /> is an <c>fps</c> filter after the grade on
/// the FFmpeg command line; dropping and duplicating whole frames before or after a per-frame grade gives the
/// same pictures, so here it always runs before the grade and a dropped frame is never graded.
/// </para>
/// <para>
/// One instance describes the shaping of one run. <see cref="Wrap" /> puts it round a frame source; the
/// colour pass and the alpha pass each wrap their own decode, and because rotation, scaling and frame-rate
/// conversion are deterministic the two come out in lock step. The alpha plane is never graded.
/// </para>
/// </remarks>
internal sealed class ManagedFrameShaping
{
    private readonly List<string> ignoredScalerFlags = new List<string>();

    private ManagedFrameShaping()
    {
    }

    /// <summary>Whether a rotated source is turned upright.</summary>
    internal bool AutoRotate { get; private set; }

    /// <summary>The frame size to scale to; <see cref="AuthoringFrameSize.Source" /> for none.</summary>
    internal AuthoringFrameSize FrameSize { get; private set; } = AuthoringFrameSize.Source;

    /// <summary>The resampler named by the request's scaler flags, or null to choose per geometry.</summary>
    internal ScalingFilter? Filter { get; private set; }

    /// <summary>The scaler flag tokens that have no in-process meaning, in the order given.</summary>
    internal IReadOnlyList<string> IgnoredScalerFlags => ignoredScalerFlags;

    /// <summary>True when the scaler flags named neither <c>lanczos</c> nor <c>bilinear</c>.</summary>
    internal bool ScalerFlagsUnrecognised { get; private set; }

    /// <summary>The target frame rate's numerator, or 0 for no frame-rate conversion.</summary>
    internal int FrameRateNumerator { get; private set; }

    /// <summary>The target frame rate's denominator, or 0 for no frame-rate conversion.</summary>
    internal int FrameRateDenominator { get; private set; }

    /// <summary>The colour grade's table, or null for no grade (or a dry run, which reads no table).</summary>
    internal Lut3D Lut { get; private set; }

    /// <summary>Whether the request grades the picture at all.</summary>
    internal bool HasGrade { get; private set; }

    /// <summary>What the grade is called: the chain's own description.</summary>
    internal string GradeTitle { get; private set; }

    /// <summary>True when a frame rate is set.</summary>
    internal bool ConvertsFrameRate => FrameRateNumerator > 0;

    /// <summary>
    /// True when the request's own settings shape the picture - everything except a rotated source, which only
    /// reading the source can reveal.
    /// </summary>
    internal bool ShapesFromRequest => !FrameSize.IsSourceSize || ConvertsFrameRate || HasGrade;

    /// <summary>Builds the shaping a request asks for.</summary>
    /// <param name="request">The request.</param>
    /// <param name="lut">What its colour-grade chain reduced to.</param>
    /// <param name="loadLut">
    /// True to read the table (a real run - the resolver has already written a composed one); false for a dry
    /// run, which touches no disk.
    /// </param>
    /// <returns>The shaping.</returns>
    /// <exception cref="VideoAuthoringException">The table cannot be read as a colour grade.</exception>
    internal static ManagedFrameShaping Create(VideoAuthoringRequest request, ResolvedLutChain lut, bool loadLut)
    {
        AuthoringVideoSettings video = request.Video;
        ManagedFrameShaping shaping = new ManagedFrameShaping
        {
            AutoRotate = video.AutoRotate,
            FrameSize = video.FrameSize,
        };

        shaping.ParseScalerFlags(video.ScalerFlags);

        if (video.FrameRate > 0d)
        {
            ToRational(video.FrameRate, out int numerator, out int denominator);
            shaping.FrameRateNumerator = numerator;
            shaping.FrameRateDenominator = denominator;
        }

        if (lut != null && lut.HasGrade)
        {
            shaping.HasGrade = true;
            shaping.GradeTitle = LutChainResolver.DescribeChain(video.Luts);
            if (loadLut) shaping.Lut = LoadTable(lut.FilterPath);
        }

        return shaping;
    }

    /// <summary>The clockwise turn a source needs, from its frame source's rotation.</summary>
    /// <param name="rotationDegrees">
    /// The CLOCKWISE rotation a player applies to the stored picture (<c>VideoSourceInfo.RotationDegrees</c>).
    /// </param>
    /// <returns>The turn, or <see cref="RotationAngle.None" /> when AutoRotate is off.</returns>
    /// <remarks>
    /// The core's <see cref="PlaneRotator.FromDisplayMatrixRotation" /> takes FFmpeg's COUNTER-clockwise
    /// display-matrix angle; the engine contract states the clockwise one, so the sign is turned round on the
    /// way in. The contract has no way to report a MIRRORED display matrix, so no flip is ever applied.
    /// </remarks>
    internal RotationAngle RotationFor(int rotationDegrees) =>
        AutoRotate && rotationDegrees % 360 != 0
            ? PlaneRotator.FromDisplayMatrixRotation(-rotationDegrees)
            : RotationAngle.None;

    /// <summary>True when a source with this rotation needs any shaping at all.</summary>
    /// <param name="rotationDegrees">The source's clockwise rotation.</param>
    /// <returns>True when something would change.</returns>
    internal bool IsNeededFor(int rotationDegrees) => ShapesFromRequest || RotationFor(rotationDegrees) != RotationAngle.None;

    /// <summary>The size a picture of the given (already turned) size is scaled to.</summary>
    /// <param name="width">The width after rotation.</param>
    /// <param name="height">The height after rotation.</param>
    /// <param name="targetWidth">The width to scale to.</param>
    /// <param name="targetHeight">The height to scale to.</param>
    /// <remarks>
    /// The aspect-preserving forms do what FFmpeg's <c>-2</c> does: the free side is the stated side times the
    /// input's aspect ratio, rounded to the nearest whole number of PAIRS (<c>av_rescale</c>'s round-to-nearest),
    /// and never less than one pair.
    /// </remarks>
    internal void TargetSize(int width, int height, out int targetWidth, out int targetHeight)
    {
        switch (FrameSize.Kind)
        {
            case AuthoringFrameSizeKind.Exact:
                targetWidth = FrameSize.Width;
                targetHeight = FrameSize.Height;
                return;

            case AuthoringFrameSizeKind.LongSide:
            case AuthoringFrameSizeKind.ShortSide:
                bool landscape = width >= height;
                bool statedIsWidth = FrameSize.Kind == AuthoringFrameSizeKind.LongSide ? landscape : !landscape;
                int pixels = FrameSize.Pixels;
                if (statedIsWidth)
                {
                    targetWidth = pixels;
                    targetHeight = EvenRescale(pixels, height, width);
                }
                else
                {
                    targetHeight = pixels;
                    targetWidth = EvenRescale(pixels, width, height);
                }

                return;

            default:
                targetWidth = width;
                targetHeight = height;
                return;
        }
    }

    /// <summary>The resampler a geometry is scaled with.</summary>
    /// <param name="sourceWidth">The width before scaling.</param>
    /// <param name="sourceHeight">The height before scaling.</param>
    /// <param name="targetWidth">The width after.</param>
    /// <param name="targetHeight">The height after.</param>
    /// <returns>The named filter, or the core's per-geometry default when the flags named none.</returns>
    internal ScalingFilter FilterFor(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight) =>
        Filter ?? PlaneScaler.ChooseDefault(sourceWidth, sourceHeight, targetWidth, targetHeight);

    /// <summary>
    /// The deterministic lines a dry run lists for the managed steps the request alone decides, in the order
    /// they run. A rotation is not among them: only reading the source can reveal it.
    /// </summary>
    /// <returns>The lines - <c>scale ...</c>, <c>fps ...</c>, <c>grade ...</c> - possibly none.</returns>
    internal List<string> DescribeDryRun()
    {
        List<string> lines = new List<string>(3);

        if (!FrameSize.IsSourceSize)
        {
            lines.Add("scale " + DryRunSizeToken() + " " + (Filter.HasValue ? FilterName(Filter.Value) : "default-filter"));
        }

        if (ConvertsFrameRate) lines.Add(DescribeFrameRate());
        if (HasGrade) lines.Add("grade \"" + GradeTitle + "\" tetrahedral");

        return lines;
    }

    /// <summary>The lines a real run lists for the managed steps that ran, in the order they ran.</summary>
    /// <param name="rotation">The turn applied.</param>
    /// <param name="turnedWidth">The width after the turn.</param>
    /// <param name="turnedHeight">The height after the turn.</param>
    /// <returns>The lines - <c>rotate ...</c>, <c>scale ...</c>, <c>fps ...</c>, <c>grade ...</c>.</returns>
    internal List<string> DescribeRun(RotationAngle rotation, int turnedWidth, int turnedHeight)
    {
        List<string> lines = new List<string>(4);

        if (rotation != RotationAngle.None) lines.Add("rotate " + Number((int)rotation));

        if (!FrameSize.IsSourceSize)
        {
            TargetSize(turnedWidth, turnedHeight, out int width, out int height);
            lines.Add("scale " + Number(width) + "x" + Number(height) + " "
                + FilterName(FilterFor(turnedWidth, turnedHeight, width, height)));
        }

        if (ConvertsFrameRate) lines.Add(DescribeFrameRate());

        if (HasGrade)
        {
            lines.Add("grade \"" + GradeTitle + "\" "
                + (Lut != null ? Number(Lut.Size) + "-node " : string.Empty) + "tetrahedral");
        }

        return lines;
    }

    /// <summary>
    /// The notes a run adds for the parts of the request that steer FFmpeg's prepare pass and have no in-process
    /// meaning, said rather than silently ignored.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The notes, possibly none.</returns>
    internal List<string> DescribeIgnoredOptions(VideoAuthoringRequest request)
    {
        List<string> notes = new List<string>(3);

        if (!FrameSize.IsSourceSize && ScalerFlagsUnrecognised)
        {
            notes.Add(
                "ScalerFlags '" + request.Video.ScalerFlags + "' names no resampler the in-process scaler has "
                + "(it has 'lanczos' and 'bilinear'), so the default was used: Lanczos when either side shrinks, "
                + "bilinear otherwise.");
        }
        else if (!FrameSize.IsSourceSize && ignoredScalerFlags.Count > 0)
        {
            notes.Add(
                "ScalerFlags '" + string.Join("+", ignoredScalerFlags) + "' steer FFmpeg's scaler only and were "
                + "ignored in process; the picture was scaled with " + FilterName(Filter.Value) + ".");
        }

        if (ConvertsFrameRate && request.Video.FrameRateMode == AuthoringFrameRateMode.Encoder)
        {
            notes.Add(
                "FrameRateMode = Encoder has no separate in-process meaning: the frame rate was converted by "
                + "dropping and duplicating whole frames, before the grade, exactly as FrameRateMode = Filter does "
                + "(FFmpeg's fps filter) - the same frames the FFmpeg path keeps in either mode.");
        }

        return notes;
    }

    /// <summary>Wraps a frame source so the frames it hands on are shaped.</summary>
    /// <param name="source">The engine's frame source; disposed with the wrapper.</param>
    /// <param name="alphaOnly">
    /// True for the alpha pass: the frames handed on are Gray, made from the alpha plane (fully opaque where a
    /// frame has none), rotated, scaled and rate-converted exactly as the picture is, and never graded.
    /// </param>
    /// <returns>The shaped source.</returns>
    internal IVideoFrameSource Wrap(IVideoFrameSource source, bool alphaOnly) =>
        new ShapedVideoFrameSource(source, this, alphaOnly);

    /// <summary>The name a filter is listed with.</summary>
    /// <param name="filter">The filter.</param>
    /// <returns><c>bilinear</c> or <c>lanczos3</c>.</returns>
    internal static string FilterName(ScalingFilter filter) =>
        filter == ScalingFilter.Bilinear ? "bilinear" : "lanczos3";

    /// <summary>
    /// The rational FFmpeg makes of a decimal frame rate: the closest fraction with neither term above
    /// 1,001,000 (<c>av_parse_video_rate</c>'s bound) - 30 is 30/1, 29.97 is 2997/100, 23.976 is 2997/125.
    /// </summary>
    /// <param name="rate">The rate, frames a second; greater than zero.</param>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The denominator.</param>
    internal static void ToRational(double rate, out int numerator, out int denominator)
    {
        const long bound = 1001000;

        // Continued fraction convergents, stopping at the last one inside the bound or at an exact match.
        long previousNumerator = 0, previousDenominator = 1;
        long currentNumerator = 1, currentDenominator = 0;
        double remainder = rate;

        for (int step = 0; step < 64; step++)
        {
            double whole = Math.Floor(remainder);
            long term = (long)whole;
            long nextNumerator = (term * currentNumerator) + previousNumerator;
            long nextDenominator = (term * currentDenominator) + previousDenominator;
            if (nextNumerator > bound || nextDenominator > bound) break;

            previousNumerator = currentNumerator;
            previousDenominator = currentDenominator;
            currentNumerator = nextNumerator;
            currentDenominator = nextDenominator;

            double fraction = remainder - whole;
            if (Math.Abs(((double)currentNumerator / currentDenominator) - rate) <= rate * 1e-12 || fraction < 1e-12) break;
            remainder = 1d / fraction;
        }

        if (currentDenominator == 0)
        {
            currentNumerator = Math.Min(bound, (long)Math.Round(rate));
            currentDenominator = 1;
        }

        numerator = (int)currentNumerator;
        denominator = (int)currentDenominator;
    }

    private string DescribeFrameRate() =>
        "fps " + Number(FrameRateNumerator) + "/" + Number(FrameRateDenominator) + " drop/dup";

    private string DryRunSizeToken()
    {
        switch (FrameSize.Kind)
        {
            case AuthoringFrameSizeKind.Exact:
                return Number(FrameSize.Width) + "x" + Number(FrameSize.Height);
            case AuthoringFrameSizeKind.LongSide:
                return "long-side-" + Number(FrameSize.Pixels);
            default:
                return "short-side-" + Number(FrameSize.Pixels);
        }
    }

    // The flags are FFmpeg's: '+'-separated tokens. The first resampler token decides; everything else (accurate_rnd,
    // full_chroma_int, another resampler) steers FFmpeg's scaler only.
    private void ParseScalerFlags(string flags)
    {
        foreach (string raw in (flags ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (Filter == null && token == "lanczos")
            {
                Filter = ScalingFilter.Lanczos3;
            }
            else if (Filter == null && (token == "bilinear" || token == "fast_bilinear"))
            {
                Filter = ScalingFilter.Bilinear;
                if (token == "fast_bilinear") ignoredScalerFlags.Add(token);
            }
            else if (token.Length > 0)
            {
                ignoredScalerFlags.Add(token);
            }
        }

        ScalerFlagsUnrecognised = Filter == null;
    }

    // A single plain 3-D table is used as it stands - the very table FFmpeg's lut3d reads; anything else (the
    // composed table is always plain, but a single 1-D or shaper-plus-cube file is not) is folded into one 3-D
    // table by the core's one composer.
    private static Lut3D LoadTable(string path)
    {
        try
        {
            CubeLut cube = CubeLutFile.ReadFile(path);
            if (cube.IsThreeDimensional && !cube.IsCombined) return cube.Lut3D;

            return LutComposer.Compose(new[] { LutLayer.FromCube(cube) });
        }
        catch (Exception ex) when (ex is not VideoAuthoringException)
        {
            throw new VideoAuthoringException(
                "The colour lookup table '" + path + "' could not be read for the in-process grade: " + ex.Message, ex);
        }
    }

    // round(stated * other / reference / 2) * 2, halves away from zero, at least 2.
    private static int EvenRescale(int stated, int other, int reference)
    {
        long numerator = (long)stated * other;
        long denominator = (long)reference * 2;
        long pairs = (numerator + (denominator / 2)) / denominator;
        return (int)Math.Max(2, pairs * 2);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
