using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CodeBrix.VideoProcessing.Core.Tools;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// An external-tools facet with no process behind it: which tools are "found", where they were "looked for", the
/// encoder listing <c>ffmpeg -encoders</c> answers with, and a scripted result for every other run - the error-stream
/// lines it streams to the command's callback and the exit code it ends with. Every run is recorded.
/// </summary>
internal sealed class FakeExternalTools : IExternalTools
{
    /// <summary>The legend ffmpeg prints above its encoder listing.</summary>
    private const string ListingHeader =
        "Encoders:\n V..... = Video\n A..... = Audio\n S..... = Subtitle\n ------\n";

    private readonly object gate = new object();
    private readonly List<ExternalToolCommand> runs = new List<ExternalToolCommand>();

    /// <summary>The folder reported as <see cref="BinaryFolder" />.</summary>
    public string BinaryFolder { get; set; } = "/opt/fake-ffmpeg";

    /// <summary>Whether ffmpeg is "found".</summary>
    public bool FFmpegFound { get; set; } = true;

    /// <summary>Whether ffprobe is "found".</summary>
    public bool FFprobeFound { get; set; } = true;

    /// <summary>What <see cref="ExternalTool.LookedIn" /> says for a tool.</summary>
    public string LookedIn { get; set; } = "in '/opt/fake-ffmpeg' and then on the PATH";

    /// <summary>The encoders <c>ffmpeg -hide_banner -encoders</c> lists.</summary>
    public HashSet<string> Encoders { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "libsvtav1", "libaom-av1", "libopus", "libvorbis", "flac",
    };

    /// <summary>The error-stream lines every pass run streams, in order.</summary>
    public List<string> ErrorLines { get; } = new List<string>();

    /// <summary>The exit code every pass run ends with.</summary>
    public int ExitCode { get; set; }

    /// <summary>Called with each pass run's command before it "ends" - to write an output file, or to cancel.</summary>
    public Action<ExternalToolCommand> OnRun { get; set; }

    /// <summary>Every command run, the encoder listings included, in order.</summary>
    public IReadOnlyList<ExternalToolCommand> Runs
    {
        get
        {
            lock (gate) return runs.ToArray();
        }
    }

    /// <summary>The rendered arguments of every pass run (the encoder listings left out).</summary>
    public List<string> PassArguments
    {
        get
        {
            List<string> passes = new List<string>();
            foreach (ExternalToolCommand command in Runs)
            {
                if (!IsListing(command)) passes.Add(command.Arguments.Render());
            }

            return passes;
        }
    }

    /// <inheritdoc />
    public ExternalTool Locate(string toolName)
    {
        bool found = toolName switch
        {
            "ffmpeg" => FFmpegFound,
            "ffprobe" => FFprobeFound,
            _ => false,
        };

        return new ExternalTool(toolName, found ? BinaryFolder + "/" + toolName : null, LookedIn);
    }

    /// <inheritdoc />
    public ExternalToolResult Run(ExternalToolCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) runs.Add(command);

        string rendered = BinaryFolder + "/" + command.ToolName + " " + command.Arguments.Render();

        if (IsListing(command))
        {
            StringBuilder listing = new StringBuilder(ListingHeader);
            foreach (string encoder in Encoders)
            {
                bool audio = encoder == "libopus" || encoder == "libvorbis" || encoder == "flac";
                listing.Append(audio ? " A....D " : " V....D ").Append(encoder).Append("             The encoder\n");
            }

            return new ExternalToolResult(0, rendered, listing.ToString(), string.Empty);
        }

        foreach (string line in ErrorLines)
        {
            command.OnStandardErrorLine?.Invoke(line);
        }

        OnRun?.Invoke(command);
        token.ThrowIfCancellationRequested();

        return new ExternalToolResult(ExitCode, rendered, string.Empty, string.Empty);
    }

    private static bool IsListing(ExternalToolCommand command) =>
        command.Arguments.Render() == "-hide_banner -encoders";
}
