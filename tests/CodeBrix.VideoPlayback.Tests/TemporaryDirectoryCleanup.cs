using System;
using Xunit;

[assembly: AssemblyFixture(typeof(CodeBrix.VideoPlayback.Tests.TemporaryDirectoryCleanup))]

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Sweeps the temporary folders the suite made, once, when the whole run is over.
/// </summary>
/// <remarks>
/// An assembly fixture is created before the first test and disposed after the last, which is the one
/// moment every folder made through <see cref="TestAssets.CreateTemporaryDirectory" /> is certainly no
/// longer in use. The synthetic clips the session and container tests write are small, but there are many
/// of them, and a suite that leaves its scratch files behind is a suite nobody can tell has misbehaved.
/// </remarks>
public sealed class TemporaryDirectoryCleanup : IDisposable
{
    /// <summary>Deletes every folder the run created.</summary>
    public void Dispose() => TestAssets.DeleteTemporaryDirectories();
}
