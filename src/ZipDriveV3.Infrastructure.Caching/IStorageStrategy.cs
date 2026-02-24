namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Strategy for storing and retrieving cached data.
/// Implementations handle the specifics of data storage and cleanup.
/// The internal storage type is hidden from cache users.
/// </summary>
/// <typeparam name="TValue">Type of value returned to caller (e.g., Stream, ArchiveStructure)</typeparam>
public interface IStorageStrategy<TValue>
{
    /// <summary>
    /// Stores the factory result and returns an opaque StoredEntry.
    /// The actual storage format (byte[], MMF, object) is implementation-specific.
    /// </summary>
    /// <param name="result">The factory result containing value and size</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Opaque stored entry wrapping the internal representation</returns>
    Task<StoredEntry> StoreAsync(CacheFactoryResult<TValue> result, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the value from the stored entry.
    /// Called on cache hits to return data to the caller.
    /// </summary>
    /// <param name="stored">The opaque stored entry</param>
    /// <returns>The value to return to caller</returns>
    TValue Retrieve(StoredEntry stored);

    /// <summary>
    /// Disposes/cleans up the stored data when evicted.
    /// For GC-managed data, this may be a no-op.
    /// For disk-based storage, this deletes temp files.
    /// </summary>
    /// <param name="stored">The stored entry to clean up</param>
    void Dispose(StoredEntry stored);

    /// <summary>
    /// Whether disposal requires async cleanup (e.g., file deletion).
    /// If true, cache will queue for background cleanup instead of inline disposal.
    /// </summary>
    bool RequiresAsyncCleanup { get; }
}
