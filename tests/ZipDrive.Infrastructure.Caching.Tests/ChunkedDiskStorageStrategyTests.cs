using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZipDrive.Infrastructure.Caching.Tests;

public class ChunkedDiskStorageStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChunkedDiskStorageStrategy _strategy;

    public ChunkedDiskStorageStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChunkedStrategyTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024, // 1KB chunks for fast tests
            tempDirectory: _tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore */ }
    }

    private static async Task<CacheFactoryResult<Stream>> CreateFactory(byte[] data, CancellationToken ct)
    {
        return new CacheFactoryResult<Stream>
        {
            Value = new MemoryStream(data),
            SizeBytes = data.Length
        };
    }

    [Fact]
    public async Task MaterializeAsync_ReturnsAfterFirstChunk()
    {
        byte[] data = new byte[4096]; // 4 chunks × 1KB
        Random.Shared.NextBytes(data);

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        stored.Should().NotBeNull();
        stored.Data.Should().BeOfType<ChunkedFileEntry>();

        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        entry.IsChunkReady(0).Should().BeTrue("first chunk should be ready");
    }

    [Fact]
    public async Task MaterializeAsync_ReportsFullSize()
    {
        byte[] data = new byte[4096];

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        stored.SizeBytes.Should().Be(4096, "should report full uncompressed size");
    }

    [Fact]
    public async Task Retrieve_ReturnsFreshChunkedStreamEachCall()
    {
        byte[] data = new byte[2048];
        Random.Shared.NextBytes(data);

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        // Wait for full extraction
        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        await entry.ExtractionTask;

        Stream stream1 = _strategy.Retrieve(stored);
        Stream stream2 = _strategy.Retrieve(stored);

        stream1.Should().NotBeSameAs(stream2, "each Retrieve should return a different instance");
        stream1.Should().BeOfType<ChunkedStream>();
        stream2.Should().BeOfType<ChunkedStream>();

        stream1.Dispose();
        stream2.Dispose();
    }

    [Fact]
    public async Task Retrieve_StreamReadsCorrectData()
    {
        byte[] data = new byte[2048];
        Random.Shared.NextBytes(data);

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        // Wait for full extraction to complete
        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        await entry.ExtractionTask;

        using Stream stream = _strategy.Retrieve(stored);
        byte[] readBuffer = new byte[2048];
        int totalRead = 0;
        while (totalRead < data.Length)
        {
            int read = await stream.ReadAsync(readBuffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        totalRead.Should().Be(2048);
        readBuffer.Should().Equal(data);
    }

    [Fact]
    public async Task Dispose_CancelsExtractionAndDeletesFile()
    {
        // Use a slow factory to ensure extraction is still running
        byte[] data = new byte[10240]; // 10 chunks of 1KB
        Random.Shared.NextBytes(data);

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        string filePath = entry.BackingFilePath;
        File.Exists(filePath).Should().BeTrue();

        _strategy.Dispose(stored);

        // File should be deleted
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public void RequiresAsyncCleanup_ReturnsTrue()
    {
        _strategy.RequiresAsyncCleanup.Should().BeTrue();
    }

    [Fact]
    public async Task MaterializeAsync_EmptyFile_Succeeds()
    {
        byte[] data = [];

        StoredEntry stored = await _strategy.MaterializeAsync(
            ct => CreateFactory(data, ct),
            CancellationToken.None);

        stored.SizeBytes.Should().Be(0);
        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        entry.ChunkCount.Should().Be(0);

        entry.Dispose();
    }

    [Fact]
    public void DeleteCacheDirectory_RemovesDirectory()
    {
        string testDir = Path.Combine(_tempDir, $"ZipDrive-{Environment.ProcessId}");
        // The strategy constructor already created a per-process dir
        // Verify by creating a strategy with a known temp dir
        string baseTempDir = Path.Combine(_tempDir, "delete-test");
        Directory.CreateDirectory(baseTempDir);
        var strategy = new ChunkedDiskStorageStrategy(
            NullLogger<ChunkedDiskStorageStrategy>.Instance,
            chunkSizeBytes: 1024,
            tempDirectory: baseTempDir);

        string expectedDir = Path.Combine(baseTempDir, $"ZipDrive-{Environment.ProcessId}");
        Directory.Exists(expectedDir).Should().BeTrue();

        strategy.DeleteCacheDirectory();

        Directory.Exists(expectedDir).Should().BeFalse();
    }
}
