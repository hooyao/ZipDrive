using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Tracks chunk state for an incrementally-extracted disk cache entry.
/// Owns the sparse backing file, background extraction task, and cancellation.
/// </summary>
internal sealed class ChunkedFileEntry : IDisposable
{
    // === Immutable Configuration ===
    public string BackingFilePath { get; }
    public long FileSize { get; }
    public int ChunkSize { get; }
    public int ChunkCount { get; }

    // === Chunk Tracking (thread-safe via Volatile) ===
    private readonly int[] _chunkReady; // 0 = not ready, 1 = ready (Volatile read/write)
    private readonly TaskCompletionSource<bool>[] _chunkCompletions;

    // === Extraction Lifecycle ===
    public CancellationTokenSource ExtractionCts { get; }
    public Task ExtractionTask { get; internal set; } = Task.CompletedTask;

    // === Telemetry ===
    private long _bytesExtracted;
    private int _chunksCompleted;
    public long BytesExtracted => Volatile.Read(ref _bytesExtracted);
    public int ChunksCompleted => Volatile.Read(ref _chunksCompleted);
    public double ExtractionProgress => FileSize == 0 ? 1.0 : (double)BytesExtracted / FileSize;

    private bool _disposed;

    public ChunkedFileEntry(string backingFilePath, long fileSize, int chunkSize)
    {
        if (fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize), "File size cannot be negative.");
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");

        BackingFilePath = backingFilePath ?? throw new ArgumentNullException(nameof(backingFilePath));
        FileSize = fileSize;
        ChunkSize = chunkSize;
        ChunkCount = fileSize == 0 ? 0 : (int)((fileSize + chunkSize - 1) / chunkSize);

        _chunkReady = new int[ChunkCount];
        _chunkCompletions = new TaskCompletionSource<bool>[ChunkCount];
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkCompletions[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        ExtractionCts = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets the chunk index containing the given byte offset.
    /// </summary>
    public int GetChunkIndex(long offset)
        => (int)(offset / ChunkSize);

    /// <summary>
    /// Gets the byte offset in the backing file where the given chunk starts.
    /// </summary>
    public long GetChunkOffset(int chunkIndex)
        => (long)chunkIndex * ChunkSize;

    /// <summary>
    /// Gets the byte length of the given chunk (last chunk may be smaller).
    /// </summary>
    public int GetChunkLength(int chunkIndex)
        => chunkIndex < ChunkCount - 1
            ? ChunkSize
            : (int)(FileSize - (long)chunkIndex * ChunkSize);

    /// <summary>
    /// Returns true if the chunk at the given index is extracted and ready.
    /// Thread-safe via Volatile.Read.
    /// </summary>
    public bool IsChunkReady(int chunkIndex)
        => Volatile.Read(ref _chunkReady[chunkIndex]) == 1;

    /// <summary>
    /// Waits for the chunk at the given index to be extracted.
    /// Returns immediately if already ready.
    /// </summary>
    public Task WaitForChunkAsync(int chunkIndex, CancellationToken cancellationToken)
    {
        Task completionTask = _chunkCompletions[chunkIndex].Task;
        if (completionTask.IsCompleted)
            return completionTask;

        // WaitAsync cancels only the waiter, not the underlying TCS —
        // other readers and the extraction task are unaffected.
        return completionTask.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Marks a chunk as complete. Called by the extraction task after writing chunk data.
    /// Order: write data → set ready flag → increment counters → signal TCS.
    /// </summary>
    internal void MarkChunkReady(int chunkIndex, int bytesWritten)
    {
        Volatile.Write(ref _chunkReady[chunkIndex], 1);
        Interlocked.Add(ref _bytesExtracted, bytesWritten);
        Interlocked.Increment(ref _chunksCompleted);
        _chunkCompletions[chunkIndex].TrySetResult(true);

        CacheTelemetry.ChunksExtracted.Add(1);
    }

    /// <summary>
    /// Signals all pending chunks as cancelled.
    /// </summary>
    internal void CancelPendingChunks()
    {
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkCompletions[i].TrySetCanceled();
        }
    }

    /// <summary>
    /// Signals all pending chunks with an exception.
    /// </summary>
    internal void FailPendingChunks(Exception exception)
    {
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkCompletions[i].TrySetException(exception);
        }
    }

    /// <summary>
    /// Runs the background extraction, writing chunks sequentially to the backing file.
    /// </summary>
    internal async Task ExtractAsync(
        Stream decompressedStream,
        Func<ValueTask>? onDisposed,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            await using FileStream fs = new FileStream(
                BackingFilePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            for (int i = 0; i < ChunkCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long chunkStartTimestamp = Stopwatch.GetTimestamp();
                int chunkLength = GetChunkLength(i);
                int totalRead = 0;

                while (totalRead < chunkLength)
                {
                    int read = await decompressedStream.ReadAsync(
                        buffer.AsMemory(totalRead, chunkLength - totalRead),
                        cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                        break;

                    totalRead += read;
                }

                // Premature EOF: stream ended before expected chunk length.
                // Treat as data corruption — writing a short chunk would leave
                // zero-filled gaps in the sparse file that silently corrupt reads.
                if (totalRead < chunkLength)
                    throw new InvalidDataException(
                        $"Decompressed stream ended prematurely at chunk {i}: " +
                        $"expected {chunkLength} bytes, got {totalRead}. " +
                        $"Archive may be truncated or corrupt.");

                fs.Position = GetChunkOffset(i);
                await fs.WriteAsync(buffer.AsMemory(0, totalRead), cancellationToken)
                    .ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

                MarkChunkReady(i, totalRead);

                double chunkMs = Stopwatch.GetElapsedTime(chunkStartTimestamp).TotalMilliseconds;
                CacheTelemetry.ChunkExtractionDuration.Record(chunkMs);

                logger?.LogDebug(
                    "ChunkedFileEntry: Chunk {ChunkIndex}/{ChunkCount} extracted ({Progress:P0} complete)",
                    i + 1, ChunkCount, ExtractionProgress);
            }
        }
        catch (OperationCanceledException)
        {
            CancelPendingChunks();
            logger?.LogWarning(
                "ChunkedFileEntry: Extraction cancelled ({ChunksCompleted}/{ChunkCount} chunks extracted)",
                ChunksCompleted, ChunkCount);
        }
        catch (Exception ex)
        {
            FailPendingChunks(ex);
            logger?.LogError(ex,
                "ChunkedFileEntry: Extraction failed ({ChunksCompleted}/{ChunkCount} chunks extracted)",
                ChunksCompleted, ChunkCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            if (decompressedStream is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (decompressedStream is IDisposable disposable)
                disposable.Dispose();

            if (onDisposed is not null)
                await onDisposed().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ExtractionCts.Cancel();

        // Best-effort wait for extraction task to observe cancellation
        try
        {
            ExtractionTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Swallow — task may have already faulted or been cancelled
        }

        ExtractionCts.Dispose();
        CancelPendingChunks();

        try
        {
            if (File.Exists(BackingFilePath))
                File.Delete(BackingFilePath);
        }
        catch
        {
            // Non-fatal — file may still be locked briefly, or the cache directory
            // may have been deleted during shutdown (DirectoryNotFoundException).
        }
    }
}
