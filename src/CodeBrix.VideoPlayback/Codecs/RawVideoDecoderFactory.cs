using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// Supplies decoders for the uncompressed video codec.
/// </summary>
/// <remarks>
/// <para>
/// It is BUILT IN. <see cref="VideoDecoders" /> holds one of these from the moment the registry is first
/// touched, so <see cref="VideoDecoders.IsCodecSupported" /> answers true for
/// <see cref="VideoCodecIds.Raw" /> and a session plays a <c>V_UNCOMPRESSED</c> track with no registration
/// call and no codec package installed - the same bargain Vorbis audio makes. Registration is what an
/// EXTERNAL codec package does: <c>CodeBrixVideoPlaybackDav1d.Register()</c> and its like.
/// </para>
/// <para>
/// Its <see cref="Priority" /> is 0 and it serves only the uncompressed codec, so it can never shadow a
/// factory that arrives for any other codec. An application that wants to decorate or replace it registers
/// its own: for one session with <c>session.RegisterDecoderFactory(...)</c>, which is always tried first, or
/// process-wide with <see cref="VideoDecoders.Register" /> at a priority ABOVE 0 - at the same priority the
/// built-in one was in the registry first and wins the tie.
/// </para>
/// <para>
/// It remains a diagnostics and test codec rather than a distribution format: uncompressed video is
/// enormous, and a real clip wants a real codec package.
/// </para>
/// </remarks>
public sealed class RawVideoDecoderFactory : IVideoDecoderFactory
{
    /// <inheritdoc />
    public string FactoryId => "CodeBrix.VideoPlayback.RawVideo";

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedCodecIds { get; } = new[] { VideoCodecIds.Raw };

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="codecPrivate" /> must be the 24-byte descriptor
    /// <see cref="RawVideoFormat.CreateDescriptor" /> writes. A playback session builds one from a Matroska
    /// track's own elements when the container carried none.
    /// </remarks>
    public IVideoDecoder CreateDecoder(
        string codecId,
        ReadOnlyMemory<byte> codecPrivate,
        VideoDecoderOptions options)
    {
        if (!string.Equals(codecId, VideoCodecIds.Raw, StringComparison.OrdinalIgnoreCase)) return null;
        if (options == null) throw new ArgumentNullException(nameof(options));

        if (!RawVideoFormat.TryParseDescriptor(codecPrivate.Span, out RawVideoDescriptor descriptor))
        {
            throw new VideoPlaybackException(
                "An uncompressed video track needs a descriptor in its codec-private data stating its size, "
                + $"layout and bit depth; this track carried {codecPrivate.Length} bytes that are not one.");
        }

        if (options.BufferPool == null)
        {
            throw new VideoPlaybackException(
                "An uncompressed video decoder needs a frame-buffer pool; VideoDecoderOptions.BufferPool was null.");
        }

        return new RawVideoDecoder(descriptor, options.BufferPool);
    }
}
