using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Archives.Rar.Tests;

/// <summary>
/// Integration tests using real RAR files created by rar.exe.
/// </summary>
public sealed class RarIntegrationTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "TestFixtures");

    private static string NonSolidRar5 => Path.Combine(FixtureDir, "nonsolid-rar5.rar");
    private static string SolidRar5 => Path.Combine(FixtureDir, "solid-rar5.rar");
    private static string NonSolidRar4 => Path.Combine(FixtureDir, "nonsolid-rar4.rar");
    private static string SolidRar4 => Path.Combine(FixtureDir, "solid-rar4.rar");

    private readonly RarStructureBuilder _builder = new(NullLogger<RarStructureBuilder>.Instance);
    private readonly RarEntryExtractor _extractor = new();

    // ══════════════════════════════════════════════════════════════════════
    // RarSignature.DetectVersion — magic byte detection
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DetectVersion_Rar5()
    {
        byte[] header = new byte[8];
        using var fs = File.OpenRead(NonSolidRar5);
        fs.ReadExactly(header);
        RarSignature.DetectVersion(header).Should().Be(5);
    }

    [Fact]
    public void DetectVersion_Rar4()
    {
        byte[] header = new byte[8];
        using var fs = File.OpenRead(NonSolidRar4);
        fs.ReadExactly(header);
        RarSignature.DetectVersion(header).Should().Be(4);
    }

    // ══════════════════════════════════════════════════════════════════════
    // RarSignature.IsSolid — SharpCompress-based solid detection
    // ══════════════════════════════════════════════════════════════════════

    [Fact] public void IsSolid_NonSolidRar5() => RarSignature.IsSolid(NonSolidRar5).Should().BeFalse();
    [Fact] public void IsSolid_SolidRar5()    => RarSignature.IsSolid(SolidRar5).Should().BeTrue();
    [Fact] public void IsSolid_NonSolidRar4() => RarSignature.IsSolid(NonSolidRar4).Should().BeFalse();
    [Fact] public void IsSolid_SolidRar4()    => RarSignature.IsSolid(SolidRar4).Should().BeTrue();

    // ══════════════════════════════════════════════════════════════════════
    // ProbeAsync — all 4 fixture variants
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProbeAsync_NonSolidRar5_Supported()
    {
        var result = await _builder.ProbeAsync(NonSolidRar5);
        result.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_SolidRar5_Unsupported()
    {
        var result = await _builder.ProbeAsync(SolidRar5);
        result.IsSupported.Should().BeFalse();
        result.UnsupportedReason.Should().Contain("Solid");
    }

    [Fact]
    public async Task ProbeAsync_NonSolidRar4_Supported()
    {
        var result = await _builder.ProbeAsync(NonSolidRar4);
        result.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_SolidRar4_Unsupported()
    {
        var result = await _builder.ProbeAsync(SolidRar4);
        result.IsSupported.Should().BeFalse();
        result.UnsupportedReason.Should().Contain("Solid");
    }

    // ══════════════════════════════════════════════════════════════════════
    // BuildAsync — non-solid RAR5
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildAsync_NonSolidRar5_ReturnsCorrectStructure()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test.rar", NonSolidRar5);

        structure.FormatId.Should().Be("rar");
        structure.ArchiveKey.Should().Be("test.rar");
        structure.EntryCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BuildAsync_NonSolidRar5_ContainsExpectedFiles()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test.rar", NonSolidRar5);

        var files = structure.Entries
            .Where(kv => !kv.Value.IsDirectory)
            .Select(kv => kv.Key)
            .ToList();

        files.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task BuildAsync_NonSolidRar5_SynthesizesParentDirectories()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test.rar", NonSolidRar5);

        var dirs = structure.Entries
            .Where(kv => kv.Value.IsDirectory)
            .Select(kv => kv.Key)
            .ToList();

        dirs.Should().NotBeEmpty("parent directories should be synthesized");
    }

    // ══════════════════════════════════════════════════════════════════════
    // BuildAsync — non-solid RAR4
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildAsync_NonSolidRar4_ReturnsCorrectStructure()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test4.rar", NonSolidRar4);

        structure.FormatId.Should().Be("rar");
        structure.EntryCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BuildAsync_NonSolidRar4_ContainsExpectedFiles()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test4.rar", NonSolidRar4);

        var files = structure.Entries
            .Where(kv => !kv.Value.IsDirectory)
            .Select(kv => kv.Key)
            .ToList();

        files.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    // ══════════════════════════════════════════════════════════════════════
    // BuildAsync — solid (both versions produce warning structure)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildAsync_SolidRar5_ReturnsWarningStructure()
    {
        ArchiveStructure structure = await _builder.BuildAsync("solid5.rar", SolidRar5);

        structure.EntryCount.Should().Be(1);
        var entry = structure.GetEntry(RarStructureBuilder.UnsupportedWarningFileName);
        entry.Should().NotBeNull();
        entry!.Value.UncompressedSize.Should().Be(RarStructureBuilder.SolidWarningContent.Length);
    }

    [Fact]
    public async Task BuildAsync_SolidRar4_ReturnsWarningStructure()
    {
        ArchiveStructure structure = await _builder.BuildAsync("solid4.rar", SolidRar4);

        structure.EntryCount.Should().Be(1);
        structure.GetEntry(RarStructureBuilder.UnsupportedWarningFileName).Should().NotBeNull();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ExtractAsync — non-solid RAR5
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtractAsync_NonSolidRar5_ReturnsDecompressedContent()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test.rar", NonSolidRar5);

        var fileEntry = structure.Entries
            .FirstOrDefault(kv => !kv.Value.IsDirectory && kv.Key.EndsWith(".txt"));
        fileEntry.Key.Should().NotBeNullOrEmpty();

        ExtractionResult result = await _extractor.ExtractAsync("test.rar", NonSolidRar5, fileEntry.Key);

        result.SizeBytes.Should().BeGreaterThan(0);
        result.Stream.Length.Should().Be(result.SizeBytes);

        using var reader = new StreamReader(result.Stream);
        string content = await reader.ReadToEndAsync();
        content.Should().NotBeNullOrEmpty();

        if (result.OnDisposed != null) await result.OnDisposed();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ExtractAsync — non-solid RAR4
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtractAsync_NonSolidRar4_ReturnsDecompressedContent()
    {
        ArchiveStructure structure = await _builder.BuildAsync("test4.rar", NonSolidRar4);

        var fileEntry = structure.Entries
            .FirstOrDefault(kv => !kv.Value.IsDirectory && kv.Key.EndsWith(".txt"));
        fileEntry.Key.Should().NotBeNullOrEmpty();

        ExtractionResult result = await _extractor.ExtractAsync("test4.rar", NonSolidRar4, fileEntry.Key);

        result.SizeBytes.Should().BeGreaterThan(0);
        using var reader = new StreamReader(result.Stream);
        string content = await reader.ReadToEndAsync();
        content.Should().NotBeNullOrEmpty();

        if (result.OnDisposed != null) await result.OnDisposed();
    }

    // ══════════════════════════════════════════════════════════════════════
    // ExtractAsync — error cases + warning file
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtractAsync_WarningFile_ReturnsStaticContent()
    {
        ExtractionResult result = await _extractor.ExtractAsync(
            "solid.rar", SolidRar5, RarStructureBuilder.UnsupportedWarningFileName);

        result.Stream.Should().NotBeNull();
        result.SizeBytes.Should().Be(RarStructureBuilder.SolidWarningContent.Length);

        byte[] content = new byte[result.SizeBytes];
        await result.Stream.ReadExactlyAsync(content);
        content.Should().BeEquivalentTo(RarStructureBuilder.SolidWarningContent);
    }

    [Fact]
    public async Task ExtractAsync_NonExistentEntry_Throws()
    {
        Func<Task> act = () => _extractor.ExtractAsync("test.rar", NonSolidRar5, "nonexistent.txt");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ── End-to-end: Solid RAR three-layer UX ────────────────────────────

    [Fact]
    public async Task SolidRar_ThreeLayerUX_ProbeDetectsSolid()
    {
        // Layer 1: ProbeAsync detects solid before trie registration
        ArchiveProbeResult probe = await _builder.ProbeAsync(SolidRar5);
        probe.IsSupported.Should().BeFalse();

        // Layer 2: BuildAsync produces warning structure with renamed key
        string suffixedKey = "solid.rar" + ArchiveProbeResult.UnsupportedFolderSuffix;
        ArchiveStructure structure = await _builder.BuildAsync(suffixedKey, SolidRar5);
        structure.ArchiveKey.Should().Contain("(NOT SUPPORTED)");
        structure.EntryCount.Should().Be(1);

        // The warning file is readable
        var warningEntry = structure.GetEntry(RarStructureBuilder.UnsupportedWarningFileName);
        warningEntry.Should().NotBeNull();

        // Layer 3: Warning file content includes config hint
        ExtractionResult result = await _extractor.ExtractAsync(
            suffixedKey, SolidRar5, RarStructureBuilder.UnsupportedWarningFileName);
        using var reader = new StreamReader(result.Stream);
        string text = await reader.ReadToEndAsync();
        text.Should().Contain("HideUnsupportedArchives");
        text.Should().Contain("solid compression");
        text.Should().Contain("rar a -s-");
    }
}
