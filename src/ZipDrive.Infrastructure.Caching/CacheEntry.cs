namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Internal cache entry with reference counting.
/// </summary>
internal sealed class CacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public string CacheKey { get; }

    /// <summary>
    /// The stored data wrapped in an opaque StoredEntry.
    /// </summary>
    public StoredEntry Stored { get; }

    /// <inheritdoc />
    public long SizeBytes => Stored.SizeBytes;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the time-to-live for this entry.
    /// </summary>
    public TimeSpan Ttl { get; }

    /// <inheritdoc />
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <inheritdoc />
    public int AccessCount { get; set; }

    /// <summary>
    /// Reference count. Entry can only be evicted when RefCount = 0.
    /// </summary>
    private int _refCount;

    /// <summary>
    /// Whether this entry was removed from the cache dictionary while borrowed.
    /// When true, the last handle return triggers storage cleanup.
    /// </summary>
    private volatile bool _orphaned;

    /// <summary>
    /// Set to 1 when storage has been disposed (or queued for disposal).
    /// BorrowAsync checks this after IncrementRefCount to detect a race
    /// where the evictor disposed storage between TryGetValue and IncrementRefCount.
    /// </summary>
    private int _storageDisposed;

    /// <summary>
    /// Gets the current reference count. Thread-safe read.
    /// </summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheEntry"/> class.
    /// </summary>
    /// <param name="cacheKey">The unique cache key.</param>
    /// <param name="stored">The stored data.</param>
    /// <param name="createdAt">The timestamp when this entry was created.</param>
    /// <param name="ttl">The time-to-live for this entry.</param>
    public CacheEntry(
        string cacheKey,
        StoredEntry stored,
        DateTimeOffset createdAt,
        TimeSpan ttl)
    {
        CacheKey = cacheKey ?? throw new ArgumentNullException(nameof(cacheKey));
        Stored = stored ?? throw new ArgumentNullException(nameof(stored));
        CreatedAt = createdAt;
        Ttl = ttl;
        LastAccessedAt = createdAt;
        AccessCount = 1;
        _refCount = 0; // Starts at 0, incremented when borrowed
    }

    /// <summary>
    /// Atomically increments the reference count.
    /// Called when the entry is borrowed.
    /// </summary>
    public void IncrementRefCount() => Interlocked.Increment(ref _refCount);

    /// <summary>
    /// Atomically decrements the reference count.
    /// Called when the handle is disposed.
    /// Returns the new reference count value (from Interlocked.Decrement).
    /// Callers must use the return value for orphan cleanup decisions —
    /// reading RefCount separately is racy.
    /// </summary>
    public int DecrementRefCount() => Interlocked.Decrement(ref _refCount);

    /// <summary>
    /// Gets whether this entry has been orphaned (removed while borrowed).
    /// </summary>
    public bool IsOrphaned => _orphaned;

    /// <summary>
    /// Marks this entry as orphaned. Called by TryRemove when RefCount > 0.
    /// </summary>
    public void MarkOrphaned() => _orphaned = true;

    /// <summary>
    /// Marks storage as disposed. Returns true if this call set the flag (first caller wins).
    /// </summary>
    public bool TryMarkStorageDisposed() => Interlocked.CompareExchange(ref _storageDisposed, 1, 0) == 0;

    /// <summary>
    /// True if storage has been disposed (or queued for disposal).
    /// </summary>
    public bool IsStorageDisposed => Volatile.Read(ref _storageDisposed) == 1;

    /// <summary>
    /// Gets the expiration timestamp for this entry.
    /// </summary>
    public DateTimeOffset ExpiresAt => CreatedAt + Ttl;
}
