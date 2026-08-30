using System;
using CodeBrix.VideoPlayback.Color.Luts;

namespace CodeBrix.VideoPlayback.Rendering;

/// <summary>
/// The one shader the graphics path runs: three planes in, one colour-managed pixel out, with the whole
/// effect chain folded into a single lookup.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this class hands back is TEXT.</b> The text is written in SkSL, the shading language Skia
/// compiles, because that is the dialect every presenter in this family feeds to a runtime effect. That
/// makes the source content, not a dependency: this package references no drawing library, names no drawing
/// type, and never compiles anything. A presenter takes the string, compiles it with whatever library it is
/// built on, and binds the children named below. Keeping the text here rather than in a presenter package is
/// what stops every presenter from carrying its own copy of the one thing that IS the colour arithmetic.
/// </para>
/// <para>
/// THREE variants are built from the same body: one that stops at the colour conversion, and one for each
/// way of reading the resultant lookup table between its nodes. Building separate shaders rather than
/// binding an identity table to the plain one saves a texture sample per pixel in the common case, and
/// building one PER INTERPOLATION rather than branching on a uniform keeps the fetch count of each variant
/// down to what that variant actually needs - two for trilinear, four for tetrahedral - instead of the
/// larger of the two. The chain changes rarely and the shaders are cached, so compiling one more costs
/// nothing that is ever measured.
/// </para>
/// <para>
/// <b>The lookup atlas.</b> SkSL has no three-dimensional sampler, so the resultant table is laid out as a
/// two-dimensional strip: <c>n</c> tiles of <c>n</c> by <c>n</c> side by side, tile <c>b</c> holding the
/// slice for that blue index, with red running across a tile and green down it. Interpolation in red and
/// green comes free from the sampler; the shader interpolates between two tiles itself for blue. Clamping
/// the input to 0 to 1 before scaling by <c>n - 1</c> is what keeps a sample inside its own tile, so no tile
/// ever bleeds into its neighbour.
/// </para>
/// <para>
/// <b>Chroma siting.</b> A luma coordinate maps to a chroma coordinate one of three ways per axis: straight
/// through when the axis is not subsampled; snapped to the covering chroma texel's centre when the chroma
/// sample sits ON the luma sample; and simply halved when it sits BETWEEN two, which makes the sampler's own
/// linear filter produce the three-to-one blend the specification asks for. Halving is right because a pixel
/// centre at <c>x + 0.5</c> becomes <c>x / 2 + 0.25</c>, a quarter of a chroma texel from the near centre -
/// exactly the weights 3/4 and 1/4.
/// </para>
/// </remarks>
public static class YuvShaderSource
{
    /// <summary>The uniforms and helper both variants share.</summary>
    private const string Preamble = @"uniform shader yPlane;
uniform shader uPlane;
uniform shader vPlane;
uniform float2 chromaShift;
uniform float2 chromaCosited;
uniform float planeMaximum;
uniform float3 sampleOffsets;
uniform float3 redRow;
uniform float3 greenRow;
uniform float3 blueRow;
";

    /// <summary>The lookup-table uniforms, shared by both lookup variants.</summary>
    private const string LookupUniforms = @"uniform shader lookupTable;
uniform float lookupSize;
";

    /// <summary>
    /// The TRILINEAR atlas read: two fetches, and the sampler's own filter does red and green.
    /// </summary>
    /// <remarks>
    /// The atlas is bound with a LINEAR filter for this variant, so a fetch part way across a tile already
    /// comes back blended in red and green; only blue needs mixing, because that axis crosses whole tiles.
    /// Clamping to 0 to 1 before scaling by <c>n - 1</c> keeps a fetch inside its own tile, so no tile ever
    /// bleeds into its neighbour. This is what a graphics card's texture unit does natively and it is the
    /// cheapest correct read there is.
    /// </remarks>
    private const string TrilinearLookup = @"
float3 sampleLookupTable(float3 colour) {
    float nodes = lookupSize;
    float3 position = clamp(colour, 0.0, 1.0) * (nodes - 1.0);
    float lowerBlue = floor(position.b);
    float upperBlue = min(lowerBlue + 1.0, nodes - 1.0);
    float blueFraction = position.b - lowerBlue;
    float2 within = float2(position.r + 0.5, position.g + 0.5);
    float3 lower = float3(lookupTable.eval(float2((lowerBlue * nodes) + within.x, within.y)).rgb);
    float3 upper = float3(lookupTable.eval(float2((upperBlue * nodes) + within.x, within.y)).rgb);
    return mix(lower, upper, blueFraction);
}
";

