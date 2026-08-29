namespace SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;

/// <summary>How far apart two pictures are, pixel for pixel.</summary>
public sealed class ImageComparisonResult
{
    /// <summary>Creates the record.</summary>
    /// <param name="width">The width both pictures share, or 0 when they do not share one.</param>
    /// <param name="height">The height both pictures share, or 0 when they do not share one.</param>
    /// <param name="sizesMatch">Whether the two pictures are the same size at all.</param>
    /// <param name="maxChannelDelta">The largest difference in any one colour channel, 0 to 255.</param>
    /// <param name="meanAbsoluteDelta">The mean absolute channel difference, 0 to 255.</param>
    /// <param name="differingPixelPercent">How many pixels differ at all, as a percentage.</param>
    public ImageComparisonResult(
        int width,
        int height,
        bool sizesMatch,
        int maxChannelDelta,
        double meanAbsoluteDelta,
        double differingPixelPercent)
    {
        Width = width;
        Height = height;
        SizesMatch = sizesMatch;
        MaxChannelDelta = maxChannelDelta;
        MeanAbsoluteDelta = meanAbsoluteDelta;
        DifferingPixelPercent = differingPixelPercent;
    }

    /// <summary>The width both pictures share, or 0 when they do not share one.</summary>
    public int Width { get; }

    /// <summary>The height both pictures share, or 0 when they do not share one.</summary>
    public int Height { get; }

    /// <summary>Whether the two pictures are the same size at all.</summary>
    public bool SizesMatch { get; }

    /// <summary>The largest difference in any one colour channel, 0 to 255.</summary>
    public int MaxChannelDelta { get; }

    /// <summary>The mean absolute channel difference, 0 to 255.</summary>
    public double MeanAbsoluteDelta { get; }

    /// <summary>How many pixels differ at all, as a percentage.</summary>
    public double DifferingPixelPercent { get; }

    /// <inheritdoc />
    public override string ToString() =>
        SizesMatch
            ? $"{Width}x{Height} max-channel-delta={MaxChannelDelta} mean-abs-delta={MeanAbsoluteDelta:0.###} "
              + $"differing-pixels={DifferingPixelPercent:0.###}%"
            : "the two pictures are not the same size";
}
