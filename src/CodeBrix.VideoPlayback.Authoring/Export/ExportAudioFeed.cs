using System;
using CodeBrix.Audio.Engine.Interfaces;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoProcessing.Core;
using CodeBrix.VideoProcessing.Core.Frames;
using CodeBrix.VideoProcessing.Core.Output;

namespace CodeBrix.VideoPlayback.Authoring.Export;

/// <summary>
/// The sound an export writes, fed to the writer in step with the pictures: the source's own decoded packets, or
/// a backdrop video's sound read through the engine. Silence fills whatever the chosen sound does not cover.
/// </summary>
internal abstract class ExportAudioFeed : IDisposable
{
    /// <summary>The lowest rate the AAC writer takes.</summary>
    internal const int LowestWriterRate = 8000;

    /// <summary>The highest rate the AAC writer takes.</summary>
    internal const int HighestWriterRate = 96000;

    /// <summary>The rate a sound outside the writer's range is resampled to.</summary>
    internal const int FallbackRate = 48000;

    /// <summary>The most channels the AAC writer takes.</summary>
    internal const int MaximumChannels = 8;

    private const int ChunkFrames = 4096;

    private float[] silence;

    /// <summary>The rate the writer is fed at.</summary>
    public int SampleRate { get; protected set; }

    /// <summary>The channel count the writer is fed with.</summary>
    public int Channels { get; protected set; }

    /// <summary>Frames per channel handed to the writer so far, silence included.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Frames per channel of real sound left unwritten because the picture ended first.</summary>
    public long FramesCut { get; protected set; }

    /// <summary>Frames per channel of silence written after the chosen sound ran out.</summary>
    public long SilenceFrames { get; private set; }

    /// <summary>Writes as much sound as is ready, up to a total of <paramref name="targetFrames" />.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="targetFrames">The total frames per channel the picture so far covers.</param>
    public abstract void Pump(IStandardVideoWriter writer, long targetFrames);

