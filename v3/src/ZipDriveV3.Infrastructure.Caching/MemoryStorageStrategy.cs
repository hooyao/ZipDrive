namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Stores file content as byte[] in memory.
/// Use for small files (&lt; 50MB by default).
/// Internal storage: byte[]
/// Returns: Stream (MemoryStream wrapping the byte[])
/// </summary>
public sealed class MemoryStorageStrategy : IStorageStrategy<Stream>
{
    /// <inheritdoc />
    public async Task<StoredEntry> StoreAsync(CacheFactoryResult<Stream> result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Value);

        await using var ms = new MemoryStream((int)result.SizeBytes);
        await result.Value.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();

        return new StoredEntry(bytes, result.SizeBytes);
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var bytes = (byte[])stored.Data;
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
