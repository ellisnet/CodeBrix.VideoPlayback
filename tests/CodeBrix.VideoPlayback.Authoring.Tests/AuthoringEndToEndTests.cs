using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.VideoPlayback.Authoring.Captions;
using CodeBrix.VideoPlayback.Authoring.Effects;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Color.Luts;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Containers.Ogg;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using CodeBrix.VideoProcessing;
using SilverAssertions;
using Xunit;

// Both libraries have a Chapter type; the one this class means is the container's - the same alias
// CbvAuthor itself uses.
using Chapter = CodeBrix.VideoPlayback.Chapters.Chapter;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// Authors real files with real FFmpeg, from a clip generated on the spot, and reads them back with this
/// family's own container readers.
/// </summary>
/// <remarks>
/// <para>
/// Every test here SKIPS itself, naming what was looked for, when FFmpeg is not installed - it is the one
/// external tool authoring needs, and a machine without it still has a green suite.
/// </para>
/// <para>
/// The clips are two seconds of a test pattern at 128 by 72, which SVT-AV1 will encode in a fraction of a
/// second, so the whole class runs in a few seconds. Nothing here is third-party media.
/// </para>
/// </remarks>
public class AuthoringEndToEndTests
{
    [Fact]
    public void The_webm_profile_flavour_writes_a_file_that_passes_the_streamable_profile()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-profile");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.Profile.Verdict.Should().Be("passes the profile");
        result.SizeInBytes.Should().BeGreaterThan(0);
        result.Commands.Count.Should().Be(1);
    }

    [Fact]
    public void The_bespoke_flavour_writes_a_file_that_passes_the_streamable_profile()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-profile");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.PassesProfile.Should().BeTrue();
        result.Commands.Count.Should().Be(2);
        result.Mux.Should().NotBeNull();
        result.Mux.VideoFrameCount.Should().BeGreaterThan(0);
        result.Mux.AudioPacketCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_bespoke_flavour_leaves_no_intermediate_file_behind()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-temp");
        using WorkFolder scratch = new WorkFolder("bespoke-scratch");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), scratch);

        //Act
        CbvAuthor.Write(request);

        //Assert
        Directory.GetFiles(scratch.Path).Length.Should().Be(0);
    }

    [Fact]
    public void A_failed_bespoke_run_leaves_no_intermediate_file_behind()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-fail");
        using WorkFolder scratch = new WorkFolder("bespoke-fail-scratch");

        // A source with no audio at all: the audio pass has nothing to map and FFmpeg refuses.
        string source = work.File("video-only.mkv");
        WriteVideoOnlyClip(source);

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), scratch);

        //Act
        Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        Directory.GetFiles(scratch.Path).Length.Should().Be(0);
    }

    [Fact]
    public void The_webm_profile_flavour_carries_both_caption_tracks_with_their_flags_and_cues()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-captions");
        VideoAuthoringResult result = AuthorWithText(VideoAuthoringFlavour.WebMProfile, work);

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);
        IReadOnlyList<CaptionTrack> captions = reader.CaptionTracks;
        int cuesBeforeReading = captions[0].CueCount;
        while (reader.TryReadPacket(out MediaPacket _))
        {
        }

        //Assert
        result.PassesProfile.Should().BeTrue();
        captions.Count.Should().Be(2);
        captions[0].Language.Should().Be("en");
        captions[0].Name.Should().Be("English");
        (captions[0].Flags & CaptionTrackFlags.Default).Should().Be(CaptionTrackFlags.Default);
        captions[1].Name.Should().Be("English SDH");

        // A WebM caption cue is a block in a cluster, so the cues arrive WITH the packets - unlike the
        // bespoke flavour, which keeps whole caption tracks in the header region and has them all at once.
        cuesBeforeReading.Should().Be(0);
        captions[0].CueCount.Should().Be(2);
        captions[0].Cues[0].Text.Should().Contain("Hello there");

        // A WebM document has no element for the hearing-impaired flag, so it does NOT survive this flavour -
        // and the run says so rather than leaving it to be discovered.
        (captions[1].Flags & CaptionTrackFlags.HearingImpaired).Should().Be(CaptionTrackFlags.None);
        string notes = string.Join(" | ", result.Notes);
        notes.Should().Contain("hearing-impaired flag");
        notes.Should().Contain("English SDH");
    }

    [Fact]
    public void The_matroska_container_keeps_the_hearing_impaired_flag_the_webm_one_drops()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("mkv-sdh");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        string sdh = SyntheticSource.WriteCaptions(work.File("en-sdh.vtt"), "[door closes]");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.mkv"), work);
        request.Container = AuthoringContainerFormat.Matroska;
        request.CuesToFront = false;
        request.FailWhenProfileFails = false;
        request.Captions.Add(new AuthoringCaptionInput(sdh, "en", "English SDH", CaptionTrackFlags.HearingImpaired));

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);

        //Assert
        (reader.CaptionTracks[0].Flags & CaptionTrackFlags.HearingImpaired)
            .Should().Be(CaptionTrackFlags.HearingImpaired);
    }

    [Fact]
    public void The_bespoke_flavour_carries_both_caption_tracks_with_their_flags_and_cues()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-captions");
        VideoAuthoringResult result = AuthorWithText(VideoAuthoringFlavour.Bespoke, work);

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);
        IReadOnlyList<CaptionTrack> captions = reader.CaptionTracks;

        //Assert
        result.PassesProfile.Should().BeTrue();
        captions.Count.Should().Be(2);
        captions[0].Language.Should().Be("en");
        captions[0].Name.Should().Be("English");
        (captions[0].Flags & CaptionTrackFlags.Default).Should().Be(CaptionTrackFlags.Default);
        (captions[1].Flags & CaptionTrackFlags.HearingImpaired).Should().Be(CaptionTrackFlags.HearingImpaired);
        captions[0].CueCount.Should().Be(2);
        captions[0].AreCuesComplete.Should().BeTrue();
        captions[0].Cues[0].Identifier.Should().Be("opening-cue");
        captions[0].Cues[0].Settings.Should().Contain("position:50%");
    }

    [Fact]
    public void The_webm_profile_flavour_keeps_one_untagged_chapter_title_and_says_which_it_dropped()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-chapters");
        VideoAuthoringResult result = AuthorWithText(VideoAuthoringFlavour.WebMProfile, work);

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);
        IReadOnlyList<Chapter> chapters = reader.Chapters;

        //Assert
        chapters.Count.Should().Be(2);

        foreach (Chapter chapter in chapters)
        {
            // ONE title, and it is the untagged one. This is the asymmetry, asserted rather than hoped for.
            chapter.Titles.Count.Should().Be(1);
        }

        chapters[0].Titles[string.Empty].Should().Be("Opening");
        chapters[1].Titles[string.Empty].Should().Be("Closing");

        string notes = string.Join(" | ", result.Notes);
        notes.Should().Contain("DROPPED");
        notes.Should().Contain("de");
        notes.Should().Contain("fr");
    }

    [Fact]
    public void The_bespoke_flavour_keeps_every_language_of_every_chapter_title()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-chapters");
        VideoAuthoringResult result = AuthorWithText(VideoAuthoringFlavour.Bespoke, work);

        //Act
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);
        IReadOnlyList<Chapter> chapters = reader.Chapters;

        //Assert
        chapters.Count.Should().Be(2);
        chapters[0].Titles.Count.Should().Be(3);
        chapters[0].Titles[string.Empty].Should().Be("Opening");
        chapters[0].Titles["de"].Should().Be("Anfang");
        chapters[0].Titles["fr"].Should().Be("Ouverture");
        chapters[1].Titles["de"].Should().Be("Schluss");
        chapters[1].Titles["fr"].Should().Be("Fermeture");

        // Nothing was dropped, so there is nothing to report.
        string notes = string.Join(" | ", result.Notes);
        notes.Should().NotContain("DROPPED");
    }

    [Fact]
    public void The_two_flavours_disagree_about_chapter_titles_and_agree_about_everything_else()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder webmWork = new WorkFolder("asymmetry-webm");
        using WorkFolder bespokeWork = new WorkFolder("asymmetry-bespoke");

        //Act
        VideoAuthoringResult webm = AuthorWithText(VideoAuthoringFlavour.WebMProfile, webmWork);
        VideoAuthoringResult bespoke = AuthorWithText(VideoAuthoringFlavour.Bespoke, bespokeWork);

        using IMediaContainerReader webmReader = MediaContainers.Open(webm.OutputPath);
        using IMediaContainerReader bespokeReader = MediaContainers.Open(bespoke.OutputPath);

        //Assert
        webmReader.Chapters.Count.Should().Be(bespokeReader.Chapters.Count);
        webmReader.CaptionTracks.Count.Should().Be(bespokeReader.CaptionTracks.Count);

        // The asymmetry, stated as a difference: one title in the WebM-profile file, three in the bespoke one.
        webmReader.Chapters[0].Titles.Count.Should().Be(1);
        bespokeReader.Chapters[0].Titles.Count.Should().Be(3);
    }

    [Fact]
    public void The_webm_profile_flavour_writes_a_webm_document_with_its_cues_in_front()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("webm-layout");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using MatroskaReader reader = (MatroskaReader)MediaContainers.Open(result.OutputPath);

        //Assert
        reader.DocType.Should().Be("webm");
        reader.HasIndex.Should().BeTrue();
        reader.CuesPrecedeFirstCluster.Should().BeTrue();
        reader.HasDeclaredDuration.Should().BeTrue();
        reader.HasUnknownSizeElements.Should().BeFalse();
    }

    [Fact]
    public void The_bespoke_flavour_writes_a_cbvf_document_with_its_index_in_front()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("bespoke-layout");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using CbvReader reader = (CbvReader)MediaContainers.Open(result.OutputPath);

        //Assert
        reader.Index.Count.Should().BeGreaterThan(0);
        reader.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        reader.HeaderChecksumVerified.Should().BeTrue();
        reader.IndexChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public void The_bespoke_flavour_writes_vorbis_audio_by_default_and_the_webm_profile_one_opus()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("codec-defaults");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        //Act
        VideoAuthoringResult webm = CbvAuthor.Write(
            NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("webm.cbv"), work));
        VideoAuthoringResult bespoke = CbvAuthor.Write(
            NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("bespoke.cbv"), work));

        //Assert
        AudioCodecOf(webm.OutputPath).Should().Be(VideoCodecIds.Opus);
        AudioCodecOf(bespoke.OutputPath).Should().Be(VideoCodecIds.Vorbis);
    }

    [Fact]
    public void A_grade_chain_of_two_tables_at_forty_percent_is_composed_into_one()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-chain");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        string warm = AuthoringTestAssets.Lut("warm_33.cube");
        string cool = AuthoringTestAssets.Lut("cool_33.cube");
        Assert.SkipUnless(File.Exists(warm) && File.Exists(cool), "The generated lookup tables are not beside the test assembly.");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("graded.cbv"), work);
        request.Video.Luts.Add(new AuthoringLutInput(warm, 40));
        request.Video.Luts.Add(new AuthoringLutInput(cool, 40));

        Lut3D expected = LutComposer.Compose(new List<LutLayer>
        {
            LutLayer.FromCubeFile(warm, 40),
            LutLayer.FromCubeFile(cool, 40),
        });

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.ComposedLutSize.Should().Be(expected.Size);
        result.ComposedLutTitle.Should().Contain("warm_33.cube at 40%");
        result.ComposedLutTitle.Should().Contain("cool_33.cube at 40%");
        result.PassesProfile.Should().BeTrue();

        string notes = string.Join(" | ", result.Notes);
        notes.Should().Contain("composed into one");
        Directory.GetFiles(work.Path, "*.effective.cube").Length.Should().Be(0);
        result.ComposedLutPath.Should().BeNull();
    }

    [Fact]
    public void A_composed_grade_chain_is_kept_where_the_request_asks_for_it()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-kept");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        string warm = AuthoringTestAssets.Lut("warm_33.cube");
        string cool = AuthoringTestAssets.Lut("cool_33.cube");
        Assert.SkipUnless(File.Exists(warm) && File.Exists(cool), "The generated lookup tables are not beside the test assembly.");

        string kept = Path.Combine(work.Path, "kept", "effective.cube");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("graded.cbv"), work);
        request.Video.ComposedLutPath = kept;
        request.Video.Luts.Add(new AuthoringLutInput(warm, 40));
        request.Video.Luts.Add(new AuthoringLutInput(cool, 65));

        Lut3D expected = LutComposer.Compose(new List<LutLayer>
        {
            LutLayer.FromCubeFile(warm, 40),
            LutLayer.FromCubeFile(cool, 65),
        });

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.ComposedLutPath.Should().Be(kept);
        File.Exists(kept).Should().BeTrue();

        // The kept file is the file the lookup read: the command line names it, and nothing was left in the
        // temporary folder to be a second, differently-written copy of it.
        result.Commands[0].Arguments.Should().Contain("lut3d=file=" + kept.Replace("\\", "\\\\\\\\") + ":");
        Directory.GetFiles(work.Path, "*.effective.cube").Length.Should().Be(0);

        CubeLut readBack = CubeLutFile.ReadFile(kept);
        readBack.Lut3D.Size.Should().Be(expected.Size);
        readBack.Lut3D.Values.ToArray().Should().Equal(expected.Values.ToArray());
        readBack.Title.Should().Be(result.ComposedLutTitle);

        string notes = string.Join(" | ", result.Notes);
        notes.Should().Contain("KEPT at");
        result.PassesProfile.Should().BeTrue();
    }

    [Fact]
    public void A_kept_path_copies_the_one_table_when_the_chain_needs_no_composing()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-kept-copy");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        string sepia = AuthoringTestAssets.Lut("sepia_33.cube");
        Assert.SkipUnless(File.Exists(sepia), "The generated lookup tables are not beside the test assembly.");

        string kept = Path.Combine(work.Path, "kept", "effective.cube");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("graded.cbv"), work);
        request.Video.ComposedLutPath = kept;
        request.Video.Luts.Add(new AuthoringLutInput(sepia));

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert - nothing was composed, so FFmpeg read the caller's own table and the command still names it;
        // the kept path holds a byte-for-byte COPY of that same table.
        result.ComposedLutPath.Should().Be(kept);
        result.ComposedLutSize.Should().Be(0);
        result.ComposedLutTitle.Should().BeNull();

        File.Exists(kept).Should().BeTrue();
        File.ReadAllBytes(kept).Should().Equal(File.ReadAllBytes(sepia));

        result.Commands[0].Arguments.Should().Contain("lut3d=file=" + sepia.Replace("\\", "\\\\\\\\") + ":");
        result.Commands[0].Arguments.Should().NotContain("effective.cube");

        string notes = string.Join(" | ", result.Notes);
        notes.Should().Contain("a COPY of it was KEPT at");
        result.PassesProfile.Should().BeTrue();
    }

    [Fact]
    public void A_request_with_no_grade_at_all_keeps_nothing_however_the_path_is_set()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-kept-none");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        string kept = work.File("effective.cube");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("plain.cbv"), work);
        request.Video.ComposedLutPath = kept;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        File.Exists(kept).Should().BeFalse();
        result.ComposedLutPath.Should().BeNull();
        result.Commands[0].Arguments.Should().NotContain("lut3d");
        result.PassesProfile.Should().BeTrue();
    }

    [Fact]
    public void A_cancel_part_way_through_an_encode_stops_promptly_and_leaves_no_file()
    {
        //Arrange - fifteen seconds of source, upscaled and encoded at the SLOWEST speed setting there is, so
        // the pass is certainly still running two seconds in and the cancel lands mid-flight.
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("cancel-mid-encode");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"), 320, 180, 15);

        VideoAuthoringRequest request = NewRequest(
            VideoAuthoringFlavour.WebMProfile, source, work.File("cancelled.cbv"), work);

        request.Video.SpeedPreset = 0;
        request.Video.ConstantRateFactor = 20;
        request.Video.FrameSize = AuthoringFrameSize.Exact(1280, 720);

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        request.CancellationToken = cancellation.Token;
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        //Act
        Stopwatch clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => CbvAuthor.Write(request));
        clock.Stop();

        //Assert - the child is killed outright rather than asked to quit tidily, so "promptly" means seconds,
        // not the length of the encode that was abandoned.
        (clock.Elapsed < TimeSpan.FromSeconds(60)).Should().BeTrue();
        File.Exists(request.OutputPath).Should().BeFalse();
        Directory.GetFiles(work.Path, "*.ivf").Length.Should().Be(0);
        Directory.GetFiles(work.Path, "*.ogg").Length.Should().Be(0);
    }

    [Fact]
    public void An_ogg_written_from_a_bespoke_files_own_audio_is_one_ffprobe_accepts()
    {
        //Arrange - the core's Ogg writer is proven against the core's own readers in the core suite. This is
        // the OUTSIDE opinion: take the sound back out of a ".cbv" this library just authored, write it as an
        // Ogg Vorbis file, and let ffprobe - which has never heard of CBVF - judge the result.
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("ogg-writer-ffprobe");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringResult authored = CbvAuthor.Write(
            NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("bespoke.cbv"), work));

        byte[] codecPrivate;
        int sampleRate;
        int channels;
        List<byte[]> packets = new List<byte[]>();
        List<TimeSpan> ends = new List<TimeSpan>();

        using (CbvReader reader = new CbvReader(new FileMediaSource(authored.OutputPath)))
        {
            MediaTrackInfo audio = FirstOfKind(reader, MediaTrackKind.Audio);
            audio.CodecId.Should().Be(VideoCodecIds.Vorbis);

            codecPrivate = audio.CodecPrivate.ToArray();
            sampleRate = audio.SampleRate;
            channels = audio.Channels;

            // A packet's bytes are borrowed from the reader and are gone on the next read, so they are copied.
            while (reader.TryReadPacket(out MediaPacket packet))
            {
                if (packet.TrackId != audio.Id) continue;
                packets.Add(packet.Data.ToArray());
                ends.Add(packet.Timestamp + packet.Duration);
            }
        }

        string ogg = work.File("extracted.ogg");

        //Act
        using (OggAudioWriter writer = OggAudioWriter.CreateVorbis(ogg, codecPrivate, sampleRate))
        {
            for (int i = 0; i < packets.Count; i++) writer.WritePacket(packets[i], ends[i]);
            writer.Complete();
        }

        IMediaAnalysis analysis = FFProbe.Analyse(ogg);

        //Assert
        packets.Count.Should().BeGreaterThan(0);
        analysis.AudioStreams.Count.Should().Be(1);
        analysis.AudioStreams[0].CodecName.Should().Be("vorbis");
        analysis.AudioStreams[0].SampleRateHz.Should().Be(sampleRate);
        analysis.AudioStreams[0].Channels.Should().Be(channels);
        (analysis.Duration > TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void A_run_with_a_token_that_is_never_cancelled_authors_exactly_as_before()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("cancel-unused");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        using CancellationTokenSource cancellation = new CancellationTokenSource();

        VideoAuthoringRequest request = NewRequest(
            VideoAuthoringFlavour.Bespoke, source, work.File("with-token.cbv"), work);
        request.CancellationToken = cancellation.Token;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        File.Exists(result.OutputPath).Should().BeTrue();
        (result.SizeInBytes > 0).Should().BeTrue();
        result.Commands.Count.Should().Be(2);
    }

    [Fact]
    public void A_single_table_at_full_strength_is_used_as_it_stands()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-single");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        string sepia = AuthoringTestAssets.Lut("sepia_33.cube");
        Assert.SkipUnless(File.Exists(sepia), "The generated lookup tables are not beside the test assembly.");

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("graded.cbv"), work);
        request.Video.Luts.Add(new AuthoringLutInput(sepia));

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.ComposedLutSize.Should().Be(0);
        result.ComposedLutTitle.Should().BeNull();
        result.Commands[0].Arguments.Should().Contain("lut3d=file=");
        result.PassesProfile.Should().BeTrue();
    }

    [Fact]
    public void A_grade_that_names_a_table_that_is_not_there_fails_before_anything_is_encoded()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("lut-missing");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("graded.cbv"), work);
        request.Video.Luts.Add(new AuthoringLutInput(work.File("no-such-table.cube")));

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        failure.Message.Should().Contain("no colour lookup table at");
        File.Exists(work.File("graded.cbv")).Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_matroska_file_is_reported_as_missing_the_cues_rule_rather_than_refused()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("negative-control");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("plain.mkv"), work);
        request.Container = AuthoringContainerFormat.Matroska;
        request.CuesToFront = false;
        request.FailWhenProfileFails = false;

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);

        //Assert
        result.PassesProfile.Should().BeFalse();
        string failures = string.Empty;
        foreach (StreamableProfileRule rule in result.Profile.FailedRules()) failures += rule.ToString();
        failures.Should().Contain("cues sit before the first cluster");
    }

    [Fact]
    public void A_file_that_misses_a_rule_is_a_failure_when_the_request_asked_to_be_told()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("profile-failure");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.WebMProfile, source, work.File("plain.mkv"), work);
        request.Container = AuthoringContainerFormat.Matroska;
        request.CuesToFront = false;

        //Act
        VideoAuthoringException failure = Assert.Throws<VideoAuthoringException>(() => CbvAuthor.Write(request));

        //Assert
        failure.Message.Should().Contain("does not pass the streamable profile");
        failure.Message.Should().Contain("cues sit before the first cluster");
    }

    [Fact]
    public void A_frame_size_stated_on_one_side_only_keeps_the_aspect_ratio()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("aspect");
        string source = SyntheticSource.WriteClip(work.File("source.mkv"), 256, 144);

        VideoAuthoringRequest request = NewRequest(VideoAuthoringFlavour.Bespoke, source, work.File("clip.cbv"), work);
        request.Video.FrameSize = AuthoringFrameSize.LongSide(128);

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        using IMediaContainerReader reader = MediaContainers.Open(result.OutputPath);
        MediaTrackInfo video = FirstOfKind(reader, MediaTrackKind.Video);

        //Assert
        video.Width.Should().Be(128);
        video.Height.Should().Be(72);
    }

    private static VideoAuthoringResult AuthorWithText(VideoAuthoringFlavour flavour, WorkFolder work)
    {
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        string full = SyntheticSource.WriteCaptions(work.File("en.vtt"), "Hello there");
        string sdh = SyntheticSource.WriteCaptions(work.File("en-sdh.vtt"), "[door closes] Hello there");
        string chapters = SyntheticSource.WriteMultilingualChapters(work.File("chapters.ffmeta"));

        VideoAuthoringRequest request = NewRequest(flavour, source, work.File("clip.cbv"), work);
        request.ChaptersPath = chapters;
        request.Captions.Add(new AuthoringCaptionInput(full, "en", "English", CaptionTrackFlags.Default));
        request.Captions.Add(new AuthoringCaptionInput(sdh, "en", "English SDH", CaptionTrackFlags.HearingImpaired));

        return CbvAuthor.Write(request);
    }

    private static VideoAuthoringRequest NewRequest(
        VideoAuthoringFlavour flavour,
        string source,
        string output,
        WorkFolder work)
    {
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = output,
            TemporaryFolder = work.Path,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;

        return request;
    }

    private static string AudioCodecOf(string path)
    {
        using IMediaContainerReader reader = MediaContainers.Open(path);
        return FirstOfKind(reader, MediaTrackKind.Audio).CodecId;
    }

    private static MediaTrackInfo FirstOfKind(IMediaContainerReader reader, MediaTrackKind kind)
    {
        foreach (MediaTrackInfo track in reader.Tracks)
        {
            if (track.Kind == kind) return track;
        }

        throw new InvalidOperationException("The file carries no " + kind + " track.");
    }

    private static void WriteVideoOnlyClip(string path)
    {
        VideoProcessing.FFMpegArguments
            .FromFileInput("testsrc2=size=128x72:rate=10:duration=1", false, input => input.ForceFormat("lavfi"))
            .OutputToFile(path, true, output =>
            {
                AuthoringEncoders.ApplyFastestTo(output, 50);
                output.ForcePixelFormat("yuv420p").ForceFormat("matroska");
            })
            .ProcessSynchronously();
    }
}
