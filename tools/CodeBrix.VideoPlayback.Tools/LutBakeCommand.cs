using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CodeBrix.VideoPlayback.Color.Luts;

namespace CodeBrix.VideoPlayback.Tools;

/// <summary>
/// The <c>lutbake</c> verb: folds one or more ".cube" lookup tables, each with its own apply-at percentage,
/// into ONE effective table and writes it to a ".cube" file.
/// </summary>
/// <remarks>
/// <para>
/// This is the authoring hook. The authoring pipeline calls it - or calls <see cref="LutComposer" /> and
/// <see cref="CubeLutFile" /> directly, which is all this verb does - and hands the PATH of the file it
/// writes to FFmpeg's <c>lut3d</c> filter. The playback side composes the very same chain through the very
/// same code, so the graded picture an application shows and the graded video the pipeline encodes agree.
/// </para>
/// <para>Nothing but the core package is used here: no drawing, no codec, no FFmpeg.</para>
/// </remarks>
public static class LutBakeCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="args">The switches describing the chain and the output.</param>
    /// <returns>0 when the file was written, 1 when it could not be, 2 when the command line was wrong.</returns>
    public static int Run(string[] args)
    {
        List<LutLayer> layers = new List<LutLayer>();
        LutComposerOptions options = new LutComposerOptions();
        string output = null;
        string title = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--lut":
                    if (!TryTake(args, ref i, out string lut)) return Missing(arg);
                    if (!TryAddLayer(layers, lut)) return 2;
                    break;

                case "--size":
                    if (!TryTake(args, ref i, out string size)) return Missing(arg);
                    if (!int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodes)
                        || nodes < Lut3D.MinimumSize
                        || nodes > Lut3D.MaximumSize)
                    {
                        Console.Error.WriteLine(
                            $"lutbake: --size takes a whole number between {Lut3D.MinimumSize} and "
                            + $"{Lut3D.MaximumSize}; '{size}' is not one.");

                        return 2;
                    }

                    options.OutputSize = nodes;
                    break;

                case "--interp":
                    if (!TryTake(args, ref i, out string interpolation)) return Missing(arg);
                    switch (interpolation.ToLowerInvariant())
                    {
                        case "tetrahedral":
                            options.Interpolation = LutInterpolation.Tetrahedral;
                            break;

                        case "trilinear":
                            options.Interpolation = LutInterpolation.Trilinear;
                            break;

                        default:
                            Console.Error.WriteLine(
                                $"lutbake: --interp takes 'tetrahedral' or 'trilinear'; '{interpolation}' is "
                                + "neither.");

                            return 2;
                    }

                    break;

                case "--domain":
                    if (!TryTake(args, ref i, out string minimum)) return Missing(arg);
                    if (!TryTake(args, ref i, out string maximum)) return Missing(arg);
                    if (!TryParseNumber(minimum, out float low) || !TryParseNumber(maximum, out float high))
                    {
                        Console.Error.WriteLine(
                            $"lutbake: --domain takes two numbers; '{minimum} {maximum}' are not two.");

                        return 2;
                    }

                    options.OutputDomainMinimum = new[] { low, low, low };
                    options.OutputDomainMaximum = new[] { high, high, high };
                    break;

                case "--title":
                    if (!TryTake(args, ref i, out title)) return Missing(arg);
                    break;

                case "-o":
                case "--output":
                    if (!TryTake(args, ref i, out output)) return Missing(arg);
                    break;

                default:
                    Console.Error.WriteLine($"lutbake: '{arg}' is not a switch this verb knows.");
                    WriteUsage();
                    return 2;
            }
        }

        if (layers.Count == 0)
        {
            Console.Error.WriteLine("lutbake: at least one --lut is required.");
            WriteUsage();
            return 2;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("lutbake: --output is required.");
            WriteUsage();
            return 2;
        }

        Lut3D effective = LutComposer.Compose(layers, options);

        string folder = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        CubeLutFile.Write(effective, output, title);

        for (int index = 0; index < layers.Count; index++)
        {
            Console.WriteLine($"  layer {index + 1}: {layers[index]}");
        }

        Console.WriteLine(
            $"  effective: {effective} by {options.Interpolation.ToString().ToLowerInvariant()} "
            + "interpolation");

        Console.WriteLine($"  wrote: {output} ({new FileInfo(output).Length} bytes)");
        return 0;
    }

    /// <summary>Writes the verb's own help.</summary>
    public static void WriteUsage()
    {
        Console.Error.WriteLine(
            "  lutbake --lut <file.cube>[@<percent>] [--lut ...] [--size <n>]");
        Console.Error.WriteLine(
            "          [--interp tetrahedral|trilinear] [--domain <min> <max>] [--title <text>]");
        Console.Error.WriteLine(
            "          -o <effective.cube>");
        Console.Error.WriteLine(
            "      Folds the .cube tables into ONE effective table and writes it. Each --lut may");
        Console.Error.WriteLine(
            "      carry '@<percent>' saying how much of it to apply, 0 to 100; the default is 100.");
        Console.Error.WriteLine(
            "      The tables are applied IN ORDER, each sampled at its own size and over its own");
        Console.Error.WriteLine(
            "      domain, so swapping two of them gives a different table.");
        Console.Error.WriteLine(
            "      --size sets the nodes a side of the result; the default is the largest size any");
        Console.Error.WriteLine(
            $"      table has, never below {LutComposer.DefaultMinimumOutputSize} and never above "
            + $"{LutComposer.DefaultMaximumOutputSize}.");
        Console.Error.WriteLine(
            "      --interp is how each table is sampled between its own nodes; tetrahedral is the");
        Console.Error.WriteLine(
            "      default and is what FFmpeg's lut3d filter does.");
        Console.Error.WriteLine(
            "      --domain sets the input range of the RESULT; the default is 0 to 1, which is what");
        Console.Error.WriteLine(
            "      a decoded picture and FFmpeg's raw video both carry.");
        Console.Error.WriteLine(
            "      The written file is what goes to FFmpeg: -vf lut3d=file=<effective.cube>");
    }

    private static bool TryAddLayer(List<LutLayer> layers, string specification)
    {
        string path = specification;
        double percent = 100d;

        int at = specification.LastIndexOf('@');
        if (at > 0
            && double.TryParse(
                specification.Substring(at + 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            path = specification.Substring(0, at);
            percent = parsed;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"lutbake: there is no .cube lookup-table file at '{path}'.");
            return false;
        }

        try
        {
            layers.Add(LutLayer.FromCubeFile(path, percent));
            return true;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"lutbake: '{path}' is not a readable .cube file: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !float.IsNaN(value)
        && !float.IsInfinity(value);

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static int Missing(string switchName)
    {
        Console.Error.WriteLine($"lutbake: {switchName} needs a value after it.");
        WriteUsage();
        return 2;
    }
}
