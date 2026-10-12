using System;

namespace CodeBrix.VideoPlayback.Processing;

/// <summary>
/// The argument checks every processing primitive shares, so that a refusal reads the same wherever it comes
/// from: it names the parameter and the value, and says what was expected.
/// </summary>
internal static class ProcessingGuard
{
    public static void Dimension(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be greater than zero; it is {value}.");
        }
    }

    public static void BytesPerSample(int value, string name)
    {
        if (value != 1 && value != 2)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{name} must be 1 (8-bit samples) or 2 (deeper samples, little-endian, value in the low bits); it is {value}.");
        }
    }

    public static void Channels(int value, string name)
    {
        if (value != 1 && value != 4)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{name} must be 1 (one plane) or 4 (interleaved RGBA); it is {value}.");
        }
    }

    public static void MaxSampleValue(int value, int bytesPerSample, string name)
    {
        int ceiling = bytesPerSample == 1 ? byte.MaxValue : ushort.MaxValue;
        if (value < 1 || value > ceiling)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"{name} must be between 1 and {ceiling} for {bytesPerSample}-byte samples; it is {value}.");
        }
    }

    public static void BitDepth(int value, string name)
    {
        if (value != 8 && value != 10 && value != 12)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be 8, 10 or 12; it is {value}.");
        }
    }

    /// <summary>Checks a plane's stride and length against its geometry.</summary>
    public static void Plane(
        int length,
        int width,
        int height,
        int stride,
        int bytesPerSample,
        int channels,
        string spanName,
        string strideName)
    {
        long rowBytes = (long)width * bytesPerSample * channels;
        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(
                strideName,
                stride,
                $"A row of {width} sample(s) x {channels} channel(s) x {bytesPerSample} byte(s) needs {rowBytes} "
                + $"bytes; {strideName} is {stride}.");
        }

        long required = ((long)(height - 1) * stride) + rowBytes;
        if (length < required)
        {
            throw new ArgumentException(
                $"A {width}x{height} plane with a stride of {stride} bytes needs {required} bytes; {spanName} holds {length}.",
                spanName);
        }
    }

    public static void NoOverlap(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, string name)
    {
        if (!source.IsEmpty && !destination.IsEmpty && source.Overlaps(destination))
        {
            throw new ArgumentException(
                $"{name} overlaps the source; these primitives read and write different memory, so use a second buffer.",
                name);
        }
    }

    public static int ChromaShiftX(ProcessingFrameLayout layout) =>
        layout == ProcessingFrameLayout.I420 || layout == ProcessingFrameLayout.I422 ? 1 : 0;

    public static int ChromaShiftY(ProcessingFrameLayout layout) => layout == ProcessingFrameLayout.I420 ? 1 : 0;

    public static bool HasChroma(ProcessingFrameLayout layout) =>
        layout == ProcessingFrameLayout.I420 || layout == ProcessingFrameLayout.I422 || layout == ProcessingFrameLayout.I444;

    public static int ChromaSize(int size, int shift) => (size + (1 << shift) - 1) >> shift;

    public static void Layout(ProcessingFrameLayout layout, string name)
    {
        if (layout < ProcessingFrameLayout.Gray || layout > ProcessingFrameLayout.Rgba)
        {
            throw new ArgumentOutOfRangeException(name, layout, $"{name} is not a known layout: {(int)layout}.");
        }
    }
}
