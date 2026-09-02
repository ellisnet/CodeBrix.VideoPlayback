using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// Decodes the uncompressed video codec: it copies each packet's tightly packed planes into a frame buffer
/// rented from the host's pool.
/// </summary>
/// <remarks>
/// <para>
/// It is not a codec in any interesting sense, and that is the point. It lets the containers, the session,
/// the clock, the seek logic, the frame pool and the presenter be exercised end to end with no codec package
/// installed at all, and it gives the headless tools something to decode.
/// </para>
/// <para>
/// It SHIPS in this package and is always available: <see cref="RawVideoDecoderFactory" /> is built into
/// <see cref="VideoDecoders" />, so a <c>V_UNCOMPRESSED</c> track - in a Matroska file or in a bespoke
/// <c>.cbv</c> - plays with no codec package installed and no registration call at all. It stays a
/// diagnostics and test codec rather than a distribution format: uncompressed video is enormous, and a real
/// clip wants a real codec package.
/// </para>
/// </remarks>
public sealed class RawVideoDecoder : IVideoDecoder
{
    private readonly RawVideoDescriptor descriptor;
    private readonly IVideoFrameBufferPool pool;
    private readonly Queue<VideoFrame> ready = new Queue<VideoFrame>();
    private readonly long expectedPacketBytes;

    private long frameNumber;
    private bool disposed;

    /// <summary>Creates a decoder for one uncompressed track.</summary>
    /// <param name="descriptor">The shape of the track's frames.</param>
    /// <param name="pool">The pool frames are written into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The descriptor does not describe a decodable frame.</exception>
    public RawVideoDecoder(in RawVideoDescriptor descriptor, IVideoFrameBufferPool pool)
    {
        if (pool == null) throw new ArgumentNullException(nameof(pool));
        if (!descriptor.IsValid)
        {
            throw new VideoPlaybackException(
                $"An uncompressed video track must state a positive size, a known layout and a bit depth of 8, 10 "
                + $"or 12; this one says {descriptor}.");
        }

        this.descriptor = descriptor;
        this.pool = pool;
        expectedPacketBytes = RawVideoFormat.GetFrameByteCount(descriptor);

        Info = new VideoStreamInfo(
            descriptor.Width,
            descriptor.Height,
            descriptor.Width,
            descriptor.Height,
            descriptor.Layout,
            descriptor.BitDepth,
            descriptor.Color);
    }

    /// <inheritdoc />
    public VideoStreamInfo Info { get; }

    /// <inheritdoc />
    public bool SupportsExternalBuffers => true;

    /// <inheritdoc />
    public string CodecId => VideoCodecIds.Raw;

    /// <summary>How many bytes one packet of this track must carry.</summary>
    public long ExpectedPacketBytes => expectedPacketBytes;

    /// <inheritdoc />
    public bool SendPacket(VideoPacket packet)
    {
        ThrowIfDisposed();

        if (packet.Data.Length != expectedPacketBytes)
        {
            throw new VideoPlaybackException(
                $"An uncompressed {descriptor} frame is {expectedPacketBytes} bytes; this packet carries "
                + $"{packet.Data.Length}.");
        }

        VideoFrameBufferDescriptor bufferShape = new VideoFrameBufferDescriptor(
            descriptor.Width,
            descriptor.Height,
            descriptor.Layout,
            descriptor.BitDepth);

        VideoFrameBuffer buffer = pool.Rent(bufferShape);

        ReadOnlySpan<byte> source = packet.Data.Span;
        int offset = 0;

        for (int plane = 0; plane < 3; plane++)
        {
            int width = RawVideoFormat.GetPlaneWidth(descriptor, plane);
            int height = RawVideoFormat.GetPlaneHeight(descriptor, plane);
            if (width == 0 || height == 0) continue;

            int rowBytes = width * descriptor.BytesPerSample;
            VideoFramePlane target = buffer.GetPlane(plane);

            unsafe
            {
                byte* destination = (byte*)target.Data;
                for (int row = 0; row < height; row++)
                {
                    source.Slice(offset, rowBytes).CopyTo(new Span<byte>(destination + ((long)row * target.Stride), rowBytes));
                    offset += rowBytes;
                }
            }
        }

        VideoFrameInfo info = new VideoFrameInfo(
            descriptor.Width,
            descriptor.Height,
            descriptor.Width,
            descriptor.Height,
            descriptor.Layout,
            descriptor.BitDepth,
            packet.Timestamp,
            packet.Timestamp.Ticks,
            frameNumber++,
            true,
            descriptor.Color,
            null);

        ready.Enqueue(VideoFrame.Create(buffer, info, pool));
        return true;
    }

    /// <inheritdoc />
    public bool TryReceiveFrame(out VideoFrame frame)
    {
        ThrowIfDisposed();

        if (ready.Count == 0)
        {
            frame = null;
            return false;
        }

        frame = ready.Dequeue();
        return true;
    }

    /// <inheritdoc />
    public void Flush()
    {
        while (ready.Count > 0) ready.Dequeue().Dispose();
    }

    /// <inheritdoc />
    public void Drain()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Flush();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RawVideoDecoder));
    }
}
