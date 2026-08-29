using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Color;

/// <summary>
/// Turns a decoded planar frame into BGRA32 pixels on the CPU - the whole of the GPU-free render path's
/// pixel work, written as managed vector code so it runs the same way on x64, ARM64 and RISC-V 64.
/// </summary>
/// <remarks>
/// <para>
/// This is not a stopgap for the graphics path; it is a supported way to play video on a machine with no
/// usable graphics device, on a frame-buffer display, or wherever deterministic output matters more than the
/// last few per cent of speed. It is written accordingly: per-row vector code, no allocation once warm, and
/// output straight into a buffer the caller already owns.
/// </para>
/// <para>
/// <b>What it honours.</b> The BT.601, BT.709 and BT.2020 matrices; studio and full sample ranges; 4:2:0,
/// 4:2:2, 4:4:4 and monochrome layouts; 8-bit, 10-bit and 12-bit samples; and the chroma siting the stream
/// declares, which decides how subsampled chroma is stretched back to full resolution.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not tone-map high-dynamic-range transfer curves. A frame
/// carrying SMPTE ST 2084 ("PQ") or ARIB STD-B67 ("HLG") is converted with its own matrix but with the
/// transfer curve ignored, exactly as if it were a standard-range curve, and the fact is written once per
/// process to <see cref="System.Diagnostics.Trace" /> as a warning. The picture will look washed out or
/// dark; that is honest rather than wrong, and an application that needs better should tone-map on the
/// graphics device. It also treats the BT.2020 CONSTANT-luminance matrix as if it were the non-constant one,
/// which is a launch limitation rather than a correct conversion - constant-luminance material is
/// vanishingly rare and doing it properly needs the transfer curve applied first.
/// </para>
/// <para>
/// <b>Chroma siting.</b> Subsampled chroma has to be stretched back over the luma grid, and where the chroma
/// sample is deemed to SIT decides how. The three cases behave as
/// ITU-T H.273 defines them:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="VideoChromaSiting.Colocated" /> - the chroma sample sits on the top-left luma sample, so it
///     is replicated horizontally and vertically with no interpolation.
///   </description></item>
///   <item><description>
///     <see cref="VideoChromaSiting.Vertical" /> - the MPEG-2 siting: horizontally on the luma column (so
///     replicated across), vertically halfway between two luma rows (so blended 3:1 between the two nearest
///     chroma rows).
///   </description></item>
///   <item><description>
///     <see cref="VideoChromaSiting.Interstitial" /> - the JPEG / MPEG-1 siting: halfway between luma samples
///     in BOTH directions, so blended 3:1 horizontally as well as vertically.
///   </description></item>
/// </list>
/// <para>
/// Blends use quarter-weight integer arithmetic - <c>(3a + b + 2) / 4</c> - which is what the reference
/// implementation in the test suite reproduces exactly, so the two agree to within the rounding of the
/// matrix itself. Siting only matters where a dimension is actually subsampled; 4:4:4 and monochrome ignore
/// it entirely, and 4:2:2 ignores the vertical half of it.
/// </para>
/// <para>
/// <b>Bit-depth reduction.</b> 10-bit and 12-bit frames are reduced to 8-bit output with ordered dithering
/// rather than truncation, using the classic 4x4 Bayer matrix
/// <c>{0,8,2,10 / 12,4,14,6 / 3,11,1,9 / 15,7,13,5}</c>: the matrix entry for the pixel's position replaces
/// the usual round-to-nearest constant in the final shift, which spreads the quantisation error over the
/// picture instead of banding it. 8-bit input is not dithered - it maps exactly - and uses plain
/// round-to-nearest.
/// </para>
/// <para>
/// <b>Allocation.</b> Nothing is allocated per frame. A small per-thread scratch row is kept between calls
/// and only grows when a wider frame arrives, so the steady state allocates zero bytes.
/// </para>
/// </remarks>
public static class VideoFrameConverter
{
    /// <summary>The number of fractional bits the fixed-point conversion arithmetic carries.</summary>
    private const int Shift = 14;

    /// <summary>The number of extra samples the scratch row carries past the frame width, for safety.</summary>
    private const int ScratchSlack = 64;

    /// <summary>The classic 4x4 Bayer ordered-dither matrix, row-major, values 0 to 15.</summary>
    private static readonly int[] BayerMatrix =
    {
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5,
    };

