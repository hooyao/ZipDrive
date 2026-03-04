using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Archives.Zip.Formats;

namespace ZipDrive.Infrastructure.Caching.Tests;

public sealed class FileContentCacheTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly string _tempDir;

    public FileContentCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FileContentCacheTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private FileContentCache CreateCache(int cutoffMb = 1, int memoryMb = 10, int diskMb = 10, StubZipReaderFactory? factory = null)
    {
        var opts = Options.Create(new CacheOptions
        {
            SmallFileCutoffMb = cutoffMb,
            MemoryCacheSizeMb = memoryMb,
            DiskCacheSizeMb = diskMb,
            TempDirectory = _tempDir
        });

        return new FileContentCache(
            factory ?? new StubZipReaderFactory(),
            opts,
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);
    }

    private static ZipEntryInfo MakeEntry(long uncompressedSize) => new()
    {
        LocalHeaderOffset = 0,
        CompressedSize = uncompressedSize,
        UncompressedSize = uncompressedSize,
        CompressionMethod = 0,
        IsDirectory = false,
        LastModified = DateTime.UtcNow,
        Attributes = FileAttributes.Normal
    };

    // ═══════════════════════════════════════════════════════════════════
    // Tier Routing Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_SmallFile_RoutesToMemoryTier()
    {
        byte[] data = CreateTestData(512);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(cutoffMb: 1, factory: stubFactory);

        byte[] buffer = new byte[1024];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(512), "key1", buffer, 0);

        bytesRead.Should().Be(512);
        cache.MemoryTier.EntryCount.Should().Be(1);
        cache.DiskTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_LargeFile_RoutesToDiskTier()
    {
        int size = 2 * 1024 * 1024; // 2MB, cutoff is 1MB
        byte[] data = CreateTestData(size);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(cutoffMb: 1, factory: stubFactory);

        byte[] buffer = new byte[1024];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(size), "key1", buffer, 0);

        bytesRead.Should().Be(1024);
        cache.DiskTier.EntryCount.Should().Be(1);
        cache.MemoryTier.EntryCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ReadAsync Behavior Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_CacheHit_ReturnsCachedContentWithoutReExtraction()
    {
        byte[] data = CreateTestData(500);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer1 = new byte[500];
        byte[] buffer2 = new byte[500];

        await cache.ReadAsync("test.zip", MakeEntry(500), "key1", buffer1, 0);
        await cache.ReadAsync("test.zip", MakeEntry(500), "key1", buffer2, 0);

        buffer1.Should().Equal(buffer2);
        stubFactory.CreateCount.Should().Be(1, "factory should only be called once (cache hit on second call)");
    }

    [Fact]
    public async Task ReadAsync_AtOffset_ReturnsCorrectBytes()
    {
        byte[] data = new byte[5000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);

        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(5000), "key1", buffer, 1000);

        bytesRead.Should().Be(2000);
        for (int i = 0; i < 2000; i++)
            buffer[i].Should().Be(data[1000 + i], $"byte at position {i} should match source at offset 1000+{i}");
    }

    [Fact]
    public async Task ReadAsync_PastEof_ReturnsRemainingBytes()
    {
        byte[] data = CreateTestData(5000);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(5000), "key1", buffer, 4500);

        bytesRead.Should().Be(500, "only 500 bytes remain from offset 4500 in a 5000-byte file");
    }

    [Fact]
    public async Task ReadAsync_AtExactEof_ReturnsZero()
    {
        byte[] data = CreateTestData(5000);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(5000), "key1", buffer, 5000);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_BeyondEof_ReturnsZero()
    {
        byte[] data = CreateTestData(100);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer = new byte[100];
        int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(100), "key1", buffer, 999);

        bytesRead.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Resource Lifecycle Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_FactoryCreatesAndDisposesZipReader()
    {
        byte[] data = CreateTestData(100);
        var stubFactory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: stubFactory);

        byte[] buffer = new byte[100];
        await cache.ReadAsync("test.zip", MakeEntry(100), "key1", buffer, 0);

        stubFactory.CreateCount.Should().Be(1, "should create exactly one reader");
        stubFactory.LastReader!.Disposed.Should().BeTrue("reader should be disposed after materialization");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Thundering Herd Test
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_ConcurrentSameKey_OnlyExtractsOnce()
    {
        int callCount = 0;
        byte[] data = CreateTestData(1000);
        var stubFactory = new StubZipReaderFactory(data, onCreateReader: () =>
        {
            Interlocked.Increment(ref callCount);
            // Small delay to increase chance of concurrent access
            Thread.Sleep(50);
        });

        var cache = CreateCache(factory: stubFactory);
        var entry = MakeEntry(1000);

        Task<int>[] tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                byte[] buffer = new byte[1000];
                return await cache.ReadAsync("test.zip", entry, "key1", buffer, 0);
            }))
            .ToArray();

        int[] results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().Be(1000));
        callCount.Should().Be(1, "thundering herd: 10 concurrent requests should result in exactly 1 extraction");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Maintenance Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AggregatedProperties_SumBothTiers()
    {
        byte[] smallData = CreateTestData(512);
        int largeSize = 2 * 1024 * 1024;
        byte[] largeData = CreateTestData(largeSize);

        byte[]? currentData = null;
        var stubFactory = new StubZipReaderFactory(ct => currentData!);
        var cache = CreateCache(cutoffMb: 1, factory: stubFactory);

        // Add small file (memory tier)
        currentData = smallData;
        byte[] buf = new byte[1024];
        await cache.ReadAsync("test.zip", MakeEntry(512), "small-key", buf, 0);

        // Add large file (disk tier)
        currentData = largeData;
        await cache.ReadAsync("test.zip", MakeEntry(largeSize), "large-key", buf, 0);

        cache.EntryCount.Should().Be(2);
        cache.CurrentSizeBytes.Should().Be(512 + largeSize);
        cache.BorrowedEntryCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // WarmAsync Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WarmAsync_SmallEntry_StoredInMemoryTier()
    {
        byte[] data = CreateTestData(512);
        var cache = CreateCache(cutoffMb: 1);

        var entry = MakeEntry(512); // 512 bytes < 1 MB cutoff
        await cache.WarmAsync(entry, "warm-key", new MemoryStream(data));

        cache.MemoryTier.EntryCount.Should().Be(1);
        cache.DiskTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task WarmAsync_LargeEntry_StoredInDiskTier()
    {
        int size = 2 * 1024 * 1024; // 2 MB, cutoff is 1 MB
        byte[] data = CreateTestData(size);
        var cache = CreateCache(cutoffMb: 1);

        var entry = MakeEntry(size);
        await cache.WarmAsync(entry, "warm-large-key", new MemoryStream(data));

        cache.DiskTier.EntryCount.Should().Be(1);
        cache.MemoryTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task WarmAsync_ExistingKey_IsNoOp_EntryUnchanged()
    {
        byte[] original = CreateTestData(100);
        byte[] replacement = CreateTestData(100);

        var cache = CreateCache();
        var entry = MakeEntry(100);

        await cache.WarmAsync(entry, "dup-key", new MemoryStream(original));
        // Second warm for the same key — should be a no-op (thundering herd protection)
        await cache.WarmAsync(entry, "dup-key", new MemoryStream(replacement));

        cache.EntryCount.Should().Be(1, "second WarmAsync should not create a second entry");
    }

    [Fact]
    public async Task WarmAsync_AfterWarm_ReadAsyncReturnsCachedContent()
    {
        byte[] data = CreateTestData(500);
        var stubFactory = new StubZipReaderFactory(); // should never be called
        var cache = CreateCache(factory: stubFactory);
        var entry = MakeEntry(500);

        await cache.WarmAsync(entry, "prefetch-key", new MemoryStream(data, writable: false));

        byte[] buffer = new byte[500];
        int bytesRead = await cache.ReadAsync("test.zip", entry, "prefetch-key", buffer, 0);

        bytesRead.Should().Be(500);
        buffer.Should().Equal(data);
        stubFactory.CreateCount.Should().Be(0, "ReadAsync should serve from warm cache, not call factory");
    }

    [Fact]
    public async Task WarmAsync_RefCountIsZeroAfterWarm()
    {
        byte[] data = CreateTestData(200);
        var cache = CreateCache();
        var entry = MakeEntry(200);

        await cache.WarmAsync(entry, "refcount-key", new MemoryStream(data));

        cache.BorrowedEntryCount.Should().Be(0, "WarmAsync disposes handle immediately, leaving RefCount=0");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static byte[] CreateTestData(int size)
    {
        byte[] data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test Stubs
    // ═══════════════════════════════════════════════════════════════════

    private sealed class StubZipReaderFactory : IZipReaderFactory
    {
        private readonly Func<CancellationToken, byte[]>? _dataProvider;
        private readonly byte[]? _fixedData;
        private readonly Action? _onCreateReader;
        private int _createCount;

        public StubZipReaderFactory() : this(Array.Empty<byte>()) { }

        public StubZipReaderFactory(byte[] data, Action? onCreateReader = null)
        {
            _fixedData = data;
            _onCreateReader = onCreateReader;
        }

        public StubZipReaderFactory(Func<CancellationToken, byte[]> dataProvider)
        {
            _dataProvider = dataProvider;
        }

        public int CreateCount => _createCount;
        public StubZipReader? LastReader { get; private set; }

        public IZipReader Create(string filePath)
        {
            Interlocked.Increment(ref _createCount);
            _onCreateReader?.Invoke();
            byte[] data = _fixedData ?? _dataProvider!(CancellationToken.None);
            var reader = new StubZipReader(data);
            LastReader = reader;
            return reader;
        }
    }

    internal sealed class StubZipReader : IZipReader
    {
        private readonly byte[] _data;
        public bool Disposed { get; private set; }

        public StubZipReader(byte[] data)
        {
            _data = data;
        }

        public Task<Stream> OpenEntryStreamAsync(ZipEntryInfo entry, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Stream>(new MemoryStream(_data, writable: false));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        // ═══════════════════════════════════════════════════════════════
        // Unused IZipReader members — stubbed for compilation
        // ═══════════════════════════════════════════════════════════════

        public long StreamLength => _data.Length;
        public string? FilePath => "stub.zip";

        public Task<ZipEocd> ReadEocdAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(
            ZipEocd eocd, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ZipLocalHeader> ReadLocalHeaderAsync(
            long localHeaderOffset, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
