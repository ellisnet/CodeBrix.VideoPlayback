using CodeBrix.Platform.Simple;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SimpleCbxVideoPlayer.Helpers;
using System;

namespace SimpleCbxVideoPlayer.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
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

        InitializeComponent();
    }

    protected Window MainWindow { get; private set; }

    /// <summary>The app's main window, exposed so a page can reach its HWND when it needs one.</summary>
    public static Window CurrentWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window
        {
            Title = "SimpleCbxVideoPlayer"
        };
        CurrentWindow = MainWindow;

        if (MainWindow.Content is not Frame rootFrame)
        {
            rootFrame = new Frame();
            MainWindow.Content = rootFrame;
            rootFrame.NavigationFailed += OnNavigationFailed;
        }

        if (rootFrame.Content == null)
        {
            rootFrame.Navigate(typeof(Views.MainPage), args.Arguments);
        }

        MainWindow.Activate();
    }

    /// <summary>Invoked when navigation to a certain page fails.</summary>
    /// <param name="sender">The Frame which failed navigation.</param>
    /// <param name="e">Details about the navigation failure.</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }
}
