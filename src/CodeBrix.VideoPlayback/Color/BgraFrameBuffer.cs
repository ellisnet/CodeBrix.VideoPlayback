using System;
using System.Runtime.InteropServices;

namespace CodeBrix.VideoPlayback.Color;

/// <summary>
/// A block of 64-byte-aligned unmanaged memory holding one frame's worth of BGRA32 pixels - the surface the
/// GPU-free render path converts into and a presenter draws from.
/// </summary>
/// <remarks>
/// <para>
/// The memory is unmanaged and never moves, which is what lets a drawing library wrap it as an image without
/// copying it and without pinning anything on the managed heap for the lifetime of playback.
/// </para>
/// <para>
/// Pixels are stored as four bytes each in memory order B, G, R, A - the byte order that reads back as
/// <c>0xAARRGGBB</c> when a little-endian machine loads the four bytes as one unsigned 32-bit integer, which
/// is what every common drawing surface calls "BGRA32" or "PBGRA32". The alpha byte is always 255.
/// </para>
/// <para>
/// Instances come from <see cref="BgraFrameBufferPool" /> and go back to it. Nothing else should create or
/// free one.
/// </para>
/// </remarks>
public sealed class BgraFrameBuffer
{
    /// <summary>The alignment, in bytes, of the address <see cref="Data" /> returns.</summary>
    public const int Alignment = 64;

    private IntPtr data;

    internal BgraFrameBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = checked(width * 4);
        SizeInBytes = (long)Stride * height;

        unsafe
        {
            data = (IntPtr)NativeMemory.AlignedAlloc((nuint)SizeInBytes, Alignment);
        }

        if (data == IntPtr.Zero)
        {
            throw new OutOfMemoryException(
                $"Could not allocate {SizeInBytes} bytes of {Alignment}-byte-aligned memory for a {width}x{height} BGRA surface.");
        }
    }

    /// <summary>A 64-byte-aligned pointer to the first byte of the first row.</summary>
    /// <remarks>Reads <see cref="IntPtr.Zero" /> once the buffer has been freed.</remarks>
    public IntPtr Data => data;

    /// <summary>The number of pixels in a row.</summary>
    public int Width { get; }

    /// <summary>The number of rows.</summary>
    public int Height { get; }

    /// <summary>
    /// The distance in BYTES from the start of one row to the start of the next, which is always
    /// <see cref="Width" /> times four - the rows are packed with no padding between them.
    /// </summary>
    public int Stride { get; }

    /// <summary>The total number of bytes the surface occupies.</summary>
    public long SizeInBytes { get; }

    /// <summary>True once the memory behind this buffer has been freed.</summary>
    public bool IsFreed => data == IntPtr.Zero;

    /// <summary>Returns the whole surface as a span of bytes.</summary>
    /// <returns>A span covering <see cref="SizeInBytes" /> bytes, starting at <see cref="Data" />.</returns>
    /// <exception cref="ObjectDisposedException">The buffer has been freed.</exception>
    public unsafe Span<byte> AsSpan()
    {
        IntPtr address = data;
        if (address == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(BgraFrameBuffer), "This BGRA surface has been freed.");
        }

        return new Span<byte>((void*)address, checked((int)SizeInBytes));
    }

    /// <summary>Returns one row of the surface as a span of bytes.</summary>
    /// <param name="row">The zero-based row index.</param>
    /// <returns>A span covering <see cref="Stride" /> bytes of the requested row.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the surface.</exception>
    /// <exception cref="ObjectDisposedException">The buffer has been freed.</exception>
    public unsafe Span<byte> GetRow(int row)
    {
        IntPtr address = data;
        if (address == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(BgraFrameBuffer), "This BGRA surface has been freed.");
        }

        if (row < 0 || row >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"The row must be between 0 and {Height - 1}.");
        }

        return new Span<byte>((byte*)address + ((long)row * Stride), Stride);
    }

    /// <summary>Fills the whole surface with zero.</summary>
    public unsafe void Clear()
    {
        IntPtr address = data;
        if (address == IntPtr.Zero) return;
        NativeMemory.Clear((void*)address, (nuint)SizeInBytes);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height} BGRA32, stride {Stride}, {SizeInBytes} bytes";

    internal unsafe void Free()
    {
        IntPtr address = data;
        if (address == IntPtr.Zero) return;
        data = IntPtr.Zero;
        NativeMemory.AlignedFree((void*)address);
    }
}
