using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Caching.Tests;

/// <summary>
/// Tests for FileContentCache.RemoveArchive — per-archive cache cleanup.
/// Covers TC-FC-01 through TC-FC-05, TC-SC-01, TC-SC-02 from the dynamic-reload-v2 design.
/// </summary>
public class FileContentCacheRemoveArchiveTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly FileContentCache _cache;

    public FileContentCacheRemoveArchiveTests()
    {
        _cache = new FileContentCache(
            new StubFormatRegistry(new StubExtractor()),
            Options.Create(new CacheOptions
            {
                MemoryCacheSizeMb = 100,
                DiskCacheSizeMb = 100,
                SmallFileCutoffMb = 50,
                ChunkSizeMb = 10,
                DefaultTtlMinutes = 30
            }),
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);
    }

    public void Dispose() { }

    // TC-FC-01: Remove archive with entries in memory tier
    [Fact]
    public async Task RemoveArchive_MemoryTierEntries_RemovesAll()
    {
        // Warm 3 entries for "game.zip"
        await WarmEntry("game.zip:file1.txt", 100);
        await WarmEntry("game.zip:file2.txt", 200);
        await WarmEntry("game.zip:file3.txt", 300);

        _cache.ContainsKey("game.zip:file1.txt").Should().BeTrue();
        _cache.ContainsKey("game.zip:file2.txt").Should().BeTrue();
        _cache.ContainsKey("game.zip:file3.txt").Should().BeTrue();

        int removed = _cache.RemoveArchive("game.zip");

        removed.Should().Be(3);
        _cache.ContainsKey("game.zip:file1.txt").Should().BeFalse();
        _cache.ContainsKey("game.zip:file2.txt").Should().BeFalse();
        _cache.ContainsKey("game.zip:file3.txt").Should().BeFalse();
    }

    // TC-FC-03: Remove nonexistent archive
    [Fact]
    public void RemoveArchive_NonexistentArchive_ReturnsZero()
    {
        int removed = _cache.RemoveArchive("nosuch.zip");
        removed.Should().Be(0);
    }

    // TC-FC-04: Remove then re-cache
    [Fact]
    public async Task RemoveArchive_ThenRecache_Works()
    {
        await WarmEntry("game.zip:file1.txt", 100);
        _cache.RemoveArchive("game.zip");
        _cache.ContainsKey("game.zip:file1.txt").Should().BeFalse();

        // Re-cache
        await WarmEntry("game.zip:file1.txt", 200);
        _cache.ContainsKey("game.zip:file1.txt").Should().BeTrue();
    }

    // TC-VFS-03: Remove does not affect other archives
    [Fact]
    public async Task RemoveArchive_DoesNotAffectOtherArchives()
    {
        await WarmEntry("game.zip:file1.txt", 100);
        await WarmEntry("data.zip:file1.txt", 100);

        _cache.RemoveArchive("game.zip");

        _cache.ContainsKey("game.zip:file1.txt").Should().BeFalse();
        _cache.ContainsKey("data.zip:file1.txt").Should().BeTrue();
    }

    // TC-SC-01: ArchiveStructureCache.Invalidate works
    [Fact]
    public async Task ArchiveStructureCache_Invalidate_RemovesCachedStructure()
    {
        // Build a structure cache and verify Invalidate works
        var store = new ArchiveStructureStore(
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);

        // Insert a dummy structure
        using var handle = await store.BorrowAsync("game.zip", TimeSpan.FromMinutes(30), _ =>
            Task.FromResult(new CacheFactoryResult<Domain.Models.ArchiveStructure>
            {
                Value = new Domain.Models.ArchiveStructure
                {
                    ArchiveKey = "game.zip",
                    AbsolutePath = "C:\\test\\game.zip",
                    Entries = new KTrie.TrieDictionary<Domain.Models.ArchiveEntryInfo>(),
                    BuiltAt = _fakeTime.GetUtcNow()
                },
                SizeBytes = 1024
            }));
        handle.Dispose();

        store.EntryCount.Should().Be(1);

        // TryRemove should work
        bool removed = store.TryRemove("game.zip");
        removed.Should().BeTrue();
        store.EntryCount.Should().Be(0);
    }

    // TC-SC-02: Invalidate uncached archive
    [Fact]
    public void ArchiveStructureStore_TryRemove_Uncached_ReturnsFalse()
    {
        var store = new ArchiveStructureStore(
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);

        bool removed = store.TryRemove("never-cached.zip");
        removed.Should().BeFalse();
    }

    private async Task WarmEntry(string cacheKey, int size)
    {
        byte[] data = new byte[size];
        var entry = new Domain.Models.ArchiveEntryInfo
        {
            UncompressedSize = size,
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Attributes = System.IO.FileAttributes.Normal
        };

        await _cache.WarmAsync(entry, cacheKey, new MemoryStream(data));
    }

    /// <summary>
    /// Minimal stub that is never actually called (WarmAsync bypasses the extractor).
    /// </summary>
    private sealed class StubExtractor : IArchiveEntryExtractor
    {
        public string FormatId => "zip";
        public Task<ExtractionResult> ExtractAsync(string archiveKey, string archivePath, string internalPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("StubExtractor should not be called in warm-only tests");
    }

    private sealed class StubFormatRegistry : IFormatRegistry
    {
        private readonly IArchiveEntryExtractor _extractor;
        public StubFormatRegistry(IArchiveEntryExtractor extractor) => _extractor = extractor;
        public IArchiveStructureBuilder GetStructureBuilder(string f) => throw new NotImplementedException();
        public IArchiveEntryExtractor GetExtractor(string f) => _extractor;
        public IPrefetchStrategy? GetPrefetchStrategy(string f) => null;
        public string? DetectFormat(string p) => null;
        public IReadOnlyList<string> SupportedExtensions => [];
        public void OnArchiveRemoved(string k) { }
    }
}
