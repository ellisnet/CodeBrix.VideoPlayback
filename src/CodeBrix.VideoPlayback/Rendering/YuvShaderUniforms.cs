using System;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// The numbers the colour shader needs for one particular frame description: how to read a sample, where the
/// chroma sits, and the three rows of the matrix that turns Y'CbCr into R'G'B'.
/// </summary>
/// <remarks>
/// This is deliberately a pure function of the frame's description, so it can be checked against the CPU
/// converter's own constants without a graphics device anywhere in the picture.
/// </remarks>
public readonly struct YuvShaderUniforms
{
    private YuvShaderUniforms(
        float planeMaximum,
        float lumaOffset,
        float chromaOffset,
        float[] redRow,
        float[] greenRow,
        float[] blueRow,
        float chromaShiftX,
        float chromaShiftY,
        float chromaCositedX,
        float chromaCositedY)
    {
        PlaneMaximum = planeMaximum;
        LumaOffset = lumaOffset;
        ChromaOffset = chromaOffset;
        RedRow = redRow;
        GreenRow = greenRow;
        BlueRow = blueRow;
        ChromaShiftX = chromaShiftX;
        ChromaShiftY = chromaShiftY;
        ChromaCositedX = chromaCositedX;
        ChromaCositedY = chromaCositedY;
    }

    /// <summary>
    /// What a fully-lit texel reads as in sample units: 255 for an 8-bit plane uploaded as R8, 65535 for a
    /// 10-bit or 12-bit plane uploaded as R16.
    /// </summary>
    public float PlaneMaximum { get; }

    /// <summary>The luma sample value that means black - 16 at 8-bit studio range, 0 at full range.</summary>
    public float LumaOffset { get; }

    /// <summary>The chroma sample value that means "no colour" - 128 at 8 bits.</summary>
    public float ChromaOffset { get; }

    /// <summary>The (Y, U, V) coefficients producing red on a 0-to-255 scale.</summary>
    public float[] RedRow { get; }

    /// <summary>The (Y, U, V) coefficients producing green on a 0-to-255 scale.</summary>
    public float[] GreenRow { get; }

    /// <summary>The (Y, U, V) coefficients producing blue on a 0-to-255 scale.</summary>
    public float[] BlueRow { get; }

    /// <summary>1 when the chroma planes are half width, 0 when they are full width.</summary>
    public float ChromaShiftX { get; }

    /// <summary>1 when the chroma planes are half height, 0 when they are full height.</summary>
    public float ChromaShiftY { get; }

    /// <summary>1 when a chroma sample sits ON its luma column, 0 when it sits between two.</summary>
    public float ChromaCositedX { get; }

    /// <summary>1 when a chroma sample sits ON its luma row, 0 when it sits between two.</summary>
    public float ChromaCositedY { get; }

    /// <summary>Works out the shader's numbers from a frame's description.</summary>
    /// <param name="color">
    /// The frame's colour description, ALREADY resolved - unspecified fields must have been turned into
    /// concrete ones by <see cref="VideoColorInfo.Resolve" /> first.
    /// </param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="monochrome">True when the frame has no chroma planes to read.</param>
    /// <returns>The uniform values for that description.</returns>
    /// <remarks>
    /// The arithmetic mirrors the core converter's <c>ConversionConstants</c> exactly, in floating point
    /// rather than fixed point, so the two paths produce the same picture to within rounding.
    /// </remarks>
    public static YuvShaderUniforms Create(in VideoColorInfo color, int bitDepth, VideoPixelLayout layout, bool monochrome)
    {
        int scale = 1 << (bitDepth - 8);
        int maximum = (1 << bitDepth) - 1;
        bool limited = color.Range != VideoColorRange.Full;

        float lumaOffset = limited ? 16f * scale : 0f;
        float lumaScale = limited ? 255f / (219f * scale) : 255f / maximum;
        float chromaOffset = limited ? 128f * scale : 1 << (bitDepth - 1);
        float chromaScale = limited ? 255f / (224f * scale) : 255f / maximum;

        float shiftX = layout == VideoPixelLayout.I420 || layout == VideoPixelLayout.I422 ? 1f : 0f;
        float shiftY = layout == VideoPixelLayout.I420 ? 1f : 0f;

        // Colocated sits on the luma sample in both directions; Vertical sits on the luma COLUMN and between
        // the luma ROWS (AV1's CSP_VERTICAL, the siting essentially all 4:2:0 video uses); Interstitial sits
        // between them in both. Reading "Vertical" as "co-sited vertically" inverts the upsampling.
        float cositedX = color.ChromaSiting == VideoChromaSiting.Interstitial ? 0f : 1f;
        float cositedY =
            color.ChromaSiting == VideoChromaSiting.Interstitial || color.ChromaSiting == VideoChromaSiting.Vertical
                ? 0f
                : 1f;

        if (monochrome)
        {
            // The chroma children are bound to the luma plane so that every child is bound, and the chroma
            // coefficients are zero, so what they read cannot reach the picture.
            return new YuvShaderUniforms(
                bitDepth > 8 ? 65535f : 255f,
                lumaOffset,
                chromaOffset,
                new[] { lumaScale, 0f, 0f },
                new[] { lumaScale, 0f, 0f },
                new[] { lumaScale, 0f, 0f },
                shiftX,
                shiftY,
                cositedX,
                cositedY);
        }

        if (color.Matrix == VideoMatrixCoefficients.Identity)
        {
            // The three planes are not luma and chroma at all - they carry G, B and R, each on the luma
            // range - so the "matrix" is a permutation and every offset is the luma one.
            return new YuvShaderUniforms(
                bitDepth > 8 ? 65535f : 255f,
                lumaOffset,
                lumaOffset,
                new[] { 0f, 0f, lumaScale },
                new[] { lumaScale, 0f, 0f },
                new[] { 0f, lumaScale, 0f },
                shiftX,
                shiftY,
                cositedX,
                cositedY);
        }

        GetLuminanceWeights(color.Matrix, out double kr, out double kb);
        double kg = 1.0 - kr - kb;

        float crToRed = (float)(2.0 * (1.0 - kr)) * chromaScale;
        float cbToBlue = (float)(2.0 * (1.0 - kb)) * chromaScale;
        float cbToGreen = (float)(2.0 * kb * (1.0 - kb) / kg) * chromaScale;
        float crToGreen = (float)(2.0 * kr * (1.0 - kr) / kg) * chromaScale;

        return new YuvShaderUniforms(
            bitDepth > 8 ? 65535f : 255f,
            lumaOffset,
            chromaOffset,
            new[] { lumaScale, 0f, crToRed },
            new[] { lumaScale, -cbToGreen, -crToGreen },
            new[] { lumaScale, cbToBlue, 0f },
            shiftX,
            shiftY,
            cositedX,
            cositedY);
    }

    private static void GetLuminanceWeights(VideoMatrixCoefficients matrix, out double kr, out double kb)
    {
        switch (matrix)
        {
            case VideoMatrixCoefficients.Fcc:
                kr = 0.30;
                kb = 0.11;
                return;

            case VideoMatrixCoefficients.Bt470Bg:
            case VideoMatrixCoefficients.Smpte170M:
                kr = 0.299;
                kb = 0.114;
                return;

            case VideoMatrixCoefficients.Smpte240M:
                kr = 0.212;
                kb = 0.087;
                return;

            case VideoMatrixCoefficients.Bt2020NonConstantLuminance:
            case VideoMatrixCoefficients.Bt2020ConstantLuminance:
                kr = 0.2627;
                kb = 0.0593;
                return;

            default:
                kr = 0.2126;
                kb = 0.0722;
                return;
        }
    }
}
