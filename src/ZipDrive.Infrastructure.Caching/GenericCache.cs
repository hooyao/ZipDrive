using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Generic cache implementation with pluggable storage strategies and eviction policies.
/// Uses borrow/return pattern with reference counting to protect entries from eviction during use.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
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
/// doesn't block reads. Only acquired when capacity is exceeded. Only evicts entries with RefCount = 0.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class GenericCache<T> : ICache<T>, ICacheMetricsSource
{
    // ═══════════════════════════════════════════════════════════════════════
    // Layer 1: Lock-free reads
    // ═══════════════════════════════════════════════════════════════════════
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // ═══════════════════════════════════════════════════════════════════════
    // Layer 2: Per-key thundering herd prevention
    // ═══════════════════════════════════════════════════════════════════════
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _materializationTasks = new();

    // ═══════════════════════════════════════════════════════════════════════
    // Layer 3: Eviction lock and async cleanup queue
    // ═══════════════════════════════════════════════════════════════════════
    private readonly Lock _evictionLock = new();
    private readonly ConcurrentQueue<StoredEntry> _pendingCleanup = new();

    private readonly IStorageStrategy<T> _storageStrategy;
    private readonly IEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GenericCache<T>> _logger;
    private readonly long _capacityBytes;
    private readonly string _name;
    private readonly KeyValuePair<string, object?> _tierTag;

    private long _currentSizeBytes;
    private long _hits;
    private long _misses;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericCache{T}"/> class.
    /// </summary>
    /// <param name="storageStrategy">The storage strategy for storing and retrieving data.</param>
    /// <param name="evictionPolicy">The eviction policy for selecting entries to evict.</param>
    /// <param name="capacityBytes">The maximum capacity in bytes.</param>
    /// <param name="timeProvider">Optional time provider for TTL management. If null, uses TimeProvider.System.</param>
    /// <param name="logger">Optional logger instance.</param>
    /// <param name="name">Optional name for this cache instance, used as the 'tier' metric tag. Defaults to type name.</param>
    public GenericCache(
        IStorageStrategy<T> storageStrategy,
        IEvictionPolicy evictionPolicy,
        long capacityBytes,
        TimeProvider timeProvider,
        ILogger<GenericCache<T>> logger,
        string? name = null)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Capacity must be positive");

        _storageStrategy = storageStrategy ?? throw new ArgumentNullException(nameof(storageStrategy));
        _evictionPolicy = evictionPolicy ?? throw new ArgumentNullException(nameof(evictionPolicy));
        _capacityBytes = capacityBytes;
        _timeProvider = timeProvider;
        _logger = logger;
        _name = name ?? typeof(T).Name;
        _tierTag = new KeyValuePair<string, object?>("tier", _name);

        CacheTelemetry.RegisterInstance(this);

        _logger.LogInformation(
            "GenericCache<{Type}> initialized with capacity: {CapacityMb:F1} MB, tier: {Tier}",
            typeof(T).Name,
            capacityBytes / (1024.0 * 1024.0),
            _name);
    }

    /// <summary>
    /// Attempts a synchronous lock-free cache hit. Returns <c>true</c> and a borrowed handle
    /// if the entry is present and not expired; <c>false</c> if it is absent or expired.
    /// The caller MUST dispose the handle when done.
    /// </summary>
    internal bool TryBorrow(string cacheKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ICacheHandle<T>? handle)
    {
        if (_cache.TryGetValue(cacheKey, out CacheEntry? entry) && !IsExpired(entry))
        {
            entry.IncrementRefCount();
            entry.LastAccessedAt = _timeProvider.GetUtcNow();
            entry.AccessCount++;
            Interlocked.Increment(ref _hits);
            CacheTelemetry.Hits.Add(1, _tierTag);
            T value = _storageStrategy.Retrieve(entry.Stored);
            handle = new CacheHandle<T>(entry, value, Return);
            return true;
        }

        handle = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<ICacheHandle<T>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(cacheKey))
            throw new ArgumentException("Cache key cannot be empty or whitespace", nameof(cacheKey));

        using (Activity? borrowActivity = CacheTelemetry.Source.StartActivity("cache.borrow"))
        {
            borrowActivity?.SetTag("tier", _name);

            // ═══════════════════════════════════════════════════════════════════
            // LAYER 1: Lock-free cache lookup (FAST PATH)
            // ═══════════════════════════════════════════════════════════════════
            if (_cache.TryGetValue(cacheKey, out CacheEntry? existingEntry) && !IsExpired(existingEntry))
            {
                // Increment RefCount BEFORE returning handle (protects from eviction)
                existingEntry.IncrementRefCount();
                existingEntry.LastAccessedAt = _timeProvider.GetUtcNow();
                existingEntry.AccessCount++;
                Interlocked.Increment(ref _hits);

                CacheTelemetry.Hits.Add(1, _tierTag);
                borrowActivity?.SetTag("result", "hit");

                _logger.LogDebug("Cache HIT: {Key} (RefCount={RefCount})", cacheKey, existingEntry.RefCount);

                T value = _storageStrategy.Retrieve(existingEntry.Stored);
                return new CacheHandle<T>(existingEntry, value, Return);
            }

            // ═══════════════════════════════════════════════════════════════════
            // LAYER 2: Per-key materialization lock (THUNDERING HERD PREVENTION)
            // ═══════════════════════════════════════════════════════════════════
            Interlocked.Increment(ref _misses);

            CacheTelemetry.Misses.Add(1, _tierTag);
            borrowActivity?.SetTag("result", "miss");

            _logger.LogDebug("Cache MISS: {Key}", cacheKey);

            Lazy<Task<CacheEntry>> lazy = _materializationTasks.GetOrAdd(cacheKey, _ =>
                new Lazy<Task<CacheEntry>>(
                    () => MaterializeAndCacheAsync(cacheKey, ttl, factory, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication)); // Critical: Only one execution!

            try
            {
                CacheEntry entry = await lazy.Value.ConfigureAwait(false);

                // Increment RefCount for each borrower (thundering herd: all get handles)
                entry.IncrementRefCount();

                T value = _storageStrategy.Retrieve(entry.Stored);
                return new CacheHandle<T>(entry, value, Return);
            }
            finally
            {
                // Clean up the lazy task to prevent memory leaks
                _materializationTasks.TryRemove(cacheKey, out _);
            }
        }
    }

    /// <summary>
    /// Called by CacheHandle.Dispose() to return the entry to the cache.
    /// Decrements RefCount, allowing eviction when RefCount reaches 0.
    /// </summary>
    private void Return(CacheEntry entry)
    {
        entry.DecrementRefCount();
        _logger.LogDebug("Returned: {Key} (RefCount={RefCount})", entry.CacheKey, entry.RefCount);
    }

    /// <summary>
    /// Materializes (decompresses) the entry and adds it to the cache.
    /// </summary>
    private async Task<CacheEntry> MaterializeAndCacheAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Materializing: {Key}", cacheKey);

        long startTimestamp = Stopwatch.GetTimestamp();

        // Strategy owns the full pipeline: call factory → consume stream → dispose resources → return StoredEntry
        StoredEntry stored = await _storageStrategy.MaterializeAsync(factory, cancellationToken).ConfigureAwait(false);

        double materializationMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        string sizeBucket = SizeBucketClassifier.Classify(stored.SizeBytes);

        using (Activity? materializeActivity = CacheTelemetry.Source.StartActivity("cache.materialize"))
        {
            materializeActivity?.SetTag("tier", _name);
            materializeActivity?.SetTag("size_bucket", sizeBucket);
            materializeActivity?.SetTag("size_bytes", stored.SizeBytes);

            CacheEntry entry = new CacheEntry(
                cacheKey,
                stored,
                _timeProvider.GetUtcNow(),
                ttl);

            // ═══════════════════════════════════════════════════════════════════
            // CRITICAL: Order matters for consistency!
            // 1. Add to cache FIRST
            // 2. Update size counter SECOND
            // 3. Run post-store eviction check
            //
            // This ensures _currentSizeBytes may temporarily UNDERCOUNT
            // (safe - causes less eviction) but will NEVER OVERCOUNT
            // (dangerous - could prevent needed eviction).
            // ═══════════════════════════════════════════════════════════════════

            // Temporarily increment RefCount to protect entry during post-store eviction
            entry.IncrementRefCount();

            _cache[cacheKey] = entry;
            Interlocked.Add(ref _currentSizeBytes, stored.SizeBytes);

            CacheTelemetry.MaterializationDuration.Record(materializationMs,
                _tierTag,
                new KeyValuePair<string, object?>("size_bucket", sizeBucket));

            _logger.LogInformation(
                "Materialized: {Key} ({SizeMb:F2} MB, {UtilizationPct:F1}% capacity, {Tier} tier, {MaterializationMs:F1} ms)",
                cacheKey,
                stored.SizeBytes / (1024.0 * 1024.0),
                (CurrentSizeBytes / (double)_capacityBytes) * 100,
                _name,
                materializationMs);

            // ═══════════════════════════════════════════════════════════════════
            // POST-STORE CAPACITY CHECK (Soft Capacity Design)
            //
            // Multiple concurrent materializations may cause temporary
            // overage. This check ensures we converge back to capacity.
            // The newly added entry is protected by RefCount = 1.
            // ═══════════════════════════════════════════════════════════════════
            if (Interlocked.Read(ref _currentSizeBytes) > _capacityBytes)
            {
                await EvictIfNeededAsync(neededBytes: 0).ConfigureAwait(false);
            }

            // Release the temporary hold - caller will increment when borrowing
            entry.DecrementRefCount();

            return entry;
        }
    }

    /// <summary>
    /// Evicts entries if necessary to make room for a new entry.
    /// Only evicts entries with RefCount = 0.
    /// </summary>
    private Task EvictIfNeededAsync(long neededBytes)
    {
        // Fast path: Check capacity WITHOUT locking (common case)
        if (Interlocked.Read(ref _currentSizeBytes) + neededBytes <= _capacityBytes)
        {
            return Task.CompletedTask;
        }

        _logger.LogDebug(
            "Eviction check: current={CurrentMb:F1}MB, needed={NeededMb:F1}MB, capacity={CapacityMb:F1}MB",
            CurrentSizeBytes / (1024.0 * 1024.0),
            neededBytes / (1024.0 * 1024.0),
            _capacityBytes / (1024.0 * 1024.0));

        // Slow path: Acquire eviction lock
        using (Activity? evictActivity = CacheTelemetry.Source.StartActivity("cache.evict"))
        {
            evictActivity?.SetTag("tier", _name);

            using (_evictionLock.EnterScope())
            {
                // Double-check after acquiring lock (another thread might have evicted already)
                if (_currentSizeBytes + neededBytes <= _capacityBytes)
                {
                    evictActivity?.SetTag("evicted_count", 0);
                    evictActivity?.SetTag("evicted_bytes", 0L);
                    return Task.CompletedTask;
                }

                // ═══════════════════════════════════════════════════════════════
                // IMPORTANT: Only evict entries with RefCount = 0
                // Entries currently borrowed are protected from eviction
                // ═══════════════════════════════════════════════════════════════

                // Phase 1: Evict expired entries first (only if not borrowed)
                DateTimeOffset now = _timeProvider.GetUtcNow();
                List<string> expiredKeys = _cache
                    .Where(kvp => now > kvp.Value.ExpiresAt)
                    .Where(kvp => kvp.Value.RefCount == 0) // Only evict if not borrowed
                    .Select(kvp => kvp.Key)
                    .ToList();

                int expiredCount = 0;
                long expiredBytes = 0L;

                foreach (string key in expiredKeys)
                {
                    if (TryEvictEntry(key, "expired", out long evictedBytes))
                    {
                        expiredCount++;
                        expiredBytes += evictedBytes;
                    }
                }

                if (expiredCount > 0)
                {
                    _logger.LogInformation(
                        "Evicted {Count} expired entries ({SizeMb:F2} MB, {Tier} tier)",
                        expiredCount,
                        expiredBytes / (1024.0 * 1024.0),
                        _name);
                }

                // Phase 2: Use eviction policy if still need space
                if (_currentSizeBytes + neededBytes > _capacityBytes)
                {
                    // Only consider entries with RefCount = 0 for eviction
                    List<ICacheEntry> evictableEntries = _cache.Values
                        .Where(e => e.RefCount == 0)
                        .Cast<ICacheEntry>()
                        .ToList();

                    if (evictableEntries.Count == 0)
                    {
                        _logger.LogWarning(
                            "All {Count} entries are borrowed, cannot evict. Allowing soft capacity overage.",
                            _cache.Count);
                        return Task.CompletedTask;
                    }

                    IEnumerable<ICacheEntry> victims = _evictionPolicy.SelectVictims(
                        evictableEntries,
                        neededBytes,
                        _currentSizeBytes,
                        _capacityBytes);

                    int policyEvictedCount = 0;
                    long policyEvictedBytes = 0L;

                    foreach (ICacheEntry victim in victims)
                    {
                        if (TryEvictEntry(victim.CacheKey, "policy", out long evictedBytes))
                        {
                            policyEvictedCount++;
                            policyEvictedBytes += evictedBytes;
                        }
                    }

                    if (policyEvictedCount > 0)
                    {
                        _logger.LogInformation(
                            "Evicted {Count} entries via policy ({SizeMb:F2} MB, {Tier} tier)",
                            policyEvictedCount,
                            policyEvictedBytes / (1024.0 * 1024.0),
                            _name);

                        expiredCount += policyEvictedCount;
                        expiredBytes += policyEvictedBytes;
                    }
                }

                evictActivity?.SetTag("evicted_count", expiredCount);
                evictActivity?.SetTag("evicted_bytes", expiredBytes);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Attempts to evict a single entry by key.
    /// </summary>
    private bool TryEvictEntry(string key, string reason, out long evictedBytes)
    {
        evictedBytes = 0;

        if (!_cache.TryGetValue(key, out CacheEntry? entry))
        {
            return false;
        }

        // Double-check RefCount before eviction (another thread might have borrowed)
        if (entry.RefCount > 0)
        {
            _logger.LogDebug("Skipping eviction of {Key}: RefCount={RefCount}", key, entry.RefCount);
            return false;
        }

        if (_cache.TryRemove(key, out CacheEntry? removed))
        {
            evictedBytes = removed.Stored.SizeBytes;
            Interlocked.Add(ref _currentSizeBytes, -evictedBytes);

            CacheTelemetry.Evictions.Add(1,
                _tierTag,
                new KeyValuePair<string, object?>("reason", reason));

            if (_storageStrategy.RequiresAsyncCleanup)
            {
                // Queue for background cleanup (non-blocking)
                _pendingCleanup.Enqueue(removed.Stored);
            }

            _logger.LogInformation(
                "Evicted: {Key} ({SizeBytes} bytes, {Tier} tier, reason: {Reason})",
                key, evictedBytes, _name, reason);

            if (!_storageStrategy.RequiresAsyncCleanup)
            {
                _storageStrategy.Dispose(removed.Stored);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Processes pending cleanup items (for strategies that require async cleanup).
    /// Call this periodically from a background task.
    /// </summary>
    /// <param name="maxItems">Maximum number of items to process in this batch.</param>
    /// <returns>The number of items processed.</returns>
    public int ProcessPendingCleanup(int maxItems = 100)
    {
        int processed = 0;

        while (processed < maxItems && _pendingCleanup.TryDequeue(out StoredEntry? stored))
        {
            try
            {
                _storageStrategy.Dispose(stored);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup stored entry");
            }
        }

        if (processed > 0)
        {
            _logger.LogDebug("Processed {Count} pending cleanup items", processed);
        }

        return processed;
    }

    /// <inheritdoc />
    public void EvictExpired()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<string> expiredKeys = _cache
            .Where(kvp => now > kvp.Value.ExpiresAt)
            .Where(kvp => kvp.Value.RefCount == 0) // Only evict if not borrowed
            .Select(kvp => kvp.Key)
            .ToList();

        int evictedCount = 0;
        long evictedBytes = 0L;

        foreach (string key in expiredKeys)
        {
            if (TryEvictEntry(key, "manual", out long bytes))
            {
                evictedCount++;
                evictedBytes += bytes;
            }
        }

        if (evictedCount > 0)
        {
            _logger.LogInformation(
                "Manual eviction: removed {Count} expired entries ({SizeMb:F2} MB, {Tier} tier)",
                evictedCount,
                evictedBytes / (1024.0 * 1024.0),
                _name);
        }
    }

    /// <summary>
    /// Clears all entries from the cache, disposing their storage.
    /// This method ignores RefCount and forcibly removes all entries.
    /// Use for cleanup/shutdown scenarios only.
    /// </summary>
    public void Clear()
    {
        List<string> keys = _cache.Keys.ToList();
        int clearedCount = 0;
        long clearedBytes = 0L;

        foreach (string key in keys)
        {
            if (_cache.TryRemove(key, out CacheEntry? removed))
            {
                clearedBytes += removed.Stored.SizeBytes;
                clearedCount++;

                // Dispose immediately (don't queue for async cleanup)
                try
                {
                    _storageStrategy.Dispose(removed.Stored);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose entry {Key} during Clear()", key);
                }
            }
        }

        Interlocked.Exchange(ref _currentSizeBytes, 0);

        // Also process any pending cleanup
        while (_pendingCleanup.TryDequeue(out StoredEntry? stored))
        {
            try
            {
                _storageStrategy.Dispose(stored);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process pending cleanup during Clear()");
            }
        }

        _logger.LogInformation(
            "Cache cleared({CacheName}): removed {Count} entries ({SizeMb:F2} MB)",
            _name,
            clearedCount,
            clearedBytes / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Asynchronously clears all entries from the cache, disposing their storage.
    /// This method ignores RefCount and forcibly removes all entries.
    /// Use for cleanup/shutdown scenarios only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        List<string> keys = _cache.Keys.ToList();
        int clearedCount = 0;
        long clearedBytes = 0L;

        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_cache.TryRemove(key, out CacheEntry? removed))
            {
                clearedBytes += removed.Stored.SizeBytes;
                clearedCount++;

                // Dispose - yield occasionally to avoid blocking
                try
                {
                    _storageStrategy.Dispose(removed.Stored);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose entry {Key} during ClearAsync()", key);
                }

                // Yield every 10 entries to allow other work
                if (clearedCount % 10 == 0)
                {
                    await Task.Yield();
                }
            }
        }

        Interlocked.Exchange(ref _currentSizeBytes, 0);

        // Also process any pending cleanup
        int pendingCount = 0;
        while (_pendingCleanup.TryDequeue(out StoredEntry? stored))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _storageStrategy.Dispose(stored);
                pendingCount++;

                if (pendingCount % 10 == 0)
                {
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process pending cleanup during ClearAsync()");
            }
        }

        _logger.LogInformation(
            "Cache({CacheName}) cleared: removed {Count} entries ({SizeMb:F2} MB)",
            _name,
            clearedCount,
            clearedBytes / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Checks if an entry has expired based on its TTL.
    /// </summary>
    private bool IsExpired(CacheEntry entry)
        => _timeProvider.GetUtcNow() > entry.ExpiresAt;

    /// <summary>
    /// Gets the name of this cache instance (used as metric tier tag).
    /// </summary>
    public string Name => _name;

    /// <inheritdoc />
    string ICacheMetricsSource.Name => _name;

    /// <inheritdoc />
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSizeBytes);

    /// <inheritdoc />
    public long CapacityBytes => _capacityBytes;

    /// <inheritdoc />
    public double HitRate
    {
        get
        {
            long totalHits = Interlocked.Read(ref _hits);
            long totalMisses = Interlocked.Read(ref _misses);
            long total = totalHits + totalMisses;

            return total == 0 ? 0.0 : (double)totalHits / total;
        }
    }

    /// <inheritdoc />
    public int EntryCount => _cache.Count;

    /// <inheritdoc />
    public int BorrowedEntryCount => _cache.Values.Count(e => e.RefCount > 0);

    /// <summary>
    /// Gets the number of pending cleanup items.
    /// </summary>
    public int PendingCleanupCount => _pendingCleanup.Count;
}