    /// <summary>Writes the rest - real sound, then silence - to exactly <paramref name="totalFrames" />.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="totalFrames">The total frames per channel the whole picture covers.</param>
    public abstract void Finish(IStandardVideoWriter writer, long totalFrames);

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }

    /// <summary>Hands interleaved samples to the writer and counts them.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="interleaved">Whole frames.</param>
    protected void Write(IStandardVideoWriter writer, ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return;
        writer.WriteAudio(interleaved);
        FramesWritten += interleaved.Length / Channels;
    }

    /// <summary>Writes silence up to a total.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="totalFrames">The total frames per channel to reach.</param>
    protected void PadTo(IStandardVideoWriter writer, long totalFrames)
    {
        silence ??= new float[ChunkFrames * Channels];
        while (FramesWritten < totalFrames)
        {
            int frames = (int)Math.Min(ChunkFrames, totalFrames - FramesWritten);
            SilenceFrames += frames;
            Write(writer, silence.AsSpan(0, frames * Channels));
        }
    }

    /// <summary>
    /// The source's own sound, decoded packet by packet with a decoder from the shared audio output's packet codecs,
    /// made without starting the shared output.
    /// </summary>
    internal sealed class Ours : ExportAudioFeed
    {
        private readonly IPacketSoundDecoder decoder;
        private readonly float[] decoded;
        private readonly int decoderRate;
        private readonly int trackTrimFrames;
        private readonly LinearResampler resampler;
        private readonly int holdBackFrames;
        private float[] queue = new float[8192];
        private int queueStart;
        private int queueCount;
        private int preSkipLeft;
        private TimeSpan lastDiscardPadding;

        /// <summary>Creates the feed.</summary>
        /// <param name="decoder">The decoder for the track.</param>
        /// <param name="track">The audio track.</param>
        public Ours(IPacketSoundDecoder decoder, MediaTrackInfo track)
        {
            this.decoder = decoder;
            decoderRate = decoder.SampleRate > 0 ? decoder.SampleRate : track.SampleRate;
            Channels = decoder.Channels > 0 ? decoder.Channels : track.Channels;
            if (Channels < 1 || Channels > MaximumChannels)
            {
                throw new VideoAuthoringException(
                    "The source's sound has " + Channels + " channel(s); an MP4 export's AAC track takes 1 to "
                    + MaximumChannels + ". Set IncludeAudio to false to export the picture alone.");
            }

            if (decoderRate < LowestWriterRate || decoderRate > HighestWriterRate)
            {
                resampler = new LinearResampler(decoderRate, FallbackRate, Channels);
                SampleRate = FallbackRate;
            }
            else
            {
                SampleRate = decoderRate;
            }

            decoded = new float[Math.Max(decoder.MaxSamplesPerPacket, Channels)];
            preSkipLeft = Math.Max(0, decoder.PreSkipSamples);
            trackTrimFrames = Math.Max(0, track.TrailingTrimSamples);

            // The end trim of a Matroska track only arrives on its last packet, so the most it can be - one packet
            // - is held back until the end, when it is known.
            holdBackFrames = (int)((long)Math.Max(decoded.Length / Channels, trackTrimFrames) * SampleRate / decoderRate) + 1;
        }

        /// <summary>True when the decoder's rate is outside the writer's range and the sound is resampled.</summary>
        public bool IsResampled => resampler != null;

        /// <summary>The rate the decoder delivers.</summary>
        public int DecoderRate => decoderRate;

        /// <summary>Decodes one packet into the queue.</summary>
        /// <param name="packet">The packet.</param>
        public void Decode(in MediaPacket packet)
        {
            int samples = decoder.DecodePacket(packet.Data.Span, decoded);
            if (packet.DiscardPadding > TimeSpan.Zero) lastDiscardPadding = packet.DiscardPadding;
            else if (!packet.IsEmpty) lastDiscardPadding = TimeSpan.Zero;
            if (samples <= 0) return;

            Span<float> span = decoded.AsSpan(0, samples - (samples % Channels));
            int frames = span.Length / Channels;
            if (preSkipLeft > 0)
            {
                int skip = Math.Min(preSkipLeft, frames);
                preSkipLeft -= skip;
                span = span.Slice(skip * Channels);
            }

            if (span.IsEmpty) return;
            if (resampler != null) resampler.Process(span, this);
            else Enqueue(span);
        }

        /// <summary>Adds samples to the queue.</summary>
        /// <param name="samples">Whole interleaved frames.</param>
        internal void Enqueue(ReadOnlySpan<float> samples)
        {
            if (queueStart + queueCount + samples.Length > queue.Length)
            {
                if (queueCount + samples.Length <= queue.Length)
                {
                    Array.Copy(queue, queueStart, queue, 0, queueCount);
                }
                else
                {
                    float[] larger = new float[Math.Max(queue.Length * 2, queueCount + samples.Length)];
                    Array.Copy(queue, queueStart, larger, 0, queueCount);
                    queue = larger;
                }

                queueStart = 0;
            }

            samples.CopyTo(queue.AsSpan(queueStart + queueCount));
            queueCount += samples.Length;
        }

        /// <inheritdoc />
        public override void Pump(IStandardVideoWriter writer, long targetFrames)
        {
            long ready = queueCount / Channels - holdBackFrames;
            long frames = Math.Min(ready, targetFrames - FramesWritten);
            if (frames > 0) Dequeue(writer, (int)frames);
        }

        /// <inheritdoc />
        public override void Finish(IStandardVideoWriter writer, long totalFrames)
        {
            resampler?.Flush(this);

            long trimSource = Math.Max(trackTrimFrames, (long)Math.Round(lastDiscardPadding.TotalSeconds * decoderRate));
            long trim = trimSource * SampleRate / decoderRate;
            long available = Math.Max(0, queueCount / Channels - trim);
            long frames = Math.Min(available, Math.Max(0, totalFrames - FramesWritten));
            if (frames > 0) Dequeue(writer, (int)frames);
            FramesCut = available - frames;
            PadTo(writer, totalFrames);
        }

        /// <inheritdoc />
        public override void Dispose() => (decoder as IDisposable)?.Dispose();

        private void Dequeue(IStandardVideoWriter writer, int frames)
        {
            int count = frames * Channels;
            Write(writer, queue.AsSpan(queueStart, count));
            queueStart += count;
            queueCount -= count;
            if (queueCount == 0) queueStart = 0;
        }
    }

    /// <summary>A backdrop video's sound, read through the engine.</summary>
    internal sealed class Backdrop : ExportAudioFeed
    {
        private readonly IAudioSampleSource source;
        private readonly float[] buffer;
        private bool ended;

        /// <summary>Opens the backdrop's sound.</summary>
        /// <param name="engine">The engine.</param>
        /// <param name="path">The backdrop video.</param>
        public Backdrop(IVideoProcessor engine, string path)
        {
            source = engine.OpenAudio(path, new AudioSourceOptions());
            AudioSourceInfo info = source.Info;
            if (info.SampleRate < LowestWriterRate || info.SampleRate > HighestWriterRate
                || info.Channels < 1 || info.Channels > MaximumChannels)
            {
                source.Dispose();
                source = engine.OpenAudio(path, new AudioSourceOptions
                {
                    SampleRate = info.SampleRate < LowestWriterRate || info.SampleRate > HighestWriterRate ? FallbackRate : 0,
                    Channels = info.Channels < 1 || info.Channels > MaximumChannels ? 2 : 0,
                });
                info = source.Info;
            }

            SampleRate = info.SampleRate;
            Channels = info.Channels;
            buffer = new float[ChunkFrames * Channels];
        }

        /// <inheritdoc />
        public override void Pump(IStandardVideoWriter writer, long targetFrames)
        {
            while (!ended && FramesWritten < targetFrames)
            {
                int want = (int)Math.Min(ChunkFrames, targetFrames - FramesWritten);
                int read = source.Read(buffer.AsSpan(0, want * Channels));
                if (read <= 0)
                {
                    ended = true;
                    break;
                }

                Write(writer, buffer.AsSpan(0, read * Channels));
            }
        }

        /// <inheritdoc />
        public override void Finish(IStandardVideoWriter writer, long totalFrames)
        {
            Pump(writer, totalFrames);
            if (!ended && source.Read(buffer.AsSpan(0, Channels)) > 0) FramesCut = 1;
            PadTo(writer, totalFrames);
        }

        /// <inheritdoc />
        public override void Dispose() => source.Dispose();
    }

    /// <summary>
    /// Linear interpolation from one rate to another, streaming - used only for a decoded sound whose rate the AAC
    /// writer does not take.
    /// </summary>
    internal sealed class LinearResampler
    {
        private readonly int channels;
        private readonly double step;
        private readonly float[] previous;
        private float[] output = new float[4096];
        private double position;
        private bool hasPrevious;

        /// <summary>Creates the resampler.</summary>
        /// <param name="fromRate">The input rate.</param>
        /// <param name="toRate">The output rate.</param>
        /// <param name="channels">The channel count.</param>
        public LinearResampler(int fromRate, int toRate, int channels)
        {
            this.channels = channels;
            step = (double)fromRate / toRate;
            previous = new float[channels];
        }

        /// <summary>Resamples a chunk and queues the result.</summary>
        /// <param name="input">Whole interleaved frames at the input rate.</param>
        /// <param name="target">The queue.</param>
        public void Process(ReadOnlySpan<float> input, Ours target)
        {
            int frames = input.Length / channels;
            if (frames == 0) return;

            // Position counts input frames from the previous chunk's last frame (index -1) when there is one.
            int estimate = (int)((frames + 1) / step) + 2;
            if (output.Length < estimate * channels) output = new float[estimate * channels];

            int written = 0;
            int origin = hasPrevious ? -1 : 0;
            while (true)
            {
                double at = position + origin;
                int left = (int)Math.Floor(at);
                if (left + 1 > frames - 1) break;

                double fraction = at - left;
                for (int c = 0; c < channels; c++)
                {
                    float a = left < 0 ? previous[c] : input[(left * channels) + c];
                    float b = input[((left + 1) * channels) + c];
                    output[(written * channels) + c] = (float)(a + ((b - a) * fraction));
                }

                written++;
                position += step;
            }

            // Carry the position over so it counts from this chunk's last frame next time.
            position -= frames - 1 - origin;
            if (position < 0) position = 0;
            input.Slice((frames - 1) * channels, channels).CopyTo(previous);
            hasPrevious = true;
            target.Enqueue(output.AsSpan(0, written * channels));
        }

        /// <summary>Emits the last input frame, so the tail is not lost.</summary>
        /// <param name="target">The queue.</param>
        public void Flush(Ours target)
        {
            if (!hasPrevious) return;
            target.Enqueue(previous);
            hasPrevious = false;
        }
    }
}
