using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Dual-tier file content cache that routes to memory or disk tier based on file size.
/// Implements <see cref="ICache{Stream}"/> for transparent use by consumers.
/// </summary>
public sealed class DualTierFileCache : ICache<Stream>
{
    private readonly GenericCache<Stream> _memoryCache;
    private readonly GenericCache<Stream> _diskCache;
    private readonly DiskStorageStrategy _diskStorageStrategy;
    private readonly long _cutoffBytes;
    private readonly ILogger<DualTierFileCache> _logger;

    public DualTierFileCache(
        IOptions<CacheOptions> options,
        IEvictionPolicy evictionPolicy,
        TimeProvider timeProvider,
        ILogger<DualTierFileCache> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evictionPolicy);

        CacheOptions opts = options.Value;
        _cutoffBytes = opts.SmallFileCutoffBytes;
        _logger = logger;

        _memoryCache = new GenericCache<Stream>(
            new MemoryStorageStrategy(),
            evictionPolicy,
            opts.MemoryCacheSizeBytes,
            timeProvider,
            loggerFactory.CreateLogger<GenericCache<Stream>>(),
            name: "memory");

        _diskStorageStrategy = new DiskStorageStrategy(
            loggerFactory.CreateLogger<DiskStorageStrategy>(),
            opts.TempDirectory);

        _diskCache = new GenericCache<Stream>(
            _diskStorageStrategy,
            evictionPolicy,
            opts.DiskCacheSizeBytes,
            timeProvider,
            loggerFactory.CreateLogger<GenericCache<Stream>>(),
            name: "disk");

        _logger.LogInformation(
            "DualTierFileCache initialized: memory={MemoryMb}MB, disk={DiskMb}MB, cutoff={CutoffMb}MB",
            opts.MemoryCacheSizeMb, opts.DiskCacheSizeMb, opts.SmallFileCutoffMb);
    }

    /// <summary>
    /// Borrows a cached entry with size-hint routing.
    /// Files below the cutoff go to memory tier, others to disk tier.
    /// </summary>
    public Task<ICacheHandle<Stream>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        long sizeHintBytes,
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken = default)
    {
        GenericCache<Stream> targetCache = sizeHintBytes < _cutoffBytes ? _memoryCache : _diskCache;
        return BorrowFromAsync(targetCache, cacheKey, ttl, factory, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ICacheHandle<Stream>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken = default)
    {
        // Without size hint, default to memory tier
        return BorrowFromAsync(_memoryCache, cacheKey, ttl, factory, cancellationToken);
    }

    private async Task<ICacheHandle<Stream>> BorrowFromAsync(
        GenericCache<Stream> cache,
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken)
    {
        ICacheHandle<Stream> handle = await cache.BorrowAsync(cacheKey, ttl, factory, cancellationToken)
            .ConfigureAwait(false);

        // Track combined hit/miss counts
        // (The underlying GenericCache already emits per-tier metrics)
        return handle;
    }

    /// <inheritdoc />
    public long CurrentSizeBytes => _memoryCache.CurrentSizeBytes + _diskCache.CurrentSizeBytes;

    /// <inheritdoc />
    public long CapacityBytes => _memoryCache.CapacityBytes + _diskCache.CapacityBytes;

    /// <inheritdoc />
    public double HitRate
    {
        get
        {
            // Combine hit rates weighted by request count
            double memHitRate = _memoryCache.HitRate;
            double diskHitRate = _diskCache.HitRate;
            int memEntries = _memoryCache.EntryCount;
            int diskEntries = _diskCache.EntryCount;
            int total = memEntries + diskEntries;
            if (total == 0) return 0.0;
            return (memHitRate * memEntries + diskHitRate * diskEntries) / total;
        }
    }

    /// <inheritdoc />
    public int EntryCount => _memoryCache.EntryCount + _diskCache.EntryCount;

    /// <inheritdoc />
    public int BorrowedEntryCount => _memoryCache.BorrowedEntryCount + _diskCache.BorrowedEntryCount;

    /// <inheritdoc />
    public void EvictExpired()
    {
        _memoryCache.EvictExpired();
        _diskCache.EvictExpired();
    }

    /// <summary>
    /// Gets the memory tier cache (for testing/diagnostics).
    /// </summary>
    internal GenericCache<Stream> MemoryTier => _memoryCache;

    /// <summary>
    /// Gets the disk tier cache (for testing/diagnostics).
    /// </summary>
    internal GenericCache<Stream> DiskTier => _diskCache;

    /// <summary>
    /// Gets the size cutoff in bytes.
    /// </summary>
    internal long CutoffBytes => _cutoffBytes;

    /// <summary>
    /// Processes pending async cleanup items on both tiers (e.g., temp file deletion on disk tier).
    /// Call this periodically from a background maintenance task.
    /// </summary>
    /// <param name="maxItems">Maximum number of items to process per tier.</param>
    /// <returns>Total number of items processed across both tiers.</returns>
    public int ProcessPendingCleanup(int maxItems = 100)
    {
        return _memoryCache.ProcessPendingCleanup(maxItems) + _diskCache.ProcessPendingCleanup(maxItems);
    }

    /// <summary>
    /// Clears all entries from both tiers, disposing their storage (including temp files).
    /// Use for cleanup/shutdown scenarios only.
    /// </summary>
    public void Clear()
    {
        _logger.LogInformation("Clearing both cache tiers...");
        _memoryCache.Clear();
        _diskCache.Clear();
        _logger.LogInformation("Both cache tiers cleared");
    }

    /// <summary>
    /// Deletes the per-process disk cache directory after all entries have been cleared.
    /// Call after <see cref="Clear"/> on shutdown.
    /// </summary>
    public void DeleteCacheDirectory()
    {
        _diskStorageStrategy.DeleteCacheDirectory();
    }
}
