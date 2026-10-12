using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// Resizes one plane of samples - a luma or chroma plane, an alpha plane, or an interleaved RGBA plane - with a
/// separable bilinear or three-lobe Lanczos filter, entirely in managed integer arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Instances hold the tables.</b> A scaler is built for ONE source size, ONE destination size and ONE
/// filter, and precomputes both axes' weight tables then; scaling a stream of frames through one instance
/// costs no table work and allocates nothing (the row cache is rented from <see cref="ArrayPool{T}" /> per
/// call and returned). An instance holds no per-call state, so one instance may serve several threads at
/// once. The static <see cref="Scale(ReadOnlySpan{byte}, int, int, int, Span{byte}, int, int, int, int, int, ScalingFilter, int)" />
/// keeps the last few geometries' instances per thread (luma and chroma of a frame both fit), so repeated
/// static calls at one size are cheap too.
/// </para>
/// <para>
/// <b>Arithmetic.</b> Weights are fourteen-bit integers summing exactly to one. The horizontal pass keeps
/// seven fractional bits in its intermediate row (the same headroom FFmpeg's scaler keeps) and, as FFmpeg's
/// does, caps that intermediate just under one level above the sample ceiling so an upward overshoot is not
/// carried into the second pass; the vertical pass rounds half up to the sample, and the result is clamped to <c>[0, maxSampleValue]</c> - which is what
/// stops a Lanczos overshoot wrapping around. Sixteen-bit samples accumulate in 64-bit integers, so no input
/// can overflow. An axis whose size does not change is copied exactly, and a call whose geometry does not
/// change at all is a straight row copy - byte-identical.
/// </para>
/// <para>
/// <b>Geometry.</b> Centre-aligned (output sample <c>o</c> samples source position
/// <c>(o + 0.5) * src / dst - 0.5</c>), edges clamped. Interleaved channels are scaled independently and
/// identically; RGBA is NOT premultiplied first - the same as FFmpeg - so a caller scaling straight-alpha
/// pictures with hard transparent edges may want to premultiply around the call.
/// </para>
/// <para>
/// <b>Chroma.</b> A subsampled chroma plane is just a smaller plane: scale it with its own geometry
/// (<c>ceil(w/2)</c> to <c>ceil(dstW/2)</c>), or let <see cref="FrameScaler" /> do the whole frame.
/// </para>
/// </remarks>
public sealed class PlaneScaler
{
    /// <summary>The fractional bits the horizontal pass keeps in its intermediate samples.</summary>
    private const int IntermediateBits = 7;

    /// <summary>The shift that brings a vertical accumulation back to a sample.</summary>
    private const int FinalShift = (2 * ScalingAxis.WeightBits) - IntermediateBits;

    /// <summary>How many recently used geometries the static entry point keeps per thread.</summary>
    private const int RecentCapacity = 4;

    [ThreadStatic]
    private static PlaneScaler[] recent;

    [ThreadStatic]
    private static int recentNext;

    private readonly ScalingAxis horizontal;
    private readonly ScalingAxis vertical;

