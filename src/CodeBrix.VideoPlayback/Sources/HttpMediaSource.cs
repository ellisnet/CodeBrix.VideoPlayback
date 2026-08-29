using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBrix.VideoPlayback.Sources;

/// <summary>
/// A source that plays a file over HTTP or HTTPS, using byte-range requests to seek when the server allows
/// them and falling back to a plain progressive download when it does not.
/// </summary>
/// <remarks>
/// <para>
/// Two behaviours, chosen by what the server says when the source is created:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Range requests supported</b> (the server answers a probe with <c>206 Partial Content</c>, or
///     advertises <c>Accept-Ranges: bytes</c>): the source seeks freely. Reads are served from a read-ahead
///     window so that a demultiplexer walking a file does not make one request per element, and a single
///     large read - fetching a cues element from the end of a file, for instance - becomes a single request.
///   </description></item>
///   <item><description>
///     <b>Range requests not supported</b>: one response body is read from beginning to end.
///     <see cref="CanSeek" /> is false, so the container reader demultiplexes forwards and the session
///     reports that seeking is not available for this source.
///   </description></item>
/// </list>
/// <para>
/// A WebM file whose cues sit at the END is still fast to open over a range-capable server: the reader asks
/// for the tail once and gets the whole index in one request.
/// </para>
/// <para>
/// The calls are synchronous because a demultiplexer runs on its own thread and wants bytes, not tasks. Use
/// <see cref="CreateAsync" /> to open the source without blocking the thread that asks for it.
/// </para>
/// </remarks>
public sealed class HttpMediaSource : IMediaSource
{
    private const int WindowSize = 256 * 1024;

    private readonly HttpClient client;
    private readonly bool leaveClientOpen;
    private readonly Uri uri;
    private readonly byte[] window = new byte[WindowSize];

    private long windowOffset = -1;
    private int windowLength;
    private long position;
    private Stream progressiveStream;
    private long progressiveStreamPosition;
    private bool disposed;

    private HttpMediaSource(Uri uri, HttpClient client, bool leaveClientOpen, bool supportsRangeRequests, long length)
    {
        this.uri = uri;
        this.client = client;
        this.leaveClientOpen = leaveClientOpen;
        SupportsRangeRequests = supportsRangeRequests;
        Length = length;
        Name = uri.ToString();
    }

    /// <summary>Opens a source over an HTTP or HTTPS address, probing the server for range support.</summary>
    /// <param name="uri">The address of the media file.</param>
    /// <param name="client">An <see cref="HttpClient" /> to use, or null to create one for this source.</param>
    /// <param name="leaveClientOpen">
    /// True to leave <paramref name="client" /> alive when the source is disposed. Ignored when the source
    /// created the client itself, which it always disposes.
    /// </param>
    /// <returns>A source ready to be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri" /> is null.</exception>
    /// <exception cref="ArgumentException">The address is not HTTP or HTTPS.</exception>
    /// <exception cref="VideoPlaybackException">The server refused the request.</exception>
    public static HttpMediaSource Create(Uri uri, HttpClient client = null, bool leaveClientOpen = false) =>
        CreateAsync(uri, client, leaveClientOpen, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Opens a source over an HTTP or HTTPS address without blocking, probing for range support.</summary>
    /// <param name="uri">The address of the media file.</param>
    /// <param name="client">An <see cref="HttpClient" /> to use, or null to create one for this source.</param>
    /// <param name="leaveClientOpen">True to leave <paramref name="client" /> alive when the source is disposed.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task producing a source ready to be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri" /> is null.</exception>
    /// <exception cref="ArgumentException">The address is not HTTP or HTTPS.</exception>
    /// <exception cref="VideoPlaybackException">The server refused the request.</exception>
    public static async Task<HttpMediaSource> CreateAsync(
        Uri uri,
        HttpClient client = null,
        bool leaveClientOpen = false,
        CancellationToken cancellationToken = default)
    {
        if (uri == null) throw new ArgumentNullException(nameof(uri));
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"'{uri}' is not an HTTP or HTTPS address; an {nameof(HttpMediaSource)} can only read those.",
                nameof(uri));
        }

        HttpClient effectiveClient = client ?? new HttpClient();
        bool ownsClient = client == null;

        try
        {
            using HttpRequestMessage probe = new HttpRequestMessage(HttpMethod.Get, uri);
            probe.Headers.Range = new RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await effectiveClient
                .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new VideoPlaybackException(
                    $"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for '{uri}', so there "
                    + "is nothing to play.");
            }

