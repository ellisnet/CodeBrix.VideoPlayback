using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks HTTP playback against a server running inside the test process - once with byte ranges and once
/// without, which are the two behaviours a real server offers and the two code paths the source has.
/// </summary>
public class HttpMediaSourceTests
{
    [Fact]
    public void Create_uses_ranges_when_the_server_offers_them()
    {
        //Arrange
        byte[] content = MakeContent(64 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: true);

        //Act
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);

        //Assert
        source.SupportsRangeRequests.Should().BeTrue();
        source.CanSeek.Should().BeTrue();
        source.IsLengthKnown.Should().BeTrue();
        source.Length.Should().Be(content.Length);
    }

    [Fact]
    public void ReadAt_fetches_exactly_the_bytes_asked_for()
    {
        //Arrange
        byte[] content = MakeContent(64 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: true);
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);
        byte[] buffer = new byte[256];

        //Act
        source.ReadExactlyAt(40_000, buffer, "a block in the middle");

        //Assert
        buffer.Should().Equal(content.AsSpan(40_000, 256).ToArray());
    }

    [Fact]
    public void ReadAt_near_the_end_returns_the_tail_in_one_request()
    {
        //Arrange
        byte[] content = MakeContent(64 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: true);
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);
        byte[] buffer = new byte[512];
        int requestsBefore = source.RequestCount;

        //Act
        source.ReadExactlyAt(content.Length - 512, buffer, "the tail");

        //Assert
        buffer.Should().Equal(content.AsSpan(content.Length - 512, 512).ToArray());
        (source.RequestCount - requestsBefore).Should().Be(1);
    }

    [Fact]
    public void Read_serves_small_reads_out_of_one_window()
    {
        //Arrange
        byte[] content = MakeContent(64 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: true);
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);
        int requestsBefore = source.RequestCount;
        byte[] buffer = new byte[16];

        //Act
        for (int i = 0; i < 200; i++) source.ReadExactly(buffer, "a small read");

        //Assert
        source.Position.Should().Be(200 * 16);
        (source.RequestCount - requestsBefore).Should().Be(1);
    }

    [Fact]
    public void Create_falls_back_to_a_progressive_download_when_the_server_has_no_ranges()
    {
        //Arrange
        byte[] content = MakeContent(8 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: false);

        //Act
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);
        byte[] head = new byte[64];
        source.ReadExactly(head, "the head");
        Action seek = () => source.Position = 4096;

        //Assert
        source.SupportsRangeRequests.Should().BeFalse();
        source.CanSeek.Should().BeFalse();
        head.Should().Equal(content.AsSpan(0, 64).ToArray());
        seek.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Read_over_a_progressive_download_reaches_the_end_of_the_file()
    {
        //Arrange
        byte[] content = MakeContent(8 * 1024);
        using TestServer server = TestServer.Start(content, supportsRanges: false);
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);

        //Act
        byte[] all = new byte[content.Length];
        int read = source.ReadAtLeast(all);

        //Assert
        read.Should().Be(content.Length);
        all.Should().Equal(content);
    }

    [Fact]
    public void Create_reports_the_status_when_the_server_refuses()
    {
        //Arrange
        using TestServer server = TestServer.Start(Array.Empty<byte>(), supportsRanges: true, statusCode: 404);

        //Act
        Action act = () => HttpMediaSource.Create(server.Uri);

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*404*");
    }

    [Fact]
    public void A_webm_file_with_its_cues_at_the_end_plays_over_a_range_capable_server()
    {
        //Arrange
        byte[] content = File.ReadAllBytes(TestAssets.Path("av1-opus-cues-at-end.webm"));
        using TestServer server = TestServer.Start(content, supportsRanges: true);
        using HttpMediaSource source = HttpMediaSource.Create(server.Uri);

        //Act
        using MatroskaReader reader = new MatroskaReader(source, true);
        int packets = 0;
        while (reader.TryReadPacket(out MediaPacket _)) packets++;

        //Assert
        reader.Cues.Count.Should().BeGreaterThan(0);
        reader.CuesPrecedeFirstCluster.Should().BeFalse();
        packets.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_webm_file_reads_identically_over_http_and_from_disk()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");
        byte[] content = File.ReadAllBytes(path);
        using TestServer server = TestServer.Start(content, supportsRanges: true);

        //Act
        int fromDisk = CountPackets(new FileMediaSource(path));
        int overHttp = CountPackets(HttpMediaSource.Create(server.Uri));

        //Assert
        overHttp.Should().Be(fromDisk);
    }

    private static int CountPackets(IMediaSource source)
    {
        using MatroskaReader reader = new MatroskaReader(source);
        int packets = 0;
        while (reader.TryReadPacket(out MediaPacket _)) packets++;
        return packets;
    }

    private static byte[] MakeContent(int length)
    {
        byte[] content = new byte[length];
        for (int i = 0; i < length; i++) content[i] = (byte)((i * 31) ^ (i >> 8));
        return content;
    }

    /// <summary>
    /// A one-file web server inside the test process, which can be told whether to honour byte ranges.
    /// </summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener listener;
        private readonly byte[] content;
        private readonly bool supportsRanges;
        private readonly int statusCode;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Task loop;

        private TestServer(HttpListener listener, Uri uri, byte[] content, bool supportsRanges, int statusCode)
        {
            this.listener = listener;
            this.content = content;
            this.supportsRanges = supportsRanges;
            this.statusCode = statusCode;
            Uri = uri;
            loop = Task.Run(Serve);
        }

        internal Uri Uri { get; }

        internal static TestServer Start(byte[] content, bool supportsRanges, int statusCode = 200)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int port = Random.Shared.Next(20000, 45000);
                string prefix = $"http://127.0.0.1:{port}/media/";
                HttpListener listener = new HttpListener();
                listener.Prefixes.Add(prefix);

                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    continue;
                }

                return new TestServer(listener, new Uri(prefix + "clip.bin"), content, supportsRanges, statusCode);
            }

            throw new InvalidOperationException("No free local port could be found for the test server.");
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }

            try
            {
                loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loop ends by the listener being closed underneath it; that is not a failure.
            }

            cancellation.Dispose();
        }

        private void Serve()
        {
            while (!cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    Respond(context);
                }
                catch (HttpListenerException)
                {
                    // The client went away mid-response; nothing to do.
                }
                catch (IOException)
                {
                    // The same.
                }
            }
        }

        private void Respond(HttpListenerContext context)
        {
            HttpListenerResponse response = context.Response;

            if (statusCode != 200)
            {
                response.StatusCode = statusCode;
                response.Close();
                return;
            }

            string range = context.Request.Headers["Range"];

            if (!supportsRanges || string.IsNullOrEmpty(range))
            {
                if (supportsRanges) response.Headers.Add("Accept-Ranges", "bytes");
                response.StatusCode = 200;
                response.ContentLength64 = content.Length;
                response.OutputStream.Write(content, 0, content.Length);
                response.Close();
                return;
            }

            ParseRange(range, content.Length, out long from, out long to);

            response.StatusCode = 206;
            response.Headers.Add("Accept-Ranges", "bytes");
            response.Headers.Add("Content-Range", $"bytes {from}-{to}/{content.Length}");
            response.ContentLength64 = to - from + 1;
            response.OutputStream.Write(content, (int)from, (int)(to - from + 1));
            response.Close();
        }

        private static void ParseRange(string header, int length, out long from, out long to)
        {
            from = 0;
            to = length - 1;

            int equals = header.IndexOf('=');
            if (equals < 0) return;

            string spec = header.Substring(equals + 1);
            int dash = spec.IndexOf('-');
            if (dash < 0) return;

            string start = spec.Substring(0, dash);
            string end = spec.Substring(dash + 1);

            if (start.Length > 0) long.TryParse(start, out from);
            if (end.Length > 0 && long.TryParse(end, out long parsedEnd)) to = Math.Min(parsedEnd, length - 1);
        }
    }
}
