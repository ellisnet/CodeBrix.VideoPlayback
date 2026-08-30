using System;

namespace CodeBrix.VideoPlayback.Authoring.Encoding;

/// <summary>
/// How the sound is encoded, and what the container is told about the track.
/// </summary>
/// <remarks>
/// <para>
/// The codec choice is the one authoring decision that reaches the APPLICATION that plays the file. Vorbis
/// plays with the core playback package alone; Opus needs the application to reference CodeBrix.Audio.Opus
/// and call its <c>Register()</c>. <see cref="AuthoringAudioCodec.Default" /> therefore resolves per
/// flavour - Opus for the WebM-profile file, which is what the wider world expects of a WebM, and Vorbis for
/// the bespoke one, which is the flavour an application ships inside itself.
/// </para>
/// <para>
/// Rate control: Opus takes a bit rate. Vorbis takes either - set <see cref="VorbisQuality" /> and the
/// quality number is used and the bit rate is not emitted at all.
/// </para>
/// </remarks>
public sealed class AuthoringAudioSettings
{
    private int bitrateKilobitsPerSecond = 128;
    private int sampleRateHz = 48000;
    private int channels = 2;

    /// <summary>Which encoder to ask FFmpeg for. Per-flavour by default.</summary>
    public AuthoringAudioCodec Codec { get; set; } = AuthoringAudioCodec.Default;

    /// <summary>True to encode the source's audio at all. True by default.</summary>
    /// <remarks>Set it false to author a file with a picture and no sound.</remarks>
    public bool Include { get; set; } = true;

    /// <summary>The bit rate in kilobits per second. 128 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 6 and 512.</exception>
    public int BitrateKilobitsPerSecond
    {
        get => bitrateKilobitsPerSecond;
        set
        {
            if (value < 6 || value > 512)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "An audio bit rate in kilobits per second runs 6 to 512 for the codecs this library authors.");
            }

            bitrateKilobitsPerSecond = value;
        }
    }

    /// <summary>
    /// Vorbis's quality number, -1 (smallest) to 10 (best), or null to rate-control by bit rate instead.
    /// Ignored by Opus, which has no such knob.
    /// </summary>
    public double? VorbisQuality { get; set; }

    /// <summary>
    /// The sample rate in hertz. 48000 by default, which is Opus's own internal rate, so nothing is
    /// resampled on the way in from a source that was recorded there.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 8000 and 192000.</exception>
    public int SampleRateHz
    {
        get => sampleRateHz;
        set
        {
            if (value < 8000 || value > 192000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "An audio sample rate runs 8000 to 192000 Hz.");
            }

            sampleRateHz = value;
        }
    }

    /// <summary>The channel count. Two by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 8.</exception>
    public int Channels
    {
        get => channels;
        set
        {
            if (value < 1 || value > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A channel count runs 1 to 8.");
            }

            channels = value;
        }
    }

    /// <summary>A BCP 47 language tag for the audio track, or null.</summary>
    public string Language { get; set; }

    /// <summary>A name for the audio track, or null.</summary>
    public string Name { get; set; }
}
