using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;
using SkiaSharp;
using System.IO;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class ImageComparisonTests
{
    [Fact]
    public void Compare_of_a_picture_with_itself_finds_no_difference()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var first = WritePicture(temp, "first.png", 40, 80, 120);
        var second = WritePicture(temp, "second.png", 40, 80, 120);

        //Act
        ImageComparisonResult comparison = ImageComparison.Compare(first, second);

        //Assert
        comparison.SizesMatch.Should().BeTrue();
        comparison.MaxChannelDelta.Should().Be(0);
        comparison.MeanAbsoluteDelta.Should().Be(0);
        comparison.DifferingPixelPercent.Should().Be(0);
    }

    [Fact]
    public void Compare_measures_how_far_apart_two_pictures_are()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var first = WritePicture(temp, "first.png", 40, 80, 120);
        var second = WritePicture(temp, "second.png", 43, 80, 120);

        //Act
        ImageComparisonResult comparison = ImageComparison.Compare(first, second);

        //Assert
        comparison.MaxChannelDelta.Should().Be(3);
        comparison.DifferingPixelPercent.Should().Be(100);
        comparison.MeanAbsoluteDelta.Should().Be(1);
    }

    [Fact]
    public void Compare_says_so_when_the_pictures_are_not_the_same_size()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var first = WritePicture(temp, "first.png", 10, 10, 10, 8, 8);
        var second = WritePicture(temp, "second.png", 10, 10, 10, 16, 16);

        //Act
        ImageComparisonResult comparison = ImageComparison.Compare(first, second);

        //Assert
        comparison.SizesMatch.Should().BeFalse();
        comparison.MaxChannelDelta.Should().Be(255);
    }

    [Fact]
    public void Compare_of_a_file_that_is_not_there_is_null_rather_than_a_failure()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var first = WritePicture(temp, "first.png", 1, 2, 3);

        //Act and assert
        ImageComparison.Compare(first, Path.Combine(temp.Path, "missing.png")).Should().BeNull();
        ImageComparison.Compare(Path.Combine(temp.Path, "missing.png"), first).Should().BeNull();
    }

    private static string WritePicture(
        TempFolder temp,
        string fileName,
        byte red,
        byte green,
        byte blue,
        int width = 8,
        int height = 8)
    {
        var path = Path.Combine(temp.Path, fileName);

        using SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new SKCanvas(bitmap)) { canvas.Clear(new SKColor(red, green, blue)); }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());

        return path;
    }
}
