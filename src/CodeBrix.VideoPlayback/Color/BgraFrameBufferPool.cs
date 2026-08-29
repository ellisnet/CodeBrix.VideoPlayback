using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Color;

/// <summary>
/// Hands out and takes back the BGRA surfaces the GPU-free render path converts into, so that playback
/// allocates nothing once it is warm.
/// </summary>
/// <remarks>
/// <para>
/// A player on the GPU-free path needs a BGRA surface for every frame it draws, and a 1080p surface is eight
/// megabytes. Allocating one per frame would hand the garbage collector eight megabytes of large-object
/// traffic twenty-five times a second; renting one instead costs nothing after the first frame.
/// </para>
/// <para>
/// Buffers are bucketed by size, so a resolution change simply starts renting from a different bucket - and
/// the old bucket's memory is released the next time <see cref="Trim" /> or <see cref="Dispose" /> runs.
/// </para>
/// <para>Every member is safe to call from any thread.</para>
/// </remarks>
public sealed class BgraFrameBufferPool : IDisposable
{
    private readonly object gate = new object();
    private readonly Dictionary<long, Stack<BgraFrameBuffer>> buckets = new Dictionary<long, Stack<BgraFrameBuffer>>();

    private long allocations;
    private int pooled;
    private bool disposed;

    /// <summary>Creates an empty pool. The first rent of a given size allocates; the ones after it should not.</summary>
    public BgraFrameBufferPool()
    {
    }

    /// <summary>
    /// How many surfaces the pool has actually allocated. In a healthy steady state this stops rising.
    /// </summary>
    public long Allocations
    {
        get
        {
            lock (gate) return allocations;
        }
    }

    /// <summary>How many surfaces are sitting in the free list, ready to be rented without allocating.</summary>
    public int Pooled
    {
        get
        {
            lock (gate) return pooled;
        }
    }

    /// <summary>Takes a surface of the requested size out of the pool, allocating one only if it has none.</summary>
    /// <param name="width">The number of pixels in a row. Must be greater than zero.</param>
    /// <param name="height">The number of rows. Must be greater than zero.</param>
    /// <returns>A surface of exactly that size. Its contents are whatever the previous tenant left behind.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not greater than zero.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public BgraFrameBuffer Rent(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be greater than zero.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be greater than zero.");

        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(BgraFrameBufferPool));

            long key = MakeKey(width, height);
            if (buckets.TryGetValue(key, out Stack<BgraFrameBuffer> bucket) && bucket.Count > 0)
            {
                pooled--;
                return bucket.Pop();
            }

            BgraFrameBuffer buffer = new BgraFrameBuffer(width, height);
            allocations++;
            return buffer;
        }
    }

    /// <summary>Gives a surface back so the next rent of that size can reuse it.</summary>
    /// <param name="buffer">The surface to return. A null reference is ignored.</param>
    /// <remarks>A surface returned to a disposed pool is freed rather than kept.</remarks>
    public void Return(BgraFrameBuffer buffer)
    {
        if (buffer == null) return;
        if (buffer.IsFreed) return;

        lock (gate)
        {
            if (disposed)
            {
                buffer.Free();
                return;
            }

            long key = MakeKey(buffer.Width, buffer.Height);
            if (!buckets.TryGetValue(key, out Stack<BgraFrameBuffer> bucket))
            {
                bucket = new Stack<BgraFrameBuffer>();
                buckets[key] = bucket;
            }

            bucket.Push(buffer);
            pooled++;
        }
    }

    /// <summary>Frees every pooled surface EXCEPT those of the given size, and returns how many it freed.</summary>
    /// <param name="keepWidth">The width to keep.</param>
    /// <param name="keepHeight">The height to keep.</param>
    /// <returns>How many surfaces were freed.</returns>
    /// <remarks>
    /// Call this after a resolution change to give back the memory the old size was using. Surfaces that are
    /// currently rented out are untouched; they are freed when they come back.
    /// </remarks>
    public int Trim(int keepWidth, int keepHeight)
    {
        long keep = MakeKey(keepWidth, keepHeight);
        int freed = 0;

        lock (gate)
        {
            List<long> stale = null;
            foreach (KeyValuePair<long, Stack<BgraFrameBuffer>> entry in buckets)
            {
                if (entry.Key == keep) continue;
                (stale ??= new List<long>()).Add(entry.Key);
            }

            if (stale == null) return 0;

            foreach (long key in stale)
            {
                Stack<BgraFrameBuffer> bucket = buckets[key];
                while (bucket.Count > 0)
                {
                    bucket.Pop().Free();
                    pooled--;
                    freed++;
                }

                buckets.Remove(key);
            }
        }

        return freed;
    }

    /// <summary>
    /// Frees every surface the pool is holding. Surfaces still rented out are freed as they come back.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            foreach (KeyValuePair<long, Stack<BgraFrameBuffer>> entry in buckets)
            {
                while (entry.Value.Count > 0) entry.Value.Pop().Free();
            }

            buckets.Clear();
            pooled = 0;
        }
    }

    private static long MakeKey(int width, int height) => ((long)width << 32) | (uint)height;
}
