using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.Archives.Zip;

namespace ZipDrive.Infrastructure.Caching.Tests;

public class PerProcessCacheDirectoryTests : IDisposable
{
    private readonly string _baseDir;
    private readonly int _pid = Environment.ProcessId;

    public PerProcessCacheDirectoryTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"zipdrive-perproctest-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void ChunkedDiskStorageStrategy_CreatesProcessSubdirectory()
    {
        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            _baseDir);

        string expected = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        Directory.Exists(expected).Should().BeTrue("the per-process subdirectory should be created");
    }

    [Fact]
    public async Task ChunkedDiskStorageStrategy_MaterializeAsync_StoresFilesInProcessSubdirectory()
    {
        string expectedDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");

        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            _baseDir);

        byte[] data = new byte[1024];
        Random.Shared.NextBytes(data);

        var stored = await strategy.MaterializeAsync(
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            }),
            CancellationToken.None);

        var files = Directory.GetFiles(expectedDir, "*.zip2vd.chunked");
        files.Should().HaveCount(1, "cache file should be stored in the process subdirectory");

        // Cleanup
        strategy.Dispose(stored);
    }

    [Fact]
    public void ChunkedDiskStorageStrategy_DeleteCacheDirectory_RemovesEntireDirectory()
    {
        string expectedDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");

        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            _baseDir);

        // Create a dummy file in the directory to simulate cache content
        File.WriteAllText(Path.Combine(expectedDir, "dummy.zip2vd.chunked"), "test");
        Directory.Exists(expectedDir).Should().BeTrue();

        strategy.DeleteCacheDirectory();

        Directory.Exists(expectedDir).Should().BeFalse("the entire subdirectory should be deleted");
        Directory.Exists(_baseDir).Should().BeTrue("the base directory should NOT be deleted");
    }

    [Fact]
    public void FileContentCache_ConstructsCorrectSubdirectoryName()
    {
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 10,
            DiskCacheSizeMb = 10,
            TempDirectory = _baseDir
        });

        var metadataStore = new ZipFormatMetadataStore();
        var cache = new FileContentCache(
            new StubFormatRegistry(new ZipEntryExtractor(new ZipReaderFactory(), metadataStore)),
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

        var dirs = Directory.GetDirectories(_baseDir);
        dirs.Should().HaveCount(1);
        Path.GetFileName(dirs[0]).Should().Be($"ZipDrive-{_pid}");
    }

    [Fact]
    public void FileContentCache_DeleteCacheDirectory_RemovesDirectory()
    {
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 10,
            DiskCacheSizeMb = 10,
            TempDirectory = _baseDir
        });

        var metadataStore = new ZipFormatMetadataStore();
        var cache = new FileContentCache(
            new StubFormatRegistry(new ZipEntryExtractor(new ZipReaderFactory(), metadataStore)),
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

        var dirs = Directory.GetDirectories(_baseDir);
        dirs.Should().HaveCount(1);

        cache.Clear();
        cache.DeleteCacheDirectory();

        Directory.GetDirectories(_baseDir).Should().BeEmpty(
            "DeleteCacheDirectory should remove the per-process subdirectory");
    }

    [Fact]
    public void ChunkedDiskStorageStrategy_NullTempDirectory_CreatesSubdirectoryUnderSystemTemp()
    {
        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            tempDirectory: null);

        string expected = Path.Combine(Path.GetTempPath(), $"ZipDrive-{_pid}");
        try
        {
            Directory.Exists(expected).Should().BeTrue(
                "the per-process subdirectory should be created under the system temp directory");
        }
        finally
        {
            strategy.DeleteCacheDirectory();
        }
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
