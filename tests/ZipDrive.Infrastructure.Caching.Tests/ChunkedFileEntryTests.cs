using FluentAssertions;

namespace ZipDrive.Infrastructure.Caching.Tests;

public class ChunkedFileEntryTests : IDisposable
{
    private readonly string _tempDir;

    public ChunkedFileEntryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChunkedFileEntryTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore cleanup errors */ }
    }

    private string CreateTempFile(long size = 0)
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.chunked");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (size > 0)
            fs.SetLength(size);
        return path;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Chunk Index Calculations
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(100, 10, 10)]       // 100 bytes, 10-byte chunks = 10 chunks
    [InlineData(105, 10, 11)]       // 105 bytes, 10-byte chunks = 11 chunks (last partial)
    [InlineData(10, 10, 1)]         // Exact fit = 1 chunk
    [InlineData(1, 10, 1)]          // Smaller than chunk = 1 chunk
    [InlineData(0, 10, 0)]          // Empty file = 0 chunks
    public void ChunkCount_CalculatedCorrectly(long fileSize, int chunkSize, int expectedChunks)
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, fileSize, chunkSize);
        entry.ChunkCount.Should().Be(expectedChunks);
    }

    [Theory]
    [InlineData(0, 10, 0)]     // offset 0 → chunk 0
    [InlineData(9, 10, 0)]     // offset 9 → chunk 0
    [InlineData(10, 10, 1)]    // offset 10 → chunk 1
    [InlineData(25, 10, 2)]    // offset 25 → chunk 2
    public void GetChunkIndex_ReturnsCorrectIndex(long offset, int chunkSize, int expectedIndex)
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, chunkSize);
        entry.GetChunkIndex(offset).Should().Be(expectedIndex);
    }

    [Theory]
    [InlineData(0, 10, 0)]      // chunk 0 → offset 0
    [InlineData(1, 10, 10)]     // chunk 1 → offset 10
    [InlineData(5, 10, 50)]     // chunk 5 → offset 50
    public void GetChunkOffset_ReturnsCorrectOffset(int chunkIndex, int chunkSize, long expectedOffset)
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, chunkSize);
        entry.GetChunkOffset(chunkIndex).Should().Be(expectedOffset);
    }

    [Fact]
    public void GetChunkLength_MiddleChunk_ReturnsFullChunkSize()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 105, 10);
        entry.GetChunkLength(0).Should().Be(10);
        entry.GetChunkLength(5).Should().Be(10);
    }

    [Fact]
    public void GetChunkLength_LastChunk_ReturnsPartialSize()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 105, 10);
        // 105 bytes / 10 = 10 full chunks + 1 partial (5 bytes)
        entry.GetChunkLength(10).Should().Be(5);
    }

    [Fact]
    public void GetChunkLength_ExactFit_LastChunkIsFull()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);
        entry.GetChunkLength(9).Should().Be(10); // Last chunk is full
    }

    // ═══════════════════════════════════════════════════════════════════
    // TCS Signaling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void IsChunkReady_BeforeSignal_ReturnsFalse()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);
        entry.IsChunkReady(0).Should().BeFalse();
    }

    [Fact]
    public void MarkChunkReady_SetsReadyAndSignalsTcs()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        entry.MarkChunkReady(0, 10);

        entry.IsChunkReady(0).Should().BeTrue();
        entry.BytesExtracted.Should().Be(10);
    }

    [Fact]
    public async Task WaitForChunkAsync_ReadyChunk_ReturnsImmediately()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        entry.MarkChunkReady(0, 10);

        // Should complete immediately — no timeout needed
        await entry.WaitForChunkAsync(0, CancellationToken.None);
    }

    [Fact]
    public async Task WaitForChunkAsync_PendingChunk_WaitsForSignal()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        var waitTask = entry.WaitForChunkAsync(0, CancellationToken.None);
        waitTask.IsCompleted.Should().BeFalse();

        // Signal chunk ready
        entry.MarkChunkReady(0, 10);

        await waitTask; // Should complete now
    }

    [Fact]
    public async Task WaitForChunkAsync_ChunksSignaledInOrder()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 30, 10);

        var wait0 = entry.WaitForChunkAsync(0, CancellationToken.None);
        var wait1 = entry.WaitForChunkAsync(1, CancellationToken.None);
        var wait2 = entry.WaitForChunkAsync(2, CancellationToken.None);

        entry.MarkChunkReady(0, 10);
        await wait0;
        wait1.IsCompleted.Should().BeFalse();
        wait2.IsCompleted.Should().BeFalse();

        entry.MarkChunkReady(1, 10);
        await wait1;
        wait2.IsCompleted.Should().BeFalse();

        entry.MarkChunkReady(2, 10);
        await wait2;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cancellation and Error Propagation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelPendingChunks_WaitersGetCancelled()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        var waitTask = entry.WaitForChunkAsync(5, CancellationToken.None);

        entry.CancelPendingChunks();

        Func<Task> act = async () => await waitTask;
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task FailPendingChunks_WaitersGetException()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        var waitTask = entry.WaitForChunkAsync(5, CancellationToken.None);
        var exception = new InvalidOperationException("Corrupt ZIP data");

        entry.FailPendingChunks(exception);

        Func<Task> act = async () => await waitTask;
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Corrupt ZIP data");
    }

    [Fact]
    public async Task WaitForChunkAsync_CallerCancellation_ThrowsWithoutAffectingExtraction()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        using var cts = new CancellationTokenSource();
        var waitTask = entry.WaitForChunkAsync(5, cts.Token);

        cts.Cancel();

        Func<Task> act = async () => await waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Chunk TCS should NOT be cancelled — other readers may still need it
        var anotherWait = entry.WaitForChunkAsync(5, CancellationToken.None);
        anotherWait.IsCompleted.Should().BeFalse(); // Still pending, not cancelled
    }

    // ═══════════════════════════════════════════════════════════════════
    // Extraction Progress
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractionProgress_TracksCorrectly()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 100, 10);

        entry.ExtractionProgress.Should().Be(0.0);

        entry.MarkChunkReady(0, 10);
        entry.ExtractionProgress.Should().BeApproximately(0.1, 0.001);

        entry.MarkChunkReady(1, 10);
        entry.ExtractionProgress.Should().BeApproximately(0.2, 0.001);
    }

    [Fact]
    public void ExtractionProgress_EmptyFile_ReturnsOne()
    {
        string path = CreateTempFile();
        using var entry = new ChunkedFileEntry(path, 0, 10);
        entry.ExtractionProgress.Should().Be(1.0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Dispose Safety
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        string path = CreateTempFile(100);
        var entry = new ChunkedFileEntry(path, 100, 10);

        entry.Dispose();
        entry.Dispose(); // Should not throw
    }

    [Fact]
    public void Dispose_DeletesBackingFile()
    {
        string path = CreateTempFile(100);
        var entry = new ChunkedFileEntry(path, 100, 10);
        File.Exists(path).Should().BeTrue();

        entry.Dispose();
        File.Exists(path).Should().BeFalse(
            "the entry owns its backing file's lifetime (RAII) and deletes it on Dispose");
    }

    [Fact]
    public void Dispose_WhenBackingFileAlreadyGone_DoesNotThrow()
    {
        string path = CreateTempFile(100);
        var entry = new ChunkedFileEntry(path, 100, 10);

        // Simulate the file already being removed (e.g. cache directory deleted
        // during shutdown) — Dispose must swallow this and not throw.
        File.Delete(path);

        Action act = () => entry.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CancelsPendingChunks()
    {
        string path = CreateTempFile(100);
        var entry = new ChunkedFileEntry(path, 100, 10);

        var waitTask = entry.WaitForChunkAsync(5, CancellationToken.None);

        entry.Dispose();

        waitTask.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_WhileWriterHandleOpen_ClosesHandleAndDeletesFile()
    {
        // Regression guard for the WinFsp Ctrl+C temp-file leak: while a background
        // extraction is actively writing (its writer FileStream is open on the backing
        // file), Dispose() must close that handle DIRECTLY and delete the file — without
        // waiting for the extraction task to observe cancellation and unwind.
        long fileSize = 64 * 1024; // 64 chunks of 1KB
        const int chunkSize = 1024;
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.chunked");
        // Pre-create the backing file the way the strategy does (extraction opens it for write).
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            fs.SetLength(fileSize);

        var entry = new ChunkedFileEntry(path, fileSize, chunkSize);

        // Slow source (5ms/read) so extraction is guaranteed still mid-flight at Dispose:
        // fully draining would take 64 * 5ms ≈ 320ms.
        var source = new SlowReadStream(CreateSequentialData((int)fileSize), delayMsPerRead: 5);
        var extraction = entry.ExtractAsync(source, onDisposed: null, logger: null, entry.ExtractionCts.Token);

        // Wait until the writer handle is open and at least the first chunk has been written.
        await entry.WaitForChunkAsync(0, CancellationToken.None);
        entry.ChunksCompleted.Should().BeLessThan(entry.ChunkCount, "extraction should still be running");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        entry.Dispose();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "Dispose closes the writer handle directly, it must not wait for extraction to unwind");
        File.Exists(path).Should().BeFalse(
            "the backing file must be deleted even though the writer handle was open mid-extraction");

        // Let the extraction task settle (it observes the closed handle / cancellation).
        try { await extraction; } catch { /* aborted by dispose — expected */ }
    }

    private static byte[] CreateSequentialData(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i % 256);
        return data;
    }

    /// <summary>Stream that delays each read to keep extraction mid-flight during the test.</summary>
    private sealed class SlowReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _delayMsPerRead;

        public SlowReadStream(byte[] data, int delayMsPerRead)
        {
            _inner = new MemoryStream(data);
            _delayMsPerRead = delayMsPerRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(_delayMsPerRead, ct);
            return await _inner.ReadAsync(buffer, ct);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(_delayMsPerRead, ct);
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(_delayMsPerRead);
            return _inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Constructor Validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_NegativeFileSize_Throws()
    {
        string path = CreateTempFile();
        Action act = () => new ChunkedFileEntry(path, -1, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ZeroChunkSize_Throws()
    {
        string path = CreateTempFile();
        Action act = () => new ChunkedFileEntry(path, 100, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        Action act = () => new ChunkedFileEntry(null!, 100, 10);
        act.Should().Throw<ArgumentNullException>();
    }
}
