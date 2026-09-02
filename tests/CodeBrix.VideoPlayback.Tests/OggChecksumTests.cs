using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Containers.Ogg;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Pins the Ogg page checksum to its published parameters, and pins the fact that it is NOT the CRC-32
/// everything else in this library uses.
/// </summary>
/// <remarks>
/// The vectors below were computed from the specification's parameters - direct (non-reflected) polynomial
/// 0x04C11DB7, initial value zero, no final exclusive-or - independently of the implementation they check.
/// The strongest one is the last: a page an encoder actually wrote, checksummed against the value that
/// encoder stored in it.
/// </remarks>
public class OggChecksumTests
{
    [Fact]
    public void Nothing_at_all_checksums_to_zero()
    {
        //Arrange
        ReadOnlySpan<byte> nothing = ReadOnlySpan<byte>.Empty;

        //Act
        uint checksum = OggChecksum.Compute(nothing);

        //Assert
        checksum.Should().Be(0u);
    }

    [Fact]
    public void The_check_string_produces_the_value_the_parameters_say_it_should()
    {
        //Arrange - the customary check string for a thirty-two bit checksum.
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        //Act
        uint checksum = OggChecksum.Compute(data);

        //Assert
        checksum.Should().Be(0x89A1897Fu);
    }

    [Fact]
    public void The_capture_pattern_alone_produces_its_own_known_value()
    {
        //Arrange
        byte[] data = Encoding.ASCII.GetBytes("OggS");

        //Act
        uint checksum = OggChecksum.Compute(data);

        //Assert
        checksum.Should().Be(0x5FB0A94Fu);
    }

    [Fact]
    public void It_is_a_different_checksum_from_the_one_ebml_uses()
    {
        //Arrange
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        //Act
        uint ogg = OggChecksum.Compute(data);
        uint ebml = EbmlCrc32.Compute(data);

        //Assert - both are called CRC-32 and they never agree; that is the whole point of publishing both.
        ebml.Should().Be(0xCBF43926u);
        (ogg == ebml).Should().BeFalse();
    }

    [Fact]
    public void A_page_an_encoder_wrote_checksums_to_the_value_it_stored()
    {
        //Arrange - the first page of the committed Ogg Vorbis file, with its own checksum field zeroed.
        byte[] file = File.ReadAllBytes(TestAssets.Path("vorbis-audio.ogg"));
        int segments = file[26];
        int payload = 0;
        for (int i = 0; i < segments; i++) payload += file[27 + i];

        int pageLength = 27 + segments + payload;
        byte[] page = new byte[pageLength];
        Array.Copy(file, page, pageLength);

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), 0);

        //Act
        uint computed = OggChecksum.Compute(page);

        //Assert
        computed.Should().Be(stored);
    }
}
