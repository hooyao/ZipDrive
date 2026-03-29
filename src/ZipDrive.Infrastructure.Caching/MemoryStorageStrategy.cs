using System.Buffers;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as pooled byte[] in memory.
/// Use for small files (&lt; 50MB by default).
/// Internal storage: PooledBuffer (rented byte[] + actual data length)
/// Returns: Stream (MemoryStream wrapping the rented byte[], bounded to actual length)
///
/// Uses a dedicated ArrayPool (not Shared) to control retention. The pool is bounded
/// to avoid the unbounded thread-local retention behavior of ArrayPool.Shared under
/// high concurrency.
/// </summary>
public sealed class MemoryStorageStrategy : IStorageStrategy<Stream>
{
    // Dedicated pool with bounded retention — avoids Shared pool's per-thread caching
    // that retains large arrays across 100+ concurrent threads indefinitely.
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(
        maxArrayLength: 50 * 1024 * 1024,  // 50MB max (matches SmallFileCutoffMb default)
        maxArraysPerBucket: 8);             // Bounded retention per size bucket

    /// <inheritdoc />
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        await using CacheFactoryResult<Stream> result = await factory(cancellationToken).ConfigureAwait(false);

        if (result.SizeBytes > int.MaxValue)
            throw new InvalidOperationException(
                $"File size {result.SizeBytes} bytes exceeds the memory tier limit of {int.MaxValue} bytes. " +
                "Route large files to the disk tier instead.");

        int size = (int)result.SizeBytes;
        byte[] rented = Pool.Rent(size);

        try
        {
            // Copy decompressed stream into pooled array via a wrapping MemoryStream.
            // CopyTo handles the read loop internally.
            var target = new MemoryStream(rented, 0, size, writable: true);
            await result.Value.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            int totalRead = (int)target.Position;

            return new StoredEntry(new PooledBuffer(rented, totalRead), result.SizeBytes);
        }
        catch
        {
            Pool.Return(rented);
            throw;
        }
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var pooled = (PooledBuffer)stored.Data;
        return new MemoryStream(pooled.Array, 0, pooled.Length, writable: false);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var pooled = (PooledBuffer)stored.Data;
        // Idempotent: only return to pool once (guards against double-dispose)
        if (Interlocked.Exchange(ref pooled._returned, 1) == 0)
            Pool.Return(pooled.Array, clearArray: true);
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => false;

    /// <summary>
    /// Wraps a rented byte[] with the actual data length (pool may return oversized arrays).
    /// </summary>
    internal sealed class PooledBuffer
    {
        public byte[] Array { get; }
        public int Length { get; }
        internal int _returned; // 0 = not returned, 1 = returned to pool

        public PooledBuffer(byte[] array, int length)
        {
            Array = array;
            Length = length;
        }
    }
}
