using System.Buffers;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as pooled byte[] in memory.
/// Use for small files (&lt; 50MB by default).
/// Internal storage: PooledBuffer (rented byte[] + length)
/// Returns: Stream (MemoryStream wrapping the rented byte[])
///
/// Uses ArrayPool to eliminate LOH pressure from repeated allocation/deallocation
/// of large byte arrays during cache eviction churn. Dispose MUST be called to
/// return arrays to the pool.
/// </summary>
public sealed class MemoryStorageStrategy : IStorageStrategy<Stream>
{
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
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);

        try
        {
            // Read exactly 'size' bytes from the factory stream into the rented buffer
            int totalRead = 0;
            while (totalRead < size)
            {
                int read = await result.Value.ReadAsync(
                    rented.AsMemory(totalRead, size - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            return new StoredEntry(new PooledBuffer(rented, totalRead), result.SizeBytes);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var pooled = (PooledBuffer)stored.Data;
        // Wrap the rented array — no copy. MemoryStream is bounded to actual length.
        return new MemoryStream(pooled.Array, 0, pooled.Length, writable: false);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var pooled = (PooledBuffer)stored.Data;
        ArrayPool<byte>.Shared.Return(pooled.Array);
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => false;

    /// <summary>
    /// Wraps a rented byte[] with the actual data length (ArrayPool may return oversized arrays).
    /// </summary>
    internal sealed class PooledBuffer
    {
        public byte[] Array { get; }
        public int Length { get; }

        public PooledBuffer(byte[] array, int length)
        {
            Array = array;
            Length = length;
        }
    }
}
