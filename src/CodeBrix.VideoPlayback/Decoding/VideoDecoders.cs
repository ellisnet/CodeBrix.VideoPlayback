using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeBrix.VideoPlayback.Decoding;

/// <summary>
/// The process-wide registry of video decoder factories - the front door a decoder package knocks on.
/// </summary>
/// <remarks>
/// <para>
/// This library ships no video decoder at all. A decoder arrives as a separate package, because a decoder
/// carries a licence and a set of native binaries that not every application wants, and it makes itself
/// available with one call at start-up:
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
    private static readonly List<IVideoDecoderFactory> Factories = new List<IVideoDecoderFactory>();

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

    /// <summary>Clears the registry. Intended for tests; an application has no reason to call it.</summary>
    public static void Clear()
    {
        lock (Gate) Factories.Clear();
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
