using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.VideoPlayback.Containers;
using CodeBrix.VideoPlayback.Containers.Cbv;
using CodeBrix.VideoPlayback.Containers.Matroska;
using CodeBrix.VideoPlayback.Decoding;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Reads the sample-video corpus under <c>tests/assets/authoring</c> with this repository's own container
/// readers, and checks that every file is what the manifest beside it says it is.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is twenty-four files derived from two Public-Domain phone clips - three resolutions by four
/// container profiles - produced by <c>tools/CodeBrix.VideoPlayback.AssetAuthoring</c> through the authoring
/// library. It is tens of megabytes of real video, so a checkout need not have it: every test here skips
/// itself, naming the folder and the command that fills it, when the corpus is not present - exactly as the
/// golden-corpus tests do for a missing asset.
/// </para>
/// <para>
/// THREE OF THE FOUR FOLDERS ARE MATROSKA DOCUMENTS AND THE FOURTH IS NOT. MKV, WebM and CodeBrix-Mode1 are
/// all EBML: an off-the-shelf Matroska, an off-the-shelf WebM, and a WebM whose seek index has been moved to
/// the front. CodeBrix-Mode2 is the BESPOKE container - a <c>CBVF</c> file, index-first by construction, with
/// Vorbis rather than Opus so that playing one needs no extra package at all. Every test below that asks
/// something a Matroska document alone can answer branches on that.
/// </para>
/// <para>
/// Nothing here DECODES: there is no AV1 decoder in this repository's tests, and the point of these checks is
/// the container, not the picture. What is measured is what a reader learns before the first frame.
/// </para>
/// </remarks>
public class AuthoringCorpusTests
{
    private const int LandscapeSourceMilliseconds = 4033;

    private const int PortraitSourceMilliseconds = 5467;

    private static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(250);

    private static readonly string AuthoringRoot = FindAuthoringRoot();

    private static readonly string[] EveryFolder = { "MKV", "WebM", "CodeBrix-Mode1", "CodeBrix-Mode2" };

    private static readonly string[] EveryName =
    {
        "landscape_4k", "landscape_hd", "landscape_720p", "portrait_4k", "portrait_hd", "portrait_720p",
    };

