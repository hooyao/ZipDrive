namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Result from cache factory, containing the cached value and metadata.
/// The factory is responsible for preparing the data and reporting its size.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public sealed class CacheFactoryResult<T>
{
    /// <summary>
    /// The cached value to store.
    /// </summary>
    public required T Value { get; init; }

    /// <summary>
    /// Size in bytes (for capacity tracking and tier routing).
    /// Discovered by the factory during data preparation.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Optional metadata (e.g., content type, compression ratio, original filename).
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
