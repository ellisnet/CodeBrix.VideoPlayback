using CodeBrix.Platform.Simple;
using System.Windows;
using SimpleCbxVideoPlayer.SkiaVideo;
using SimpleCbxVideoPlayer.SkiaVideo.Assets;
using SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;
using SimpleCbxVideoPlayer.SkiaVideo.Effects;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleCbxVideoPlayer.ViewModels;

/// <summary>
/// The whole application: the corpus drop-down, the render-path drop-down, the transport and the
/// lookup-table panel.
/// </summary>
public class MainViewModel : SimpleViewModel
{
    private readonly VideoPlaybackController controller;
    private readonly SmokeOptions smoke;
    private readonly List<LutCatalogEntry> lutCatalogue = [];

    private bool isSyncingPosition;
    private bool hasStartedSmokeRun;
    private int playbackEndedCount;
    private string pendingSnapshotPath;
    private TaskCompletionSource<ComposedFrameSnapshot> pendingSnapshot;
    private TaskCompletionSource<bool> playbackEnded;

    /// <summary>Creates the view model, reads the corpus and registers the decoders.</summary>
    public MainViewModel()
    {
        if (IsDesignMode(true)) { return; } //Leave as the first line of constructor

        smoke = SmokeOptions.FromCommandLine();

        //One call registers the AV1 decoder and the Opus audio decoder; it is the only start-up work.
        SkiaVideoRuntime.Initialize();

        controller = new VideoPlaybackController(smoke.PlayAudio);
        controller.Invalidated += (_, _) => InvalidateVideoCanvas?.Invoke();
        controller.MediaOpened += (_, _) => InvokeOnMainThread(OnMediaOpened);
        controller.PlaybackEnded += (_, _) => InvokeOnMainThread(OnPlaybackEnded);
        controller.PositionChanged += (_, args) => InvokeOnMainThread(() => OnPositionChanged(args));
        controller.Failed += (_, args) => InvokeOnMainThread(() => ShowMessage(args.Message));
        controller.RenderPathChanged += (_, _) => InvokeOnMainThread(UpdateUiState);

        RenderPathChoices.Add(new RenderPathChoice(
            VideoRenderPathOption.GpuAuto,
            "GPU (auto)",
            "Compose on the graphics device when there is one, and on the processor when there is not."));
        RenderPathChoices.Add(new RenderPathChoice(
            VideoRenderPathOption.GpuNoFallback,
            "GPU only (no fallback)",
            "Insist on the graphics device, and show the failure instead of degrading quietly."));
        RenderPathChoices.Add(new RenderPathChoice(
            VideoRenderPathOption.Cpu,
            "CPU",
            "Compose on the processor. Lookup tables are not applied on this path."));

        SelectedRenderPath = RenderPathChoices[0];

        LoadCorpus();

        if (!SkiaVideoRuntime.IsInitialized) { ShowMessage(SkiaVideoRuntime.ErrorMessage); }

        Debug.WriteLine($"SimpleCbxVideoPlayer startup: {SkiaVideoRuntime.Summary}");
    }

    #region | Bindable properties |

    /// <summary>Every playable file the corpus scan found.</summary>
    public ObservableCollection<VideoListItem> Videos { get; } = new();

    /// <summary>Every ".cube" table the corpus scan found.</summary>
    public ObservableCollection<LutListItem> Luts { get; } = new();

    /// <summary>The three render paths the presenter offers.</summary>
    public ObservableCollection<RenderPathChoice> RenderPathChoices { get; } = new();

