using System;

namespace CodeBrix.VideoPlayback.Authoring.Commands;

/// <summary>
/// One FFmpeg command line an authoring request produces, rendered but not run.
/// </summary>
/// <remarks>
/// A WebM-profile request produces one of these; a bespoke request produces two, because the picture and the
/// sound are encoded separately and muxed afterwards by managed code. Both a dry run and a real run expose
/// the same objects, so what a pipeline records in a manifest is what was actually executed.
/// </remarks>
public sealed class AuthoringCommand
{
    /// <summary>Creates a command.</summary>
    /// <param name="label">Which step of the authoring this is, in a few words.</param>
    /// <param name="arguments">The rendered arguments, without the executable's own name.</param>
    /// <exception cref="ArgumentException"><paramref name="label" /> or <paramref name="arguments" /> is null or blank.</exception>
    public AuthoringCommand(string label, string arguments)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A command needs a label.", nameof(label));
        if (string.IsNullOrWhiteSpace(arguments)) throw new ArgumentException("A command needs arguments.", nameof(arguments));

        Label = label;
        Arguments = arguments;
    }

    /// <summary>Which step of the authoring this is - for example "one pass", "video pass", "audio pass".</summary>
    public string Label { get; }

    /// <summary>The rendered arguments, without the <c>ffmpeg</c> at the front.</summary>
    public string Arguments { get; }

    /// <inheritdoc />
    public override string ToString() => "ffmpeg " + Arguments;
}
