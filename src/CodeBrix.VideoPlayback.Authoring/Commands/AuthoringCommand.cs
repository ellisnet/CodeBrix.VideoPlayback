using System;

namespace CodeBrix.VideoPlayback.Authoring.Commands;

/// <summary>
/// One pass an authoring request produces - an FFmpeg command line, or the settings line of a pass that runs
/// in process - rendered but not run.
/// </summary>
/// <remarks>
/// A WebM-profile request produces one of these; a bespoke request produces two, because the picture and the
/// sound are encoded separately and muxed afterwards by managed code. A master request produces one per
/// in-process pass - picture, alpha plane, sound - and, first, the FFmpeg command of its prepare pass when it
/// needs one; see <see cref="RunsFFmpeg" />. Both a dry run and a real run expose the same objects, so what a
/// pipeline records in a manifest is what was actually executed.
/// <para>
/// A real run records each command AFTER its pass has run, from the line FFmpeg was actually given. The two
/// agree except in one case: when <see cref="VideoAuthoringRequest.AllowAv1EncoderFallback" /> (or the
/// process-wide switch) rewrote a pass for <c>libaom-av1</c> on a build without SVT-AV1, the real run's
/// command names <c>libaom-av1</c> while the dry run's still names <c>libsvtav1</c>.
/// </para>
/// </remarks>
public sealed class AuthoringCommand
{
    /// <summary>Creates a command.</summary>
    /// <param name="label">Which step of the authoring this is, in a few words.</param>
    /// <param name="arguments">The rendered arguments, without the executable's own name.</param>
    /// <exception cref="ArgumentException"><paramref name="label" /> or <paramref name="arguments" /> is null or blank.</exception>
    public AuthoringCommand(string label, string arguments)
        : this(label, arguments, true)
    {
    }

    /// <summary>Creates a command that is either an FFmpeg command line or the settings of an in-process pass.</summary>
    /// <param name="label">Which step of the authoring this is, in a few words.</param>
    /// <param name="arguments">
    /// The rendered arguments, without the executable's own name, or - for a pass that runs in process - its
    /// settings line.
    /// </param>
    /// <param name="runsFFmpeg">True for an FFmpeg command line, false for a pass that runs in process.</param>
    /// <exception cref="ArgumentException"><paramref name="label" /> or <paramref name="arguments" /> is null or blank.</exception>
    public AuthoringCommand(string label, string arguments, bool runsFFmpeg)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A command needs a label.", nameof(label));
        if (string.IsNullOrWhiteSpace(arguments)) throw new ArgumentException("A command needs arguments.", nameof(arguments));

        Label = label;
        Arguments = arguments;
        RunsFFmpeg = runsFFmpeg;
    }

    /// <summary>Which step of the authoring this is - for example "one pass", "video pass", "audio pass".</summary>
    public string Label { get; }

    /// <summary>
    /// The rendered arguments, without the <c>ffmpeg</c> at the front - or, when <see cref="RunsFFmpeg" /> is
    /// false, the pass's settings line, such as
    /// <c>av1 i420 8-bit good-quality cq-level 30 cpu-used 6 keyint 240 &lt;- in.mov -&gt; clip.video.ivf</c>.
    /// </summary>
    public string Arguments { get; }

    /// <summary>
    /// True when this is an FFmpeg command line; false when it is a pass that runs in process - the master
    /// flavour's picture, alpha and sound passes - and <see cref="Arguments" /> is its settings line.
    /// </summary>
    public bool RunsFFmpeg { get; }

    /// <inheritdoc />
    public override string ToString() => RunsFFmpeg ? "ffmpeg " + Arguments : "in process: " + Arguments;
}
