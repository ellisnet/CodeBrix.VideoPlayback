using System;
using System.Buffers.Binary;
using System.Text;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Containers.Ivf;

/// <summary>
/// Reads an IVF file - the thin, one-codec wrapper an encoder writes around an elementary video stream.
/// </summary>
/// <remarks>
/// <para>
/// IVF is a 32-byte header followed by frames, each preceded by its length and a timestamp counted in the
/// header's own time base. It is not a media container in any real sense - there is no audio, no index, no
/// metadata - which is precisely why it is a convenient way to hand a coded video stream from an encoder to
/// a muxer.
/// </para>
/// <para>
/// This reader is an authoring-time input to the bespoke muxer. Nothing in playback uses it.
/// </para>
/// </remarks>
public sealed class IvfReader : IDisposable
{
    private const int HeaderLength = 32;
    private const int FrameHeaderLength = 12;

    private readonly IMediaSource source;
    private readonly bool leaveSourceOpen;

    private byte[] buffer = new byte[64 * 1024];
    private long position;
    private long frameIndex;
    private bool disposed;

    /// <summary>Opens an IVF file over a source.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="leaveSourceOpen">True to leave the source open when this reader is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="VideoPlaybackException">The file is not an IVF file, or its header is malformed.</exception>
    public IvfReader(IMediaSource source, bool leaveSourceOpen = false)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.leaveSourceOpen = leaveSourceOpen;

        Span<byte> header = stackalloc byte[HeaderLength];
        source.ReadExactly(header, "the IVF header");
        position = HeaderLength;

        if (header[0] != (byte)'D' || header[1] != (byte)'K' || header[2] != (byte)'I' || header[3] != (byte)'F')
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' does not begin with the IVF signature 'DKIF', so it is not an IVF file.");
        }

        Version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        FourCharacterCode = Encoding.ASCII.GetString(header.Slice(8, 4));
        Width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(12, 2));
        Height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(14, 2));
        TimeBaseDenominator = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        TimeBaseNumerator = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
        DeclaredFrameCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));

        if (headerLength < HeaderLength)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares a {headerLength}-byte IVF header; the format's header is {HeaderLength} bytes.");
        }

        if (headerLength > HeaderLength)
        {
            if (!source.Skip(headerLength - HeaderLength))
            {
                throw new VideoPlaybackException($"'{source.Name}' ended inside its IVF header.");
            }

            position = headerLength;
        }

        if (TimeBaseDenominator == 0)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares an IVF time base with a zero denominator, so its timestamps mean nothing.");
        }
    }

    /// <summary>The format version the header declares. Every writer this library has met writes 0.</summary>
    public ushort Version { get; }

    /// <summary>
    /// The four-character code naming the codec: <c>AV01</c> for AV1, <c>VP90</c> for VP9, and so on.
    /// </summary>
    public string FourCharacterCode { get; }

    /// <summary>The frame width the header declares, in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame height the header declares, in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// The numerator of the time base - one timestamp unit is
    /// <see cref="TimeBaseNumerator" /> / <see cref="TimeBaseDenominator" /> seconds.
    /// </summary>
    public uint TimeBaseNumerator { get; }

    /// <summary>The denominator of the time base.</summary>
    public uint TimeBaseDenominator { get; }

    /// <summary>How many frames the header claims. Writers do not always fill it in correctly.</summary>
    public uint DeclaredFrameCount { get; }

    /// <summary>How long one timestamp unit lasts.</summary>
    public TimeSpan TimeBase =>
        TimeSpan.FromSeconds(TimeBaseNumerator / (double)TimeBaseDenominator);

    /// <summary>How many frames have actually been read so far.</summary>
    public long FramesRead => frameIndex;

    /// <summary>Reads the next frame.</summary>
    /// <param name="data">
    /// The frame's bytes, borrowed from a reader-owned buffer and valid only until the next call.
    /// </param>
    /// <param name="timestamp">When the frame is for, converted from the file's time base.</param>
    /// <param name="rawTimestamp">The frame's timestamp in the file's own units.</param>
    /// <returns>True when a frame was read; false at the end of the file.</returns>
    /// <exception cref="VideoPlaybackException">The file is truncated or declares an unreasonable frame size.</exception>
    public bool TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out long rawTimestamp)
    {
        ThrowIfDisposed();

        Span<byte> frameHeader = stackalloc byte[FrameHeaderLength];
        int read = source.ReadAtLeast(frameHeader);
        if (read == 0)
        {
            data = default;
            timestamp = TimeSpan.Zero;
            rawTimestamp = 0;
            return false;
        }

        if (read < FrameHeaderLength)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' ends with {read} bytes where an IVF frame header of {FrameHeaderLength} bytes "
                + $"was expected, at offset {position}.");
        }

        position += FrameHeaderLength;

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.Slice(0, 4));
        rawTimestamp = BinaryPrimitives.ReadInt64LittleEndian(frameHeader.Slice(4, 8));

        if (size > 256 * 1024 * 1024)
        {
            throw new VideoPlaybackException(
                $"'{source.Name}' declares a {size}-byte IVF frame at offset {position - FrameHeaderLength}, which "
                + "is beyond anything this reader will accept.");
        }

        if (buffer.Length < size)
        {
            int capacity = buffer.Length;
            while (capacity < size) capacity *= 2;
            buffer = new byte[capacity];
        }

        source.ReadExactly(buffer.AsSpan(0, (int)size), $"IVF frame {frameIndex}");
        position += size;

        data = buffer.AsMemory(0, (int)size);
        timestamp = TimeSpan.FromSeconds(rawTimestamp * TimeBaseNumerator / (double)TimeBaseDenominator);
        frameIndex++;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveSourceOpen) source.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(IvfReader));
    }
}
