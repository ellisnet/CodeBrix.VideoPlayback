using SimpleCbxVideoPlayer.SkiaVideo.Playback;
using SimpleCbxVideoPlayer.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace SimpleCbxVideoPlayer.Views;

public partial class MainWindow : Window
{
    private SKGLElement gpuCanvas;
    private bool hasSettledVideoSurface;

    //I tend to like to declare/define private members above the constructor, in C# classes
    private MainViewModel ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        DataContextChanged += (_, _) =>
        {
            if (ViewModel != null)
            {
                //Raised on the decoding thread: hop to the user-interface thread and repaint the canvas
                ViewModel.InvalidateVideoCanvas = () => Dispatcher?.BeginInvoke(InvalidateVideoCanvas);
                ViewModel.PickSaveCubePathAsync = PickSaveCubePathAsync;
            }
        };

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            ViewModel?.Shutdown();
            gpuCanvas?.Dispose();
        };

        InitializeComponent(); //Leave this line last
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (hasSettledVideoSurface) { return; }

        //The GPU canvas is built here rather than in XAML because SKGLElement starts OpenGL in its own
        //  constructor: a machine with no usable context throws, and a throw inside InitializeComponent
        //  would take the whole window with it. Built here, a failure just settles on the CPU canvas.
        try
        {
            gpuCanvas = new SKGLElement();
            gpuCanvas.PaintSurface += OnGpuPaintSurface;
            VideoHost.Children.Insert(0, gpuCanvas);
        }
        catch (Exception exception)
        {
            gpuCanvas = null;
            Debug.WriteLine($"SimpleCbxVideoPlayer: OpenGL did not start - {exception.Message}");
        }

        CpuCanvas.SizeChanged += (_, _) => CpuCanvas.InvalidateVisual();

        //GRContext reads null until the element has loaded and rendered its first frame.
        gpuCanvas?.InvalidateVisual();

        _ = SettleVideoSurfaceAsync();
    }

    private async Task SettleVideoSurfaceAsync()
    {
        await Task.Delay(600);

        SettleVideoSurface();
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
            gpuCanvas.InvalidateVisual();
            return;
        }

        CpuCanvas?.InvalidateVisual();
    }

    /// <summary>Asks where to write a baked lookup table, and returns null when the person cancels.</summary>
    /// <remarks>
    /// <para>
    /// Marshalled onto the user-interface thread ON PURPOSE. A SimpleCommand does not promise to run its
    /// handler there, and both the dialog and the window it is owned by belong to that thread - so the
    /// thread the button was pressed on is made irrelevant rather than assumed.
    /// </para>
    /// <para>
    /// No InitialDirectory: the dialog opens wherever this person last saved something, and the
    /// application never proposes a folder of its own. The standard overwrite prompt is left ON, because
    /// this application has no confirmation of its own that it would double up with.
    /// </para>
    /// </remarks>
    private Task<string> PickSaveCubePathAsync(string suggestedFileName) =>
        Dispatcher.InvokeAsync(() =>
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the baked lookup table as",
                Filter = "Cube lookup table (*.cube)|*.cube|All files (*.*)|*.*",
                DefaultExt = BakeLocations.LutFileExtension,
                AddExtension = true,
                FileName = suggestedFileName,
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }).Task;

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
