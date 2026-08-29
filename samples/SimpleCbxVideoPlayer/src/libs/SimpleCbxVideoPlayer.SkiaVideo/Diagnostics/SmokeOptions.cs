using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;

/// <summary>
/// The hidden command line that lets the application verify itself without a person watching.
/// </summary>
/// <remarks>
/// <para>
/// The application is a window, and a window cannot be asserted about. With <c>--smoke</c> on the command
/// line it still opens its window and still paints through the real head and the real canvas - but it
/// chooses a file, plays it, writes one composed frame to a PNG, prints what it saw and leaves with an
/// exit code. That is a test an unattended run can read.
/// </para>
/// <para>
/// Usage:
/// </para>
/// <code>
/// SimpleCbxVideoPlayer.LinuxX11 --smoke MKV/landscape_hd.mkv --snapshot /tmp/frame.png --exit
///     --render-path cpu            gpuauto (default) | gpunofallback | cpu
///     --lut sepia_33.cube@40        repeatable, applied in order; the percentage defaults to 40
///                                   (a full path to a .cube file outside the corpus is accepted too)
///     --bake /tmp/chain.cube        write the APPLIED chain out as one .cube file
///     --compare /tmp/other.png      measure the captured frame against another one, and fail when
///     --compare-tolerance 2         any channel differs by more than this (default 2)
///     --seconds 2                   how long to play before capturing
///     --snapshot-at 1.0             the position the captured frame is seeked to
///     --until-ended                 play the whole file instead (the audible run)
///     --no-audio                    open the file with its soundtrack switched off
/// </code>
/// </remarks>
public sealed class SmokeOptions
{
    /// <summary>The switch that turns a normal launch into a smoke run.</summary>
    public const string SmokeSwitch = "--smoke";

    /// <summary>How long the file plays before the frame is captured, unless asked otherwise.</summary>
    public static readonly TimeSpan DefaultPlayDuration = TimeSpan.FromSeconds(2);

    /// <summary>The position the captured frame is seeked to, unless asked otherwise.</summary>
    public static readonly TimeSpan DefaultSnapshotPosition = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far any one colour channel may differ before <c>--compare</c> counts as a failure.
    /// </summary>
    /// <remarks>
    /// Two is a pinned tolerance, not a shrug: a table baked from a chain and the chain itself are the same
    /// arithmetic, so the two pictures should agree to the last bit or very nearly. Anything that needs
    /// more than a couple of levels of slack is a difference worth looking at.
    /// </remarks>
    public const int DefaultCompareTolerance = 2;

    private SmokeOptions() { }

    /// <summary>True when the command line asked for a smoke run.</summary>
    public bool IsSmokeRun { get; private set; }

    /// <summary>The video to play: a corpus name such as "MKV/landscape_hd.mkv", a file name, or a full path.</summary>
    public string VideoName { get; private set; } = string.Empty;

    /// <summary>Where to write the composed frame, or an empty string to write none.</summary>
    public string SnapshotPath { get; private set; } = string.Empty;

    /// <summary>The render path to select before playing.</summary>
    public VideoRenderPathOption RenderPath { get; private set; } = VideoRenderPathOption.GpuAuto;

    /// <summary>The lookup tables to tick, in order, each with its percentage.</summary>
    public IReadOnlyList<SmokeLutRequest> Luts { get; private set; } = [];

    /// <summary>How long to play before capturing.</summary>
    public TimeSpan PlayDuration { get; private set; } = DefaultPlayDuration;

    /// <summary>The position the captured frame is seeked to, so that two runs capture the same picture.</summary>
    public TimeSpan SnapshotPosition { get; private set; } = DefaultSnapshotPosition;

    /// <summary>Whether to play the whole file rather than a fixed stretch of it.</summary>
    public bool PlayUntilEnded { get; private set; }

    /// <summary>Whether to open the file with its soundtrack switched off.</summary>
    public bool PlayAudio { get; private set; } = true;

    /// <summary>Where to bake the applied chain of lookup tables, or an empty string to bake none.</summary>
    public string BakePath { get; private set; } = string.Empty;

    /// <summary>A picture to measure the captured frame against, or an empty string to measure none.</summary>
    public string ComparePath { get; private set; } = string.Empty;

    /// <summary>How far any one colour channel may differ before a comparison counts as a failure.</summary>
    public int CompareTolerance { get; private set; } = DefaultCompareTolerance;

    /// <summary>
    /// What was wrong with the command line, or an empty string when nothing was.
    /// </summary>
    /// <remarks>
    /// A smoke run whose command line was not understood must FAIL rather than quietly run with a switch
    /// missing - a silently dropped <c>--lut</c> makes a passing run meaningless - so the message is carried
    /// here and the run reports it and leaves with a non-zero exit code.
    /// </remarks>
    public string ParseError { get; private set; } = string.Empty;

