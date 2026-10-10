using System;
using System.Buffers.Binary;
using CodeBrix.VideoPlayback.Containers.Cbv;

namespace CodeBrix.VideoPlayback.Codecs;

/// <summary>
/// Reads the one thing a container needs from a FLAC track's codec-private data: that it IS a FLAC stream
/// header, and what its STREAMINFO block says.
/// </summary>
/// <remarks>
/// The codec-private data follows Matroska's <c>A_FLAC</c> convention: the four bytes <c>fLaC</c>, then the
/// stream's metadata blocks, of which STREAMINFO (type 0, 34 bytes) comes first and is mandatory. Nothing here
/// decodes audio; the decoder lives in CodeBrix.Audio.Core.
/// </remarks>
internal static class FlacStreamHeader
{
    /// <summary>The length of a STREAMINFO block's body.</summary>
    internal const int StreamInfoLength = 34;

    /// <summary>Checks a FLAC stream header and reads its STREAMINFO.</summary>
    /// <param name="codecPrivate">The codec-private bytes.</param>
    /// <param name="sampleRate">The sample rate STREAMINFO states.</param>
    /// <param name="channels">The channel count STREAMINFO states.</param>
    /// <param name="bitsPerSample">The bits per sample STREAMINFO states.</param>
    /// <param name="problem">What is wrong, or an empty string when nothing is.</param>
    /// <returns>True when the bytes are a FLAC stream header whose first block is a STREAMINFO.</returns>
    internal static bool TryRead(
        ReadOnlySpan<byte> codecPrivate,
        out int sampleRate,
        out int channels,
        out int bitsPerSample,
        out string problem)
    {
        sampleRate = 0;
        channels = 0;
        bitsPerSample = 0;

        if (codecPrivate.Length < 4 || !codecPrivate.Slice(0, 4).SequenceEqual(CbvFormat.FlacMarker))
        {
            problem = "a FLAC track's codec-private data must begin with the stream marker 'fLaC' (Matroska's "
                + "A_FLAC convention), and this does not";
            return false;
        }

        if (codecPrivate.Length < 8 + StreamInfoLength)
        {
            problem = $"a FLAC track's codec-private data is {codecPrivate.Length} bytes, too short to hold the "
                + $"marker and a {StreamInfoLength}-byte STREAMINFO block";
            return false;
        }

        int blockType = codecPrivate[4] & 0x7F;
        int blockLength = (codecPrivate[5] << 16) | (codecPrivate[6] << 8) | codecPrivate[7];

        if (blockType != 0 || blockLength != StreamInfoLength)
        {
            problem = $"a FLAC track's first metadata block must be a {StreamInfoLength}-byte STREAMINFO (type 0); "
                + $"this one is type {blockType} of {blockLength} bytes";
            return false;
        }

        ReadOnlySpan<byte> body = codecPrivate.Slice(8, StreamInfoLength);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(10, 8));
        sampleRate = (int)(packed >> 44);
        channels = (int)((packed >> 41) & 0x7) + 1;
        bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;

        if (sampleRate == 0)
        {
            problem = "a FLAC track's STREAMINFO states a sample rate of zero";
            return false;
        }

        problem = string.Empty;
        return true;
    }
}
