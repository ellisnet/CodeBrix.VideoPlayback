using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Floating-point conversion between Y'CbCr code values and normalised R'G'B' for the LUT grader.
/// </summary>
/// <remarks>
/// The constants are IDENTICAL to the ones <see cref="CodeBrix.VideoPlayback.Color.VideoFrameConverter" />
/// uses (its own fixed-point table is private): Kr/Kb 0.2126/0.0722 for BT.709 and 0.299/0.114 for BT.601
/// (BT.470BG and SMPTE 170M); studio range = luma 16..235 and chroma 16..240 scaled by
/// <c>2^(bitDepth - 8)</c>, full range = 0..max with the chroma centre at <c>2^(bitDepth - 1)</c>. An
/// unspecified range is treated as studio range, as the converter does.
/// </remarks>
internal readonly struct YuvRgbMatrix
{
    private YuvRgbMatrix(double kr, double kb, double lumaOffset, double lumaRange, double chromaOffset, double chromaRange, int maximum)
    {
        Kr = kr;
        Kb = kb;
        Kg = 1d - kr - kb;
        LumaOffset = lumaOffset;
        LumaRange = lumaRange;
        ChromaOffset = chromaOffset;
        ChromaRange = chromaRange;
        Maximum = maximum;
    }

    public double Kr { get; }

    public double Kb { get; }

    public double Kg { get; }

    public double LumaOffset { get; }

    public double LumaRange { get; }

    public double ChromaOffset { get; }

    public double ChromaRange { get; }

    public int Maximum { get; }

    public static YuvRgbMatrix Create(VideoMatrixCoefficients matrix, VideoColorRange range, int bitDepth)
    {
        double kr;
        double kb;
        switch (matrix)
        {
            case VideoMatrixCoefficients.Bt709:
                kr = 0.2126;
                kb = 0.0722;
                break;

            case VideoMatrixCoefficients.Bt470Bg:
            case VideoMatrixCoefficients.Smpte170M:
                kr = 0.299;
                kb = 0.114;
                break;

            case VideoMatrixCoefficients.Unspecified:
                throw new ArgumentException(
                    "matrix is Unspecified; the grader needs to be told the matrix (Bt709, or Bt470Bg / Smpte170M for BT.601).",
                    nameof(matrix));

            default:
                throw new NotSupportedException(
                    $"The LUT grader converts with the BT.709 and BT.601 matrices only; matrix is {matrix}.");
        }

        int scale = 1 << (bitDepth - 8);
        int maximum = (1 << bitDepth) - 1;
        bool full = range == VideoColorRange.Full;

        return full
            ? new YuvRgbMatrix(kr, kb, 0d, maximum, 1 << (bitDepth - 1), maximum, maximum)
            : new YuvRgbMatrix(kr, kb, 16d * scale, 219d * scale, 128d * scale, 224d * scale, maximum);
    }

    /// <summary>Code values to normalised R'G'B' (not clamped).</summary>
    public void ToRgb(double y, double u, double v, out float red, out float green, out float blue)
    {
        double luma = (y - LumaOffset) / LumaRange;
        double cb = (u - ChromaOffset) / ChromaRange;
        double cr = (v - ChromaOffset) / ChromaRange;

        double r = luma + (2d * (1d - Kr) * cr);
        double b = luma + (2d * (1d - Kb) * cb);
        double g = (luma - (Kr * r) - (Kb * b)) / Kg;

        red = (float)r;
        green = (float)g;
        blue = (float)b;
    }

    /// <summary>Normalised R'G'B' to code values (not rounded, not clamped).</summary>
    public void ToYuv(float red, float green, float blue, out double y, out double u, out double v)
    {
        double luma = (Kr * red) + (Kg * green) + (Kb * blue);
        double cb = (blue - luma) / (2d * (1d - Kb));
        double cr = (red - luma) / (2d * (1d - Kr));

        y = (luma * LumaRange) + LumaOffset;
        u = (cb * ChromaRange) + ChromaOffset;
        v = (cr * ChromaRange) + ChromaOffset;
    }

    /// <summary>Rounds a code value half up and clamps it to the sample range.</summary>
    public int Quantise(double value)
    {
        double rounded = Math.Floor(value + 0.5d);
        if (rounded < 0d) return 0;
        return rounded > Maximum ? Maximum : (int)rounded;
    }
}
