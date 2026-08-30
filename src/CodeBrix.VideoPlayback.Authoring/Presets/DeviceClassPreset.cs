using System;
using System.Globalization;
using CodeBrix.VideoPlayback.Authoring.Encoding;

namespace CodeBrix.VideoPlayback.Authoring.Presets;

/// <summary>
/// A starting point for one class of playback machine: how big the picture should be, how hard the encoder
/// should work on it, how much rate to give it, and how much to give the sound.
/// </summary>
/// <remarks>
/// A preset is a starting point and nothing more. <see cref="ApplyTo(VideoAuthoringRequest)" /> writes its
/// four numbers into a request; every one of them can then be overridden, and the request is not marked in
/// any way by having been through a preset.
/// </remarks>
public sealed class DeviceClassPreset
{
    /// <summary>Creates a preset.</summary>
    /// <param name="deviceClass">The class of machine the numbers were chosen for.</param>
    /// <param name="longSidePixels">The long side of the frame, in pixels.</param>
    /// <param name="speedPreset">The encoder's speed knob.</param>
    /// <param name="constantRateFactor">The constant rate factor.</param>
    /// <param name="audioKilobitsPerSecond">The audio bit rate in kilobits per second.</param>
    /// <param name="description">One line saying what the numbers buy.</param>
    public DeviceClassPreset(
        DeviceClass deviceClass,
        int longSidePixels,
        int speedPreset,
        int constantRateFactor,
        int audioKilobitsPerSecond,
        string description)
    {
        DeviceClass = deviceClass;
        LongSidePixels = longSidePixels;
        SpeedPreset = speedPreset;
        ConstantRateFactor = constantRateFactor;
        AudioKilobitsPerSecond = audioKilobitsPerSecond;
        Description = description ?? string.Empty;
    }

    /// <summary>The class of machine the numbers were chosen for.</summary>
    public DeviceClass DeviceClass { get; }

    /// <summary>The long side of the frame, in pixels.</summary>
    public int LongSidePixels { get; }

    /// <summary>The encoder's speed knob - SVT-AV1's preset, or libaom's cpu-used.</summary>
    public int SpeedPreset { get; }

    /// <summary>The constant rate factor.</summary>
    public int ConstantRateFactor { get; }

    /// <summary>The audio bit rate in kilobits per second.</summary>
    public int AudioKilobitsPerSecond { get; }

    /// <summary>One line saying what the numbers buy.</summary>
    public string Description { get; }

    /// <summary>Writes this preset's four numbers into a request.</summary>
    /// <param name="request">The request to start from these numbers.</param>
    /// <returns>The same request, so the call can be chained into a configuration block.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public VideoAuthoringRequest ApplyTo(VideoAuthoringRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        request.Video.FrameSize = AuthoringFrameSize.LongSide(LongSidePixels);
        request.Video.SpeedPreset = SpeedPreset;
        request.Video.ConstantRateFactor = ConstantRateFactor;
        request.Audio.BitrateKilobitsPerSecond = AudioKilobitsPerSecond;
        return request;
    }

    /// <inheritdoc />
    public override string ToString() =>
        DeviceClass + ": " + LongSidePixels.ToString(CultureInfo.InvariantCulture) + " on the long side, preset "
        + SpeedPreset.ToString(CultureInfo.InvariantCulture) + ", crf "
        + ConstantRateFactor.ToString(CultureInfo.InvariantCulture) + ", "
        + AudioKilobitsPerSecond.ToString(CultureInfo.InvariantCulture) + " kbit/s audio";
}
