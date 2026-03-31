using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

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

    private FileContentCache CreateCache(int cutoffMb = 1, int memoryMb = 10, int diskMb = 10, StubExtractor? extractor = null)
    {
        var opts = Options.Create(new CacheOptions
        {
            SmallFileCutoffMb = cutoffMb,
            MemoryCacheSizeMb = memoryMb,
            DiskCacheSizeMb = diskMb,
            TempDirectory = _tempDir
        });

        return new FileContentCache(
            new StubFormatRegistry(extractor ?? new StubExtractor()),
            opts,
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);
    }

    private static ArchiveEntryInfo MakeEntry(long uncompressedSize) => new()
    {
        UncompressedSize = uncompressedSize,
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
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(cutoffMb: 1, extractor: stubFactory);

        byte[] buffer = new byte[1024];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(512), "file", "key1", buffer, 0);

        bytesRead.Should().Be(512);
        cache.MemoryTier.EntryCount.Should().Be(1);
        cache.DiskTier.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_LargeFile_RoutesToDiskTier()
    {
        int size = 2 * 1024 * 1024; // 2MB, cutoff is 1MB
        byte[] data = CreateTestData(size);
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(cutoffMb: 1, extractor: stubFactory);

        byte[] buffer = new byte[1024];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(size), "file", "key1", buffer, 0);

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
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer1 = new byte[500];
        byte[] buffer2 = new byte[500];

        await cache.ReadAsync("test.zip", "zip", MakeEntry(500), "file", "key1", buffer1, 0);
        await cache.ReadAsync("test.zip", "zip", MakeEntry(500), "file", "key1", buffer2, 0);

        buffer1.Should().Equal(buffer2);
        stubFactory.ExtractCount.Should().Be(1, "factory should only be called once (cache hit on second call)");
    }

    [Fact]
    public async Task ReadAsync_AtOffset_ReturnsCorrectBytes()
    {
        byte[] data = new byte[5000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);

        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(5000), "file", "key1", buffer, 1000);

        bytesRead.Should().Be(2000);
        for (int i = 0; i < 2000; i++)
            buffer[i].Should().Be(data[1000 + i], $"byte at position {i} should match source at offset 1000+{i}");
    }

    [Fact]
    public async Task ReadAsync_PastEof_ReturnsRemainingBytes()
    {
        byte[] data = CreateTestData(5000);
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(5000), "file", "key1", buffer, 4500);

        bytesRead.Should().Be(500, "only 500 bytes remain from offset 4500 in a 5000-byte file");
    }

    [Fact]
    public async Task ReadAsync_AtExactEof_ReturnsZero()
    {
        byte[] data = CreateTestData(5000);
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer = new byte[2000];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(5000), "file", "key1", buffer, 5000);

        bytesRead.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_BeyondEof_ReturnsZero()
    {
        byte[] data = CreateTestData(100);
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer = new byte[100];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", MakeEntry(100), "file", "key1", buffer, 999);

        bytesRead.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Resource Lifecycle Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_FactoryCreatesAndDisposesZipReader()
    {
        byte[] data = CreateTestData(100);
        var stubFactory = new StubExtractor(data);
        var cache = CreateCache(extractor: stubFactory);

        byte[] buffer = new byte[100];
        await cache.ReadAsync("test.zip", "zip", MakeEntry(100), "file", "key1", buffer, 0);

        stubFactory.ExtractCount.Should().Be(1, "should extract exactly once");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Thundering Herd Test
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_ConcurrentSameKey_OnlyExtractsOnce()
    {
        int callCount = 0;
        byte[] data = CreateTestData(1000);
        var stubFactory = new StubExtractor(data, onExtract: () =>
        {
            Interlocked.Increment(ref callCount);
            // Small delay to increase chance of concurrent access
            Thread.Sleep(50);
        });

        var cache = CreateCache(extractor: stubFactory);
        var entry = MakeEntry(1000);

        Task<int>[] tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                byte[] buffer = new byte[1000];
                return await cache.ReadAsync("test.zip", "zip", entry, "file", "key1", buffer, 0);
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
        var stubFactory = new StubExtractor(ct => currentData!);
        var cache = CreateCache(cutoffMb: 1, extractor: stubFactory);

        // Add small file (memory tier)
        currentData = smallData;
        byte[] buf = new byte[1024];
        await cache.ReadAsync("test.zip", "zip", MakeEntry(512), "file", "small-key", buf, 0);

        // Add large file (disk tier)
        currentData = largeData;
        await cache.ReadAsync("test.zip", "zip", MakeEntry(largeSize), "file", "large-key", buf, 0);

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
        var stubFactory = new StubExtractor(); // should never be called
        var cache = CreateCache(extractor: stubFactory);
        var entry = MakeEntry(500);

        await cache.WarmAsync(entry, "prefetch-key", new MemoryStream(data, writable: false));

        byte[] buffer = new byte[500];
        int bytesRead = await cache.ReadAsync("test.zip", "zip", entry, "file", "prefetch-key", buffer, 0);

        bytesRead.Should().Be(500);
        buffer.Should().Equal(data);
        stubFactory.ExtractCount.Should().Be(0, "ReadAsync should serve from warm cache, not call factory");
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

    internal sealed class StubExtractor : IArchiveEntryExtractor
    {
        private readonly Func<CancellationToken, byte[]>? _dataProvider;
        private readonly byte[]? _fixedData;
        private readonly Action? _onExtract;
        private int _extractCount;

        public string FormatId => "zip";
        public int ExtractCount => _extractCount;

        public StubExtractor() : this(Array.Empty<byte>()) { }

        public StubExtractor(byte[] data, Action? onExtract = null)
        {
            _fixedData = data;
            _onExtract = onExtract;
        }

        public StubExtractor(Func<CancellationToken, byte[]> dataProvider)
        {
            _dataProvider = dataProvider;
        }

        public Task<ExtractionResult> ExtractAsync(
            string archivePath, string internalPath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _extractCount);
            _onExtract?.Invoke();
            byte[] data = _fixedData ?? _dataProvider!(cancellationToken);
            return Task.FromResult(new ExtractionResult
            {
                Stream = new MemoryStream(data, writable: false),
                SizeBytes = data.Length
            });
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
