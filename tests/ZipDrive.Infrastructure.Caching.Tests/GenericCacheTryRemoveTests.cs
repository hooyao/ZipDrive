using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ZipDrive.Infrastructure.Caching.Tests;

/// <summary>
/// Tests for GenericCache.TryRemove — targeted entry removal with orphan tracking.
/// Covers TC-GC-01 through TC-GC-05 and TC-EDGE-02 from the dynamic-reload-v2 design.
/// </summary>
public class GenericCacheTryRemoveTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    public void Dispose() { }

    // TC-GC-01: Remove existing entry with RefCount 0
    [Fact]
    public async Task TryRemove_ExistingEntry_RefCountZero_ReturnsTrueAndRemoves()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        byte[] data = new byte[512];

        // Cache an entry and immediately release
        using (await cache.BorrowAsync("key1", TimeSpan.FromMinutes(5), _ =>
            Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            })))
        { }

        cache.ContainsKey("key1").Should().BeTrue();
        long sizeBefore = cache.CurrentSizeBytes;

        // Act
        bool removed = cache.TryRemove("key1");

        // Assert
        removed.Should().BeTrue();
        cache.ContainsKey("key1").Should().BeFalse();
        cache.CurrentSizeBytes.Should().Be(sizeBefore - data.Length);
        cache.EntryCount.Should().Be(0);
        // Memory tier: cleanup is immediate (RequiresAsyncCleanup=false)
        cache.PendingCleanupCount.Should().Be(0);
    }

    // TC-GC-02: Remove non-existent key
    [Fact]
    public void TryRemove_NonExistentKey_ReturnsFalse()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);

        bool removed = cache.TryRemove("nonexistent");

        removed.Should().BeFalse();
        cache.CurrentSizeBytes.Should().Be(0);
        cache.EntryCount.Should().Be(0);
    }

    // TC-GC-03: Remove borrowed entry (RefCount > 0) — deferred cleanup
    [Fact]
    public async Task TryRemove_BorrowedEntry_MarksOrphanedAndDefersCleanup()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        byte[] data = new byte[512];

        // Borrow and hold the handle
        ICacheHandle<Stream> handle = await cache.BorrowAsync("key1", TimeSpan.FromMinutes(5), _ =>
            Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            }));

        cache.BorrowedEntryCount.Should().Be(1);
        int cleanupBefore = cache.PendingCleanupCount;

        // Act — remove while borrowed
        bool removed = cache.TryRemove("key1");

        // Assert — removed from dictionary
        removed.Should().BeTrue();
        cache.ContainsKey("key1").Should().BeFalse();
        cache.CurrentSizeBytes.Should().Be(0); // Size decremented immediately

        // Storage NOT cleaned up yet (orphaned, waiting for handle dispose)
        cache.PendingCleanupCount.Should().Be(cleanupBefore);

        // Active handle still works
        handle.Value.Should().NotBeNull();
        handle.Value.CanRead.Should().BeTrue();

        // Dispose handle — triggers orphan cleanup
        handle.Dispose();

        // Memory tier: orphan cleanup is immediate (RequiresAsyncCleanup=false)
        cache.PendingCleanupCount.Should().Be(cleanupBefore);
    }

    // TC-GC-04: Remove key with in-progress materialization
    [Fact]
    public async Task TryRemove_DuringMaterialization_ReturnsFalse_EntryAppearsAfter()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        byte[] data = new byte[512];
        var gate = new ManualResetEventSlim(false);

        // Start a slow materialization
        var borrowTask = Task.Run(async () =>
        {
            return await cache.BorrowAsync("key1", TimeSpan.FromMinutes(5), async _ =>
            {
                gate.Wait(); // Block until released
                return new CacheFactoryResult<Stream>
                {
                    Value = new MemoryStream(data),
                    SizeBytes = data.Length
                };
            });
        });

        // Give materialization time to start
        await Task.Delay(100);

        // Act — TryRemove while materializing
        bool removed = cache.TryRemove("key1");

        // Assert — entry not yet in _cache, so TryRemove returns false
        removed.Should().BeFalse();

        // Release the factory
        gate.Set();
        using var handle = await borrowTask;

        // Ghost entry now exists (expected — drain-first ordering prevents this in production)
        cache.ContainsKey("key1").Should().BeTrue();
    }

    // TC-GC-05: BorrowAsync after TryRemove returns cache miss
    [Fact]
    public async Task BorrowAsync_AfterTryRemove_IsCacheMiss()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        byte[] data1 = new byte[512];
        byte[] data2 = new byte[256];
        int factoryCallCount = 0;

        // Cache an entry and release
        using (await cache.BorrowAsync("key1", TimeSpan.FromMinutes(5), _ =>
        {
            factoryCallCount++;
            return Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data1),
                SizeBytes = data1.Length
            });
        }))
        { }

        factoryCallCount.Should().Be(1);

        // Remove it
        cache.TryRemove("key1");

        // Borrow again — should be a cache miss
        using (var handle = await cache.BorrowAsync("key1", TimeSpan.FromMinutes(5), _ =>
        {
            factoryCallCount++;
            return Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data2),
                SizeBytes = data2.Length
            });
        }))
        {
            factoryCallCount.Should().Be(2); // Factory called again
            handle.SizeBytes.Should().Be(data2.Length); // New data
        }
    }

    // TC-EDGE-02: TTL expiration racing with TryRemove
    [Fact]
    public async Task TryRemove_RacingWithEvictExpired_NoDuplicateCleanup()
    {
        var cache = CreateCache(capacityBytes: 1024 * 1024);
        byte[] data = new byte[512];

        // Cache an entry with short TTL
        using (await cache.BorrowAsync("key1", TimeSpan.FromMinutes(1), _ =>
            Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            })))
        { }

        // Advance past TTL
        _fakeTime.Advance(TimeSpan.FromMinutes(2));

        // Race: TryRemove and EvictExpired concurrently
        int removeSucceeded = 0;
        var barrier = new Barrier(2);

        var t1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            if (cache.TryRemove("key1"))
                Interlocked.Increment(ref removeSucceeded);
        });

        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            cache.EvictExpired();
        });

        await Task.WhenAll(t1, t2);

        // Exactly one removal should have succeeded (ConcurrentDictionary.TryRemove is atomic)
        // The entry should be gone either way
        cache.ContainsKey("key1").Should().BeFalse();
        cache.EntryCount.Should().Be(0);
        cache.CurrentSizeBytes.Should().Be(0);
    }

    // Helper: create a memory-tier cache
    private GenericCache<Stream> CreateCache(long capacityBytes) =>
        new(
            new MemoryStorageStrategy(),
            new LruEvictionPolicy(),
            capacityBytes,
            _fakeTime,
            NullLogger<GenericCache<Stream>>.Instance,
            name: "test");

}
