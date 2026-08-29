using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// The frame-buffer pool this package hands to every decoder: 64-byte-aligned unmanaged memory, padded
/// dimensions, thread-safe return, and no allocation at all once playback is warm.
/// </summary>
/// <remarks>
/// <para>
/// The layout it produces is the one documented on <see cref="VideoFrameBuffer" />, which is what a software
/// AV1 decoder asks its host allocator for and what a graphics upload wants to read: planes aligned to 64
/// bytes, strides a multiple of 64 bytes, both dimensions rounded up to a multiple of 128 samples, 64 bytes
/// of slack after each plane, the two chroma planes sharing a stride, and 10-bit and 12-bit samples stored as
/// little-endian 16-bit words justified towards the least significant bit.
/// </para>
/// <para>
/// <b>Generations.</b> Buffers are pooled by frame shape. When a stream's frame shape changes - a resolution
/// change, or a bit-depth change - the pool starts a new generation: buffers of the old shape are freed as
/// they come back instead of being reused, so a size change costs one round of allocation and then settles.
/// A buffer knows which generation it belongs to, so a frame still being displayed across the change stays
/// valid and is disposed of correctly.
/// </para>
/// <para>
/// <b>Fences.</b> A returned buffer whose <see cref="VideoFrameBuffer.Tag" /> holds an
/// <see cref="IVideoFrameFence" /> (or a <c>System.Func&lt;bool&gt;</c>) is parked until that fence signals.
/// Parked buffers are re-examined on every rent and return, and by <see cref="PumpFences" /> for a caller
/// that wants to force the question - a presenter that has just finished a batch of uploads, for instance.
/// </para>
/// <para>Every member is safe to call from any thread.</para>
/// </remarks>
public sealed class PinnedFrameBufferPool : IVideoFrameBufferPool, IDisposable
{
    private const int FrameObjectCapacity = 64;

    private readonly object gate = new object();
    private readonly Stack<PinnedVideoFrameBuffer> free = new Stack<PinnedVideoFrameBuffer>();
    private readonly List<PinnedVideoFrameBuffer> fenced = new List<PinnedVideoFrameBuffer>();
    private readonly VideoFrame[] frameObjects = new VideoFrame[FrameObjectCapacity];

    private VideoFrameBufferDescriptor currentDescriptor;
    private bool hasCurrentDescriptor;
    private int generation;
    private long rents;
    private long returns;
    private long allocations;
    private long releases;
    private int live;
    private long bytesAllocated;
    private long bytesResident;
    private int frameObjectCount;
    private bool disposed;

    /// <summary>Creates an empty pool. The first rent allocates; every rent after that should not.</summary>
    public PinnedFrameBufferPool()
    {
    }

    /// <summary>The pool's current generation, which increments each time the requested frame shape changes.</summary>
    public int Generation
    {
        get
        {
            lock (gate) return generation;
        }
    }

    /// <summary>Takes a snapshot of the pool's counters.</summary>
    /// <returns>The counters as they stood at the moment of the call.</returns>
    public VideoFrameBufferPoolStatistics GetStatistics()
    {
        lock (gate)
        {
            return new VideoFrameBufferPoolStatistics(
                rents,
                returns,
                allocations,
                releases,
                free.Count,
                live,
                fenced.Count,
                generation,
                bytesAllocated,
                bytesResident);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public VideoFrameBuffer Rent(VideoFrameBufferDescriptor descriptor)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PinnedFrameBufferPool));

            CollectSignaledFencesNoLock();

            if (!hasCurrentDescriptor || !currentDescriptor.Equals(descriptor))
            {
                if (hasCurrentDescriptor) generation++;
                hasCurrentDescriptor = true;
                currentDescriptor = descriptor;
                DiscardFreeListNoLock();
            }

