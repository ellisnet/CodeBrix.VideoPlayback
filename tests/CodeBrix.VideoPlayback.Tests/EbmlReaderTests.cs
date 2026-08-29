using System;
using System.Text;
using CodeBrix.VideoPlayback;
using CodeBrix.VideoPlayback.Containers.Ebml;
using CodeBrix.VideoPlayback.Sources;
using SilverAssertions;
using Xunit;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Pins the EBML layer against RFC 8794: the variable-length encodings, the primitive value types, the
/// bounds that keep a malformed file from being followed, and the checksum.
/// </summary>
public class EbmlReaderTests
{
    [Theory]
    [InlineData(0x80, 1)]
    [InlineData(0xFF, 1)]
    [InlineData(0x40, 2)]
    [InlineData(0x20, 3)]
    [InlineData(0x10, 4)]
    [InlineData(0x08, 5)]
    [InlineData(0x01, 8)]
    public void MeasureVintLength_counts_the_leading_zero_bits(int first, int expected)
        => EbmlReader.MeasureVintLength((byte)first).Should().Be(expected);

    [Fact]
    public void MeasureVintLength_rejects_a_zero_byte()
        => EbmlReader.MeasureVintLength(0).Should().Be(-1);

    [Fact]
    public void TryReadVint_removes_the_marker_bit()
    {
        //Arrange
        byte[] oneByte = { 0x81 };
        byte[] twoByte = { 0x40, 0x7F };

        //Act
        bool first = EbmlReader.TryReadVint(oneByte, out ulong firstValue, out int firstLength);
        bool second = EbmlReader.TryReadVint(twoByte, out ulong secondValue, out int secondLength);

        //Assert
        first.Should().BeTrue();
        firstValue.Should().Be(1UL);
        firstLength.Should().Be(1);
        second.Should().BeTrue();
        secondValue.Should().Be(127UL);
        secondLength.Should().Be(2);
    }

    [Fact]
    public void TryReadVint_refuses_a_value_that_runs_past_the_span()
        => EbmlReader.TryReadVint(new byte[] { 0x40 }, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData(0x80, -63)]
    [InlineData(0xBF, 0)]
    [InlineData(0xFF, 64)]
    public void TryReadSignedVint_recentres_the_range_on_zero(int coded, int expected)
    {
        //Arrange
        byte[] data = { (byte)coded };

        //Act
        bool read = EbmlReader.TryReadSignedVint(data, out long value, out int length);

        //Assert
        read.Should().BeTrue();
        value.Should().Be(expected);
        length.Should().Be(1);
    }

