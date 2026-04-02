using FluentAssertions;

namespace ZipDrive.Infrastructure.Archives.Rar.Tests;

public sealed class RarSignatureTests
{
    // ── DetectVersion ────────────────────────────────────────────────────────

    [Fact]
    public void DetectVersion_Rar5Signature_Returns5()
    {
        int version = RarSignature.DetectVersion(RarFixtures.Rar5NonSolidHeader);
        version.Should().Be(5);
    }

    [Fact]
    public void DetectVersion_Rar4Signature_Returns4()
    {
        int version = RarSignature.DetectVersion(RarFixtures.Rar4NonSolidHeader);
        version.Should().Be(4);
    }

    [Fact]
    public void DetectVersion_Rar5SolidSignature_Returns5()
    {
        int version = RarSignature.DetectVersion(RarFixtures.Rar5SolidHeader);
        version.Should().Be(5);
    }

    [Fact]
    public void DetectVersion_Rar4SolidSignature_Returns4()
    {
        int version = RarSignature.DetectVersion(RarFixtures.Rar4SolidHeader);
        version.Should().Be(4);
    }

    [Fact]
    public void DetectVersion_EmptyBuffer_Returns0()
    {
        int version = RarSignature.DetectVersion(ReadOnlySpan<byte>.Empty);
        version.Should().Be(0);
    }

    [Fact]
    public void DetectVersion_TooShort_Returns0()
    {
        int version = RarSignature.DetectVersion(new byte[] { 0x52, 0x61, 0x72 });
        version.Should().Be(0);
    }

    [Fact]
    public void DetectVersion_ZipSignature_Returns0()
    {
        // ZIP local file header signature
        int version = RarSignature.DetectVersion(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 });
        version.Should().Be(0);
    }

    [Fact]
    public void DetectVersion_AllZeros_Returns0()
    {
        int version = RarSignature.DetectVersion(new byte[16]);
        version.Should().Be(0);
    }

    // ── IsSolid (uses SharpCompress, needs valid RAR files) ────────────────

    [Fact]
    public void IsSolid_NonRarFile_ReturnsFalse()
    {
        string path = RarFixtures.WriteTempFile(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 });
        try
        {
            bool isSolid = RarSignature.IsSolid(path);
            isSolid.Should().BeFalse();
        }
        finally
        {
            RarFixtures.CleanupTempFile(path);
        }
    }

    [Fact]
    public void IsSolid_TruncatedFile_ReturnsFalse()
    {
        string path = RarFixtures.WriteTempFile(new byte[] { 0x52, 0x61, 0x72 });
        try
        {
            bool isSolid = RarSignature.IsSolid(path);
            isSolid.Should().BeFalse();
        }
        finally
        {
            RarFixtures.CleanupTempFile(path);
        }
    }
}
