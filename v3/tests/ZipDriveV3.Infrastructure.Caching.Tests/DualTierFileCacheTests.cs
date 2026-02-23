using FluentAssertions;

namespace ZipDriveV3.Infrastructure.Caching.Tests;

public class DualTierFileCacheTests
{
    private static CacheOptions DefaultOptions(int cutoffMb = 1) => new()
    {
        MemoryCacheSizeMb = 100,
        DiskCacheSizeMb = 200,
        SmallFileCutoffMb = cutoffMb,
        TempDirectory = Path.Combine(Path.GetTempPath(), $"zipdrive-test-{Guid.NewGuid()}")
    };

    private static DualTierFileCache CreateCache(CacheOptions? options = null)
    {
        var opts = options ?? DefaultOptions();
        return new DualTierFileCache(opts, new LruEvictionPolicy());
    }

    private static Func<CancellationToken, Task<CacheFactoryResult<Stream>>> CreateFactory(int sizeBytes)
    {
        return _ =>
        {
            byte[] data = new byte[sizeBytes];
            Random.Shared.NextBytes(data);
            return Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = sizeBytes
            });
        };
    }

    [Fact]
    public async Task SmallFile_RoutesToMemoryTier()
    {
        var cache = CreateCache(DefaultOptions(cutoffMb: 1)); // 1 MB cutoff
        int smallSize = 512; // 512 bytes - well under 1 MB

        using var handle = await cache.BorrowAsync("small-file", TimeSpan.FromMinutes(5),
            sizeHintBytes: smallSize, CreateFactory(smallSize));

        handle.Value.Should().NotBeNull();
        cache.MemoryTier.EntryCount.Should().Be(1);
        cache.DiskTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task LargeFile_RoutesToDiskTier()
    {
        var options = DefaultOptions(cutoffMb: 1);
        var cache = CreateCache(options);
        int largeSize = 2 * 1024 * 1024; // 2 MB - above 1 MB cutoff

        using var handle = await cache.BorrowAsync("large-file", TimeSpan.FromMinutes(5),
            sizeHintBytes: largeSize, CreateFactory(largeSize));

        handle.Value.Should().NotBeNull();
        cache.MemoryTier.EntryCount.Should().Be(0);
        cache.DiskTier.EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task CacheHit_ReturnsFromCorrectTier()
    {
        var cache = CreateCache(DefaultOptions(cutoffMb: 1));
        int smallSize = 256;

        // First call - cache miss, materializes
        using (var handle1 = await cache.BorrowAsync("test-key", TimeSpan.FromMinutes(5),
            sizeHintBytes: smallSize, CreateFactory(smallSize)))
        {
            handle1.Value.Should().NotBeNull();
        }

        // Second call - cache hit
        using (var handle2 = await cache.BorrowAsync("test-key", TimeSpan.FromMinutes(5),
            sizeHintBytes: smallSize, CreateFactory(smallSize)))
        {
            handle2.Value.Should().NotBeNull();
        }

        cache.MemoryTier.EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task AggregatedProperties_SumBothTiers()
    {
        var options = DefaultOptions(cutoffMb: 1);
        var cache = CreateCache(options);

        // Add small file to memory tier
        using (var h = await cache.BorrowAsync("small", TimeSpan.FromMinutes(5),
            sizeHintBytes: 100, CreateFactory(100))) { }

        // Add large file to disk tier
        using (var h = await cache.BorrowAsync("large", TimeSpan.FromMinutes(5),
            sizeHintBytes: 2 * 1024 * 1024, CreateFactory(2 * 1024 * 1024))) { }

        cache.EntryCount.Should().Be(2);
        cache.CurrentSizeBytes.Should().BeGreaterThan(0);
        cache.CapacityBytes.Should().Be(
            options.MemoryCacheSizeBytes + options.DiskCacheSizeBytes);
        cache.BorrowedEntryCount.Should().Be(0); // Both disposed
    }

    [Fact]
    public async Task EvictExpired_DelegatesBothTiers()
    {
        var cache = CreateCache(DefaultOptions(cutoffMb: 1));

        // Add an entry with very short TTL
        using (var h = await cache.BorrowAsync("ephemeral", TimeSpan.FromMilliseconds(1),
            sizeHintBytes: 100, CreateFactory(100))) { }

        // Wait for expiry
        await Task.Delay(50);

        cache.EvictExpired();

        cache.MemoryTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task WithoutSizeHint_DefaultsToMemoryTier()
    {
        var cache = CreateCache(DefaultOptions(cutoffMb: 1));

        // Use the ICache<Stream> interface (no size hint)
        ICache<Stream> cacheInterface = cache;
        using var handle = await cacheInterface.BorrowAsync("no-hint", TimeSpan.FromMinutes(5),
            CreateFactory(256));

        cache.MemoryTier.EntryCount.Should().Be(1);
        cache.DiskTier.EntryCount.Should().Be(0);
    }
}
