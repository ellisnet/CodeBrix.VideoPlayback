using System;
using System.Collections.Generic;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Codecs;
using CodeBrix.VideoPlayback.Decoding;

namespace CodeBrix.VideoPlayback.RawCodec;

/// <summary>
/// Supplies decoders for the uncompressed video codec.
/// </summary>
/// <remarks>
/// Register it with a session - <c>session.RegisterDecoderFactory(new RawVideoDecoderFactory())</c> - or
/// process-wide with <see cref="VideoDecoders.Register" />. It is not part of the shipped package: its source
/// lives with the tests and is linked into the tools project.
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
