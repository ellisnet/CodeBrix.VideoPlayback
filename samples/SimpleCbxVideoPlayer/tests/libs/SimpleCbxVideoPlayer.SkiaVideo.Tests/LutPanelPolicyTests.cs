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
    //  ticked  backend                             transport                          can bake
    [InlineData(2, VideoRenderBackendOption.Gpu, VideoTransportState.Paused, true)]
    //  Stopped covers a video that was stopped AND one that played to its end: both are bakeable
    [InlineData(2, VideoRenderBackendOption.Gpu, VideoTransportState.Stopped, true)]
    //  Ticked but never played - the whole point: a bake owes nothing to what is on screen
    [InlineData(1, VideoRenderBackendOption.Gpu, VideoTransportState.Stopped, true)]
    //  The panel is read-only while the picture runs, and the button is part of the panel
    [InlineData(2, VideoRenderBackendOption.Gpu, VideoTransportState.Playing, false)]
    //  Nothing ticked, so there is no chain to compose
    [InlineData(0, VideoRenderBackendOption.Gpu, VideoTransportState.Paused, false)]
    [InlineData(0, VideoRenderBackendOption.Gpu, VideoTransportState.Stopped, false)]
    //  The processor path disables every part of the panel, this button included
    [InlineData(2, VideoRenderBackendOption.Cpu, VideoTransportState.Paused, false)]
    [InlineData(2, VideoRenderBackendOption.Cpu, VideoTransportState.Stopped, false)]
    [InlineData(2, VideoRenderBackendOption.Cpu, VideoTransportState.Playing, false)]
    public void CanBake_follows_the_panel_and_counts_what_is_ticked(
        int tickedTableCount,
        VideoRenderBackendOption backend,
        VideoTransportState transport,
        bool expected)
    {
        //Act
        var canBake = LutPanelPolicy.CanBake(tickedTableCount, backend, transport);

        //Assert
        canBake.Should().Be(expected);
    }
}
