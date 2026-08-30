using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.RawCodec;
using CodeBrix.VideoPlayback.Sources;

namespace CodeBrix.VideoPlayback.Tools;

/// <summary>
/// The <c>cbvdecode</c> verb: decodes every video frame in a file, prints a hash of each one and the timing
/// statistics, and optionally writes the decoded planes out as a Y4M file.
/// </summary>
/// <remarks>
/// <para>
/// It needs no display and no audio device, which is what makes it the way to check that a build works on a
/// machine that has neither - a small board, a frame-buffer target, a build agent. The per-frame hashes make
/// two machines' output directly comparable.
/// </para>
/// <para>
/// Uncompressed video decodes with nothing installed. A coded stream needs a decoder package registered; with
/// none, the verb prints the same message a playback session would, naming the package to add.
/// </para>
/// </remarks>
public static class CbvDecodeCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="args">The file to decode, plus any switches.</param>
    /// <returns>0 when every frame decoded, 1 when the file could not be decoded.</returns>
    public static int Run(string[] args)
    {
        string path = null;
        string y4mPath = null;
        long frameLimit = long.MaxValue;
        bool quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--headless":
                    break;

                case "--quiet":
                    quiet = true;
                    break;

                case "--y4m":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("cbvdecode: --y4m needs a file to write.");
                        return 2;
                    }

                    y4mPath = args[++i];
                    break;

                case "--frames":
                    if (i + 1 >= args.Length || !long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out frameLimit))
                    {
                        Console.Error.WriteLine("cbvdecode: --frames needs a number.");
                        return 2;
                    }

                    i++;
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"cbvdecode: '{arg}' is not a switch this verb knows.");
                        return 2;
                    }

                    path = arg;
                    break;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("cbvdecode: a file to decode is required.");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"cbvdecode: there is no file at '{path}'.");
            return 1;
        }

        using IMediaSource source = new FileMediaSource(path);
        using IMediaContainerReader reader = OpenReader(source);

        MediaTrackInfo videoTrack = null;
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind != MediaTrackKind.Video) continue;
            videoTrack = track;
            break;
        }

        if (videoTrack == null)
        {
            Console.Error.WriteLine($"cbvdecode: '{path}' carries no video track.");
            return 1;
        }

        using PinnedFrameBufferPool pool = new PinnedFrameBufferPool();
        VideoDecoderOptions decoderOptions = new VideoDecoderOptions { BufferPool = pool };

        IVideoDecoder decoder = CreateDecoder(videoTrack, decoderOptions);
        if (decoder == null)
        {
            Console.Error.WriteLine(
                $"cbvdecode: video codec '{videoTrack.CodecId}' has no registered decoder. Uncompressed video "
                + "decodes with nothing installed; a coded stream needs a decoder package registered.");
            return 1;
        }

        using (decoder)
        {
            Stream y4m = null;
            try
            {
                if (y4mPath != null) y4m = new FileStream(y4mPath, FileMode.Create, FileAccess.Write, FileShare.None);
                return Decode(reader, decoder, videoTrack, y4m, frameLimit, quiet);
            }
            finally
            {
                y4m?.Dispose();
            }
        }
    }

    // One sniff, one implementation: the library picks the reader the header calls for.
    private static IMediaContainerReader OpenReader(IMediaSource source) =>
        MediaContainers.Open(source, true);

    private static IVideoDecoder CreateDecoder(MediaTrackInfo track, VideoDecoderOptions options)
    {
        ReadOnlyMemory<byte> codecPrivate = track.CodecPrivate;

        if (string.Equals(track.CodecId, VideoCodecIds.Raw, StringComparison.OrdinalIgnoreCase)
            && codecPrivate.IsEmpty)
        {
            Codecs.RawVideoDescriptor descriptor = new Codecs.RawVideoDescriptor(
                track.Width,
                track.Height,
                track.BitDepth > 0 ? track.BitDepth : 8,
                track.Layout == VideoPixelLayout.Unknown ? VideoPixelLayout.I420 : track.Layout,
                track.Color);

            if (descriptor.IsValid) codecPrivate = Codecs.RawVideoFormat.CreateDescriptor(descriptor);
        }

        IVideoDecoder decoder = new RawVideoDecoderFactory().CreateDecoder(track.CodecId, codecPrivate, options);
        return decoder ?? VideoDecoders.TryCreateDecoder(track.CodecId, codecPrivate, options);
    }

    private static int Decode(
        IMediaContainerReader reader,
        IVideoDecoder decoder,
        MediaTrackInfo videoTrack,
        Stream y4m,
        long frameLimit,
        bool quiet)
    {
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch step = new Stopwatch();

        long frames = 0;
        long packets = 0;
        double slowestMilliseconds = 0;
        double decodeMilliseconds = 0;
        bool wroteY4mHeader = false;

        using IncrementalHash streamHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        while (frames < frameLimit && reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId != videoTrack.Id) continue;

            packets++;
            step.Restart();
            decoder.SendPacket(new VideoPacket(packet.Data, packet.Timestamp, packet.IsKeyFrame, packet.Duration, packets - 1));

            while (decoder.TryReceiveFrame(out VideoFrame frame))
            {
                step.Stop();
                double elapsed = step.Elapsed.TotalMilliseconds;
                decodeMilliseconds += elapsed;
                if (elapsed > slowestMilliseconds) slowestMilliseconds = elapsed;

                using (frame)
                {
                    string hash = HashFrame(frame, streamHash);
                    if (!quiet)
                    {
                        Console.WriteLine(
                            $"frame {frames,6}  {frame.Timestamp.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture)}  "
                            + $"{frame.Width}x{frame.Height} {frame.Layout} {frame.BitDepth}-bit"
                            + $"{(frame.IsKeyFrame ? " key" : "    ")}  {hash}  {elapsed.ToString("0.000", CultureInfo.InvariantCulture)} ms");
                    }

                    if (y4m != null)
                    {
                        if (!wroteY4mHeader)
                        {
                            WriteY4mHeader(y4m, frame, videoTrack);
                            wroteY4mHeader = true;
                        }

                        WriteY4mFrame(y4m, frame);
                    }
                }

                frames++;
                step.Restart();
            }

            step.Stop();
        }

        decoder.Drain();

        while (decoder.TryReceiveFrame(out VideoFrame frame))
        {
            using (frame)
            {
                string hash = HashFrame(frame, streamHash);
                if (!quiet)
                {
                    Console.WriteLine(
                        $"frame {frames,6}  {frame.Timestamp.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture)}  "
                        + $"{frame.Width}x{frame.Height} {frame.Layout} {frame.BitDepth}-bit"
                        + $"{(frame.IsKeyFrame ? " key" : "    ")}  {hash}  (drained)");
                }

                if (y4m != null)
                {
                    if (!wroteY4mHeader)
                    {
                        WriteY4mHeader(y4m, frame, videoTrack);
                        wroteY4mHeader = true;
                    }

                    WriteY4mFrame(y4m, frame);
                }
            }

            frames++;
        }

        total.Stop();

        Console.WriteLine();
        Console.WriteLine($"packets           {packets}");
        Console.WriteLine($"frames            {frames}");
        Console.WriteLine($"wall time         {total.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        Console.WriteLine($"decode time       {decodeMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms");

        if (frames > 0)
        {
            double perFrame = decodeMilliseconds / frames;
            Console.WriteLine($"per frame         {perFrame.ToString("0.000", CultureInfo.InvariantCulture)} ms mean, {slowestMilliseconds.ToString("0.000", CultureInfo.InvariantCulture)} ms slowest");
            if (perFrame > 0) Console.WriteLine($"throughput        {(1000.0 / perFrame).ToString("0.0", CultureInfo.InvariantCulture)} frames per second");
        }

        Console.WriteLine($"stream hash       {Convert.ToHexString(streamHash.GetHashAndReset()).ToLowerInvariant()}");
        return frames > 0 ? 0 : 1;
    }

    private static string HashFrame(VideoFrame frame, IncrementalHash streamHash)
    {
        using IncrementalHash frameHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int plane = 0; plane < 3; plane++)
        {
            VideoFramePlane samples = frame.Buffer.GetPlane(plane);
            if (samples.IsEmpty) continue;

            for (int row = 0; row < samples.Height; row++)
            {
                ReadOnlySpan<byte> bytes = samples.GetRowBytes(row);
                frameHash.AppendData(bytes);
                streamHash.AppendData(bytes);
            }
        }

        return Convert.ToHexString(frameHash.GetHashAndReset()).ToLowerInvariant().Substring(0, 16);
    }

    private static void WriteY4mHeader(Stream stream, VideoFrame frame, MediaTrackInfo track)
    {
        string colourSpace = frame.Layout switch
        {
            VideoPixelLayout.I420 => frame.BitDepth == 8 ? "420" : $"420p{frame.BitDepth}",
            VideoPixelLayout.I422 => frame.BitDepth == 8 ? "422" : $"422p{frame.BitDepth}",
            VideoPixelLayout.I444 => frame.BitDepth == 8 ? "444" : $"444p{frame.BitDepth}",
            _ => "mono",
        };

        string rate = "25:1";
        if (track.DefaultDuration > TimeSpan.Zero)
        {
            long numerator = TimeSpan.TicksPerSecond;
            long denominator = track.DefaultDuration.Ticks;
            long divisor = GreatestCommonDivisor(numerator, denominator);
            rate = $"{numerator / divisor}:{denominator / divisor}";
        }

        string header = $"YUV4MPEG2 W{frame.Width} H{frame.Height} F{rate} Ip A1:1 C{colourSpace}\n";
        byte[] bytes = Encoding.ASCII.GetBytes(header);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static long GreatestCommonDivisor(long a, long b)
    {
        while (b != 0)
        {
            long remainder = a % b;
            a = b;
            b = remainder;
        }

        return a == 0 ? 1 : a;
    }

    private static void WriteY4mFrame(Stream stream, VideoFrame frame)
    {
        byte[] marker = Encoding.ASCII.GetBytes("FRAME\n");
        stream.Write(marker, 0, marker.Length);

        for (int plane = 0; plane < 3; plane++)
        {
            VideoFramePlane samples = frame.Buffer.GetPlane(plane);
            if (samples.IsEmpty) continue;

            for (int row = 0; row < samples.Height; row++)
            {
                stream.Write(samples.GetRowBytes(row));
            }
        }
    }
}
