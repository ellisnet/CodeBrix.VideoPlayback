using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CodeBrix.VideoPlayback.Containers.Ivf;

/// <summary>
/// Writes an IVF file - the thin, one-codec wrapper an encoder puts around an elementary video stream, and the
/// shape both FFmpeg and this library's own bespoke muxer accept as a video input.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="IvfReader" />, and it is here so that taking a bespoke file APART is
/// as ordinary as putting one together. A consumer that reads a <c>.cbv</c> file's video packets out with
/// <see cref="CodeBrix.VideoPlayback.Containers.Cbv.CbvReader" /> can hand them straight back to FFmpeg by
/// writing them here; without it, every such consumer has to write its own thirty-two byte header.
/// </para>
/// <para>
/// The file is a 32-byte header followed by frames, each a four-byte payload length, an eight-byte timestamp
/// in the header's own time base, and the payload. This writer states the time base
/// 1 / <see cref="TickTimeBaseDenominator" /> - one timestamp unit is one ten-millionth of a second, which is
/// exactly one .NET tick - so a frame's timestamp is its <see cref="TimeSpan.Ticks" /> value and no
/// arithmetic happens on the way in or on the way back out.
/// </para>
/// <para>
/// The frame count in the header is BACK-PATCHED by <see cref="Complete" />, so the stream has to be
/// seekable, and a file whose writer was never completed declares zero frames. <see cref="Dispose" /> does
/// not complete the file: that is deliberate, so that an abandoned encode leaves an obviously unfinished file
/// rather than a plausible one.
/// </para>
/// <para>
/// This is an authoring-time type. Nothing in playback writes anything.
/// </para>
/// </remarks>
public sealed class IvfWriter : IDisposable
{
    private const int HeaderLength = 32;
    private const int FrameCountOffset = 24;
    private const int FrameHeaderLength = 12;

    /// <summary>The four-character code that names an AV1 elementary stream.</summary>
    public const string Av1FourCharacterCode = "AV01";

    /// <summary>
    /// The time-base denominator this writer states: one timestamp unit is one ten-millionth of a second,
    /// which is one .NET tick, so a timestamp needs no scaling in either direction.
    /// </summary>
    public const uint TickTimeBaseDenominator = 10_000_000;

    private readonly Stream output;
    private readonly bool leaveOutputOpen;

    private uint frameCount;
    private bool completed;
    private bool disposed;

    /// <summary>Creates a writer over a seekable stream, and writes the header immediately.</summary>
    /// <param name="output">Where the file goes. It must be writable and seekable.</param>
    /// <param name="fourCharacterCode">The codec's four-character code, such as <c>AV01</c>.</param>
    /// <param name="width">The coded width, in pixels.</param>
    /// <param name="height">The coded height, in pixels.</param>
    /// <param name="leaveOutputOpen">True to leave the stream open when this writer is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The stream cannot seek, or the four-character code is not exactly four characters.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is outside the 1 to 65535 an IVF header can state.
    /// </exception>
    public IvfWriter(Stream output, string fourCharacterCode, int width, int height, bool leaveOutputOpen = false)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.leaveOutputOpen = leaveOutputOpen;

        if (!output.CanSeek)
        {
            throw new ArgumentException(
                "An IVF file's frame count is written last, over the header, so its stream has to be seekable.",
                nameof(output));
        }

        if (fourCharacterCode == null || fourCharacterCode.Length != 4)
        {
            throw new ArgumentException(
                "An IVF codec code is exactly four ASCII characters, such as 'AV01'.",
                nameof(fourCharacterCode));
        }

        if (width <= 0 || width > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), width, "An IVF header states its frame width in sixteen bits.");
        }

        if (height <= 0 || height > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height), height, "An IVF header states its frame height in sixteen bits.");
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        header[0] = (byte)'D';
        header[1] = (byte)'K';
        header[2] = (byte)'I';
        header[3] = (byte)'F';
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), HeaderLength);
        Encoding.ASCII.GetBytes(fourCharacterCode, header.Slice(8, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14, 2), (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), TickTimeBaseDenominator);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), 0);
        output.Write(header);

        FourCharacterCode = fourCharacterCode;
        Width = width;
        Height = height;
    }

    /// <summary>Creates a writer for an AV1 elementary stream over a new file.</summary>
    /// <param name="path">The file to create, overwriting anything already there.</param>
    /// <param name="width">The coded width, in pixels.</param>
    /// <param name="height">The coded height, in pixels.</param>
    /// <returns>A writer that owns the file and closes it when it is disposed.</returns>
    /// <exception cref="ArgumentException">The path is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is outside 1 to 65535.</exception>
    public static IvfWriter CreateAv1(string path, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An IVF file needs a path to be written to.", nameof(path));
        }

        return new IvfWriter(
            new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None),
            Av1FourCharacterCode,
            width,
            height);
    }

    /// <summary>The four-character code written into the header.</summary>
    public string FourCharacterCode { get; }

    /// <summary>The frame width written into the header, in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame height written into the header, in pixels.</summary>
    public int Height { get; }

    /// <summary>How many frames have been written so far.</summary>
    public uint FrameCount => frameCount;

    /// <summary>Writes one coded frame - for AV1, one temporal unit.</summary>
    /// <param name="data">The frame's bytes, exactly as the container or the encoder handed them over.</param>
    /// <param name="timestamp">When the frame is for. It is written as its tick count.</param>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timestamp is negative.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void WriteFrame(ReadOnlySpan<byte> data, TimeSpan timestamp)
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This IVF file has already been completed; nothing more can go in it.");
        }

        if (timestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp), timestamp, "An IVF frame timestamp cannot be negative.");
        }

        Span<byte> frameHeader = stackalloc byte[FrameHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.Slice(0, 4), (uint)data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(frameHeader.Slice(4, 8), timestamp.Ticks);
        output.Write(frameHeader);
        output.Write(data);
        frameCount++;
    }

    /// <summary>Back-patches the frame count into the header and flushes the file.</summary>
    /// <exception cref="InvalidOperationException">The file has already been completed.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public void Complete()
    {
        ThrowIfDisposed();

        if (completed)
        {
            throw new InvalidOperationException("This IVF file has already been completed.");
        }

        long end = output.Position;
        output.Position = FrameCountOffset;

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, frameCount);
        output.Write(count);

        output.Position = end;
        output.Flush();
        completed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveOutputOpen) output.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(IvfWriter));
    }
}
