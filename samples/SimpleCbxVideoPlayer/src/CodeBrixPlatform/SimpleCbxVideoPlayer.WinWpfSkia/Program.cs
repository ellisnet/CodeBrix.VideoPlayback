using CodeBrix.Platform.UI.Hosting;
using CodeBrix.Platform.UI.Runtime.Skia.Wpf;
using System;

namespace SimpleCbxVideoPlayer;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.InitializeLogging();

        var host = CodeBrixPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWindowsWpf()
            .UseDirectSkiaCanvasMode() //Experimental - should be safe to leave enabled
            .Build();

        if (host is WpfHost wpfHost)
        {
            wpfHost.RenderSurfaceType = RenderSurfaceType.Software;
        }

        host.Run();
    }
}