    [ThreadStatic]
    private static short[] scratchRow;

    private static int highDynamicRangeWarningIssued;

    /// <summary>
    /// True when the conversion runs on vector hardware rather than the scalar fallback.
    /// </summary>
    /// <remarks>
    /// False does not mean the converter is unavailable - the scalar path produces identical output - only
    /// that it will be several times slower, which is worth knowing when deciding what resolution a device
    /// can carry.
    /// </remarks>
    public static bool IsHardwareAccelerated => Vector256.IsHardwareAccelerated || Vector128.IsHardwareAccelerated;

    /// <summary>The number of bytes one row of BGRA32 output occupies.</summary>
    /// <param name="width">The number of pixels in a row.</param>
    /// <returns><paramref name="width" /> times four.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width" /> is not greater than zero.</exception>
    public static int GetBgraStride(int width)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be greater than zero.");
        return checked(width * 4);
    }

    /// <summary>The number of bytes a whole BGRA32 frame occupies when its rows are packed.</summary>
    /// <param name="width">The number of pixels in a row.</param>
    /// <param name="height">The number of rows.</param>
    /// <returns>The stride times the height.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not greater than zero.</exception>
    public static int GetBgraBufferSize(int width, int height)
    {
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be greater than zero.");
        return checked(GetBgraStride(width) * height);
    }

    /// <summary>Converts a decoded frame to BGRA32.</summary>
    /// <param name="frame">The frame to convert.</param>
    /// <param name="destination">
    /// Where the pixels go. Must be large enough for
    /// <c>(height - 1) * destinationStride + width * 4</c> bytes.
    /// </param>
    /// <param name="destinationStride">
    /// The distance in bytes from one output row to the next. May be larger than <c>width * 4</c>; the extra
    /// bytes at the end of each row are left exactly as they were.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stride is too small, or the frame's bit depth is not 8, 10 or 12.</exception>
    public static void ToBgra32(VideoFrame frame, Span<byte> destination, int destinationStride)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        ToBgra32(
            frame.Y,
            frame.U,
            frame.V,
            frame.Width,
            frame.Height,
            frame.Layout,
            frame.BitDepth,
            frame.Color,
            destination,
            destinationStride);
    }

    /// <summary>Converts three planes to BGRA32.</summary>
    /// <param name="y">The luma plane.</param>
    /// <param name="u">The first chroma plane. May be empty for monochrome content.</param>
    /// <param name="v">The second chroma plane. May be empty for monochrome content.</param>
    /// <param name="width">The visible width in luma samples.</param>
    /// <param name="height">The visible height in luma samples.</param>
    /// <param name="layout">The plane layout and chroma subsampling.</param>
    /// <param name="bitDepth">Bits per sample: 8, 10 or 12.</param>
    /// <param name="color">
    /// The colour description. Unspecified fields are resolved with
    /// <see cref="VideoColorInfo.Resolve" /> against <paramref name="height" />.
    /// </param>
    /// <param name="destination">
    /// Where the pixels go. Must be large enough for
    /// <c>(height - 1) * destinationStride + width * 4</c> bytes.
    /// </param>
    /// <param name="destinationStride">
    /// The distance in bytes from one output row to the next. May be larger than <c>width * 4</c>; the extra
    /// bytes at the end of each row are left exactly as they were.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The layout is unknown, the luma plane is empty, or the destination is too small.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is not greater than zero, the bit depth is not 8, 10 or 12, or the stride is smaller than
    /// <c>width * 4</c>.
    /// </exception>
    public static unsafe void ToBgra32(
        in VideoFramePlane y,
        in VideoFramePlane u,
        in VideoFramePlane v,
        int width,
        int height,
        VideoPixelLayout layout,
        int bitDepth,
        in VideoColorInfo color,
        Span<byte> destination,
        int destinationStride)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be greater than zero.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be greater than zero.");
        if (layout == VideoPixelLayout.Unknown) throw new ArgumentException("The pixel layout must be known.", nameof(layout));
        if (bitDepth != 8 && bitDepth != 10 && bitDepth != 12)
        {
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "The bit depth must be 8, 10 or 12.");
        }

        int minimumStride = checked(width * 4);
        if (destinationStride < minimumStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationStride),
                destinationStride,
                $"A {width}-pixel row of BGRA32 needs {minimumStride} bytes; the stride given is smaller.");
        }

        long required = (long)(height - 1) * destinationStride + minimumStride;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"A {width}x{height} BGRA32 frame with a stride of {destinationStride} needs {required} bytes; "
                + $"the destination holds {destination.Length}.",
                nameof(destination));
        }

        if (y.IsEmpty) throw new ArgumentException("The luma plane carries no samples.", nameof(y));

        VideoColorInfo resolved = color.Resolve(height);
        WarnOnceAboutHighDynamicRange(resolved);

        bool monochrome = layout == VideoPixelLayout.Gray || u.IsEmpty || v.IsEmpty;
        ConversionConstants constants = ConversionConstants.Create(resolved, bitDepth, monochrome);

        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int shiftX = layout == VideoPixelLayout.I420 || layout == VideoPixelLayout.I422 ? 1 : 0;
        int shiftY = layout == VideoPixelLayout.I420 ? 1 : 0;
        bool horizontalInterstitial = shiftX == 1 && resolved.ChromaSiting == VideoChromaSiting.Interstitial;
        bool verticalInterstitial = shiftY == 1
            && (resolved.ChromaSiting == VideoChromaSiting.Interstitial || resolved.ChromaSiting == VideoChromaSiting.Vertical);

        int lane = width + ScratchSlack;
        short[] scratch = GetScratch(lane * 4);

        fixed (byte* destinationBase = destination)
        fixed (short* scratchBase = scratch)
        {
            short* uFull = scratchBase;
            short* vFull = scratchBase + lane;
            short* uBlend = scratchBase + (2 * lane);
            short* vBlend = scratchBase + (3 * lane);

            if (monochrome)
            {
                // A neutral chroma difference is zero once the offset has been taken out, and the scratch
                // never changes, so it is filled once rather than per row.
                new Span<short>(uFull, width).Clear();
                new Span<short>(vFull, width).Clear();
            }

            byte* lumaBase = (byte*)y.Data;
            byte* uBase = monochrome ? null : (byte*)u.Data;
            byte* vBase = monochrome ? null : (byte*)v.Data;

            for (int row = 0; row < height; row++)
            {
                if (!monochrome)
                {
                    BuildChromaRow(
                        uBase, u.Stride, u.Width, u.Height, row, width, shiftX, shiftY,
                        horizontalInterstitial, verticalInterstitial, constants.ChromaOffset, bytesPerSample,
                        uBlend, uFull);

                    BuildChromaRow(
                        vBase, v.Stride, v.Width, v.Height, row, width, shiftX, shiftY,
                        horizontalInterstitial, verticalInterstitial, constants.ChromaOffset, bytesPerSample,
                        vBlend, vFull);
                }

                byte* lumaRow = lumaBase + ((long)row * y.Stride);
                byte* destinationRow = destinationBase + ((long)row * destinationStride);

                if (constants.IsIdentity)
                {
                    ConvertIdentityRow(lumaRow, uFull, vFull, destinationRow, width, bitDepth, constants);
                }
                else
                {
                    ConvertRow(lumaRow, uFull, vFull, destinationRow, width, bitDepth, row, constants);
                }
            }
        }
    }

    private static short[] GetScratch(int minimumLength)
    {
        short[] existing = scratchRow;
        if (existing != null && existing.Length >= minimumLength) return existing;

        short[] created = new short[minimumLength];
        scratchRow = created;
        return created;
    }

    private static void WarnOnceAboutHighDynamicRange(in VideoColorInfo color)
    {
        if (!color.IsHighDynamicRange) return;
        if (Interlocked.Exchange(ref highDynamicRangeWarningIssued, 1) != 0) return;

        Trace.TraceWarning(
            "CodeBrix.VideoPlayback: this video declares the high-dynamic-range transfer curve '{0}', which the "
            + "CPU colour converter does not tone-map. The frames are converted with their own colour matrix but "
            + "with the transfer curve ignored, so the picture will not look as the author intended. Convert on "
            + "the graphics device, or accept the difference. This warning is issued once per process.",
            color.Transfer);
    }

    /// <summary>
    /// Builds one output row's worth of chroma differences at full luma width, blending vertically and
    /// horizontally as the siting demands and taking the chroma offset out on the way.
    /// </summary>
    private static unsafe void BuildChromaRow(
        byte* planeBase,
        int planeStride,
        int chromaWidth,
        int chromaHeight,
        int outputRow,
        int width,
        int shiftX,
        int shiftY,
        bool horizontalInterstitial,
        bool verticalInterstitial,
        int chromaOffset,
        int bytesPerSample,
        short* blend,
        short* full)
    {
        int firstRow;
        int secondRow;
        int firstWeight;
        int secondWeight;

        if (shiftY == 0)
        {
            firstRow = Clamp(outputRow, chromaHeight);
            secondRow = firstRow;
            firstWeight = 4;
            secondWeight = 0;
        }
        else if (!verticalInterstitial)
        {
            firstRow = Clamp(outputRow >> 1, chromaHeight);
            secondRow = firstRow;
            firstWeight = 4;
            secondWeight = 0;
        }
        else if ((outputRow & 1) == 0)
        {
            // Output row 2k sits a quarter of a chroma row ABOVE chroma row k.
            int k = outputRow >> 1;
            firstRow = Clamp(k, chromaHeight);
            secondRow = Clamp(k - 1, chromaHeight);
            firstWeight = 3;
            secondWeight = 1;
        }
        else
        {
            // Output row 2k+1 sits a quarter of a chroma row BELOW chroma row k.
            int k = outputRow >> 1;
            firstRow = Clamp(k, chromaHeight);
            secondRow = Clamp(k + 1, chromaHeight);
            firstWeight = 3;
            secondWeight = 1;
        }

        byte* rowA = planeBase + ((long)firstRow * planeStride);
        byte* rowB = planeBase + ((long)secondRow * planeStride);

        if (secondWeight == 0 || firstRow == secondRow)
        {
            if (bytesPerSample == 1)
            {
                for (int i = 0; i < chromaWidth; i++) blend[i] = rowA[i];
            }
            else
            {
                ushort* source = (ushort*)rowA;
                for (int i = 0; i < chromaWidth; i++) blend[i] = (short)source[i];
            }
        }
        else if (bytesPerSample == 1)
        {
            for (int i = 0; i < chromaWidth; i++)
            {
                blend[i] = (short)(((rowA[i] * firstWeight) + (rowB[i] * secondWeight) + 2) >> 2);
            }
        }
        else
        {
            ushort* sourceA = (ushort*)rowA;
            ushort* sourceB = (ushort*)rowB;
            for (int i = 0; i < chromaWidth; i++)
            {
                blend[i] = (short)(((sourceA[i] * firstWeight) + (sourceB[i] * secondWeight) + 2) >> 2);
            }
        }

        if (shiftX == 0)
        {
            for (int x = 0; x < width; x++)
            {
                full[x] = (short)(blend[Clamp(x, chromaWidth)] - chromaOffset);
            }

            return;
        }

        if (!horizontalInterstitial)
        {
            int last = chromaWidth - 1;
            int x = 0;
            for (int cx = 0; cx <= last && x + 1 < width; cx++, x += 2)
            {
                short value = (short)(blend[cx] - chromaOffset);
                full[x] = value;
                full[x + 1] = value;
            }

            for (; x < width; x++)
            {
                full[x] = (short)(blend[Clamp(x >> 1, chromaWidth)] - chromaOffset);
            }

            return;
        }

        for (int x = 0; x < width; x++)
        {
            int centre = x >> 1;
            int neighbour = (x & 1) == 0 ? centre - 1 : centre + 1;
            int a = blend[Clamp(centre, chromaWidth)];
            int b = blend[Clamp(neighbour, chromaWidth)];
            full[x] = (short)((((a * 3) + b + 2) >> 2) - chromaOffset);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int index, int count)
    {
        if (index < 0) return 0;
        int last = count - 1;
        return index > last ? last : index;
    }

    private static unsafe void ConvertRow(
        byte* lumaRow,
        short* uFull,
        short* vFull,
        byte* destinationRow,
        int width,
        int bitDepth,
        int row,
        in ConversionConstants k)
    {
        // The dither offset replaces the usual round-to-nearest constant. For 8-bit input there is nothing to
        // dither away, so all four columns carry the same plain rounding value.
        int bayerRow = (row & 3) * 4;
        bool dithered = bitDepth > 8;

        int d0 = dithered ? (BayerMatrix[bayerRow] << Shift) >> 4 : 1 << (Shift - 1);
        int d1 = dithered ? (BayerMatrix[bayerRow + 1] << Shift) >> 4 : d0;
        int d2 = dithered ? (BayerMatrix[bayerRow + 2] << Shift) >> 4 : d0;
        int d3 = dithered ? (BayerMatrix[bayerRow + 3] << Shift) >> 4 : d0;

        int x = 0;
        int* destination = (int*)destinationRow;

        // Both vector paths step SIXTEEN pixels at a time, which is what keeps every load inside the row:
        // sixteen 8-bit luma samples are one 16-byte load and sixteen 16-bit samples are two of them, so
        // nothing is ever read past the last visible sample of the last row.
        if (Vector256.IsHardwareAccelerated && width >= 16)
        {
            Vector256<int> yOffset = Vector256.Create(k.YOffset);
            Vector256<int> yMultiplier = Vector256.Create(k.YMul);
            Vector256<int> crR = Vector256.Create(k.CrR);
            Vector256<int> cbG = Vector256.Create(k.CbG);
            Vector256<int> crG = Vector256.Create(k.CrG);
            Vector256<int> cbB = Vector256.Create(k.CbB);
            Vector256<int> dither = Vector256.Create(d0, d1, d2, d3, d0, d1, d2, d3);
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> ceiling = Vector256.Create(255);
            Vector256<int> alpha = Vector256.Create(unchecked((int)0xFF000000u));

            for (; x + 16 <= width; x += 16)
            {
                Vector256<int> lumaLow;
                Vector256<int> lumaHigh;

                if (bitDepth == 8)
                {
                    Vector128<byte> raw = Vector128.Load(lumaRow + x);
                    Vector128<ushort> lower = Vector128.WidenLower(raw);
                    Vector128<ushort> upper = Vector128.WidenUpper(raw);
                    lumaLow = Vector256.Create(
                        Vector128.WidenLower(lower).AsInt32(),
                        Vector128.WidenUpper(lower).AsInt32());
                    lumaHigh = Vector256.Create(
                        Vector128.WidenLower(upper).AsInt32(),
                        Vector128.WidenUpper(upper).AsInt32());
                }
                else
                {
                    Vector256<ushort> samples = Vector256.Load((ushort*)lumaRow + x);
                    lumaLow = Vector256.WidenLower(samples).AsInt32();
                    lumaHigh = Vector256.WidenUpper(samples).AsInt32();
                }

                Vector256<short> uPacked = Vector256.Load(uFull + x);
                Vector256<short> vPacked = Vector256.Load(vFull + x);

                Vector256<int> pixelsLow = Convert256(
                    lumaLow,
                    Vector256.WidenLower(uPacked),
                    Vector256.WidenLower(vPacked),
                    yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha);

                Vector256<int> pixelsHigh = Convert256(
                    lumaHigh,
                    Vector256.WidenUpper(uPacked),
                    Vector256.WidenUpper(vPacked),
                    yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha);

                Vector256.Store(pixelsLow, destination + x);
                Vector256.Store(pixelsHigh, destination + x + 8);
            }
        }
        else if (Vector128.IsHardwareAccelerated && width >= 16)
        {
            Vector128<int> yOffset = Vector128.Create(k.YOffset);
            Vector128<int> yMultiplier = Vector128.Create(k.YMul);
            Vector128<int> crR = Vector128.Create(k.CrR);
            Vector128<int> cbG = Vector128.Create(k.CbG);
            Vector128<int> crG = Vector128.Create(k.CrG);
            Vector128<int> cbB = Vector128.Create(k.CbB);
            Vector128<int> dither = Vector128.Create(d0, d1, d2, d3);
            Vector128<int> zero = Vector128<int>.Zero;
            Vector128<int> ceiling = Vector128.Create(255);
            Vector128<int> alpha = Vector128.Create(unchecked((int)0xFF000000u));

            for (; x + 16 <= width; x += 16)
            {
                Vector128<int> luma0;
                Vector128<int> luma1;
                Vector128<int> luma2;
                Vector128<int> luma3;

                if (bitDepth == 8)
                {
                    Vector128<byte> raw = Vector128.Load(lumaRow + x);
                    Vector128<ushort> lower = Vector128.WidenLower(raw);
                    Vector128<ushort> upper = Vector128.WidenUpper(raw);
                    luma0 = Vector128.WidenLower(lower).AsInt32();
                    luma1 = Vector128.WidenUpper(lower).AsInt32();
                    luma2 = Vector128.WidenLower(upper).AsInt32();
                    luma3 = Vector128.WidenUpper(upper).AsInt32();
                }
                else
                {
                    Vector128<ushort> first = Vector128.Load((ushort*)lumaRow + x);
                    Vector128<ushort> second = Vector128.Load((ushort*)lumaRow + x + 8);
                    luma0 = Vector128.WidenLower(first).AsInt32();
                    luma1 = Vector128.WidenUpper(first).AsInt32();
                    luma2 = Vector128.WidenLower(second).AsInt32();
                    luma3 = Vector128.WidenUpper(second).AsInt32();
                }

                Vector128<short> uFirst = Vector128.Load(uFull + x);
                Vector128<short> uSecond = Vector128.Load(uFull + x + 8);
                Vector128<short> vFirst = Vector128.Load(vFull + x);
                Vector128<short> vSecond = Vector128.Load(vFull + x + 8);

                Vector128.Store(
                    Convert128(
                        luma0, Vector128.WidenLower(uFirst), Vector128.WidenLower(vFirst),
                        yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha),
                    destination + x);

                Vector128.Store(
                    Convert128(
                        luma1, Vector128.WidenUpper(uFirst), Vector128.WidenUpper(vFirst),
                        yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha),
                    destination + x + 4);

                Vector128.Store(
                    Convert128(
                        luma2, Vector128.WidenLower(uSecond), Vector128.WidenLower(vSecond),
                        yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha),
                    destination + x + 8);

                Vector128.Store(
                    Convert128(
                        luma3, Vector128.WidenUpper(uSecond), Vector128.WidenUpper(vSecond),
                        yOffset, yMultiplier, crR, cbG, crG, cbB, dither, zero, ceiling, alpha),
                    destination + x + 12);
            }
        }

        for (; x < width; x++)
        {
            int luma = bitDepth == 8 ? lumaRow[x] : ((ushort*)lumaRow)[x];
            int dither = (x & 3) switch
            {
                0 => d0,
                1 => d1,
                2 => d2,
                _ => d3,
            };

            int lumaTerm = ((luma - k.YOffset) * k.YMul) + dither;
            int cu = uFull[x];
            int cv = vFull[x];

            int r = ClampByte((lumaTerm + (cv * k.CrR)) >> Shift);
            int g = ClampByte((lumaTerm - (cu * k.CbG) - (cv * k.CrG)) >> Shift);
            int b = ClampByte((lumaTerm + (cu * k.CbB)) >> Shift);

            destination[x] = unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
        }
    }

    private static unsafe void ConvertIdentityRow(
        byte* lumaRow,
        short* uFull,
        short* vFull,
        byte* destinationRow,
        int width,
        int bitDepth,
        in ConversionConstants k)
    {
        // The "identity" matrix means the three planes are not luma and chroma at all - they are G, B and R
        // straight out, each scaled by the same range. It is rare enough (and never produced by the codecs
        // this library plays) that it runs scalar rather than doubling the vector code.
        int* destination = (int*)destinationRow;
        int rounding = 1 << (Shift - 1);

        for (int x = 0; x < width; x++)
        {
            int green = bitDepth == 8 ? lumaRow[x] : ((ushort*)lumaRow)[x];

            int g = ClampByte((((green - k.YOffset) * k.YMul) + rounding) >> Shift);
            int b = ClampByte(((uFull[x] * k.YMul) + rounding) >> Shift);
            int r = ClampByte(((vFull[x] * k.YMul) + rounding) >> Shift);

            destination[x] = unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Convert256(
        Vector256<int> luma,
        Vector256<int> cu,
        Vector256<int> cv,
        Vector256<int> yOffset,
        Vector256<int> yMultiplier,
        Vector256<int> crR,
        Vector256<int> cbG,
        Vector256<int> crG,
        Vector256<int> cbB,
        Vector256<int> dither,
        Vector256<int> zero,
        Vector256<int> ceiling,
        Vector256<int> alpha)
    {
        Vector256<int> lumaTerm = (luma - yOffset) * yMultiplier + dither;

        Vector256<int> r = Vector256.ShiftRightArithmetic(lumaTerm + (cv * crR), Shift);
        Vector256<int> g = Vector256.ShiftRightArithmetic(lumaTerm - (cu * cbG) - (cv * crG), Shift);
        Vector256<int> b = Vector256.ShiftRightArithmetic(lumaTerm + (cu * cbB), Shift);

        r = Vector256.Min(Vector256.Max(r, zero), ceiling);
        g = Vector256.Min(Vector256.Max(g, zero), ceiling);
        b = Vector256.Min(Vector256.Max(b, zero), ceiling);

        return alpha | Vector256.ShiftLeft(r, 16) | Vector256.ShiftLeft(g, 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Convert128(
        Vector128<int> luma,
        Vector128<int> cu,
        Vector128<int> cv,
        Vector128<int> yOffset,
        Vector128<int> yMultiplier,
        Vector128<int> crR,
        Vector128<int> cbG,
        Vector128<int> crG,
        Vector128<int> cbB,
        Vector128<int> dither,
        Vector128<int> zero,
        Vector128<int> ceiling,
        Vector128<int> alpha)
    {
        Vector128<int> lumaTerm = (luma - yOffset) * yMultiplier + dither;

        Vector128<int> r = Vector128.ShiftRightArithmetic(lumaTerm + (cv * crR), Shift);
        Vector128<int> g = Vector128.ShiftRightArithmetic(lumaTerm - (cu * cbG) - (cv * crG), Shift);
        Vector128<int> b = Vector128.ShiftRightArithmetic(lumaTerm + (cu * cbB), Shift);

        r = Vector128.Min(Vector128.Max(r, zero), ceiling);
        g = Vector128.Min(Vector128.Max(g, zero), ceiling);
        b = Vector128.Min(Vector128.Max(b, zero), ceiling);

        return alpha | Vector128.ShiftLeft(r, 16) | Vector128.ShiftLeft(g, 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampByte(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);

    /// <summary>The fixed-point coefficients one particular colour description and bit depth reduce to.</summary>
    private readonly struct ConversionConstants
    {
        private ConversionConstants(
            int yMul,
            int yOffset,
            int crR,
            int cbG,
            int crG,
            int cbB,
            int chromaOffset,
            bool isIdentity)
        {
            YMul = yMul;
            YOffset = yOffset;
            CrR = crR;
            CbG = cbG;
            CrG = crG;
            CbB = cbB;
            ChromaOffset = chromaOffset;
            IsIdentity = isIdentity;
        }

        public int YMul { get; }

        public int YOffset { get; }

        public int CrR { get; }

        public int CbG { get; }

        public int CrG { get; }

        public int CbB { get; }

        public int ChromaOffset { get; }

        public bool IsIdentity { get; }

        public static ConversionConstants Create(in VideoColorInfo color, int bitDepth, bool monochrome)
        {
            int scale = 1 << (bitDepth - 8);
            int maximum = (1 << bitDepth) - 1;
            bool limited = color.Range != VideoColorRange.Full;

            int lumaOffset = limited ? 16 * scale : 0;
            double lumaScale = limited ? 255.0 / (219.0 * scale) : 255.0 / maximum;
            int chromaOffset = limited ? 128 * scale : 1 << (bitDepth - 1);
            double chromaScale = limited ? 255.0 / (224.0 * scale) : 255.0 / maximum;

            bool identity = !monochrome && color.Matrix == VideoMatrixCoefficients.Identity;
            if (identity)
            {
                // The three planes carry G, B and R, each on the LUMA range, so there is no chroma centre to
                // take out beyond the luma offset itself.
                return new ConversionConstants(
                    (int)Math.Round(lumaScale * (1 << Shift)),
                    lumaOffset,
                    0,
                    0,
                    0,
                    0,
                    lumaOffset,
                    true);
            }

            GetLuminanceWeights(color.Matrix, out double kr, out double kb);
            double kg = 1.0 - kr - kb;

            double crToRed = 2.0 * (1.0 - kr);
            double cbToBlue = 2.0 * (1.0 - kb);
            double cbToGreen = 2.0 * kb * (1.0 - kb) / kg;
            double crToGreen = 2.0 * kr * (1.0 - kr) / kg;

            return new ConversionConstants(
                (int)Math.Round(lumaScale * (1 << Shift)),
                lumaOffset,
                (int)Math.Round(crToRed * chromaScale * (1 << Shift)),
                (int)Math.Round(cbToGreen * chromaScale * (1 << Shift)),
                (int)Math.Round(crToGreen * chromaScale * (1 << Shift)),
                (int)Math.Round(cbToBlue * chromaScale * (1 << Shift)),
                chromaOffset,
                false);
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
}