    /// <summary>
    /// The TETRAHEDRAL atlas read: four exact node fetches and the wedge the colour falls in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cell the colour lands in is split into six tetrahedra around its black-to-white diagonal. Which
    /// one the colour is in is decided by the ORDER of the three fractional parts, and the answer is the
    /// cell's black corner plus three steps taken in that order - so four corners are read, never eight, and
    /// every weight is non-negative and they sum to one. It is what colour-grading tools and FFmpeg's
    /// <c>lut3d</c> filter do, and it holds the neutral axis exactly.
    /// </para>
    /// <para>
    /// The atlas is bound with a NEAREST filter for this variant, because these fetches must be the node
    /// values themselves and not a blend of them. Every coordinate is a texel centre - an integer plus a
    /// half - which is what makes the fetch exact.
    /// </para>
    /// </remarks>
    private const string TetrahedralLookup = @"
float3 lookupNode(float red, float green, float blue) {
    return float3(lookupTable.eval(float2((blue * lookupSize) + red + 0.5, green + 0.5)).rgb);
}

float3 sampleLookupTable(float3 colour) {
    float nodes = lookupSize;
    float3 position = clamp(colour, 0.0, 1.0) * (nodes - 1.0);
    float3 origin = min(floor(position), nodes - 2.0);
    float3 fraction = position - origin;

    float3 black = lookupNode(origin.r, origin.g, origin.b);
    float3 white = lookupNode(origin.r + 1.0, origin.g + 1.0, origin.b + 1.0);

    float3 first;
    float3 second;
    float firstWeight;
    float secondWeight;
    float thirdWeight;

    if (fraction.r > fraction.g) {
        if (fraction.g > fraction.b) {
            first = lookupNode(origin.r + 1.0, origin.g, origin.b);
            second = lookupNode(origin.r + 1.0, origin.g + 1.0, origin.b);
            firstWeight = fraction.r; secondWeight = fraction.g; thirdWeight = fraction.b;
        } else if (fraction.r > fraction.b) {
            first = lookupNode(origin.r + 1.0, origin.g, origin.b);
            second = lookupNode(origin.r + 1.0, origin.g, origin.b + 1.0);
            firstWeight = fraction.r; secondWeight = fraction.b; thirdWeight = fraction.g;
        } else {
            first = lookupNode(origin.r, origin.g, origin.b + 1.0);
            second = lookupNode(origin.r + 1.0, origin.g, origin.b + 1.0);
            firstWeight = fraction.b; secondWeight = fraction.r; thirdWeight = fraction.g;
        }
    } else {
        if (fraction.b > fraction.g) {
            first = lookupNode(origin.r, origin.g, origin.b + 1.0);
            second = lookupNode(origin.r, origin.g + 1.0, origin.b + 1.0);
            firstWeight = fraction.b; secondWeight = fraction.g; thirdWeight = fraction.r;
        } else if (fraction.b > fraction.r) {
            first = lookupNode(origin.r, origin.g + 1.0, origin.b);
            second = lookupNode(origin.r, origin.g + 1.0, origin.b + 1.0);
            firstWeight = fraction.g; secondWeight = fraction.b; thirdWeight = fraction.r;
        } else {
            first = lookupNode(origin.r, origin.g + 1.0, origin.b);
            second = lookupNode(origin.r + 1.0, origin.g + 1.0, origin.b);
            firstWeight = fraction.g; secondWeight = fraction.r; thirdWeight = fraction.b;
        }
    }

    return black
        + (firstWeight * (first - black))
        + (secondWeight * (second - first))
        + (thirdWeight * (white - second));
}
";

    /// <summary>The colour conversion itself, identical in both variants.</summary>
    private const string Body = @"
float2 chromaCoordinate(float2 lumaCoordinate) {
    float2 snapped = floor(floor(lumaCoordinate) * 0.5) + 0.5;
    float2 halfway = lumaCoordinate * 0.5;
    float2 subsampled = mix(halfway, snapped, chromaCosited);
    return mix(lumaCoordinate, subsampled, chromaShift);
}

half4 main(float2 coordinate) {
    float2 chroma = chromaCoordinate(coordinate);
    float3 samples = float3(
        yPlane.eval(coordinate).r,
        uPlane.eval(chroma).r,
        vPlane.eval(chroma).r) * planeMaximum;
    float3 centred = samples - sampleOffsets;
    float3 colour = clamp(
        float3(dot(redRow, centred), dot(greenRow, centred), dot(blueRow, centred)) * (1.0 / 255.0),
        0.0,
        1.0);
";

    private const string PlainTail = @"    return half4(half3(colour), 1.0);
}
";

    private const string LookupTail = @"    colour = clamp(sampleLookupTable(colour), 0.0, 1.0);
    return half4(half3(colour), 1.0);
}
";

    /// <summary>The name of the child the luma plane is bound to.</summary>
    public const string LumaChild = "yPlane";

    /// <summary>The name of the child the first chroma plane is bound to.</summary>
    public const string ChromaBlueChild = "uPlane";

    /// <summary>The name of the child the second chroma plane is bound to.</summary>
    public const string ChromaRedChild = "vPlane";

    /// <summary>The name of the child the resultant lookup atlas is bound to.</summary>
    public const string LookupChild = "lookupTable";

    /// <summary>Builds the shader source for the variant that stops at the colour conversion.</summary>
    /// <returns>SkSL source, ready for a runtime-effect compiler.</returns>
    public static string Build() => Preamble + Body + PlainTail;

    /// <summary>Builds the shader source for a variant that reads the resultant lookup table.</summary>
    /// <param name="interpolation">How the table is read between its nodes.</param>
    /// <returns>SkSL source, ready for a runtime-effect compiler.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The interpolation is not one this shader knows.</exception>
    public static string Build(LutInterpolation interpolation)
    {
        string lookup = interpolation switch
        {
            LutInterpolation.Tetrahedral => TetrahedralLookup,
            LutInterpolation.Trilinear => TrilinearLookup,
            _ => throw new ArgumentOutOfRangeException(
                nameof(interpolation),
                interpolation,
                "The colour shader reads a lookup table tetrahedrally or trilinearly."),
        };

        return Preamble + LookupUniforms + lookup + Body + LookupTail;
    }

    /// <summary>Whether a variant needs the atlas bound with a smoothing filter.</summary>
    /// <param name="interpolation">How the table is read between its nodes.</param>
    /// <returns>
    /// True when the sampler's own filter does part of the work - trilinear - and false when the shader
    /// fetches node values itself and a filter would corrupt them.
    /// </returns>
    public static bool NeedsFilteredAtlas(LutInterpolation interpolation) =>
        interpolation == LutInterpolation.Trilinear;
}
