using System;

namespace CodeBrix.VideoPlayback.Skia.Effects;

/// <summary>
/// The grid an effect chain is folded into: one resultant three-dimensional lookup table that says what every
/// colour becomes once every effect in the chain has had its say.
/// </summary>
/// <remarks>
/// <para>
/// A composer starts as the table that changes nothing. Each effect in turn is handed the composer and
/// applies itself, so effect two sees the colours effect one produced - which is what makes the chain's ORDER
/// meaningful. When the last effect has run, the presenter turns the grid into one texture and the shader
/// samples it once per pixel.
/// </para>
/// <para>
/// The grid is sampled, not exact: a colour that falls between nodes is interpolated from the eight around
/// it. A 33-node grid - the default, and the size the ".cube" convention settled on - is enough for any
/// smooth colour change. A chain containing a hard step (a posterise, a key) wants a larger grid, or a layer.
/// </para>
/// <para>
/// An instance is used by one thread at a time, during composition; it is not shared with the drawing path.
/// </para>
/// </remarks>
public sealed class EffectComposer
{
    private readonly float[] nodes;

    internal EffectComposer(int size)
    {
        Size = size;
        nodes = new float[size * size * size * 3];
        Reset();
    }

    /// <summary>The number of nodes along each axis of the grid.</summary>
    public int Size { get; }

    /// <summary>How many nodes the grid holds altogether - <c>Size</c> cubed.</summary>
    public int NodeCount => Size * Size * Size;

    internal float[] Nodes => nodes;

    /// <summary>Puts the grid back to the table that changes nothing.</summary>
    public void Reset()
    {
        float last = Size - 1;
        int index = 0;
        for (int b = 0; b < Size; b++)
        {
            for (int g = 0; g < Size; g++)
            {
                for (int r = 0; r < Size; r++)
                {
                    nodes[index++] = r / last;
                    nodes[index++] = g / last;
                    nodes[index++] = b / last;
                }
            }
        }
    }

    /// <summary>Composes a three-dimensional table onto whatever the grid already holds.</summary>
    /// <param name="lut">The table to apply after the effects already composed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public void ApplyLut(Lut3D lut)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));

        for (int index = 0; index < nodes.Length; index += 3)
        {
            lut.Sample(
                nodes[index],
                nodes[index + 1],
                nodes[index + 2],
                out float red,
                out float green,
                out float blue);

            nodes[index] = red;
            nodes[index + 1] = green;
            nodes[index + 2] = blue;
        }
    }

    /// <summary>Composes a set of per-channel curves onto whatever the grid already holds.</summary>
    /// <param name="lut">The curves to apply after the effects already composed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lut" /> is null.</exception>
    public void ApplyLut(Lut1D lut)
    {
        if (lut == null) throw new ArgumentNullException(nameof(lut));

        for (int index = 0; index < nodes.Length; index += 3)
        {
            lut.Sample(
                nodes[index],
                nodes[index + 1],
                nodes[index + 2],
                out float red,
                out float green,
                out float blue);

            nodes[index] = red;
            nodes[index + 1] = green;
            nodes[index + 2] = blue;
        }
    }

    /// <summary>Composes an arbitrary colour function onto whatever the grid already holds.</summary>
    /// <param name="transform">The function, called once for every node of the grid.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transform" /> is null.</exception>
    /// <remarks>
    /// This is the general form: anything that can be written as "this colour becomes that colour" can be an
    /// effect without being a lookup table first.
    /// </remarks>
    public void Apply(VideoColorTransform transform)
    {
        if (transform == null) throw new ArgumentNullException(nameof(transform));

        for (int index = 0; index < nodes.Length; index += 3)
        {
            float red = nodes[index];
            float green = nodes[index + 1];
            float blue = nodes[index + 2];

            transform(ref red, ref green, ref blue);

            nodes[index] = red;
            nodes[index + 1] = green;
            nodes[index + 2] = blue;
        }
    }

    /// <summary>Reads one node of the grid.</summary>
    /// <param name="redIndex">The node's index along the red axis, 0 to <c>Size - 1</c>.</param>
    /// <param name="greenIndex">The node's index along the green axis, 0 to <c>Size - 1</c>.</param>
    /// <param name="blueIndex">The node's index along the blue axis, 0 to <c>Size - 1</c>.</param>
    /// <param name="red">The node's red output.</param>
    /// <param name="green">The node's green output.</param>
    /// <param name="blue">The node's blue output.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the grid.</exception>
    public void GetNode(
        int redIndex,
        int greenIndex,
        int blueIndex,
        out float red,
        out float green,
        out float blue)
    {
        if (redIndex < 0 || redIndex >= Size) throw new ArgumentOutOfRangeException(nameof(redIndex));
        if (greenIndex < 0 || greenIndex >= Size) throw new ArgumentOutOfRangeException(nameof(greenIndex));
        if (blueIndex < 0 || blueIndex >= Size) throw new ArgumentOutOfRangeException(nameof(blueIndex));

        int offset = (((blueIndex * Size) + greenIndex) * Size + redIndex) * 3;
        red = nodes[offset];
        green = nodes[offset + 1];
        blue = nodes[offset + 2];
    }

    /// <summary>Turns the composed grid into a standalone table, for inspection or for reuse.</summary>
    /// <returns>A copy of the grid as a <see cref="Lut3D" />.</returns>
    public Lut3D ToLut3D() => new Lut3D(Size, nodes);

    /// <inheritdoc />
    public override string ToString() => $"effect composer, {Size}x{Size}x{Size} nodes";
}
