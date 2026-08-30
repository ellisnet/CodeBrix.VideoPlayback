using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CodeBrix.Audio.Opus;
using CodeBrix.VideoPlayback.Authoring.Encoding;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Dav1d;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Frames;
using CodeBrix.VideoPlayback.Playback;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Authoring.Tests;

/// <summary>
/// PLAYS what the authoring library writes, with a real AV1 decoder.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate that turns "a file was written and its structure reads back" into "a file is a video".
/// The decoder is CodeBrix.VideoPlayback.Dav1d, referenced by this TEST project only - the authoring library
/// itself has no decoder, no drawing surface and no opinion about either.
/// </para>
/// <para>
/// No audio device is opened: the sessions are built with PlayAudio false, which is this family's headless
/// pattern. What IS asserted about the sound is the thing that costs an application a package - that a
/// Vorbis track is decodable by the shared audio output with the Opus package never registered.
/// </para>
/// </remarks>
public class AuthoringPlaybackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The six bespoke corpus files, which are played straight out of the repository.</summary>
    public static TheoryData<string> EveryMode2CorpusFile
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();

            foreach (string name in new[]
                     {
                         "landscape_4k", "landscape_hd", "landscape_720p",
                         "portrait_4k", "portrait_hd", "portrait_720p",
                     })
            {
                data.Add("CodeBrix-Mode2/" + name + ".cbv");
            }

            return data;
        }
    }

    [Fact]
    public void An_authored_webm_profile_file_decodes_to_frames()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("play-webm");
        string path = Author(VideoAuthoringFlavour.WebMProfile, work);

        //Act
        PlaybackTally tally = PlayThrough(path, 0);

        //Assert
        tally.Frames.Should().BeGreaterThanOrEqualTo(15);
        tally.DistinctHashes.Should().BeGreaterThan(1);
        tally.Failure.Should().BeNull();
    }

    [Fact]
    public void An_authored_bespoke_file_decodes_to_frames()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("play-bespoke");
        string path = Author(VideoAuthoringFlavour.Bespoke, work);

        //Act
        PlaybackTally tally = PlayThrough(path, 0);

        //Assert
        tally.Frames.Should().BeGreaterThanOrEqualTo(15);
        tally.DistinctHashes.Should().BeGreaterThan(1);
        tally.Failure.Should().BeNull();
    }

    [Fact]
    public void A_graded_bespoke_file_decodes_to_frames()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("play-graded");

        string warm = AuthoringTestAssets.Lut("warm_33.cube");
        string cool = AuthoringTestAssets.Lut("cool_33.cube");
        Assert.SkipUnless(
            File.Exists(warm) && File.Exists(cool),
            "The generated lookup tables are not beside the test assembly.");

        string source = SyntheticSource.WriteClip(work.File("source.mkv"));
        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = VideoAuthoringFlavour.Bespoke,
            SourcePath = source,
            OutputPath = work.File("graded.cbv"),
            TemporaryFolder = work.Path,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;
        request.Video.Luts.Add(new Effects.AuthoringLutInput(warm, 40));
        request.Video.Luts.Add(new Effects.AuthoringLutInput(cool, 40));

        //Act
        VideoAuthoringResult result = CbvAuthor.Write(request);
        PlaybackTally tally = PlayThrough(result.OutputPath, 0);

        //Assert
        result.ComposedLutSize.Should().BeGreaterThan(0);
        tally.Frames.Should().BeGreaterThanOrEqualTo(15);
        tally.Failure.Should().BeNull();
    }

    [Fact]
    public void An_authored_bespoke_file_carries_vorbis_that_needs_no_opus_package()
    {
        //Arrange
        SyntheticSource.SkipWithoutFFmpeg();
        using WorkFolder work = new WorkFolder("play-vorbis");
        string path = Author(VideoAuthoringFlavour.Bespoke, work);

        //Act
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        MediaTrackInfo audio = null;
        foreach (MediaTrackInfo track in session.Tracks)
        {
            if (track.Kind == Containers.MediaTrackKind.Audio) audio = track;
        }

        //Assert
        audio.Should().NotBeNull();
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);

        // The claim, made where it can be checked: this file's sound is decodable with the Opus package
        // never registered. Asking costs no audio device.
        CodeBrixAudioOpus.IsRegistered.Should().BeFalse();
        AudioDecoders.IsCodecSupported(audio.CodecId).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EveryMode2CorpusFile))]
    public void Every_mode2_corpus_file_decodes_to_frames(string relativePath)
    {
        //Arrange
        string path = ResolveCorpusFile(relativePath);

        //Act
        PlaybackTally tally = PlayThrough(path, 12);

        //Assert
        tally.Frames.Should().BeGreaterThanOrEqualTo(12);
        tally.DistinctHashes.Should().BeGreaterThan(1);
        tally.Failure.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(EveryMode2CorpusFile))]
    public void Every_mode2_corpus_file_carries_vorbis_that_needs_no_opus_package(string relativePath)
    {
        //Arrange
        string path = ResolveCorpusFile(relativePath);

        //Act
        using VideoPlaybackSession session = NewSession();
        session.Open(path);
        MediaTrackInfo audio = null;
        foreach (MediaTrackInfo track in session.Tracks)
        {
            if (track.Kind == Containers.MediaTrackKind.Audio) audio = track;
        }

        //Assert
        audio.Should().NotBeNull();
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);
        CodeBrixAudioOpus.IsRegistered.Should().BeFalse();
        AudioDecoders.IsCodecSupported(audio.CodecId).Should().BeTrue();
    }

    private static string Author(VideoAuthoringFlavour flavour, WorkFolder work)
    {
        string source = SyntheticSource.WriteClip(work.File("source.mkv"));

        VideoAuthoringRequest request = new VideoAuthoringRequest
        {
            Flavour = flavour,
            SourcePath = source,
            OutputPath = work.File("clip.cbv"),
            TemporaryFolder = work.Path,
        };

        AuthoringEncoders.ApplyFastest(request.Video);
        request.Video.ConstantRateFactor = 50;
        request.Video.KeyframeIntervalFrames = 5;
        request.Audio.BitrateKilobitsPerSecond = 96;

        return CbvAuthor.Write(request).OutputPath;
    }

    private static VideoPlaybackSession NewSession()
    {
        // The headless pattern: no audio device, and the decoder registered on this session only so that
        // nothing here disturbs the process-wide registry another test may be reading.
        VideoPlaybackSession session = new VideoPlaybackSession(new VideoPlaybackOptions { PlayAudio = false });
        CodeBrixVideoPlaybackDav1d.Register(session);
        return session;
    }

    private static PlaybackTally PlayThrough(string path, int stopAfterFrames)
    {
        PlaybackTally tally = new PlaybackTally();
        HashSet<ulong> hashes = new HashSet<ulong>();

        using VideoPlaybackSession session = NewSession();
        session.MediaFailed += (s, e) => tally.Failure = e.Exception == null ? "media failed" : e.Exception.Message;

        int ended = 0;
        session.PlaybackEnded += (s, e) => Interlocked.Increment(ref ended);

        session.Open(path);
        session.Play();

        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < Timeout)
        {
            while (session.Presenter.TryTakeLatest(out VideoFrame frame))
            {
                using (frame)
                {
                    tally.Frames++;
                    hashes.Add(HashLuma(frame));
                }
            }

            if (tally.Failure != null) break;
            if (stopAfterFrames > 0 && tally.Frames >= stopAfterFrames) break;
            if (Volatile.Read(ref ended) > 0 && !session.Presenter.HasFrame) break;

            Thread.Sleep(2);
        }

        session.Stop();
        tally.DistinctHashes = hashes.Count;
        return tally;
    }

    private static ulong HashLuma(VideoFrame frame)
    {
        VideoFramePlane luma = frame.Y;
        ulong hash = 14695981039346656037UL;

        for (int row = 0; row < luma.Height; row += 4)
        {
            ReadOnlySpan<byte> line = luma.GetRowBytes(row);

            for (int column = 0; column < line.Length; column += 8)
            {
                hash = (hash ^ line[column]) * 1099511628211UL;
            }
        }

        return hash;
    }

    private static string ResolveCorpusFile(string relativePath)
    {
        string root = FindAuthoringRoot();
        Assert.SkipWhen(root == null, "The repository's sample-video corpus folder was not found.");

        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.SkipUnless(
            File.Exists(path),
            "The authoring corpus file '" + path + "' has not been generated. Run "
            + "'dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release -- --only CodeBrix-Mode2'.");

        return path;
    }

    private static string FindAuthoringRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "assets", "authoring");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private sealed class PlaybackTally
    {
        internal int Frames;

        internal int DistinctHashes;

        internal string Failure;
    }
}
