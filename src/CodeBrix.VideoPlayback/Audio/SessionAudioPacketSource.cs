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
        packet = new AudioPacket(queued.Data, queued.Timestamp);
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
