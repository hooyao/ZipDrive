namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Handle to a borrowed cache entry. Dispose to return to cache.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
internal sealed class CacheHandle<T> : ICacheHandle<T>
{
    private readonly CacheEntry _entry;
    private readonly Action<CacheEntry> _returnAction;
    private int _disposed;

    /// <inheritdoc />
    public T Value { get; }

    /// <inheritdoc />
    public string CacheKey => _entry.CacheKey;

    /// <inheritdoc />
    public long SizeBytes => _entry.SizeBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheHandle{T}"/> class.
    /// </summary>
    /// <param name="entry">The cache entry being borrowed.</param>
    /// <param name="value">The value retrieved from the entry.</param>
    /// <param name="returnAction">Action to call when returning the entry (decrements RefCount).</param>
    internal CacheHandle(CacheEntry entry, T value, Action<CacheEntry> returnAction)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Value = value;
        _returnAction = returnAction ?? throw new ArgumentNullException(nameof(returnAction));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Ensure we only decrement RefCount once, even if Dispose is called multiple times
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Dispose the borrowed value if it's disposable (e.g., MemoryMappedViewStream)
            if (Value is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _returnAction(_entry);
        }
    }
}
