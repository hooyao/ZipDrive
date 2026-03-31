using FluentAssertions;

namespace ZipDrive.Infrastructure.Archives.Rar.Tests;

public sealed class RarEntryExtractorTests
{
    private readonly RarEntryExtractor _sut = new();

    // ── Interface properties ─────────────────────────────────────────────────

    [Fact]
    public void FormatId_Returns_Rar()
    {
        _sut.FormatId.Should().Be("rar");
    }

    // ── Warning file extraction (synthetic solid archive content) ─────────────

    [Fact]
    public async Task ExtractAsync_WarningFile_ReturnsStaticContent()
    {
        var result = await _sut.ExtractAsync(
            "nonexistent.rar",
            RarStructureBuilder.UnsupportedWarningFileName);

        result.Should().NotBeNull();
        result.SizeBytes.Should().Be(RarStructureBuilder.SolidWarningContent.Length);

        using var ms = new MemoryStream();
        await result.Stream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(RarStructureBuilder.SolidWarningContent);
    }

    [Fact]
    public async Task ExtractAsync_WarningFile_StreamIsReadable()
    {
        var result = await _sut.ExtractAsync(
            "nonexistent.rar",
            RarStructureBuilder.UnsupportedWarningFileName);

        result.Stream.CanRead.Should().BeTrue();
        result.Stream.Position.Should().Be(0);
        result.Stream.Length.Should().Be(RarStructureBuilder.SolidWarningContent.Length);
    }

    [Fact]
    public async Task ExtractAsync_WarningFile_NoDisposalCallback()
    {
        var result = await _sut.ExtractAsync(
            "nonexistent.rar",
            RarStructureBuilder.UnsupportedWarningFileName);

        // Warning file uses a read-only MemoryStream over static content,
        // no archive resources to clean up
        result.OnDisposed.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_WarningFile_DoesNotTouchArchivePath()
    {
        // Even with a completely invalid path, the warning file should work
        // because it serves static content without opening any archive
        var result = await _sut.ExtractAsync(
            @"Z:\nonexistent\path\to\nowhere.rar",
            RarStructureBuilder.UnsupportedWarningFileName);

        result.SizeBytes.Should().BeGreaterThan(0);
        result.Stream.Should().NotBeNull();
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_NonExistentArchive_ThrowsIOException()
    {
        // SharpCompress throws DirectoryNotFoundException when the parent dir doesn't exist,
        // or FileNotFoundException when only the file is missing. Both inherit IOException.
        var act = () => _sut.ExtractAsync(
            @"C:\nonexistent\archive.rar",
            "some/file.txt");

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task ExtractAsync_InvalidArchiveFile_Throws()
    {
        // Create a file with non-RAR content
        string path = RarFixtures.WriteTempFile(
            System.Text.Encoding.UTF8.GetBytes("This is not a RAR file"));
        try
        {
            var act = () => _sut.ExtractAsync(path, "some/file.txt");
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            RarFixtures.CleanupTempFile(path);
        }
    }
}
