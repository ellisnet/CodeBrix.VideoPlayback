using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// Reads the ".cube" lookup-table files colour-grading tools export.
/// </summary>
/// <remarks>
/// <para>
/// The format is a plain text file: some keywords, then a list of triplets. <c>LUT_3D_SIZE n</c> introduces
/// <c>n * n * n</c> triplets with red changing fastest; <c>LUT_1D_SIZE n</c> introduces <c>n</c> triplets,
/// one per point of three per-channel curves. <c>TITLE</c> names the table, <c>DOMAIN_MIN</c> and
/// <c>DOMAIN_MAX</c> state the input range, blank lines and lines beginning with <c>#</c> are ignored.
/// </para>
/// <para>
/// <b>What is refused.</b> A domain other than 0 to 1 is refused rather than silently misapplied: honouring
/// it means remapping the input before the lookup, which is a decision about the picture and not about the
/// file. Everything else in the format is either read or ignored.
/// </para>
/// </remarks>
public static class CubeLutFile
{
    /// <summary>Reads a ".cube" file from disk.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>An effect applying the table the file holds, named after the file (or its TITLE).</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable ".cube" table.</exception>
    public static LutEffect ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path to the .cube file is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no .cube lookup-table file at '{path}'.", path);
        }

        return Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Parses the text of a ".cube" file.</summary>
    /// <param name="text">The file's whole text.</param>
    /// <param name="fallbackName">
    /// The name to give the effect when the file carries no TITLE. May be null, in which case a generic name
    /// is used.
    /// </param>
    /// <returns>An effect applying the table the text describes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is null.</exception>
    /// <exception cref="InvalidDataException">The text is not a readable ".cube" table.</exception>
    public static LutEffect Parse(string text, string fallbackName)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        string title = null;
        int size3D = 0;
        int size1D = 0;
        List<float> values = new List<float>();
        float[] domainMinimum = { 0f, 0f, 0f };
        float[] domainMaximum = { 1f, 1f, 1f };

        int lineNumber = 0;
        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                title = line.Substring(5).Trim().Trim('"');
                continue;
            }

            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size3D = ReadInteger(line.Substring(11), lineNumber, "LUT_3D_SIZE");
                continue;
            }

            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                size1D = ReadInteger(line.Substring(11), lineNumber, "LUT_1D_SIZE");
                continue;
            }

            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
            {
                domainMinimum = ReadTriplet(line.Substring(10), lineNumber, "DOMAIN_MIN");
                continue;
            }

            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
            {
                domainMaximum = ReadTriplet(line.Substring(10), lineNumber, "DOMAIN_MAX");
                continue;
            }

            if (line.StartsWith("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("LUT_1D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
            {
                float[] range = ReadNumbers(line.Substring(18), lineNumber, "input range", 2);
                domainMinimum = new[] { range[0], range[0], range[0] };
                domainMaximum = new[] { range[1], range[1], range[1] };
                continue;
            }

            float[] triplet = ReadTriplet(line, lineNumber, "a table row");
            values.Add(triplet[0]);
            values.Add(triplet[1]);
            values.Add(triplet[2]);
        }

        for (int channel = 0; channel < 3; channel++)
        {
            if (domainMinimum[channel] != 0f || domainMaximum[channel] != 1f)
            {
                throw new InvalidDataException(
                    "This .cube file states an input domain of "
                    + $"[{domainMinimum[0]}, {domainMaximum[0]}] and this reader applies tables over 0 to 1 "
                    + "only. Re-export the table with the default domain, or build a Lut3D yourself and "
                    + "remap the input with EffectComposer.Apply.");
            }
        }

        string name = !string.IsNullOrWhiteSpace(title)
            ? title
            : (!string.IsNullOrWhiteSpace(fallbackName) ? fallbackName : "cube lookup table");

        if (size3D > 0 && size1D > 0)
        {
            throw new InvalidDataException(
                "This .cube file states both LUT_3D_SIZE and LUT_1D_SIZE; a file holds one table or the "
                + "other, never both.");
        }

        if (size3D > 0)
        {
            int expected = size3D * size3D * size3D * 3;
            if (values.Count != expected)
            {
                throw new InvalidDataException(
                    $"This .cube file states LUT_3D_SIZE {size3D}, which needs {expected / 3} rows of three "
                    + $"numbers; it holds {values.Count / 3}.");
            }

            return new LutEffect(new Lut3D(size3D, values.ToArray()), name);
        }

        if (size1D > 0)
        {
            if (values.Count != size1D * 3)
            {
                throw new InvalidDataException(
                    $"This .cube file states LUT_1D_SIZE {size1D}, which needs {size1D} rows of three "
                    + $"numbers; it holds {values.Count / 3}.");
            }

            float[] red = new float[size1D];
            float[] green = new float[size1D];
            float[] blue = new float[size1D];
            for (int i = 0; i < size1D; i++)
            {
                red[i] = values[i * 3];
                green[i] = values[(i * 3) + 1];
                blue[i] = values[(i * 3) + 2];
            }

            return new LutEffect(new Lut1D(red, green, blue), name);
        }

        throw new InvalidDataException(
            "This text states neither LUT_3D_SIZE nor LUT_1D_SIZE, so it is not a .cube lookup table. Every "
            + "such file names its table's size before its rows.");
    }

    private static int ReadInteger(string text, int lineNumber, string keyword)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value <= 0)
        {
            throw new InvalidDataException(
                $"Line {lineNumber} of this .cube file states '{keyword}' followed by '{text.Trim()}', which "
                + "is not a positive whole number.");
        }

        return value;
    }

    private static float[] ReadTriplet(string text, int lineNumber, string what) =>
        ReadNumbers(text, lineNumber, what, 3);

    private static float[] ReadNumbers(string text, int lineNumber, string what, int count)
    {
        string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count)
        {
            throw new InvalidDataException(
                $"Line {lineNumber} of this .cube file should hold {count} numbers for {what}; it holds "
                + $"{parts.Length}: '{text.Trim()}'.");
        }

        float[] numbers = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                throw new InvalidDataException(
                    $"Line {lineNumber} of this .cube file holds '{parts[i]}' where {what} needs a number.");
            }
        }

        return numbers;
    }
}
