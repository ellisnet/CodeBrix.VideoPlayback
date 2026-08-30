using CodeBrix.Platform.Simple;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using SimpleCbxVideoPlayer.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SimpleCbxVideoPlayer.WinUI.Views;

public sealed partial class MainPage : Page
{
    private SKSwapChainPanel gpuCanvas;
    private bool hasSettledVideoSurface;

    //I tend to like to declare/define private members above the constructor, in C# classes
    private MainViewModel ViewModel => DataContext as MainViewModel;

    public MainPage()
    {
        DataContextChanged += (_, _) =>
        {
            //Give the view model's SimpleDialog helpers a XamlRoot to attach dialogs to
            (DataContext as IXamlRootGetter)?.SetXamlRootGetter(() => XamlRoot);

            if (ViewModel != null)
            {
                //Raised on the decoding thread: hop to the user-interface thread and mark the canvas dirty
                ViewModel.InvalidateVideoCanvas = () => DispatcherQueue?.TryEnqueue(InvalidateVideoCanvas);
                ViewModel.PickSaveCubePathAsync = PickSaveCubePathAsync;
            }
        };

        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel?.Shutdown();

        this.InitializeComponent(); //Leave this line last
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (hasSettledVideoSurface) { return; }

        //The GPU canvas is built here rather than in XAML because SKSwapChainPanel starts ANGLE on its
        //  own: a machine with no usable context throws, and a throw inside InitializeComponent would
        //  take the whole page with it. Built here, a failure just settles on the CPU canvas.
        try
        {
            gpuCanvas = new SKSwapChainPanel();
            gpuCanvas.PaintSurface += OnGpuPaintSurface;
            VideoHost.Children.Insert(0, gpuCanvas);
        }
        catch (Exception exception)
        {
            gpuCanvas = null;
            Debug.WriteLine($"SimpleCbxVideoPlayer: the swap chain did not start - {exception.Message}");
        }

        CpuCanvas.SizeChanged += (_, _) => CpuCanvas.Invalidate();

        //GRContext reads null until the panel has loaded and drawn its first frame.
        gpuCanvas?.Invalidate();

        await Task.Delay(600);

        DispatcherQueue?.TryEnqueue(SettleVideoSurface);
    }

    private void SettleVideoSurface()
    {
        var hasGpuCanvas = gpuCanvas?.GRContext != null;

        if (gpuCanvas != null)
        {
            gpuCanvas.Visibility = hasGpuCanvas ? Visibility.Visible : Visibility.Collapsed;
        }

        CpuCanvas.Visibility = hasGpuCanvas ? Visibility.Collapsed : Visibility.Visible;

        if (!hasSettledVideoSurface)
        {
            hasSettledVideoSurface = true;
            ViewModel?.OnVideoSurfaceReady(hasGpuCanvas);
        }

        InvalidateVideoCanvas();
    }

    private void InvalidateVideoCanvas()
    {
        if (gpuCanvas is { Visibility: Visibility.Visible })
        {
            gpuCanvas.Invalidate();
            return;
        }

        CpuCanvas?.Invalidate();
    }

    /// <summary>Asks where to write a baked lookup table, and returns null when the person cancels.</summary>
    /// <remarks>
    /// A WinUI 3 picker has to be told which window it belongs to before it will show at all - there is no
    /// ambient main window to infer, and an unpackaged process has no identity to fall back on. No
    /// SuggestedStartLocation is set: the dialog opens where this person last was, and the application
    /// never proposes a folder of its own.
    /// </remarks>
    private Task<string> PickSaveCubePathAsync(string suggestedFileName)
    {
        //A SimpleCommand does not promise to run its handler on the user-interface thread, and a picker
        //  belongs to the window it is shown over - so the thread is made certain rather than assumed.
        TaskCompletionSource<string> chosen =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = DispatcherQueue?.TryEnqueue(async () =>
        {
            try { chosen.TrySetResult(await ShowSavePickerAsync(suggestedFileName)); }
            catch (Exception exception) { chosen.TrySetException(exception); }
        });

        //No dispatcher means no window to show it over, which reads the same as declining to choose.
        if (enqueued != true) { chosen.TrySetResult(null); }

        return chosen.Task;
    }

    private async Task<string> ShowSavePickerAsync(string suggestedFileName)
    {
        FileSavePicker picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = BakeLocations.LutFileExtension,
        };
        picker.FileTypeChoices.Add("Cube lookup table", [BakeLocations.LutFileExtension]);

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow));

        StorageFile file = await picker.PickSaveFileAsync();

        if (file == null) { return null; }

        //The picker creates an empty placeholder at a brand-new name. The bake is about to write over it
        //  anyway; clearing it means a bake that fails leaves nothing behind that looks like a result.
        RemoveEmptyPlaceholder(file.Path);

        return file.Path;
    }

    private static void RemoveEmptyPlaceholder(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);

            if (info.Exists && info.Length == 0) { info.Delete(); }
        }
        catch (Exception exception)
        {
            //Leaving it is harmless - the bake overwrites it either way.
            Debug.WriteLine($"SimpleCbxVideoPlayer: could not clear the placeholder - {exception.Message}");
        }
    }

    private void OnGpuPaintSurface(object sender, SKPaintGLSurfaceEventArgs args)
    {
        //The context is current for the length of this call, which is where the presenter wants it.
        ViewModel?.SetGraphicsContext(gpuCanvas?.GRContext);
        args.Surface.Canvas.Clear(SKColors.Black);
        ViewModel?.DrawVideo(args.Surface.Canvas, new SKRect(0f, 0f, args.Info.Width, args.Info.Height));
    }

    private void OnCpuPaintSurface(object sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Black);
        ViewModel?.DrawVideo(args.Surface.Canvas, new SKRect(0f, 0f, args.Info.Width, args.Info.Height));
    }
}
