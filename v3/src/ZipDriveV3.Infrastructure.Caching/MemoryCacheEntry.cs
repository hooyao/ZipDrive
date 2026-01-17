namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Represents a cached entry in the memory tier.
/// </summary>
/// <remarks>
/// Memory tier entries store decompressed file data as byte arrays in RAM.
/// This provides the fastest possible access but is limited by available memory.
/// </remarks>
internal sealed class MemoryCacheEntry : ICacheEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCacheEntry"/> class.
    /// </summary>
    /// <param name="cacheKey">The unique cache key.</param>
    /// <param name="data">The cached data as a byte array.</param>
    /// <param name="sizeBytes">The size of the data in bytes.</param>
    /// <param name="createdAt">The timestamp when this entry was created.</param>
    /// <param name="ttl">The time-to-live for this entry.</param>
    public MemoryCacheEntry(
        string cacheKey,
        byte[] data,
        long sizeBytes,
        DateTimeOffset createdAt,
        TimeSpan ttl)
    {
        CacheKey = cacheKey ?? throw new ArgumentNullException(nameof(cacheKey));
        Data = data ?? throw new ArgumentNullException(nameof(data));
        SizeBytes = sizeBytes;
        CreatedAt = createdAt;
        Ttl = ttl;
        LastAccessedAt = createdAt;
        AccessCount = 0;
    }

    /// <inheritdoc />
    public string CacheKey { get; }

    /// <summary>
    /// Gets the cached data as a byte array.
    /// </summary>
    public byte[] Data { get; }

    /// <inheritdoc />
    public long SizeBytes { get; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <inheritdoc />
    public int AccessCount { get; set; }

    /// <summary>
    /// Gets the time-to-live for this entry.
    /// </summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Gets the expiration timestamp for this entry.
    /// </summary>
    public DateTimeOffset ExpiresAt => CreatedAt + Ttl;
}
