namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as byte[] in memory.
/// Use for small files (&lt; 50MB by default).
/// Internal storage: byte[]
/// Returns: Stream (MemoryStream wrapping the byte[])
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

        MemoryStream ms = new MemoryStream((int)result.SizeBytes);
        await result.Value.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        byte[] bytes = ms.ToArray();

        return new StoredEntry(bytes, result.SizeBytes);
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        byte[] bytes = (byte[])stored.Data;
        return new MemoryStream(bytes, writable: false);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        // No-op: byte[] is garbage collected
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => false;
}
