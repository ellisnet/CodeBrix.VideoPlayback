using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeBrix.Audio.Codecs;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// Creates the decoders an export reads its source with - from the core's registries only, exactly as a playback
/// session does - and words the refusal when one is missing.
/// </summary>
/// <remarks>
/// This package never references a decoder. AV1 comes from whatever the application registered with
/// <see cref="VideoDecoders" /> (CodeBrix.VideoPlayback.Dav1d's <c>Register()</c>), the sound from the packet
/// codec factories the shared audio output holds (Vorbis and FLAC built into CodeBrix.Audio.Core, Opus from
/// CodeBrix.Audio.Opus's <c>Register()</c>) - asked directly, so the shared output is never started and no audio
/// device is ever opened.
/// </remarks>
internal static class ExportDecoding
{
    /// <summary>
    /// Lets a test add packet codec factories for the flow it runs in only - so a codec can be decoded through the
    /// same lookup an application's export uses without registering its package process-wide, which other tests in
    /// the same process assert has not happened. They are treated as registered after everything the application
    /// registered. Null - the default - adds nothing.
    /// </summary>
    internal static readonly AsyncLocal<IReadOnlyList<IPacketCodecFactory>> FlowPacketCodecFactories =
        new AsyncLocal<IReadOnlyList<IPacketCodecFactory>>();

    // The packet codecs CodeBrix.Audio.Core builds into the shared output, made here directly so that decoding
    // never needs the shared output - and so never needs an audio device.
    private static readonly IPacketCodecFactory BuiltInVorbis = new VorbisPacketCodecFactory();
    private static readonly IPacketCodecFactory BuiltInFlac = new FlacPacketCodecFactory();

    /// <summary>Creates the decoder for a picture or alpha-plane track.</summary>
    /// <param name="track">The track.</param>
    /// <param name="options">The decoder options, with the frame pool set.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="VideoAuthoringException">No registered factory serves the codec, or the one that does failed.</exception>
    internal static IVideoDecoder CreateVideoDecoder(MediaTrackInfo track, VideoDecoderOptions options)
    {
        IVideoDecoder decoder;
        try
        {
            decoder = VideoDecoders.TryCreateDecoder(track.CodecId, DescribeCodecPrivate(track), options);
        }
        catch (VideoPlaybackException ex)
        {
            throw new VideoAuthoringException("The export cannot decode its picture: " + ex.Message, ex);
        }

        if (decoder == null) throw new VideoAuthoringException(DescribeMissingVideoDecoder(track.CodecId));
        return decoder;
    }

    /// <summary>Creates the decoder for the audio track without starting the shared audio output.</summary>
    /// <param name="track">The audio track.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="VideoAuthoringException">No factory serves the codec.</exception>
    /// <remarks>
    /// <c>SharedAudioOutput.CreatePacketDecoder</c> is deliberately NOT used: it starts the shared output and opens
    /// the audio device, because its registry lives on the running engine - and an export must run on a host with no
    /// sound device at all. The same factories are asked directly instead: see <see cref="PacketCodecCandidates" />.
    /// </remarks>
    internal static IPacketSoundDecoder CreateAudioDecoder(MediaTrackInfo track)
    {
        IPacketSoundDecoder decoder;
        try
        {
            decoder = CreatePacketDecoder(PacketCodecCandidates(), track.CodecId, track.CodecPrivate);
        }
        catch (NotSupportedException ex)
        {
            throw new VideoAuthoringException(DescribeMissingAudioDecoder(track.CodecId), ex);
        }

        if (decoder == null) throw new VideoAuthoringException(DescribeMissingAudioDecoder(track.CodecId));
        return decoder;
    }

    /// <summary>
    /// The packet codec factories the shared audio output would consult, in the order it holds them: the built-in
    /// Vorbis and FLAC factories, then everything registered with <c>SharedAudioOutput.RegisterPacketCodecFactory</c>
    /// (CodeBrix.Audio.Opus's factory among them once <c>CodeBrixAudioOpus.Register()</c> has run), then any a test
    /// added for its own flow. Reading the registry starts nothing.
    /// </summary>
    /// <returns>The candidates.</returns>
    internal static IReadOnlyList<IPacketCodecFactory> PacketCodecCandidates()
    {
        List<IPacketCodecFactory> candidates = new List<IPacketCodecFactory> { BuiltInVorbis, BuiltInFlac };

        IReadOnlyList<IPacketCodecFactory> registered = SharedAudioOutput.RegisteredPacketCodecFactories;
        if (registered != null) candidates.AddRange(registered);

        IReadOnlyList<IPacketCodecFactory> flow = FlowPacketCodecFactories.Value;
        if (flow != null) candidates.AddRange(flow);

        return candidates;
    }

