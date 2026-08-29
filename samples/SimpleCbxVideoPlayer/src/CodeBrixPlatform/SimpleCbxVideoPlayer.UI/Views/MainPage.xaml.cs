using CodeBrix.Platform.Simple;
using CodeBrix.Platform.WinUI.Graphics3DGL;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleCbxVideoPlayer.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using System.Threading.Tasks;

namespace SimpleCbxVideoPlayer.Views;

public sealed partial class MainPage : Page
{
    private SkiaGLCanvasElement gpuCanvas;
    private bool hasSettledVideoSurface;

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
            }
        };

        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel?.Shutdown();

        this.InitializeComponent(); //Leave this line last
    }

    private MainViewModel ViewModel => DataContext as MainViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (gpuCanvas != null) { return; }

        //The GPU canvas is built here rather than in XAML because its only constructor takes an argument.
        gpuCanvas = new SkiaGLCanvasElement();
        gpuCanvas.PaintSurface += OnGpuPaintSurface;
        VideoHost.Children.Insert(0, gpuCanvas);

        CpuCanvas.SizeChanged += (_, _) => CpuCanvas.Invalidate();

        //IsGpuInitialized reads null until the element has loaded and tried to start OpenGL.
        await Task.Delay(600);

        DispatcherQueue?.TryEnqueue(SettleVideoSurface);
    }

    private void SettleVideoSurface()
    {
        var hasGpuCanvas = gpuCanvas?.IsGpuInitialized == true;

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

    private void OnGpuPaintSurface(object sender, SkiaGLPaintSurfaceEventArgs args)
    {
        //The context is current for the length of this call, which is where the presenter wants it.
        ViewModel?.SetGraphicsContext(args.Context);
        args.Surface.Canvas.Clear(SKColors.Black);
        ViewModel?.DrawVideo(args.Surface.Canvas, new SKRect(0f, 0f, args.Info.Width, args.Info.Height));
    }

    private void OnCpuPaintSurface(object sender, SKPaintSurfaceEventArgs args)
    {
        args.Surface.Canvas.Clear(SKColors.Black);
        ViewModel?.DrawVideo(args.Surface.Canvas, new SKRect(0f, 0f, args.Info.Width, args.Info.Height));
    }
}
