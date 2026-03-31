using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZipDrive.Infrastructure.Archives.Rar.Tests;

public sealed class RarStructureBuilderTests
{
    private readonly RarStructureBuilder _sut = new(NullLogger<RarStructureBuilder>.Instance);

    // ── Interface properties ─────────────────────────────────────────────────

    [Fact]
    public void FormatId_Returns_Rar()
    {
        _sut.FormatId.Should().Be("rar");
    }

    [Fact]
    public void SupportedExtensions_Contains_DotRar()
    {
        _sut.SupportedExtensions.Should().ContainSingle()
            .Which.Should().Be(".rar");
    }

    // ── ProbeAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_NonSolidRar5_ReturnsSupported()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "nonsolid-rar5.rar");
        var result = await _sut.ProbeAsync(path);
        result.IsSupported.Should().BeTrue();
        result.UnsupportedReason.Should().BeNull();
    }

    [Fact]
    public async Task ProbeAsync_SolidRar5_ReturnsUnsupported()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "solid-rar5.rar");
        var result = await _sut.ProbeAsync(path);
        result.IsSupported.Should().BeFalse();
        result.UnsupportedReason.Should().Contain("Solid");
    }

    [Fact]
    public async Task ProbeAsync_NonRarFile_ReturnsSupported()
    {
        // Non-RAR file — IsSolid returns false (can't open), so ProbeAsync returns supported.
        // The actual failure will happen at BuildAsync time.
        string path = RarFixtures.WriteTempFile(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        try
        {
            var result = await _sut.ProbeAsync(path);
            result.IsSupported.Should().BeTrue();
        }
        finally
        {
            RarFixtures.CleanupTempFile(path);
        }
    }

    [Fact]
    public async Task ProbeAsync_NonSolidRar4_ReturnsSupported()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "nonsolid-rar4.rar");
        var result = await _sut.ProbeAsync(path);
        result.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_SolidRar4_ReturnsUnsupported()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "solid-rar4.rar");
        var result = await _sut.ProbeAsync(path);
        result.IsSupported.Should().BeFalse();
        result.UnsupportedReason.Should().Contain("Solid");
    }

    // ── BuildSolidWarningStructure ───────────────────────────────────────────

    [Fact]
    public void BuildSolidWarningStructure_ContainsWarningFile()
    {
        var structure = RarStructureBuilder.BuildSolidWarningStructure("test.rar", @"C:\test.rar");

        structure.ArchiveKey.Should().Be("test.rar");
        structure.AbsolutePath.Should().Be(@"C:\test.rar");
        structure.FormatId.Should().Be("rar");
        structure.Entries.Should().ContainKey(RarStructureBuilder.UnsupportedWarningFileName);
    }

    [Fact]
    public void BuildSolidWarningStructure_WarningFileHasContent()
    {
        var structure = RarStructureBuilder.BuildSolidWarningStructure("test.rar", @"C:\test.rar");

        var entry = structure.GetEntry(RarStructureBuilder.UnsupportedWarningFileName);
        entry.Should().NotBeNull();
        entry!.Value.IsDirectory.Should().BeFalse();
        entry.Value.UncompressedSize.Should().Be(RarStructureBuilder.SolidWarningContent.Length);
        entry.Value.Attributes.Should().HaveFlag(FileAttributes.ReadOnly);
    }

    [Fact]
    public void BuildSolidWarningStructure_HasSingleEntry()
    {
        var structure = RarStructureBuilder.BuildSolidWarningStructure("test.rar", @"C:\test.rar");

        structure.EntryCount.Should().Be(1);
        structure.TotalUncompressedSize.Should().Be(RarStructureBuilder.SolidWarningContent.Length);
        structure.TotalCompressedSize.Should().Be(0);
    }

    // ── NormalizePath ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("folder/file.txt", "folder/file.txt")]
    [InlineData("folder\\file.txt", "folder/file.txt")]
    [InlineData("/folder/file.txt", "folder/file.txt")]
    [InlineData("\\folder\\file.txt", "folder/file.txt")]
    [InlineData("file.txt", "file.txt")]
    [InlineData("", "")]
    public void NormalizePath_ConvertsCorrectly(string input, string expected)
    {
        RarStructureBuilder.NormalizePath(input).Should().Be(expected);
    }

    // ── SolidWarningContent ──────────────────────────────────────────────────

    [Fact]
    public void SolidWarningContent_IsNotEmpty()
    {
        RarStructureBuilder.SolidWarningContent.Should().NotBeEmpty();
    }

    [Fact]
    public void SolidWarningContent_ContainsInstructions()
    {
        string text = System.Text.Encoding.UTF8.GetString(RarStructureBuilder.SolidWarningContent);
        text.Should().Contain("solid compression");
        text.Should().Contain("WinRAR");
        text.Should().Contain("rar a -s-");
    }
}
