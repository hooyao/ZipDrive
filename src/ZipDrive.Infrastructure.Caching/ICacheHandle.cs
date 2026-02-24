namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Handle to a borrowed cache entry. MUST be disposed after use.
/// The entry is protected from eviction while the handle is active.
/// Multiple handles can reference the same entry (reference counted).
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public interface ICacheHandle<out T> : IDisposable
{
    /// <summary>
    /// The cached value (Stream, ArchiveStructure, etc.)
    /// </summary>
    T Value { get; }

    /// <summary>
    /// Cache key for debugging/logging.
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// Size of the cached entry in bytes.
    /// </summary>
    long SizeBytes { get; }
}