            PinnedVideoFrameBuffer buffer = free.Count > 0 ? free.Pop() : AllocateNoLock(descriptor);
            buffer.Tag = null;
            rents++;
            live++;
            return buffer;
        }
    }

    /// <inheritdoc />
    public void Return(VideoFrameBuffer buffer)
    {
        if (buffer == null) return;

        if (buffer is not PinnedVideoFrameBuffer pinned)
        {
            throw new ArgumentException(
                $"A {nameof(PinnedFrameBufferPool)} can only take back buffers it produced; this one is a "
                + $"{buffer.GetType().Name}.",
                nameof(buffer));
        }

        lock (gate)
        {
            returns++;
            if (live > 0) live--;

            if (disposed)
            {
                FreeNoLock(pinned);
                return;
            }

            CollectSignaledFencesNoLock();

            if (!pinned.IsFenceSignaled())
            {
                fenced.Add(pinned);
                return;
            }

            RecycleNoLock(pinned);
        }
    }

    /// <summary>
    /// Hands out a recycled <see cref="VideoFrame" /> object, or a new one when there is none to reuse.
    /// </summary>
    /// <remarks>
    /// The frame OBJECT is recycled as well as the buffer, so a decode loop allocates nothing at all. The
    /// free list belongs to this pool rather than to the process, so two sessions never hand each other's
    /// recycled frames about.
    /// </remarks>
    /// <returns>A frame object with no state, ready for <see cref="VideoFrame.Create" /> to fill in.</returns>
    internal VideoFrame TakeFrameObject()
    {
        lock (gate)
        {
            if (frameObjectCount == 0) return VideoFrame.CreateUninitialized();

            frameObjectCount--;
            VideoFrame frame = frameObjects[frameObjectCount];
            frameObjects[frameObjectCount] = null;
            return frame;
        }
    }

    /// <summary>Takes a released frame object back for reuse.</summary>
    /// <param name="frame">The frame whose last reference has just dropped.</param>
    internal void ReturnFrameObject(VideoFrame frame)
    {
        lock (gate)
        {
            if (frameObjectCount >= FrameObjectCapacity) return;
            frameObjects[frameObjectCount] = frame;
            frameObjectCount++;
        }
    }

    /// <summary>
    /// Re-examines every buffer that was held back by a fence and recycles the ones whose fence has since
    /// signalled.
    /// </summary>
    /// <returns>How many buffers became reusable.</returns>
    /// <remarks>
    /// The pool does this on every rent and return anyway. Call it explicitly when a presenter has just
    /// finished a batch of graphics work and wants the memory back before the next frame arrives.
    /// </remarks>
    public int PumpFences()
    {
        lock (gate)
        {
            return CollectSignaledFencesNoLock();
        }
    }

    /// <summary>
    /// Frees every buffer the pool is holding. Buffers still rented out are freed as they come back.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            while (free.Count > 0) FreeNoLock(free.Pop());
            foreach (PinnedVideoFrameBuffer buffer in fenced) FreeNoLock(buffer);
            fenced.Clear();
        }
    }

    private int CollectSignaledFencesNoLock()
    {
        if (fenced.Count == 0) return 0;

        int collected = 0;
        for (int i = fenced.Count - 1; i >= 0; i--)
        {
            PinnedVideoFrameBuffer candidate = fenced[i];
            if (!candidate.IsFenceSignaled()) continue;

            fenced.RemoveAt(i);
            RecycleNoLock(candidate);
            collected++;
        }

        return collected;
    }

    private void RecycleNoLock(PinnedVideoFrameBuffer buffer)
    {
        buffer.Tag = null;

        if (buffer.Generation != generation || !hasCurrentDescriptor || !buffer.Descriptor.Equals(currentDescriptor))
        {
            FreeNoLock(buffer);
            return;
        }

        free.Push(buffer);
    }

    private void DiscardFreeListNoLock()
    {
        while (free.Count > 0) FreeNoLock(free.Pop());
    }

    private PinnedVideoFrameBuffer AllocateNoLock(VideoFrameBufferDescriptor descriptor)
    {
        PinnedVideoFrameBuffer buffer = new PinnedVideoFrameBuffer(descriptor, generation);
        allocations++;
        bytesAllocated += buffer.AllocationBytes;
        bytesResident += buffer.AllocationBytes;
        return buffer;
    }

    private void FreeNoLock(PinnedVideoFrameBuffer buffer)
    {
        if (buffer.IsFreed) return;
        bytesResident -= buffer.AllocationBytes;
        releases++;
        buffer.Free();
    }
}
