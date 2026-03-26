using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

        string processDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        Directory.Exists(processDir).Should().BeTrue("the per-process subdirectory should be created");
        // Each scope gets a unique subdirectory under the process dir
        Directory.GetDirectories(processDir).Should().HaveCount(1,
            "a unique scope subdirectory should be created under the process directory");
    }

    [Fact]
    public async Task ChunkedDiskStorageStrategy_MaterializeAsync_StoresFilesInProcessSubdirectory()
    {
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

        // Find the scope subdirectory under ZipDrive-{pid}
        string processDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        var scopeDirs = Directory.GetDirectories(processDir);
        scopeDirs.Should().HaveCount(1);
        var files = Directory.GetFiles(scopeDirs[0], "*.zip2vd.chunked");
        files.Should().HaveCount(1, "cache file should be stored in the scope subdirectory");

        // Cleanup
        strategy.Dispose(stored);
    }

    [Fact]
    public void ChunkedDiskStorageStrategy_DeleteCacheDirectory_RemovesEntireDirectory()
    {
        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            _baseDir);

        // Find the scope subdirectory
        string processDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        var scopeDirs = Directory.GetDirectories(processDir);
        scopeDirs.Should().HaveCount(1);
        string scopeDir = scopeDirs[0];

        // Create a dummy file in the directory to simulate cache content
        File.WriteAllText(Path.Combine(scopeDir, "dummy.zip2vd.chunked"), "test");
        Directory.Exists(scopeDir).Should().BeTrue();

        strategy.DeleteCacheDirectory();

        Directory.Exists(scopeDir).Should().BeFalse("the scope subdirectory should be deleted");
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

        var cache = new FileContentCache(
            new ZipReaderFactory(),
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

        // Process-level dir should exist
        string processDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        Directory.Exists(processDir).Should().BeTrue();
        // Scope-level subdirectory should exist under it
        Directory.GetDirectories(processDir).Should().HaveCount(1);
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

        var cache = new FileContentCache(
            new ZipReaderFactory(),
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

        string processDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        Directory.GetDirectories(processDir).Should().HaveCount(1);

        cache.Clear();
        cache.DeleteCacheDirectory();

        // The scope subdirectory should be gone
        Directory.GetDirectories(processDir).Should().BeEmpty(
            "DeleteCacheDirectory should remove the scope subdirectory");
    }

    [Fact]
    public void ChunkedDiskStorageStrategy_NullTempDirectory_CreatesSubdirectoryUnderSystemTemp()
    {
        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024 * 1024,
            tempDirectory: null);

        string processDir = Path.Combine(Path.GetTempPath(), $"ZipDrive-{_pid}");
        try
        {
            Directory.Exists(processDir).Should().BeTrue(
                "the per-process subdirectory should be created under the system temp directory");
            Directory.GetDirectories(processDir).Should().NotBeEmpty(
                "a scope subdirectory should exist under the process directory");
        }
        finally
        {
            strategy.DeleteCacheDirectory();
        }
    }
}
