using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Commands;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Captions;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Renders the command lines both flavours produce, and the refusals a bad request earns, WITHOUT running
/// FFmpeg or touching a single file.
/// </summary>
/// <remarks>
/// That is the point of the dry run: every argument decision in this library is a string, and a string can be
/// asserted on a machine with no encoder installed, on a checkout with no media in it, in milliseconds. The
/// end-to-end tests beside these prove the strings actually produce files; these prove the strings are right.
/// </remarks>
public class CbvAuthorTests
{
    private const string Source = "/clips/source.mkv";

    [Fact]
    public void RenderCommands_for_the_webm_profile_flavour_is_one_pass()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(1);
        commands[0].Label.Should().Be("one pass");
    }

    [Fact]
    public void RenderCommands_for_the_webm_profile_flavour_renders_the_whole_line()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.SpeedPreset = 6;
        request.Video.ConstantRateFactor = 28;
        request.Video.FrameSize = AuthoringFrameSize.Exact(1920, 1080);
        request.Video.FrameRate = 30;
        request.Video.KeyframeIntervalFrames = 60;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Be(
            "-autorotate -i \"/clips/source.mkv\" -map 0:v:0 -map 0:a:0 -map_metadata -1 "
            + "-c:v libsvtav1 -preset 6 -crf 28 -pix_fmt yuv420p -r 30 -g 60 "
            + "-vf \"scale=w=1920:h=1080:flags=lanczos, format=yuv420p\" "
            + "-c:a libopus -b:a 128k -ar 48000 -ac 2 -f webm -cues_to_front 1 \"/out/clip.cbv\" -y");
    }

    [Fact]
    public void RenderCommands_for_the_bespoke_flavour_is_a_video_pass_and_an_audio_pass()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke);
        request.TemporaryFolder = "/work";
        request.Video.SpeedPreset = 6;
        request.Video.ConstantRateFactor = 28;
        request.Video.FrameSize = AuthoringFrameSize.Exact(1920, 1080);
        request.Video.FrameRate = 30;
        request.Video.KeyframeIntervalFrames = 60;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
        commands[0].Label.Should().Be("video pass");
        commands[1].Label.Should().Be("audio pass");

        commands[0].Arguments.Should().Be(
            "-autorotate -i \"/clips/source.mkv\" -map 0:v:0 -map_metadata -1 "
            + "-c:v libsvtav1 -preset 6 -crf 28 -pix_fmt yuv420p -r 30 -g 60 "
            + "-vf \"scale=w=1920:h=1080:flags=lanczos, format=yuv420p\" "
            + "-an -f ivf \"" + Path.Combine("/work", "clip.video.ivf") + "\" -y");

        commands[1].Arguments.Should().Be(
            "-i \"/clips/source.mkv\" -map 0:a:0 -map_metadata -1 "
            + "-c:a libvorbis -b:a 128k -ar 48000 -ac 2 -vn -f ogg \""
            + Path.Combine("/work", "clip.audio.ogg") + "\" -y");
    }

    [Fact]
    public void The_webm_profile_flavour_defaults_to_opus_and_the_bespoke_one_to_vorbis()
    {
        //Arrange
        VideoAuthoringRequest webm = NewRequest(VideoAuthoringFlavour.WebMProfile);
        VideoAuthoringRequest bespoke = NewRequest(VideoAuthoringFlavour.Bespoke);

        //Act
        string webmLine = CbvAuthor.RenderCommands(webm)[0].Arguments;
        string bespokeLine = CbvAuthor.RenderCommands(bespoke)[1].Arguments;

        //Assert
        webmLine.Should().Contain("-c:a libopus");
        bespokeLine.Should().Contain("-c:a libvorbis");
        bespokeLine.Should().NotContain("-c:a vorbis ");
    }

    [Fact]
    public void RenderCommands_never_names_ffmpegs_built_in_vorbis_encoder()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Audio.Codec = AuthoringAudioCodec.LibVorbis;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-c:a libvorbis");
    }

    [Fact]
    public void RenderCommands_rate_controls_vorbis_by_quality_when_a_quality_is_given()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Audio.Codec = AuthoringAudioCodec.LibVorbis;
        request.Audio.VorbisQuality = 5;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-c:a libvorbis -q:a 5 -ar 48000");
        line.Should().NotContain("-b:a");
    }

    [Fact]
    public void RenderCommands_maps_every_caption_file_as_an_extra_input()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.vtt", "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new AuthoringCaptionInput("/text/de.vtt", "de", "Deutsch"));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-i \"/text/en.vtt\" -i \"/text/de.vtt\"");
        line.Should().Contain("-map 1:s:0 -map 2:s:0");
    }

    [Fact]
    public void RenderCommands_copies_caption_tracks_and_never_re_encodes_them()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.vtt", "en"));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-c:s copy");
        line.Should().NotContain("-c:s webvtt");
    }

    [Fact]
    public void RenderCommands_writes_the_language_the_title_and_the_disposition_of_every_caption_track()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.vtt", "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new AuthoringCaptionInput(
            "/text/en-sdh.vtt",
            "en",
            "English SDH",
            CaptionTrackFlags.HearingImpaired | CaptionTrackFlags.Forced));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-metadata:s:s:0 language=\"en\"");
        line.Should().Contain("-metadata:s:s:0 title=\"English\"");
        line.Should().Contain("-metadata:s:s:1 title=\"English SDH\"");
        line.Should().Contain("-disposition:s:0 default");
        line.Should().Contain("-disposition:s:1 forced+hearing_impaired");
    }

    [Fact]
    public void RenderCommands_maps_the_chapter_file_as_the_input_after_the_captions()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.vtt", "en"));
        request.ChaptersPath = "/text/chapters.ffmeta";

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-i \"/text/en.vtt\" -i \"/text/chapters.ffmeta\" -map_metadata 2");
        line.Should().NotContain("-map_metadata -1");
    }

    [Fact]
    public void RenderCommands_moves_the_cues_to_the_front_by_default_and_leaves_them_when_asked()
    {
        //Arrange
        VideoAuthoringRequest front = NewRequest(VideoAuthoringFlavour.WebMProfile);
        VideoAuthoringRequest asMuxed = NewRequest(VideoAuthoringFlavour.WebMProfile);
        asMuxed.CuesToFront = false;
        asMuxed.Container = AuthoringContainerFormat.Matroska;

        //Act
        string frontLine = CbvAuthor.RenderCommands(front)[0].Arguments;
        string asMuxedLine = CbvAuthor.RenderCommands(asMuxed)[0].Arguments;

        //Assert
        frontLine.Should().Contain("-f webm -cues_to_front 1");
        asMuxedLine.Should().Contain("-f matroska ");
        asMuxedLine.Should().NotContain("-cues_to_front");
    }

    [Fact]
    public void RenderCommands_builds_exactly_one_video_filter_chain()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.FrameSize = AuthoringFrameSize.Exact(640, 360);
        request.Video.FrameRate = 24;
        request.Video.FrameRateMode = AuthoringFrameRateMode.Filter;
        request.Video.Luts.Add(new AuthoringLutInput("/luts/warm.cube"));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;
        int chains = CountOccurrences(line, "-vf ");

        //Assert
        chains.Should().Be(1);
        line.Should().Contain(
            "-vf \"scale=w=640:h=360:flags=lanczos, fps=24, lut3d=file=/luts/warm.cube:interp=tetrahedral, format=yuv420p\"");
    }

    [Fact]
    public void RenderCommands_puts_the_frame_rate_at_the_encoder_by_default()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.FrameRate = 24;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-r 24");
        line.Should().NotContain("fps=24");
    }

    [Fact]
    public void RenderCommands_keeps_the_aspect_ratio_when_only_one_side_is_stated()
    {
        //Arrange
        VideoAuthoringRequest longSide = NewRequest(VideoAuthoringFlavour.WebMProfile);
        longSide.Video.FrameSize = AuthoringFrameSize.LongSide(1280);

        VideoAuthoringRequest shortSide = NewRequest(VideoAuthoringFlavour.WebMProfile);
        shortSide.Video.FrameSize = AuthoringFrameSize.ShortSide(720);

        //Act
        string longLine = CbvAuthor.RenderCommands(longSide)[0].Arguments;
        string shortLine = CbvAuthor.RenderCommands(shortSide)[0].Arguments;

        //Assert
        longLine.Should().Contain("scale=w=if(gte(iw\\,ih)\\,1280\\,-2):h=if(gte(iw\\,ih)\\,-2\\,1280):flags=lanczos");
        shortLine.Should().Contain("scale=w=if(gte(iw\\,ih)\\,-2\\,720):h=if(gte(iw\\,ih)\\,720\\,-2):flags=lanczos");
    }

    [Fact]
    public void RenderCommands_emits_no_scale_filter_when_the_source_size_is_kept()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-vf \"format=yuv420p\"");
        line.Should().NotContain("scale=");
    }

    [Fact]
    public void RenderCommands_escapes_a_lookup_table_path_carrying_a_colon_and_a_space()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.Luts.Add(new AuthoringLutInput("/grades/a b:c/warm look.cube"));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("lut3d=file=/grades/a b\\\\\\:c/warm look.cube:interp=tetrahedral");
    }

    [Fact]
    public void RenderCommands_names_the_table_the_chain_would_be_composed_into()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.TemporaryFolder = "/work";
        request.Video.Luts.Add(new AuthoringLutInput("/luts/warm.cube", 40));
        request.Video.Luts.Add(new AuthoringLutInput("/luts/cool.cube", 40));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("lut3d=file=" + Path.Combine("/work", "clip.effective.cube") + ":interp=tetrahedral");
    }

    [Fact]
    public void RenderCommands_hands_a_single_table_at_full_strength_straight_to_the_filter()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.TemporaryFolder = "/work";
        request.Video.Luts.Add(new AuthoringLutInput("/luts/warm.cube"));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("lut3d=file=/luts/warm.cube:");
        line.Should().NotContain("effective.cube");
    }

    [Fact]
    public void RenderCommands_skips_a_table_that_is_applied_at_nothing()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.Luts.Add(new AuthoringLutInput("/luts/warm.cube", 0));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().NotContain("lut3d");
    }

    [Fact]
    public void RenderCommands_asks_libaom_for_its_own_speed_knob_and_pins_its_bit_rate()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Video.Encoder = AuthoringVideoEncoder.LibAomAv1;
        request.Video.SpeedPreset = 8;
        request.Video.ConstantRateFactor = 40;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-c:v libaom-av1 -cpu-used 8 -crf 40 -b:v 0 -pix_fmt yuv420p");
        line.Should().NotContain("-preset");
    }

    [Fact]
    public void RenderCommands_keeps_the_source_metadata_when_asked()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.CopySourceMetadata = true;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().NotContain("-map_metadata");
    }

    [Fact]
    public void RenderCommands_leaves_stream_selection_to_ffmpeg_when_asked()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.SelectStreamsExplicitly = false;

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().NotContain("-map 0:v:0");
    }

    [Fact]
    public void RenderCommands_writes_a_picture_with_no_sound_when_the_audio_is_left_out()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke);
        request.Audio.Include = false;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(1);
        commands[0].Label.Should().Be("video pass");
    }

    [Fact]
    public void RenderCommands_refuses_a_request_with_no_source()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.SourcePath = null;

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain("no source file");
    }

    [Fact]
    public void RenderCommands_refuses_a_request_with_no_output()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.OutputPath = "  ";

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain("no output path");
    }

    [Fact]
    public void RenderCommands_refuses_a_caption_track_with_no_language()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.vtt", null));

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain("no language tag");
    }

    [Theory]
    [InlineData("en_GB")]
    [InlineData("e")]
    [InlineData("english language")]
    [InlineData("123")]
    [InlineData("en-")]
    public void RenderCommands_refuses_a_malformed_language_tag(string tag)
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/x.vtt", tag));

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain("BCP 47");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-GB")]
    [InlineData("zh-Hant-TW")]
    [InlineData("de-1901")]
    public void RenderCommands_accepts_a_well_formed_language_tag(string tag)
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/x.vtt", tag));

        //Act
        string line = CbvAuthor.RenderCommands(request)[0].Arguments;

        //Assert
        line.Should().Contain("-metadata:s:s:0 language=\"" + tag + "\"");
    }

    [Fact]
    public void RenderCommands_refuses_a_subrip_caption_in_the_webm_profile_flavour()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.srt", "en"));

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain(".vtt");
    }

    [Fact]
    public void RenderCommands_accepts_a_subrip_caption_in_the_bespoke_flavour()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke);
        request.Captions.Add(new AuthoringCaptionInput("/text/en.srt", "en"));

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands.Count.Should().Be(2);
    }

    [Fact]
    public void RenderCommands_refuses_opus_when_the_file_must_play_with_no_extra_package()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.RequireNoExtraPlaybackPackages = true;

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(
            () => CbvAuthor.RenderCommands(request));

        //Assert
        failure.Message.Should().Contain("CodeBrix.Audio.Opus");
    }

    [Fact]
    public void RenderCommands_accepts_vorbis_when_the_file_must_play_with_no_extra_package()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke);
        request.RequireNoExtraPlaybackPackages = true;

        //Act
        IReadOnlyList<AuthoringCommand> commands = CbvAuthor.RenderCommands(request);

        //Assert
        commands[1].Arguments.Should().Contain("-c:a libvorbis");
    }

    [Fact]
    public void Write_refuses_a_source_that_is_not_there()
    {
        //Arrange
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile);
        request.SourcePath = Path.Combine(Path.GetTempPath(), "no-such-clip-at-all.mkv");

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        failure.Message.Should().Contain("no source file at");
        failure.Message.Should().Contain("no-such-clip-at-all.mkv");
    }

    [Fact]
    public void RenderCommands_refuses_a_null_request()
    {
        //Act
        //Assert
        Assert.Throws<ArgumentNullException>(() => CbvAuthor.RenderCommands(null));
    }

    private static VideoAuthoringRequest NewRequest(VideoAuthoringFlavour flavour) =>
        new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = Source,
            OutputPath = "/out/clip.cbv",
        };

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
