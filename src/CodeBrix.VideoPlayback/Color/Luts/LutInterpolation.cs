namespace CodeBrix.VideoPlayback.Color.Luts;

/// <summary>
/// How a colour that falls between the nodes of a three-dimensional lookup table is worked out.
/// </summary>
/// <remarks>
/// <para>
/// Every input colour lands inside one cell of the cube, and the cell has eight corners. The two methods
/// differ in how many of those corners they use and therefore in how a table's shape is reconstructed
/// between its nodes. They agree exactly whenever the table is separable and linear along each axis - an
/// identity table, a gain, an inversion - and differ only where the table bends.
/// </para>
/// <para>
/// <see cref="Tetrahedral" /> is the default everywhere in this library because it is what colour-grading
/// tools and FFmpeg's <c>lut3d</c> filter use by default, so a table baked here and applied there agrees.
/// </para>
/// </remarks>
public enum LutInterpolation
{
    /// <summary>
    /// Splits the cell into six tetrahedra sharing the black-to-white diagonal and interpolates inside the
    /// one the colour falls in, using four corners.
    /// </summary>
    /// <remarks>
    /// This preserves the neutral axis exactly - a grey in stays a grey out when the table says so - which
    /// is why grading tools prefer it. It is also FFmpeg's <c>lut3d</c> default.
    /// </remarks>
    Tetrahedral = 0,

    /// <summary>Blends all eight corners of the cell, weighted by distance along each axis.</summary>
    /// <remarks>
    /// This is what a graphics card's own texture filter does, so it is what the shader path produces
    /// between the nodes of the resultant table. It can pull a colour slightly off the neutral axis where
    /// the table bends.
    /// </remarks>
    Trilinear = 1,
}
