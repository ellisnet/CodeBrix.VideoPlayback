using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.VideoPlayback.Codecs;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The process-wide registry of video decoder factories - the front door a decoder package knocks on.
/// </summary>
/// <remarks>
/// <para>
/// One decoder is BUILT IN: <see cref="RawVideoDecoderFactory" />, for uncompressed video, is in the registry
/// from the moment it is first touched. Nothing has to be called for it, so a <c>V_UNCOMPRESSED</c> track
/// plays with no codec package installed at all - the same bargain Vorbis audio makes. It has priority 0 and
/// serves only <see cref="VideoCodecIds.Raw" />, so it never shadows a factory that arrives for another
/// codec.
/// </para>
/// <para>
/// Every CODED format arrives as a separate package, because a decoder carries a licence and a set of native
/// binaries that not every application wants, and it makes itself available with one call at start-up:
/// </para>
/// <code>
/// VideoDecoders.Register(SomeDecoderPackage.Factory);
/// </code>
/// <para>
/// Registration lasts for the process and is idempotent on the INSTANCE, so a start-up path that runs twice
/// registers once. When several factories serve one codec, the highest
/// <see cref="IVideoDecoderFactory.Priority" /> is tried first and registration order breaks ties.
/// </para>
/// <para>
/// A single playback session can override the process-wide list for itself - see
/// <see cref="CodeBrix.VideoPlayback.VideoPlaybackSession.RegisterDecoderFactory" /> - which is how a test, or an
/// application playing two things at once with different needs, avoids disturbing everybody else.
/// </para>
/// <para>Every member is safe to call from any thread.</para>
/// </remarks>
public static class VideoDecoders
{
    private static readonly object Gate = new object();

    // The built-in uncompressed-video factory. It is seeded into the list below by the static initialiser -
    // which the runtime runs exactly once, before any member of this class can be reached - so it is in the
    // registry with no registration call, exactly once, however many threads arrive at the same moment.
    private static readonly IVideoDecoderFactory BuiltInRawVideo = new RawVideoDecoderFactory();

    private static readonly List<IVideoDecoderFactory> Factories =
        new List<IVideoDecoderFactory> { BuiltInRawVideo };

    /// <summary>The built-in uncompressed-video factory - the one instance the registry starts life with.</summary>
    /// <remarks>
    /// It is here so that the built-in decoder can be named without hunting for it in
    /// <see cref="RegisteredFactories" /> and guessing which entry it is. It is an ORDINARY entry: pass it to
    /// <see cref="Unregister" /> to take uncompressed video out of a process that must not have it, and
    /// <see cref="Clear" /> puts this same instance back. It is the same object every time, so
    /// <c>ReferenceEquals</c> against it is the reliable way to ask "is that the built-in one?".
    /// </remarks>
    public static IVideoDecoderFactory BuiltInRawVideoFactory => BuiltInRawVideo;

    /// <summary>Adds a factory to the registry, or does nothing if that same instance is already registered.</summary>
    /// <param name="factory">The factory to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    public static void Register(IVideoDecoderFactory factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));

