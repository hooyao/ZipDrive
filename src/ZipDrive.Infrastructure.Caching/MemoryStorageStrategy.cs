namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as GC-managed byte[] in memory.
/// Use for small files (&lt; 50MB by default).
///
/// Storage arrays are plain heap allocations (not pooled). This is intentional:
/// cache entries are long-lived (minutes to hours), making ArrayPool counterproductive.
/// GC guarantees the array lives until ALL references — including MemoryStreams held
/// by concurrent readers — are collected, eliminating the return-reuse-overwrite race
/// that occurs with ArrayPool.
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
        byte[] buffer = new byte[size];

        var target = new MemoryStream(buffer, 0, size, writable: true);
        await result.Value.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        int totalRead = (int)target.Position;

        return new StoredEntry(new DataBuffer(buffer, totalRead), result.SizeBytes);
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var buf = (DataBuffer)stored.Data;
        return new MemoryStream(buf.Array, 0, buf.Length, writable: false);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        // No-op. The byte[] is GC-managed and will be collected when all references
        // (including MemoryStreams from concurrent readers) are gone.
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => false;

    /// <summary>
    /// Wraps a byte[] with the actual data length (allocated array may be exact size,
    /// but totalRead from CopyToAsync may be less if the stream was shorter than declared).
    /// </summary>
    internal sealed class DataBuffer(byte[] array, int length)
    {
        public byte[] Array { get; } = array;
        public int Length { get; } = length;
    }
}
