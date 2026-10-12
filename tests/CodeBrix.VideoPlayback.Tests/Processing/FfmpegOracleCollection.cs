using Xunit;

namespace CodeBrix.VideoPlayback.Tests.Processing;

/// <summary>
/// The processing tests start several dozen FFmpeg child processes. Run alongside the suite's in-process
/// HTTP test servers, that intermittently made <c>HttpListener.Close</c> fail with "Address already in use"
/// in <c>HttpMediaSourceTests</c> (never seen with these tests excluded). Putting every FFmpeg-oracle class
/// in one collection that is not run in parallel keeps the two apart; it costs about a second.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FfmpegOracleCollection
{
    /// <summary>The collection name the FFmpeg-oracle test classes share.</summary>
    public const string Name = "FFmpeg oracle processes";
}
