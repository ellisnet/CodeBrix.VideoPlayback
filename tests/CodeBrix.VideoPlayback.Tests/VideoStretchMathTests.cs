using CodeBrix.VideoPlayback.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the letterbox arithmetic every presenter in the family fits a frame into a host rectangle with.
/// </summary>
/// <remarks>
/// These are the same cases the Skia presenter's own geometry tests make, one layer down: the presenter's
/// <c>ComputeDestinationRect</c> is a two-line conversion around this, so proving the arithmetic here proves
/// it for every presenter that will ever call it.
/// </remarks>
public class VideoStretchMathTests
{
    [Fact]
    public void Fill_stretches_the_picture_over_the_whole_destination()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(10f, 20f, 200f, 100f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 64, 36, VideoStretch.Fill);

        //Assert
        target.Should().Be(destination);
    }

    [Fact]
    public void Uniform_letterboxes_a_wide_picture_in_a_square_destination()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(100f, 100f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 200, 100, VideoStretch.Uniform);

        //Assert
        target.Width.Should().Be(100f);
        target.Height.Should().Be(50f);
        target.Left.Should().Be(0f);
        target.Top.Should().Be(25f);
    }

    [Fact]
    public void Uniform_pillarboxes_a_tall_picture_in_a_wide_destination()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(400f, 100f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 100, 200, VideoStretch.Uniform);

        //Assert
        target.Width.Should().Be(50f);
        target.Height.Should().Be(100f);
        target.Left.Should().Be(175f);
        target.Top.Should().Be(0f);
    }

    [Fact]
    public void UniformToFill_covers_the_destination_and_overflows_the_long_way()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(100f, 100f);

        //Act
        VideoRectangle target =
            VideoStretchMath.ComputeDestination(destination, 200, 100, VideoStretch.UniformToFill);

        //Assert
        target.Width.Should().Be(200f);
        target.Height.Should().Be(100f);
        target.Left.Should().Be(-50f);
        target.Top.Should().Be(0f);
    }

    [Fact]
    public void None_centres_the_picture_at_its_own_size()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(100f, 100f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 40, 20, VideoStretch.None);

        //Assert
        target.Should().Be(VideoRectangle.Create(30f, 40f, 40f, 20f));
    }

    [Fact]
    public void The_letterbox_follows_the_size_it_is_handed_whatever_the_coded_size_was()
    {
        //Arrange - an anamorphic frame: 100 coded pixels wide, meant to be shown 200 wide
        VideoRectangle destination = VideoRectangle.Create(400f, 400f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 200, 100, VideoStretch.Uniform);

        //Assert
        (target.Width / target.Height).Should().Be(2f);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-4, 8)]
    public void A_picture_with_no_size_falls_back_to_the_whole_destination(int width, int height)
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(100f, 50f);

        //Act
        VideoRectangle target =
            VideoStretchMath.ComputeDestination(destination, width, height, VideoStretch.Uniform);

        //Assert
        target.Should().Be(destination);
    }

    [Fact]
    public void A_destination_away_from_the_origin_keeps_the_picture_centred_in_it()
    {
        //Arrange
        VideoRectangle destination = VideoRectangle.Create(1000f, 500f, 100f, 100f);

        //Act
        VideoRectangle target = VideoStretchMath.ComputeDestination(destination, 200, 100, VideoStretch.Uniform);

        //Assert
        target.Should().Be(VideoRectangle.Create(1000f, 525f, 100f, 50f));
    }
}
