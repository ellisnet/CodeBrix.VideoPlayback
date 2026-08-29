using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Captions;
using CodeBrix.VideoPlayback.Chapters;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks the Matroska and WebM reader against the golden corpus, cross-referencing everything it reports
/// with the <c>ffprobe</c> output recorded beside each file.
/// </summary>
/// <remarks>
/// <para>
/// Three differences from <c>ffprobe</c> are expected, and the assertions allow for them deliberately:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>ffprobe -show_frames</c> reports DECODED frames, not packets. A Vorbis track therefore shows one
///     fewer than the packet count, because a lapped-transform codec finalises nothing from its first packet.
///   </description></item>
///   <item><description>
///     <c>ffprobe</c> subtracts a track's <c>CodecDelay</c> from its timestamps; this reader reports what the
///     container stored and hands the delay to the caller separately.
///   </description></item>
///   <item><description>
///     For laced audio with no <c>DefaultDuration</c>, <c>ffprobe</c> works out each frame's own time from the
///     codec's sample count; this reader gives every frame in a lace its block's timestamp and says so in
///     <see cref="MatroskaReader.Notices" />.
///   </description></item>
/// </list>
/// </remarks>
public class MatroskaReaderTests
{
    private static readonly string AssetDirectory = FindAssetDirectory();

