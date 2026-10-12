using CodeBrix.Audio.Wave;
using CodeBrix.VideoPlayback.Authoring.Export;
using CodeBrix.VideoPlayback.Containers;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// The tests that stop the process-wide shared audio output, and so must run with nothing else running beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedAudioOutputCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Shared audio output";
}

/// <summary>
/// An export decodes its source's own sound without starting the shared audio output - so without opening an audio
/// device, which a headless host or a background service does not have. Each test stops the shared output first
/// (another test in this process plays a session with sound) and checks it is still stopped afterwards.
/// </summary>
[Collection(SharedAudioOutputCollection.Name)]
public class ExportWithoutAudioDeviceTests
{
    [Fact]
    public void A_mode2_file_exports_its_vorbis_sound_without_starting_the_shared_output()
    {
        //Arrange
        ExportTestMedia.Require();
        SharedAudioOutput.Shutdown();
        using WorkFolder work = new WorkFolder("export-no-device-mode2");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = ExportTestMedia.RepositoryAsset("av1-vorbis.cbv"),
            OutputPath = work.File("mode2.mp4"),
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        SharedAudioOutput.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void A_mode3_master_exports_its_flac_sound_without_starting_the_shared_output()
    {
        //Arrange
        ExportTestMedia.Require();
        SharedAudioOutput.Shutdown();
        using WorkFolder work = new WorkFolder("export-no-device-mode3");
        VideoExportRequest request = new VideoExportRequest
        {
            SourcePath = AuthoringTestAssets.Path_("av1-flac.cbvmaster"),
            OutputPath = work.File("mode3.mp4"),
        };

        //Act
        VideoExportResult result = VideoExporter.Run(request);

        //Assert
        result.AudioSource.Should().Be(VideoExportAudioSource.Ours);
        SharedAudioOutput.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void A_codec_nothing_serves_is_refused_by_name_without_starting_the_shared_output()
    {
        //Arrange
        SharedAudioOutput.Shutdown();
        MediaTrackInfo track = new MediaTrackInfo { Kind = MediaTrackKind.Audio, CodecId = "aac" };

        //Act
        VideoAuthoringException thrown = Assert.Throws<VideoAuthoringException>(() => ExportDecoding.CreateAudioDecoder(track));

        //Assert
        thrown.Message.Should().Contain("'aac'");
        SharedAudioOutput.IsRunning.Should().BeFalse();
    }
}
