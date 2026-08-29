namespace CodeBrix.VideoPlayback.Frames;

/// <summary>
/// A snapshot of what a frame-buffer pool has been doing - the numbers that prove playback is not allocating.
/// </summary>
/// <remarks>
/// The one to watch is <see cref="Allocations" />. In a healthy steady state it stops rising after the first
/// few frames: every rent is served out of the free list. It rises again only when the frame size changes,
/// which also raises <see cref="Generation" />.
/// </remarks>
public readonly struct VideoFrameBufferPoolStatistics
{
    /// <summary>Creates a statistics snapshot.</summary>
    /// <param name="rents">How many times a buffer has been rented.</param>
    /// <param name="returns">How many times a buffer has been returned.</param>
    /// <param name="allocations">How many buffers have actually been allocated.</param>
    /// <param name="releases">How many buffers have been freed rather than pooled.</param>
    /// <param name="pooled">How many buffers are sitting in the free list right now.</param>
    /// <param name="live">How many buffers are rented out right now.</param>
    /// <param name="waitingOnFences">How many returned buffers are held back by an unsignalled fence.</param>
    /// <param name="generation">The pool's current generation.</param>
    /// <param name="bytesAllocated">The total number of bytes the pool has ever allocated.</param>
    /// <param name="bytesResident">The number of bytes the pool currently holds.</param>
    public VideoFrameBufferPoolStatistics(
        long rents,
        long returns,
        long allocations,
        long releases,
        int pooled,
        int live,
        int waitingOnFences,
        int generation,
        long bytesAllocated,
        long bytesResident)
    {
        Rents = rents;
        Returns = returns;
        Allocations = allocations;
        Releases = releases;
        Pooled = pooled;
        Live = live;
        WaitingOnFences = waitingOnFences;
        Generation = generation;
        BytesAllocated = bytesAllocated;
        BytesResident = bytesResident;
    }

    /// <summary>How many times a buffer has been rented since the pool was created.</summary>
    public long Rents { get; }

    /// <summary>How many times a buffer has been returned since the pool was created.</summary>
    public long Returns { get; }

    /// <summary>How many buffers the pool has actually allocated - the number that must stop rising.</summary>
    public long Allocations { get; }

    /// <summary>How many buffers the pool has freed instead of pooling, because they belonged to an older generation.</summary>
    public long Releases { get; }

    /// <summary>How many buffers are sitting in the free list, ready to be rented without allocating.</summary>
    public int Pooled { get; }

    /// <summary>How many buffers are rented out right now.</summary>
    public int Live { get; }

    /// <summary>How many returned buffers are being held back because a presenter's fence has not signalled yet.</summary>
    public int WaitingOnFences { get; }

    /// <summary>
    /// The pool's current generation. It increments every time the requested frame shape changes, which is
    /// the only event that legitimately allocates.
    /// </summary>
    public int Generation { get; }

    /// <summary>The total number of bytes the pool has ever allocated.</summary>
    public long BytesAllocated { get; }

    /// <summary>The number of bytes the pool currently holds, pooled and rented out together.</summary>
    public long BytesResident { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"rents {Rents}, returns {Returns}, allocations {Allocations}, pooled {Pooled}, live {Live}, "
        + $"fenced {WaitingOnFences}, generation {Generation}, resident {BytesResident} bytes";
}
