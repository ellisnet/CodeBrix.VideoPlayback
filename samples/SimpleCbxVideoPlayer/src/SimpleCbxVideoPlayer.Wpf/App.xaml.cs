using CodeBrix.Platform.Simple;
using SimpleCbxVideoPlayer.Helpers;
using System.Windows;

namespace SimpleCbxVideoPlayer;

public partial class App : Application
{
    public App()
    {
        //A windowed application has no console of its own, so a smoke run borrows the one it was
        //  started from. Without this the SMOKE lines would go nowhere and the run could not be read.
        ConsoleHelper.AttachForSmokeRun();

        SimpleServiceResolver.CreateInstance(HostHelper.GetHost(), services =>
        {
            //No custom services needed - the player lives in the view model
        });
        SimpleViewModel.SetIsDesignMode(false);
    }
}
