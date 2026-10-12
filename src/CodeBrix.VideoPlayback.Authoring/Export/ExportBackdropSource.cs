using System;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.VideoPlayback.Processing;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// Supplies the opaque RGBA backdrop, at the output size, for each output time: one buffer for a colour or an
/// image, the nearest-earlier frame of a video.
/// </summary>
internal abstract class ExportBackdropSource : IDisposable
{
    /// <summary>Creates a backdrop of a given output size.</summary>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    protected ExportBackdropSource(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    /// <summary>The output width.</summary>
    public int Width { get; }

    /// <summary>The output height.</summary>
    public int Height { get; }

    /// <summary>The current backdrop picture, opaque RGBA at the output size.</summary>
    protected byte[] Pixels { get; }

    /// <summary>True for a backdrop with a length of its own - a video.</summary>
    public virtual bool HasLength => false;

    /// <summary>The backdrop picture at an output time, or null once a video backdrop has ended.</summary>
    /// <param name="time">The output frame's time.</param>
    /// <returns>Opaque RGBA at the output size, valid until the next call; null when the backdrop has ended.</returns>
    public virtual byte[] PictureAt(TimeSpan time) => Pixels;

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }

    /// <summary>Scales RGBA pixels of any size into a destination at the output size, then flattens them over grey.</summary>
    /// <param name="scaler">The scaler for this geometry, or null when the sizes are equal.</param>
    /// <param name="source">The source pixels, rows packed.</param>
    /// <param name="sourceWidth">The source width.</param>
    /// <param name="sourceHeight">The source height.</param>
    /// <param name="destination">The destination, at the output size.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    protected static void ScaleInto(
        FrameScaler scaler,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        byte[] destination,
        int width,
        int height)
    {
        if (scaler == null)
        {
            source.Slice(0, width * height * 4).CopyTo(destination);
        }
        else
        {
            ReadOnlyPlanarFrame from = ReadOnlyPlanarFrame.CreateRgba(sourceWidth, sourceHeight, source, sourceWidth * 4);
            PlanarFrame to = PlanarFrame.CreateRgba(width, height, destination, width * 4);
            scaler.Scale(in from, in to);
        }

        ExportPixels.FlattenOverGrey(destination);
    }

    /// <summary>The scaler for a geometry - the core's default filter - or null when no scaling is needed.</summary>
    /// <param name="sourceWidth">The source width.</param>
    /// <param name="sourceHeight">The source height.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <returns>The scaler, or null.</returns>
    protected static FrameScaler ScalerFor(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height) return null;

        return new FrameScaler(
            ProcessingFrameLayout.Rgba,
            sourceWidth,
            sourceHeight,
            width,
            height,
            PlaneScaler.ChooseDefault(sourceWidth, sourceHeight, width, height));
    }

    /// <summary>A backdrop of one solid colour.</summary>
    internal sealed class Solid : ExportBackdropSource
    {
        /// <summary>Creates the backdrop.</summary>
        /// <param name="width">The output width.</param>
        /// <param name="height">The output height.</param>
        /// <param name="rgb">The colour as <c>0xRRGGBB</c>.</param>
        public Solid(int width, int height, int rgb)
            : base(width, height)
        {
            ExportPixels.Fill(Pixels, rgb);
        }
    }

    /// <summary>A still image, decoded once and scaled once.</summary>
    internal sealed class Still : ExportBackdropSource
    {
        /// <summary>Creates the backdrop from RGBA pixels.</summary>
        /// <param name="width">The output width.</param>
        /// <param name="height">The output height.</param>
        /// <param name="rgba">The image's pixels, straight RGBA, rows packed.</param>
        /// <param name="imageWidth">The image width.</param>
        /// <param name="imageHeight">The image height.</param>
        public Still(int width, int height, byte[] rgba, int imageWidth, int imageHeight)
            : base(width, height)
        {
            ScaleInto(ScalerFor(imageWidth, imageHeight, width, height), rgba, imageWidth, imageHeight, Pixels, width, height);
        }

        /// <summary>Decodes an image file to straight RGBA with CodeBrix.Imaging.</summary>
        /// <param name="path">The file.</param>
        /// <param name="imageWidth">Its width.</param>
        /// <param name="imageHeight">Its height.</param>
        /// <returns>The pixels, rows packed.</returns>
        /// <exception cref="VideoAuthoringException">The file cannot be read as an image.</exception>
        public static byte[] Load(string path, out int imageWidth, out int imageHeight)
        {
            try
            {
                using Image<Rgba32> image = Image.Load<Rgba32>(path);
                imageWidth = image.Width;
                imageHeight = image.Height;
                byte[] rgba = new byte[imageWidth * imageHeight * 4];
                image.CopyPixelDataTo(rgba.AsSpan());
                return rgba;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new VideoAuthoringException(
                    "The backdrop image '" + path + "' cannot be read as an image: " + ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// A video, decoded as RGBA through the export's engine; each output time takes the frame shown at or before it.
    /// </summary>
    internal sealed class Moving : ExportBackdropSource
    {
        private readonly IVideoFrameSource source;
        private readonly FrameScaler scaler;
        private readonly byte[] raw;
        private readonly int sourceWidth;
        private readonly int sourceHeight;
        private bool hasPending;
        private VideoFrameData pending;
        private bool hasPicture;
        private bool rawChanged;
        private bool exhausted;
        private TimeSpan lastTimestamp;

        /// <summary>Opens the backdrop video.</summary>
        /// <param name="engine">The engine.</param>
        /// <param name="path">The video file.</param>
        /// <param name="width">The output width.</param>
        /// <param name="height">The output height.</param>
        public Moving(IVideoProcessor engine, string path, int width, int height)
            : base(width, height)
        {
            source = engine.OpenVideo(path, new VideoSourceOptions { PixelLayout = VideoPixelLayout.Rgba, BitDepth = 8 });
            VideoSourceInfo info = source.Info;
            sourceWidth = info.Width;
            sourceHeight = info.Height;
            FrameDuration = info.FrameRate.IsSpecified ? info.FrameRate.FrameDuration : TimeSpan.Zero;
            raw = new byte[sourceWidth * sourceHeight * 4];
            scaler = ScalerFor(sourceWidth, sourceHeight, width, height);
            SourceDuration = info.Duration;
        }

        /// <inheritdoc />
        public override bool HasLength => true;

        /// <summary>The size the backdrop video was decoded at.</summary>
        public string SourceSize => sourceWidth + "x" + sourceHeight;

        /// <summary>True when the backdrop video is a different size from the output and is scaled.</summary>
        public bool IsScaled => scaler != null;

        /// <summary>The length of one backdrop frame.</summary>
        public TimeSpan FrameDuration { get; }

        /// <summary>The length the engine reported when it opened the backdrop.</summary>
        public TimeSpan SourceDuration { get; }

        /// <summary>When the backdrop ended - its last frame's time plus one frame - once it has; otherwise null.</summary>
        public TimeSpan? EndTime { get; private set; }

        /// <inheritdoc />
        public override byte[] PictureAt(TimeSpan time)
        {
            while (!exhausted)
            {
                if (!hasPending)
                {
                    if (!source.TryReadFrame(out pending))
                    {
                        exhausted = true;
                        EndTime = hasPicture ? lastTimestamp + FrameDuration : TimeSpan.Zero;
                        break;
                    }

                    hasPending = true;
                }

                // The first frame always counts, so a backdrop that starts a hair late still covers time zero.
                if (pending.Timestamp > time && hasPicture) break;

                CopyRows(pending.Rgba, raw, sourceWidth * 4, sourceHeight);
                lastTimestamp = pending.Timestamp;
                hasPicture = true;
                rawChanged = true;
                hasPending = false;
                if (lastTimestamp > time) break;
            }

            if (!hasPicture || (EndTime.HasValue && time >= EndTime.Value)) return null;

            if (rawChanged)
            {
                ScaleInto(scaler, raw, sourceWidth, sourceHeight, Pixels, Width, Height);
                rawChanged = false;
            }

            return Pixels;
        }

        /// <inheritdoc />
        public override void Dispose() => source.Dispose();

        private static void CopyRows(VideoPlane plane, byte[] target, int rowLength, int rows)
        {
            for (int row = 0; row < rows; row++)
            {
                plane.GetRow(row).Slice(0, rowLength).CopyTo(target.AsSpan(row * rowLength, rowLength));
            }
        }
    }
}
