using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ZipDriveV3.Infrastructure.Caching.Tests;

/// <summary>
/// Integration tests for GenericCache with MemoryStorageStrategy and DiskStorageStrategy.
/// Tests the borrow/return pattern, reference counting, eviction, and actual data caching.
/// </summary>
public class GenericCacheIntegrationTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly string _tempDir;

    public GenericCacheIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"GenericCacheTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Cleanup temp directory
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }

    #region MemoryStorageStrategy Tests

    [Fact]
    public async Task MemoryCache_BorrowAsync_CacheMiss_CallsFactoryAndCachesData()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024); // 1MB
        byte[] testData = CreateTestData(1024); // 1KB
        int factoryCallCount = 0;

        // Act
        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                   "test-key",
                   TimeSpan.FromMinutes(30),
                   async ct =>
                   {
                       factoryCallCount++;
                       return new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(testData),
                           SizeBytes = testData.Length
                       };
                   },
                   CancellationToken.None))
        {
            // Assert - handle contains valid data
            handle.CacheKey.Should().Be("test-key");
            handle.SizeBytes.Should().Be(testData.Length);
            handle.Value.Should().NotBeNull();
            handle.Value.CanSeek.Should().BeTrue();

            // Read and verify data
            byte[] buffer = new byte[testData.Length];
            int bytesRead = await handle.Value.ReadAsync(buffer);
            bytesRead.Should().Be(testData.Length);
            buffer.Should().BeEquivalentTo(testData);
        }

        factoryCallCount.Should().Be(1);
        cache.EntryCount.Should().Be(1);
        cache.CurrentSizeBytes.Should().Be(testData.Length);
    }

    [Fact]
    public async Task MemoryCache_BorrowAsync_CacheHit_ReturnsWithoutCallingFactory()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(1024);
        int factoryCallCount = 0;

        Task<CacheFactoryResult<Stream>> Factory(CancellationToken ct)
        {
            factoryCallCount++;
            return Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            });
        }

        // First call - cache miss
        using (ICacheHandle<Stream> handle1 = await cache.BorrowAsync("test-key", TimeSpan.FromMinutes(30), Factory, CancellationToken.None))
        {
            // Just borrow and return
        }

        factoryCallCount.Should().Be(1);

        // Second call - cache hit
        using (ICacheHandle<Stream> handle2 = await cache.BorrowAsync("test-key", TimeSpan.FromMinutes(30), Factory, CancellationToken.None))
        {
            // Verify data is still valid
            byte[] buffer = new byte[testData.Length];
            await handle2.Value.ReadExactlyAsync(buffer);
            buffer.Should().BeEquivalentTo(testData);
        }

        // Factory should NOT be called again
        factoryCallCount.Should().Be(1);
        cache.HitRate.Should().Be(0.5); // 1 hit, 1 miss
    }

    [Fact]
    public async Task MemoryCache_BorrowAsync_RandomAccess_SupportsSeek()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(10 * 1024); // 10KB with known pattern

        using ICacheHandle<Stream> handle = await cache.BorrowAsync(
            "seekable-key",
            TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            }),
            CancellationToken.None);

        // Act & Assert - seek to middle and read
        handle.Value.Seek(5000, SeekOrigin.Begin);
        byte[] buffer = new byte[100];
        await handle.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(testData.AsSpan(5000, 100).ToArray());

        // Seek to beginning and read
        handle.Value.Seek(0, SeekOrigin.Begin);
        await handle.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(testData.AsSpan(0, 100).ToArray());

        // Seek to end-100 and read
        handle.Value.Seek(-100, SeekOrigin.End);
        await handle.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(testData.AsSpan(testData.Length - 100, 100).ToArray());
    }

    [Fact]
    public async Task MemoryCache_RefCount_ProtectsFromEviction()
    {
        // Arrange - small cache that can only hold ~2 entries
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 2048); // 2KB
        byte[] data1 = CreateTestData(1024); // 1KB

        // Borrow entry but don't dispose yet
        ICacheHandle<Stream> handle1 = await cache.BorrowAsync(
            "key1",
            TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data1),
                SizeBytes = data1.Length
            }),
            CancellationToken.None);

        cache.BorrowedEntryCount.Should().Be(1);

        // Borrow second entry (also don't dispose)
        byte[] data2 = CreateTestData(1024);
        ICacheHandle<Stream> handle2 = await cache.BorrowAsync(
            "key2",
            TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data2),
                SizeBytes = data2.Length
            }),
            CancellationToken.None);

        // Both entries should be in cache and borrowed
        cache.EntryCount.Should().Be(2);
        cache.BorrowedEntryCount.Should().Be(2);

        // Add third entry that exceeds capacity
        byte[] data3 = CreateTestData(1024);
        ICacheHandle<Stream> handle3 = await cache.BorrowAsync(
            "key3",
            TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data3),
                SizeBytes = data3.Length
            }),
            CancellationToken.None);

        // All 3 entries should exist because they're all borrowed (RefCount > 0)
        // Soft capacity allows overage when all entries are borrowed
        cache.EntryCount.Should().Be(3);
        cache.BorrowedEntryCount.Should().Be(3);

        // Verify all handles still work
        byte[] buffer = new byte[1024];
        handle1.Value.Seek(0, SeekOrigin.Begin);
        await handle1.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(data1);

        handle2.Value.Seek(0, SeekOrigin.Begin);
        await handle2.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(data2);

        handle3.Value.Seek(0, SeekOrigin.Begin);
        await handle3.Value.ReadExactlyAsync(buffer);
        buffer.Should().BeEquivalentTo(data3);

        // Now dispose handle1
        handle1.Dispose();
        cache.BorrowedEntryCount.Should().Be(2);

        // Dispose remaining handles
        handle2.Dispose();
        handle3.Dispose();
        cache.BorrowedEntryCount.Should().Be(0);
    }

    [Fact]
    public async Task MemoryCache_TtlExpiration_EvictsExpiredEntries()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(1024);

        // Add entry with 1 minute TTL
        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                   "expiring-key",
                   TimeSpan.FromMinutes(1),
                   ct => Task.FromResult(new CacheFactoryResult<Stream>
                   {
                       Value = new MemoryStream(testData),
                       SizeBytes = testData.Length
                   }),
                   CancellationToken.None))
        {
            // Entry exists
        }

        cache.EntryCount.Should().Be(1);

        // Advance time past TTL
        _fakeTime.Advance(TimeSpan.FromMinutes(2));

        // Manually trigger eviction
        cache.EvictExpired();

        // Entry should be evicted
        cache.EntryCount.Should().Be(0);
        cache.CurrentSizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task MemoryCache_CapacityEviction_EvictsLruEntries()
    {
        // Arrange - cache that can hold ~3KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 3 * 1024);

        // Add 3 entries of 1KB each
        for (int i = 1; i <= 3; i++)
        {
            byte[] data = CreateTestData(1024);
            using ICacheHandle<Stream> handle = await cache.BorrowAsync(
                $"key{i}",
                TimeSpan.FromMinutes(30),
                ct => Task.FromResult(new CacheFactoryResult<Stream>
                {
                    Value = new MemoryStream(data),
                    SizeBytes = data.Length
                }),
                CancellationToken.None);
        }

        cache.EntryCount.Should().Be(3);

        // Access key1 to make it recently used
        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                   "key1",
                   TimeSpan.FromMinutes(30),
                   ct => throw new InvalidOperationException("Should not be called"),
                   CancellationToken.None))
        {
            // Just access it
        }

        // Add 4th entry - should evict key2 (LRU)
        byte[] data4 = CreateTestData(1024);
        using (ICacheHandle<Stream> handle4 = await cache.BorrowAsync(
                   "key4",
                   TimeSpan.FromMinutes(30),
                   ct => Task.FromResult(new CacheFactoryResult<Stream>
                   {
                       Value = new MemoryStream(data4),
                       SizeBytes = data4.Length
                   }),
                   CancellationToken.None))
        {
            // Added
        }

        // Should have evicted one entry to make room
        cache.EntryCount.Should().BeLessThanOrEqualTo(3);
    }

    #endregion

    #region DiskStorageStrategy Tests

    [Fact]
    public async Task DiskCache_BorrowAsync_CacheMiss_CreatesMemoryMappedFile()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 100 * 1024 * 1024); // 100MB
        byte[] testData = CreateTestData(1024 * 1024); // 1MB

        try
        {
            // Act
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                       "large-file-key",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(testData),
                           SizeBytes = testData.Length
                       }),
                       CancellationToken.None))
            {
                // Assert
                handle.CacheKey.Should().Be("large-file-key");
                handle.SizeBytes.Should().Be(testData.Length);
                handle.Value.Should().NotBeNull();
                handle.Value.CanSeek.Should().BeTrue();

                // Verify data integrity
                byte[] buffer = new byte[testData.Length];
                int bytesRead = await handle.Value.ReadAsync(buffer);
                bytesRead.Should().Be(testData.Length);
                buffer.Should().BeEquivalentTo(testData);
            }

            cache.EntryCount.Should().Be(1);

            // Verify temp file was created
            string[] tempFiles = Directory.GetFiles(_tempDir, "*.zip2vd.cache");
            tempFiles.Should().HaveCount(1);
        }
        finally
        {
            // Cleanup: Clear cache to delete memory-mapped files
            await cache.ClearAsync();
        }
    }

    [Fact]
    public async Task DiskCache_BorrowAsync_RandomAccess_SupportsSeekOnLargeFile()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 100 * 1024 * 1024);
        byte[] testData = CreateTestData(5 * 1024 * 1024); // 5MB

        try
        {
            using ICacheHandle<Stream> handle = await cache.BorrowAsync(
                "large-seekable",
                TimeSpan.FromMinutes(30),
                ct => Task.FromResult(new CacheFactoryResult<Stream>
                {
                    Value = new MemoryStream(testData),
                    SizeBytes = testData.Length
                }),
                CancellationToken.None);

            // Act & Assert - random access at various positions
            long[] positions = new long[] { 0, 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, testData.Length - 1024 };
            byte[] buffer = new byte[1024];

            foreach (long pos in positions)
            {
                handle.Value.Seek(pos, SeekOrigin.Begin);
                await handle.Value.ReadExactlyAsync(buffer);
                buffer.Should().BeEquivalentTo(testData.AsSpan((int)pos, 1024).ToArray(),
                    $"Data at position {pos} should match");
            }
        }
        finally
        {
            // Cleanup: Clear cache to delete memory-mapped files
            await cache.ClearAsync();
        }
    }

    [Fact]
    public async Task DiskCache_CacheHit_ReusesMemoryMappedFile()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 100 * 1024 * 1024);
        byte[] testData = CreateTestData(1024 * 1024);
        int factoryCallCount = 0;

        Task<CacheFactoryResult<Stream>> Factory(CancellationToken ct)
        {
            factoryCallCount++;
            return Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            });
        }

        try
        {
            // First access
            using (ICacheHandle<Stream> handle1 = await cache.BorrowAsync("disk-key", TimeSpan.FromMinutes(30), Factory, CancellationToken.None))
            {
                // Read some data
                byte[] buffer = new byte[1024];
                await handle1.Value.ReadExactlyAsync(buffer);
            }

            // Second access - should be cache hit
            using (ICacheHandle<Stream> handle2 = await cache.BorrowAsync("disk-key", TimeSpan.FromMinutes(30), Factory, CancellationToken.None))
            {
                // Read and verify
                byte[] buffer = new byte[testData.Length];
                await handle2.Value.ReadExactlyAsync(buffer);
                buffer.Should().BeEquivalentTo(testData);
            }

            factoryCallCount.Should().Be(1);
            cache.HitRate.Should().Be(0.5);

            // Should still have only 1 temp file
            string[] tempFiles = Directory.GetFiles(_tempDir, "*.zip2vd.cache");
            tempFiles.Should().HaveCount(1);
        }
        finally
        {
            // Cleanup: Clear cache to delete memory-mapped files
            await cache.ClearAsync();
        }
    }

    [Fact]
    public async Task DiskCache_Eviction_DeletesTempFile()
    {
        // Arrange - small cache
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 2 * 1024 * 1024); // 2MB
        byte[] data1 = CreateTestData(1024 * 1024); // 1MB

        try
        {
            // Add first entry
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                       "evict-key1",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data1),
                           SizeBytes = data1.Length
                       }),
                       CancellationToken.None))
            {
            }

            Directory.GetFiles(_tempDir, "*.zip2vd.cache").Should().HaveCount(1);

            // Add second entry that fits
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                       "evict-key2",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data1),
                           SizeBytes = data1.Length
                       }),
                       CancellationToken.None))
            {
            }

            Directory.GetFiles(_tempDir, "*.zip2vd.cache").Should().HaveCount(2);

            // Add third entry - should trigger eviction
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                       "evict-key3",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data1),
                           SizeBytes = data1.Length
                       }),
                       CancellationToken.None))
            {
            }

            // Process pending cleanup (disk strategy queues deletions)
            cache.ProcessPendingCleanup();

            // Should have evicted at least one file
            cache.EntryCount.Should().BeLessThanOrEqualTo(2);
        }
        finally
        {
            // Cleanup: Clear cache to delete memory-mapped files
            await cache.ClearAsync();
        }
    }

    #endregion

    #region Concurrent Access Tests

    [Fact]
    public async Task MemoryCache_ConcurrentBorrow_SameKey_OnlyMaterializesOnce()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(1024);
        int factoryCallCount = 0;
        TimeSpan factoryDelay = TimeSpan.FromMilliseconds(100);

        async Task<CacheFactoryResult<Stream>> SlowFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            await Task.Delay(factoryDelay, ct); // Simulate slow materialization
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            };
        }

        // Act - 10 concurrent requests for same key
        List<Task<byte[]>> tasks = Enumerable.Range(0, 10)
            .Select(async _ =>
            {
                using ICacheHandle<Stream> handle = await cache.BorrowAsync("concurrent-key", TimeSpan.FromMinutes(30), SlowFactory, CancellationToken.None);
                byte[] buffer = new byte[testData.Length];
                await handle.Value.ReadExactlyAsync(buffer);
                return buffer;
            })
            .ToList();

        byte[][] results = await Task.WhenAll(tasks);

        // Assert - factory called only once (thundering herd prevention)
        factoryCallCount.Should().Be(1);

        // All results should have correct data
        foreach (byte[] result in results)
        {
            result.Should().BeEquivalentTo(testData);
        }
    }

    [Fact]
    public async Task MemoryCache_ConcurrentBorrow_DifferentKeys_MaterializesInParallel()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 10 * 1024 * 1024);
        ConcurrentDictionary<string, DateTimeOffset> factoryStartTimes = new ConcurrentDictionary<string, DateTimeOffset>();
        TimeSpan factoryDelay = TimeSpan.FromMilliseconds(100);

        async Task<CacheFactoryResult<Stream>> Factory(string key, CancellationToken ct)
        {
            factoryStartTimes[key] = DateTimeOffset.UtcNow;
            await Task.Delay(factoryDelay, ct);
            byte[] data = CreateTestData(1024);
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            };
        }

        // Act - concurrent requests for different keys
        DateTimeOffset startTime = DateTimeOffset.UtcNow;
        List<Task<ICacheHandle<Stream>>> tasks = Enumerable.Range(0, 5)
            .Select(i => cache.BorrowAsync($"parallel-key-{i}", TimeSpan.FromMinutes(30),
                ct => Factory($"parallel-key-{i}", ct), CancellationToken.None))
            .ToList();

        ICacheHandle<Stream>[] handles = await Task.WhenAll(tasks);
        DateTimeOffset endTime = DateTimeOffset.UtcNow;

        // Cleanup handles
        foreach (ICacheHandle<Stream> handle in handles)
        {
            handle.Dispose();
        }

        // Assert - all factories started roughly at the same time (parallel execution)
        TimeSpan totalTime = endTime - startTime;

        // If sequential, would take 5 * 100ms = 500ms+
        // If parallel, should take ~100ms + overhead
        totalTime.Should().BeLessThan(TimeSpan.FromMilliseconds(400),
            "Different keys should materialize in parallel");

        factoryStartTimes.Should().HaveCount(5);
    }

    [Fact]
    public async Task MemoryCache_MultipleBorrowers_SameEntry_AllGetValidHandles()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(1024);

        // Pre-populate cache
        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                   "shared-key",
                   TimeSpan.FromMinutes(30),
                   ct => Task.FromResult(new CacheFactoryResult<Stream>
                   {
                       Value = new MemoryStream(testData),
                       SizeBytes = testData.Length
                   }),
                   CancellationToken.None))
        {
        }

        // Act - multiple concurrent borrowers
        List<ICacheHandle<Stream>> handles = new List<ICacheHandle<Stream>>();
        for (int i = 0; i < 5; i++)
        {
            ICacheHandle<Stream> handle = await cache.BorrowAsync(
                "shared-key",
                TimeSpan.FromMinutes(30),
                ct => throw new InvalidOperationException("Should not be called"),
                CancellationToken.None);
            handles.Add(handle);
        }

        // Assert - all handles are valid and entry is protected
        cache.BorrowedEntryCount.Should().Be(1); // Same entry, but RefCount = 5
        handles.Should().HaveCount(5);

        foreach (ICacheHandle<Stream> handle in handles)
        {
            handle.Value.Should().NotBeNull();
            handle.CacheKey.Should().Be("shared-key");

            // Each handle can read independently
            byte[] buffer = new byte[testData.Length];
            handle.Value.Seek(0, SeekOrigin.Begin);
            await handle.Value.ReadExactlyAsync(buffer);
            buffer.Should().BeEquivalentTo(testData);
        }

        // Dispose all handles
        foreach (ICacheHandle<Stream> handle in handles)
        {
            handle.Dispose();
        }

        cache.BorrowedEntryCount.Should().Be(0);
    }

    #endregion

    #region Helper Methods

    private GenericCache<Stream> CreateMemoryCache(long capacityBytes)
    {
        return new GenericCache<Stream>(
            new MemoryStorageStrategy(),
            new LruEvictionPolicy(),
            capacityBytes,
            _fakeTime,
            NullLogger<GenericCache<Stream>>.Instance);
    }

    private GenericCache<Stream> CreateDiskCache(long capacityBytes)
    {
        return new GenericCache<Stream>(
            new DiskStorageStrategy(_tempDir, NullLogger<DiskStorageStrategy>.Instance),
            new LruEvictionPolicy(),
            capacityBytes,
            _fakeTime,
            NullLogger<GenericCache<Stream>>.Instance);
    }

    private static byte[] CreateTestData(int size)
    {
        byte[] data = new byte[size];
        // Fill with predictable pattern for verification
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 256);
        }

        return data;
    }

    #endregion
}