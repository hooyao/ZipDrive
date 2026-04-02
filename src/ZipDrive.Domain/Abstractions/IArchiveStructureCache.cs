using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Cache for parsed ZIP archive structures.
/// Uses GenericCache with ObjectStorageStrategy internally.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cache Strategy:</strong>
/// <list type="bullet">
/// <item>Unit of caching: entire <see cref="ArchiveStructure"/> per archive</item>
/// <item>Any file access within archive refreshes TTL for whole structure</item>
/// <item>LRU eviction when memory limit is reached</item>
/// <item>TTL-based expiration (default: 30 minutes)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Memory Estimation:</strong>
/// ~114 bytes per ZIP entry (struct + filename string + dictionary overhead).
/// <list type="bullet">
/// <item>100 files: ~11 KB</item>
/// <item>1,000 files: ~114 KB</item>
/// <item>10,000 files: ~1.1 MB</item>
/// <item>100,000 files: ~11 MB</item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong>
/// Implementations must be thread-safe. Multiple threads may call
/// <see cref="GetOrBuildAsync"/> concurrently for the same or different archives.
/// Thundering herd prevention should be implemented (single build per archive).
/// </para>
/// </remarks>
public interface IArchiveStructureCache
{
    /// <summary>
    /// Gets the cached structure or builds it by delegating to the format-specific
    /// <see cref="IArchiveStructureBuilder"/> resolved via <see cref="IFormatRegistry"/>.
    /// </summary>
    /// <param name="archiveKey">Unique archive identifier (e.g., "archive.zip").</param>
    /// <param name="absolutePath">Filesystem path to the archive file.</param>
    /// <param name="formatId">Archive format identifier (e.g., "zip", "rar").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed archive structure (from cache or freshly built).</returns>
    /// <remarks>
    /// <para>
    /// On cache miss, delegates to <c>IFormatRegistry.GetStructureBuilder(formatId).BuildAsync()</c>.
    /// The builder handles all format-specific parsing.
    /// </para>
    /// <para>
    /// If the structure is already cached, TTL is extended and LRU metrics are updated.
    /// </para>
    /// <para>
    /// If multiple threads request the same uncached archive simultaneously,
    /// only one should perform the build (thundering herd prevention).
    /// </para>
    /// </remarks>
    Task<ArchiveStructure> GetOrBuildAsync(
        string archiveKey,
        string absolutePath,
        string formatId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates a cached structure.
    /// </summary>
    /// <param name="archiveKey">Archive key to invalidate.</param>
    /// <returns>True if an entry was found and invalidated; false if not found.</returns>
    /// <remarks>
    /// Use this when the underlying ZIP file has been modified and the cached
    /// structure is stale.
    /// </remarks>
    bool Invalidate(string archiveKey);

    /// <summary>
    /// Clears all cached structures.
    /// </summary>
    void Clear();

    /// <summary>
    /// Manually evicts expired entries.
    /// </summary>
    /// <remarks>
    /// Expired entries are typically evicted automatically, but this method
    /// can be called to force immediate cleanup.
    /// </remarks>
    void EvictExpired();

    /// <summary>
    /// Current number of cached archive structures.
    /// </summary>
    int CachedArchiveCount { get; }

    /// <summary>
    /// Estimated total memory usage in bytes.
    /// </summary>
    /// <remarks>
    /// This is an approximation based on entry counts and estimated per-entry overhead.
    /// </remarks>
    long EstimatedMemoryBytes { get; }

    /// <summary>
    /// Cache hit rate (0.0 to 1.0).
    /// </summary>
    /// <remarks>
    /// Calculated as hits / (hits + misses).
    /// </remarks>
    double HitRate { get; }

    /// <summary>
    /// Total number of cache hits since start or last reset.
    /// </summary>
    long HitCount { get; }

    /// <summary>
    /// Total number of cache misses since start or last reset.
    /// </summary>
    long MissCount { get; }
}
