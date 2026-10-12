using CodeBrix.VideoProcessing;
using Xunit;

[assembly: AssemblyFixture(typeof(CodeBrix.VideoPlayback.Authoring.Tests.FFmpegEngineRegistration))]

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Registers the desktop ffmpeg engine once, before the first test - exactly as an application does at start-up.
/// </summary>
/// <remarks>
/// <para>
/// The authoring library works through the REGISTERED video-processing engine only, and refuses without one: its
/// ffmpeg command lines run through that engine's external tools, and its in-process passes decode through it. It
/// used to fall back to the ffmpeg engine on its own when nothing was registered; it no longer references the
/// ffmpeg package at all, so the suite registers the engine here, where that fallback used to supply it.
/// </para>
/// <para>
/// The registry cannot take an engine back out, so tests that need ANOTHER engine hand it to the library for their
/// own flow through <see cref="EngineScope" />, and tests of the "nothing registered" refusal use
/// <see cref="NothingRegisteredScope" />. Registering needs no ffmpeg on the machine: the engine looks for its
/// binaries only when it is asked to run them.
/// </para>
/// </remarks>
public sealed class FFmpegEngineRegistration
{
    /// <summary>Registers the engine.</summary>
    public FFmpegEngineRegistration()
    {
        CodeBrixVideoProcessing.Register();
    }
}
