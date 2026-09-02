using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Containers.Ivf;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Writes IVF files and reads them back with this library's OWN <see cref="IvfReader" />, which is the only
/// statement worth making about a writer: what it puts down is what the reader takes up, byte for byte and
/// tick for tick.
/// </summary>
/// <remarks>
/// Nothing here starts FFmpeg. The golden corpus already holds an IVF an encoder wrote, so the strongest test
/// available - take a real file apart and put it back together - needs no encoder at all.
/// </remarks>
public class IvfWriterTests
{
    [Fact]
    public void A_written_file_round_trips_every_frame_through_the_reader()
    {
        //Arrange
        List<byte[]> payloads = new List<byte[]>();
        List<TimeSpan> timestamps = new List<TimeSpan>();

        for (int i = 0; i < 7; i++)
        {
            byte[] payload = new byte[13 + (i * 5)];
            for (int b = 0; b < payload.Length; b++) payload[b] = (byte)((i * 31) + b);
            payloads.Add(payload);
            timestamps.Add(TimeSpan.FromTicks(i * 416_667L));
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (IvfWriter writer = new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, 128, 72, true))
        {
            for (int i = 0; i < payloads.Count; i++) writer.WriteFrame(payloads[i], timestamps[i]);
            writer.Complete();
        }

        List<byte[]> readPayloads = new List<byte[]>();
        List<TimeSpan> readTimestamps = new List<TimeSpan>();
        List<long> readRaw = new List<long>();

        using IvfReader reader = new IvfReader(new MemoryMediaSource(buffer.ToArray(), "round-trip.ivf"));
        while (reader.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out long raw))
        {
            readPayloads.Add(data.ToArray());
            readTimestamps.Add(timestamp);
            readRaw.Add(raw);
        }

