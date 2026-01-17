namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Pluggable eviction policy for selecting cache entries to evict when capacity is exceeded.
/// </summary>
/// <remarks>
/// Implementations use the Strategy pattern to provide different eviction algorithms:
/// <list type="bullet">
/// <item><description>LRU (Least Recently Used): Default, evicts oldest accessed</description></item>
/// <item><description>LFU (Least Frequently Used): Evicts least accessed</description></item>
/// <item><description>Size-First: Evicts largest files first</description></item>
/// <item><description>Custom: Application-specific algorithms</description></item>
/// </list>
/// </remarks>
public interface IEvictionPolicy
{
    /// <summary>
    /// Selects cache entries to evict to free up the required space.
    /// </summary>
    /// <param name="entries">
    /// All non-expired cache entries available for eviction.
    /// </param>
    /// <param name="requiredBytes">
    /// The number of bytes needed for a new entry.
    /// </param>
    /// <param name="currentBytes">
    /// The current total size of all cached entries in bytes.
    /// </param>
    /// <param name="capacityBytes">
    /// The maximum allowed cache capacity in bytes.
    /// </param>
    /// <returns>
    /// An enumerable of cache entries to evict, ordered by eviction priority.
    /// The total size of returned entries should be sufficient to free up
    /// <paramref name="requiredBytes"/>.
    /// </returns>
    /// <remarks>
    /// Implementations should return just enough entries to satisfy the space requirement,
    /// not all possible candidates. The enumerable will be consumed until sufficient
    /// space is freed.
    /// </remarks>
    IEnumerable<ICacheEntry> SelectVictims(
        IReadOnlyCollection<ICacheEntry> entries,
        long requiredBytes,
        long currentBytes,
        long capacityBytes);
}

/// <summary>
/// Represents metadata about a cache entry for eviction policy decisions.
/// </summary>
/// <remarks>
/// This interface is implemented by both memory tier and disk tier cache entries,
/// providing a unified view for eviction policies.
/// </remarks>
public interface ICacheEntry
{
    /// <summary>
    /// Gets the unique cache key for this entry.
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// Gets the size of this entry in bytes.
    /// </summary>
    long SizeBytes { get; }

    /// <summary>
    /// Gets the timestamp when this entry was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets or sets the timestamp when this entry was last accessed.
    /// Used by LRU (Least Recently Used) eviction policies.
    /// </summary>
    DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>
    /// Gets or sets the number of times this entry has been accessed.
    /// Used by LFU (Least Frequently Used) eviction policies.
    /// </summary>
    int AccessCount { get; set; }
}
