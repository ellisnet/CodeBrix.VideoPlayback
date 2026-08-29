using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;

namespace CodeBrix.VideoPlayback.Containers;

/// <summary>
/// A demultiplexer: it knows a container's structure, lists its tracks, and hands out their packets in the
/// order they are stored.
/// </summary>
/// <remarks>
/// <para>
/// Both readers in this package - Matroska and WebM, and the bespoke container - implement this, so a session
/// plays either without knowing which it opened.
/// </para>
/// <para>
/// A reader is used from ONE thread, the demultiplexing thread. The lists it exposes are safe to read from
/// any thread once the file has been opened.
/// </para>
/// </remarks>
public interface IMediaContainerReader : IDisposable
{
    /// <summary>The container format's name, for messages and diagnostics - for example "Matroska/WebM".</summary>
    string FormatName { get; }

    /// <summary>
    /// How long the media lasts, or <see cref="TimeSpan.Zero" /> when the file does not say and it cannot be
    /// worked out without reading everything.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>True when <see cref="Seek" /> can be used - the file has an index and the source can seek.</summary>
    bool CanSeek { get; }

    /// <summary>Every track the file declares, including the ones this library will not play.</summary>
    IReadOnlyList<MediaTrackInfo> Tracks { get; }

    /// <summary>The file's text caption tracks.</summary>
    /// <remarks>
    /// Whether the cues are all present from the start depends on the container - see
    /// <see cref="CaptionTrack.AreCuesComplete" />.
    /// </remarks>
    IReadOnlyList<CaptionTrack> CaptionTracks { get; }

    /// <summary>The file's chapters, flattened to one edition and ordered by start time.</summary>
    IReadOnlyList<Chapter> Chapters { get; }

    /// <summary>
    /// Things the reader stepped over and thought worth mentioning - a bitmap subtitle track it cannot read,
    /// an element it does not model. Never null; usually empty.
    /// </summary>
    IReadOnlyList<string> Notices { get; }

    /// <summary>Reads the next packet in storage order.</summary>
    /// <param name="packet">
    /// The packet. Its <see cref="MediaPacket.Data" /> is borrowed from the reader and is only valid until the
    /// next call.
    /// </param>
    /// <returns>True when a packet was read; false at the end of the media.</returns>
    /// <exception cref="VideoPlaybackException">The file is malformed.</exception>
    bool TryReadPacket(out MediaPacket packet);

    /// <summary>
    /// Says whether the reader can PROVE that a track will produce no further packets from where it now
    /// stands.
    /// </summary>
    /// <param name="trackId">The track to ask about.</param>
    /// <returns>
    /// True only when no further packet for that track exists between the reader's current position and the
    /// end of the media. False means "not proven" - which covers both "there is definitely more" and "this
    /// reader cannot tell yet". A track the file does not declare reads as exhausted, because nothing will
    /// ever arrive for it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> A player has to know that a track has ENDED, not merely that it has gone
    /// quiet, and the two are not the same thing: an audio track that finishes a second before the picture
    /// does looks exactly like an audio track whose next packet has not been demultiplexed yet. Told the
    /// difference, a session can let the sound finish and carry the picture on; not told it, the session can
    /// only wait - and a player that waits for something which is never coming has stopped.
    /// </para>
    /// <para>
    /// <b>What it must never do</b> is answer true speculatively. A false negative costs a little latency;
    /// a false positive truncates the media. Every reader here is written that way round, and each states
    /// below exactly when it can answer at all.
    /// </para>
    /// <para>
    /// <b>The two shipped readers.</b> The bespoke container answers EXACTLY and EARLY - its index names the
    /// track of every chunk in the file, so the reader knows where each track's last chunk is before it has
    /// read a single one. Matroska and WebM answer exactly only once every cluster has been read, because
    /// nothing in the format says where a track stops: Cues index key frames, usually of the video track
    /// alone, so they can prove that a track is NOT finished but never that it is.
    /// </para>
    /// <para>
    /// A reader that has been repositioned by <see cref="Seek" /> answers for its NEW position, so a track
    /// exhausted before a backwards seek is not exhausted after it.
    /// </para>
    /// <para>Called from the demultiplexing thread, like <see cref="TryReadPacket" />.</para>
    /// </remarks>
    bool IsTrackExhausted(int trackId);

    /// <summary>The timestamp of a track's last packet, when the reader knows it.</summary>
    /// <param name="trackId">The track to ask about.</param>
    /// <returns>
    /// The timestamp of the last packet the track will ever produce, or null when the reader cannot know it
    /// without reading the rest of the file.
    /// </returns>
    /// <remarks>
    /// This is the same knowledge as <see cref="IsTrackExhausted" /> seen from the other side, and it comes
    /// from the same place: an index that names every packet's track. It is not needed to play a file - it
    /// is for a caller that wants to say how long each track actually lasts, which is not always what the
    /// container's overall duration says.
    /// </remarks>
    TimeSpan? GetTrackEndTimestamp(int trackId);

    /// <summary>Repositions the reader so that the next packets cover a given moment.</summary>
    /// <param name="position">Where playback should resume.</param>
    /// <param name="keyFrameTrackId">
    /// The track whose key frames decide where the reader may land - the video track, normally - or -1 to let
    /// the reader choose.
    /// </param>
    /// <returns>
    /// The position the reader actually landed on, which is at or before <paramref name="position" />: the
    /// caller decodes and discards from there to reach the exact moment.
    /// </returns>
    /// <exception cref="NotSupportedException"><see cref="CanSeek" /> is false.</exception>
    /// <exception cref="VideoPlaybackException">The file is malformed.</exception>
    TimeSpan Seek(TimeSpan position, int keyFrameTrackId);
}
