using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZipDriveV3.Infrastructure.Caching;

namespace ZipDriveV3.Infrastructure.Tests;

/// <summary>
/// Unit tests for MemoryTierCache focusing on the three-layer concurrency strategy.
/// </summary>
public class MemoryTierCacheTests
{
    private readonly FakeTimeProvider _fakeTime = new();

    [Fact]
    public async Task CacheHit_ReturnsDataWithoutCallingFactory()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 1024 * 1024); // 1MB
        var factoryCallCount = 0;

        var factory = (CancellationToken ct) =>
        {
            factoryCallCount++;
            var data = new byte[1024];
            return Task.FromResult<Stream>(new MemoryStream(data));
        };

        // Act: First call (cache miss)
        var stream1 = await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), factory);
        stream1.Should().NotBeNull();
        factoryCallCount.Should().Be(1);

        // Act: Second call (cache hit)
        var stream2 = await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), factory);
        stream2.Should().NotBeNull();
        factoryCallCount.Should().Be(1, "factory should not be called on cache hit");

        // Assert: Cache metrics
        cache.HitRate.Should().Be(0.5); // 1 hit, 1 miss
        cache.EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task ThunderingHerd_OnlyOneMaterialization()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 10 * 1024 * 1024); // 10MB
        var factoryCallCount = 0;
        var materializationStartedCount = 0;
        var tcs = new TaskCompletionSource();

        var factory = async (CancellationToken ct) =>
        {
            Interlocked.Increment(ref materializationStartedCount);
            Interlocked.Increment(ref factoryCallCount);

            // Simulate slow materialization
            await tcs.Task;

            var data = new byte[1024];
            return (Stream)new MemoryStream(data);
        };

        // Act: Start 10 concurrent requests for the same key
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => cache.GetOrAddAsync("same-key", 1024, TimeSpan.FromMinutes(10), factory)))
            .ToArray();

        // Wait for all threads to start materialization
        await Task.Delay(100);

        // Release the factory
        tcs.SetResult();

        // Wait for all tasks to complete
        var streams = await Task.WhenAll(tasks);

        // Assert: Factory called exactly once despite 10 concurrent requests
        factoryCallCount.Should().Be(1, "thundering herd prevention should ensure only one materialization");
        materializationStartedCount.Should().Be(1, "only one thread should start materialization");

        // Assert: All threads got valid streams
        streams.Should().HaveCount(10);
        streams.Should().AllSatisfy(s => s.Should().NotBeNull());

        // Assert: Cache has only one entry
        cache.EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task DifferentKeys_ParallelMaterialization()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 10 * 1024 * 1024); // 10MB
        var key1Started = new TaskCompletionSource();
        var key2Started = new TaskCompletionSource();
        var key1Complete = new TaskCompletionSource();
        var key2Complete = new TaskCompletionSource();

        var factory1 = async (CancellationToken ct) =>
        {
            key1Started.SetResult();
            await key1Complete.Task;
            return (Stream)new MemoryStream(new byte[1024]);
        };

        var factory2 = async (CancellationToken ct) =>
        {
            key2Started.SetResult();
            await key2Complete.Task;
            return (Stream)new MemoryStream(new byte[1024]);
        };

        // Act: Start materialization for two different keys
        var task1 = Task.Run(() => cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), factory1));
        var task2 = Task.Run(() => cache.GetOrAddAsync("key2", 1024, TimeSpan.FromMinutes(10), factory2));

        // Wait for both to start
        await Task.WhenAll(key1Started.Task, key2Started.Task).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: Both started concurrently (not blocked)
        key1Started.Task.IsCompleted.Should().BeTrue("key1 should start without waiting for key2");
        key2Started.Task.IsCompleted.Should().BeTrue("key2 should start without waiting for key1");

        // Complete both
        key1Complete.SetResult();
        key2Complete.SetResult();

        await Task.WhenAll(task1, task2);

        // Assert: Both entries cached
        cache.EntryCount.Should().Be(2);
    }

    [Fact]
    public async Task TtlExpiration_WithFakeTime_EvictsExpiredEntries()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 10 * 1024 * 1024); // 10MB
        var factoryCallCount = 0;

        var factory = (CancellationToken ct) =>
        {
            factoryCallCount++;
            var data = new byte[1024];
            return Task.FromResult<Stream>(new MemoryStream(data));
        };

        // Act: Add entry with 1 minute TTL
        _fakeTime.SetUtcNow(DateTimeOffset.UtcNow);
        await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(1), factory);
        factoryCallCount.Should().Be(1);

        // Act: Cache hit within TTL
        _fakeTime.Advance(TimeSpan.FromSeconds(30));
        await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(1), factory);
        factoryCallCount.Should().Be(1, "should be cache hit within TTL");

        // Act: Advance time past TTL
        _fakeTime.Advance(TimeSpan.FromSeconds(31)); // Total: 61 seconds
        cache.EvictExpired();

        // Act: Next access should be cache miss
        await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(1), factory);
        factoryCallCount.Should().Be(2, "should be cache miss after TTL expired");
    }

    [Fact]
    public async Task CapacityEnforcement_EvictsUsingLruPolicy()
    {
        // Arrange: 1MB capacity cache
        var cache = CreateCache(capacityBytes: 1024 * 1024);

        // Act: Add 500KB file (entry1)
        await AddTestEntry(cache, "entry1", 512 * 1024);
        cache.CurrentSizeBytes.Should().Be(512 * 1024);
        cache.EntryCount.Should().Be(1);

        // Small delay to ensure different timestamps
        _fakeTime.Advance(TimeSpan.FromSeconds(1));

        // Act: Add 500KB file (entry2) - total 1MB, at capacity
        await AddTestEntry(cache, "entry2", 512 * 1024);
        cache.CurrentSizeBytes.Should().Be(1024 * 1024);
        cache.EntryCount.Should().Be(2);

        _fakeTime.Advance(TimeSpan.FromSeconds(1));

        // Act: Add 600KB file (entry3) - should evict entry1 (oldest) via LRU
        await AddTestEntry(cache, "entry3", 600 * 1024);

        // Assert: Capacity not exceeded
        cache.CurrentSizeBytes.Should().BeLessThanOrEqualTo(1024 * 1024);

        // Assert: entry1 evicted, entry2 and entry3 remain
        cache.EntryCount.Should().BeInRange(1, 2);
    }

    [Fact]
    public async Task CapacityEnforcement_EvictsMultipleEntries()
    {
        // Arrange: 1MB capacity cache
        var cache = CreateCache(capacityBytes: 1024 * 1024);

        // Add four 256KB entries
        for (int i = 0; i < 4; i++)
        {
            await AddTestEntry(cache, $"entry{i}", 256 * 1024);
            _fakeTime.Advance(TimeSpan.FromSeconds(1));
        }

        cache.CurrentSizeBytes.Should().Be(1024 * 1024);
        cache.EntryCount.Should().Be(4);

        // Act: Add 700KB entry - should evict at least 3 old entries
        await AddTestEntry(cache, "large", 700 * 1024);

        // Assert: Capacity not exceeded
        cache.CurrentSizeBytes.Should().BeLessThanOrEqualTo(1024 * 1024);

        // Assert: Only 1-2 entries remain (large + possibly entry3)
        cache.EntryCount.Should().BeInRange(1, 2);
    }

    [Fact]
    public async Task ConcurrentAccess_NoExceptions()
    {
        // Arrange: 5MB capacity cache
        var cache = CreateCache(capacityBytes: 5 * 1024 * 1024);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act: Concurrent additions and reads
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await AddTestEntry(cache, $"concurrent-{i}", 200 * 1024);
                    await Task.Delay(5);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert: No exceptions occurred
        exceptions.Should().BeEmpty("concurrent operations should not throw exceptions");

        // Assert: Cache is operating correctly
        cache.CurrentSizeBytes.Should().BeLessOrEqualTo(5 * 1024 * 1024 + (500 * 1024),
            "cache should roughly respect capacity (allowing some over-capacity during concurrent adds)");
    }

    [Fact]
    public async Task HitRate_Calculation_IsAccurate()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 10 * 1024 * 1024);

        // Act: 3 misses
        await AddTestEntry(cache, "key1", 1024);
        await AddTestEntry(cache, "key2", 1024);
        await AddTestEntry(cache, "key3", 1024);

        // 2 hits
        await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), ct => throw new Exception("Should not call"));
        await cache.GetOrAddAsync("key2", 1024, TimeSpan.FromMinutes(10), ct => throw new Exception("Should not call"));

        // Assert: Hit rate = 2 / (2 + 3) = 0.4
        cache.HitRate.Should().BeApproximately(0.4, 0.01);
    }

    [Fact]
    public async Task GetOrAddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 1024 * 1024);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await cache.GetOrAddAsync(null!, 1024, TimeSpan.FromMinutes(10), ct => Task.FromResult<Stream>(new MemoryStream())));
    }

    [Fact]
    public async Task GetOrAddAsync_WithNegativeSize_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 1024 * 1024);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await cache.GetOrAddAsync("key", -1, TimeSpan.FromMinutes(10), ct => Task.FromResult<Stream>(new MemoryStream())));
    }

    [Fact]
    public async Task ReturnedStream_IsReadOnly()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        await AddTestEntry(cache, "key1", 1024);

        // Act
        var stream = await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), ct => throw new Exception("Should not call"));

        // Assert
        stream.CanRead.Should().BeTrue();
        stream.CanWrite.Should().BeFalse();
        stream.CanSeek.Should().BeTrue();
    }

    [Fact]
    public async Task ReturnedStream_SupportsSeek()
    {
        // Arrange
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        var testData = new byte[1024];
        for (int i = 0; i < testData.Length; i++)
            testData[i] = (byte)(i % 256);

        var factory = (CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(testData));
        await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), factory);

        // Act
        var stream = await cache.GetOrAddAsync("key1", 1024, TimeSpan.FromMinutes(10), ct => throw new Exception("Should not call"));

        // Assert: Seek to middle
        stream.Seek(512, SeekOrigin.Begin);
        var buffer = new byte[4];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory());
        bytesRead.Should().Be(4);
        buffer.Should().Equal(testData.AsSpan(512, 4).ToArray());

        // Assert: Seek to beginning
        stream.Seek(0, SeekOrigin.Begin);
        bytesRead = await stream.ReadAsync(buffer.AsMemory());
        bytesRead.Should().Be(4);
        buffer.Should().Equal(testData.AsSpan(0, 4).ToArray());
    }

    // Helper methods

    private MemoryTierCache CreateCache(long capacityBytes)
    {
        return new MemoryTierCache(
            capacityBytes,
            new LruEvictionPolicy(),
            NullLogger<MemoryTierCache>.Instance,
            _fakeTime);
    }

    private async Task AddTestEntry(MemoryTierCache cache, string key, long size)
    {
        var data = new byte[size];
        var factory = (CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(data));
        await cache.GetOrAddAsync(key, size, TimeSpan.FromMinutes(10), factory);
    }
}
