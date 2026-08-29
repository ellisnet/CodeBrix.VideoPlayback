using System;
using CodeBrix.VideoPlayback.Frames;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// A video decoder: compressed packets go in, decoded frames come out.
/// </summary>
/// <remarks>
/// <para>
/// The model is push-then-pull, which is what every modern video codec wants: send a packet, then pull
/// frames until there are none, then send the next packet. A codec with frame-level parallelism will absorb
/// several packets before producing anything and then produce several in a row, so neither call maps
/// one-to-one onto the other.
/// </para>
/// <para>
/// The loop a caller writes is:
/// </para>
/// <code>
/// decoder.SendPacket(packet);
/// while (decoder.TryReceiveFrame(out VideoFrame frame))
/// {
///     using (frame)
///     {
///         // ... present it, or hand a Retain() of it to something that outlives this block
///     }
/// }
/// </code>
/// <para>
/// Every frame handed out carries ONE reference, and the caller owns it. At the end of the stream call
/// <see cref="Drain" /> and pull again until <see cref="TryReceiveFrame" /> is false; after a seek call
/// <see cref="Flush" /> before sending the first packet from the new position.
/// </para>
/// <para>
/// One thread at a time. A decoder is not required to tolerate concurrent calls - the session in this
/// package drives one from a single decode thread - although the frames it produces may be READ from any
/// thread once they exist.
/// </para>
/// </remarks>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// What the decoder knows about the stream so far. Every field reads zero or unknown until enough of the
    /// stream has been parsed; see <see cref="VideoStreamInfo.IsKnown" />.
    /// </summary>
    VideoStreamInfo Info { get; }

    /// <summary>
    /// True when the decoder writes its output straight into the pool from
    /// <see cref="VideoDecoderOptions.BufferPool" />, so there is no copy between decoding and display.
    /// </summary>
    /// <remarks>
    /// False is a perfectly good answer: a decoder that must own its own memory still produces frames over
    /// pool buffers, at the cost of one copy per frame. The pipeline does not change; only the copy count
    /// does.
    /// </remarks>
    bool SupportsExternalBuffers { get; }

    /// <summary>The codec identifier this decoder was created for - see <see cref="VideoCodecIds" />.</summary>
    string CodecId { get; }

    /// <summary>Hands the decoder one compressed access unit.</summary>
    /// <param name="packet">The packet. Its memory only has to stay valid for the duration of the call.</param>
    /// <returns>
    /// True when the packet was taken. False means the decoder is full and the caller must pull frames with
    /// <see cref="TryReceiveFrame" /> before offering the same packet again.
    /// </returns>
    /// <exception cref="VideoPlaybackException">The packet could not be decoded.</exception>
    bool SendPacket(VideoPacket packet);

    /// <summary>Takes the next finished frame, if there is one.</summary>
    /// <param name="frame">
    /// The frame, carrying one reference which the caller owns and must dispose; null when the method
    /// returns false.
    /// </param>
    /// <returns>True when a frame was produced; false when the decoder needs more input first.</returns>
    /// <exception cref="VideoPlaybackException">Decoding failed.</exception>
    bool TryReceiveFrame(out VideoFrame frame);

    /// <summary>
    /// Throws away everything buffered, ready for packets from a completely different position in the
    /// stream. Call it after a seek, before sending the first packet from the new position.
    /// </summary>
    /// <remarks>
    /// Frames already handed out stay valid: they hold their own references, and the memory behind them
    /// returns to the pool when their holders are done.
    /// </remarks>
    void Flush();

    /// <summary>
    /// Tells the decoder that no more packets are coming, so it should finish and emit whatever it is
    /// holding. Keep calling <see cref="TryReceiveFrame" /> until it returns false.
    /// </summary>
    void Drain();
}
