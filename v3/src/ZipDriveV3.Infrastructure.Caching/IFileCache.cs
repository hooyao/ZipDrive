namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// File cache abstraction for materialized ZIP entries.
/// Converts sequential ZIP streams into random-access streams.
/// </summary>
/// <remarks>
/// <para>
/// The cache solves the fundamental mismatch between ZIP archives (sequential access only)
/// and Windows file system operations (random access required). Without caching, every read
/// would require full decompression from the beginning, making the system unusable.
/// </para>
/// <para>
/// Implementation uses a dual-tier strategy:
/// <list type="bullet">
/// <item><description>Small files (&lt; 50MB): In-memory byte arrays</description></item>
/// <item><description>Large files (≥ 50MB): Memory-mapped files on disk</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IFileCache
{
    /// <summary>
    /// Gets a cached entry or materializes it via the provided factory function.
    /// Returns a random-access stream that supports Seek().
    /// </summary>
    /// <param name="cacheKey">
    /// Unique cache key in the format: {archiveKey}:{entryPath}.
    /// Example: "data.zip:folder/file.txt"
    /// </param>
    /// <param name="sizeBytes">
    /// Uncompressed size in bytes. Used for tier routing (memory vs disk)
    /// and capacity management.
    /// </param>
    /// <param name="ttl">
    /// Time-to-live for this cache entry. After this duration, the entry
    /// will be evicted automatically.
    /// </param>
    /// <param name="factory">
    /// Async factory function that returns a sequential ZIP stream.
    /// Only called on cache miss to materialize (decompress) the entry.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A random-access stream (seekable) containing the decompressed file contents.
    /// The stream can be disposed safely by the caller; the cache maintains ownership
    /// of the underlying resources.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="cacheKey"/> or <paramref name="factory"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cacheKey"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sizeBytes"/> is negative.
    /// </exception>
    Task<Stream> GetOrAddAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current total size of cached entries in bytes (both tiers combined).
    /// </summary>
    long CurrentSizeBytes { get; }

    /// <summary>
    /// Gets the total capacity in bytes (both tiers combined).
    /// </summary>
    long CapacityBytes { get; }

    /// <summary>
    /// Gets the cache hit rate (0.0 to 1.0).
    /// Calculated as: hits / (hits + misses).
    /// </summary>
    double HitRate { get; }

    /// <summary>
    /// Gets the total number of cached entries (both tiers combined).
    /// </summary>
    int EntryCount { get; }

    /// <summary>
    /// Manually triggers eviction of expired entries.
    /// This is automatically called periodically, but can be invoked manually
    /// for immediate cleanup.
    /// </summary>
    void EvictExpired();
}
