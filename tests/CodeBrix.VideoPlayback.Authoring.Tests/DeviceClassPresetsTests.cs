using System;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Authoring.Presets;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>Checks the device-class table and what applying one of its rows does to a request.</summary>
public class DeviceClassPresetsTests
{
    [Fact]
    public void The_table_carries_three_rungs_biggest_first()
    {
        //Act
        //Assert
        DeviceClassPresets.All.Count.Should().Be(3);
        DeviceClassPresets.All[0].LongSidePixels.Should().Be(3840);
        DeviceClassPresets.All[1].LongSidePixels.Should().Be(1920);
        DeviceClassPresets.All[2].LongSidePixels.Should().Be(1280);
    }

    [Fact]
    public void The_preset_gets_faster_as_the_frame_gets_bigger()
    {
        //Act
        //Assert
        DeviceClassPresets.Desktop4K.SpeedPreset.Should().Be(6);
        DeviceClassPresets.Pi1080p.SpeedPreset.Should().Be(5);
        DeviceClassPresets.RiscV720p.SpeedPreset.Should().Be(4);
    }

    [Fact]
    public void The_rate_factor_gets_lower_as_the_frame_gets_smaller()
    {
        //Act
        //Assert
        DeviceClassPresets.Desktop4K.ConstantRateFactor.Should().Be(28);
        DeviceClassPresets.Pi1080p.ConstantRateFactor.Should().Be(26);
        DeviceClassPresets.RiscV720p.ConstantRateFactor.Should().Be(24);
    }

    [Fact]
    public void The_audio_shrinks_with_the_picture_on_the_smallest_rung()
    {
        //Act
        //Assert
        DeviceClassPresets.Desktop4K.AudioKilobitsPerSecond.Should().Be(128);
        DeviceClassPresets.Pi1080p.AudioKilobitsPerSecond.Should().Be(128);
        DeviceClassPresets.RiscV720p.AudioKilobitsPerSecond.Should().Be(96);
    }

    [Fact]
    public void ApplyTo_writes_the_four_numbers_and_leaves_everything_else_alone()
    {
        //Arrange
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            SourcePath = "/clips/in.mkv",
            OutputPath = "/out/clip.cbv",
        };

        request.Video.KeyframeIntervalFrames = 48;

        //Act
        DeviceClassPresets.Pi1080p.ApplyTo(request);

        //Assert
        request.Video.FrameSize.Kind.Should().Be(AuthoringFrameSizeKind.LongSide);
        request.Video.FrameSize.Pixels.Should().Be(1920);
        request.Video.SpeedPreset.Should().Be(5);
        request.Video.ConstantRateFactor.Should().Be(26);
        request.Audio.BitrateKilobitsPerSecond.Should().Be(128);
        request.Video.KeyframeIntervalFrames.Should().Be(48);
    }

    [Fact]
    public void ApplyTo_can_be_overridden_afterwards()
    {
        //Arrange
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            SourcePath = "/clips/in.mkv",
            OutputPath = "/out/clip.cbv",
        };

        //Act
        DeviceClassPresets.Desktop4K.ApplyTo(request);
        request.Video.ConstantRateFactor = 20;

        //Assert
        request.Video.ConstantRateFactor.Should().Be(20);
    }

    [Fact]
    public void For_finds_a_preset_by_its_device_class()
    {
        //Act
        //Assert
        DeviceClassPresets.For(DeviceClass.RiscV720p).Should().BeSameAs(DeviceClassPresets.RiscV720p);
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceClassPresets.For((DeviceClass)99));
    }
}