    /// <summary>Every file in the corpus, with the frame size and duration it was authored to carry.</summary>
    public static TheoryData<string, int, int, int> EveryCorpusFile
    {
        get
        {
            TheoryData<string, int, int, int> data = new TheoryData<string, int, int, int>();

            foreach (string folder in EveryFolder)
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
    public static TheoryData<string> EveryMode1File => FilesIn("CodeBrix-Mode1");

    /// <summary>The six Mode2 files, which are the bespoke container rather than a Matroska one.</summary>
    public static TheoryData<string> EveryMode2File => FilesIn("CodeBrix-Mode2");

    /// <summary>The twelve off-the-shelf files, which are muxed the way any tool on the internet muxes them.</summary>
    public static TheoryData<string> EveryOffTheShelfFile
    {
        get
        {
            TheoryData<string> data = new TheoryData<string>();

            foreach (string folder in new[] { "MKV", "WebM" })
            {
                string extension = ExtensionFor(folder);
                foreach (string name in EveryName) data.Add($"{folder}/{name}{extension}");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_opens_as_the_container_its_folder_promises(
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
        using IMediaContainerReader reader = OpenCorpusFile(relativePath);

        //Assert
        if (IsBespoke(relativePath))
        {
            // Mode2 is NOT Matroska. It begins with 'CBVF', so the sniff hands it to the other reader
            // entirely - which is the whole reason the two flavours can share one extension.
            reader.Should().BeOfType<CbvReader>();
            reader.FormatName.Should().Be("CodeBrix Video (.cbv)");
        }
        else
        {
            reader.Should().BeOfType<MatroskaReader>();
            ((MatroskaReader)reader).DocType.Should().Be(ExpectedDocType(relativePath));
        }
    }

    [Theory]
    [MemberData(nameof(EveryCorpusFile))]
    public void Every_corpus_file_carries_one_av1_track_and_one_royalty_free_audio_track(
        string relativePath,
        int width,
        int height,
        int milliseconds)
    {
        //Arrange
        _ = width;
        _ = height;
        _ = milliseconds;
        using IMediaContainerReader reader = OpenCorpusFile(relativePath);

        //Act
        List<MediaTrackInfo> video = reader.Tracks.Where(track => track.Kind == MediaTrackKind.Video).ToList();
        List<MediaTrackInfo> audio = reader.Tracks.Where(track => track.Kind == MediaTrackKind.Audio).ToList();

        //Assert
        reader.Tracks.Count.Should().Be(2);
        video.Count.Should().Be(1);
        audio.Count.Should().Be(1);
        video[0].CodecId.Should().Be(VideoCodecIds.Av1);

        // Opus in the three FFmpeg-muxed folders; Vorbis in Mode2, so that an application shipping a Mode2
        // clip inside itself needs no Opus package at all.
        audio[0].CodecId.Should().Be(IsBespoke(relativePath) ? VideoCodecIds.Vorbis : VideoCodecIds.Opus);
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
        using IMediaContainerReader reader = OpenCorpusFile(relativePath);

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
        using IMediaContainerReader reader = OpenCorpusFile(relativePath);

        //Act
        TimeSpan duration = reader.Duration;

        //Assert
        if (reader is MatroskaReader matroska) matroska.HasDeclaredDuration.Should().BeTrue();
        duration.Should().BeGreaterThan(TimeSpan.Zero);

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
        using IMediaContainerReader reader = OpenCorpusFile(relativePath);

        //Act
        //Assert
        if (reader is CbvReader cbv)
        {
            cbv.Index.Count.Should().BeGreaterThan(0);
        }
        else
        {
            MatroskaReader matroska = (MatroskaReader)reader;
            matroska.HasIndex.Should().BeTrue();
            matroska.Cues.Count.Should().BeGreaterThan(0);
        }
    }

    [Theory]
    [MemberData(nameof(EveryMode1File))]
    public void Every_mode1_file_puts_its_cues_before_the_first_cluster(string relativePath)
    {
        //Arrange
        using MatroskaReader reader = (MatroskaReader)OpenCorpusFile(relativePath);

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
        using MatroskaReader reader = (MatroskaReader)OpenCorpusFile(relativePath);

        //Act
        string docType = reader.DocType;

        //Assert
        docType.Should().Be("webm");
        reader.HasUnknownSizeElements.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EveryMode2File))]
    public void Every_mode2_file_puts_its_index_in_front_of_the_media_data(string relativePath)
    {
        //Arrange
        using CbvReader reader = (CbvReader)OpenCorpusFile(relativePath);

        //Act
        bool indexFirst = (reader.Flags & CbvHeaderFlags.HasIndex) != 0;

        //Assert
        indexFirst.Should().BeTrue();
        reader.Index.Count.Should().BeGreaterThan(0);
        reader.HeaderChecksumVerified.Should().BeTrue();
        reader.IndexChecksumVerified.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EveryMode2File))]
    public void Every_mode2_file_carries_vorbis_audio_so_it_needs_no_extra_package(string relativePath)
    {
        //Arrange
        using CbvReader reader = (CbvReader)OpenCorpusFile(relativePath);

        //Act
        MediaTrackInfo audio = reader.Tracks.Single(track => track.Kind == MediaTrackKind.Audio);

        //Assert
        audio.CodecId.Should().Be(VideoCodecIds.Vorbis);
        audio.SampleRate.Should().Be(48000);
        audio.Channels.Should().Be(2);

        // Asked, not started: this opens no audio device, and it is the question an application asks before
        // deciding whether it needs the Opus package.
        AudioDecoders.IsCodecSupported(audio.CodecId).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EveryMode2File))]
    public void Every_mode2_file_passes_the_streamable_profile(string relativePath)
    {
        //Arrange
        string path = ResolveCorpusFile(relativePath);

        //Act
        StreamableProfileReport report = StreamableProfile.EvaluateFile(path);

        //Assert
        report.Passes.Should().BeTrue();
        report.Verdict.Should().Be("passes the profile");
    }

    [Theory]
    [MemberData(nameof(EveryOffTheShelfFile))]
    public void Every_off_the_shelf_file_leaves_its_cues_at_the_end(string relativePath)
    {
        //Arrange
        using MatroskaReader reader = (MatroskaReader)OpenCorpusFile(relativePath);

        //Act
        bool cuesFirst = reader.CuesPrecedeFirstCluster;

        //Assert
        cuesFirst.Should().BeFalse();
    }

    private static TheoryData<string> FilesIn(string folder)
    {
        TheoryData<string> data = new TheoryData<string>();
        string extension = ExtensionFor(folder);
        foreach (string name in EveryName) data.Add($"{folder}/{name}{extension}");
        return data;
    }

    private static bool IsBespoke(string relativePath) =>
        relativePath.StartsWith("CodeBrix-Mode2/", StringComparison.Ordinal);

    private static IMediaContainerReader OpenCorpusFile(string relativePath)
    {
        // The library sniffs the first four bytes and picks the reader; the reader owns the source, so
        // disposing it closes the file - which is what every other container test here relies on too.
        return MediaContainers.Open(ResolveCorpusFile(relativePath));
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
            case "CodeBrix-Mode2": return ".cbv";
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
