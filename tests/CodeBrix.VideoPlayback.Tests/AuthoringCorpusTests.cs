using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Reads the sample-video corpus under <c>tests/assets/authoring</c> with this repository's own Matroska
/// reader, and checks that every file is what the manifest beside it says it is.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is eighteen files derived from two Public-Domain phone clips - three resolutions by three
/// container profiles - and it is produced by <c>tools/CodeBrix.VideoPlayback.AssetAuthoring</c> rather than
/// by the synthetic generators the golden corpus uses. It is tens of megabytes of real video, so a checkout
/// need not have it: every test here skips itself, naming the folder and the command that fills it, when the
/// corpus is not present - exactly as the golden-corpus tests do for a missing asset.
/// </para>
/// <para>
/// Nothing here DECODES: there is no AV1 decoder in this repository's tests, and the point of these checks is
/// the container, not the picture. What is measured is what a reader learns before the first frame - the
/// track codecs, the frame size the container declares, that a duration is stated at all, and, for the Mode1
/// files, that the seek index sits before the first cluster so the first scrub costs no second read.
/// </para>
/// </remarks>
public class AuthoringCorpusTests
{
    private const int LandscapeSourceMilliseconds = 4033;

    private const int PortraitSourceMilliseconds = 5467;

    private static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(250);

    private static readonly string AuthoringRoot = FindAuthoringRoot();

    /// <summary>Every file in the corpus, with the frame size and duration it was authored to carry.</summary>
    public static TheoryData<string, int, int, int> EveryCorpusFile
    {
        get
        {
            TheoryData<string, int, int, int> data = new TheoryData<string, int, int, int>();

            foreach (string folder in new[] { "MKV", "WebM", "CodeBrix-Mode1" })
            {
                string extension = ExtensionFor(folder);

                data.Add($"{folder}/landscape_4k{extension}", 3840, 2160, LandscapeSourceMilliseconds);
                data.Add($"{folder}/landscape_hd{extension}", 1920, 1080, LandscapeSourceMilliseconds);
                data.Add($"{folder}/landscape_720p{extension}", 1280, 720, LandscapeSourceMilliseconds);
                data.Add($"{folder}/portrait_4k{extension}", 2160, 3840, PortraitSourceMilliseconds);
                data.Add($"{folder}/portrait_hd{extension}", 1080, 1920, PortraitSourceMilliseconds);
                data.Add($"{folder}/portrait_720p{extension}", 720, 1280, PortraitSourceMilliseconds);
            }

            return data;
        }
    }

    /// <summary>The six Mode1 files, whose seek index must sit at the front.</summary>
    public static TheoryData<string> EveryMode1File
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();
            foreach (string name in new[] { "landscape_4k", "landscape_hd", "landscape_720p", "portrait_4k", "portrait_hd", "portrait_720p" })
            {
                data.Add($"CodeBrix-Mode1/{name}.cbv");
            }

