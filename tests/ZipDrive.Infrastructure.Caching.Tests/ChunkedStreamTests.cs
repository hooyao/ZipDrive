using FluentAssertions;

namespace ZipDrive.Infrastructure.Caching.Tests;

public class ChunkedStreamTests : IDisposable
{
    private readonly string _tempDir;
    private const int ChunkSize = 10; // 10 bytes for easy test math

    public ChunkedStreamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ChunkedStreamTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* ignore cleanup errors */ }
    }

    /// <summary>
    /// Creates a ChunkedFileEntry backed by a real file with the given data,
    /// marking all chunks as ready.
    /// </summary>
    private ChunkedFileEntry CreateReadyEntry(byte[] data)
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.chunked");
        File.WriteAllBytes(path, data);
        var entry = new ChunkedFileEntry(path, data.Length, ChunkSize);
        for (int i = 0; i < entry.ChunkCount; i++)
            entry.MarkChunkReady(i, entry.GetChunkLength(i));
        return entry;
    }

    /// <summary>
    /// Creates a ChunkedFileEntry backed by a real file with the given data,
    /// but does NOT mark any chunks as ready (simulates in-progress extraction).
    /// </summary>
    private ChunkedFileEntry CreatePendingEntry(byte[] data)
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.chunked");
        File.WriteAllBytes(path, data);
        return new ChunkedFileEntry(path, data.Length, ChunkSize);
    }

    private static byte[] CreateTestData(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i % 256);
        return data;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Read From Ready Chunks
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_ReadyChunk_ReturnsDataInstantly()
    {
        byte[] data = CreateTestData(25); // 3 chunks: 10, 10, 5
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer);

        read.Should().Be(10);
        buffer.Should().Equal(data[..10]);
    }

    [Fact]
    public async Task ReadAsync_ReadEntireFile_ReturnsAllData()
    {
        byte[] data = CreateTestData(25);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        byte[] buffer = new byte[25];
        int totalRead = 0;
        while (totalRead < 25)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        totalRead.Should().Be(25);
        buffer.Should().Equal(data);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Read From Pending Chunks (waits for signal)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_PendingChunk_WaitsForSignalThenReturnsData()
    {
        byte[] data = CreateTestData(20);
        using var entry = CreatePendingEntry(data);
        using var stream = new ChunkedStream(entry);

        // Start read on chunk 0 (pending)
        byte[] buffer = new byte[10];
        var readTask = stream.ReadAsync(buffer).AsTask();

        readTask.IsCompleted.Should().BeFalse();

        // Signal chunk 0
        entry.MarkChunkReady(0, 10);

        int read = await readTask;
        read.Should().Be(10);
        buffer.Should().Equal(data[..10]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cross-Chunk Boundary Reads
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_CrossChunkBoundary_ReadsBothChunks()
    {
        byte[] data = CreateTestData(30); // 3 chunks of 10
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        // Seek to 5 bytes before chunk boundary
        stream.Position = 5;
        byte[] buffer = new byte[10]; // Read 10 bytes spanning chunk 0 and 1
        int read = await stream.ReadAsync(buffer);

        read.Should().Be(10);
        buffer.Should().Equal(data[5..15]);
    }

    [Fact]
    public async Task ReadAsync_SpanThreeChunks_ReadsAll()
    {
        byte[] data = CreateTestData(30); // 3 chunks of 10
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        // Read all 30 bytes in one call spanning all 3 chunks
        stream.Position = 0;
        byte[] buffer = new byte[30];
        int totalRead = 0;
        while (totalRead < 30)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        totalRead.Should().Be(30);
        buffer.Should().Equal(data);
    }

    // ═══════════════════════════════════════════════════════════════════
    // EOF Handling
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_AtEof_ReturnsZero()
    {
        byte[] data = CreateTestData(10);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.Position = 10; // At EOF
        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer);
        read.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_BeyondEof_ReturnsZero()
    {
        byte[] data = CreateTestData(10);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.Position = 100; // Way beyond EOF
        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer);
        read.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_ReturnsZero()
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.chunked");
        File.WriteAllBytes(path, []);
        using var entry = new ChunkedFileEntry(path, 0, ChunkSize);
        using var stream = new ChunkedStream(entry);

        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer);
        read.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Seek and Position
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Position_SetAndGet()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.Position = 15;
        stream.Position.Should().Be(15);
    }

    [Fact]
    public void Seek_Begin()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        long pos = stream.Seek(15, SeekOrigin.Begin);
        pos.Should().Be(15);
        stream.Position.Should().Be(15);
    }

    [Fact]
    public void Seek_Current()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.Position = 10;
        long pos = stream.Seek(5, SeekOrigin.Current);
        pos.Should().Be(15);
    }

    [Fact]
    public void Seek_End()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        long pos = stream.Seek(-5, SeekOrigin.End);
        pos.Should().Be(25);
    }

    [Fact]
    public void Seek_NegativeResult_Throws()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        Action act = () => stream.Seek(-1, SeekOrigin.Begin);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadAsync_AfterSeek_ReadsFromCorrectOffset()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.Position = 15;
        byte[] buffer = new byte[5];
        int read = await stream.ReadAsync(buffer);

        read.Should().Be(5);
        buffer.Should().Equal(data[15..20]);
        stream.Position.Should().Be(20);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Concurrent Readers — Independent Positions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentReaders_IndependentPositions()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream1 = new ChunkedStream(entry);
        using var stream2 = new ChunkedStream(entry);

        stream1.Position = 0;
        stream2.Position = 20;

        byte[] buf1 = new byte[10];
        byte[] buf2 = new byte[10];
        int read1 = await stream1.ReadAsync(buf1);
        int read2 = await stream2.ReadAsync(buf2);

        read1.Should().Be(10);
        read2.Should().Be(10);
        buf1.Should().Equal(data[..10]);
        buf2.Should().Equal(data[20..30]);

        stream1.Position.Should().Be(10);
        stream2.Position.Should().Be(30);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cancelled / Failed Extraction
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAsync_CancelledExtraction_Throws()
    {
        byte[] data = CreateTestData(20);
        using var entry = CreatePendingEntry(data);
        using var stream = new ChunkedStream(entry);

        var readTask = stream.ReadAsync(new byte[10]).AsTask();

        entry.CancelPendingChunks();

        Func<Task> act = async () => await readTask;
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ReadAsync_FailedExtraction_PropagatesException()
    {
        byte[] data = CreateTestData(20);
        using var entry = CreatePendingEntry(data);
        using var stream = new ChunkedStream(entry);

        var readTask = stream.ReadAsync(new byte[10]).AsTask();

        entry.FailPendingChunks(new InvalidOperationException("Corrupt"));

        Func<Task> act = async () => await readTask;
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Corrupt");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Sync Read Fallback
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Read_SyncFallback_Works()
    {
        byte[] data = CreateTestData(20);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        byte[] buffer = new byte[10];
        int read = stream.Read(buffer, 0, 10);

        read.Should().Be(10);
        buffer.Should().Equal(data[..10]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Stream Properties
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void StreamProperties_Correct()
    {
        byte[] data = CreateTestData(30);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        stream.CanRead.Should().BeTrue();
        stream.CanSeek.Should().BeTrue();
        stream.CanWrite.Should().BeFalse();
        stream.Length.Should().Be(30);
    }

    [Fact]
    public void Write_Throws()
    {
        byte[] data = CreateTestData(10);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        Action act = () => stream.Write(new byte[1], 0, 1);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void SetLength_Throws()
    {
        byte[] data = CreateTestData(10);
        using var entry = CreateReadyEntry(data);
        using var stream = new ChunkedStream(entry);

        Action act = () => stream.SetLength(100);
        act.Should().Throw<NotSupportedException>();
    }
}
