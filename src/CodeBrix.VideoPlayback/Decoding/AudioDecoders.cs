using System;
using System.Collections.Generic;
using CodeBrix.Audio.Wave;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Asks the audio package which packet codecs it can decode, WITHOUT opening the audio device - the audio
/// counterpart of <see cref="VideoDecoders.IsCodecSupported" />.
/// </summary>
/// <remarks>
/// <para>
/// Audio decoders are not registered here. They live on the shared audio output in CodeBrix.Audio: Vorbis is
/// built in and always available, and Opus arrives with the CodeBrix.Audio.Opus package and one
/// <c>CodeBrixAudioOpus.Register()</c> call at start-up. This type only ASKS, and asking starts nothing: it
/// forwards to <c>SharedAudioOutput.IsPacketCodecSupported</c>, which reads the registry without starting the
/// shared output. Building a decoder - which is what a session does when it really is going to play sound -
/// DOES open the device, so it must never be used as a question.
/// </para>
/// <para>
/// That is what makes a preflight check possible: a file browser, a thumbnail extractor or a session built
/// with <see cref="CodeBrix.VideoPlayback.VideoPlaybackOptions.PlayAudio" /> false can say "this file needs
/// the Opus package" on a machine with no sound card at all.
/// </para>
/// <code>
/// foreach (MediaTrackInfo track in session.Tracks)
/// {
///     if (track.Kind != MediaTrackKind.Audio) continue;
///     bool playable = AudioDecoders.IsCodecSupported(track.CodecId);
/// }
/// </code>
/// <para>
/// The answer is about the SEAM, not about one track: a codec that is registered may still decline a
/// particular track's codec-private data. And it answers for the SHARED audio output only - a factory
/// registered directly on an <c>AudioEngine</c> of the application's own is invisible to it, exactly as the
/// audio package documents.
/// </para>
/// </remarks>
public static class AudioDecoders
{
    /// <summary>Reports whether the shared audio output can decode packets of a codec.</summary>
    /// <param name="codecId">The codec identifier, as the container states it - "opus" or "vorbis".</param>
    /// <returns>True when a packet codec factory on the shared audio output claims the codec.</returns>
    /// <remarks>
    /// Matching is case-insensitive. Nothing is started and no audio device is opened, so this is safe on a
    /// machine with no sound hardware.
    /// </remarks>
    public static bool IsCodecSupported(string codecId)
    {
        if (string.IsNullOrEmpty(codecId)) return false;
        return SharedAudioOutput.IsPacketCodecSupported(codecId);
    }

    /// <summary>Every packet codec the shared audio output can decode today.</summary>
    /// <remarks>
    /// Built-in codecs first, then whatever an application registered. Nothing is started and no audio device
    /// is opened. The set grows when a package such as CodeBrix.Audio.Opus registers itself, so read it after
    /// start-up rather than caching it.
    /// </remarks>
    public static IReadOnlyCollection<string> SupportedCodecIds
    {
        get
        {
            IReadOnlyCollection<string> ids = SharedAudioOutput.SupportedPacketCodecIds;
            return ids == null ? Array.Empty<string>() : ids;
        }
    }
}
