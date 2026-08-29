using CodeBrix.Audio.Playback;
using CodeBrix.VideoPlayback.Internal;

namespace CodeBrix.VideoPlayback.Audio;

/// <summary>
/// Hands the session's queued audio packets to the audio engine, on the audio thread, without ever blocking
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The audio player pulls: it asks for the next packet exactly when it needs one, from the audio thread. So
/// both members here have to return immediately, which is why the demultiplexing thread has already read the
/// packets into a ring and this class only moves a cursor.
/// </para>
/// <para>
/// Running dry is not an error. Returning false with <see cref="EndOfStream" /> still false means "nothing
/// ready this instant": the player fills the gap with silence and keeps the voice alive, ready for the packets
/// that follow.
/// </para>
/// <para>
/// Every packet carries the container's per-block discard padding through
/// <see cref="AudioPacket.DiscardPadding" />. The audio player applies the LARGER of that and the track-level
/// trim the session set with <c>SetTrailingTrim</c>, so a container that states its padding per block and one
/// that states it once in a track header are both honoured. The per-packet value is best-effort - it can only
/// hold back what is still in the player's hand plus what that packet decodes to - which is why the session
/// also sets the track-level trim, the exact instrument. See the audio package's AGENT-README, TRIMMING THE
/// END OF A TRACK.
/// </para>
/// <para>
/// <b>The loss seam.</b> A file source never loses a packet: a byte range either reads or throws, so nothing
/// here ever produces <see cref="AudioPacket.Loss(System.TimeSpan, System.Nullable{System.TimeSpan})" />. A
/// future source that CAN see a gap - a live stream, a lossy transport - reports it by handing the player a
/// loss packet of the gap's own length, and the decoder conceals it when its
/// <c>SupportsLossConcealment</c> says it can and the player fills the rest with silence. Either way the gap
/// comes out the length it really was, so the audio after it keeps its position. A moment when this session's
/// reader has merely not kept up is NOT a loss: it is the false-with-no-end-of-stream answer above, which
/// consumes none of the timeline.
/// </para>
/// </remarks>
internal sealed class SessionAudioPacketSource : IAudioPacketSource
{
    private readonly PacketRing ring;
    private bool holding;
    private volatile bool endOfStream;

    internal SessionAudioPacketSource(PacketRing ring)
    {
        this.ring = ring;
    }

    /// <inheritdoc />
    public bool EndOfStream
    {
        get => endOfStream;
    }

    internal void SetEndOfStream(bool value) => endOfStream = value;

    /// <inheritdoc />
    public bool TryReadPacket(out AudioPacket packet)
    {
        if (holding)
        {
            ring.EndRead();
            holding = false;
        }

        if (!ring.TryBeginRead(out RingPacket queued))
        {
            packet = default;
            return false;
        }

        holding = true;

        // A struct, built from values the ring already holds - no allocation on the audio thread, which is
        // the whole point of the ring sitting between the two.
        packet = new AudioPacket(queued.Data, queued.Timestamp, queued.DiscardPadding);
        return true;
    }

    /// <summary>Releases any packet still held, so the ring can be cleared safely after a seek.</summary>
    internal void ReleaseHeldPacket()
    {
        if (!holding) return;
        ring.EndRead();
        holding = false;
    }
}
