using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class LutPanelPolicyTests
{
    [Theory]
    //  backend                             transport                          editable
    [InlineData(VideoRenderBackendOption.Gpu, VideoTransportState.Stopped, true)]
    [InlineData(VideoRenderBackendOption.Gpu, VideoTransportState.Paused, true)]
    [InlineData(VideoRenderBackendOption.Gpu, VideoTransportState.Playing, false)]
    [InlineData(VideoRenderBackendOption.Cpu, VideoTransportState.Stopped, false)]
    [InlineData(VideoRenderBackendOption.Cpu, VideoTransportState.Paused, false)]
    [InlineData(VideoRenderBackendOption.Cpu, VideoTransportState.Playing, false)]
    public void IsEditable_is_the_whole_matrix(
        VideoRenderBackendOption backend,
        VideoTransportState transport,
        bool expected)
    {
        //Act
        var editable = LutPanelPolicy.IsEditable(backend, transport);

        //Assert
        editable.Should().Be(expected);
    }

    [Fact]
    public void the_processor_note_wins_over_the_playing_note()
    {
        //Assert
        LutPanelPolicy.GetNote(VideoRenderBackendOption.Cpu, VideoTransportState.Playing)
            .Should().Be(LutPanelPolicy.CpuNote);
        LutPanelPolicy.GetNote(VideoRenderBackendOption.Cpu, VideoTransportState.Paused)
            .Should().Be(LutPanelPolicy.CpuNote);
    }

    [Fact]
    public void the_playing_note_explains_a_panel_locked_by_the_transport()
    {
        //Act
        var note = LutPanelPolicy.GetNote(VideoRenderBackendOption.Gpu, VideoTransportState.Playing);

        //Assert
        note.Should().Be(LutPanelPolicy.PlayingNote);
        note.Should().Contain("Play");
    }

    [Fact]
    public void an_editable_panel_has_nothing_to_explain()
    {
        //Assert
        LutPanelPolicy.GetNote(VideoRenderBackendOption.Gpu, VideoTransportState.Paused)
            .Should().Be(string.Empty);
        LutPanelPolicy.GetNote(VideoRenderBackendOption.Gpu, VideoTransportState.Stopped)
            .Should().Be(string.Empty);
    }

    [Theory]
    //  applied  transport                          effects  can bake
    [InlineData(2, VideoTransportState.Playing, true, true)]
    [InlineData(2, VideoTransportState.Paused, true, true)]
    [InlineData(2, VideoTransportState.Stopped, true, false)]
    [InlineData(0, VideoTransportState.Playing, true, false)]
    [InlineData(2, VideoTransportState.Playing, false, false)]
    [InlineData(0, VideoTransportState.Stopped, false, false)]
    public void CanBake_needs_a_chain_that_is_on_screen(
        int appliedTableCount,
        VideoTransportState transport,
        bool effectsActive,
        bool expected)
    {
        //Act
        var canBake = LutPanelPolicy.CanBake(appliedTableCount, transport, effectsActive);

        //Assert
        canBake.Should().Be(expected);
    }
}
