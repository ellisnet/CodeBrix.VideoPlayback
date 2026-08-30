using System;
using System.Collections.Generic;

namespace CodeBrix.VideoPlayback.Authoring.Presets;

/// <summary>
/// The device-class table: three rungs of starting numbers, with the reasoning behind them.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are the ones this repository's own sample-video corpus is built from, so they are measured
/// rather than guessed - see the encoder-settings table in <c>tests/assets/authoring/MANIFEST.txt</c>. The
/// two rules they encode:
/// </para>
/// <list type="bullet">
///   <item><description>
///     THE SPEED PRESET GETS FASTER AS THE FRAME GETS BIGGER. Encode cost scales with the pixel count, so
///     the 2160p rung takes the faster preset and the 720p rung the slower one. That keeps the wall clock of
///     a whole ladder roughly level instead of being dominated by its top rung.
///   </description></item>
///   <item><description>
///     THE RATE FACTOR GETS LOWER AS THE FRAME GETS SMALLER. AV1's rate factor is resolution-relative: the
///     same number looks better the more pixels are hiding the error, so 28 at 2160p, 26 at 1080p and 24 at
///     720p land the three rungs at a similar perceived quality.
///   </description></item>
/// </list>
/// <para>
/// The audio follows the picture: the smallest rung is the "small file" rung, and its sound shrinks with it.
/// </para>
/// </remarks>
public static class DeviceClassPresets
{
    private static readonly DeviceClassPreset DesktopPreset = new DeviceClassPreset(
        DeviceClass.Desktop4K,
        3840,
        6,
        28,
        128,
        "4K for a desktop, a laptop or Apple Silicon: the faster preset, because 2160p is where encode cost lives.");

    private static readonly DeviceClassPreset PiPreset = new DeviceClassPreset(
        DeviceClass.Pi1080p,
        1920,
        5,
        26,
        128,
        "1080p for a Raspberry-Pi-class 64-bit ARM board: the working ceiling for a software AV1 decoder there.");

    private static readonly DeviceClassPreset RiscVPreset = new DeviceClassPreset(
        DeviceClass.RiscV720p,
        1280,
        4,
        24,
        96,
        "720p for a current RISC-V board: the slower preset and the lower rate factor, because the file is small.");

    /// <summary>The three rungs, biggest frame first.</summary>
    public static IReadOnlyList<DeviceClassPreset> All { get; } = new[] { DesktopPreset, PiPreset, RiscVPreset };

    /// <summary>4K for a desktop, a laptop or Apple Silicon.</summary>
    public static DeviceClassPreset Desktop4K => DesktopPreset;

    /// <summary>1080p for a Raspberry-Pi-class 64-bit ARM board.</summary>
    public static DeviceClassPreset Pi1080p => PiPreset;

    /// <summary>720p for a current RISC-V board.</summary>
    public static DeviceClassPreset RiscV720p => RiscVPreset;

    /// <summary>Looks a preset up by its device class.</summary>
    /// <param name="deviceClass">The class of machine.</param>
    /// <returns>The preset.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no preset for that class.</exception>
    public static DeviceClassPreset For(DeviceClass deviceClass)
    {
        switch (deviceClass)
        {
            case DeviceClass.Desktop4K: return DesktopPreset;
            case DeviceClass.Pi1080p: return PiPreset;
            case DeviceClass.RiscV720p: return RiscVPreset;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(deviceClass),
                    deviceClass,
                    "There is no device-class preset for that class.");
        }
    }
}