    [Fact]
    public void TryReadElementHeader_reads_a_four_byte_identifier_and_its_size()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0x1A45DFA3, new byte[] { 1, 2, 3 });
        using EbmlReader reader = OpenReader(builder);

        //Act
        bool read = reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Assert
        read.Should().BeTrue();
        header.Id.Should().Be(0x1A45DFA3u);
        header.DataSize.Should().Be(3L);
        header.Offset.Should().Be(0L);
        header.HeaderSize.Should().Be(5);
        header.IsUnknownSize.Should().BeFalse();
        header.EndOffset.Should().Be(8L);
    }

    [Fact]
    public void TryReadElementHeader_reports_an_element_that_declares_no_size()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0x18538067);
        builder.WriteUnknownSize();
        using EbmlReader reader = OpenReader(builder);

        //Act
        bool read = reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Assert
        read.Should().BeTrue();
        header.IsUnknownSize.Should().BeTrue();
        header.DataSize.Should().Be(-1L);
        header.EndOffset.Should().Be(-1L);
    }

    [Fact]
    public void TryReadElementHeader_stops_at_the_bound_it_was_given()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.UInt(0xEC, 1);
        using EbmlReader reader = OpenReader(builder);

        //Act
        bool read = reader.TryReadElementHeader(0, out _);

        //Assert
        read.Should().BeFalse();
    }

    [Fact]
    public void TryReadElementHeader_refuses_an_element_that_runs_past_its_parent()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0xEC);
        builder.WriteSize(1000);
        using EbmlReader reader = OpenReader(builder);

        //Act
        Action read = () => reader.TryReadElementHeader(10, out _);

        //Assert
        read.Should().Throw<VideoPlaybackException>().WithMessage("*its parent ends at 10*");
    }

    [Fact]
    public void TryReadElementHeader_refuses_a_zero_byte_where_an_identifier_belongs()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Raw(new byte[] { 0x00, 0x00 });
        using EbmlReader reader = OpenReader(builder);

        //Act
        Action read = () => reader.TryReadElementHeader(long.MaxValue, out _);

        //Assert
        read.Should().Throw<VideoPlaybackException>().WithMessage("*element identifier was expected*");
    }

    [Fact]
    public void ReadUnsignedInteger_reads_big_endian_bytes()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0xEC, new byte[] { 0x01, 0x02, 0x03 });
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        ulong value = reader.ReadUnsignedInteger(header);

        //Assert
        value.Should().Be(0x010203UL);
    }

    [Fact]
    public void ReadUnsignedInteger_treats_an_empty_payload_as_zero()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0xEC, ReadOnlySpan<byte>.Empty);
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        ulong value = reader.ReadUnsignedInteger(header);

        //Assert
        value.Should().Be(0UL);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-13500000L)]
    [InlineData(127L)]
    [InlineData(-128L)]
    [InlineData(long.MinValue + 1)]
    public void ReadSignedInteger_sign_extends_from_the_stored_width(long expected)
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.SInt(0xFB, expected);
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        long value = reader.ReadSignedInteger(header);

        //Assert
        value.Should().Be(expected);
    }

    [Fact]
    public void ReadFloat_reads_both_widths()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Float32(0x4489, 1.5f);
        builder.Float64(0x4489, 1008.0);
        using EbmlReader reader = OpenReader(builder);

        //Act
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader first);
        double single = reader.ReadFloat(first);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader second);
        double doubleValue = reader.ReadFloat(second);

        //Assert
        single.Should().Be(1.5);
        doubleValue.Should().Be(1008.0);
    }

    [Fact]
    public void ReadFloat_refuses_a_width_the_format_does_not_allow()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0x4489, new byte[] { 1, 2, 3 });
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        Action read = () => reader.ReadFloat(header);

        //Assert
        read.Should().Throw<VideoPlaybackException>().WithMessage("*has to be 0, 4 or 8 bytes*");
    }

    [Fact]
    public void ReadString_stops_at_the_first_nul_because_ebml_pads_rather_than_shortens()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0x4282, Encoding.UTF8.GetBytes("webm\0\0\0"));
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        string value = reader.ReadString(header);

        //Assert
        value.Should().Be("webm");
    }

    [Fact]
    public void ReadString_reads_multi_byte_utf8()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Str(0x7BA9, "Mesures d'ouverture");
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        string value = reader.ReadString(header);

        //Assert
        value.Should().Be("Mesures d'ouverture");
    }

    [Fact]
    public void ReadDate_counts_nanoseconds_from_the_start_of_2001()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.SInt(0x4461, 1_000_000_000L);
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        DateTime value = reader.ReadDate(header);

        //Assert
        value.Should().Be(new DateTime(2001, 1, 1, 0, 0, 1, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadBinary_returns_the_payload_bytes()
    {
        //Arrange
        byte[] payload = { 9, 8, 7, 6, 5 };
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0x63A2, payload);
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        byte[] value = reader.ReadBinary(header);

        //Assert
        value.Should().Equal(payload);
    }

    [Fact]
    public void ReadBinaryInto_grows_the_callers_buffer_then_reuses_it()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.Element(0xA3, new byte[500]);
        builder.Element(0xA3, new byte[400]);
        using EbmlReader reader = OpenReader(builder);
        byte[] buffer = Array.Empty<byte>();

        //Act
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader first);
        int firstLength = reader.ReadBinaryInto(first, ref buffer);
        byte[] afterFirst = buffer;
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader second);
        int secondLength = reader.ReadBinaryInto(second, ref buffer);

        //Assert
        firstLength.Should().Be(500);
        secondLength.Should().Be(400);
        buffer.Should().BeSameAs(afterFirst);
        buffer.Length.Should().BeGreaterThan(499);
    }

    [Fact]
    public void ReadBinary_refuses_a_payload_larger_than_the_limit_before_allocating()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0x63A2);
        builder.WriteSize(1024);
        builder.Raw(new byte[1024]);
        using EbmlReader reader = OpenReader(builder);
        reader.MaxBinaryElementSize = 16;
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        Action read = () => reader.ReadBinary(header);

        //Assert
        read.Should().Throw<VideoPlaybackException>().WithMessage("*16-byte limit*");
    }

    [Fact]
    public void ReadBinary_refuses_a_payload_that_claims_more_than_the_file_holds()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0x63A2);
        builder.WriteSize(4096);
        builder.Raw(new byte[8]);
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        Action read = () => reader.ReadBinary(header);

        //Assert
        read.Should().Throw<VideoPlaybackException>().WithMessage("*but the file is only*");
    }

    [Fact]
    public void SkipElement_refuses_an_element_that_declares_no_size()
    {
        //Arrange
        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0x1F43B675);
        builder.WriteUnknownSize();
        using EbmlReader reader = OpenReader(builder);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader header);

        //Act
        Action skip = () => reader.SkipElement(header);

        //Assert
        skip.Should().Throw<VideoPlaybackException>().WithMessage("*declares no size*");
    }

    [Fact]
    public void EbmlCrc32_matches_the_standard_check_value()
        => EbmlCrc32.Compute(Encoding.ASCII.GetBytes("123456789")).Should().Be(0xCBF43926u);

    [Fact]
    public void EbmlCrc32_can_be_accumulated_in_pieces()
    {
        //Arrange
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        //Act
        uint running = EbmlCrc32.Continue(EbmlCrc32.InitialValue, data.AsSpan(0, 4));
        running = EbmlCrc32.Continue(running, data.AsSpan(4));
        uint pieces = EbmlCrc32.Finish(running);

        //Assert
        pieces.Should().Be(EbmlCrc32.Compute(data));
    }

    [Fact]
    public void ComputeCrc32_reads_the_range_it_is_given()
    {
        //Arrange
        byte[] data = Encoding.ASCII.GetBytes("XX123456789XX");
        using MemoryMediaSource memory = new MemoryMediaSource(data, "crc range");
        using EbmlReader reader = new EbmlReader(memory, leaveSourceOpen: true);

        //Act
        uint crc = reader.ComputeCrc32(2, 9);

        //Assert
        crc.Should().Be(0xCBF43926u);
    }

    [Fact]
    public void VerifyMasterChecksum_accepts_a_master_whose_content_matches()
    {
        //Arrange
        (EbmlReader reader, EbmlElementHeader master, EbmlElementHeader crc) = BuildChecksummedMaster(corrupt: false);

        //Act
        Action verify = () => reader.VerifyMasterChecksum(master, crc, "Tracks");

        //Assert
        verify.Should().NotThrow();
        reader.Dispose();
    }

    [Fact]
    public void VerifyMasterChecksum_refuses_a_master_whose_content_has_changed()
    {
        //Arrange
        (EbmlReader reader, EbmlElementHeader master, EbmlElementHeader crc) = BuildChecksummedMaster(corrupt: true);

        //Act
        Action verify = () => reader.VerifyMasterChecksum(master, crc, "Tracks");

        //Assert
        verify.Should().Throw<VideoPlaybackException>().WithMessage("*The file is damaged*");
        reader.Dispose();
    }

    [Fact]
    public void VerifyMasterChecksum_can_be_switched_off_for_a_diagnostics_tool()
    {
        //Arrange
        (EbmlReader reader, EbmlElementHeader master, EbmlElementHeader crc) = BuildChecksummedMaster(corrupt: true);
        reader.VerifyCrc32 = false;

        //Act
        Action verify = () => reader.VerifyMasterChecksum(master, crc, "Tracks");

        //Assert
        verify.Should().NotThrow();
        reader.Dispose();
    }

    private static (EbmlReader Reader, EbmlElementHeader Master, EbmlElementHeader Crc) BuildChecksummedMaster(bool corrupt)
    {
        MatroskaTestBuilder content = new MatroskaTestBuilder();
        content.UInt(0xD7, 1);
        content.Str(0x86, "V_AV1");
        byte[] payload = content.ToArray();

        MatroskaTestBuilder builder = new MatroskaTestBuilder();
        builder.WriteId(0x1654AE6B);
        builder.WriteSize(6 + payload.Length);
        builder.Raw(MatroskaTestBuilder.Crc32Element(payload));
        builder.Raw(payload);

        byte[] bytes = builder.ToArray();
        if (corrupt) bytes[^1] ^= 0xFF;

        MemoryMediaSource memory = new MemoryMediaSource(bytes, "checksummed master");
        EbmlReader reader = new EbmlReader(memory);
        reader.TryReadElementHeader(long.MaxValue, out EbmlElementHeader master);
        reader.TryReadElementHeader(master.EndOffset, out EbmlElementHeader crc);
        return (reader, master, crc);
    }

    private static EbmlReader OpenReader(MatroskaTestBuilder builder)
        => new EbmlReader(new MemoryMediaSource(builder.ToArray(), "synthetic EBML"));
}
