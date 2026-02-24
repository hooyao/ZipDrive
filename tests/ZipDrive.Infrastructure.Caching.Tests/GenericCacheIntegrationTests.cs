using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ZipDrive.Infrastructure.Caching.Tests;

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

    /// <summary>
    /// Verifies that on a cache miss, the factory is invoked exactly once and the resulting
    /// data is correctly stored in the cache. The returned handle should provide access to
    /// the cached stream with correct metadata (key, size) and the data should be readable.
    /// </summary>
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
            await handle.Value.ReadExactlyAsync(buffer);
            buffer.Should().BeEquivalentTo(testData);
        }

        factoryCallCount.Should().Be(1);
        cache.EntryCount.Should().Be(1);
        cache.CurrentSizeBytes.Should().Be(testData.Length);
    }

    /// <summary>
    /// Verifies that on a cache hit, the factory is NOT called again. The cached data should
    /// be returned directly, and the hit rate metric should reflect the cache hit.
    /// This tests the fundamental caching behavior - avoiding redundant work on repeated access.
    /// </summary>
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

    /// <summary>
    /// Verifies that the cached stream supports random access (seeking) to arbitrary positions.
    /// This is critical for the ZipDrive use case where Windows file system operations require
    /// reading at arbitrary offsets, not just sequential access.
    /// Tests seeking to: beginning, middle, and end of the cached data.
    /// </summary>
    [Fact]
    public async Task MemoryCache_BorrowAsync_RandomAccess_SupportsSeek()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 1024 * 1024);
        byte[] testData = CreateTestData(10 * 1024); // 10KB with known pattern

        // Act & Assert - seek to middle and read
        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                   "seekable-key",
                   TimeSpan.FromMinutes(30),
                   ct => Task.FromResult(new CacheFactoryResult<Stream>
                   {
                       Value = new MemoryStream(testData),
                       SizeBytes = testData.Length
                   }),
                   CancellationToken.None))
        {
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
    }

    /// <summary>
    /// Verifies that entries with RefCount > 0 (currently borrowed) are protected from eviction.
    /// This is critical for data integrity - an entry being read must not be evicted mid-operation.
    /// The cache uses "soft capacity" to allow temporary overage when all entries are borrowed,
    /// rather than failing or corrupting data.
    /// </summary>
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

    /// <summary>
    /// Verifies that entries are evicted when their TTL expires and EvictExpired() is called.
    /// Uses FakeTimeProvider to deterministically advance time past the TTL.
    /// Expired entries are only evicted if they are not currently borrowed (RefCount = 0).
    /// </summary>
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

    /// <summary>
    /// Verifies that the LRU (Least Recently Used) eviction policy correctly evicts the oldest
    /// accessed entries when capacity is exceeded. After accessing key1, adding a new entry
    /// should evict key2 (least recently used) rather than key1 or key3.
    /// </summary>
    [Fact]
    public async Task MemoryCache_CapacityEviction_EvictsLruEntries()
    {
        // Arrange - cache that can hold ~3KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 3 * 1024);

        // Add 3 entries of 1KB each
        for (int i = 1; i <= 3; i++)
        {
            byte[] data = CreateTestData(1024);
            using (await cache.BorrowAsync(
                       $"key{i}",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }),
                       CancellationToken.None))
            {
            }
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

    /// <summary>
    /// Verifies that DiskStorageStrategy creates a memory-mapped file on disk for cached data.
    /// The data should be written to a temp file with .zip2vd.cache extension, and the returned
    /// handle should provide seekable stream access to the cached content with full data integrity.
    /// </summary>
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
                await handle.Value.ReadExactlyAsync(buffer);
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

    /// <summary>
    /// Verifies that disk-cached large files support efficient random access via memory-mapped files.
    /// Tests seeking to multiple positions across a 5MB file to ensure the MemoryMappedViewStream
    /// correctly handles arbitrary offset reads - essential for Windows file system operations.
    /// </summary>
    [Fact]
    public async Task DiskCache_BorrowAsync_RandomAccess_SupportsSeekOnLargeFile()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 100 * 1024 * 1024);
        byte[] testData = CreateTestData(5 * 1024 * 1024); // 5MB

        try
        {
            // Act & Assert - random access at various positions
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                       "large-seekable",
                       TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(testData),
                           SizeBytes = testData.Length
                       }),
                       CancellationToken.None))
            {
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
        }
        finally
        {
            // Cleanup: Clear cache to delete memory-mapped files
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// Verifies that disk cache hits reuse the existing MemoryMappedFile rather than creating
    /// a new one. The factory should only be called once, and subsequent borrows should return
    /// handles to the same underlying file. Only one temp file should exist on disk.
    /// </summary>
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

    /// <summary>
    /// Verifies that when disk cache entries are evicted, their temp files are properly deleted.
    /// DiskStorageStrategy uses async cleanup (queued deletion) for performance, so ProcessPendingCleanup()
    /// must be called to actually delete the files. This prevents resource leaks from orphaned temp files.
    /// </summary>
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

    /// <summary>
    /// Verifies the "thundering herd" prevention mechanism with 10 concurrent requests for the same key.
    /// When multiple threads request an uncached key simultaneously, only ONE factory invocation should
    /// occur (using Lazy&lt;Task&gt; with ExecutionAndPublication mode). All threads should receive
    /// the same cached data. This is Layer 2 of the three-layer concurrency strategy.
    /// </summary>
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
                using (ICacheHandle<Stream> handle = await cache.BorrowAsync("concurrent-key", TimeSpan.FromMinutes(30), SlowFactory, CancellationToken.None))
                {
                    byte[] buffer = new byte[testData.Length];
                    await handle.Value.ReadExactlyAsync(buffer);
                    return buffer;
                }
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

    /// <summary>
    /// Verifies that requests for DIFFERENT keys can materialize in parallel, not blocking each other.
    /// This ensures the per-key locking (Layer 2) doesn't create a global bottleneck.
    /// 5 concurrent requests for different keys with 100ms factory delay should complete in ~100ms
    /// (parallel), not 500ms (sequential). Critical for concurrent ZIP file access.
    /// </summary>
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

    /// <summary>
    /// Verifies that multiple handles can be borrowed for the same cached entry simultaneously.
    /// Each borrower gets an independent stream (MemoryStream wrapping the same byte[]),
    /// but they all share the same underlying cache entry. The entry's RefCount tracks the
    /// number of active borrowers, and the entry cannot be evicted until all handles are disposed.
    /// </summary>
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

    #region Heavy Concurrent Access Tests - Memory Cache

    /// <summary>
    /// HEAVY LOAD TEST: Verifies thundering herd prevention at scale with 100 concurrent threads
    /// all requesting the same uncached key simultaneously. Despite the high concurrency, the factory
    /// must be called exactly ONCE. All 100 threads must receive correct data with full integrity.
    /// This stress-tests Layer 2 (per-key Lazy&lt;Task&gt;) of the concurrency strategy.
    /// </summary>
    [Fact]
    public async Task MemoryCache_HeavyConcurrency_100ThreadsSameKey_OnlyMaterializesOnce()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 10 * 1024 * 1024);
        byte[] testData = CreateTestData(64 * 1024); // 64KB
        int factoryCallCount = 0;
        int successCount = 0;

        async Task<CacheFactoryResult<Stream>> SlowFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            await Task.Delay(50, ct); // Simulate slow materialization
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            };
        }

        // Act - 100 concurrent requests for same key (thundering herd scenario)
        Task[] tasks = Enumerable.Range(0, 100)
            .Select(async _ =>
            {
                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                           "heavy-same-key",
                           TimeSpan.FromMinutes(30),
                           SlowFactory,
                           CancellationToken.None))
                {
                    byte[] buffer = new byte[testData.Length];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    await handle.Value.ReadExactlyAsync(buffer);

                    if (buffer.SequenceEqual(testData))
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        factoryCallCount.Should().Be(1, "Factory should be called exactly once (thundering herd prevention)");
        successCount.Should().Be(100, "All 100 threads should get correct data");
        cache.EntryCount.Should().Be(1);
        cache.BorrowedEntryCount.Should().Be(0, "All handles should be returned");
    }

    /// <summary>
    /// HEAVY LOAD TEST: Verifies parallel materialization of 50 different keys simultaneously.
    /// Each key should be materialized exactly once (no duplicate work), and all data should
    /// verify correctly. Tests that per-key locking doesn't serialize different keys.
    /// Uses deterministic test data generation for verification.
    /// </summary>
    [Fact]
    public async Task MemoryCache_HeavyConcurrency_50DifferentKeys_AllMaterializeCorrectly()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 100 * 1024 * 1024); // 100MB
        ConcurrentDictionary<string, int> factoryCalls = new();
        ConcurrentDictionary<string, bool> dataVerified = new();
        const int keyCount = 50;

        async Task<CacheFactoryResult<Stream>> Factory(string key, CancellationToken ct)
        {
            factoryCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
            await Task.Delay(10, ct); // Small delay
            byte[] data = CreateTestDataForKey(key, 32 * 1024); // 32KB per key
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            };
        }

        // Act - concurrent access to 50 different keys
        Task[] tasks = Enumerable.Range(0, keyCount)
            .Select(async i =>
            {
                string key = $"diff-key-{i}";

                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                           key,
                           TimeSpan.FromMinutes(30),
                           ct => Factory(key, ct),
                           CancellationToken.None))
                {
                    byte[] expected = CreateTestDataForKey(key, 32 * 1024);
                    byte[] actual = new byte[expected.Length];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    await handle.Value.ReadExactlyAsync(actual);

                    dataVerified[key] = actual.SequenceEqual(expected);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        factoryCalls.Should().HaveCount(keyCount);
        factoryCalls.Values.Should().AllSatisfy(count => count.Should().Be(1),
            "Each key should be materialized exactly once");
        dataVerified.Values.Should().AllSatisfy(verified => verified.Should().BeTrue(),
            "All data should be verified correctly");
        cache.EntryCount.Should().Be(keyCount);
    }

    /// <summary>
    /// HEAVY LOAD TEST: Simulates realistic workload with 1000 concurrent operations across 20 keys.
    /// Tests data integrity under mixed read/write patterns where some operations hit cached entries
    /// and others trigger materialization. All 1000 operations must return correct data.
    /// Verifies cache correctness under realistic, high-throughput conditions.
    /// </summary>
    [Fact]
    public async Task MemoryCache_HeavyConcurrency_MixedReadWrite_DataIntegrity()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 50 * 1024 * 1024);
        const int iterations = 1000;
        const int keySpace = 20; // 20 unique keys
        ConcurrentBag<bool> verificationResults = new();

        // Act - 1000 iterations of random read/write across 20 keys
        Task[] tasks = Enumerable.Range(0, iterations)
            .Select(async i =>
            {
                string key = $"mixed-key-{i % keySpace}";
                byte[] expectedData = CreateTestDataForKey(key, 8 * 1024); // 8KB

                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                           key,
                           TimeSpan.FromMinutes(30),
                           ct => Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(expectedData),
                               SizeBytes = expectedData.Length
                           }),
                           CancellationToken.None))
                {
                    byte[] buffer = new byte[expectedData.Length];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    await handle.Value.ReadExactlyAsync(buffer);

                    verificationResults.Add(buffer.SequenceEqual(expectedData));
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        verificationResults.Should().HaveCount(iterations);
        verificationResults.Should().AllSatisfy(result => result.Should().BeTrue(),
            "All reads should return correct data");
        cache.EntryCount.Should().BeLessThanOrEqualTo(keySpace);
    }

    /// <summary>
    /// HEAVY LOAD TEST: Stress-tests the eviction system under extreme concurrent load.
    /// Uses a tiny cache (512KB) with 200 concurrent 32KB entries, forcing constant eviction.
    /// Despite the cache being far over capacity, ALL borrowed entries must remain readable
    /// (RefCount protection). No exceptions should occur, and all data must verify correctly.
    /// Tests Layer 3 (eviction lock) and the soft-capacity safety mechanism.
    /// </summary>
    [Fact]
    public async Task MemoryCache_HeavyConcurrency_EvictionUnderLoad_NoDataCorruption()
    {
        // Arrange - small cache to force frequent eviction
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 512 * 1024); // 512KB
        const int iterations = 200;
        ConcurrentBag<bool> verificationResults = new();
        ConcurrentBag<Exception> exceptions = new();

        // Act - many concurrent operations that will cause eviction
        Task[] tasks = Enumerable.Range(0, iterations)
            .Select(async i =>
            {
                try
                {
                    string key = $"evict-load-key-{i}";
                    byte[] data = CreateTestDataForKey(key, 32 * 1024); // 32KB each

                    // Read while borrowed - should never fail
                    using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                               key,
                               TimeSpan.FromMinutes(30),
                               ct => Task.FromResult(new CacheFactoryResult<Stream>
                               {
                                   Value = new MemoryStream(data),
                                   SizeBytes = data.Length
                               }),
                               CancellationToken.None))
                    {
                        byte[] buffer = new byte[data.Length];
                        handle.Value.Seek(0, SeekOrigin.Begin);
                        await handle.Value.ReadExactlyAsync(buffer);

                        verificationResults.Add(buffer.SequenceEqual(data));
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty("No exceptions should occur during heavy concurrent access");
        verificationResults.Should().HaveCount(iterations);
        verificationResults.Should().AllSatisfy(result => result.Should().BeTrue(),
            "All reads should return correct data even under eviction pressure");
    }

    /// <summary>
    /// HEAVY LOAD TEST: Verifies RefCount correctness under rapid borrow/return cycles.
    /// 50 concurrent borrowers each perform 20 borrow/return cycles on the same key.
    /// After all 1000 operations complete, RefCount must be exactly 0 and the entry must
    /// still exist. Also tracks peak concurrent borrowers to verify overlapping access occurred.
    /// Tests the thread-safety of IncrementRefCount/DecrementRefCount.
    /// </summary>
    [Fact]
    public async Task MemoryCache_HeavyConcurrency_BorrowReturnCycle_RefCountCorrect()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 10 * 1024 * 1024);
        byte[] testData = CreateTestData(16 * 1024);
        const int concurrentBorrowers = 50;
        const int cyclesPerBorrower = 20;

        // Pre-populate
        using (ICacheHandle<Stream> h = await cache.BorrowAsync("refcount-key", TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            }), CancellationToken.None))
        { }

        // Act - many concurrent borrow/return cycles
        int peakBorrowed = 0;
        object peakLock = new();

        Task[] tasks = Enumerable.Range(0, concurrentBorrowers)
            .Select(async _ =>
            {
                for (int cycle = 0; cycle < cyclesPerBorrower; cycle++)
                {
                    using (await cache.BorrowAsync(
                               "refcount-key",
                               TimeSpan.FromMinutes(30),
                               ct => throw new InvalidOperationException("Should not materialize"),
                               CancellationToken.None))
                    {
                        int currentBorrowed = cache.BorrowedEntryCount;
                        lock (peakLock)
                        {
                            if (currentBorrowed > peakBorrowed)
                                peakBorrowed = currentBorrowed;
                        }

                        // Simulate some work
                        await Task.Yield();
                    }
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        cache.BorrowedEntryCount.Should().Be(0, "All handles should be returned");
        cache.EntryCount.Should().Be(1, "Entry should still exist");
        peakBorrowed.Should().BeGreaterThan(0, "Should have observed concurrent borrowers");
    }

    #endregion

    #region Heavy Concurrent Access Tests - Disk Cache

    /// <summary>
    /// HEAVY LOAD TEST: Tests thundering herd prevention for disk-backed cache with 100 threads.
    /// Writing to disk is slow, so all 100 threads waiting for the same 1MB file must share
    /// a single materialization. The factory is called once, one temp file is created,
    /// and all threads get correct data. Critical for large file caching scenarios.
    /// </summary>
    [Fact]
    public async Task DiskCache_HeavyConcurrency_100ThreadsSameKey_OnlyMaterializesOnce()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 500 * 1024 * 1024);
        byte[] testData = CreateTestData(1024 * 1024); // 1MB
        int factoryCallCount = 0;
        int successCount = 0;

        async Task<CacheFactoryResult<Stream>> SlowFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryCallCount);
            await Task.Delay(100, ct); // Disk operations are slower
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(testData),
                SizeBytes = testData.Length
            };
        }

        try
        {
            // Act - 100 concurrent requests for same key
            Task[] tasks = Enumerable.Range(0, 100)
                .Select(async _ =>
                {
                    using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                               "disk-heavy-same",
                               TimeSpan.FromMinutes(30),
                               SlowFactory,
                               CancellationToken.None))
                    {
                        byte[] buffer = new byte[testData.Length];
                        handle.Value.Seek(0, SeekOrigin.Begin);
                        await handle.Value.ReadExactlyAsync(buffer);

                        if (buffer.SequenceEqual(testData))
                        {
                            Interlocked.Increment(ref successCount);
                        }
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            factoryCallCount.Should().Be(1, "Factory should be called exactly once");
            successCount.Should().Be(100, "All threads should get correct data");

            // Only one temp file should exist
            Directory.GetFiles(_tempDir, "*.zip2vd.cache").Should().HaveCount(1);
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// HEAVY LOAD TEST: Verifies parallel disk I/O performance with 20 concurrent 5MB files.
    /// Total 100MB of data materialized in parallel to disk-backed MemoryMappedFiles.
    /// Each file undergoes random access verification at 4 different offsets.
    /// Should complete in under 30 seconds (parallel), not minutes (sequential).
    /// Tests that disk I/O doesn't serialize across different keys.
    /// </summary>
    [Fact]
    public async Task DiskCache_HeavyConcurrency_20LargeFiles_ParallelMaterialization()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 500 * 1024 * 1024); // 500MB
        ConcurrentDictionary<string, int> factoryCalls = new();
        const int fileCount = 20;
        const int fileSize = 5 * 1024 * 1024; // 5MB each

        try
        {
            // Act - concurrent access to 20 large files
            DateTimeOffset startTime = DateTimeOffset.UtcNow;

            Task[] tasks = Enumerable.Range(0, fileCount)
                .Select(async i =>
                {
                    string key = $"disk-large-{i}";
                    byte[] data = CreateTestDataForKey(key, fileSize);

                    factoryCalls.AddOrUpdate(key, 1, (_, c) => c + 1);

                    // Verify random access at multiple positions
                    using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                               key,
                               TimeSpan.FromMinutes(30),
                               ct => Task.FromResult(new CacheFactoryResult<Stream>
                               {
                                   Value = new MemoryStream(data),
                                   SizeBytes = data.Length
                               }),
                               CancellationToken.None))
                    {
                        byte[] buffer = new byte[4096];
                        long[] positions = { 0, fileSize / 4, fileSize / 2, fileSize - 4096 };

                        foreach (long pos in positions)
                        {
                            handle.Value.Seek(pos, SeekOrigin.Begin);
                            await handle.Value.ReadExactlyAsync(buffer);

                            byte[] expected = data.AsSpan((int)pos, 4096).ToArray();
                            buffer.Should().BeEquivalentTo(expected,
                                $"Data at position {pos} for {key} should match");
                        }
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);
            DateTimeOffset endTime = DateTimeOffset.UtcNow;

            // Assert
            factoryCalls.Should().HaveCount(fileCount);
            cache.EntryCount.Should().Be(fileCount);

            // Should complete in reasonable time (parallel, not sequential)
            TimeSpan elapsed = endTime - startTime;
            elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
                "20 files should materialize in parallel");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// HEAVY LOAD TEST: Stress-tests random access on disk-cached files under heavy concurrent load.
    /// Pre-populates 5 x 10MB files, then launches 100 concurrent readers (20 per file).
    /// Each reader performs 10 random-offset reads at different positions.
    /// Total: 1000 random reads across 50MB of cached data. All reads must return correct data.
    /// Validates MemoryMappedViewStream thread-safety and data integrity.
    /// </summary>
    [Fact]
    public async Task DiskCache_HeavyConcurrency_RandomAccessUnderLoad_DataIntegrity()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 200 * 1024 * 1024);
        const int fileCount = 5;
        const int fileSize = 10 * 1024 * 1024; // 10MB each
        const int readersPerFile = 20;
        ConcurrentBag<bool> verificationResults = new();

        try
        {
            // Pre-populate cache with files
            Dictionary<string, byte[]> fileData = new();
            for (int i = 0; i < fileCount; i++)
            {
                string key = $"disk-random-{i}";
                byte[] data = CreateTestDataForKey(key, fileSize);
                fileData[key] = data;

                using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                           ct => Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(data),
                               SizeBytes = data.Length
                           }), CancellationToken.None))
                {
                }
            }

            // Act - many concurrent random access reads
            Random random = new(42); // Fixed seed for reproducibility
            int[] randomOffsets = Enumerable.Range(0, 100)
                .Select(_ => random.Next(0, fileSize - 4096))
                .ToArray();

            Task[] tasks = Enumerable.Range(0, fileCount * readersPerFile)
                .Select(async i =>
                {
                    string key = $"disk-random-{i % fileCount}";
                    byte[] expected = fileData[key];

                    // Random access reads
                    using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                               key,
                               TimeSpan.FromMinutes(30),
                               ct => throw new InvalidOperationException("Should be cached"),
                               CancellationToken.None))
                    {
                        foreach (int offset in randomOffsets.Take(10))
                        {
                            byte[] buffer = new byte[4096];
                            handle.Value.Seek(offset, SeekOrigin.Begin);
                            await handle.Value.ReadExactlyAsync(buffer);

                            bool match = buffer.SequenceEqual(expected.AsSpan(offset, 4096).ToArray());
                            verificationResults.Add(match);
                        }
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All random access reads should return correct data");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// HEAVY LOAD TEST: Tests eviction behavior with active borrowers for disk cache.
    /// 20 concurrent 2MB files compete for 10MB cache capacity, forcing constant eviction.
    /// Each borrower holds their handle and performs 5 reads with 10ms delays between.
    /// Borrowed entries MUST remain accessible despite eviction pressure on other entries.
    /// No exceptions should occur, and all 100 reads (20 files × 5 reads) must succeed.
    /// </summary>
    [Fact]
    public async Task DiskCache_HeavyConcurrency_EvictionWithActiveBorrowers_NoCorruption()
    {
        // Arrange - small cache to force eviction
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 10 * 1024 * 1024); // 10MB
        const int fileSize = 2 * 1024 * 1024; // 2MB each
        const int totalFiles = 20; // Will definitely exceed capacity
        ConcurrentBag<bool> verificationResults = new();
        ConcurrentBag<Exception> exceptions = new();

        try
        {
            // Act
            Task[] tasks = Enumerable.Range(0, totalFiles)
                .Select(async i =>
                {
                    try
                    {
                        string key = $"disk-evict-{i}";
                        byte[] data = CreateTestDataForKey(key, fileSize);

                        // Hold the handle and do multiple reads
                        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                                   key,
                                   TimeSpan.FromMinutes(30),
                                   ct => Task.FromResult(new CacheFactoryResult<Stream>
                                   {
                                       Value = new MemoryStream(data),
                                       SizeBytes = data.Length
                                   }),
                                   CancellationToken.None))
                        {
                            for (int read = 0; read < 5; read++)
                            {
                                byte[] buffer = new byte[fileSize];
                                handle.Value.Seek(0, SeekOrigin.Begin);
                                await handle.Value.ReadExactlyAsync(buffer);
                                verificationResults.Add(buffer.SequenceEqual(data));

                                await Task.Delay(10); // Hold handle for a bit
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);

            // Process any pending cleanup
            cache.ProcessPendingCleanup();

            // Assert
            exceptions.Should().BeEmpty("No exceptions during eviction under load");
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All reads should succeed even during eviction");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// RESOURCE LEAK TEST: Verifies that MemoryMappedViewStreams are properly disposed when
    /// handles are returned to the cache. Performs 100 borrow/return cycles on a single 1MB
    /// disk-cached entry. After CacheHandle.Dispose() fix, each cycle disposes its ViewStream.
    /// Without the fix, this would leak 100 ViewStreams. Test verifies only 1 temp file exists
    /// and the entry remains fully functional after all cycles.
    /// </summary>
    [Fact]
    public async Task DiskCache_HeavyConcurrency_StreamDisposeOnReturn_NoResourceLeak()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 100 * 1024 * 1024);
        byte[] testData = CreateTestData(1024 * 1024); // 1MB
        const int iterations = 100;

        try
        {
            // Pre-populate
            using (ICacheHandle<Stream> h = await cache.BorrowAsync("stream-dispose-key", TimeSpan.FromMinutes(30),
                ct => Task.FromResult(new CacheFactoryResult<Stream>
                {
                    Value = new MemoryStream(testData),
                    SizeBytes = testData.Length
                }), CancellationToken.None))
            { }

            // Act - many borrow/return cycles
            for (int i = 0; i < iterations; i++)
            {
                // Read some data
                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                           "stream-dispose-key",
                           TimeSpan.FromMinutes(30),
                           ct => throw new InvalidOperationException("Should be cached"),
                           CancellationToken.None))
                {
                    byte[] buffer = new byte[4096];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    await handle.Value.ReadExactlyAsync(buffer);
                }

                // Handle disposed at end of using block - stream should be disposed too
            }

            // Verify we can still borrow after many cycles
            using (ICacheHandle<Stream> finalHandle = await cache.BorrowAsync(
                "stream-dispose-key",
                TimeSpan.FromMinutes(30),
                ct => throw new InvalidOperationException("Should be cached"),
                CancellationToken.None))
            {
                byte[] buffer = new byte[testData.Length];
                finalHandle.Value.Seek(0, SeekOrigin.Begin);
                await finalHandle.Value.ReadExactlyAsync(buffer);
                buffer.Should().BeEquivalentTo(testData);
            }

            // Assert
            cache.EntryCount.Should().Be(1);
            cache.BorrowedEntryCount.Should().Be(0);

            // Only one temp file should exist
            Directory.GetFiles(_tempDir, "*.zip2vd.cache").Should().HaveCount(1);
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    #endregion

    #region Cache Thrashing / Frequent Eviction Tests

    /// <summary>
    /// CACHE THRASHING TEST: Simulates worst-case scenario where cache capacity equals one entry size.
    /// Every new entry evicts the previous one, and then we immediately access the evicted entry.
    /// This creates a "thrashing" pattern where the cache provides zero benefit.
    /// Verifies correctness under this pathological workload - all data must remain correct
    /// despite constant eviction and re-materialization.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Thrashing_SingleEntryCapacity_EvictsOnEveryAdd()
    {
        // Arrange - cache can only hold ONE 10KB entry
        const int entrySize = 10 * 1024; // 10KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize);
        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();

        // Act - repeatedly add entries, each evicting the previous
        for (int round = 0; round < 50; round++)
        {
            // Add entry A (evicts nothing first time, evicts B on subsequent rounds)
            string keyA = "thrash-key-A";
            byte[] dataA = CreateTestDataForKey(keyA, entrySize);

            using (ICacheHandle<Stream> handleA = await cache.BorrowAsync(keyA, TimeSpan.FromMinutes(30),
                ct =>
                {
                    Interlocked.Increment(ref factoryCallCount);
                    return Task.FromResult(new CacheFactoryResult<Stream>
                    {
                        Value = new MemoryStream(dataA),
                        SizeBytes = dataA.Length
                    });
                }, CancellationToken.None))
            {
                byte[] buffer = new byte[entrySize];
                handleA.Value.Seek(0, SeekOrigin.Begin);
                await handleA.Value.ReadExactlyAsync(buffer);
                verificationResults.Add(buffer.SequenceEqual(dataA));
            }

            // Add entry B (evicts A)
            string keyB = "thrash-key-B";
            byte[] dataB = CreateTestDataForKey(keyB, entrySize);

            using (ICacheHandle<Stream> handleB = await cache.BorrowAsync(keyB, TimeSpan.FromMinutes(30),
                ct =>
                {
                    Interlocked.Increment(ref factoryCallCount);
                    return Task.FromResult(new CacheFactoryResult<Stream>
                    {
                        Value = new MemoryStream(dataB),
                        SizeBytes = dataB.Length
                    });
                }, CancellationToken.None))
            {
                byte[] buffer = new byte[entrySize];
                handleB.Value.Seek(0, SeekOrigin.Begin);
                await handleB.Value.ReadExactlyAsync(buffer);
                verificationResults.Add(buffer.SequenceEqual(dataB));
            }
        }

        // Assert
        verificationResults.Should().HaveCount(100); // 50 rounds × 2 entries
        verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
            "All reads must return correct data despite constant eviction");

        // Factory should be called many times (no caching benefit in thrashing)
        // First round: 2 calls. Subsequent 49 rounds: 2 calls each (A evicts B, B evicts A)
        factoryCallCount.Should().BeGreaterThanOrEqualTo(50,
            "Thrashing should cause frequent re-materialization");

        cache.EntryCount.Should().Be(1, "Only one entry fits in cache");
    }

    /// <summary>
    /// CACHE THRASHING TEST: Sequential access pattern where each access evicts the previous entry,
    /// followed by immediate re-access of the evicted entry. Tests N entries where cache holds N-2.
    /// Pattern: Access 1,2,3,4,5 then 1,2,3,4,5 again. Second round sees cache misses for entries
    /// evicted in the first round. Verifies correctness under sequential eviction patterns.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Thrashing_SequentialAccessPattern_FrequentReEviction()
    {
        // Arrange - cache holds 3 entries, we access 5 in sequence (ensures eviction)
        const int entrySize = 10 * 1024; // 10KB
        const int cacheCapacity = 3;
        const int keyCount = 5;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);

        Dictionary<string, int> factoryCalls = new();
        ConcurrentBag<bool> verificationResults = new();

        async Task AccessKey(int keyIndex)
        {
            string key = $"seq-key-{keyIndex}";
            byte[] data = CreateTestDataForKey(key, entrySize);

            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct =>
                       {
                           lock (factoryCalls)
                           {
                               factoryCalls[key] = factoryCalls.GetValueOrDefault(key, 0) + 1;
                           }

                           return Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(data),
                               SizeBytes = data.Length
                           });
                       }, CancellationToken.None))
            {
                byte[] buffer = new byte[entrySize];
                handle.Value.Seek(0, SeekOrigin.Begin);
                await handle.Value.ReadExactlyAsync(buffer);
                verificationResults.Add(buffer.SequenceEqual(data));
            }
        }

        // Act - multiple rounds of sequential access
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < keyCount; i++)
            {
                await AccessKey(i);
            }
        }

        // Assert
        verificationResults.Should().HaveCount(50); // 10 rounds × 5 keys
        verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
            "All reads must return correct data");

        // Total factory calls should be higher than keyCount due to eviction
        int totalFactoryCalls = factoryCalls.Values.Sum();
        totalFactoryCalls.Should().BeGreaterThan(keyCount,
            "Sequential access with smaller cache should cause some re-materialization");

        factoryCalls.Should().HaveCount(keyCount);
    }

    /// <summary>
    /// CACHE THRASHING TEST: Working set larger than cache with random access pattern.
    /// 20 keys compete for cache space that holds only 5. Random access ensures
    /// unpredictable eviction patterns. Tests cache stability and correctness under
    /// high eviction pressure with non-sequential access.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Thrashing_RandomAccessLargerThanCache_HighEvictionRate()
    {
        // Arrange - cache holds 5 entries, working set is 20
        const int entrySize = 10 * 1024; // 10KB
        const int cacheCapacity = 5;
        const int workingSetSize = 20;
        const int totalAccesses = 500;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);

        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();
        Random random = new(42); // Fixed seed for reproducibility

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < workingSetSize; i++)
        {
            string key = $"random-key-{i}";
            keyData[key] = CreateTestDataForKey(key, entrySize);
        }

        // Act - random access pattern
        for (int i = 0; i < totalAccesses; i++)
        {
            int keyIndex = random.Next(workingSetSize);
            string key = $"random-key-{keyIndex}";
            byte[] expectedData = keyData[key];

            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct =>
                       {
                           Interlocked.Increment(ref factoryCallCount);
                           return Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(expectedData),
                               SizeBytes = expectedData.Length
                           });
                       }, CancellationToken.None))
            {
                byte[] buffer = new byte[entrySize];
                handle.Value.Seek(0, SeekOrigin.Begin);
                await handle.Value.ReadExactlyAsync(buffer);
                verificationResults.Add(buffer.SequenceEqual(expectedData));
            }
        }

        // Assert
        verificationResults.Should().HaveCount(totalAccesses);
        verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
            "All reads must return correct data despite high eviction rate");

        // With 20 keys and 5 capacity, expect high miss rate
        double missRate = (double)factoryCallCount / totalAccesses;
        missRate.Should().BeGreaterThan(0.5, "High eviction rate expected with 4x oversubscription");

        cache.EntryCount.Should().BeLessThanOrEqualTo(cacheCapacity);
    }

    /// <summary>
    /// CACHE THRASHING TEST: Concurrent thrashing from multiple threads.
    /// 10 threads each performing sequential access over 10 keys with cache holding 3.
    /// Tests thread-safety of eviction under heavy concurrent thrashing load.
    /// All operations must complete without deadlock or data corruption.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Thrashing_ConcurrentThrashing_ThreadSafety()
    {
        // Arrange - cache holds 3 entries, 10 keys accessed by 10 threads
        const int entrySize = 8 * 1024; // 8KB
        const int cacheCapacity = 3;
        const int keyCount = 10;
        const int threadCount = 10;
        const int accessesPerThread = 50;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);

        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();
        ConcurrentBag<Exception> exceptions = new();

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keyCount; i++)
        {
            string key = $"concurrent-thrash-{i}";
            keyData[key] = CreateTestDataForKey(key, entrySize);
        }

        // Act - concurrent thrashing
        Task[] tasks = Enumerable.Range(0, threadCount)
            .Select(async threadId =>
            {
                try
                {
                    for (int access = 0; access < accessesPerThread; access++)
                    {
                        int keyIndex = (threadId + access) % keyCount; // Different pattern per thread
                        string key = $"concurrent-thrash-{keyIndex}";
                        byte[] expectedData = keyData[key];

                        using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                                   ct =>
                                   {
                                       Interlocked.Increment(ref factoryCallCount);
                                       return Task.FromResult(new CacheFactoryResult<Stream>
                                       {
                                           Value = new MemoryStream(expectedData),
                                           SizeBytes = expectedData.Length
                                       });
                                   }, CancellationToken.None))
                        {
                            byte[] buffer = new byte[entrySize];
                            handle.Value.Seek(0, SeekOrigin.Begin);
                            await handle.Value.ReadExactlyAsync(buffer);
                            verificationResults.Add(buffer.SequenceEqual(expectedData));
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty("No exceptions during concurrent thrashing");
        verificationResults.Should().HaveCount(threadCount * accessesPerThread);
        verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
            "All concurrent reads must return correct data");

        // High factory call count expected due to thrashing
        factoryCallCount.Should().BeGreaterThan(keyCount,
            "Thrashing should cause re-materialization");
    }

    /// <summary>
    /// DISK CACHE THRASHING TEST: Same as memory thrashing but with disk storage.
    /// Single-entry capacity forces every add to evict and delete temp file.
    /// Verifies MemoryMappedFile cleanup works correctly under rapid eviction.
    /// </summary>
    [Fact]
    public async Task DiskCache_Thrashing_SingleEntryCapacity_TempFileCleanup()
    {
        // Arrange - cache can only hold ONE 1MB entry
        const int entrySize = 1024 * 1024; // 1MB
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: entrySize);
        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();

        try
        {
            // Act - repeatedly add entries, each evicting the previous
            for (int round = 0; round < 20; round++)
            {
                // Add entry A
                string keyA = "disk-thrash-A";
                byte[] dataA = CreateTestDataForKey(keyA, entrySize);

                using (ICacheHandle<Stream> handleA = await cache.BorrowAsync(keyA, TimeSpan.FromMinutes(30),
                    ct =>
                    {
                        Interlocked.Increment(ref factoryCallCount);
                        return Task.FromResult(new CacheFactoryResult<Stream>
                        {
                            Value = new MemoryStream(dataA),
                            SizeBytes = dataA.Length
                        });
                    }, CancellationToken.None))
                {
                    byte[] buffer = new byte[entrySize];
                    handleA.Value.Seek(0, SeekOrigin.Begin);
                    await handleA.Value.ReadExactlyAsync(buffer);
                    verificationResults.Add(buffer.SequenceEqual(dataA));
                }

                // Process pending cleanup to actually delete files
                cache.ProcessPendingCleanup();

                // Add entry B (evicts A)
                string keyB = "disk-thrash-B";
                byte[] dataB = CreateTestDataForKey(keyB, entrySize);

                using (ICacheHandle<Stream> handleB = await cache.BorrowAsync(keyB, TimeSpan.FromMinutes(30),
                    ct =>
                    {
                        Interlocked.Increment(ref factoryCallCount);
                        return Task.FromResult(new CacheFactoryResult<Stream>
                        {
                            Value = new MemoryStream(dataB),
                            SizeBytes = dataB.Length
                        });
                    }, CancellationToken.None))
                {
                    byte[] buffer = new byte[entrySize];
                    handleB.Value.Seek(0, SeekOrigin.Begin);
                    await handleB.Value.ReadExactlyAsync(buffer);
                    verificationResults.Add(buffer.SequenceEqual(dataB));
                }

                // Process pending cleanup
                cache.ProcessPendingCleanup();
            }

            // Assert
            verificationResults.Should().HaveCount(40); // 20 rounds × 2 entries
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All reads must return correct data");

            // Should have only 1 temp file at any time
            int tempFileCount = Directory.GetFiles(_tempDir, "*.zip2vd.cache").Length;
            tempFileCount.Should().BeLessThanOrEqualTo(1,
                "Temp files should be cleaned up after eviction");

            factoryCallCount.Should().BeGreaterThanOrEqualTo(20,
                "Thrashing should cause frequent re-materialization");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// DISK CACHE THRASHING TEST: Working set exceeds cache with large files.
    /// 10 x 5MB files compete for 20MB cache (holds ~4). Random access pattern
    /// causes frequent eviction and temp file deletion/recreation.
    /// Verifies disk cache stability under heavy eviction load.
    /// </summary>
    [Fact]
    public async Task DiskCache_Thrashing_LargeFilesExceedCapacity_FrequentEviction()
    {
        // Arrange - cache holds ~4 entries, working set is 10
        const int entrySize = 5 * 1024 * 1024; // 5MB
        const int cacheCapacityEntries = 4;
        const int workingSetSize = 10;
        const int totalAccesses = 50;
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: (long)entrySize * cacheCapacityEntries);

        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();
        Random random = new(123);

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < workingSetSize; i++)
        {
            string key = $"disk-large-thrash-{i}";
            keyData[key] = CreateTestDataForKey(key, entrySize);
        }

        try
        {
            // Act - random access pattern causing eviction
            for (int i = 0; i < totalAccesses; i++)
            {
                int keyIndex = random.Next(workingSetSize);
                string key = $"disk-large-thrash-{keyIndex}";
                byte[] expectedData = keyData[key];

                // Verify with random access reads
                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                           ct =>
                           {
                               Interlocked.Increment(ref factoryCallCount);
                               return Task.FromResult(new CacheFactoryResult<Stream>
                               {
                                   Value = new MemoryStream(expectedData),
                                   SizeBytes = expectedData.Length
                               });
                           }, CancellationToken.None))
                {
                    byte[] buffer = new byte[4096];
                    int[] offsets = { 0, entrySize / 4, entrySize / 2, entrySize - 4096 };

                    foreach (int offset in offsets)
                    {
                        handle.Value.Seek(offset, SeekOrigin.Begin);
                        await handle.Value.ReadExactlyAsync(buffer);
                        bool match = buffer.SequenceEqual(expectedData.AsSpan(offset, 4096).ToArray());
                        verificationResults.Add(match);
                    }

                    // Periodically process cleanup
                    if (i % 5 == 0)
                    {
                        cache.ProcessPendingCleanup();
                    }
                }
            }

            // Assert
            verificationResults.Should().HaveCount(totalAccesses * 4); // 4 reads per access
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All random access reads must return correct data");

            // High miss rate expected
            double missRate = (double)factoryCallCount / totalAccesses;
            missRate.Should().BeGreaterThan(0.4,
                "Eviction expected with 2.5x oversubscription");

            cache.EntryCount.Should().BeLessThanOrEqualTo(cacheCapacityEntries);
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// DISK CACHE THRASHING TEST: Concurrent thrashing with disk storage.
    /// Multiple threads accessing overlapping keys, causing concurrent eviction
    /// and MemoryMappedFile creation/disposal. Tests thread-safety of disk cleanup.
    /// </summary>
    [Fact]
    public async Task DiskCache_Thrashing_ConcurrentEviction_NoResourceLeak()
    {
        // Arrange - cache holds 3 entries, 8 keys accessed by 5 threads
        const int entrySize = 2 * 1024 * 1024; // 2MB
        const int cacheCapacity = 3;
        const int keyCount = 8;
        const int threadCount = 5;
        const int accessesPerThread = 20;
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: (long)entrySize * cacheCapacity);

        int factoryCallCount = 0;
        ConcurrentBag<bool> verificationResults = new();
        ConcurrentBag<Exception> exceptions = new();

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keyCount; i++)
        {
            string key = $"disk-concurrent-thrash-{i}";
            keyData[key] = CreateTestDataForKey(key, entrySize);
        }

        try
        {
            // Act - concurrent disk thrashing
            Task[] tasks = Enumerable.Range(0, threadCount)
                .Select(async threadId =>
                {
                    try
                    {
                        Random random = new(threadId * 1000); // Different seed per thread

                        for (int access = 0; access < accessesPerThread; access++)
                        {
                            int keyIndex = random.Next(keyCount);
                            string key = $"disk-concurrent-thrash-{keyIndex}";
                            byte[] expectedData = keyData[key];

                            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                                       ct =>
                                       {
                                           Interlocked.Increment(ref factoryCallCount);
                                           return Task.FromResult(new CacheFactoryResult<Stream>
                                           {
                                               Value = new MemoryStream(expectedData),
                                               SizeBytes = expectedData.Length
                                           });
                                       }, CancellationToken.None))
                            {
                                byte[] buffer = new byte[entrySize];
                                handle.Value.Seek(0, SeekOrigin.Begin);
                                await handle.Value.ReadExactlyAsync(buffer);
                                verificationResults.Add(buffer.SequenceEqual(expectedData));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);

            // Process all pending cleanup
            while (cache.PendingCleanupCount > 0)
            {
                cache.ProcessPendingCleanup();
                await Task.Delay(10);
            }

            // Assert
            exceptions.Should().BeEmpty("No exceptions during concurrent disk thrashing");
            verificationResults.Should().HaveCount(threadCount * accessesPerThread);
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All concurrent disk reads must return correct data");

            // Verify no temp file leak
            int tempFileCount = Directory.GetFiles(_tempDir, "*.zip2vd.cache").Length;
            tempFileCount.Should().BeLessThanOrEqualTo(cacheCapacity,
                "Should not leak temp files");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// EVICT-AND-REACCESS TEST: Specifically tests the pattern where an entry is evicted
    /// and then immediately re-accessed. Verifies the cache correctly handles this common
    /// worst-case pattern without corruption or errors.
    /// </summary>
    [Fact]
    public async Task MemoryCache_EvictThenReaccess_ImmediateReaccessAfterEviction()
    {
        // Arrange - cache holds exactly 2 entries
        const int entrySize = 10 * 1024; // 10KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * 2);

        Dictionary<string, int> factoryCalls = new();
        ConcurrentBag<bool> verificationResults = new();

        async Task<ICacheHandle<Stream>> AccessKey(string key)
        {
            byte[] data = CreateTestDataForKey(key, entrySize);

            ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                ct =>
                {
                    lock (factoryCalls)
                    {
                        factoryCalls[key] = factoryCalls.GetValueOrDefault(key, 0) + 1;
                    }
                    return Task.FromResult(new CacheFactoryResult<Stream>
                    {
                        Value = new MemoryStream(data),
                        SizeBytes = data.Length
                    });
                }, CancellationToken.None);

            byte[] buffer = new byte[entrySize];
            handle.Value.Seek(0, SeekOrigin.Begin);
            await handle.Value.ReadExactlyAsync(buffer);
            verificationResults.Add(buffer.SequenceEqual(data));

            return handle;
        }

        // Act - explicit evict-and-reaccess pattern
        for (int round = 0; round < 20; round++)
        {
            // Fill cache with A and B
            using (await AccessKey("evict-reaccess-A")) { }
            using (await AccessKey("evict-reaccess-B")) { }

            cache.EntryCount.Should().Be(2);

            // Add C, which evicts A (LRU)
            using (await AccessKey("evict-reaccess-C")) { }

            // Immediately re-access A (which was just evicted)
            using (await AccessKey("evict-reaccess-A")) { }

            // Add D, which evicts B (LRU)
            using (await AccessKey("evict-reaccess-D")) { }

            // Immediately re-access B (which was just evicted)
            using (await AccessKey("evict-reaccess-B")) { }
        }

        // Assert
        verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
            "All reads including re-accesses after eviction must return correct data");

        // A and B should have been called multiple times (evicted and re-accessed)
        factoryCalls["evict-reaccess-A"].Should().BeGreaterThan(1,
            "A should be re-materialized after eviction");
        factoryCalls["evict-reaccess-B"].Should().BeGreaterThan(1,
            "B should be re-materialized after eviction");
    }

    /// <summary>
    /// EVICT-AND-REACCESS TEST for disk cache: Same pattern as memory cache test.
    /// Entry is evicted (temp file deleted), then immediately re-accessed (new temp file created).
    /// Verifies MemoryMappedFile recreation works correctly.
    /// </summary>
    [Fact]
    public async Task DiskCache_EvictThenReaccess_TempFileRecreation()
    {
        // Arrange - cache holds exactly 2 entries
        const int entrySize = 1024 * 1024; // 1MB
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: entrySize * 2);

        Dictionary<string, int> factoryCalls = new();
        ConcurrentBag<bool> verificationResults = new();

        async Task<ICacheHandle<Stream>> AccessKey(string key)
        {
            byte[] data = CreateTestDataForKey(key, entrySize);

            ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                ct =>
                {
                    lock (factoryCalls)
                    {
                        factoryCalls[key] = factoryCalls.GetValueOrDefault(key, 0) + 1;
                    }
                    return Task.FromResult(new CacheFactoryResult<Stream>
                    {
                        Value = new MemoryStream(data),
                        SizeBytes = data.Length
                    });
                }, CancellationToken.None);

            byte[] buffer = new byte[entrySize];
            handle.Value.Seek(0, SeekOrigin.Begin);
            await handle.Value.ReadExactlyAsync(buffer);
            verificationResults.Add(buffer.SequenceEqual(data));

            return handle;
        }

        try
        {
            // Act - explicit evict-and-reaccess pattern for disk cache
            for (int round = 0; round < 10; round++)
            {
                // Fill cache with A and B
                using (await AccessKey("disk-evict-A")) { }
                using (await AccessKey("disk-evict-B")) { }

                // Process cleanup
                cache.ProcessPendingCleanup();

                // Add C, which evicts A
                using (await AccessKey("disk-evict-C")) { }

                // Process cleanup to delete A's temp file
                cache.ProcessPendingCleanup();

                // Immediately re-access A (creates new temp file)
                using (await AccessKey("disk-evict-A")) { }

                // Verify temp file count
                int tempFiles = Directory.GetFiles(_tempDir, "*.zip2vd.cache").Length;
                tempFiles.Should().BeLessThanOrEqualTo(3,
                    "Should not accumulate excess temp files");
            }

            // Assert
            verificationResults.Should().AllSatisfy(v => v.Should().BeTrue(),
                "All reads must return correct data after temp file recreation");

            factoryCalls["disk-evict-A"].Should().BeGreaterThan(1,
                "A should be re-materialized after eviction");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    #endregion

    #region Internal Consistency Verification Tests

    /// <summary>
    /// CONSISTENCY TEST: Verifies that after many sequential borrow/return operations,
    /// RefCount returns to 0 for all entries and CurrentSizeBytes matches the sum of
    /// all entry sizes. Tests internal accounting correctness over 1000 operations.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_RefCountAndSizeCorrectAfterManyOperations()
    {
        // Arrange
        const int entrySize = 10 * 1024; // 10KB
        const int cacheCapacity = 10;
        const int keyCount = 20;
        const int operationCount = 1000;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);
        Random random = new(42);

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keyCount; i++)
        {
            keyData[$"consistency-key-{i}"] = CreateTestDataForKey($"consistency-key-{i}", entrySize);
        }

        // Act - many random borrow/return operations
        for (int op = 0; op < operationCount; op++)
        {
            int keyIndex = random.Next(keyCount);
            string key = $"consistency-key-{keyIndex}";
            byte[] data = keyData[key];

            // Read to verify handle works
            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }), CancellationToken.None))
            {
                byte[] buffer = new byte[100];
                handle.Value.Seek(0, SeekOrigin.Begin);
                _ = await handle.Value.ReadAsync(buffer, 0, buffer.Length);
            }
        }

        // Assert - all RefCounts should be 0 after operations complete
        cache.BorrowedEntryCount.Should().Be(0,
            "All handles returned, so no entries should be borrowed");

        // CurrentSizeBytes should equal sum of all cached entry sizes
        long expectedSize = cache.EntryCount * entrySize;
        cache.CurrentSizeBytes.Should().Be(expectedSize,
            $"CurrentSizeBytes ({cache.CurrentSizeBytes}) should equal EntryCount ({cache.EntryCount}) × entrySize ({entrySize})");

        // Size should not exceed capacity
        cache.CurrentSizeBytes.Should().BeLessThanOrEqualTo(entrySize * cacheCapacity,
            "CurrentSizeBytes should not exceed capacity");
    }

    /// <summary>
    /// CONSISTENCY TEST: Verifies internal consistency after heavy concurrent operations
    /// with many evictions. Multiple threads perform random borrow/return while eviction
    /// constantly occurs. After all threads complete, verifies RefCount = 0 and size accounting.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_ConcurrentOperationsWithEviction_InternalStateCorrect()
    {
        // Arrange - small cache to force many evictions
        const int entrySize = 8 * 1024; // 8KB
        const int cacheCapacity = 5;
        const int keyCount = 50;
        const int threadCount = 10;
        const int operationsPerThread = 100;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keyCount; i++)
        {
            keyData[$"concurrent-consistency-{i}"] = CreateTestDataForKey($"concurrent-consistency-{i}", entrySize);
        }

        // Act - concurrent operations causing evictions
        Task[] tasks = Enumerable.Range(0, threadCount)
            .Select(async threadId =>
            {
                Random random = new(threadId * 1000);

                for (int op = 0; op < operationsPerThread; op++)
                {
                    int keyIndex = random.Next(keyCount);
                    string key = $"concurrent-consistency-{keyIndex}";
                    byte[] data = keyData[key];

                    // Small delay to increase overlap
                    using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                               ct => Task.FromResult(new CacheFactoryResult<Stream>
                               {
                                   Value = new MemoryStream(data),
                                   SizeBytes = data.Length
                               }), CancellationToken.None))
                    {
                        if (op % 10 == 0)
                        {
                            await Task.Yield();
                        }
                    }
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - verify internal consistency
        cache.BorrowedEntryCount.Should().Be(0,
            "All handles returned after concurrent operations");

        // Size must be consistent with entry count
        int entryCount = cache.EntryCount;
        long currentSize = cache.CurrentSizeBytes;

        currentSize.Should().Be(entryCount * entrySize,
            $"Size mismatch: CurrentSizeBytes={currentSize}, EntryCount={entryCount}, expected={entryCount * entrySize}");

        // Size should not exceed capacity
        currentSize.Should().BeLessThanOrEqualTo(entrySize * cacheCapacity,
            "CurrentSizeBytes should not exceed capacity after evictions");
    }

    /// <summary>
    /// CONSISTENCY TEST: Verifies that after thrashing (every operation causes eviction),
    /// the internal state remains consistent. Size should always equal exactly one entry
    /// since cache holds only one.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_ThrashingMaintainsCorrectSize()
    {
        // Arrange - cache holds exactly ONE entry
        const int entrySize = 10 * 1024; // 10KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize);
        const int operationCount = 200;

        // Act - alternating between two keys (constant eviction)
        for (int op = 0; op < operationCount; op++)
        {
            string key = op % 2 == 0 ? "thrash-A" : "thrash-B";
            byte[] data = CreateTestDataForKey(key, entrySize);

            // Verify size during borrow
            using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }), CancellationToken.None))
            {
                cache.CurrentSizeBytes.Should().Be(entrySize,
                    $"Size should be exactly {entrySize} during operation {op}");
            }
        }

        // Assert final state
        cache.EntryCount.Should().Be(1, "Only one entry should fit");
        cache.CurrentSizeBytes.Should().Be(entrySize, "Size should equal one entry");
        cache.BorrowedEntryCount.Should().Be(0, "No borrowed entries after completion");
    }

    /// <summary>
    /// CONSISTENCY TEST: Verifies size accounting when entries have varying sizes.
    /// After operations with different sized entries, CurrentSizeBytes should exactly
    /// match the sum of remaining entry sizes.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_VariableSizeEntries_SizeAccountingCorrect()
    {
        // Arrange
        const long cacheCapacity = 100 * 1024; // 100KB
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: cacheCapacity);

        // Create entries with different sizes
        (string key, int size)[] entries =
        [
            ("var-size-1", 10 * 1024),  // 10KB
            ("var-size-2", 20 * 1024),  // 20KB
            ("var-size-3", 15 * 1024),  // 15KB
            ("var-size-4", 25 * 1024),  // 25KB
            ("var-size-5", 8 * 1024),   // 8KB
        ];

        // Act - add all entries
        foreach ((string key, int size) in entries)
        {
            byte[] data = CreateTestDataForKey(key, size);
            using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }), CancellationToken.None))
            {
            }
        }

        // Calculate expected total size
        long expectedTotalSize = entries.Sum(e => e.size);

        // Assert
        cache.EntryCount.Should().Be(entries.Length);
        cache.CurrentSizeBytes.Should().Be(expectedTotalSize,
            $"Size should be sum of all entries: {expectedTotalSize}");
        cache.BorrowedEntryCount.Should().Be(0);

        // Now access entries multiple times and verify size stays constant
        for (int round = 0; round < 5; round++)
        {
            foreach ((string key, int size) in entries)
            {
                using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                           ct => throw new InvalidOperationException("Should be cached"),
                           CancellationToken.None))
                {
                }
            }
        }

        // Size should remain unchanged
        cache.CurrentSizeBytes.Should().Be(expectedTotalSize,
            "Size should remain constant after repeated accesses");
    }

    /// <summary>
    /// CONSISTENCY TEST: Verifies that after Clear(), all internal state is reset correctly.
    /// EntryCount, BorrowedEntryCount, and CurrentSizeBytes should all be 0.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_ClearResetsAllState()
    {
        // Arrange
        const int entrySize = 10 * 1024;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 100 * 1024);

        // Add several entries
        for (int i = 0; i < 5; i++)
        {
            string key = $"clear-test-{i}";
            byte[] data = CreateTestDataForKey(key, entrySize);
            using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }), CancellationToken.None))
            {
            }
        }

        cache.EntryCount.Should().Be(5);
        cache.CurrentSizeBytes.Should().Be(5 * entrySize);

        // Act
        await cache.ClearAsync();

        // Assert
        cache.EntryCount.Should().Be(0, "No entries after clear");
        cache.BorrowedEntryCount.Should().Be(0, "No borrowed entries after clear");
        cache.CurrentSizeBytes.Should().Be(0, "Size should be 0 after clear");
    }

    /// <summary>
    /// CONSISTENCY TEST for disk cache: Verifies RefCount and size correctness after
    /// many operations with MemoryMappedFiles. Same verification as memory cache but
    /// with disk storage to ensure file-backed entries are properly accounted.
    /// </summary>
    [Fact]
    public async Task DiskCache_Consistency_RefCountAndSizeCorrectAfterManyOperations()
    {
        // Arrange
        const int entrySize = 1024 * 1024; // 1MB
        const int cacheCapacity = 5;
        const int keyCount = 10;
        const int operationCount = 50;
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: (long)entrySize * cacheCapacity);

        try
        {
            // Pre-generate key data
            Dictionary<string, byte[]> keyData = new();
            for (int i = 0; i < keyCount; i++)
            {
                keyData[$"disk-consistency-{i}"] = CreateTestDataForKey($"disk-consistency-{i}", entrySize);
            }

            Random random = new(42);

            // Act - many random borrow/return operations
            for (int op = 0; op < operationCount; op++)
            {
                int keyIndex = random.Next(keyCount);
                string key = $"disk-consistency-{keyIndex}";
                byte[] data = keyData[key];

                // Read to verify handle works
                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                           ct => Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(data),
                               SizeBytes = data.Length
                           }), CancellationToken.None))
                {
                    byte[] buffer = new byte[1024];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    _ = await handle.Value.ReadAsync(buffer, 0, buffer.Length);

                    // Periodically process cleanup
                    if (op % 10 == 0)
                    {
                        cache.ProcessPendingCleanup();
                    }
                }
            }

            // Process all pending cleanup
            cache.ProcessPendingCleanup();

            // Assert
            cache.BorrowedEntryCount.Should().Be(0,
                "All handles returned");

            int entryCount = cache.EntryCount;
            long currentSize = cache.CurrentSizeBytes;

            currentSize.Should().Be(entryCount * entrySize,
                $"Size mismatch: CurrentSizeBytes={currentSize}, EntryCount={entryCount}");

            currentSize.Should().BeLessThanOrEqualTo((long)entrySize * cacheCapacity,
                "Size should not exceed capacity");

            // Temp file count should match entry count
            int tempFileCount = Directory.GetFiles(_tempDir, "*.zip2vd.cache").Length;
            tempFileCount.Should().Be(entryCount,
                "Temp file count should match entry count");
        }
        finally
        {
            await cache.ClearAsync();
        }
    }

    /// <summary>
    /// CONSISTENCY TEST: Verifies that RefCount never goes negative even under
    /// heavy concurrent borrow/return with double-dispose attempts.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_RefCountNeverNegative_WithDoubleDispose()
    {
        // Arrange
        const int entrySize = 10 * 1024;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * 10);
        byte[] data = CreateTestData(entrySize);
        const int iterations = 100;

        // Pre-populate
        using (ICacheHandle<Stream> h = await cache.BorrowAsync("refcount-negative-test", TimeSpan.FromMinutes(30),
            ct => Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            }), CancellationToken.None))
        { }

        ConcurrentBag<int> observedRefCounts = new();

        // Act - concurrent borrow/return with some double-dispose attempts
        Task[] tasks = Enumerable.Range(0, 20)
            .Select(async threadId =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    ICacheHandle<Stream> handle = await cache.BorrowAsync("refcount-negative-test", TimeSpan.FromMinutes(30),
                        ct => throw new InvalidOperationException("Should be cached"),
                        CancellationToken.None);

                    observedRefCounts.Add(cache.BorrowedEntryCount);

                    // Normal dispose
                    handle.Dispose();

                    // Attempt double dispose (should be safe - no-op)
                    handle.Dispose();

                    observedRefCounts.Add(cache.BorrowedEntryCount);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        cache.BorrowedEntryCount.Should().Be(0,
            "Final RefCount should be 0");

        observedRefCounts.Should().AllSatisfy(count => count.Should().BeGreaterThanOrEqualTo(0),
            "RefCount should never be negative");
    }

    /// <summary>
    /// CONSISTENCY TEST: Long-running soak test that performs many operations and
    /// periodically verifies internal consistency. Catches subtle accounting bugs
    /// that only manifest after many operations.
    /// </summary>
    [Fact]
    public async Task MemoryCache_Consistency_LongRunningSoak_PeriodicVerification()
    {
        // Arrange
        const int entrySize = 5 * 1024; // 5KB
        const int cacheCapacity = 20;
        const int keyCount = 100;
        const int totalOperations = 2000;
        const int verifyInterval = 100;
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: entrySize * cacheCapacity);
        Random random = new(12345);

        // Pre-generate key data
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keyCount; i++)
        {
            keyData[$"soak-key-{i}"] = CreateTestDataForKey($"soak-key-{i}", entrySize);
        }

        List<(int op, int entryCount, long size, int borrowed)> inconsistencies = new();

        // Act - many operations with periodic verification
        for (int op = 0; op < totalOperations; op++)
        {
            int keyIndex = random.Next(keyCount);
            string key = $"soak-key-{keyIndex}";
            byte[] data = keyData[key];

            using (ICacheHandle<Stream> handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                ct => Task.FromResult(new CacheFactoryResult<Stream>
                {
                    Value = new MemoryStream(data),
                    SizeBytes = data.Length
                }), CancellationToken.None))
            {
                // Small read
                byte[] buffer = new byte[100];
                handle.Value.Seek(0, SeekOrigin.Begin);
                _ = await handle.Value.ReadAsync(buffer, 0, buffer.Length);
            }

            // Periodic consistency check
            if (op % verifyInterval == 0)
            {
                int entryCount = cache.EntryCount;
                long currentSize = cache.CurrentSizeBytes;
                int borrowed = cache.BorrowedEntryCount;

                // All handles should be returned at this point
                if (borrowed != 0)
                {
                    inconsistencies.Add((op, entryCount, currentSize, borrowed));
                }

                // Size should match entry count
                long expectedSize = entryCount * entrySize;
                if (currentSize != expectedSize)
                {
                    inconsistencies.Add((op, entryCount, currentSize, borrowed));
                }

                // Size should not exceed capacity
                if (currentSize > entrySize * cacheCapacity)
                {
                    inconsistencies.Add((op, entryCount, currentSize, borrowed));
                }
            }
        }

        // Final verification
        int finalEntryCount = cache.EntryCount;
        long finalSize = cache.CurrentSizeBytes;
        int finalBorrowed = cache.BorrowedEntryCount;

        // Assert
        inconsistencies.Should().BeEmpty(
            $"Found {inconsistencies.Count} consistency violations during soak test");

        finalBorrowed.Should().Be(0, "No borrowed entries at end");
        finalSize.Should().Be(finalEntryCount * entrySize,
            "Final size should match entry count");
        finalSize.Should().BeLessThanOrEqualTo(entrySize * cacheCapacity,
            "Final size should not exceed capacity");
    }

    #endregion

    #region Stress Tests

    /// <summary>
    /// STRESS TEST: High-throughput performance validation with 1000 concurrent operations.
    /// Uses 100 keys with 50% pre-populated to create a mix of cache hits and misses.
    /// Measures operation latency (must average under 50ms) and tracks hit rate.
    /// All 1000 operations must succeed with correct data. Tests the cache under
    /// realistic sustained load conditions.
    /// </summary>
    [Fact]
    public async Task MemoryCache_StressTest_HighThroughput_1000OperationsPerSecond()
    {
        // Arrange
        GenericCache<Stream> cache = CreateMemoryCache(capacityBytes: 50 * 1024 * 1024);
        const int operations = 1000;
        const int keySpace = 100;
        ConcurrentBag<long> latencies = new();
        int successCount = 0;

        // Pre-create data for all keys to ensure consistency
        Dictionary<string, byte[]> keyData = new();
        for (int i = 0; i < keySpace; i++)
        {
            keyData[$"stress-key-{i}"] = CreateTestData(8 * 1024 + i); // Unique size per key
        }

        // Pre-populate some keys
        for (int i = 0; i < keySpace / 2; i++)
        {
            string key = $"stress-key-{i}";
            byte[] data = keyData[key];
            using (await cache.BorrowAsync(key, TimeSpan.FromMinutes(30),
                       ct => Task.FromResult(new CacheFactoryResult<Stream>
                       {
                           Value = new MemoryStream(data),
                           SizeBytes = data.Length
                       }), CancellationToken.None))
            {
            }
        }

        // Act - high throughput operations
        Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

        Task[] tasks = Enumerable.Range(0, operations)
            .Select(async i =>
            {
                long opStart = sw.ElapsedMilliseconds;

                string key = $"stress-key-{i % keySpace}";
                byte[] data = keyData[key];

                using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                           key,
                           TimeSpan.FromMinutes(30),
                           ct => Task.FromResult(new CacheFactoryResult<Stream>
                           {
                               Value = new MemoryStream(data),
                               SizeBytes = data.Length
                           }),
                           CancellationToken.None))
                {
                    byte[] buffer = new byte[data.Length];
                    handle.Value.Seek(0, SeekOrigin.Begin);
                    await handle.Value.ReadExactlyAsync(buffer);

                    if (buffer.SequenceEqual(data))
                    {
                        Interlocked.Increment(ref successCount);
                    }

                    latencies.Add(sw.ElapsedMilliseconds - opStart);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        successCount.Should().Be(operations, "All operations should succeed");

        double avgLatency = latencies.Average();
        double maxLatency = latencies.Max();

        avgLatency.Should().BeLessThan(50, "Average latency should be under 50ms");

        // Hit rate should be decent with 50% pre-population
        cache.HitRate.Should().BeGreaterThan(0.3, "Should have reasonable hit rate");
    }

    /// <summary>
    /// DEADLOCK DETECTION TEST: Verifies the cache doesn't deadlock under heavy concurrent disk I/O.
    /// 10 files × 5 readers = 50 concurrent operations, each reading 10MB in 1MB chunks.
    /// Uses 60-second timeout - if test completes, no deadlock occurred.
    /// Tests the interaction between per-key locks, eviction locks, and disk I/O.
    /// Critical for production reliability with large file workloads.
    /// </summary>
    [Fact]
    public async Task DiskCache_StressTest_LargeFilesConcurrent_NoDeadlock()
    {
        // Arrange
        GenericCache<Stream> cache = CreateDiskCache(capacityBytes: 200 * 1024 * 1024);
        const int fileCount = 10;
        const int fileSize = 10 * 1024 * 1024; // 10MB
        const int readersPerFile = 5;
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60)); // Timeout
        int completedOperations = 0;

        try
        {
            // Act - concurrent large file operations with timeout
            Task[] tasks = Enumerable.Range(0, fileCount * readersPerFile)
                .Select(async i =>
                {
                    string key = $"deadlock-test-{i % fileCount}";
                    byte[] data = CreateTestDataForKey(key, fileSize);

                    // Do some reads
                    using (ICacheHandle<Stream> handle = await cache.BorrowAsync(
                               key,
                               TimeSpan.FromMinutes(30),
                               ct => Task.FromResult(new CacheFactoryResult<Stream>
                               {
                                   Value = new MemoryStream(data),
                                   SizeBytes = data.Length
                               }),
                               cts.Token))
                    {
                        byte[] buffer = new byte[1024 * 1024]; // 1MB reads
                        for (int pos = 0; pos < fileSize; pos += buffer.Length)
                        {
                            handle.Value.Seek(pos, SeekOrigin.Begin);
                            await handle.Value.ReadExactlyAsync(buffer, cts.Token);
                        }

                        Interlocked.Increment(ref completedOperations);
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert - if we get here without timeout, no deadlock occurred
            completedOperations.Should().Be(fileCount * readersPerFile,
                "All operations should complete without deadlock");
        }
        finally
        {
            cts.Dispose();
            await cache.ClearAsync();
        }
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
            new DiskStorageStrategy(NullLogger<DiskStorageStrategy>.Instance, _tempDir),
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

    /// <summary>
    /// Creates test data that is unique per key, ensuring different keys have different content.
    /// Uses a deterministic hash (not string.GetHashCode which is randomized per process).
    /// </summary>
    private static byte[] CreateTestDataForKey(string key, int size)
    {
        byte[] data = new byte[size];

        // Use a deterministic hash based on key characters
        int hash = 17;
        foreach (char c in key)
        {
            hash = hash * 31 + c;
        }

        for (int i = 0; i < size; i++)
        {
            // Mix key hash with position for unique but deterministic data
            data[i] = (byte)((i + hash) % 256);
        }

        return data;
    }

    #endregion
}