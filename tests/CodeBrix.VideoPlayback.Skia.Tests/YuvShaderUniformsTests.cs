using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Skia.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Skia.Tests;

/// <summary>
/// Checks the numbers the shader is handed: the sample scale, the sample offsets, the matrix rows, and -
/// the one that is easy to get backwards - where the chroma sits.
/// </summary>
public class YuvShaderUniformsTests
{
    [Theory]
    [InlineData(8, 255f)]
    [InlineData(10, 65535f)]
    [InlineData(12, 65535f)]
    public void The_plane_maximum_follows_the_texture_format_not_the_bit_depth(int bitDepth, float expected)
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers =
            YuvShaderUniforms.Create(color, bitDepth, VideoPixelLayout.I420, false);

        //Assert
        numbers.PlaneMaximum.Should().Be(expected);
    }

    [Fact]
    public void Studio_range_takes_sixteen_off_the_luma_and_a_hundred_and_twenty_eight_off_the_chroma()
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.I420, false);

        //Assert
        numbers.LumaOffset.Should().Be(16f);
        numbers.ChromaOffset.Should().Be(128f);
        numbers.RedRow[0].Should().BeApproximately(255f / 219f, 1e-5f);
    }

    [Fact]
    public void Full_range_takes_nothing_off_the_luma()
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Full);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.I420, false);

        //Assert
        numbers.LumaOffset.Should().Be(0f);
        numbers.ChromaOffset.Should().Be(128f);
        numbers.RedRow[0].Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void Ten_bit_studio_range_scales_every_offset_by_four()
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 10, VideoPixelLayout.I420, false);

        //Assert
        numbers.LumaOffset.Should().Be(64f);
        numbers.ChromaOffset.Should().Be(512f);
    }

    [Fact]
    public void The_BT_709_rows_carry_the_familiar_coefficients()
    {
        //Arrange
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            VideoColorRange.Full,
            VideoChromaSiting.Vertical);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.I420, false);

        //Assert - 2(1-Kr) = 1.5748, 2(1-Kb) = 1.8556, at full range the chroma scale is one
        numbers.RedRow[1].Should().Be(0f);
        numbers.RedRow[2].Should().BeApproximately(1.5748f, 1e-3f);
        numbers.BlueRow[1].Should().BeApproximately(1.8556f, 1e-3f);
        numbers.BlueRow[2].Should().Be(0f);
        numbers.GreenRow[1].Should().BeApproximately(-0.1873f, 1e-3f);
        numbers.GreenRow[2].Should().BeApproximately(-0.4681f, 1e-3f);
    }

    [Theory]
    [InlineData(VideoPixelLayout.I420, 1f, 1f)]
    [InlineData(VideoPixelLayout.I422, 1f, 0f)]
    [InlineData(VideoPixelLayout.I444, 0f, 0f)]
    [InlineData(VideoPixelLayout.Gray, 0f, 0f)]
    public void The_chroma_shift_follows_the_layout(VideoPixelLayout layout, float x, float y)
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, layout, layout == VideoPixelLayout.Gray);

        //Assert
        numbers.ChromaShiftX.Should().Be(x);
        numbers.ChromaShiftY.Should().Be(y);
    }

    [Theory]
    [InlineData(VideoChromaSiting.Colocated, 1f, 1f)]
    [InlineData(VideoChromaSiting.Vertical, 1f, 0f)]
    [InlineData(VideoChromaSiting.Interstitial, 0f, 0f)]
    public void Vertical_means_on_the_luma_COLUMN_and_between_the_luma_ROWS(
        VideoChromaSiting siting,
        float cositedX,
        float cositedY)
    {
        //Arrange
        VideoColorInfo color = Colour(siting, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.I420, false);

        //Assert
        numbers.ChromaCositedX.Should().Be(cositedX);
        numbers.ChromaCositedY.Should().Be(cositedY);
    }

    [Fact]
    public void A_monochrome_frame_zeroes_every_chroma_coefficient()
    {
        //Arrange
        VideoColorInfo color = Colour(VideoChromaSiting.Vertical, VideoColorRange.Limited);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.Gray, true);

        //Assert
        numbers.RedRow[1].Should().Be(0f);
        numbers.RedRow[2].Should().Be(0f);
        numbers.GreenRow[1].Should().Be(0f);
        numbers.GreenRow[2].Should().Be(0f);
        numbers.BlueRow[1].Should().Be(0f);
        numbers.BlueRow[2].Should().Be(0f);
        numbers.RedRow[0].Should().Be(numbers.BlueRow[0]);
    }

    [Fact]
    public void The_identity_matrix_permutes_the_planes_into_green_blue_and_red()
    {
        //Arrange
        VideoColorInfo color = new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Identity,
            VideoColorRange.Full,
            VideoChromaSiting.Colocated);

        //Act
        YuvShaderUniforms numbers = YuvShaderUniforms.Create(color, 8, VideoPixelLayout.I444, false);

        //Assert - the first plane carries green, the second blue, the third red
        numbers.GreenRow[0].Should().BeApproximately(1f, 1e-5f);
        numbers.BlueRow[1].Should().BeApproximately(1f, 1e-5f);
        numbers.RedRow[2].Should().BeApproximately(1f, 1e-5f);
        numbers.ChromaOffset.Should().Be(numbers.LumaOffset);
    }

    private static VideoColorInfo Colour(VideoChromaSiting siting, VideoColorRange range) =>
        new VideoColorInfo(
            VideoColorPrimaries.Bt709,
            VideoTransferCharacteristics.Bt709,
            VideoMatrixCoefficients.Bt709,
            range,
            siting);
}
