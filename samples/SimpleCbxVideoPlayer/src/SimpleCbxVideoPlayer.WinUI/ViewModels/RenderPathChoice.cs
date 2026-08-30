using SimpleCbxVideoPlayer.SkiaVideo.Playback;

namespace SimpleCbxVideoPlayer.ViewModels;

/// <summary>One row of the render-path drop-down.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public class RenderPathChoice
{
    /// <summary>Creates the row.</summary>
    /// <param name="option">The render path the row selects.</param>
    /// <param name="label">What the drop-down shows.</param>
    /// <param name="description">A sentence explaining what the choice does.</param>
    public RenderPathChoice(VideoRenderPathOption option, string label, string description)
    {
        Option = option;
        Label = label;
        Description = description;
    }

    /// <summary>The render path the row selects.</summary>
    public VideoRenderPathOption Option { get; }

    /// <summary>What the drop-down shows.</summary>
    public string Label { get; }

    /// <summary>A sentence explaining what the choice does.</summary>
    public string Description { get; }

    /// <inheritdoc />
    public override string ToString() => Label;
}
