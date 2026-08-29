using CodeBrix.VideoPlayback.Decoding;
using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class SkiaVideoRuntimeTests
{
    [Fact]
    public void Initialize_registers_the_av1_decoder_and_says_so()
    {
        //Act
        var initialized = SkiaVideoRuntime.Initialize();
        var again = SkiaVideoRuntime.Initialize();

        //Assert
        initialized.Should().BeTrue();
        again.Should().BeTrue();
        SkiaVideoRuntime.ErrorMessage.Should().Be(string.Empty);
        SkiaVideoRuntime.Summary.Should().Contain("dav1d");
        VideoDecoders.IsCodecSupported(VideoCodecIds.Av1).Should().BeTrue();
    }
}
