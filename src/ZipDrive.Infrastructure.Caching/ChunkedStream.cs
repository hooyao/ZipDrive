using System.Buffers;
using System.Diagnostics;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Read-only stream that maps reads to chunks in a <see cref="ChunkedFileEntry"/>.
/// Blocks on unextracted regions via <see cref="EnsureChunkReadyAsync"/>.
/// Each borrower gets an independent instance with its own <see cref="FileStream"/> and position.
/// </summary>
internal sealed class ChunkedStream : Stream
{
    private readonly ChunkedFileEntry _entry;
    private readonly FileStream _readStream;
    private long _position;
    private bool _disposed;

    public ChunkedStream(ChunkedFileEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _readStream = new FileStream(
            entry.BackingFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _entry.FileSize;

    public override long Position
    {
        get => _position;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
            _position = value;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_entry.ChunkCount == 0)
            return 0;

        long remaining = _entry.FileSize - _position;
        if (remaining <= 0)
            return 0;

        int bytesToRead = (int)Math.Min(buffer.Length, remaining);
        int totalRead = 0;

        while (totalRead < bytesToRead)
        {
            long currentOffset = _position + totalRead;
            int chunkIndex = _entry.GetChunkIndex(currentOffset);

            // ════════════════════════════════════════════════════════════
            // CRITICAL: Ensure chunk is ready before reading.
            // Without this check, we'd read zeros from sparse file regions.
            // ════════════════════════════════════════════════════════════
            await EnsureChunkReadyAsync(chunkIndex, cancellationToken).ConfigureAwait(false);

            // Calculate how many bytes we can read from this chunk
            long chunkStart = _entry.GetChunkOffset(chunkIndex);
            int chunkLength = _entry.GetChunkLength(chunkIndex);
            int offsetInChunk = (int)(currentOffset - chunkStart);
            int bytesAvailableInChunk = chunkLength - offsetInChunk;
            int bytesFromThisChunk = Math.Min(bytesToRead - totalRead, bytesAvailableInChunk);

            // Read from backing file
            _readStream.Position = currentOffset;
            int read = await _readStream.ReadAsync(
                buffer.Slice(totalRead, bytesFromThisChunk),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
                break;

            totalRead += read;
        }

        _position += totalRead;
        return totalRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // DokanNet may call Stream.Read() synchronously.
        // Safe: extraction uses IOCP threads (async I/O), DokanNet has its own thread pool.
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }

    public override int Read(Span<byte> buffer)
    {
        // Span overload cannot use async path directly — rent from pool
        byte[] temp = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = Read(temp, 0, buffer.Length);
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _entry.FileSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Resulting position cannot be negative.");

        _position = newPosition;
        return _position;
    }

    /// <summary>
    /// Ensures the chunk at the given index is extracted and ready for reading.
    /// Fast path: lock-free BitArray check. Slow path: await TCS.
    /// </summary>
    internal async ValueTask EnsureChunkReadyAsync(int chunkIndex, CancellationToken cancellationToken)
    {
        if (_entry.IsChunkReady(chunkIndex))
            return;

        long startTimestamp = Stopwatch.GetTimestamp();

        await _entry.WaitForChunkAsync(chunkIndex, cancellationToken).ConfigureAwait(false);

        double waitMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        CacheTelemetry.ChunkWaits.Add(1);
        CacheTelemetry.ChunkWaitDuration.Record(waitMs);
    }

    public override void Flush()
    {
        // No-op for read-only stream
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("ChunkedStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("ChunkedStream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _readStream.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _readStream.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
