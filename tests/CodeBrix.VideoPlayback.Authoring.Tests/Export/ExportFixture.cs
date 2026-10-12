using System;
using System.IO;

namespace CodeBrix.VideoPlayback.Authoring.Tests.Export;

/// <summary>
/// The masters and backdrops the export tests share, authored once per test class and only when a test first asks
/// for one - so a machine without ffmpeg skips instead of failing in the fixture.
/// </summary>
public sealed class ExportFixture : IDisposable
{
    private readonly object gate = new object();
    private readonly WorkFolder work = new WorkFolder("export-fixture");
    private string halves;
    private string silentHalves;
    private string mutedHalves;
    private string halfRed;

    /// <summary>The fixture's work folder.</summary>
    public string Folder => work.Path;

    /// <summary>A file in the work folder.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The path.</returns>
    public string File(string name) => work.File(name);

    /// <summary>A 1 s master: left half transparent black, right half opaque white, with a 440 Hz tone.</summary>
    public string Halves => Make(ref halves, "halves", 0);

    /// <summary>The same picture, flagged Silent.</summary>
    public string SilentHalves => Make(ref silentHalves, "halves-silent", 1);

    /// <summary>The same picture with no audio track and no Silent flag.</summary>
    public string MutedHalves => Make(ref mutedHalves, "halves-muted", 2);

    /// <summary>A 1 s master of BT.601 red at a constant alpha of 128, no sound.</summary>
    public string HalfRed
    {
        get
        {
            lock (gate)
            {
                if (halfRed != null) return halfRed;
                string source = ExportTestMedia.WriteHalfTransparentRedSource(work.File("half-red.mkv"));
                halfRed = ExportTestMedia.AuthorMaster(source, work.File("half-red.cbvmaster"), work.Path, 2);
                return halfRed;
            }
        }
    }

    private string Make(ref string field, string name, int audio)
    {
        lock (gate)
        {
            if (field != null) return field;
            string source = work.File("halves-source.mkv");
            if (!System.IO.File.Exists(source)) ExportTestMedia.WriteHalvesSource(source, 1, true);
            field = ExportTestMedia.AuthorMaster(source, work.File(name + ".cbvmaster"), work.Path, audio);
            return field;
        }
    }

    /// <inheritdoc />
    public void Dispose() => work.Dispose();
}
