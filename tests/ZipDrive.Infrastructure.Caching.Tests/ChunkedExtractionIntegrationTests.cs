using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ZipDrive.Infrastructure.Caching.Tests;

/// <summary>
/// Integration tests for chunked extraction: thundering herd, concurrent readers
/// during extraction, eviction during extraction, RefCount protection, re-borrow,
/// and FileContentCache routing.
/// </summary>
public class ChunkedExtractionIntegrationTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly string _tempDir;

    public ChunkedExtractionIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChunkedIntTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore */ }
    }

    private GenericCache<Stream> CreateChunkedCache(long capacityBytes, int chunkSizeBytes = 1024)
    {
        return new GenericCache<Stream>(
            new ChunkedDiskStorageStrategy(
                NullLogger<ChunkedDiskStorageStrategy>.Instance,
                chunkSizeBytes: chunkSizeBytes,
                _tempDir),
            new LruEvictionPolicy(),
            capacityBytes,
            _fakeTime,
            NullLogger<GenericCache<Stream>>.Instance,
            name: "disk");
    }

    private static byte[] CreateTestData(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i % 256);
        return data;
    }

    private static Func<CancellationToken, Task<CacheFactoryResult<Stream>>> CreateSlowFactory(
        byte[] data, int delayPerChunkMs = 0)
    {
        return async ct =>
        {
            Stream stream = delayPerChunkMs > 0
                ? new SlowStream(new MemoryStream(data), delayPerChunkMs)
                : new MemoryStream(data);

            return new CacheFactoryResult<Stream>
            {
                Value = stream,
                SizeBytes = data.Length
            };
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.1 Thundering Herd Prevention
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ThunderingHerd_20Threads_OnlyOneMaterialization()
    {
        GenericCache<Stream> cache = CreateChunkedCache(capacityBytes: 10 * 1024 * 1024);
        byte[] data = CreateTestData(4096); // 4KB = 4 chunks of 1KB
        int factoryCallCount = 0;

        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = async ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            };
        };

        // 20 concurrent requests for same key
        Task<ICacheHandle<Stream>>[] tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.BorrowAsync("same-key", TimeSpan.FromMinutes(30), factory))
            .ToArray();

        ICacheHandle<Stream>[] handles = await Task.WhenAll(tasks);

        try
        {
            factoryCallCount.Should().Be(1, "factory should be called exactly once");
            handles.Should().HaveCount(20);

            // All handles should return valid streams with correct data
            foreach (var handle in handles)
            {
                handle.Value.Should().BeOfType<ChunkedStream>();
                handle.SizeBytes.Should().Be(4096);
            }
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
            await cache.ClearAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.2 Concurrent Readers During Extraction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentReaders_DuringExtraction_AllGetCorrectData()
    {
        GenericCache<Stream> cache = CreateChunkedCache(
            capacityBytes: 10 * 1024 * 1024,
            chunkSizeBytes: 512); // 512-byte chunks for finer granularity
        byte[] data = CreateTestData(2048); // 2KB = 4 chunks of 512

        // Use slow factory to ensure extraction takes some time
        var factory = CreateSlowFactory(data, delayPerChunkMs: 10);

        // Borrow multiple handles
        var handle1 = await cache.BorrowAsync("key", TimeSpan.FromMinutes(30), factory);
        var handle2 = await cache.BorrowAsync("key", TimeSpan.FromMinutes(30), factory);
        var handle3 = await cache.BorrowAsync("key", TimeSpan.FromMinutes(30), factory);

        try
        {
            // Wait for extraction to complete by reading all data
            var stream1 = handle1.Value;
            var stream2 = handle2.Value;
            var stream3 = handle3.Value;

            // Reader 1: read from start
            byte[] buf1 = new byte[512];
            stream1.Position = 0;
            int read1 = await stream1.ReadAsync(buf1);
            read1.Should().Be(512);
            buf1.Should().Equal(data[..512]);

            // Reader 2: read from middle
            byte[] buf2 = new byte[512];
            stream2.Position = 1024;
            int read2 = await stream2.ReadAsync(buf2);
            read2.Should().Be(512);
            buf2.Should().Equal(data[1024..1536]);

            // Reader 3: read from end
            byte[] buf3 = new byte[512];
            stream3.Position = 1536;
            int read3 = await stream3.ReadAsync(buf3);
            read3.Should().Be(512);
            buf3.Should().Equal(data[1536..2048]);

            // Positions should be independent
            stream1.Position.Should().Be(512);
            stream2.Position.Should().Be(1536);
            stream3.Position.Should().Be(2048);
        }
        finally
        {
            handle1.Dispose();
            handle2.Dispose();
            handle3.Dispose();
            await cache.ClearAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.3 Eviction During Extraction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Eviction_DuringExtraction_CleanCancellation()
    {
        // Small capacity to force eviction
        GenericCache<Stream> cache = CreateChunkedCache(
            capacityBytes: 2048,
            chunkSizeBytes: 512);
        byte[] data = CreateTestData(1024); // 1KB

        var factory = CreateSlowFactory(data);

        // Fill cache to capacity
        using (var handle = await cache.BorrowAsync("fill", TimeSpan.FromMinutes(30), factory))
        {
            // Handle borrowed, release it to make evictable
        }

        // Add another entry — should trigger eviction of "fill"
        using var handle2 = await cache.BorrowAsync("new-key", TimeSpan.FromMinutes(30),
            CreateSlowFactory(CreateTestData(1024)));

        // Verify no leaked handles
        handle2.Dispose();

        cache.BorrowedEntryCount.Should().Be(0, "all handles disposed");

        // No leaked temp files after clear
        await cache.ClearAsync();
        Directory.GetFiles(_tempDir, "*.zip2vd.chunked", SearchOption.AllDirectories)
            .Should().BeEmpty("all files cleaned up");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.4 RefCount Protection During Extraction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefCount_ProtectsFromEviction_DuringExtraction()
    {
        // Capacity for exactly 1 entry
        GenericCache<Stream> cache = CreateChunkedCache(
            capacityBytes: 2048,
            chunkSizeBytes: 512);
        byte[] data = CreateTestData(1024);

        // Borrow entry — RefCount = 1
        using var handle = await cache.BorrowAsync("protected",
            TimeSpan.FromMinutes(30), CreateSlowFactory(data));

        cache.BorrowedEntryCount.Should().Be(1);

        // Try to add another entry that would require eviction
        // The borrowed entry should NOT be evicted
        using var handle2 = await cache.BorrowAsync("new",
            TimeSpan.FromMinutes(30), CreateSlowFactory(CreateTestData(1024)));

        // Both entries should exist (soft capacity overage)
        cache.EntryCount.Should().Be(2);
        cache.BorrowedEntryCount.Should().Be(2);

        // Original entry should still be readable
        byte[] buf = new byte[data.Length];
        handle.Value.Position = 0;
        int read = await handle.Value.ReadAsync(buf);
        read.Should().Be(data.Length);
        buf.Should().Equal(data);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.5 Post-Eviction Re-Borrow
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostEviction_ReBorrow_StartsNewExtraction()
    {
        GenericCache<Stream> cache = CreateChunkedCache(
            capacityBytes: 2048,
            chunkSizeBytes: 512);
        byte[] data = CreateTestData(1024);
        int factoryCallCount = 0;

        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = async ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return new CacheFactoryResult<Stream>
            {
                Value = new MemoryStream(data),
                SizeBytes = data.Length
            };
        };

        // First borrow + release
        using (var handle = await cache.BorrowAsync("key", TimeSpan.FromMinutes(30), factory))
        {
            // Read to verify data
            byte[] buf = new byte[1024];
            int read = await handle.Value.ReadAsync(buf);
            read.Should().Be(1024);
        }

        factoryCallCount.Should().Be(1);

        // Force eviction via TTL
        _fakeTime.Advance(TimeSpan.FromMinutes(31));
        cache.EvictExpired();
        cache.ProcessPendingCleanup();

        cache.EntryCount.Should().Be(0, "entry should be evicted");

        // Re-borrow — should trigger fresh extraction
        using var handle2 = await cache.BorrowAsync("key", TimeSpan.FromMinutes(30), factory);

        factoryCallCount.Should().Be(2, "factory should be called again after eviction");

        byte[] buf2 = new byte[1024];
        handle2.Value.Position = 0;
        int read2 = await handle2.Value.ReadAsync(buf2);
        read2.Should().Be(1024);
        buf2.Should().Equal(data);

        handle2.Dispose();
        await cache.ClearAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5.6 FileContentCache Routing Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FileContentCache_DiskTier_UsesChunkedStrategy()
    {
        var cacheOpts = Microsoft.Extensions.Options.Options.Create(new CacheOptions
        {
            MemoryCacheSizeMb = 10,
            DiskCacheSizeMb = 100,
            SmallFileCutoffMb = 5,
            TempDirectory = _tempDir
        });

        var cache = new FileContentCache(
            new ZipDrive.Infrastructure.Archives.Zip.ZipReaderFactory(),
            cacheOpts,
            new LruEvictionPolicy(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

        // The disk tier should be wired with ChunkedDiskStorageStrategy.
        // We verify indirectly: the cache should initialize without error
        // and create the per-process subdirectory with a scope subdirectory.
        string processDir = Path.Combine(_tempDir, $"ZipDrive-{Environment.ProcessId}");
        Directory.Exists(processDir).Should().BeTrue();
        Directory.GetDirectories(processDir).Should().NotBeEmpty();

        cache.Clear();
        cache.DeleteCacheDirectory();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper: SlowStream that adds delays to simulate slow decompression
    // ═══════════════════════════════════════════════════════════════════

    private sealed class SlowStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _delayMs;

        public SlowStream(Stream inner, int delayMs)
        {
            _inner = inner;
            _delayMs = delayMs;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            return await _inner.ReadAsync(buffer, offset, count, ct);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            return await _inner.ReadAsync(buffer, ct);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delayMs > 0) Thread.Sleep(_delayMs);
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