    /// <summary>Builds a scaler, and its weight tables, for one geometry.</summary>
    /// <param name="sourceWidth">The source width in samples (pixels, for interleaved RGBA).</param>
    /// <param name="sourceHeight">The source height in rows.</param>
    /// <param name="destinationWidth">The destination width in samples.</param>
    /// <param name="destinationHeight">The destination height in rows.</param>
    /// <param name="filter">The kernel.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not greater than zero, or the filter is unknown.</exception>
    public PlaneScaler(int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight, ScalingFilter filter)
    {
        ProcessingGuard.Dimension(sourceWidth, nameof(sourceWidth));
        ProcessingGuard.Dimension(sourceHeight, nameof(sourceHeight));
        ProcessingGuard.Dimension(destinationWidth, nameof(destinationWidth));
        ProcessingGuard.Dimension(destinationHeight, nameof(destinationHeight));
        if (filter != ScalingFilter.Bilinear && filter != ScalingFilter.Lanczos3)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, $"filter is not a known kernel: {(int)filter}.");
        }

        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        DestinationWidth = destinationWidth;
        DestinationHeight = destinationHeight;
        Filter = filter;

        horizontal = ScalingAxis.Build(sourceWidth, destinationWidth, filter);
        vertical = ScalingAxis.Build(sourceHeight, destinationHeight, filter);
    }

    /// <summary>The source width in samples.</summary>
    public int SourceWidth { get; }

    /// <summary>The source height in rows.</summary>
    public int SourceHeight { get; }

    /// <summary>The destination width in samples.</summary>
    public int DestinationWidth { get; }

    /// <summary>The destination height in rows.</summary>
    public int DestinationHeight { get; }

    /// <summary>The kernel.</summary>
    public ScalingFilter Filter { get; }

    /// <summary>True when the geometry does not change, so <see cref="Scale(ReadOnlySpan{byte}, int, Span{byte}, int, int, int, int)" /> is a row copy.</summary>
    public bool IsIdentity => horizontal.IsIdentity && vertical.IsIdentity;

    /// <summary>
    /// The kernel to use when there is no reason to prefer one: <see cref="ScalingFilter.Lanczos3" /> when
    /// EITHER axis shrinks, <see cref="ScalingFilter.Bilinear" /> otherwise (enlargements and identity).
    /// </summary>
    /// <param name="sourceWidth">The source width.</param>
    /// <param name="sourceHeight">The source height.</param>
    /// <param name="destinationWidth">The destination width.</param>
    /// <param name="destinationHeight">The destination height.</param>
    /// <returns>The kernel.</returns>
    public static ScalingFilter ChooseDefault(int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight) =>
        destinationWidth < sourceWidth || destinationHeight < sourceHeight ? ScalingFilter.Lanczos3 : ScalingFilter.Bilinear;

    /// <summary>Scales one plane, building (or reusing one of this thread's recent) scalers for the geometry.</summary>
    /// <param name="source">The source samples.</param>
    /// <param name="sourceWidth">The source width in samples (pixels, for interleaved RGBA).</param>
    /// <param name="sourceHeight">The source height in rows.</param>
    /// <param name="sourceStride">The source stride in BYTES.</param>
    /// <param name="destination">Where the scaled samples go; must not overlap the source.</param>
    /// <param name="destinationWidth">The destination width in samples.</param>
    /// <param name="destinationHeight">The destination height in rows.</param>
    /// <param name="destinationStride">The destination stride in BYTES.</param>
    /// <param name="bytesPerSample">1 for eight-bit samples; 2 for deeper samples (little-endian, value in the low bits).</param>
    /// <param name="channels">1 for a plane, 4 for interleaved RGBA.</param>
    /// <param name="filter">The kernel.</param>
    /// <param name="maxSampleValue">The largest legal sample (255, 1023, 4095...); results are clamped to it.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, stride, sample width, channel count or maximum is out of range.</exception>
    /// <exception cref="ArgumentException">A span is too small, or the two spans overlap.</exception>
    public static void Scale(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int bytesPerSample,
        int channels,
        ScalingFilter filter,
        int maxSampleValue)
    {
        PlaneScaler[] cached = recent ??= new PlaneScaler[RecentCapacity];
        PlaneScaler scaler = null;
        foreach (PlaneScaler candidate in cached)
        {
            if (candidate != null
                && candidate.SourceWidth == sourceWidth
                && candidate.SourceHeight == sourceHeight
                && candidate.DestinationWidth == destinationWidth
                && candidate.DestinationHeight == destinationHeight
                && candidate.Filter == filter)
            {
                scaler = candidate;
                break;
            }
        }

        if (scaler == null)
        {
            scaler = new PlaneScaler(sourceWidth, sourceHeight, destinationWidth, destinationHeight, filter);
            cached[recentNext] = scaler;
            recentNext = (recentNext + 1) % RecentCapacity;
        }

        scaler.Scale(source, sourceStride, destination, destinationStride, bytesPerSample, channels, maxSampleValue);
    }

    /// <summary>Scales one plane with this instance's geometry and filter.</summary>
    /// <param name="source">The source samples, <see cref="SourceWidth" /> by <see cref="SourceHeight" />.</param>
    /// <param name="sourceStride">The source stride in BYTES.</param>
    /// <param name="destination">
    /// Where the scaled samples go, <see cref="DestinationWidth" /> by <see cref="DestinationHeight" />; must
    /// not overlap the source. Only the visible samples of each row are written.
    /// </param>
    /// <param name="destinationStride">The destination stride in BYTES.</param>
    /// <param name="bytesPerSample">1 for eight-bit samples; 2 for deeper samples (little-endian, value in the low bits).</param>
    /// <param name="channels">1 for a plane, 4 for interleaved RGBA.</param>
    /// <param name="maxSampleValue">The largest legal sample (255, 1023, 4095...); results are clamped to it.</param>
    /// <exception cref="ArgumentOutOfRangeException">A stride, sample width, channel count or maximum is out of range.</exception>
    /// <exception cref="ArgumentException">A span is too small, or the two spans overlap.</exception>
    public void Scale(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int bytesPerSample,
        int channels,
        int maxSampleValue)
    {
        ProcessingGuard.BytesPerSample(bytesPerSample, nameof(bytesPerSample));
        ProcessingGuard.Channels(channels, nameof(channels));
        ProcessingGuard.MaxSampleValue(maxSampleValue, bytesPerSample, nameof(maxSampleValue));
        ProcessingGuard.Plane(source.Length, SourceWidth, SourceHeight, sourceStride, bytesPerSample, channels, nameof(source), nameof(sourceStride));
        ProcessingGuard.Plane(destination.Length, DestinationWidth, DestinationHeight, destinationStride, bytesPerSample, channels, nameof(destination), nameof(destinationStride));
        ProcessingGuard.NoOverlap(source, destination, nameof(destination));

        if (IsIdentity)
        {
            int rowBytes = SourceWidth * channels * bytesPerSample;
            for (int row = 0; row < SourceHeight; row++)
            {
                source.Slice(row * sourceStride, rowBytes).CopyTo(destination.Slice(row * destinationStride, rowBytes));
            }

            return;
        }

        int rowLength = DestinationWidth * channels;
        int slots = vertical.Taps;
        int[] cache = ArrayPool<int>.Shared.Rent(slots * rowLength);
        int[] keys = ArrayPool<int>.Shared.Rent(slots);

        try
        {
            keys.AsSpan(0, slots).Fill(-1);
            int ceiling = ((maxSampleValue + 1) << IntermediateBits) - 1;
            Span<int> slotOffsets = slots <= 64 ? stackalloc int[slots] : new int[slots];

            for (int outputRow = 0; outputRow < DestinationHeight; outputRow++)
            {
                int tapBase = outputRow * slots;
                for (int t = 0; t < slots; t++)
                {
                    int sourceRow = vertical.Indices[tapBase + t];
                    int slot = sourceRow % slots;
                    if (keys[slot] != sourceRow)
                    {
                        Span<int> target = cache.AsSpan(slot * rowLength, rowLength);
                        ReadOnlySpan<byte> rowBytes = source.Slice(sourceRow * sourceStride, SourceWidth * channels * bytesPerSample);
                        if (bytesPerSample == 1)
                        {
                            Horizontal8(rowBytes, target, channels, ceiling);
                        }
                        else
                        {
                            Horizontal16(MemoryMarshal.Cast<byte, ushort>(rowBytes), target, channels, ceiling);
                        }

                        keys[slot] = sourceRow;
                    }

                    slotOffsets[t] = slot * rowLength;
                }

                Span<byte> outputBytes = destination.Slice(outputRow * destinationStride, rowLength * bytesPerSample);
                ReadOnlySpan<int> rowWeights = vertical.Weights.AsSpan(tapBase, slots);
                if (bytesPerSample == 1)
                {
                    Vertical8(cache, slotOffsets, rowWeights, outputBytes, rowLength, maxSampleValue);
                }
                else
                {
                    Vertical16(cache, slotOffsets, rowWeights, MemoryMarshal.Cast<byte, ushort>(outputBytes), rowLength, maxSampleValue);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cache);
            ArrayPool<int>.Shared.Return(keys);
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Filter} {SourceWidth}x{SourceHeight} -> {DestinationWidth}x{DestinationHeight}";

    private void Horizontal8(ReadOnlySpan<byte> row, Span<int> target, int channels, int ceiling)
    {
        int taps = horizontal.Taps;
        int[] indices = horizontal.Indices;
        int[] weights = horizontal.Weights;
        const int Rounding = 1 << (IntermediateBits - 1);
        const int Shift = ScalingAxis.WeightBits - IntermediateBits;

        for (int o = 0; o < DestinationWidth; o++)
        {
            int tapBase = o * taps;
            for (int c = 0; c < channels; c++)
            {
                int accumulator = 0;
                for (int t = 0; t < taps; t++)
                {
                    accumulator += row[(indices[tapBase + t] * channels) + c] * weights[tapBase + t];
                }

                int value = (accumulator + Rounding) >> Shift;
                target[(o * channels) + c] = value > ceiling ? ceiling : value;
            }
        }
    }

    private void Horizontal16(ReadOnlySpan<ushort> row, Span<int> target, int channels, int ceiling)
    {
        int taps = horizontal.Taps;
        int[] indices = horizontal.Indices;
        int[] weights = horizontal.Weights;
        const long Rounding = 1L << (IntermediateBits - 1);
        const int Shift = ScalingAxis.WeightBits - IntermediateBits;

        for (int o = 0; o < DestinationWidth; o++)
        {
            int tapBase = o * taps;
            for (int c = 0; c < channels; c++)
            {
                long accumulator = 0;
                for (int t = 0; t < taps; t++)
                {
                    accumulator += (long)row[(indices[tapBase + t] * channels) + c] * weights[tapBase + t];
                }

                long value = (accumulator + Rounding) >> Shift;
                target[(o * channels) + c] = (int)(value > ceiling ? ceiling : value);
            }
        }
    }

    private static void Vertical8(
        int[] cache,
        ReadOnlySpan<int> slotOffsets,
        ReadOnlySpan<int> weights,
        Span<byte> output,
        int rowLength,
        int maximum)
    {
        const int Rounding = 1 << (FinalShift - 1);
        int taps = weights.Length;

        for (int i = 0; i < rowLength; i++)
        {
            int accumulator = Rounding;
            for (int t = 0; t < taps; t++)
            {
                accumulator += cache[slotOffsets[t] + i] * weights[t];
            }

            int value = accumulator >> FinalShift;
            output[i] = (byte)(value < 0 ? 0 : value > maximum ? maximum : value);
        }
    }

    private static void Vertical16(
        int[] cache,
        ReadOnlySpan<int> slotOffsets,
        ReadOnlySpan<int> weights,
        Span<ushort> output,
        int rowLength,
        int maximum)
    {
        const long Rounding = 1L << (FinalShift - 1);
        int taps = weights.Length;

        for (int i = 0; i < rowLength; i++)
        {
            long accumulator = Rounding;
            for (int t = 0; t < taps; t++)
            {
                accumulator += (long)cache[slotOffsets[t] + i] * weights[t];
            }

            long value = accumulator >> FinalShift;
            output[i] = (ushort)(value < 0 ? 0 : value > maximum ? maximum : value);
        }
    }
}
