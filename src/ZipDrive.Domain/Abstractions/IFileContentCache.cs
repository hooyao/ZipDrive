using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Abstracts file content reading from archives with transparent caching.
/// Callers provide the archive path, entry metadata, cache key, output buffer,
/// and read offset. All caching, extraction, and tier-routing details are hidden.
/// </summary>
public interface IFileContentCache
{
    /// <summary>
    /// Reads decompressed bytes from an archive entry at the given offset into the buffer.
    /// On cache miss, extracts and caches the entry. On cache hit, reads from cache.
    /// </summary>
    /// <param name="archivePath">Absolute path to the ZIP archive file.</param>
    /// <param name="entry">Entry metadata from the structure cache.</param>
    /// <param name="cacheKey">Unique cache key for this entry.</param>
    /// <param name="buffer">Output buffer to write decompressed bytes into.</param>
    /// <param name="offset">Byte offset within the decompressed file to start reading from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes actually read (0 if offset is at or beyond EOF).</returns>
    Task<int> ReadAsync(
        string archivePath,
        ZipEntryInfo entry,
        string cacheKey,
        byte[] buffer,
        long offset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Warms a cache entry by pushing an already-decompressed stream into the appropriate
    /// cache tier without going through the internal extraction pipeline.
    /// If the entry is already cached, this is a no-op (thundering herd protection applies).
    /// The handle is immediately released — the entry has RefCount 0 and is eligible for eviction.
    /// </summary>
    /// <param name="entry">Entry metadata used for tier routing.</param>
    /// <param name="cacheKey">Unique cache key for this entry.</param>
    /// <param name="decompressedStream">Already-decompressed stream to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WarmAsync(
        ZipEntryInfo entry,
        string cacheKey,
        Stream decompressedStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if a non-expired entry is already cached for the given key. Lock-free.
    /// Use this to skip prefetch work when an entry is already warm.
    /// </summary>
    bool ContainsKey(string cacheKey);

    /// <summary>
    /// Evicts expired entries from all cache tiers.
    /// </summary>
    void EvictExpired();

    /// <summary>
    /// Removes all entries from all cache tiers.
    /// </summary>
    void Clear();

    /// <summary>
    /// Processes pending async cleanup items (e.g., temp file deletion).
    /// </summary>
    /// <param name="maxItems">Maximum items to process per tier.</param>
    /// <returns>Total items processed.</returns>
    int ProcessPendingCleanup(int maxItems = 100);

    /// <summary>
    /// Deletes the disk cache directory. Call after <see cref="Clear"/> on shutdown.
    /// </summary>
    void DeleteCacheDirectory();

    /// <summary>
    /// Total size in bytes of all cached entries across all tiers.
    /// </summary>
    long CurrentSizeBytes { get; }

    /// <summary>
    /// Total capacity in bytes across all tiers.
    /// </summary>
    long CapacityBytes { get; }

    /// <summary>
    /// Cache hit rate (hits / total requests).
    /// </summary>
    double HitRate { get; }

    /// <summary>
    /// Total number of cached entries across all tiers.
    /// </summary>
    int EntryCount { get; }

    /// <summary>
    /// Number of entries currently borrowed (protected from eviction).
    /// </summary>
    int BorrowedEntryCount { get; }
}
