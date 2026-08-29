using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Playback;
using CodeBrix.VideoPlayback.RawCodec;
using CodeBrix.VideoPlayback.Skia;
using CodeBrix.VideoPlayback.Skia.Rendering;
using SkiaSharp;

namespace CodeBrix.VideoPlayback.ConsumerShape;

/// <summary>
/// The smallest honest application: it opens a bespoke <c>.cbv</c> file with Vorbis audio, plays it right
/// through with no display, draws every frame into an off-screen surface, writes one picture of what a view
/// would have shown, and exits.
/// </summary>
/// <remarks>
/// <para>
/// It exists to be PUBLISHED and then looked at. The playback library depends on CodeBrix.Audio and nothing
/// else; the presenter depends on the playback library and plain SkiaSharp. So the publish output of an
/// application that plays Vorbis-audio files contains no Opus binary, no codec binary and no windowing
/// toolkit - which is the promise the whole family is built around, and is checked by looking at the
/// published folder rather than by taking anybody's word for it.
/// </para>
/// <para>
/// It is also a worked example of the consumer shape: open a session, attach a presenter to its mailbox,
/// draw from a paint loop, and stop when the file ends.
/// </para>
/// </remarks>
public static class Program
{
    private const int ViewWidth = 640;
    private const int ViewHeight = 360;

    /// <summary>Runs the sample.</summary>
    /// <param name="args">
    /// The path to write the snapshot to, and optionally the media file to play. With no arguments the
    /// snapshot goes to "consumer-shape.png" beside the executable and the committed sample clip is played.
    /// </param>
    /// <returns>0 when the clip played and the snapshot was written; 1 when it could not be.</returns>
    public static int Main(string[] args)
    {
        string snapshotPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "consumer-shape.png");

        string mediaPath = args.Length > 1 ? args[1] : FindSampleClip();

        if (mediaPath == null || !File.Exists(mediaPath))
        {
            Console.Error.WriteLine(
                "consumer-shape: no media file to play. Pass one on the command line, or run "
                + "tests/assets/generate-cbv-assets.sh to build the committed sample clip.");
            return 1;
        }

        Console.WriteLine($"playing  {mediaPath}");
        Console.WriteLine($"snapshot {snapshotPath}");

        // 1. A session. Audio plays through CodeBrix.Audio, which has Vorbis built in - no extra package.
        using VideoPlaybackSession session = new VideoPlaybackSession();

        // 2. A decoder. A real application references a decoder package and calls its Register method; this
        //    sample links the repository's uncompressed test codec so that it needs nothing installed.
        session.RegisterDecoderFactory(new RawVideoDecoderFactory());

        // 3. A presenter, on the processor render path because there is no graphics context in a console.
        using SkiaVideoPresenter presenter = new SkiaVideoPresenter { RenderPath = VideoRenderPath.Cpu };
        presenter.Attach(session.Presenter);

        // 4. The "view": an application would draw into the canvas its window gives it. Here it is a
        //    surface in memory, which is the same call.
        using SKSurface view = SKSurface.Create(
            new SKImageInfo(ViewWidth, ViewHeight, SKColorType.Bgra8888, SKAlphaType.Premul));

        SKRect destination = SKRect.Create(0f, 0f, ViewWidth, ViewHeight);

        using ManualResetEventSlim ended = new ManualResetEventSlim(false);
        session.PlaybackEnded += (sender, arguments) => ended.Set();

        string failure = null;
        session.MediaFailed += (sender, arguments) =>
        {
            failure = arguments.Message;
            ended.Set();
        };

        try
        {
            session.Open(mediaPath);
        }
        catch (VideoPlaybackException exception)
        {
            Console.Error.WriteLine("consumer-shape: " + exception.Message);
            return 1;
        }

        Console.WriteLine(
            $"opened   {session.Duration:mm\\:ss\\.fff}, video {session.VideoStreamInfo.Width}x"
            + $"{session.VideoStreamInfo.Height}, "
            + (session.AudioTrack == null ? "no audio" : $"audio '{session.AudioTrack.CodecId}'"));

        session.Play();

        // 5. The paint loop. A window would call this from its own paint handler; a console calls it as fast
        //    as it sensibly can and lets the session's clock decide which frame it gets.
        Stopwatch clock = Stopwatch.StartNew();
        int painted = 0;

        while (!ended.IsSet && clock.Elapsed < session.Duration + TimeSpan.FromSeconds(10))
        {
            view.Canvas.Clear(SKColors.Black);
            presenter.Draw(view.Canvas, destination, VideoStretch.Uniform);
            painted++;
            Thread.Sleep(5);
        }

        view.Canvas.Clear(SKColors.Black);
        presenter.Draw(view.Canvas, destination, VideoStretch.Uniform);

        if (failure != null)
        {
            Console.Error.WriteLine("consumer-shape: playback failed - " + failure);
            return 1;
        }

        if (!ended.IsSet)
        {
            Console.Error.WriteLine("consumer-shape: the clip did not reach its end within its own duration.");
            return 1;
        }

        Console.WriteLine(
            $"played   {painted} paints, {presenter.GetStatistics().FramesComposed} frames composed, "
            + $"{presenter.GetStatistics().SurfaceAllocations} composition surface(s), state {session.State}");

        if (!WriteSnapshot(view, snapshotPath)) return 1;

        DescribePixels(presenter, view);
        return 0;
    }

    private static bool WriteSnapshot(SKSurface view, string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using SKImage image = view.Snapshot();
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        if (encoded == null)
        {
            Console.Error.WriteLine("consumer-shape: Skia would not encode the snapshot as a PNG.");
            return false;
        }

        using (FileStream file = File.Create(path)) encoded.SaveTo(file);

        Console.WriteLine($"wrote    {path} ({new FileInfo(path).Length:N0} bytes, {image.Width}x{image.Height})");
        return true;
    }

    private static void DescribePixels(SkiaVideoPresenter presenter, SKSurface view)
    {
        Console.WriteLine(
            $"showing  frame {presenter.CurrentFrameNumber} at {presenter.CurrentTimestamp:mm\\:ss\\.fff}, "
            + $"composed {presenter.ComposedWidth}x{presenter.ComposedHeight}, shown "
            + $"{ViewWidth}x{ViewHeight}");

        using (SKImage composed = presenter.CaptureComposedFrame())
        using (SKPixmap pixels = composed.PeekPixels())
        {
            Console.WriteLine("composed (the video's own pixels, unscaled)");
            foreach ((int x, int y) in new[] { (0, 0), (16, 9), (32, 18), (48, 27), (63, 35) })
            {
                SKColor colour = pixels.GetPixelColor(x, y);
                Console.WriteLine($"           ({x,2},{y,2}) = R{colour.Red,3} G{colour.Green,3} B{colour.Blue,3}");
            }
        }

        using (SKImage shown = view.Snapshot())
        using (SKPixmap pixels = shown.PeekPixels())
        {
            Console.WriteLine("shown    (the view, scaled and centred)");
            foreach ((int x, int y) in new[] { (5, 5), (320, 180), (635, 355) })
            {
                SKColor colour = pixels.GetPixelColor(x, y);
                Console.WriteLine($"           ({x,3},{y,3}) = R{colour.Red,3} G{colour.Green,3} B{colour.Blue,3}");
            }
        }
    }

    private static string FindSampleClip()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "assets", "raw-vorbis.cbv");
        if (File.Exists(beside)) return beside;

        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "assets", "raw-vorbis.cbv");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