    /// <summary>
    /// Picks a packet decoder by the audio engine's own rule: the factories that claim the codec (matched
    /// case-insensitively), highest <see cref="IPacketCodecFactory.Priority" /> first and, among equals, in the order
    /// given; the first whose <see cref="IPacketCodecFactory.CreateDecoder" /> returns a decoder wins, and a null lets
    /// the next one try.
    /// </summary>
    /// <param name="factories">The candidates, in registration order.</param>
    /// <param name="codecId">The codec, as the container states it.</param>
    /// <param name="codecPrivate">The track's codec-private data.</param>
    /// <returns>The decoder, or null when no factory serves the codec or every one that claims it declined.</returns>
    internal static IPacketSoundDecoder CreatePacketDecoder(
        IEnumerable<IPacketCodecFactory> factories, string codecId, ReadOnlyMemory<byte> codecPrivate)
    {
        if (factories == null || string.IsNullOrEmpty(codecId)) return null;

        IEnumerable<IPacketCodecFactory> claiming = factories
            .Where(factory => factory != null && Claims(factory, codecId))
            .OrderByDescending(factory => factory.Priority);

        foreach (IPacketCodecFactory factory in claiming)
        {
            IPacketSoundDecoder decoder = factory.CreateDecoder(codecId, codecPrivate, null);
            if (decoder != null) return decoder;
        }

        return null;
    }

    /// <summary>The refusal for a picture codec with no registered decoder - the playback session's own wording.</summary>
    /// <param name="codecId">The codec.</param>
    /// <returns>The message.</returns>
    internal static string DescribeMissingVideoDecoder(string codecId)
    {
        if (string.Equals(codecId, VideoCodecIds.Av1, StringComparison.OrdinalIgnoreCase))
        {
            return "The export cannot decode its picture: video codec 'av01' has no registered decoder - reference "
                + "CodeBrix.VideoPlayback.Dav1d and call CodeBrixVideoPlaybackDav1d.Register() at start-up.";
        }

        return "The export cannot decode its picture: video codec '" + codecId + "' has no registered decoder - "
            + "register an IVideoDecoderFactory that serves it with VideoDecoders.Register(...).";
    }

    /// <summary>The refusal for an audio codec with no registered decoder - the playback session's own wording.</summary>
    /// <param name="codecId">The codec.</param>
    /// <returns>The message.</returns>
    internal static string DescribeMissingAudioDecoder(string codecId)
    {
        string advice = " Or set IncludeAudio to false to export the picture alone.";

        if (string.Equals(codecId, VideoCodecIds.Opus, StringComparison.OrdinalIgnoreCase))
        {
            return "The export cannot decode its sound: audio codec 'opus' has no registered decoder - reference "
                + "CodeBrix.Audio.Opus and call CodeBrixAudioOpus.Register() at start-up." + advice;
        }

        if (string.Equals(codecId, VideoCodecIds.Flac, StringComparison.OrdinalIgnoreCase))
        {
            return "The export cannot decode its sound: audio codec 'flac' has no registered decoder - a master "
                + "(Mode3) file's lossless sound needs a CodeBrix.Audio.Core whose shared output serves FLAC packets "
                + "(FlacPacketCodecFactory); update CodeBrix.Audio.Core." + advice;
        }

        if (string.Equals(codecId, VideoCodecIds.Vorbis, StringComparison.OrdinalIgnoreCase))
        {
            return "The export cannot decode its sound: audio codec 'vorbis' has no registered decoder - CodeBrix.Audio.Core "
                + "builds one in, so this one does not carry it; update CodeBrix.Audio.Core." + advice;
        }

        return "The export cannot decode its sound: audio codec '" + codecId + "' has no registered decoder - "
            + "register an IPacketCodecFactory that serves it with SharedAudioOutput.RegisterPacketCodecFactory(...)."
            + advice;
    }

    private static bool Claims(IPacketCodecFactory factory, string codecId)
    {
        IReadOnlyCollection<string> ids = factory.SupportedCodecIds;
        if (ids == null) return false;
        foreach (string id in ids)
        {
            if (string.Equals(id, codecId, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // An uncompressed Matroska track states its shape in ordinary track elements, so its decoder's descriptor is
    // built from them - as the playback session does. Every other codec's private data passes through as stored.
    private static ReadOnlyMemory<byte> DescribeCodecPrivate(MediaTrackInfo track)
    {
        if (!track.CodecPrivate.IsEmpty) return track.CodecPrivate;
        if (!string.Equals(track.CodecId, VideoCodecIds.Raw, StringComparison.OrdinalIgnoreCase)) return track.CodecPrivate;

        RawVideoDescriptor descriptor = new RawVideoDescriptor(
            track.Width,
            track.Height,
            track.BitDepth > 0 ? track.BitDepth : 8,
            track.Layout == VideoPixelLayout.Unknown ? VideoPixelLayout.I420 : track.Layout,
            track.Color);

        return descriptor.IsValid ? RawVideoFormat.CreateDescriptor(descriptor) : track.CodecPrivate;
    }
}
