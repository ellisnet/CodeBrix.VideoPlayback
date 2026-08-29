using CodeBrix.Audio.Opus;
using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using System;
using System.Text;

namespace SimpleCbxVideoPlayer.SkiaVideo;

/// <summary>
/// Registers the decoders this application plays with - once, whichever head is running.
/// </summary>
/// <remarks>
/// Nothing in this family is discovered by reflection: an AV1 file with no decoder registered fails with a
/// message naming the package to add, and so does an Opus soundtrack. Both registrations happen here, and
/// this is the only place in the sample that names either package, so a WinUI or WPF version of the
/// application starts up by calling the same method.
/// </remarks>
public static class SkiaVideoRuntime
{
    /// <summary>The rate the media carries, and the only rate Opus decodes at.</summary>
    public const int AudioSampleRate = 48000;

    private static readonly object Gate = new();
    private static bool hasRun;

    /// <summary>True once the registrations have been attempted and every one of them succeeded.</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>What was registered, for a log line or a status bar.</summary>
    public static string Summary { get; private set; } = string.Empty;

    /// <summary>Why initialization failed, word for word, or an empty string when it did not.</summary>
    public static string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Registers the AV1 decoder and the Opus audio decoder, once per process.</summary>
    /// <returns>True when everything registered; false when something did not, with the reason in ErrorMessage.</returns>
    /// <remarks>
    /// Safe to call from anywhere, as often as you like: the work happens on the first call and every later
    /// call reports what that one found.
    /// </remarks>
    public static bool Initialize()
    {
        lock (Gate)
        {
            if (hasRun) { return IsInitialized; }

            hasRun = true;

            StringBuilder summary = new StringBuilder();

            try
            {
                //The device runs at the media's own rate, so no sample-rate conversion happens at all.
                SharedAudioOutput.Configure(AudioSampleRate);
                summary.Append($"audio output at {AudioSampleRate} Hz");

                CodeBrixAudioOpus.Register();
                summary.Append("; Opus registered");

                CodeBrixVideoPlaybackDav1d.Register();
                summary.Append($"; dav1d {CodeBrixVideoPlaybackDav1d.NativeVersion} registered for av01");

                IsInitialized = VideoDecoders.IsCodecSupported(VideoCodecIds.Av1);

                if (!IsInitialized)
                {
                    ErrorMessage = "The AV1 decoder registered without complaint, but 'av01' is still "
                        + "unsupported. Nothing in the corpus will play.";
                }
            }
            catch (Exception exception)
            {
                //A start-up failure is shown, not swallowed: the message names the package that is missing.
                IsInitialized = false;
                ErrorMessage = exception.Message;
            }

            Summary = summary.ToString();
            return IsInitialized;
        }
    }
}