    /// <summary>The file the drop-down is showing.</summary>
    [AffectsCommands(nameof(PlayCommand), nameof(PauseCommand), nameof(StopCommand))]
    public VideoListItem SelectedVideo
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) { return; }

            SetProperty(ref field, value);
            OnSelectedVideoChanged();
        }
    }

    /// <summary>The render path the drop-down is showing.</summary>
    public RenderPathChoice SelectedRenderPath
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) { return; }

            SetProperty(ref field, value);

            if (value != null && controller != null)
            {
                controller.RenderPath = value.Option;
                UpdateUiState();
            }
        }
    }

    /// <summary>Where playback has reached, in seconds - the scrub bar's value.</summary>
    public double PositionSeconds
    {
        get;
        set
        {
            //No SetProperty overload takes a double; compare-and-notify by hand.
            if (field.Equals(value)) { return; }

            field = value;
            NotifyPropertyChanged(nameof(PositionSeconds));
            NotifyPropertyChanged(nameof(TimeText));

            if (isSyncingPosition || controller == null || !controller.IsOpen) { return; }

            controller.Seek(TimeSpan.FromSeconds(value));
        }
    }

    /// <summary>How long the open file is, in seconds - the scrub bar's maximum.</summary>
    public double DurationSeconds
    {
        get;
        private set
        {
            //No SetProperty overload takes a double; compare-and-notify by hand.
            if (field.Equals(value)) { return; }

            field = value;
            NotifyPropertyChanged(nameof(DurationSeconds));
            NotifyPropertyChanged(nameof(TimeText));
        }
    } = 1;

    /// <summary>The clock beside the scrub bar.</summary>
    public string TimeText =>
        $"{TimeSpan.FromSeconds(PositionSeconds):mm\\:ss} / {TimeSpan.FromSeconds(DurationSeconds):mm\\:ss}";

    /// <summary>What the render path is doing, in one line.</summary>
    public string RenderPathText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "Render path: settling…";

    /// <summary>Where the sample corpus was found.</summary>
    public string CorpusText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>The most recent message from the video libraries, shown word for word.</summary>
    public string MessageText
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            NotifyPropertyChanged(nameof(MessageVisibility));
        }
    } = string.Empty;

    /// <summary>Whether there is a message to show.</summary>
    public Visibility MessageVisibility =>
        string.IsNullOrWhiteSpace(MessageText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Whether the lookup-table panel accepts input. False whenever the picture is composed on the
    /// processor, because this application applies tables on the graphics path only.
    /// </summary>
    public bool IsLutPanelEnabled
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            NotifyPropertyChanged(nameof(LutPanelNoteVisibility));
        }
    } = true;

    /// <summary>The note that appears over the lookup-table panel when it is greyed out.</summary>
    public string LutPanelNote
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>Whether the note over the lookup-table panel is showing.</summary>
    public Visibility LutPanelNoteVisibility =>
        IsLutPanelEnabled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What the last bake wrote, and where.</summary>
    public string BakeStatusText
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            NotifyPropertyChanged(nameof(BakeStatusVisibility));
        }
    } = string.Empty;

    /// <summary>Whether anything has been baked to show.</summary>
    public Visibility BakeStatusVisibility =>
        string.IsNullOrWhiteSpace(BakeStatusText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What the panel holds, and whether it is waiting for Play.</summary>
    public string LutSummaryText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "No lookup tables applied";

    #endregion

    #region | The host's seams |

    /// <summary>Set by the page: asks the canvas that is showing to repaint.</summary>
    /// <remarks>Raised from the decoding thread, so the page marshals it to the user-interface thread.</remarks>
    public Action InvalidateVideoCanvas { get; set; }

    /// <summary>
    /// Set by the head: asks the person where to write a baked ".cube" file, and hands back the full path
    /// they chose, or null when they cancelled.
    /// </summary>
    /// <remarks>
    /// The save dialog belongs to the head because every platform's is its own - Win32 on WPF, the file
    /// picker on WinUI and on the CodeBrix.Platform heads. What they all share is the rule: a bake goes
    /// where the person says it goes, and nowhere otherwise.
    /// </remarks>
    public Func<string, Task<string>> PickSaveCubePathAsync { get; set; }

    /// <summary>Hands the player the graphics context the host's GPU canvas created.</summary>
    /// <param name="context">The context, or null when the host has none.</param>
    public void SetGraphicsContext(GRContext context) => controller?.SetGraphicsContext(context);

    /// <summary>Draws the video into the canvas the host's paint handler supplied.</summary>
    /// <param name="canvas">The canvas to draw into.</param>
    /// <param name="bounds">The rectangle the video should occupy.</param>
    public void DrawVideo(SKCanvas canvas, SKRect bounds)
    {
        controller?.Draw(canvas, bounds);

        //A capture belongs here, inside the paint handler, where the graphics context is current.
        CompletePendingSnapshot();
    }

    /// <summary>
    /// Told by the page which canvas it settled on, once the graphics canvas has had its chance to start.
    /// </summary>
    /// <param name="gpuCanvasAvailable">True when GPU Skia started and the graphics canvas is showing.</param>
    public void OnVideoSurfaceReady(bool gpuCanvasAvailable)
    {
        IsGpuCanvasAvailable = gpuCanvasAvailable;

        if (!gpuCanvasAvailable)
        {
            //Without a context the presenter cannot take the graphics path, whatever the drop-down says.
            controller?.SetGraphicsContext(null);
        }

        UpdateUiState();

        if (smoke is { IsSmokeRun: true } && !hasStartedSmokeRun)
        {
            hasStartedSmokeRun = true;
            _ = RunSmokeRunAsync();
        }
    }

    /// <summary>Whether the host's GPU-Skia canvas started, and is the canvas being painted.</summary>
    public bool IsGpuCanvasAvailable { get; private set; }

    /// <summary>Releases the player. Called by the page when the window closes.</summary>
    public void Shutdown() => controller?.Dispose();

    #endregion

    #region | Commands and their implementations |

    /// <summary>Opens the selected file, if it is not open already, and plays it.</summary>
    public SimpleCommand PlayCommand => field ??= new SimpleCommand(() => SelectedVideo != null, _ => DoPlay());

    /// <summary>Pauses playback, leaving the last frame on screen.</summary>
    public SimpleCommand PauseCommand => field ??= new SimpleCommand(() => SelectedVideo != null, _ => DoPause());

    /// <summary>Stops playback and returns to the beginning.</summary>
    public SimpleCommand StopCommand => field ??= new SimpleCommand(() => SelectedVideo != null, _ => DoStop());

    private void DoPlay()
    {
        if (SelectedVideo == null || controller == null) { return; }

        if (!string.Equals(controller.CurrentFilePath, SelectedVideo.FullPath, StringComparison.Ordinal))
        {
            playbackEndedCount = 0;

            if (!controller.Open(SelectedVideo.FullPath)) { return; }
        }

        //Pressing Play is what applies the panel. An unchanged panel re-applies the same chain, which the
        //  player recognises and skips, so this costs nothing when nothing was edited.
        ApplyPanelChainToPlayer();

        controller.Play();
        UpdateUiState();
    }

    private void DoPause()
    {
        controller?.Pause();
        UpdateUiState();
    }

    private void DoStop()
    {
        controller?.Stop();
        SyncPosition(TimeSpan.Zero);
        UpdateUiState();
    }

    /// <summary>Writes the chain THE PANEL HOLDS out as one ".cube" file.</summary>
    public SimpleCommand BakeCommand => field ??= new SimpleCommand(() => CanBakeChain, _ => DoBakeAsync());

    /// <remarks>
    /// Counts what is TICKED, not what is applied. Pressing Play and pressing Bake are two independent
    /// triggers on the same panel: Play composes the chain and hands it to the picture, Bake composes the
    /// chain and hands it to a file. Neither reads the other's result.
    /// </remarks>
    private bool CanBakeChain =>
        controller != null
        && LutPanelPolicy.CanBake(
            Luts.Count(lut => lut.IsChecked), controller.ActiveRenderPath, controller.TransportState);

    private async Task DoBakeAsync()
    {
        if (controller == null) { return; }

        if (PickSaveCubePathAsync == null)
        {
            //A head that cannot ask has nowhere to put the file, and this application never picks a
            //  location on someone's behalf.
            ShowMessage("This head has no save dialog, so there is nowhere to bake to.");
            return;
        }

        var cubeFilePath = await PickSaveCubePathAsync(BakeLocations.CreateFileName(DateTime.Now));

        //Cancelled. Nothing is written and nothing is said: deciding not to save is not a failure.
        if (string.IsNullOrWhiteSpace(cubeFilePath)) { return; }

        //The panel's own state, composed fresh - never the chain the presenter happens to be showing.
        BakedLut baked = controller.BakeChain(BuildPanelChain(), cubeFilePath);

        if (baked == null) { return; }

        BakeStatusText = $"Baked {baked.TableCount} table(s) into a {baked.Size}-node table: {baked.FilePath}";
        UpdateUiState();
    }

    #endregion

    #region | The corpus and the lookup tables |

    private void LoadCorpus()
    {
        var repositoryRoot = RepositoryAssets.FindRepositoryRoot();

        if (repositoryRoot == null)
        {
            CorpusText = "The sample corpus was not found. This sample plays the files under "
                + $"{RepositoryAssets.AuthoringRelativePath} in the CodeBrix.VideoPlayback repository, so it "
                + "has to run from inside a clone of it.";
            return;
        }

        foreach (VideoCorpusItem item in VideoCorpus.Scan(RepositoryAssets.GetAuthoringFolder(repositoryRoot)))
        {
            Videos.Add(new VideoListItem(item));
        }

        foreach (LutCatalogEntry entry in LutCatalog.Scan(RepositoryAssets.GetLutsFolder(repositoryRoot)))
        {
            //The catalogue is kept beside the rows, index for index, so a name can find its row.
            lutCatalogue.Add(entry);
            Luts.Add(new LutListItem(entry, OnLutPanelEdited));
        }

        CorpusText = $"{Videos.Count} videos and {Luts.Count} lookup tables from {repositoryRoot}";

        SelectedVideo = Videos.FirstOrDefault();
    }

    private void OnSelectedVideoChanged()
    {
        if (controller == null || SelectedVideo == null) { return; }

        //A new choice starts from a clean transport: the file opens when Play is pressed.
        controller.Stop();
        controller.Close();
        playbackEndedCount = 0;
        SyncPosition(TimeSpan.Zero);
        DurationSeconds = 1;
    }

    /// <summary>
    /// A tick box or a percentage changed. NOTHING is applied here - the chain reaches the picture when
    /// Play is pressed, and until then the panel only says what is waiting.
    /// </summary>
    private void OnLutPanelEdited() => UpdateUiState();

    /// <summary>Hands the panel's current state to the player. Called when Play is pressed, and only then.</summary>
    private bool ApplyPanelChainToPlayer()
    {
        if (controller == null) { return false; }

        var changed = controller.ApplyLutChain(BuildPanelChain());

        UpdateUiState();

        if (changed) { InvalidateVideoCanvas?.Invoke(); }

        return changed;
    }

    private List<LutChainEntry> BuildPanelChain()
    {
        List<LutChainEntry> entries = [];

        foreach (LutListItem lut in Luts.Where(lut => lut.IsChecked))
        {
            entries.Add(new LutChainEntry(lut.FilePath, lut.Percent));
        }

        return entries;
    }

    #endregion

    #region | Player events and status |

    private void OnMediaOpened()
    {
        DurationSeconds = Math.Max(controller.Duration.TotalSeconds, 0.001);
        UpdateUiState();
    }

    private void OnPlaybackEnded()
    {
        playbackEndedCount++;

        //Reaching the end is a transport change like any other, and the panel has to be told. Nobody
        //  pressed Pause or Stop, but the picture is no longer running - the player already reads
        //  Stopped here - so the lookup-table panel becomes editable again, which is the whole point:
        //  the next thing that can happen is Play, and Play is what applies the chain.
        UpdateUiState();

        playbackEnded?.TrySetResult(true);
    }

    private void OnPositionChanged(VideoPositionEventArgs args)
    {
        if (args.Duration > TimeSpan.Zero) { DurationSeconds = args.Duration.TotalSeconds; }

        SyncPosition(args.Position);
    }

    private void SyncPosition(TimeSpan position)
    {
        //Moving the scrub bar from the clock must not be read as a person dragging it.
        isSyncingPosition = true;
        PositionSeconds = position.TotalSeconds;
        isSyncingPosition = false;
    }

    private void UpdateUiState()
    {
        if (controller == null) { return; }

        VideoRenderBackendOption backend = controller.ActiveRenderPath;
        VideoTransportState transport = controller.TransportState;
        var onGpu = backend == VideoRenderBackendOption.Gpu;
        var canvas = IsGpuCanvasAvailable ? "GPU-Skia canvas" : "CPU canvas";

        RenderPathText = $"Render path: {(onGpu ? "GPU" : "CPU")} · effects "
            + $"{(controller.EffectsActive ? "applied" : "off")} · {canvas}";

        IsLutPanelEnabled = LutPanelPolicy.IsEditable(backend, transport);
        LutPanelNote = LutPanelPolicy.GetNote(backend, transport);
        LutSummaryText = BuildLutSummary();

        BakeCommand.RaiseCanExecuteChanged();
    }

    private string BuildLutSummary()
    {
        var selected = Luts.Count(lut => lut.IsChecked);
        var applied = controller.LutEntries.Count;
        var pending = !string.Equals(
            LutChain.ComputeSignature(BuildPanelChain()),
            LutChain.ComputeSignature(controller.LutEntries),
            StringComparison.Ordinal);

        if (pending)
        {
            return selected == 0
                ? $"Nothing ticked · press Play to drop the {applied} applied table(s)"
                : $"{selected} ticked · press Play to apply (in list order)";
        }

        return applied == 0
            ? "No lookup tables applied"
            : $"{applied} lookup table{(applied == 1 ? string.Empty : "s")} applied, in list order";
    }

    private void ShowMessage(string message)
    {
        MessageText = message ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(message)) { Debug.WriteLine($"SimpleCbxVideoPlayer: {message}"); }
    }

    #endregion

    #region | The smoke run |

    private void CompletePendingSnapshot()
    {
        if (pendingSnapshotPath == null || controller == null) { return; }

        var path = pendingSnapshotPath;
        pendingSnapshotPath = null;

        ComposedFrameSnapshot snapshot = null;

        try
        {
            snapshot = controller.SaveComposedFrame(path);
        }
        catch (Exception exception)
        {
            //A capture that fails must not take the paint handler with it; the smoke run reports it.
            SmokeLog($"FAIL capture {exception.GetType().Name}: {exception.Message}");
        }

        pendingSnapshot?.TrySetResult(snapshot);
    }

    private async Task RunSmokeRunAsync()
    {
        try
        {
            SmokeLog($"start video='{smoke.VideoName}' render-path={smoke.RenderPath} audio={smoke.PlayAudio} "
                + $"luts=[{string.Join(", ", smoke.Luts)}]");
            SmokeLog($"runtime {SkiaVideoRuntime.Summary}");
            SmokeLog($"corpus {CorpusText}");
            SmokeLog($"canvas {(IsGpuCanvasAvailable ? "SKGLElement (GPU Skia)" : "SKElement (CPU)")}");

            if (!string.IsNullOrWhiteSpace(smoke.ParseError))
            {
                SmokeLog($"FAIL command line {smoke.ParseError}");
                FinishSmokeRun(2);
                return;
            }

            if (!SkiaVideoRuntime.IsInitialized)
            {
                SmokeLog($"FAIL registration {SkiaVideoRuntime.ErrorMessage}");
                FinishSmokeRun(2);
                return;
            }

            VideoListItem item = FindSmokeVideo(smoke.VideoName);

            if (item == null)
            {
                SmokeLog($"FAIL no video in the corpus matches '{smoke.VideoName}'");
                FinishSmokeRun(2);
                return;
            }

            SelectedVideo = item;
            SelectRenderPath(smoke.RenderPath);

            if (!TickSmokeLuts())
            {
                FinishSmokeRun(2);
                return;
            }

            SmokeLog($"file {item.FullPath}");

            if (!controller.Open(item.FullPath))
            {
                SmokeLog($"FAIL open {controller.LastError}");
                FinishSmokeRun(2);
                return;
            }

            //Exactly what pressing Play does: the panel's state becomes the chain, and only then does the
            //  picture start. Nothing in this application applies a table behind a running video.
            ApplyPanelChainToPlayer();

            if (controller.LutEntries.Count != smoke.Luts.Count)
            {
                SmokeLog($"FAIL {smoke.Luts.Count} lookup table(s) were asked for but the applied chain holds "
                    + $"{controller.LutEntries.Count}");
                FinishSmokeRun(2);
                return;
            }

            playbackEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.Play();

            if (smoke.PlayUntilEnded)
            {
                var budget = controller.Duration + TimeSpan.FromSeconds(20);
                await Task.WhenAny(playbackEnded.Task, Task.Delay(budget));
            }
            else
            {
                await Task.Delay(smoke.PlayDuration);
            }

            SmokeLog($"played position={controller.Position:mm\\:ss\\.fff} duration={controller.Duration:mm\\:ss\\.fff} "
                + $"playback-ended-events={playbackEndedCount}");
            SmokeLog($"active-render-path={controller.ActiveRenderPath} effects-active={controller.EffectsActive} "
                + $"display={controller.DisplayWidth}x{controller.DisplayHeight} "
                + $"frame={controller.CurrentFrameNumber}");

            if (!string.IsNullOrWhiteSpace(controller.LastError)) { SmokeLog($"message {controller.LastError}"); }

            if (!string.IsNullOrWhiteSpace(smoke.BakePath) && !BakeSmokeChain()) { FinishSmokeRun(2); return; }

            if (!string.IsNullOrWhiteSpace(smoke.SnapshotPath))
            {
                ComposedFrameSnapshot snapshot = await CaptureSmokeSnapshotAsync();

                if (snapshot == null)
                {
                    SmokeLog("FAIL nothing was composed, so there is no frame to capture");
                    FinishSmokeRun(2);
                    return;
                }

                SmokeLog($"snapshot {snapshot}");
                SmokeLog($"orientation {(snapshot.IsPortrait ? "portrait" : "landscape")}");

                if (!string.IsNullOrWhiteSpace(smoke.ComparePath) && !CompareSmokeSnapshot()) { FinishSmokeRun(2); return; }
            }
            else if (!string.IsNullOrWhiteSpace(smoke.ComparePath))
            {
                SmokeLog("FAIL --compare needs a --snapshot to compare");
                FinishSmokeRun(2);
                return;
            }

            FinishSmokeRun(0);
        }
        catch (Exception exception)
        {
            SmokeLog($"FAIL {exception.GetType().Name}: {exception.Message}");
            FinishSmokeRun(2);
        }
    }

    private async Task<ComposedFrameSnapshot> CaptureSmokeSnapshotAsync()
    {
        //Pausing and seeking to a fixed position makes the captured picture the SAME picture on every run,
        //  which is what lets one run's snapshot be compared with another's.
        controller.Pause();
        controller.Seek(smoke.SnapshotPosition);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        pendingSnapshot = new TaskCompletionSource<ComposedFrameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingSnapshotPath = smoke.SnapshotPath;
        InvalidateVideoCanvas?.Invoke();

        Task completed = await Task.WhenAny(pendingSnapshot.Task, Task.Delay(TimeSpan.FromSeconds(8)));

        return ReferenceEquals(completed, pendingSnapshot.Task) ? pendingSnapshot.Task.Result : null;
    }

    private bool TickSmokeLuts()
    {
        foreach (SmokeLutRequest request in smoke.Luts)
        {
            var index = FindOrAddSmokeLut(request.Name);

            if (index < 0)
            {
                //A smoke run exists to verify something. A --lut that matched nothing would leave the
                //  picture ungraded while the run still passed, so it is a failure and it names the choices.
                SmokeLog($"FAIL no lookup table matches '{request.Name}'. The catalogue holds: "
                    + $"{string.Join(", ", lutCatalogue.Select(entry => entry.FileName))}");
                return false;
            }

            LutListItem match = Luts[index];
            match.PercentText = request.ApplyAtPercent.ToString("0.#", CultureInfo.CurrentCulture);
            match.IsChecked = true;

            if (!match.IsChecked || Math.Abs(match.Percent - request.ApplyAtPercent) > 0.0001)
            {
                SmokeLog($"FAIL {match.FileName} would not take {request.ApplyAtPercent:0.#}% "
                    + $"(the row reads ticked={match.IsChecked} at {match.Percent:0.#}%)");
                return false;
            }

            SmokeLog($"lut {match.GroupName}/{match.FileName} at {match.Percent:0.#}%");
        }

        return true;
    }

    /// <summary>
    /// Finds the row a --lut asks for, adding one when the value names a ".cube" file outside the corpus.
    /// </summary>
    /// <remarks>
    /// The second half is what makes a baked chain verifiable: the file a run wrote can be handed straight
    /// back to another run as a table to apply, without having to live in the corpus first.
    /// </remarks>
    private int FindOrAddSmokeLut(string name)
    {
        var index = LutCatalog.MatchIndex(lutCatalogue, name);

        if (index >= 0 && index < Luts.Count) { return index; }

        LutCatalogEntry external = LutCatalog.CreateExternalEntry(name);

        if (external == null) { return -1; }

        lutCatalogue.Add(external);
        Luts.Add(new LutListItem(external, OnLutPanelEdited));
        SmokeLog($"lut-external {external.FullPath}");

        return Luts.Count - 1;
    }

    private bool BakeSmokeChain()
    {
        //Exactly what the Bake button does: the PANEL's chain, composed on its own, whatever the picture
        //  is doing and whether or not the chain was ever played.
        BakedLut baked = controller.BakeChain(BuildPanelChain(), smoke.BakePath);

        if (baked == null)
        {
            SmokeLog($"FAIL bake {controller.LastError}");
            return false;
        }

        SmokeLog($"bake {baked.FilePath} title='{baked.Title}' size={baked.Size} tables={baked.TableCount}");
        return true;
    }

    private bool CompareSmokeSnapshot()
    {
        ImageComparisonResult comparison = ImageComparison.Compare(smoke.SnapshotPath, smoke.ComparePath);

        if (comparison == null)
        {
            SmokeLog($"FAIL compare could not read '{smoke.SnapshotPath}' or '{smoke.ComparePath}'");
            return false;
        }

        SmokeLog($"compare against={smoke.ComparePath} {comparison} tolerance={smoke.CompareTolerance}");

        if (!comparison.SizesMatch || comparison.MaxChannelDelta > smoke.CompareTolerance)
        {
            SmokeLog($"FAIL compare max-channel-delta={comparison.MaxChannelDelta} is beyond the tolerance of "
                + $"{smoke.CompareTolerance}");
            return false;
        }

        return true;
    }

    private void SelectRenderPath(VideoRenderPathOption option) =>
        SelectedRenderPath = RenderPathChoices.FirstOrDefault(choice => choice.Option == option)
            ?? RenderPathChoices.FirstOrDefault();

    private VideoListItem FindSmokeVideo(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }

        return Videos.FirstOrDefault(video =>
                   string.Equals(video.DisplayName, name, StringComparison.OrdinalIgnoreCase))
               ?? Videos.FirstOrDefault(video =>
                   string.Equals(video.FullPath, name, StringComparison.Ordinal))
               ?? Videos.FirstOrDefault(video =>
                   string.Equals(video.FileName, name, StringComparison.OrdinalIgnoreCase))
               ?? Videos.FirstOrDefault(video =>
                   video.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private void FinishSmokeRun(int exitCode)
    {
        SmokeLog($"exit {exitCode}");

        try
        {
            controller?.Stop();
            controller?.Dispose();
        }
        catch (Exception exception)
        {
            //Leaving is the point; a complaint on the way out is worth a line and nothing more.
            SmokeLog($"WARN shutdown {exception.GetType().Name}: {exception.Message}");
        }

        Console.Out.Flush();
        Environment.Exit(exitCode);
    }

    private static void SmokeLog(string message)
    {
        Console.WriteLine($"SMOKE {message}");
        Console.Out.Flush();
    }

    #endregion
}
