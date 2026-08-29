using System;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the four ways a file reaches the readers - streamed, memory-mapped, preloaded and from a plain
/// stream - and the shared read helpers that sit on top of them.
/// </summary>
public class MediaSourceTests : IDisposable
{
    private readonly string directory = TestAssets.CreateTemporaryDirectory("sources");
    private readonly string filePath;
    private readonly byte[] content;

    public MediaSourceTests()
    {
        content = new byte[4096];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)(i * 7);

        filePath = Path.Combine(directory, "sample.bin");
        File.WriteAllBytes(filePath, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // A temporary folder that will not delete is not a test failure.
        }
    }

    [Fact]
    public void FileMediaSource_reads_forwards_and_at_an_offset()
    {
        //Arrange
        using FileMediaSource source = new FileMediaSource(filePath);
        byte[] head = new byte[16];
        byte[] middle = new byte[16];

        //Act
        source.ReadExactly(head, "the head");
        source.ReadExactlyAt(1000, middle, "the middle");

        //Assert
        source.Length.Should().Be(content.Length);
        source.CanSeek.Should().BeTrue();
        source.Position.Should().Be(16);
        head.Should().Equal(content.AsSpan(0, 16).ToArray());
        middle.Should().Equal(content.AsSpan(1000, 16).ToArray());
    }

    [Fact]
    public void MemoryMappedMediaSource_reads_the_same_bytes_as_the_file()
    {
        //Arrange
        using MemoryMappedMediaSource source = new MemoryMappedMediaSource(filePath);
        byte[] buffer = new byte[64];

        //Act
        source.ReadExactlyAt(2048, buffer, "a block");

        //Assert
        source.IsLengthKnown.Should().BeTrue();
        source.Length.Should().Be(content.Length);
        buffer.Should().Equal(content.AsSpan(2048, 64).ToArray());
    }

    [Fact]
    public void PreloadedClip_can_be_opened_many_times_over_the_same_bytes()
    {
        //Arrange
        using PreloadedClip clip = PreloadedClip.FromFile(filePath);

        //Act
        using IMediaSource first = clip.OpenSource();
        using IMediaSource second = clip.OpenSource();

        byte[] fromFirst = new byte[8];
        byte[] fromSecond = new byte[8];
        first.ReadExactly(fromFirst, "the first reader");
        second.ReadExactlyAt(100, fromSecond, "the second reader");

        //Assert
        clip.Length.Should().Be(content.Length);
        first.Position.Should().Be(8);
        second.Position.Should().Be(0);
        fromFirst.Should().Equal(content.AsSpan(0, 8).ToArray());
        fromSecond.Should().Equal(content.AsSpan(100, 8).ToArray());
    }

    [Fact]
    public void PreloadedClip_after_dispose_refuses_to_open_a_source()
    {
        //Arrange
        PreloadedClip clip = PreloadedClip.FromFile(filePath);
        clip.Dispose();

        //Act
        Action act = () => clip.OpenSource();

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void StreamMediaSource_over_a_forward_only_stream_reports_that_it_cannot_seek()
    {
        //Arrange
        using StreamMediaSource source = new StreamMediaSource(new ForwardOnlyStream(content), "forward-only");

        //Act
        byte[] buffer = new byte[32];
        source.ReadExactly(buffer, "the head");
        Action seek = () => source.Position = 0;

        //Assert
        source.CanSeek.Should().BeFalse();
        source.IsLengthKnown.Should().BeFalse();
        source.Length.Should().Be(-1);
        source.Position.Should().Be(32);
        seek.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void MemoryMediaSource_stops_at_the_end_rather_than_reading_past_it()
    {
        //Arrange
        using MemoryMediaSource source = new MemoryMediaSource(new byte[] { 1, 2, 3 }, "three bytes");
        byte[] buffer = new byte[8];

        //Act
        int read = source.Read(buffer);
        int again = source.Read(buffer);

        //Assert
        read.Should().Be(3);
        again.Should().Be(0);
    }

    [Fact]
    public void ReadExactly_says_what_was_being_read_when_a_source_ends_early()
    {
        //Arrange
        using MemoryMediaSource source = new MemoryMediaSource(new byte[] { 1, 2 }, "two bytes");
        byte[] buffer = new byte[8];

        //Act
        Action act = () => source.ReadExactly(buffer, "the EBML header");

        //Assert
        act.Should().Throw<VideoPlaybackException>().WithMessage("*the EBML header*");
    }

    [Fact]
    public void Skip_moves_a_forward_only_source_by_reading_and_discarding()
    {
        //Arrange
        using StreamMediaSource source = new StreamMediaSource(new ForwardOnlyStream(content), "forward-only");

        //Act
        bool skipped = source.Skip(1000);
        byte[] buffer = new byte[4];
        source.ReadExactly(buffer, "after the skip");

        //Assert
        skipped.Should().BeTrue();
        buffer.Should().Equal(content.AsSpan(1000, 4).ToArray());
    }

    [Fact]
    public void MediaSources_Open_picks_the_reader_the_mode_asks_for()
    {
        //Arrange & Act
        using IMediaSource streaming = MediaSources.Open(filePath);
        using IMediaSource mapped = MediaSources.Open(filePath, FileSourceMode.MemoryMapped);
        using IMediaSource preloaded = MediaSources.Open(filePath, FileSourceMode.Preloaded);

        //Assert
        streaming.Should().BeOfType<FileMediaSource>();
        mapped.Should().BeOfType<MemoryMappedMediaSource>();
        preloaded.Should().BeOfType<PreloadedMediaSource>();
    }

    [Fact]
    public void MediaSources_Open_reads_a_file_address()
    {
        //Arrange
        string url = new Uri(filePath).AbsoluteUri;

        //Act
        using IMediaSource source = MediaSources.Open(url);

        //Assert
        source.Length.Should().Be(content.Length);
    }

    [Fact]
    public void FileMediaSource_says_which_file_is_missing()
    {
        //Arrange
        string missing = Path.Combine(directory, "not-here.bin");

        //Act
        Action act = () => new FileMediaSource(missing);

        //Assert
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void PreloadedClip_FromBytes_copies_what_it_is_given()
    {
        //Arrange
        byte[] source = Encoding.UTF8.GetBytes("a short clip");

        //Act
        using PreloadedClip clip = PreloadedClip.FromBytes(source, "in memory");
        source[0] = 0;

        //Assert
        clip.Length.Should().Be(12);
        clip.Data.Span[0].Should().Be((byte)'a');
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream inner;

        public ForwardOnlyStream(byte[] data) => inner = new MemoryStream(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
