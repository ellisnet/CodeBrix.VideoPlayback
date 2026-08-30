using SilverAssertions;
using SimpleCbxVideoPlayer.SkiaVideo.Assets;
using System.IO;
using System.Linq;
using Xunit;

namespace SimpleCbxVideoPlayer.SkiaVideo.Tests;

public class VideoCorpusTests
{
    [Fact]
    public void Scan_lists_the_four_playable_folders_and_leaves_out_mp4()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var authoring = temp.CreateFolder("authoring");
        temp.CreateFile(temp.CreateFolder("authoring", "MKV"), "landscape_hd.mkv");
        temp.CreateFile(temp.CreateFolder("authoring", "WebM"), "landscape_hd.webm");
        temp.CreateFile(temp.CreateFolder("authoring", "CodeBrix-Mode1"), "landscape_hd.cbv");
        temp.CreateFile(temp.CreateFolder("authoring", "CodeBrix-Mode2"), "landscape_hd.cbv");
        temp.CreateFile(temp.CreateFolder("authoring", "MP4"), "landscape_hd.mp4");

        //Act
        var items = VideoCorpus.Scan(authoring);

        //Assert
        items.Count.Should().Be(4);
        items.Any(item => item.FolderName == "MP4").Should().BeFalse();
    }

    [Fact]
    public void Scan_picks_up_a_new_folder_without_a_code_change()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var authoring = temp.CreateFolder("authoring");
        temp.CreateFile(temp.CreateFolder("authoring", "MKV"), "landscape_hd.mkv");
        temp.CreateFile(temp.CreateFolder("authoring", "CodeBrix-Mode3"), "landscape_hd.cbv");

        //Act
        var items = VideoCorpus.Scan(authoring);

        //Assert
        items.Count.Should().Be(2);
        items.Any(item => item.FolderName == "CodeBrix-Mode3").Should().BeTrue();
    }

    [Fact]
    public void Scan_leaves_out_files_this_application_cannot_open()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var authoring = temp.CreateFolder("authoring");
        var folder = temp.CreateFolder("authoring", "MKV");
        temp.CreateFile(folder, "clip.mkv");
        temp.CreateFile(folder, "clip.mkv.probe.json");
        temp.CreateFile(folder, "notes.txt");
        temp.CreateFile(folder, "clip.mp4");

        //Act
        var items = VideoCorpus.Scan(authoring);

        //Assert
        items.Count.Should().Be(1);
        items[0].FileName.Should().Be("clip.mkv");
    }

    [Fact]
    public void Scan_orders_by_folder_and_then_by_file_name()
    {
        //Arrange
        using TempFolder temp = new TempFolder();
        var authoring = temp.CreateFolder("authoring");
        temp.CreateFile(temp.CreateFolder("authoring", "WebM"), "b.webm");
        temp.CreateFile(temp.CreateFolder("authoring", "WebM"), "a.webm");
        temp.CreateFile(temp.CreateFolder("authoring", "MKV"), "z.mkv");

        //Act
        var items = VideoCorpus.Scan(authoring);

        //Assert
        items.Select(item => item.DisplayName).Should().BeEquivalentTo(["MKV/z.mkv", "WebM/a.webm", "WebM/b.webm"]);
    }

    [Fact]
    public void Scan_of_a_missing_folder_is_empty_rather_than_a_failure()
    {
        //Act
        var items = VideoCorpus.Scan(Path.Combine(Path.GetTempPath(), "no-such-corpus-folder-at-all"));

        //Assert
        items.Count.Should().Be(0);
    }

    [Fact]
    public void IsPlayable_accepts_the_three_containers_and_nothing_else()
    {
        //Assert
        VideoCorpus.IsPlayable("a.mkv").Should().BeTrue();
        VideoCorpus.IsPlayable("a.WEBM").Should().BeTrue();
        VideoCorpus.IsPlayable("a.cbv").Should().BeTrue();
        VideoCorpus.IsPlayable("a.mp4").Should().BeFalse();
        VideoCorpus.IsPlayable("a.ivf").Should().BeFalse();
        VideoCorpus.IsPlayable(null).Should().BeFalse();
    }

    [Fact]
    public void Scan_of_the_real_corpus_finds_the_twenty_four_derived_files()
    {
        //Arrange
        var root = RepositoryAssets.FindRepositoryRoot();
        Assert.SkipWhen(root == null, "This test reads the repository's own corpus, which is not beside the test assembly.");

        //Act
        var items = VideoCorpus.Scan(RepositoryAssets.GetAuthoringFolder(root));

        //Assert
        items.Count.Should().Be(24);
        items.Count(item => item.FolderName == "MKV").Should().Be(6);
        items.Count(item => item.FolderName == "WebM").Should().Be(6);
        items.Count(item => item.FolderName == "CodeBrix-Mode1").Should().Be(6);
        items.Count(item => item.FolderName == "CodeBrix-Mode2").Should().Be(6);
        items.Any(item => item.FileName.EndsWith(".mp4")).Should().BeFalse();
    }
}