            return data;
        }
    }

    /// <summary>The twelve off-the-shelf files, which are muxed the way any tool on the internet muxes them.</summary>
    public static TheoryData<string> EveryOffTheShelfFile
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();
            foreach (string folder in new[] { "MKV", "WebM" })
            {
                string extension = ExtensionFor(folder);
                foreach (string name in new[] { "landscape_4k", "landscape_hd", "landscape_720p", "portrait_4k", "portrait_hd", "portrait_720p" })
                {
                    data.Add($"{folder}/{name}{extension}");
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_opens_as_a_matroska_document(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = width;
        _ = height;
        _ = milliseconds;

        //Act
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Assert
        reader.DocType.Should().Be(ExpectedDocType(relativePath));
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_carries_one_av1_track_and_one_opus_track(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = width;
        _ = height;
        _ = milliseconds;
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        List<MediaTrackInfo> video = reader.Tracks.Where(track => track.Kind == MediaTrackKind.Video).ToList();
        List<MediaTrackInfo> audio = reader.Tracks.Where(track => track.Kind == MediaTrackKind.Audio).ToList();

        //Assert
        reader.Tracks.Count.Should().Be(2);
        video.Count.Should().Be(1);
        audio.Count.Should().Be(1);
        video[0].CodecId.Should().Be(VideoCodecIds.Av1);
        audio[0].CodecId.Should().Be(VideoCodecIds.Opus);
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_declares_the_frame_size_it_was_authored_to(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = milliseconds;
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        MediaTrackInfo video = reader.Tracks.Single(track => track.Kind == MediaTrackKind.Video);

        //Assert
        video.Width.Should().Be(width);
        video.Height.Should().Be(height);
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_states_the_duration_of_the_clip_it_came_from(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = width;
        _ = height;
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        TimeSpan duration = reader.Duration;

        //Assert
        reader.HasDeclaredDuration.Should().BeTrue();

        TimeSpan drift = duration - TimeSpan.FromMilliseconds(milliseconds);
        if (drift < TimeSpan.Zero) drift = drift.Negate();
        drift.Should().BeLessThanOrEqualTo(DurationTolerance);
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_carries_a_seek_index(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = width;
        _ = height;
        _ = milliseconds;
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        bool indexed = reader.HasIndex;

        //Assert
        indexed.Should().BeTrue();
        reader.Cues.Count.Should().BeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(EveryMode1File))]
    public void Every_mode1_file_puts_its_cues_before_the_first_cluster(string relativePath)
    {
        //Arrange
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        bool cuesFirst = reader.CuesPrecedeFirstCluster;

        //Assert
        cuesFirst.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EveryMode1File))]
    public void Every_mode1_file_is_a_webm_document_despite_its_extension(string relativePath)
    {
        //Arrange
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        string docType = reader.DocType;

        //Assert
        docType.Should().Be("webm");
        reader.HasUnknownSizeElements.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EveryOffTheShelfFile))]
    public void Every_off_the_shelf_file_leaves_its_cues_at_the_end(string relativePath)
    {
        //Arrange
        using MatroskaReader reader = OpenCorpusFile(relativePath);

        //Act
        bool cuesFirst = reader.CuesPrecedeFirstCluster;

        //Assert
        cuesFirst.Should().BeFalse();
    }

    private static MatroskaReader OpenCorpusFile(string relativePath)
    {
        string path = ResolveCorpusFile(relativePath);

        // The reader owns the source, so disposing the reader closes the file - which is what every other
        // container test in this project relies on too.
        return new MatroskaReader(new FileMediaSource(path));
    }

    private static string ResolveCorpusFile(string relativePath)
    {
        string folder = relativePath.Substring(0, relativePath.IndexOf('/'));
        string directory = Path.Combine(AuthoringRoot, folder);

        Assert.SkipUnless(
            Directory.Exists(directory),
            $"The authoring corpus folder '{directory}' has not been generated. Run "
            + "'dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release'.");

        string path = Path.Combine(AuthoringRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.SkipUnless(
            File.Exists(path),
            $"The authoring corpus file '{path}' has not been generated. Run "
            + "'dotnet run --project tools/CodeBrix.VideoPlayback.AssetAuthoring -c Release'.");

        return path;
    }

    private static string ExtensionFor(string folder)
    {
        switch (folder)
        {
            case "MKV": return ".mkv";
            case "WebM": return ".webm";
            case "CodeBrix-Mode1": return ".cbv";
            default: throw new ArgumentOutOfRangeException(nameof(folder));
        }
    }

    private static string ExpectedDocType(string relativePath) =>
        relativePath.StartsWith("MKV/", StringComparison.Ordinal) ? "matroska" : "webm";

    // The corpus is far too big to copy beside the test assembly, so it is always found by walking up to the
    // repository and reading it in place - which is also how the generator that wrote it found it.
    private static string FindAuthoringRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "assets", "authoring");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "assets", "authoring");
    }
}