        lock (Gate)
        {
            foreach (IVideoDecoderFactory registered in Factories)
            {
                if (ReferenceEquals(registered, factory)) return;
            }

            Factories.Add(factory);
        }
    }

    /// <summary>Removes a factory from the registry.</summary>
    /// <param name="factory">The factory instance that was registered.</param>
    /// <returns>True when it was registered and has now been removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <remarks>
    /// The built-in uncompressed-video factory is an ordinary entry and can be removed the same way - take it
    /// from <see cref="RegisteredFactories" /> to get the instance. <see cref="Clear" /> puts it back.
    /// </remarks>
    public static bool Unregister(IVideoDecoderFactory factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));

        lock (Gate)
        {
            for (int i = 0; i < Factories.Count; i++)
            {
                if (!ReferenceEquals(Factories[i], factory)) continue;
                Factories.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>The registered factories, in the order they would be asked: highest priority first.</summary>
    public static IReadOnlyList<IVideoDecoderFactory> RegisteredFactories
    {
        get
        {
            lock (Gate) return OrderNoLock(Factories);
        }
    }

    /// <summary>The codec identifiers at least one registered factory claims to serve.</summary>
    public static IReadOnlyCollection<string> SupportedCodecIds
    {
        get
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                foreach (IVideoDecoderFactory factory in Factories)
                {
                    IReadOnlyCollection<string> supported = factory.SupportedCodecIds;
                    if (supported == null) continue;
                    foreach (string id in supported)
                    {
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                }
            }

            return ids;
        }
    }

    /// <summary>Reports whether a decoder could be built for a codec.</summary>
    /// <param name="codecId">The codec identifier.</param>
    /// <returns>True when a registered factory claims the codec.</returns>
    public static bool IsCodecSupported(string codecId)
    {
        if (string.IsNullOrEmpty(codecId)) return false;

        lock (Gate)
        {
            foreach (IVideoDecoderFactory factory in Factories)
            {
                if (Serves(factory, codecId)) return true;
            }
        }

        return false;
    }

    /// <summary>Asks each registered factory in turn for a decoder, and returns the first one produced.</summary>
    /// <param name="codecId">The codec identifier.</param>
    /// <param name="codecPrivate">The container's initialisation data for the track.</param>
    /// <param name="options">The decoder settings, including the frame-buffer pool.</param>
    /// <returns>A decoder, or null when no registered factory served the codec.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <remarks>
    /// A factory that throws is not treated as a failure of the whole search: the search moves on, and the
    /// exception is reported only if nothing else can serve the codec either.
    /// </remarks>
    public static IVideoDecoder TryCreateDecoder(
        string codecId,
        ReadOnlyMemory<byte> codecPrivate,
        VideoDecoderOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrEmpty(codecId)) return null;

        IReadOnlyList<IVideoDecoderFactory> ordered;
        lock (Gate) ordered = OrderNoLock(Factories);

        return TryCreateDecoder(ordered, codecId, codecPrivate, options);
    }

    /// <summary>
    /// Removes every factory that was registered, and puts the built-in uncompressed-video factory back.
    /// Intended for tests; an application has no reason to call it.
    /// </summary>
    /// <remarks>
    /// The built-in factory is part of what this library IS rather than something an application added, so a
    /// cleared registry still serves <see cref="VideoCodecIds.Raw" />. That also means a test which clears
    /// the registry cannot leave another test - or another thread - without the built-in codec.
    /// </remarks>
    public static void Clear()
    {
        lock (Gate)
        {
            Factories.Clear();
            Factories.Add(BuiltInRawVideo);
        }
    }

    internal static IVideoDecoder TryCreateDecoder(
        IReadOnlyList<IVideoDecoderFactory> factories,
        string codecId,
        ReadOnlyMemory<byte> codecPrivate,
        VideoDecoderOptions options)
    {
        Exception firstFailure = null;

        foreach (IVideoDecoderFactory factory in factories)
        {
            if (!Serves(factory, codecId)) continue;

            IVideoDecoder decoder;
            try
            {
                decoder = factory.CreateDecoder(codecId, codecPrivate, options);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                continue;
            }

            if (decoder != null) return decoder;
        }

        if (firstFailure != null)
        {
            throw new VideoPlaybackException(
                $"No decoder could be created for video codec '{codecId}': {firstFailure.Message}",
                firstFailure);
        }

        return null;
    }

    internal static IReadOnlyList<IVideoDecoderFactory> Snapshot()
    {
        lock (Gate) return OrderNoLock(Factories);
    }

    private static bool Serves(IVideoDecoderFactory factory, string codecId)
    {
        IReadOnlyCollection<string> supported = factory.SupportedCodecIds;
        if (supported == null) return false;

        foreach (string id in supported)
        {
            if (string.Equals(id, codecId, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static IReadOnlyList<IVideoDecoderFactory> OrderNoLock(List<IVideoDecoderFactory> source) =>
        source
            .Select((factory, index) => new { factory, index })
            .OrderByDescending(entry => entry.factory.Priority)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.factory)
            .ToArray();
}
