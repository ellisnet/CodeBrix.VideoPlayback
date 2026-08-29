using CodeBrix.Platform.UI.Hosting;
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
            .UseLinuxWayland()
            .UseDirectSkiaCanvasMode() //Experimental - should be safe to leave enabled
            .Build();

        host.Run();
    }
}
