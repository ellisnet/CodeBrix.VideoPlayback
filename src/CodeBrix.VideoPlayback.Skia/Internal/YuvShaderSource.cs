namespace CodeBrix.VideoPlayback.Skia.Internal;

/// <summary>
/// The one shader the graphics path runs: three planes in, one colour-managed pixel out, with the whole
/// effect chain folded into a single lookup.
/// </summary>
/// <remarks>
/// <para>
/// Two variants are built from the same body. The plain one stops at the colour conversion; the other adds
/// the resultant lookup table. Building two rather than binding an identity table to the plain one saves a
/// texture sample per pixel in the common case, which is the case that has to be fast.
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
internal static class YuvShaderSource
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

    /// <summary>The lookup-table uniforms and the atlas sampler, added only to the second variant.</summary>
    private const string LookupPreamble = @"uniform shader lookupTable;
uniform float lookupSize;

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
    internal const string LumaChild = "yPlane";

    /// <summary>The name of the child the first chroma plane is bound to.</summary>
    internal const string ChromaBlueChild = "uPlane";

    /// <summary>The name of the child the second chroma plane is bound to.</summary>
    internal const string ChromaRedChild = "vPlane";

    /// <summary>The name of the child the resultant lookup atlas is bound to.</summary>
    internal const string LookupChild = "lookupTable";

    /// <summary>Builds the shader source.</summary>
    /// <param name="withLookupTable">True to include the resultant-lookup-table stage.</param>
    /// <returns>SkSL source suitable for <c>SKRuntimeEffect.CreateShader</c>.</returns>
    internal static string Build(bool withLookupTable) =>
        withLookupTable
            ? Preamble + LookupPreamble + Body + LookupTail
            : Preamble + Body + PlainTail;
}
