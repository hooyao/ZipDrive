using FluentAssertions;
using Xunit;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.Application.Services;

namespace ZipDrive.Domain.Tests;

public class FormatRegistryTests
{
    private class FakeBuilder : IArchiveStructureBuilder, IArchiveMetadataCleanup
    {
        public string FormatId { get; init; } = "fake";
        public IReadOnlyList<string> SupportedExtensions { get; init; } = [".fake"];
        public List<string> CleanedArchives { get; } = [];

        public Task<ArchiveStructure> BuildAsync(string archiveKey, string absolutePath, CancellationToken ct = default)
            => throw new NotImplementedException();

        public void CleanupArchive(string archiveKey) => CleanedArchives.Add(archiveKey);
    }

    private class FakeExtractor : IArchiveEntryExtractor
    {
        public string FormatId { get; init; } = "fake";
        public Task<ExtractionResult> ExtractAsync(string archivePath, string internalPath, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private class FakePrefetch : IPrefetchStrategy
    {
        public string FormatId { get; init; } = "fake";
        public Task PrefetchAsync(string archivePath, ArchiveStructure structure, string dirInternalPath,
            ArchiveEntryInfo? triggerEntry, IFileContentCache contentCache, PrefetchOptions options, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static FormatRegistry CreateRegistry(
        IArchiveStructureBuilder[]? builders = null,
        IArchiveEntryExtractor[]? extractors = null,
        IPrefetchStrategy[]? prefetchers = null)
    {
        return new FormatRegistry(
            builders ?? [],
            extractors ?? [],
            prefetchers ?? []);
    }

    [Fact]
    public void GetStructureBuilder_returns_registered_builder()
    {
        var builder = new FakeBuilder { FormatId = "zip", SupportedExtensions = [".zip"] };
        var registry = CreateRegistry(builders: [builder]);

        registry.GetStructureBuilder("zip").Should().BeSameAs(builder);
    }

    [Fact]
    public void GetStructureBuilder_unknown_format_throws()
    {
        var registry = CreateRegistry();

        Action act = () => registry.GetStructureBuilder("unknown");
        act.Should().Throw<NotSupportedException>().WithMessage("*unknown*");
    }

    [Fact]
    public void GetExtractor_returns_registered_extractor()
    {
        var extractor = new FakeExtractor { FormatId = "rar" };
        var registry = CreateRegistry(extractors: [extractor]);

        registry.GetExtractor("rar").Should().BeSameAs(extractor);
    }

    [Fact]
    public void GetPrefetchStrategy_returns_null_for_missing()
    {
        var registry = CreateRegistry();

        registry.GetPrefetchStrategy("rar").Should().BeNull();
    }

    [Fact]
    public void GetPrefetchStrategy_returns_registered_strategy()
    {
        var prefetch = new FakePrefetch { FormatId = "zip" };
        var registry = CreateRegistry(prefetchers: [prefetch]);

        registry.GetPrefetchStrategy("zip").Should().BeSameAs(prefetch);
    }

    [Fact]
    public void DetectFormat_by_extension()
    {
        var builder = new FakeBuilder { FormatId = "rar", SupportedExtensions = [".rar"] };
        var registry = CreateRegistry(builders: [builder]);

        registry.DetectFormat("archive.rar").Should().Be("rar");
        registry.DetectFormat("archive.RAR").Should().Be("rar"); // case-insensitive
    }

    [Fact]
    public void DetectFormat_unknown_returns_null()
    {
        var registry = CreateRegistry();
        registry.DetectFormat("file.xyz").Should().BeNull();
    }

    [Fact]
    public void SupportedExtensions_includes_all_formats()
    {
        var zip = new FakeBuilder { FormatId = "zip", SupportedExtensions = [".zip"] };
        var rar = new FakeBuilder { FormatId = "rar", SupportedExtensions = [".rar"] };
        var registry = CreateRegistry(builders: [zip, rar]);

        registry.SupportedExtensions.Should().Contain(".zip").And.Contain(".rar");
    }

    [Fact]
    public void OnArchiveRemoved_fans_out_to_cleanup_handlers()
    {
        var builder = new FakeBuilder { FormatId = "zip", SupportedExtensions = [".zip"] };
        var registry = CreateRegistry(builders: [builder]);

        registry.OnArchiveRemoved("games/doom.zip");

        builder.CleanedArchives.Should().ContainSingle().Which.Should().Be("games/doom.zip");
    }

    [Fact]
    public void FormatId_resolution_is_case_insensitive()
    {
        var builder = new FakeBuilder { FormatId = "zip", SupportedExtensions = [".zip"] };
        var extractor = new FakeExtractor { FormatId = "zip" };
        var registry = CreateRegistry(builders: [builder], extractors: [extractor]);

        registry.GetStructureBuilder("ZIP").Should().BeSameAs(builder);
        registry.GetExtractor("ZIP").Should().BeSameAs(extractor);
    }
}
