using System;
using System.Runtime.InteropServices;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// A frame buffer whose planes live in one block of 64-byte-aligned unmanaged memory.
/// </summary>
/// <remarks>
/// <para>
/// Unmanaged rather than a pinned managed array, deliberately: the memory has to stay at one address for as
/// long as a native decoder is writing into it and a graphics driver is reading out of it, and a block that
/// the garbage collector never sees cannot fragment the heap the way a long-lived pinned array does.
/// </para>
/// <para>
/// Instances come from <see cref="PinnedFrameBufferPool" /> and go back to it; nothing else should create or
/// free one.
/// </para>
/// </remarks>
public sealed class PinnedVideoFrameBuffer : VideoFrameBuffer
{
    private IntPtr baseAddress;

    internal PinnedVideoFrameBuffer(VideoFrameBufferDescriptor descriptor, int generation)
        : base(descriptor, VideoFrameStorage.HostMemory)
    {
        Generation = generation;
        AllocationBytes = descriptor.AllocationBytes;

        unsafe
        {
            baseAddress = (IntPtr)NativeMemory.AlignedAlloc(
                (nuint)AllocationBytes,
                (nuint)VideoFrameBufferDescriptor.PlaneAlignment);
        }

        if (baseAddress == IntPtr.Zero)
        {
            throw new OutOfMemoryException(
                $"Could not allocate {AllocationBytes} bytes of 64-byte-aligned memory for a {descriptor} frame buffer.");
        }

        long lumaBytes = (long)descriptor.LumaStride * descriptor.LumaAllocationRows
            + VideoFrameBufferDescriptor.TailPadding;

        Y = new VideoFramePlane(
            baseAddress,
            descriptor.LumaStride,
            descriptor.LumaVisibleWidth,
            descriptor.LumaVisibleHeight,
            descriptor.BytesPerSample);

        if (descriptor.IsMonochrome)
        {
            U = VideoFramePlane.Empty;
            V = VideoFramePlane.Empty;
            return;
        }

        long chromaBytes = (long)descriptor.ChromaStride * descriptor.ChromaAllocationRows
            + VideoFrameBufferDescriptor.TailPadding;

        U = new VideoFramePlane(
            baseAddress + (nint)lumaBytes,
            descriptor.ChromaStride,
            descriptor.ChromaVisibleWidth,
            descriptor.ChromaVisibleHeight,
            descriptor.BytesPerSample);

        V = new VideoFramePlane(
            baseAddress + (nint)(lumaBytes + chromaBytes),
            descriptor.ChromaStride,
            descriptor.ChromaVisibleWidth,
            descriptor.ChromaVisibleHeight,
            descriptor.BytesPerSample);
    }

    /// <summary>The number of bytes this buffer occupies, tail padding included.</summary>
    public long AllocationBytes { get; }

    /// <summary>True once the memory behind this buffer has been freed.</summary>
    public bool IsFreed => baseAddress == IntPtr.Zero;

    /// <summary>Fills every plane, padding included, with zero.</summary>
    /// <remarks>
    /// Used by tests and by the uncompressed-video path; ordinary decoding overwrites the samples anyway and
    /// does not pay for this.
    /// </remarks>
    public unsafe void Clear()
    {
        if (baseAddress == IntPtr.Zero) return;
        NativeMemory.Clear((void*)baseAddress, (nuint)AllocationBytes);
    }

    internal unsafe void Free()
    {
        if (baseAddress == IntPtr.Zero) return;
        NativeMemory.AlignedFree((void*)baseAddress);
        baseAddress = IntPtr.Zero;
        Y = VideoFramePlane.Empty;
        U = VideoFramePlane.Empty;
        V = VideoFramePlane.Empty;
    }
}
