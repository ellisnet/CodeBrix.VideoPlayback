using System;
using CodeBrix.VideoPlayback;

namespace CodeBrix.VideoPlayback.Tools;

/// <summary>
/// The entry point of the headless tools: <c>cbvinfo</c>, <c>cbvdecode</c>, <c>cbvmux</c> and
/// <c>lutbake</c>.
/// </summary>
/// <remarks>
/// Both verbs live in one executable so there is one build, one set of dependencies and one place to look.
/// Run them with the verb first:
/// <code>
/// dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvinfo clip.cbv
/// dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- cbvdecode --headless clip.cbv
/// dotnet run --project tools/CodeBrix.VideoPlayback.Tools -- lutbake --lut grade.cube -o out.cube
/// </code>
/// </remarks>
public static class Program
{
    /// <summary>Runs the requested verb.</summary>
    /// <param name="args">The verb followed by its own arguments.</param>
    /// <returns>0 when the verb succeeded, 1 when it failed, 2 when the command line was wrong.</returns>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            WriteUsage();
            return 2;
        }

        string verb = args[0];
        string[] rest = new string[args.Length - 1];
        Array.Copy(args, 1, rest, 0, rest.Length);

        try
        {
            switch (verb.ToLowerInvariant())
            {
                case "cbvinfo":
                case "info":
                    return CbvInfoCommand.Run(rest);

                case "cbvdecode":
                case "decode":
                    return CbvDecodeCommand.Run(rest);

                case "cbvmux":
                case "mux":
                    return CbvMuxCommand.Run(rest);

                case "lutbake":
                    return LutBakeCommand.Run(rest);

                case "-h":
                case "--help":
                case "help":
                    WriteUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"'{verb}' is not a verb this tool knows.");
                    WriteUsage();
                    return 2;
            }
        }
        catch (VideoPlaybackException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine("CodeBrix.VideoPlayback headless tools");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  cbvinfo <file> [--cues] [--packets] [--verify-checksums]");
        Console.Error.WriteLine("      Prints the tracks, index, captions and chapters of a .cbv, .webm or .mkv");
        Console.Error.WriteLine("      file, and checks it against the streamable WebM profile rules.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  cbvdecode --headless <file> [--y4m <out.y4m>] [--frames <n>] [--quiet]");
        Console.Error.WriteLine("      Decodes every video frame, printing a hash of each and timing statistics.");
        Console.Error.WriteLine("      Uncompressed video needs no codec package; a coded stream needs one");
        Console.Error.WriteLine("      registered.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  cbvmux --output <out.cbv> [--video <in.ivf>] [--audio <in.ogg>]");
        Console.Error.WriteLine("         [--chapters <chapters.ffmeta>] [--audio-language <bcp47>]");
        Console.Error.WriteLine("         [--audio-name <name>] [--video-name <name>]");
        Console.Error.WriteLine("         [--captions <path>:<bcp47>[:<name>[:default+forced+sdh]]] ...");
        Console.Error.WriteLine("      Builds a bespoke .cbv file from an encoder's IVF and Ogg output.");
        Console.Error.WriteLine();
        LutBakeCommand.WriteUsage();
    }
}