    /// <summary>Reads the command line the process was started with.</summary>
    /// <returns>
    /// The options, which are not a smoke run when no --smoke switch was given. A command line that was
    /// refused comes back with <see cref="ParseError" /> set, and is still a smoke run when it asked to be.
    /// </returns>
    public static SmokeOptions FromCommandLine()
    {
        //The first entry is the executable itself.
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();

        if (TryParse(arguments, out var options, out var error)) { return options; }

        //A command line that was refused is still a smoke run when it asked to be one, so that the run can
        //  report the refusal and leave with a failing exit code instead of opening a window nobody watches.
        options.ParseError = error;
        options.IsSmokeRun = arguments.Any(argument =>
            string.Equals(argument, SmokeSwitch, StringComparison.OrdinalIgnoreCase));

        return options;
    }

    /// <summary>Reads a command line.</summary>
    /// <param name="arguments">The arguments, without the executable's own name.</param>
    /// <param name="options">The options that were read; never null.</param>
    /// <param name="error">What was wrong with the command line, or an empty string when nothing was.</param>
    /// <returns>True when the command line was understood.</returns>
    public static bool TryParse(IReadOnlyList<string> arguments, out SmokeOptions options, out string error)
    {
        options = new SmokeOptions();
        error = string.Empty;

        if (arguments == null || arguments.Count == 0) { return true; }

        List<SmokeLutRequest> luts = [];

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.IsNullOrWhiteSpace(argument)) { continue; }

            switch (argument.ToLowerInvariant())
            {
                case SmokeSwitch:
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var video)) { return false; }
                    options.IsSmokeRun = true;
                    options.VideoName = video;
                    break;

                case "--snapshot":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var snapshot)) { return false; }
                    options.SnapshotPath = snapshot;
                    break;

                case "--render-path":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var path)) { return false; }
                    if (!TryParseRenderPath(path, out var renderPath))
                    {
                        error = $"'{path}' is not a render path. Say gpuauto, gpunofallback or cpu.";
                        return false;
                    }
                    options.RenderPath = renderPath;
                    break;

                case "--lut":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var lut)) { return false; }
                    if (!SmokeLutRequest.TryParse(lut, out var lutRequest, out error)) { return false; }
                    luts.Add(lutRequest);
                    break;

                case "--seconds":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var seconds)) { return false; }
                    if (!TryParseSeconds(seconds, out var playDuration))
                    {
                        error = $"'{seconds}' is not a number of seconds.";
                        return false;
                    }
                    options.PlayDuration = playDuration;
                    break;

                case "--snapshot-at":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var at)) { return false; }
                    if (!TryParseSeconds(at, out var snapshotPosition))
                    {
                        error = $"'{at}' is not a number of seconds.";
                        return false;
                    }
                    options.SnapshotPosition = snapshotPosition;
                    break;

                case "--bake":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var bake)) { return false; }
                    options.BakePath = bake;
                    break;

                case "--compare":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var compare)) { return false; }
                    options.ComparePath = compare;
                    break;

                case "--compare-tolerance":
                    if (!TryTakeValue(arguments, ref index, argument, ref error, out var tolerance)) { return false; }
                    if (!int.TryParse(tolerance, NumberStyles.Integer, CultureInfo.InvariantCulture, out var levels)
                        || levels < 0
                        || levels > 255)
                    {
                        error = $"'{tolerance}' is not a tolerance: say a whole number of colour levels, 0 to 255.";
                        return false;
                    }
                    options.CompareTolerance = levels;
                    break;

                case "--until-ended":
                    options.PlayUntilEnded = true;
                    break;

                case "--no-audio":
                    options.PlayAudio = false;
                    break;

                case "--exit":
                    //Accepted, and the default: a smoke run always leaves when it is finished.
                    break;

                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"'{argument}' is not one of this application's switches.";
                        return false;
                    }
                    break;
            }
        }

        options.Luts = luts;

        if (options.IsSmokeRun && string.IsNullOrWhiteSpace(options.VideoName))
        {
            error = "--smoke needs the name of a video to play.";
            return false;
        }

        return true;
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string switchName,
        ref string error,
        out string value)
    {
        if (index + 1 >= arguments.Count)
        {
            error = $"{switchName} needs a value after it.";
            value = string.Empty;
            return false;
        }

        index++;
        value = arguments[index];
        return true;
    }

    private static bool TryParseRenderPath(string text, out VideoRenderPathOption renderPath)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "cpu":
                renderPath = VideoRenderPathOption.Cpu;
                return true;
            case "gpu":
            case "gpuauto":
            case "gpu-auto":
                renderPath = VideoRenderPathOption.GpuAuto;
                return true;
            case "gpunofallback":
            case "gpu-no-fallback":
                renderPath = VideoRenderPathOption.GpuNoFallback;
                return true;
            default:
                renderPath = VideoRenderPathOption.GpuAuto;
                return false;
        }
    }

    private static bool TryParseSeconds(string text, out TimeSpan seconds)
    {
        seconds = TimeSpan.Zero;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) { return false; }

        if (value < 0) { return false; }

        seconds = TimeSpan.FromSeconds(value);
        return true;
    }
}
