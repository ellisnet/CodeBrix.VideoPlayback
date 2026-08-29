using System.Runtime.CompilerServices;

// The test project reaches the shader source, the plane-descriptor arithmetic, the LUT atlas layout and the
// CPU LUT applier directly, so that the parts of the graphics path that cannot be exercised without a
// graphics device are still exercised as ordinary functions.
[assembly: InternalsVisibleTo("CodeBrix.VideoPlayback.Skia.Tests")]
