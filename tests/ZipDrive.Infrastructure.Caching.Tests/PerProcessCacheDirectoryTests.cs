using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
    public void DiskStorageStrategy_CreatesProcessSubdirectory()
    {
        var strategy = new DiskStorageStrategy(
            NullLogger<DiskStorageStrategy>.Instance,
            _baseDir);

        string expected = Path.Combine(_baseDir, $"ZipDrive-{_pid}");
        Directory.Exists(expected).Should().BeTrue("the per-process subdirectory should be created");
    }

    [Fact]
    public async Task DiskStorageStrategy_StoreAsync_StoresFilesInProcessSubdirectory()
    {
        string expectedDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");

        var strategy = new DiskStorageStrategy(
            NullLogger<DiskStorageStrategy>.Instance,
            _baseDir);

        byte[] data = new byte[1024];
        Random.Shared.NextBytes(data);
        var result = new CacheFactoryResult<Stream>
        {
            Value = new MemoryStream(data),
            SizeBytes = data.Length
        };

        var stored = await strategy.StoreAsync(result, CancellationToken.None);

        var files = Directory.GetFiles(expectedDir, "*.zip2vd.cache");
        files.Should().HaveCount(1, "cache file should be stored in the process subdirectory");

        // Cleanup
        strategy.Dispose(stored);
    }

    [Fact]
    public void DiskStorageStrategy_DeleteCacheDirectory_RemovesEntireDirectory()
    {
        string expectedDir = Path.Combine(_baseDir, $"ZipDrive-{_pid}");

        var strategy = new DiskStorageStrategy(
            NullLogger<DiskStorageStrategy>.Instance,
            _baseDir);

        // Create a dummy file in the directory to simulate cache content
        File.WriteAllText(Path.Combine(expectedDir, "dummy.zip2vd.cache"), "test");
        Directory.Exists(expectedDir).Should().BeTrue();

        strategy.DeleteCacheDirectory();

        Directory.Exists(expectedDir).Should().BeFalse("the entire subdirectory should be deleted");
        Directory.Exists(_baseDir).Should().BeTrue("the base directory should NOT be deleted");
    }

    [Fact]
    public void DualTierFileCache_ConstructsCorrectSubdirectoryName()
    {
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 10,
            DiskCacheSizeMb = 10,
            TempDirectory = _baseDir
        });

        var cache = new DualTierFileCache(
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLogger<DualTierFileCache>.Instance,
            NullLoggerFactory.Instance);

        var dirs = Directory.GetDirectories(_baseDir);
        dirs.Should().HaveCount(1);
        Path.GetFileName(dirs[0]).Should().Be($"ZipDrive-{_pid}");
    }

    [Fact]
    public void DualTierFileCache_DeleteCacheDirectory_RemovesDirectory()
    {
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 10,
            DiskCacheSizeMb = 10,
            TempDirectory = _baseDir
        });

        var cache = new DualTierFileCache(
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLogger<DualTierFileCache>.Instance,
            NullLoggerFactory.Instance);

        var dirs = Directory.GetDirectories(_baseDir);
        dirs.Should().HaveCount(1);

        cache.Clear();
        cache.DeleteCacheDirectory();

        Directory.GetDirectories(_baseDir).Should().BeEmpty(
            "DeleteCacheDirectory should remove the per-process subdirectory");
    }

    [Fact]
    public void DiskStorageStrategy_NullTempDirectory_CreatesSubdirectoryUnderSystemTemp()
    {
        var strategy = new DiskStorageStrategy(
            NullLogger<DiskStorageStrategy>.Instance,
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
}
