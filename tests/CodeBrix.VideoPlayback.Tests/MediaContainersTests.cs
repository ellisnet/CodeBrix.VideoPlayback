using System;
using System.IO;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the one sniff that decides which container reader a file gets.
/// </summary>
/// <remarks>
/// The EXTENSION is never consulted, which is the whole reason the two <c>.cbv</c> flavours can share one
/// name: a WebM-profile <c>.cbv</c> begins with EBML's signature and is opened by the Matroska reader, and a
/// bespoke one begins with <c>CBVF</c> and is opened by the other.
/// </remarks>
public class MediaContainersTests
{
    [Fact]
    public void A_webm_file_opens_with_the_matroska_reader()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(path);

        //Assert
        reader.Should().BeOfType<MatroskaReader>();
        reader.FormatName.Should().Be("Matroska/WebM");
    }

    [Fact]
    public void A_bespoke_file_opens_with_the_cbv_reader()
    {
        //Arrange
        string path = TestAssets.Path("av1-vorbis.cbv");

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(path);

        //Assert
        reader.Should().BeOfType<CbvReader>();
        reader.FormatName.Should().Be("CodeBrix Video (.cbv)");
    }

    [Fact]
    public void The_reader_owns_the_source_it_opened_by_default()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");

        //Act
        IMediaContainerReader reader = MediaContainers.Open(path);
        reader.Dispose();

        //Assert
        // Disposing twice is harmless; what matters is that the file handle is gone, which the next open
        // proves on every platform this family runs on.
        using IMediaContainerReader again = MediaContainers.Open(path);
        again.Tracks.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_source_may_be_left_open_when_the_caller_still_owns_it()
    {
        //Arrange
        string path = TestAssets.Path("av1-opus.webm");
        using IMediaSource source = new FileMediaSource(path);

        //Act
        using (IMediaContainerReader reader = MediaContainers.Open(source, true))
        {
            reader.Tracks.Count.Should().BeGreaterThan(0);
        }

        //Assert
        source.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_file_that_is_neither_container_is_refused_by_name()
    {
        //Arrange
        string path = Path.Combine(Path.GetTempPath(), "not-a-container-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });

        try
        {
            //Act
            VideoPlaybackException failure = Assert.Throws<VideoPlaybackException>(() => MediaContainers.Open(path));

            //Assert
            failure.Message.Should().Contain("DE AD BE EF");
            failure.Message.Should().Contain("1A 45 DF A3");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_too_short_to_hold_a_header_is_refused_by_length()
    {
        //Arrange
        string path = Path.Combine(Path.GetTempPath(), "too-short-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 1, 2 });

        try
        {
            //Act
            VideoPlaybackException failure = Assert.Throws<VideoPlaybackException>(() => MediaContainers.Open(path));

            //Assert
            failure.Message.Should().Contain("2 bytes long");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_needs_a_source()
    {
        //Act
        //Assert
        Assert.Throws<ArgumentNullException>(() => MediaContainers.Open((IMediaSource)null));
    }
}