        //Assert
        readPayloads.Count.Should().Be(payloads.Count);
        for (int i = 0; i < payloads.Count; i++)
        {
            readPayloads[i].Should().Equal(payloads[i]);
            readTimestamps[i].Should().Be(timestamps[i]);
            readRaw[i].Should().Be(timestamps[i].Ticks);
        }
    }

    [Fact]
    public void The_golden_ivf_file_survives_being_taken_apart_and_put_back_together()
    {
        //Arrange
        string path = TestAssets.Path("av1-video-only.ivf");

        List<byte[]> original = new List<byte[]>();
        List<TimeSpan> originalTimes = new List<TimeSpan>();
        int width;
        int height;
        string fourCharacterCode;

        using (IvfReader source = new IvfReader(new FileMediaSource(path)))
        {
            width = source.Width;
            height = source.Height;
            fourCharacterCode = source.FourCharacterCode;

            while (source.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out long _))
            {
                original.Add(data.ToArray());
                originalTimes.Add(timestamp);
            }
        }

        MemoryStream buffer = new MemoryStream();

        //Act
        using (IvfWriter writer = new IvfWriter(buffer, fourCharacterCode, width, height, true))
        {
            for (int i = 0; i < original.Count; i++) writer.WriteFrame(original[i], originalTimes[i]);
            writer.Complete();
        }

        //Assert
        using IvfReader readBack = new IvfReader(new MemoryMediaSource(buffer.ToArray(), "rewritten.ivf"));
        readBack.FourCharacterCode.Should().Be(fourCharacterCode);
        readBack.Width.Should().Be(width);
        readBack.Height.Should().Be(height);
        readBack.DeclaredFrameCount.Should().Be((uint)original.Count);

        int index = 0;
        while (readBack.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan timestamp, out long _))
        {
            data.ToArray().Should().Equal(original[index]);
            timestamp.Should().Be(originalTimes[index]);
            index++;
        }

        index.Should().Be(original.Count);
    }

    [Fact]
    public void The_header_states_the_one_tick_time_base()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        using (IvfWriter writer = new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, 640, 360, true))
        {
            writer.WriteFrame(new byte[] { 1, 2, 3 }, TimeSpan.FromSeconds(1));
            writer.Complete();
        }

        //Assert
        using IvfReader reader = new IvfReader(new MemoryMediaSource(buffer.ToArray(), "timebase.ivf"));
        reader.TimeBaseNumerator.Should().Be(1u);
        reader.TimeBaseDenominator.Should().Be(IvfWriter.TickTimeBaseDenominator);
        reader.Width.Should().Be(640);
        reader.Height.Should().Be(360);

        reader.TryReadFrame(out ReadOnlyMemory<byte> _, out TimeSpan timestamp, out long raw).Should().BeTrue();
        raw.Should().Be(TimeSpan.TicksPerSecond);
        timestamp.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_frame_count_is_zero_until_the_file_is_completed()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        IvfWriter writer = new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, 128, 72, true);
        writer.WriteFrame(new byte[] { 9 }, TimeSpan.Zero);
        writer.WriteFrame(new byte[] { 8 }, TimeSpan.FromMilliseconds(100));

        uint beforeCompleting;
        using (IvfReader reader = new IvfReader(new MemoryMediaSource(buffer.ToArray(), "incomplete.ivf")))
        {
            beforeCompleting = reader.DeclaredFrameCount;
        }

        writer.Complete();
        writer.Dispose();

        //Assert
        beforeCompleting.Should().Be(0u);
        writer.FrameCount.Should().Be(2u);

        using IvfReader completed = new IvfReader(new MemoryMediaSource(buffer.ToArray(), "complete.ivf"));
        completed.DeclaredFrameCount.Should().Be(2u);
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused_with_a_reason()
    {
        //Arrange
        using Stream unseekable = new UnseekableStream();

        //Act
        Action act = () => new IvfWriter(unseekable, IvfWriter.Av1FourCharacterCode, 128, 72, true);

        //Assert
        act.Should().Throw<ArgumentException>().WithMessage("*seekable*");
    }

    [Theory]
    [InlineData("AV")]
    [InlineData("AV010")]
    [InlineData(null)]
    public void A_code_that_is_not_four_characters_is_refused(string code)
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        Action act = () => new IvfWriter(buffer, code, 128, 72, true);

        //Assert
        act.Should().Throw<ArgumentException>().WithMessage("*four*");
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(128, 0)]
    [InlineData(65536, 72)]
    [InlineData(128, 65536)]
    public void A_dimension_an_ivf_header_cannot_state_is_refused(int width, int height)
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();

        //Act
        Action act = () => new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, width, height, true);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_negative_timestamp_is_refused()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();
        using IvfWriter writer = new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, 128, 72, true);

        //Act
        Action act = () => writer.WriteFrame(new byte[] { 1 }, TimeSpan.FromTicks(-1));

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Nothing_more_goes_into_a_completed_file()
    {
        //Arrange
        MemoryStream buffer = new MemoryStream();
        using IvfWriter writer = new IvfWriter(buffer, IvfWriter.Av1FourCharacterCode, 128, 72, true);
        writer.WriteFrame(new byte[] { 1 }, TimeSpan.Zero);
        writer.Complete();

        //Act
        Action writeAgain = () => writer.WriteFrame(new byte[] { 2 }, TimeSpan.FromSeconds(1));
        Action completeAgain = () => writer.Complete();

        //Assert
        writeAgain.Should().Throw<InvalidOperationException>();
        completeAgain.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_file_created_by_path_is_an_av1_ivf_the_reader_accepts()
    {
        //Arrange
        string folder = TestAssets.CreateTemporaryDirectory("ivf-writer");
        string path = Path.Combine(folder, "written.ivf");

        try
        {
            //Act
            using (IvfWriter writer = IvfWriter.CreateAv1(path, 128, 72))
            {
                writer.WriteFrame(new byte[] { 4, 5, 6, 7 }, TimeSpan.Zero);
                writer.Complete();
            }

            //Assert
            using IvfReader reader = new IvfReader(new FileMediaSource(path));
            reader.FourCharacterCode.Should().Be("AV01");
            reader.DeclaredFrameCount.Should().Be(1u);
            reader.TryReadFrame(out ReadOnlyMemory<byte> data, out TimeSpan _, out long _).Should().BeTrue();
            data.ToArray().Should().Equal(new byte[] { 4, 5, 6, 7 });
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>A writable stream that refuses to seek, so the writer's own requirement can be tested.</summary>
    private sealed class UnseekableStream : Stream
    {
        private readonly MemoryStream inner = new MemoryStream();

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
