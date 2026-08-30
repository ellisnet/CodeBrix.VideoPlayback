using System.Runtime.CompilerServices;

// The test project reaches the surface renderer - the one thing here that is still internal - so that the
// shader can be run against the core converter on Skia's raster backend, on a machine with no graphics
// device at all. Everything else the tests grade against is public, and most of it now lives in the
// drawing-free playback library.
[assembly: InternalsVisibleTo("CodeBrix.VideoPlayback.Skia.Tests")]
