using System;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Checks what each reader can PROVE about a track having ended, and - just as important - what it refuses to
/// claim.
/// </summary>
/// <remarks>
/// A false negative here costs a little latency; a false positive truncates somebody's media. So these tests
/// pin both directions: that the indexed container answers early and exactly, and that the one which cannot
/// know says so rather than guessing.
/// </remarks>
public class MediaContainerReaderExhaustionTests
{
    [Fact]
    public void The_bespoke_reader_knows_where_every_track_ends_before_it_has_read_anything()
    {
        //Arrange
        using CbvReader reader = new CbvReader(new FileMediaSource(TestAssets.Path("raw-vorbis.cbv")));

        //Act
        bool videoAtOpen = reader.IsTrackExhausted(1);
        bool audioAtOpen = reader.IsTrackExhausted(2);
        TimeSpan? videoEnd = reader.GetTrackEndTimestamp(1);
        TimeSpan? audioEnd = reader.GetTrackEndTimestamp(2);

        //Assert
        videoAtOpen.Should().BeFalse();
        audioAtOpen.Should().BeFalse();
        videoEnd.HasValue.Should().BeTrue();
        audioEnd.HasValue.Should().BeTrue();
        videoEnd.Value.Should().Be(TimeSpan.FromSeconds(0.96));
        audioEnd.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(0.9));
    }

    [Fact]
    public void The_bespoke_reader_reports_a_track_exhausted_the_moment_its_last_chunk_has_been_read()
    {
        //Arrange - 2.4 seconds of picture over one second of sound, which is the shape that used to hang
        string path = SyntheticMedia.ScratchPath("exhaustion-cbv", "long-picture.cbv");
        SyntheticMedia.WriteRawCbv(
            path,
            frameCount: 60,
            frameRate: 25,
            keyFrameInterval: 10,
            audioOggPath: TestAssets.Path("vorbis-audio.ogg"));

        using CbvReader reader = new CbvReader(new FileMediaSource(path));

        int audioPackets = 0;
        bool audioExhaustedWhileVideoContinued = false;
        int videoPacketsAfterAudioEnded = 0;

        //Act
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == 2) audioPackets++;

            if (reader.IsTrackExhausted(2))
            {
                if (!reader.IsTrackExhausted(1)) audioExhaustedWhileVideoContinued = true;
                if (packet.TrackId == 1) videoPacketsAfterAudioEnded++;
            }
        }

        //Assert - the point of the whole contract: the audio track is known to be over while the picture runs on
        audioPackets.Should().Be(49);
        audioExhaustedWhileVideoContinued.Should().BeTrue();
        videoPacketsAfterAudioEnded.Should().BeGreaterThan(0);
        reader.IsTrackExhausted(1).Should().BeTrue();
        reader.IsTrackExhausted(2).Should().BeTrue();
    }

    [Fact]
    public void The_bespoke_reader_treats_a_track_it_does_not_have_as_already_over()
    {
        //Arrange
        using CbvReader reader = new CbvReader(new FileMediaSource(TestAssets.Path("raw-synthetic.cbv")));

        //Act
        bool absentTrack = reader.IsTrackExhausted(99);
        TimeSpan? absentEnd = reader.GetTrackEndTimestamp(99);

        //Assert
        absentTrack.Should().BeTrue();
        absentEnd.HasValue.Should().BeFalse();
    }

    [Fact]
    public void A_backwards_seek_un_exhausts_the_tracks_it_rewound_past()
    {
        //Arrange
        using CbvReader reader = new CbvReader(new FileMediaSource(TestAssets.Path("raw-vorbis.cbv")));
        while (reader.TryReadPacket(out _))
        {
        }

        bool exhaustedAtEnd = reader.IsTrackExhausted(2);

        //Act
        reader.Seek(TimeSpan.Zero, 1);

        //Assert
        exhaustedAtEnd.Should().BeTrue();
        reader.IsTrackExhausted(2).Should().BeFalse();
        reader.IsTrackExhausted(1).Should().BeFalse();
    }

    [Fact]
    public void The_matroska_reader_refuses_to_claim_a_track_has_ended_until_it_is_certain()
    {
        //Arrange
        using MatroskaReader reader =
            new MatroskaReader(new FileMediaSource(TestAssets.Path("raw-vorbis-nocues.mkv")));

        int audioPackets = 0;
        bool claimedEarly = false;

        //Act
        while (reader.TryReadPacket(out MediaPacket packet))
        {
            if (packet.TrackId == 2) audioPackets++;

            // The audio really does stop a second before the picture does, and this reader must NOT say so:
            // nothing in Matroska records where a track stops, and guessing would truncate ordinary files.
            if (reader.IsTrackExhausted(2)) claimedEarly = true;
        }

        //Assert
        audioPackets.Should().Be(49);
        claimedEarly.Should().BeFalse();
        reader.IsTrackExhausted(1).Should().BeTrue();
        reader.IsTrackExhausted(2).Should().BeTrue();
    }

    [Fact]
    public void The_matroska_reader_gives_no_track_end_until_it_has_read_the_whole_file()
    {
        //Arrange
        using MatroskaReader reader =
            new MatroskaReader(new FileMediaSource(TestAssets.Path("raw-vorbis-nocues.mkv")));

        //Act
        reader.TryReadPacket(out _);
        bool knownEarly = reader.GetTrackEndTimestamp(2).HasValue;

        while (reader.TryReadPacket(out _))
        {
        }

        TimeSpan? audioEnd = reader.GetTrackEndTimestamp(2);
        TimeSpan? videoEnd = reader.GetTrackEndTimestamp(1);

        //Assert
        knownEarly.Should().BeFalse();
        audioEnd.HasValue.Should().BeTrue();
        videoEnd.HasValue.Should().BeTrue();
        audioEnd.Value.Should().BeLessThan(videoEnd.Value);
    }

    [Fact]
    public void The_matroska_reader_treats_a_track_it_does_not_have_as_already_over()
    {
        //Arrange
        using MatroskaReader reader = new MatroskaReader(new FileMediaSource(TestAssets.Path("av1-opus.webm")));

        //Act & Assert
        reader.IsTrackExhausted(99).Should().BeTrue();
        reader.IsTrackExhausted(1).Should().BeFalse();
    }
}
