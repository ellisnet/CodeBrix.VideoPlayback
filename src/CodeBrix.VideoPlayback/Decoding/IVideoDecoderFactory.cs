using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// Builds decoders for the codecs it knows. Registering one is how a decoder package makes itself available
/// to this library.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors the audio side of the family exactly, so the two seams behave the same way: a factory
/// that cannot serve a request returns null rather than throwing, and the registry moves on to the next one.
/// Throwing means "this request is mine and it is broken" - a malformed configuration record, say - and the
/// message reaches the application.
/// </para>
/// <para>Hold ONE instance and register that: the registry de-duplicates on the instance, not on the identifier.</para>
/// </remarks>
public interface IVideoDecoderFactory
{
    /// <summary>
    /// A stable name for this factory, conventionally the package that ships it.
    /// </summary>
    string FactoryId { get; }

    /// <summary>The codec identifiers this factory can build decoders for - see <see cref="VideoCodecIds" />.</summary>
    IReadOnlyCollection<string> SupportedCodecIds { get; }

    /// <summary>
    /// Where this factory sits when several can serve the same codec: HIGHER runs first.
    /// </summary>
    /// <remarks>
    /// Zero is the ordinary level. A factory that should only be used when nothing better is available takes
    /// a negative value; one that deliberately overrides a built-in takes a positive one.
    /// </remarks>
    int Priority { get; }

    /// <summary>Builds a decoder, or declines the request.</summary>
    /// <param name="codecId">The codec identifier - compare it case-insensitively.</param>
    /// <param name="codecPrivate">
    /// The container's initialisation data for the track: an <c>av1C</c> record for AV1, empty when the codec
    /// needs none.
    /// </param>
    /// <param name="options">
    /// The decoder settings, including the frame-buffer pool the decoder should write into. Never null.
    /// </param>
    /// <returns>
    /// A decoder, or null when this factory does not serve the codec - which lets the registry offer the
    /// request to the next factory.
    /// </returns>
    /// <exception cref="VideoPlaybackException">
    /// The factory recognises the codec but cannot decode this particular stream, and wants the application
    /// to see why.
    /// </exception>
    IVideoDecoder CreateDecoder(string codecId, ReadOnlyMemory<byte> codecPrivate, VideoDecoderOptions options);
}
