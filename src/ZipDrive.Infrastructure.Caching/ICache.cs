namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Generic cache abstraction with pluggable eviction policies.
/// Uses borrow/return pattern to protect entries from eviction during use.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public interface ICache<T>
{
    /// <summary>
    /// Borrows a cached entry or creates via factory.
    /// The returned handle MUST be disposed after use to allow eviction.
    /// </summary>
    /// <param name="cacheKey">Unique cache key</param>
    /// <param name="ttl">Time-to-live for this entry</param>
    /// <param name="factory">
    /// Factory that produces the value AND its metadata (including size).
    /// The factory is only called on cache miss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Handle to the cached entry - MUST be disposed after use</returns>
    /// <example>
    /// <code>
    /// using (var handle = await cache.BorrowAsync(key, ttl, factory, ct))
    /// {
    ///     var stream = handle.Value;
    ///     await stream.ReadAsync(buffer);  // Safe - won't be evicted
    /// }  // Dispose() allows eviction
    /// </code>
    /// </example>
    Task<ICacheHandle<T>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current cache size in bytes.
    /// </summary>
    long CurrentSizeBytes { get; }

    /// <summary>
    /// Cache capacity in bytes.
    /// </summary>
    long CapacityBytes { get; }

    /// <summary>
    /// Cache hit rate (0.0 to 1.0).
    /// </summary>
    double HitRate { get; }

    /// <summary>
    /// Total number of cached entries.
    /// </summary>
    int EntryCount { get; }

    /// <summary>
    /// Number of entries currently borrowed (RefCount > 0).
    /// </summary>
    int BorrowedEntryCount { get; }

    /// <summary>
    /// Manually trigger eviction of expired entries (only evicts entries with RefCount = 0).
    /// </summary>
    void EvictExpired();

    /// <summary>
    /// Removes a specific entry by key. If the entry is currently borrowed (RefCount > 0),
    /// it is removed from the cache dictionary immediately (preventing new borrows), but
    /// storage cleanup is deferred until the last handle is returned.
    /// </summary>
    /// <returns>True if the entry was found and removed from the cache dictionary.</returns>
    bool TryRemove(string cacheKey);

    /// <summary>
    /// Returns true if a non-expired entry exists for the given key. Lock-free.
    /// </summary>
    bool ContainsKey(string cacheKey);
}
