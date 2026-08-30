using CodeBrix.Platform.Simple;
using SimpleCbxVideoPlayer.SkiaVideo.Assets;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using System;
using System.Globalization;

namespace SimpleCbxVideoPlayer.ViewModels;

/// <summary>One row of the lookup-table panel: a tick box, a name and a percentage.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public class LutListItem : SimpleViewModel
{
    private readonly Action changed;

    /// <summary>Creates the row from a catalogue entry.</summary>
    /// <param name="entry">The ".cube" file the catalogue found.</param>
    /// <param name="changed">Called whenever the tick or the percentage changes.</param>
    public LutListItem(LutCatalogEntry entry, Action changed)
    {
        this.changed = changed;

        DisplayName = entry.DisplayName;
        GroupName = entry.GroupName;
        FileName = entry.FileName;
        FilePath = entry.FullPath;
        Percent = LutChainEntry.DefaultApplyAtPercent;
        PercentText = FormatPercent(Percent);
    }

    /// <summary>The table's own title, or its file name when it has no title.</summary>
    public string DisplayName { get; }

    /// <summary>The corpus group the file came from: "generated" or "found".</summary>
    public string GroupName { get; }

    /// <summary>The file's own name.</summary>
    public string FileName { get; }

    /// <summary>The full path of the file.</summary>
    public string FilePath { get; }

    /// <summary>The second line of the row: where the file came from.</summary>
    public string SubtitleText => $"{GroupName}/{FileName}";

    /// <summary>Whether this table is in the chain.</summary>
    public bool IsChecked
    {
        get;
        set
        {
            if (field == value) { return; }

            SetProperty(ref field, value);
            changed?.Invoke();
        }
    }

    /// <summary>How much of the table to apply, 0 to 100.</summary>
    public double Percent { get; private set; }

    /// <summary>The percentage as it is typed, clamped into 0 to 100 when it is committed.</summary>
    public string PercentText
    {
        get;
        set
        {
            var committed = LutChainEntry.TryParsePercent(value, out var percent)
                ? percent
                : Percent;

            var text = FormatPercent(committed);

            //The text is normalized on the way in - "120" becomes "100" - so the box shows what is applied.
            SetProperty(ref field, text);

            if (Math.Abs(committed - Percent) < 0.0001) { return; }

            Percent = committed;
            NotifyPropertyChanged(nameof(Percent));

            if (IsChecked) { changed?.Invoke(); }
        }
    }

    private static string FormatPercent(double percent) =>
        percent.ToString("0.#", CultureInfo.CurrentCulture);
}
