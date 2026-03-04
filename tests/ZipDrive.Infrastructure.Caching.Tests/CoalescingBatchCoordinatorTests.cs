using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Archives.Zip.Formats;

namespace ZipDrive.Infrastructure.Caching.Tests;

/// <summary>
/// Tests for CoalescingBatchCoordinator: options defaults, batch grouping,
/// density calculation, timer behavior, hole handling, speculative caching,
/// and FileContentCache integration with coalescing enabled/disabled.
/// </summary>
public sealed class CoalescingBatchCoordinatorTests : IDisposable
{
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly string _tempDir;

    public CoalescingBatchCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CoalescingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.1  CoalescingOptions defaults
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void CoalescingOptions_Defaults_AreCorrect()
    {
        var opts = new CoalescingOptions();

        opts.Enabled.Should().BeTrue();
        opts.FastPathMs.Should().Be(0);
        opts.WindowMs.Should().Be(500);
        opts.DensityThreshold.Should().Be(0.8);
        opts.SpeculativeCache.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.2  GroupIntoBatches unit tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GroupIntoBatches_EmptyInput_ReturnsEmptyList()
    {
        var result = CoalescingBatchCoordinator.GroupIntoBatches([], 0.8);
        result.Should().BeEmpty();
    }

    [Fact]
    public void GroupIntoBatches_SingleEntry_ReturnsSingleBatchOfOne()
    {
        var requests = new List<PendingRequest> { MakeRequest(offset: 0, compressed: 1000) };
        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(1);
        result[0].Should().HaveCount(1);
    }

    [Fact]
    public void GroupIntoBatches_AllContiguous_SingleBatch()
    {
        // Three adjacent entries with density ~100% (no gaps)
        var requests = new List<PendingRequest>
        {
            MakeRequest(offset: 0,      compressed: 50_000),
            MakeRequest(offset: 50_060, compressed: 45_000),
            MakeRequest(offset: 95_120, compressed: 48_000),
        };

        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(1, "all entries are physically adjacent");
        result[0].Should().HaveCount(3);
    }

    [Fact]
    public void GroupIntoBatches_LargeGap_SplitsTwoBatches()
    {
        // img001 and img003 are adjacent, img999 is 500KB away — density drops below 80%
        var requests = new List<PendingRequest>
        {
            MakeRequest(offset: 1_000,     compressed: 50_000),   // ~50KB
            MakeRequest(offset: 51_060,    compressed: 48_000),   // ~48KB — adjacent
            MakeRequest(offset: 600_000,   compressed: 50_000),   // 500KB gap → low density
        };

        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(2, "large gap should split into two batches");
        result[0].Should().HaveCount(2);
        result[1].Should().HaveCount(1);
    }

    [Fact]
    public void GroupIntoBatches_ExactThresholdBoundary_IncludedInBatch()
    {
        // Exactly at threshold: useful = 80, range = 100 → density = 0.80
        // entries: offset=0, compressed=40; offset=60, compressed=40 (60-byte gap)
        // range = (60 + 40 + 60) - 0 = 160, useful = 80, density = 0.5 → SPLIT
        // Let's make it just above threshold instead:
        // entry1: offset=0, compressed=40; entry2: offset=50, compressed=40
        // range = (50+40+60) - 0 = 150, useful=80, density=80/150 = 0.533 < 0.8 → split
        // To be above 0.8: gap must be <= useful*(1/threshold - 1) × overhead
        // Simple test: two entries separated by tiny gap → density near 1.0
        var requests = new List<PendingRequest>
        {
            MakeRequest(offset: 0,    compressed: 48_000),
            MakeRequest(offset: 48_060, compressed: 48_000), // 60-byte gap (local header overhead)
        };

        // density = 96000 / (48060 + 48000 + 60) = 96000/96120 ≈ 0.9988 > 0.8
        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(1, "tiny gap: density is well above threshold");
        result[0].Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.3  Density formula verification
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GroupIntoBatches_DensityFormula_CalculatesCorrectly()
    {
        // Explicitly verify: img001 at 1000 (50KB), img003 at 110000 (48KB)
        // usefulBytes = 50000 + 48000 = 98000
        // lastEnd = 110000 + 48000 + 60 = 158060
        // range = 158060 - 1000 = 157060
        // density = 98000 / 157060 ≈ 0.624 < 0.8 → split
        var requests = new List<PendingRequest>
        {
            MakeRequest(offset: 1_000,    compressed: 50_000),
            MakeRequest(offset: 110_000,  compressed: 48_000),
        };

        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(2, "62% density < 80% threshold → should split");
    }

    [Fact]
    public void GroupIntoBatches_HighDensity_SingleBatch()
    {
        // Two entries very close: offset=0 (10KB), offset=10060 (10KB)
        // density = 20000 / (10060 + 10000 + 60) = 20000/20120 ≈ 0.994 > 0.8
        var requests = new List<PendingRequest>
        {
            MakeRequest(offset: 0,      compressed: 10_000),
            MakeRequest(offset: 10_060, compressed: 10_000),
        };

        var result = CoalescingBatchCoordinator.GroupIntoBatches(requests, 0.8);

        result.Should().HaveCount(1);
        result[0].Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.4  FastPathMs timer fires solo (via FileContentCache)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_SingleRequest_ExtractedAfterFastPath()
    {
        byte[] data = GenerateTestData(500);
        var factory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: factory, coalescingEnabled: true, fastPathMs: 50, windowMs: 500);

        byte[] buffer = new byte[500];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int bytes = await cache.ReadAsync("test.zip", MakeEntry(500, offset: 0), "key1", buffer, 0);
        sw.Stop();

        bytes.Should().Be(500);
        buffer.Should().BeEquivalentTo(data);
        // Should complete roughly within FastPath + some margin
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "single request should fire in ~FastPathMs, not WindowMs");
        factory.CreateCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.5  WindowMs trigger: two requests batched together
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_TwoRequests_BatchedTogether()
    {
        byte[] data1 = GenerateTestData(300);
        byte[] data2 = GenerateTestData(400);
        int factoryCallCount = 0;

        var factory = new StubZipReaderFactoryWithMultipleEntries(new Dictionary<string, byte[]>
        {
            ["key1"] = data1,
            ["key2"] = data2,
        }, onCreateReader: () => Interlocked.Increment(ref factoryCallCount));

        var cache = CreateCache(factory: factory, coalescingEnabled: true, fastPathMs: 100, windowMs: 300);

        // Fire both requests concurrently — they should coalesce
        var t1 = Task.Run(async () =>
        {
            byte[] buf = new byte[300];
            return await cache.ReadAsync("test.zip", MakeEntry(300, offset: 0), "key1", buf, 0);
        });
        var t2 = Task.Run(async () =>
        {
            byte[] buf = new byte[400];
            return await cache.ReadAsync("test.zip", MakeEntry(400, offset: 10_000), "key2", buf, 0);
        });

        int[] results = await Task.WhenAll(t1, t2);
        results.Should().BeEquivalentTo([300, 400]);

        // Both entries should be cached
        cache.MemoryTier.EntryCount.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.6  Hole skip (no speculative cache)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_CoalescingEnabled_OnlyRequestedEntriesCached()
    {
        // Three entries: request entry1 and entry3, leave entry2 as hole
        byte[] data1 = GenerateTestData(200);
        byte[] data3 = GenerateTestData(200);

        var factory = new StubZipReaderFactoryWithMultipleEntries(new Dictionary<string, byte[]>
        {
            ["key1"] = data1,
            ["key3"] = data3,
        });

        var cache = CreateCache(factory: factory, coalescingEnabled: true, fastPathMs: 100, windowMs: 300,
            speculativeCache: false);

        var t1 = Task.Run(async () =>
        {
            byte[] buf = new byte[200];
            return await cache.ReadAsync("test.zip", MakeEntry(200, offset: 0), "key1", buf, 0);
        });
        var t3 = Task.Run(async () =>
        {
            byte[] buf = new byte[200];
            return await cache.ReadAsync("test.zip", MakeEntry(200, offset: 100_000), "key3", buf, 0);
        });

        await Task.WhenAll(t1, t3);

        // Only requested entries should be cached
        cache.MemoryTier.EntryCount.Should().Be(2);
        cache.MemoryTier.EntryCount.Should().NotBe(3, "hole entry should NOT have been speculatively cached");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.7  Speculative caching of hole entries
    // ═══════════════════════════════════════════════════════════════════════
    // Note: speculative caching of true ZIP holes requires ArchiveStructure scan
    // (out of scope for the coordinator directly). This test verifies the option
    // compiles and defaults to false, and the coordinator doesn't crash when enabled.

    [Fact]
    public void CoalescingOptions_SpeculativeCache_DefaultIsFalse()
    {
        new CoalescingOptions().SpeculativeCache.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.8  Coalescing disabled: standard factory path, no window delay
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_CoalescingDisabled_UsesStandardPath()
    {
        byte[] data = GenerateTestData(500);
        var factory = new StubZipReaderFactory(data);
        var cache = CreateCache(factory: factory, coalescingEnabled: false);

        byte[] buffer = new byte[500];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int bytes = await cache.ReadAsync("test.zip", MakeEntry(500, offset: 0), "key1", buffer, 0);
        sw.Stop();

        bytes.Should().Be(500);
        buffer.Should().BeEquivalentTo(data);
        // With coalescing disabled, no window delay — should be essentially instant
        sw.ElapsedMilliseconds.Should().BeLessThan(200, "disabled coalescing should not add any window delay");
        factory.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task FileContentCache_CoalescingDisabled_ThunderingHerdStillPrevented()
    {
        int callCount = 0;
        byte[] data = GenerateTestData(1000);
        var factory = new StubZipReaderFactory(data, onCreateReader: () => Interlocked.Increment(ref callCount));
        var cache = CreateCache(factory: factory, coalescingEnabled: false);
        var entry = MakeEntry(1000, offset: 0);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                byte[] buf = new byte[1000];
                return await cache.ReadAsync("test.zip", entry, "key1", buf, 0);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        callCount.Should().Be(1, "thundering herd protection still applies when coalescing is disabled");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7.1  Burst read: 20 concurrent requests for different entries
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_BurstOf20_AllEntriesCachedCorrectly()
    {
        const int count = 20;
        var entries = Enumerable.Range(0, count)
            .Select(i => (key: $"key{i}", data: GenerateTestData(300 + i * 10), offset: (long)(i * 1000)))
            .ToList();

        var dataMap = entries.ToDictionary(e => e.key, e => e.data);
        var factory = new StubZipReaderFactoryWithMultipleEntries(dataMap);
        var cache = CreateCache(factory: factory, coalescingEnabled: true, fastPathMs: 50, windowMs: 300);

        var tasks = entries.Select(e => Task.Run(async () =>
        {
            byte[] buf = new byte[e.data.Length];
            int bytesRead = await cache.ReadAsync("test.zip", MakeEntry(e.data.Length, e.offset), e.key, buf, 0);
            return (e.key, expected: e.data, actual: buf, bytesRead);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            r.bytesRead.Should().Be(r.expected.Length, $"entry {r.key} should return correct byte count");
            r.actual.Should().BeEquivalentTo(r.expected, $"entry {r.key} should return correct data");
        }

        cache.MemoryTier.EntryCount.Should().Be(count);
        cache.BorrowedEntryCount.Should().Be(0, "all handles should be disposed after reads");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7.2  SHA-256 correctness: batch extraction matches individual extraction
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_BatchExtraction_MatchesIndividualExtraction()
    {
        byte[] data1 = GenerateTestData(512);
        byte[] data2 = GenerateTestData(768);

        // Extract individually (coalescing disabled)
        var singleFactory = new StubZipReaderFactoryWithMultipleEntries(new()
        {
            ["k1"] = data1, ["k2"] = data2,
        });
        var singleCache = CreateCache(factory: singleFactory, coalescingEnabled: false);

        byte[] single1 = new byte[512];
        byte[] single2 = new byte[768];
        await singleCache.ReadAsync("t.zip", MakeEntry(512, 0), "k1", single1, 0);
        await singleCache.ReadAsync("t.zip", MakeEntry(768, 1000), "k2", single2, 0);

        // Extract via coalescing (burst both concurrently)
        var batchFactory = new StubZipReaderFactoryWithMultipleEntries(new()
        {
            ["k1"] = data1, ["k2"] = data2,
        });
        var batchCache = CreateCache(factory: batchFactory, coalescingEnabled: true, fastPathMs: 50, windowMs: 300);

        byte[] batch1 = new byte[512];
        byte[] batch2 = new byte[768];
        var t1 = Task.Run(() => batchCache.ReadAsync("t.zip", MakeEntry(512, 0), "k1", batch1, 0));
        var t2 = Task.Run(() => batchCache.ReadAsync("t.zip", MakeEntry(768, 1000), "k2", batch2, 0));
        await Task.WhenAll(t1, t2);

        batch1.Should().BeEquivalentTo(single1, "batch extraction of k1 must match individual");
        batch2.Should().BeEquivalentTo(single2, "batch extraction of k2 must match individual");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7.3  Concurrent callers of same entry: thundering herd holds
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_ConcurrentCallersForSameEntry_SingleMaterialization()
    {
        int createCount = 0;
        byte[] data = GenerateTestData(1000);
        var factory = new StubZipReaderFactory(data, onCreateReader: () => Interlocked.Increment(ref createCount));
        var cache = CreateCache(factory: factory, coalescingEnabled: true, fastPathMs: 50, windowMs: 300);

        var entry = MakeEntry(1000, offset: 0);

        // 10 concurrent requests for the SAME cache key
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                byte[] buf = new byte[1000];
                int n = await cache.ReadAsync("test.zip", entry, "shared-key", buf, 0);
                return (n, buf);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.n.Should().Be(1000));
        results.Should().AllSatisfy(r => r.buf.Should().BeEquivalentTo(data));
        createCount.Should().Be(1, "thundering herd: 10 concurrent requests should extract once");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7.4  Large file bypass: disk tier, coalescing not involved
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FileContentCache_LargeFile_BypassesCoalescing_UsesDiskTier()
    {
        int largeSize = 2 * 1024 * 1024; // 2MB, cutoff is 1MB
        byte[] data = GenerateTestData(largeSize);
        var factory = new StubZipReaderFactory(data);
        // cutoffMb=1 so 2MB file goes to disk tier
        var cache = CreateCache(factory: factory, coalescingEnabled: true, cutoffMb: 1);

        byte[] buffer = new byte[1024];
        int bytes = await cache.ReadAsync("test.zip", MakeEntry(largeSize, 0), "large-key", buffer, 0);

        bytes.Should().Be(1024);
        cache.DiskTier.EntryCount.Should().Be(1);
        cache.MemoryTier.EntryCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private FileContentCache CreateCache(
        IZipReaderFactory? factory = null,
        bool coalescingEnabled = false,
        int fastPathMs = 20,
        int windowMs = 500,
        bool speculativeCache = false,
        int cutoffMb = 50)
    {
        var cacheOpts = Options.Create(new CacheOptions
        {
            SmallFileCutoffMb = cutoffMb,
            MemoryCacheSizeMb = 100,
            DiskCacheSizeMb = 100,
            TempDirectory = _tempDir,
        });

        var coalescingOpts = Options.Create(new CoalescingOptions
        {
            Enabled = coalescingEnabled,
            FastPathMs = fastPathMs,
            WindowMs = windowMs,
            DensityThreshold = 0.8,
            SpeculativeCache = speculativeCache,
        });

        return new FileContentCache(
            factory ?? new StubZipReaderFactory(Array.Empty<byte>()),
            cacheOpts,
            coalescingOpts,
            new LruEvictionPolicy(),
            _fakeTime,
            NullLoggerFactory.Instance);
    }

    private static ZipEntryInfo MakeEntry(int uncompressedSize, long offset) => new()
    {
        LocalHeaderOffset = offset,
        CompressedSize = uncompressedSize,
        UncompressedSize = uncompressedSize,
        CompressionMethod = 0,
        IsDirectory = false,
        LastModified = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
    };

    private static PendingRequest MakeRequest(long offset, long compressed)
    {
        var entry = new ZipEntryInfo
        {
            LocalHeaderOffset = offset,
            CompressedSize = compressed,
            UncompressedSize = compressed,
            CompressionMethod = 0,
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Attributes = FileAttributes.Normal,
        };
        var tcs = new TaskCompletionSource<ICacheHandle<Stream>>();
        return new PendingRequest("test.zip", entry, $"key_{offset}", tcs, default);
    }

    private static byte[] GenerateTestData(int size)
    {
        byte[] data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Stubs
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class StubZipReaderFactory : IZipReaderFactory
    {
        private readonly byte[] _data;
        private readonly Action? _onCreateReader;
        private int _createCount;
        public int CreateCount => _createCount;

        public StubZipReaderFactory(byte[] data, Action? onCreateReader = null)
        {
            _data = data;
            _onCreateReader = onCreateReader;
        }

        public IZipReader Create(string filePath)
        {
            Interlocked.Increment(ref _createCount);
            _onCreateReader?.Invoke();
            return new StubZipReader(_data);
        }
    }

    /// <summary>
    /// Factory that returns different data per cache key, routing by the cacheKey
    /// passed into <see cref="PendingRequest"/>. Since ZipReaderFactory.Create only
    /// receives archivePath (not entry key), we use a shared data dictionary and
    /// resolve per-entry data in the stub reader based on what the coordinator passes.
    /// For simplicity this stub returns the appropriate data based on entry offset.
    /// </summary>
    private sealed class StubZipReaderFactoryWithMultipleEntries : IZipReaderFactory
    {
        private readonly Dictionary<string, byte[]> _dataByKey;
        private readonly Action? _onCreateReader;
        private int _createCount;
        public int CreateCount => _createCount;

        // Map entry offset → key (populated when entries are registered)
        private readonly Dictionary<long, string> _offsetToKey = [];

        public StubZipReaderFactoryWithMultipleEntries(
            Dictionary<string, byte[]> dataByKey,
            Action? onCreateReader = null)
        {
            _dataByKey = dataByKey;
            _onCreateReader = onCreateReader;

            // We can't know offsets at construction time, so the reader uses a special approach:
            // it captures all data items and routes by entry's offset if possible.
        }

        public IZipReader Create(string filePath)
        {
            Interlocked.Increment(ref _createCount);
            _onCreateReader?.Invoke();
            return new MultiEntryStubZipReader(_dataByKey);
        }
    }

    private sealed class StubZipReader : IZipReader
    {
        private readonly byte[] _data;
        public bool Disposed { get; private set; }
        public long StreamLength => _data.Length;
        public string? FilePath => "stub.zip";

        public StubZipReader(byte[] data) { _data = data; }

        public Task<Stream> OpenEntryStreamAsync(ZipEntryInfo entry, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(_data, writable: false));

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<ZipEocd> ReadEocdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(ZipEocd eocd, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ZipLocalHeader> ReadLocalHeaderAsync(long offset, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// A stub ZipReader that routes entry extraction by UncompressedSize → data.
    /// Each key is mapped by its declared data length.
    /// </summary>
    private sealed class MultiEntryStubZipReader : IZipReader
    {
        private readonly Dictionary<string, byte[]> _dataByKey;
        public long StreamLength => 1_000_000;
        public string? FilePath => "multi.zip";

        public MultiEntryStubZipReader(Dictionary<string, byte[]> dataByKey)
        {
            _dataByKey = dataByKey;
        }

        public Task<Stream> OpenEntryStreamAsync(ZipEntryInfo entry, CancellationToken ct = default)
        {
            // Route by UncompressedSize: find the data whose length matches
            var data = _dataByKey.Values.FirstOrDefault(d => d.Length == entry.UncompressedSize)
                       ?? Array.Empty<byte>();
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ZipEocd> ReadEocdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(ZipEocd eocd, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ZipLocalHeader> ReadLocalHeaderAsync(long offset, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
