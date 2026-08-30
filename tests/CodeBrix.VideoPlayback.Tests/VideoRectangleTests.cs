using CodeBrix.VideoPlayback.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the drawing-free rectangle the presenters and their layers say geometry in.
/// </summary>
public class VideoRectangleTests
{
    [Fact]
    public void Four_edges_go_in_and_a_width_and_a_height_come_out()
    {
        //Arrange & Act
        VideoRectangle rectangle = new VideoRectangle(10f, 20f, 110f, 70f);

        //Assert
        rectangle.Left.Should().Be(10f);
        rectangle.Top.Should().Be(20f);
        rectangle.Right.Should().Be(110f);
        rectangle.Bottom.Should().Be(70f);
        rectangle.Width.Should().Be(100f);
        rectangle.Height.Should().Be(50f);
        rectangle.MidX.Should().Be(60f);
        rectangle.MidY.Should().Be(45f);
    }

    [Fact]
    public void Create_takes_a_corner_and_a_size_and_works_out_the_far_edges()
    {
        //Act
        VideoRectangle placed = VideoRectangle.Create(10f, 20f, 100f, 50f);
        VideoRectangle atOrigin = VideoRectangle.Create(100f, 50f);

        //Assert
        placed.Should().Be(new VideoRectangle(10f, 20f, 110f, 70f));
        atOrigin.Should().Be(new VideoRectangle(0f, 0f, 100f, 50f));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, true)]
    [InlineData(0f, 0f, 100f, 0f, true)]
    [InlineData(0f, 0f, 0f, 50f, true)]
    [InlineData(0f, 0f, 100f, -1f, true)]
    [InlineData(100f, 0f, 0f, 50f, true)]
    [InlineData(0f, 0f, 1f, 1f, false)]
    public void A_rectangle_covering_no_area_is_empty(
        float left,
        float top,
        float right,
        float bottom,
        bool expected)
    {
        //Act
        VideoRectangle rectangle = new VideoRectangle(left, top, right, bottom);

        //Assert
        rectangle.IsEmpty.Should().Be(expected);
    }

    [Fact]
    public void The_empty_rectangle_is_the_one_at_the_origin_with_no_size()
    {
        //Assert
        VideoRectangle.Empty.Should().Be(new VideoRectangle(0f, 0f, 0f, 0f));
        VideoRectangle.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Two_rectangles_with_the_same_edges_are_equal_and_hash_alike()
    {
        //Arrange
        VideoRectangle first = new VideoRectangle(1f, 2f, 3f, 4f);
        VideoRectangle same = VideoRectangle.Create(1f, 2f, 2f, 2f);
        VideoRectangle other = new VideoRectangle(1f, 2f, 3f, 5f);

        //Assert
        (first == same).Should().BeTrue();
        (first != same).Should().BeFalse();
        first.Equals(same).Should().BeTrue();
        first.Equals((object)same).Should().BeTrue();
        first.GetHashCode().Should().Be(same.GetHashCode());

        (first == other).Should().BeFalse();
        (first != other).Should().BeTrue();
        first.Equals((object)"not a rectangle").Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_normalised_so_a_backwards_rectangle_keeps_its_shape()
    {
        //Arrange - a right edge left of the left edge is kept, and reported empty
        VideoRectangle backwards = new VideoRectangle(100f, 0f, 10f, 50f);

        //Assert
        backwards.Right.Should().Be(10f);
        backwards.Width.Should().Be(-90f);
        backwards.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void The_text_form_names_the_corner_and_the_size()
    {
        //Act
        string text = VideoRectangle.Create(10.5f, 20f, 100f, 50f).ToString();

        //Assert
        text.Should().Be("(10.5, 20) 100x50");
    }
}