            bool supportsRanges = response.StatusCode == HttpStatusCode.PartialContent;
            long length = -1;

            if (supportsRanges && response.Content.Headers.ContentRange is ContentRangeHeaderValue contentRange
                && contentRange.HasLength && contentRange.Length.HasValue)
            {
                length = contentRange.Length.Value;
            }
            else if (!supportsRanges)
            {
                if (response.Headers.AcceptRanges.Contains("bytes")) supportsRanges = true;
                if (response.Content.Headers.ContentLength.HasValue) length = response.Content.Headers.ContentLength.Value;
            }

            return new HttpMediaSource(uri, effectiveClient, leaveClientOpen && !ownsClient, supportsRanges, length);
        }
        catch (HttpRequestException ex)
        {
            if (ownsClient) effectiveClient.Dispose();
            throw new VideoPlaybackException($"Could not reach '{uri}': {ex.Message}", ex);
        }
        catch
        {
            if (ownsClient) effectiveClient.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>True when the server answered the probe with a partial response or advertised byte ranges.</summary>
    public bool SupportsRangeRequests { get; }

    /// <inheritdoc />
    public bool CanSeek => SupportsRangeRequests;

    /// <inheritdoc />
    public bool IsLengthKnown => Length >= 0;

    /// <inheritdoc />
    public long Length { get; }

    /// <summary>How many HTTP requests this source has made. Useful for proving that a read-ahead window works.</summary>
    public int RequestCount { get; private set; }

    /// <inheritdoc />
    public long Position
    {
        get => position;
        set
        {
            ThrowIfDisposed();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The position cannot be negative.");

            if (!SupportsRangeRequests)
            {
                if (value == position) return;
                throw new NotSupportedException(
                    $"'{Name}' cannot seek: the server did not offer byte ranges, so the file can only be read "
                    + "from beginning to end.");
            }

            position = value;
        }
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0) return 0;

        if (!SupportsRangeRequests)
        {
            int progressive = ReadProgressive(buffer);
            position += progressive;
            return progressive;
        }

        int read = ReadAt(position, buffer);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        if (!SupportsRangeRequests)
        {
            throw new NotSupportedException(
                $"'{Name}' cannot read at an offset: the server did not offer byte ranges.");
        }

        if (buffer.Length == 0) return 0;
        if (IsLengthKnown && offset >= Length) return 0;

        if (buffer.Length >= WindowSize) return Fetch(offset, buffer);

        if (windowOffset < 0 || offset < windowOffset || offset >= windowOffset + windowLength)
        {
            windowLength = Fetch(offset, window.AsSpan());
            windowOffset = windowLength > 0 ? offset : -1;
            if (windowLength <= 0) return 0;
        }

        int available = (int)(windowOffset + windowLength - offset);
        int count = Math.Min(available, buffer.Length);
        window.AsSpan((int)(offset - windowOffset), count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        progressiveStream?.Dispose();
        progressiveStream = null;
        if (!leaveClientOpen) client.Dispose();
    }

    private int Fetch(long offset, Span<byte> destination)
    {
        long last = offset + destination.Length - 1;
        if (IsLengthKnown && last > Length - 1) last = Length - 1;
        if (last < offset) return 0;

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(offset, last);

        RequestCount++;

        using HttpResponseMessage response = client
            .Send(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0;

        if (!response.IsSuccessStatusCode)
        {
            throw new VideoPlaybackException(
                $"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for bytes {offset}-{last} "
                + $"of '{Name}'.");
        }

        using Stream body = response.Content.ReadAsStream();

        int wanted = (int)(last - offset + 1);
        int total = 0;
        while (total < wanted)
        {
            int read = body.Read(destination.Slice(total, wanted - total));
            if (read <= 0) break;
            total += read;
        }

        return total;
    }

    private int ReadProgressive(Span<byte> buffer)
    {
        if (progressiveStream == null)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            RequestCount++;

            HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string reason = response.ReasonPhrase;
                response.Dispose();
                throw new VideoPlaybackException($"The server answered {status} {reason} for '{Name}'.");
            }

            progressiveStream = response.Content.ReadAsStream();
            progressiveStreamPosition = 0;
        }

        if (position != progressiveStreamPosition)
        {
            throw new NotSupportedException(
                $"'{Name}' is being read progressively and is at byte {progressiveStreamPosition}; it cannot jump "
                + $"to byte {position} because the server did not offer byte ranges.");
        }

        int read = progressiveStream.Read(buffer);
        if (read > 0) progressiveStreamPosition += read;
        return read;
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(HttpMediaSource));
    }
}
