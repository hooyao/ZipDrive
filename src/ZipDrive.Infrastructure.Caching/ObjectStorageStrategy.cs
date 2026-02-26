namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores any object directly in memory.
/// Use for parsed structures like ZIP metadata, configuration, etc.
/// Internal storage: T object
/// Returns: T (same object)
/// </summary>
/// <typeparam name="T">The type of object to store</typeparam>
public sealed class ObjectStorageStrategy<T> : IStorageStrategy<T>
{
    /// <inheritdoc />
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        await using CacheFactoryResult<T> result = await factory(cancellationToken).ConfigureAwait(false);

        return new StoredEntry(result.Value!, result.SizeBytes);
    }

    /// <inheritdoc />
    public T Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return (T)stored.Data;
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // Dispose if IDisposable, otherwise GC handles it
        if (stored.Data is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => false;
}
