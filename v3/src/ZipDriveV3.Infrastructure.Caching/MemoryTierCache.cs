using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Memory tier cache implementation using in-memory byte arrays.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three-Layer Concurrency Strategy:</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <strong>Layer 1 (Lock-free):</strong> Cache hits use ConcurrentDictionary.TryGetValue()
/// with zero locking (&lt;100ns overhead).
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Layer 2 (Per-key):</strong> Cache misses use Lazy&lt;Task&lt;T&gt;&gt; to prevent
/// the "thundering herd" problem. 10 threads requesting the same file = 1 materialization.
/// Different keys can materialize in parallel.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Layer 3 (Eviction):</strong> Capacity enforcement uses a separate lock that
/// doesn't block reads. Only acquired when capacity is exceeded.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal sealed class MemoryTierCache
{
    // ═══════════════════════════════════════════════════════════════════
    // Layer 1: Lock-free reads
    // ═══════════════════════════════════════════════════════════════════
    private readonly ConcurrentDictionary<string, MemoryCacheEntry> _cache = new();

    // ═══════════════════════════════════════════════════════════════════
    // Layer 2: Per-key thundering herd prevention
    // ═══════════════════════════════════════════════════════════════════
    private readonly ConcurrentDictionary<string, Lazy<Task<MemoryCacheEntry>>> _materializationTasks = new();

    // ═══════════════════════════════════════════════════════════════════
    // Layer 3: Eviction lock
    // ═══════════════════════════════════════════════════════════════════
    private readonly Lock _evictionLock = new();

    private readonly IEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemoryTierCache> _logger;
    private readonly long _capacityBytes;

    private long _currentSizeBytes;
    private long _hits;
    private long _misses;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryTierCache"/> class.
    /// </summary>
    /// <param name="capacityBytes">The maximum capacity in bytes.</param>
    /// <param name="evictionPolicy">The eviction policy to use when capacity is exceeded.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="timeProvider">
    /// The time provider for TTL management. If null, uses TimeProvider.System.
    /// </param>
    public MemoryTierCache(
        long capacityBytes,
        IEvictionPolicy evictionPolicy,
        ILogger<MemoryTierCache> logger,
        TimeProvider? timeProvider = null)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Capacity must be positive");

        _capacityBytes = capacityBytes;
        _evictionPolicy = evictionPolicy ?? throw new ArgumentNullException(nameof(evictionPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        _logger.LogInformation(
            "MemoryTierCache initialized with capacity: {CapacityMb} MB",
            capacityBytes / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Gets a cached entry or materializes it via the provided factory function.
    /// </summary>
    public async Task<Stream> GetOrAddAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(factory);

        if (sizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size cannot be negative");

        // ═══════════════════════════════════════════════════════════════
        // LAYER 1: Lock-free cache lookup (fast path)
        // ═══════════════════════════════════════════════════════════════
        if (_cache.TryGetValue(cacheKey, out var entry) && !IsExpired(entry))
        {
            // Update access metadata
            entry.LastAccessedAt = _timeProvider.GetUtcNow();
            entry.AccessCount++;
            Interlocked.Increment(ref _hits);

            _logger.LogDebug("Memory cache HIT: {Key} ({Size} bytes)", cacheKey, sizeBytes);

            return new MemoryStream(entry.Data, writable: false);
        }

        // ═══════════════════════════════════════════════════════════════
        // LAYER 2: Per-key materialization (thundering herd prevention)
        // ═══════════════════════════════════════════════════════════════
        Interlocked.Increment(ref _misses);
        _logger.LogDebug("Memory cache MISS: {Key} ({Size} bytes)", cacheKey, sizeBytes);

        var lazy = _materializationTasks.GetOrAdd(cacheKey, _ =>
            new Lazy<Task<MemoryCacheEntry>>(
                () => MaterializeAndCacheAsync(cacheKey, sizeBytes, ttl, factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)); // Critical: Only one execution!

        try
        {
            entry = await lazy.Value.ConfigureAwait(false);
            return new MemoryStream(entry.Data, writable: false);
        }
        finally
        {
            // Clean up the lazy task to prevent memory leaks
            _materializationTasks.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// Materializes (decompresses) the entry and adds it to the cache.
    /// </summary>
    private async Task<MemoryCacheEntry> MaterializeAndCacheAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Materializing: {Key} ({Size} bytes)", cacheKey, sizeBytes);

        // ═══════════════════════════════════════════════════════════════
        // LAYER 3: Eviction (only if capacity exceeded)
        // ═══════════════════════════════════════════════════════════════
        await EvictIfNeededAsync(sizeBytes, cancellationToken).ConfigureAwait(false);

        // Materialize the entry
        using var sourceStream = await factory(cancellationToken).ConfigureAwait(false);
        using var memoryStream = new MemoryStream((int)sizeBytes);
        await sourceStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var data = memoryStream.ToArray();

        var entry = new MemoryCacheEntry(
            cacheKey,
            data,
            sizeBytes,
            _timeProvider.GetUtcNow(),
            ttl);

        _cache[cacheKey] = entry;
        Interlocked.Add(ref _currentSizeBytes, sizeBytes);

        _logger.LogInformation(
            "Materialized and cached: {Key} ({Size} bytes, {UtilizationPct:F1}% capacity)",
            cacheKey, sizeBytes, (CurrentSizeBytes / (double)_capacityBytes) * 100);

        return entry;
    }

    /// <summary>
    /// Evicts entries if necessary to make room for a new entry.
    /// </summary>
    private async Task EvictIfNeededAsync(long requiredBytes, CancellationToken cancellationToken)
    {
        // Fast path: Check capacity WITHOUT locking
        if (Interlocked.Read(ref _currentSizeBytes) + requiredBytes <= _capacityBytes)
        {
            return;
        }

        _logger.LogDebug(
            "Eviction needed: current={CurrentMb}MB, required={RequiredMb}MB, capacity={CapacityMb}MB",
            CurrentSizeBytes / (1024.0 * 1024.0),
            requiredBytes / (1024.0 * 1024.0),
            _capacityBytes / (1024.0 * 1024.0));

        // Slow path: Acquire eviction lock
        using (_evictionLock.EnterScope())
        {
            // Double-check after acquiring lock (another thread might have evicted already)
            if (_currentSizeBytes + requiredBytes <= _capacityBytes)
            {
                return;
            }

            // Phase 1: Evict expired entries first
            var now = _timeProvider.GetUtcNow();
            var expiredCount = 0;
            var expiredBytes = 0L;

            foreach (var kvp in _cache)
            {
                if (now > kvp.Value.ExpiresAt)
                {
                    if (_cache.TryRemove(kvp.Key, out var removed))
                    {
                        Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);
                        expiredCount++;
                        expiredBytes += removed.SizeBytes;
                    }
                }
            }

            if (expiredCount > 0)
            {
                _logger.LogInformation(
                    "Evicted {Count} expired entries ({Size} MB)",
                    expiredCount, expiredBytes / (1024.0 * 1024.0));
            }

            // Phase 2: Use eviction policy if still need space
            if (_currentSizeBytes + requiredBytes > _capacityBytes)
            {
                var victims = _evictionPolicy.SelectVictims(
                    _cache.Values.Cast<ICacheEntry>().ToList(),
                    requiredBytes,
                    _currentSizeBytes,
                    _capacityBytes);

                var policyEvictedCount = 0;
                var policyEvictedBytes = 0L;

                foreach (var victim in victims)
                {
                    if (_cache.TryRemove(victim.CacheKey, out var removed))
                    {
                        Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);
                        policyEvictedCount++;
                        policyEvictedBytes += removed.SizeBytes;

                        _logger.LogDebug("Evicted: {Key} ({Size} bytes)", victim.CacheKey, victim.SizeBytes);
                    }
                }

                if (policyEvictedCount > 0)
                {
                    _logger.LogInformation(
                        "Evicted {Count} entries via policy ({Size} MB)",
                        policyEvictedCount, policyEvictedBytes / (1024.0 * 1024.0));
                }
            }

            // Verify we have enough space now
            if (_currentSizeBytes + requiredBytes > _capacityBytes)
            {
                _logger.LogWarning(
                    "Eviction completed but still insufficient space: current={CurrentMb}MB, required={RequiredMb}MB, capacity={CapacityMb}MB",
                    _currentSizeBytes / (1024.0 * 1024.0),
                    requiredBytes / (1024.0 * 1024.0),
                    _capacityBytes / (1024.0 * 1024.0));
            }
        }

        // Small delay to allow for async operations
        await Task.Yield();
    }

    /// <summary>
    /// Manually evicts all expired entries.
    /// </summary>
    public void EvictExpired()
    {
        var now = _timeProvider.GetUtcNow();
        var evictedCount = 0;
        var evictedBytes = 0L;

        foreach (var kvp in _cache)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                if (_cache.TryRemove(kvp.Key, out var removed))
                {
                    Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);
                    evictedCount++;
                    evictedBytes += removed.SizeBytes;
                }
            }
        }

        if (evictedCount > 0)
        {
            _logger.LogInformation(
                "Manual eviction: removed {Count} expired entries ({Size} MB)",
                evictedCount, evictedBytes / (1024.0 * 1024.0));
        }
    }

    /// <summary>
    /// Checks if an entry has expired based on its TTL.
    /// </summary>
    private bool IsExpired(MemoryCacheEntry entry)
        => _timeProvider.GetUtcNow() > entry.ExpiresAt;

    /// <summary>
    /// Gets the current total size of cached entries in bytes.
    /// </summary>
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSizeBytes);

    /// <summary>
    /// Gets the maximum capacity in bytes.
    /// </summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>
    /// Gets the total number of cached entries.
    /// </summary>
    public int EntryCount => _cache.Count;

    /// <summary>
    /// Gets the cache hit rate (0.0 to 1.0).
    /// </summary>
    public double HitRate
    {
        get
        {
            var totalHits = Interlocked.Read(ref _hits);
            var totalMisses = Interlocked.Read(ref _misses);
            var total = totalHits + totalMisses;

            return total == 0 ? 0.0 : (double)totalHits / total;
        }
    }
}