    [Fact]
    public void IsMatroska_recognises_the_ebml_signature()
        => MatroskaReader.IsMatroska(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00 }).Should().BeTrue();

    [Fact]
    public void IsMatroska_rejects_anything_else()
        => MatroskaReader.IsMatroska(new byte[] { (byte)'C', (byte)'B', (byte)'V', (byte)'F' }).Should().BeFalse();

    [Fact]
    public void IsMatroska_rejects_a_span_too_short_to_hold_the_signature()
        => MatroskaReader.IsMatroska(new byte[] { 0x1A, 0x45 }).Should().BeFalse();

    [Theory]
    [InlineData("av1-opus.webm", "webm")]
    [InlineData("av1-vorbis.webm", "webm")]
    [InlineData("av1-opus.mkv", "matroska")]
    [InlineData("raw-opus.mkv", "matroska")]
    public void Open_reads_the_document_type(string asset, string expected)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        string docType = reader.DocType;

        //Assert
        docType.Should().Be(expected);
    }

    [Theory]
    [InlineData("av1-opus.webm", 1_000_000L)]
    [InlineData("av1-opus.mkv", 1_000_000L)]
    [InlineData("lacing-ebml.mkv", 20_832L)]
    [InlineData("lacing-xiph.mkv", 20_832L)]
    [InlineData("lacing-fixed.mkv", 20_832L)]
    public void Open_reads_the_timestamp_scale_rather_than_assuming_a_millisecond(string asset, long expected)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        long scale = reader.TimestampScale;

        //Assert
        scale.Should().Be(expected);
    }

    [Theory]
    [InlineData("av1-opus.webm")]
    [InlineData("av1-vorbis.webm")]
    [InlineData("av1-opus-cues-at-end.webm")]
    [InlineData("av1-opus.mkv")]
    [InlineData("raw-opus.mkv")]
    [InlineData("av1-opus-captions-chapters.mkv")]
    [InlineData("webvtt-blockadditions.mkv")]
    [InlineData("lacing-vorbis.mkv")]
    [InlineData("lacing-ebml.mkv")]
    [InlineData("lacing-xiph.mkv")]
    [InlineData("lacing-fixed.mkv")]
    public void Open_reports_a_duration_matching_the_probe(string asset)
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle(asset);
        using MatroskaReader reader = OpenAsset(asset);
        double expected = double.Parse(
            oracle.Document.RootElement.GetProperty("format").GetProperty("duration").GetString(),
            System.Globalization.CultureInfo.InvariantCulture);

        //Act
        double actual = reader.Duration.TotalSeconds;

        //Assert
        actual.Should().BeApproximately(expected, 0.002);
        reader.HasDeclaredDuration.Should().BeTrue();
    }

    [Theory]
    [InlineData("av1-opus.webm", "av01", "opus")]
    [InlineData("av1-vorbis.webm", "av01", "vorbis")]
    [InlineData("av1-opus.mkv", "av01", "opus")]
    [InlineData("raw-opus.mkv", "raw", "opus")]
    [InlineData("lacing-vorbis.mkv", "av01", "vorbis")]
    public void Open_maps_the_container_codec_identifiers(string asset, string video, string audio)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        MediaTrackInfo videoTrack = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);
        MediaTrackInfo audioTrack = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        videoTrack.CodecId.Should().Be(video);
        audioTrack.CodecId.Should().Be(audio);
    }

    [Theory]
    [InlineData("av1-opus.webm")]
    [InlineData("av1-vorbis.webm")]
    [InlineData("av1-opus.mkv")]
    [InlineData("raw-opus.mkv")]
    [InlineData("av1-opus-captions-chapters.mkv")]
    public void Open_reads_video_dimensions_matching_the_probe(string asset)
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle(asset);
        MatroskaOracle.OracleStream expected = oracle.StreamOfType("video");
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);

        //Assert
        track.Width.Should().Be(expected.Width);
        track.Height.Should().Be(expected.Height);
        track.DisplayWidth.Should().Be(expected.Width);
        track.DisplayHeight.Should().Be(expected.Height);
    }

    [Theory]
    [InlineData("av1-opus.webm")]
    [InlineData("av1-vorbis.webm")]
    [InlineData("av1-opus.mkv")]
    [InlineData("lacing-ebml.mkv")]
    public void Open_reads_audio_settings_matching_the_probe(string asset)
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle(asset);
        MatroskaOracle.OracleStream expected = oracle.StreamOfType("audio");
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        track.SampleRate.Should().Be(expected.SampleRate);
        track.Channels.Should().Be(expected.Channels);
    }

    [Fact]
    public void Open_reads_the_opus_codec_delay_seek_pre_roll_and_pre_skip()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        track.CodecDelay.Should().Be(TimeSpan.FromTicks(65_000));
        track.SeekPreRoll.Should().Be(TimeSpan.FromMilliseconds(80));
        track.PreSkipSamples.Should().Be(312);
    }

    [Fact]
    public void Open_passes_the_opus_identification_header_through_as_codec_private()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);
        ReadOnlySpan<byte> head = track.CodecPrivate.Span;

        //Assert
        head.Length.Should().Be(19);
        System.Text.Encoding.ASCII.GetString(head.Slice(0, 8)).Should().Be("OpusHead");
    }

    [Fact]
    public void Open_passes_the_vorbis_setup_headers_through_in_the_xiph_laced_shape_the_decoder_expects()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-vorbis.webm");

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);
        ReadOnlySpan<byte> setup = track.CodecPrivate.Span;

        //Assert
        setup.Length.Should().BeGreaterThan(3);
        setup[0].Should().Be((byte)2);
        track.PreSkipSamples.Should().Be(0);
    }

    [Fact]
    public void Open_reads_an_uncompressed_track_as_planar_yuv_from_its_colour_space_fourcc()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("raw-opus.mkv");

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);

        //Assert
        track.CodecId.Should().Be(VideoCodecIds.Raw);
        track.Layout.Should().Be(VideoPixelLayout.I420);
        track.BitDepth.Should().Be(8);
    }

    [Fact]
    public void Open_reads_the_studio_range_flag_a_webm_file_carries()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        MediaTrackInfo track = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);

        //Assert
        track.Color.Range.Should().Be(VideoColorRange.Limited);
    }

    [Theory]
    [InlineData("av1-opus.webm")]
    [InlineData("av1-vorbis.webm")]
    [InlineData("av1-opus-cues-at-end.webm")]
    [InlineData("av1-opus.mkv")]
    [InlineData("raw-opus.mkv")]
    [InlineData("av1-opus-captions-chapters.mkv")]
    [InlineData("webvtt-blockadditions.mkv")]
    [InlineData("lacing-vorbis.mkv")]
    public void TryReadPacket_reproduces_the_video_frames_the_probe_saw(string asset)
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle(asset);
        MatroskaOracle.OracleStream stream = oracle.StreamOfType("video");
        IReadOnlyList<MatroskaOracle.OracleFrame> expected = oracle.FramesFor(stream.Index);
        using MatroskaReader reader = OpenAsset(asset);
        int videoTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video).Id;

        //Act
        List<MediaPacket> packets = ReadAll(reader).Where(p => p.TrackId == videoTrackId).ToList();

        //Assert
        packets.Count.Should().Be(expected.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            packets[i].Timestamp.TotalSeconds.Should().BeApproximately(expected[i].PtsTime, 0.0011);
            packets[i].IsKeyFrame.Should().Be(expected[i].IsKeyFrame);
            if (expected[i].HasSize) packets[i].Data.Length.Should().Be(expected[i].Size);
        }
    }

    [Theory]
    [InlineData("av1-opus.webm", 51)]
    [InlineData("av1-opus-cues-at-end.webm", 51)]
    [InlineData("av1-opus.mkv", 51)]
    [InlineData("raw-opus.mkv", 26)]
    [InlineData("lacing-fixed.mkv", 51)]
    public void TryReadPacket_reproduces_the_opus_packet_count_the_probe_decoded(string asset, int expected)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);
        int audioTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio).Id;

        //Act
        int count = ReadAll(reader).Count(p => p.TrackId == audioTrackId);

        //Assert
        count.Should().Be(expected);
    }

    [Theory]
    [InlineData("av1-vorbis.webm")]
    [InlineData("lacing-vorbis.mkv")]
    [InlineData("lacing-ebml.mkv")]
    [InlineData("lacing-xiph.mkv")]
    public void TryReadPacket_hands_out_one_more_vorbis_packet_than_the_probe_decoded_frames(string asset)
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle(asset);
        MatroskaOracle.OracleStream stream = oracle.StreamOfType("audio");
        int decodedFrames = oracle.FramesFor(stream.Index).Count;
        using MatroskaReader reader = OpenAsset(asset);
        int audioTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio).Id;

        //Act
        int packets = ReadAll(reader).Count(p => p.TrackId == audioTrackId);

        //Assert
        // A lapped-transform codec finalises nothing from its first packet, so ffprobe reports one fewer
        // decoded frame than the container holds packets. That is the codec, not the container.
        packets.Should().Be(decodedFrames + 1);
    }

    [Theory]
    [InlineData("lacing-xiph.mkv", 49, 1770)]
    [InlineData("lacing-ebml.mkv", 49, 1770)]
    [InlineData("lacing-fixed.mkv", 51, 3060)]
    public void TryReadPacket_unpacks_every_lacing_scheme(string asset, int expectedPackets, int expectedBytes)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);

        //Act
        List<MediaPacket> packets = ReadAll(reader);
        int bytes = packets.Sum(p => p.Data.Length);

        //Assert
        packets.Count.Should().Be(expectedPackets);
        bytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void TryReadPacket_gives_the_two_lacing_schemes_of_the_same_audio_identical_frames()
    {
        //Arrange
        using MatroskaReader xiph = OpenAsset("lacing-xiph.mkv");
        using MatroskaReader ebmlLaced = OpenAsset("lacing-ebml.mkv");

        //Act
        List<byte[]> fromXiph = ReadAll(xiph).Select(p => p.Data.ToArray()).ToList();
        List<byte[]> fromEbml = ReadAll(ebmlLaced).Select(p => p.Data.ToArray()).ToList();

        //Assert
        fromXiph.Count.Should().Be(fromEbml.Count);
        for (int i = 0; i < fromXiph.Count; i++) fromXiph[i].Should().Equal(fromEbml[i]);
    }

    [Fact]
    public void TryReadPacket_reads_the_discard_padding_on_the_last_opus_packet()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        List<MediaPacket> withPadding = ReadAll(reader).Where(p => p.DiscardPadding > TimeSpan.Zero).ToList();

        //Assert
        withPadding.Count.Should().Be(1);
        withPadding[0].DiscardPadding.Should().Be(TimeSpan.FromTicks(135_000));
    }

    [Fact]
    public void TryReadPacket_reads_uncompressed_frames_at_their_full_planar_size()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("raw-opus.mkv");
        int videoTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video).Id;

        //Act
        List<MediaPacket> frames = ReadAll(reader).Where(p => p.TrackId == videoTrackId).ToList();

        //Assert
        frames.Count.Should().Be(6);
        foreach (MediaPacket frame in frames)
        {
            // 64 x 36 in 4:2:0 is one luma plane plus two quarter-size chroma planes.
            frame.Data.Length.Should().Be(64 * 36 * 3 / 2);
            frame.IsKeyFrame.Should().BeTrue();
        }
    }

    [Fact]
    public void TryReadPacket_returns_caption_packets_as_well_as_filling_the_caption_track()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus-captions-chapters.mkv");
        int captionTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Caption).Id;

        //Act
        List<MediaPacket> packets = ReadAll(reader).Where(p => p.TrackId == captionTrackId).ToList();

        //Assert
        packets.Count.Should().Be(4);
        reader.CaptionTracks[0].CueCount.Should().Be(4);
    }

    [Fact]
    public void CaptionTracks_are_not_complete_until_the_file_has_been_read_to_the_end()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus-captions-chapters.mkv");

        //Act
        bool beforeReading = reader.CaptionTracks[0].AreCuesComplete;
        ReadAll(reader);
        bool afterReading = reader.CaptionTracks[0].AreCuesComplete;

        //Assert
        beforeReading.Should().BeFalse();
        afterReading.Should().BeTrue();
    }

    [Theory]
    [InlineData("av1-opus-captions-chapters.mkv")]
    [InlineData("webvtt-blockadditions.mkv")]
    public void CaptionTracks_read_the_same_cues_from_both_webvtt_dialects(string asset)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);
        ReadAll(reader);

        //Act
        CaptionTrack track = reader.CaptionTracks.Single();
        IReadOnlyList<CaptionCue> cues = track.Cues;

        //Assert
        track.Format.Should().Be(CaptionFormat.WebVtt);
        track.Language.Should().Be("en");
        track.Name.Should().Be("English");
        track.IsDefault.Should().BeTrue();
        cues.Count.Should().Be(4);

        cues[0].Start.Should().Be(TimeSpan.Zero);
        cues[0].End.Should().Be(TimeSpan.FromMilliseconds(250));
        cues[0].Identifier.Should().Be("intro");
        cues[0].Settings.Should().Be("line:90% align:center");
        cues[0].Text.Should().Be("Bar pattern, top left.");

        cues[1].Identifier.Should().Be(string.Empty);
        cues[1].Settings.Should().Be(string.Empty);
        cues[1].Text.Should().Be("Colour ramp sweeps right.");

        cues[2].Start.Should().Be(TimeSpan.FromMilliseconds(500));
        cues[2].Identifier.Should().Be("third-cue");
        cues[2].Settings.Should().Be("align:start position:10%");
        cues[2].Text.Should().Be("Timecode digits roll over.");

        cues[3].Text.Should().Be("End of the synthetic clip.");
    }

    [Fact]
    public void Chapters_match_the_probe()
    {
        //Arrange
        MatroskaOracle oracle = LoadOracle("av1-opus-captions-chapters.mkv");
        using MatroskaReader reader = OpenAsset("av1-opus-captions-chapters.mkv");

        //Act
        IReadOnlyList<Chapter> chapters = reader.Chapters;

        //Assert
        chapters.Count.Should().Be(oracle.Chapters.Count);
        for (int i = 0; i < chapters.Count; i++)
        {
            chapters[i].Index.Should().Be(i);
            chapters[i].Start.TotalSeconds.Should().BeApproximately(oracle.Chapters[i].StartTime, 0.001);
            chapters[i].End.TotalSeconds.Should().BeApproximately(oracle.Chapters[i].EndTime, 0.001);
            chapters[i].Title.Should().Be(oracle.Chapters[i].Title);
            chapters[i].IsHidden.Should().BeFalse();
        }
    }

    [Fact]
    public void Chapters_keep_an_untagged_title_under_an_empty_language_key()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus-captions-chapters.mkv");

        //Act
        Chapter first = reader.Chapters[0];

        //Assert
        // FFmpeg writes ChapterLanguage "und", which normalises to "no language stated".
        first.Titles.Count.Should().Be(1);
        first.Titles.ContainsKey(string.Empty).Should().BeTrue();
        first.TitleFor(new[] { "fr", "en" }).Should().Be("Opening bars");
    }

    [Fact]
    public void Cues_are_read_and_report_whether_they_precede_the_first_cluster()
    {
        //Arrange
        using MatroskaReader front = OpenAsset("av1-opus.webm");
        using MatroskaReader back = OpenAsset("av1-opus-cues-at-end.webm");

        //Act
        bool frontFirst = front.CuesPrecedeFirstCluster;
        bool backFirst = back.CuesPrecedeFirstCluster;

        //Assert
        front.Cues.Count.Should().Be(3);
        back.Cues.Count.Should().Be(3);
        frontFirst.Should().BeTrue();
        backFirst.Should().BeFalse();
    }

    [Fact]
    public void Cues_convert_their_cluster_positions_to_absolute_offsets()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        MatroskaCuePoint first = reader.Cues[0];

        //Assert
        first.ClusterOffset.Should().Be(reader.FirstClusterOffset);
        first.Time.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("av1-opus.webm")]
    [InlineData("av1-opus-cues-at-end.webm")]
    [InlineData("av1-opus.mkv")]
    [InlineData("raw-opus.mkv")]
    public void Seek_lands_at_or_before_the_moment_asked_for_and_says_where_it_landed(string asset)
    {
        //Arrange
        using MatroskaReader reader = OpenAsset(asset);
        int videoTrackId = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video).Id;
        TimeSpan target = TimeSpan.FromMilliseconds(500);

        //Act
        TimeSpan landed = reader.Seek(target, videoTrackId);
        reader.TryReadPacket(out MediaPacket next).Should().BeTrue();

        //Assert
        landed.Should().BeLessThanOrEqualTo(target);
        next.Timestamp.Should().Be(landed);
    }

    [Fact]
    public void Seek_to_the_start_rewinds_so_a_file_can_be_played_again()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");
        List<MediaPacket> firstPass = ReadAll(reader);

        //Act
        reader.Seek(TimeSpan.Zero, -1);
        List<MediaPacket> secondPass = ReadAll(reader);

        //Assert
        secondPass.Count.Should().Be(firstPass.Count);
        secondPass[0].Timestamp.Should().Be(firstPass[0].Timestamp);
    }

    [Fact]
    public void Seek_still_works_on_a_file_whose_index_points_several_times_into_one_cluster()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("lacing-ebml.mkv");

        //Act
        TimeSpan landed = reader.Seek(TimeSpan.FromMilliseconds(500), -1);
        reader.TryReadPacket(out MediaPacket next).Should().BeTrue();

        //Assert
        landed.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
        next.Timestamp.Should().Be(landed);
    }

    [Fact]
    public void CanSeek_is_false_when_the_source_only_reads_forwards()
    {
        //Arrange
        using FileStream file = File.OpenRead(AssetPath("av1-opus.webm"));
        using MemoryStream copy = new MemoryStream();
        file.CopyTo(copy);
        using ForwardOnlyStream forward = new ForwardOnlyStream(copy.ToArray());
        using StreamMediaSource source = new StreamMediaSource(forward, "forward only", leaveOpen: true);
        using MatroskaReader reader = new MatroskaReader(source, leaveSourceOpen: true);

        //Act
        bool canSeek = reader.CanSeek;

        //Assert
        canSeek.Should().BeFalse();
    }

    [Fact]
    public void TryReadPacket_reads_every_packet_from_a_forward_only_source()
    {
        //Arrange
        byte[] bytes = File.ReadAllBytes(AssetPath("av1-opus.webm"));
        using MatroskaReader seekable = OpenAsset("av1-opus.webm");
        List<MediaPacket> expected = ReadAll(seekable);

        using ForwardOnlyStream forward = new ForwardOnlyStream(bytes);
        using StreamMediaSource source = new StreamMediaSource(forward, "progressive download", leaveOpen: true);
        using MatroskaReader reader = new MatroskaReader(source, leaveSourceOpen: true);

        //Act
        List<MediaPacket> actual = ReadAll(reader);

        //Assert
        // A progressive download cannot seek, so there is no index to use - but every frame must still arrive.
        reader.CanSeek.Should().BeFalse();
        actual.Count.Should().Be(expected.Count);
        for (int i = 0; i < actual.Count; i++)
        {
            actual[i].TrackId.Should().Be(expected[i].TrackId);
            actual[i].Timestamp.Should().Be(expected[i].Timestamp);
            actual[i].Data.ToArray().Should().Equal(expected[i].Data.ToArray());
        }
    }

    [Fact]
    public void Tracks_and_captions_are_readable_from_a_forward_only_source()
    {
        //Arrange
        byte[] bytes = File.ReadAllBytes(AssetPath("av1-opus-captions-chapters.mkv"));
        using ForwardOnlyStream forward = new ForwardOnlyStream(bytes);
        using StreamMediaSource source = new StreamMediaSource(forward, "progressive download", leaveOpen: true);

        //Act
        using MatroskaReader reader = new MatroskaReader(source, leaveSourceOpen: true);
        ReadAll(reader);

        //Assert
        reader.Tracks.Count.Should().Be(3);
        reader.CaptionTracks.Single().CueCount.Should().Be(4);
    }

    [Fact]
    public void Open_reads_an_explicit_FlagDefault_of_zero_as_not_default()
    {
        //Arrange
        // FFmpeg writes FlagDefault = 0 explicitly into both tracks of this file, and ffprobe reports
        // disposition.default = 0 for both. An explicit zero is not the same as an absent element.
        MatroskaOracle oracle = LoadOracle("av1-opus.webm");
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        MediaTrackInfo video = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);
        MediaTrackInfo audio = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        video.IsDefault.Should().Be(oracle.StreamOfType("video").IsDefault);
        audio.IsDefault.Should().Be(oracle.StreamOfType("audio").IsDefault);
        video.IsDefault.Should().BeFalse();
        audio.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Open_treats_an_absent_FlagDefault_as_default_because_the_specification_says_so()
    {
        //Arrange
        // mkvmerge omits FlagDefault, so the RFC 9559 default of 1 applies - and ffprobe agrees.
        MatroskaOracle oracle = LoadOracle("av1-opus.mkv");
        using MatroskaReader reader = OpenAsset("av1-opus.mkv");

        //Act
        MediaTrackInfo video = reader.Tracks.First(t => t.Kind == MediaTrackKind.Video);
        MediaTrackInfo audio = reader.Tracks.First(t => t.Kind == MediaTrackKind.Audio);

        //Assert
        oracle.StreamOfType("video").IsDefault.Should().BeTrue();
        video.IsDefault.Should().BeTrue();
        audio.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Open_applies_the_specification_defaults_to_every_absent_track_flag()
    {
        //Arrange
        // The synthetic file writes TrackNumber, TrackType and CodecID and nothing else, so every flag and the
        // language come from the specification's defaults: enabled 1, default 1, forced 0, hearing-impaired
        // 0, language "eng".
        byte[] file = BuildMinimalMatroska("V_AV1", 1);

        //Act
        using MatroskaReader reader = new MatroskaReader(new MemoryMediaSource(file, "synthetic.mkv"));
        MediaTrackInfo track = reader.Tracks.Single();

        //Assert
        track.IsEnabled.Should().BeTrue();
        track.IsDefault.Should().BeTrue();
        track.IsForced.Should().BeFalse();
        track.IsHearingImpaired.Should().BeFalse();
        track.Language.Should().Be("en");
        track.DefaultDuration.Should().Be(TimeSpan.Zero);
        track.DisplayWidth.Should().Be(96);
        track.Color.Range.Should().Be(VideoColorRange.Unspecified);
        track.Color.ChromaSiting.Should().Be(VideoChromaSiting.Unknown);
        reader.TimestampScale.Should().Be(1_000_000L);
    }

    [Fact]
    public void Open_refuses_a_track_whose_codec_this_library_does_not_read()
    {
        //Arrange
        byte[] file = BuildMinimalMatroska("V_MPEG4/ISO/AVC", 1);

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(file, "synthetic.mkv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>()
            .WithMessage("*CodecID is 'V_MPEG4/ISO/AVC'*");
    }

    [Fact]
    public void Open_refuses_an_audio_track_whose_codec_this_library_does_not_read()
    {
        //Arrange
        byte[] file = BuildMinimalMatroska("A_AAC", 2);

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(file, "synthetic.mkv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*CodecID is 'A_AAC'*");
    }

    [Fact]
    public void Open_refuses_a_track_whose_frames_have_been_compressed_or_stripped()
    {
        //Arrange
        byte[] file = BuildMinimalMatroska("V_AV1", 1, withContentEncodings: true);

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(file, "synthetic.mkv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*ContentEncodings*");
    }

    [Fact]
    public void Open_refuses_a_document_type_it_does_not_know()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.EbmlHeader("nonsense");
        builder.BeginMaster(MatroskaIds.Segment);
        builder.EndMaster();

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(builder.ToArray(), "synthetic.mkv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*DocType is 'nonsense'*");
    }

    [Fact]
    public void Open_refuses_a_document_that_needs_a_newer_reader()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.EbmlHeader("matroska", docTypeVersion: 9, readVersion: 9);
        builder.BeginMaster(MatroskaIds.Segment);
        builder.EndMaster();

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(builder.ToArray(), "synthetic.mkv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*read version 9*");
    }

    [Fact]
    public void Open_refuses_bytes_that_are_not_a_matroska_file_at_all()
    {
        //Arrange
        byte[] notMatroska = { (byte)'C', (byte)'B', (byte)'V', (byte)'F', 0, 0, 0, 0 };

        //Act
        Action open = () => new MatroskaReader(new MemoryMediaSource(notMatroska, "synthetic.cbv"));

        //Assert
        open.Should().Throw<VideoPlaybackException>().WithMessage("*EBML signature*");
    }

    [Fact]
    public void Open_skips_a_bitmap_subtitle_track_with_a_notice_rather_than_refusing_the_file()
    {
        //Arrange
        byte[] file = BuildMinimalMatroska("V_AV1", 1, extraSubtitleCodecId: "S_HDMV/PGS");

        //Act
        using MatroskaReader reader = new MatroskaReader(new MemoryMediaSource(file, "synthetic.mkv"));

        //Assert
        reader.Notices.Count.Should().Be(1);
        reader.Notices[0].Should().Contain("S_HDMV/PGS");
        reader.Tracks.Count.Should().Be(2);
        reader.CaptionTracks.Count.Should().Be(0);
    }

    [Fact]
    public void Open_reports_that_the_golden_files_use_no_unknown_size_elements()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        ReadAll(reader);

        //Assert
        reader.HasUnknownSizeElements.Should().BeFalse();
        reader.UnknownSizeElementCount.Should().Be(0);
    }

    [Fact]
    public void Open_reads_a_segment_that_declares_no_size()
    {
        //Arrange
        byte[] file = BuildMinimalMatroska("V_AV1", 1, unknownSizeSegment: true);

        //Act
        using MatroskaReader reader = new MatroskaReader(new MemoryMediaSource(file, "live.mkv"));

        //Assert
        reader.HasUnknownSizeElements.Should().BeTrue();
        reader.Tracks.Count.Should().Be(1);
    }

    [Fact]
    public void MuxingApp_and_WritingApp_are_reported_for_diagnostics()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("av1-opus.webm");

        //Act
        string muxer = reader.MuxingApp;

        //Assert
        muxer.Should().NotBeNullOrEmpty();
        reader.WritingApp.Should().NotBeNullOrEmpty();
        reader.SegmentDataOffset.Should().BeGreaterThan(0L);
    }

    [Fact]
    public void Notices_explain_that_laced_audio_frames_share_their_block_timestamp()
    {
        //Arrange
        using MatroskaReader reader = OpenAsset("lacing-ebml.mkv");

        //Act
        ReadAll(reader);

        //Assert
        reader.Notices.Count.Should().Be(1);
        reader.Notices[0].Should().Contain("laced blocks");
    }

    // ------------------------------------------------------------------ helpers

    private static List<MediaPacket> ReadAll(MatroskaReader reader)
    {
        List<MediaPacket> packets = new List<MediaPacket>();
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            packets.Add(new MediaPacket(
                packet.TrackId,
                packet.Data.ToArray(),
                packet.Timestamp,
                packet.Duration,
                packet.IsKeyFrame,
                packet.DiscardPadding));
        }

        return packets;
    }

    private static byte[] BuildMinimalMatroska(
        string codecId,
        int trackType,
        bool withContentEncodings = false,
        string extraSubtitleCodecId = null,
        bool unknownSizeSegment = false)
    {
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.EbmlHeader("matroska");

        if (unknownSizeSegment)
        {
            builder.WriteId(MatroskaIds.Segment);
            builder.WriteUnknownSize();
        }
        else
        {
            builder.BeginMaster(MatroskaIds.Segment);
        }

        builder.Info(1_000_000, 1000.0);

        builder.BeginMaster(MatroskaIds.Tracks);
        builder.BeginMaster(MatroskaIds.TrackEntry);
        builder.UInt(MatroskaIds.TrackNumber, 1);
        builder.UInt(MatroskaIds.TrackType, (ulong)trackType);
        builder.Str(MatroskaIds.CodecId, codecId);
        if (trackType == 1)
        {
            builder.BeginMaster(MatroskaIds.Video);
            builder.UInt(MatroskaIds.PixelWidth, 96);
            builder.UInt(MatroskaIds.PixelHeight, 54);
            builder.EndMaster();
        }
        else
        {
            builder.BeginMaster(MatroskaIds.Audio);
            builder.UInt(MatroskaIds.Channels, 2);
            builder.Float64(MatroskaIds.SamplingFrequency, 48000.0);
            builder.EndMaster();
        }

        if (withContentEncodings)
        {
            builder.BeginMaster(MatroskaIds.ContentEncodings);
            builder.EndMaster();
        }

        builder.EndMaster();

        if (extraSubtitleCodecId != null)
        {
            builder.BeginMaster(MatroskaIds.TrackEntry);
            builder.UInt(MatroskaIds.TrackNumber, 2);
            builder.UInt(MatroskaIds.TrackType, 17);
            builder.Str(MatroskaIds.CodecId, extraSubtitleCodecId);
            builder.EndMaster();
        }

        builder.EndMaster();

        builder.BeginMaster(MatroskaIds.Cluster);
        builder.UInt(MatroskaIds.ClusterTimestamp, 0);
        builder.Element(
            MatroskaIds.SimpleBlock,
            MatroskaTestBuilder.SimpleBlockPayload(1, 0, 0x80, new byte[] { 1, 2, 3, 4 }));
        builder.EndMaster();

        if (!unknownSizeSegment) builder.EndMaster();
        return builder.ToArray();
    }

    private static MatroskaReader OpenAsset(string name)
        => new MatroskaReader(new FileMediaSource(AssetPath(name)));

    private static MatroskaOracle LoadOracle(string name) => MatroskaOracle.Load(AssetPath(name));

    private static string AssetPath(string name)
    {
        string path = Path.Combine(AssetDirectory, name);
        Assert.SkipUnless(File.Exists(path), $"The golden asset '{name}' is not present at '{path}'.");
        return path;
    }

    private static string FindAssetDirectory()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "assets");
        if (Directory.Exists(beside)) return beside;

        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "assets");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return beside;
    }

    /// <summary>A stream that refuses to seek, so the forward-only source path can be exercised.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream inner;

        public ForwardOnlyStream(byte[] data) => inner = new MemoryStream(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
