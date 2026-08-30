using SimpleCbxVideoPlayer.SkiaVideo.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleCbxVideoPlayer.Helpers;

/// <summary>
/// Gives a smoke run somewhere to print to.
/// </summary>
/// <remarks>
/// The CodeBrix.Platform heads are console-subsystem executables, so their SMOKE lines land in the
/// terminal that started them without anyone arranging it. This head is a WINDOWS-subsystem executable -
/// it has no console at all - so a smoke run has to go and find one: the terminal it was launched from
/// when there is one, and a console of its own when there is not. A smoke run that printed nowhere would
/// be a verification nobody could read, which is the same as no verification.
/// </remarks>
internal static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// Attaches a console when - and only when - the command line asked for a smoke run.
    /// </summary>
    /// <remarks>A normal launch is left alone: nobody wants a console behind their video player.</remarks>
    public static void AttachForSmokeRun()
    {
        var isSmokeRun = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, SmokeOptions.SmokeSwitch, StringComparison.OrdinalIgnoreCase));

        if (!isSmokeRun) { return; }

        Attach();
    }

    private static void Attach()
    {
        try
        {
            //Somewhere to write already: a console inherited from the shell that started us, or - just as
            //  important - a PIPE, which is what "dotnet run" and any script that captures the output hand
            //  down. Attaching a console in that case would rebind Console.Out AWAY from the pipe and the
            //  caller would capture nothing at all, so the only right move here is to leave it alone.
            if (!ReferenceEquals(Console.OpenStandardOutput(), Stream.Null)) { return; }

            if (GetConsoleWindow() != IntPtr.Zero) { return; }

            //The terminal that started us, if it was started from one; otherwise a console of its own.
            if (!AttachConsole(AttachParentProcess) && !AllocConsole()) { return; }

            //Console.Out was bound to the null device when the process started without a console, so it
            //  has to be rebound to the handle that now exists.
            var standardOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetOut(standardOutput);

            var standardError = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetError(standardError);
        }
        catch (Exception exception)
        {
            //Not being able to print is not a reason to refuse to run; the smoke run itself will fail
            //  loudly enough by way of its exit code.
            System.Diagnostics.Debug.WriteLine($"SimpleCbxVideoPlayer: no console for the smoke run - {exception.Message}");
        }
    }
}
