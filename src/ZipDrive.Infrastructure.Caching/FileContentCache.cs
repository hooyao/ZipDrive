using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// File content cache that owns ZIP extraction, tier routing, and caching.
/// Replaces <c>DualTierFileCache</c> by merging extraction ownership with cache coordination.
/// Routes small files to memory tier and large files to disk tier based on
/// <see cref="CacheOptions.SmallFileCutoffBytes"/>.
/// </summary>
public sealed class FileContentCache : IFileContentCache
{
    private readonly GenericCache<Stream> _memoryCache;
    private readonly GenericCache<Stream> _diskCache;
    private readonly ChunkedDiskStorageStrategy _diskStorageStrategy;
    private readonly IZipReaderFactory _zipReaderFactory;
    private readonly long _cutoffBytes;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<FileContentCache> _logger;

    // Per-archive key index: archiveKey → set of cache keys belonging to that archive.
    // Used by RemoveArchive to find which keys to TryRemove from the shared caches.
    // All access is serialized under _keyIndexLock — plain Dictionary is sufficient.
    private readonly Dictionary<string, HashSet<string>> _archiveKeyIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _keyIndexLock = new();

    public FileContentCache(
        IZipReaderFactory zipReaderFactory,
        IOptions<CacheOptions> options,
        IEvictionPolicy evictionPolicy,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(zipReaderFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evictionPolicy);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _zipReaderFactory = zipReaderFactory;
        _logger = loggerFactory.CreateLogger<FileContentCache>();

        CacheOptions opts = options.Value;
        _cutoffBytes = opts.SmallFileCutoffBytes;
        _defaultTtl = opts.DefaultTtl;

        _memoryCache = new GenericCache<Stream>(
            new MemoryStorageStrategy(),
            evictionPolicy,
            opts.MemoryCacheSizeBytes,
            timeProvider,
            loggerFactory.CreateLogger<GenericCache<Stream>>(),
            name: "memory");

        _diskStorageStrategy = new ChunkedDiskStorageStrategy(
            loggerFactory.CreateLogger<ChunkedDiskStorageStrategy>(),
            opts.ChunkSizeBytes,
            opts.TempDirectory);

        _diskCache = new GenericCache<Stream>(
            _diskStorageStrategy,
            evictionPolicy,
            opts.DiskCacheSizeBytes,
            timeProvider,
            loggerFactory.CreateLogger<GenericCache<Stream>>(),
            name: "disk");

        _logger.LogInformation(
            "FileContentCache initialized: memory={MemoryMb}MB, disk={DiskMb}MB, cutoff={CutoffMb}MB, chunkSize={ChunkMb}MB",
            opts.MemoryCacheSizeMb, opts.DiskCacheSizeMb, opts.SmallFileCutoffMb, opts.ChunkSizeMb);
    }

    /// <inheritdoc />
    public async Task<int> ReadAsync(
        string archivePath,
        ZipEntryInfo entry,
        string cacheKey,
        byte[] buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        // EOF check
        if (offset >= entry.UncompressedSize)
            return 0;

        // Route to correct tier based on entry size
        GenericCache<Stream> cache = entry.UncompressedSize < _cutoffBytes
            ? _memoryCache
            : _diskCache;

        // Build factory delegate — FileContentCache owns the extraction pipeline
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = async ct =>
        {
            IZipReader reader = _zipReaderFactory.Create(archivePath);
            try
            {
                Stream decompressedStream = await reader.OpenEntryStreamAsync(entry, ct).ConfigureAwait(false);

                return new CacheFactoryResult<Stream>
                {
                    Value = decompressedStream,
                    SizeBytes = entry.UncompressedSize,
                    OnDisposed = async () => await reader.DisposeAsync().ConfigureAwait(false)
                };
            }
            catch
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };

        // Borrow from cache (thundering herd prevention is inside GenericCache)
        using ICacheHandle<Stream> handle = await cache.BorrowAsync(
            cacheKey, _defaultTtl, factory, cancellationToken).ConfigureAwait(false);

        RegisterArchiveKey(cacheKey);

        // Seek and read from the cached random-access stream.
        // Thread-safety: each BorrowAsync call returns a fresh stream instance via
        // IStorageStrategy.Retrieve(), so concurrent callers never share a stream object.
        Stream stream = handle.Value;
        stream.Position = offset;

        int bytesToRead = (int)Math.Min(buffer.Length, entry.UncompressedSize - offset);
        int totalRead = 0;

        while (totalRead < bytesToRead)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, bytesToRead - totalRead),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
                break; // EOF

            totalRead += read;
        }

        return totalRead;
    }

    /// <inheritdoc />
    public bool ContainsKey(string cacheKey) =>
        _memoryCache.ContainsKey(cacheKey) || _diskCache.ContainsKey(cacheKey);

    /// <inheritdoc />
    public async Task WarmAsync(
        ZipEntryInfo entry,
        string cacheKey,
        Stream decompressedStream,
        CancellationToken cancellationToken = default)
    {
        GenericCache<Stream> cache = entry.UncompressedSize < _cutoffBytes
            ? _memoryCache
            : _diskCache;

        // Capture stream reference for the factory closure
        Stream capturedStream = decompressedStream;

        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = _ =>
            Task.FromResult(new CacheFactoryResult<Stream>
            {
                Value = capturedStream,
                SizeBytes = entry.UncompressedSize,
                OnDisposed = () => ValueTask.CompletedTask
            });

        // BorrowAsync stores the entry (thundering herd protection: no-op if already cached).
        // Immediate dispose releases RefCount to 0 — entry stays cached but eviction-eligible.
        using ICacheHandle<Stream> handle = await cache.BorrowAsync(
            cacheKey, _defaultTtl, factory, cancellationToken).ConfigureAwait(false);

        RegisterArchiveKey(cacheKey);
    }

    /// <inheritdoc />
    public void EvictExpired()
    {
        _memoryCache.EvictExpired();
        _diskCache.EvictExpired();
    }

    /// <inheritdoc />
    public void Clear()
    {
        _logger.LogInformation("Clearing both cache tiers...");
        _memoryCache.Clear();
        _diskCache.Clear();
        _logger.LogInformation("Both cache tiers cleared");
    }

    /// <inheritdoc />
    public int ProcessPendingCleanup(int maxItems = 100)
    {
        return _memoryCache.ProcessPendingCleanup(maxItems) + _diskCache.ProcessPendingCleanup(maxItems);
    }

    /// <inheritdoc />
    public void DeleteCacheDirectory()
    {
        _diskStorageStrategy.DeleteCacheDirectory();
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
    public int RemoveArchive(string archiveKey)
    {
        HashSet<string>? keys = null;
        using (_keyIndexLock.EnterScope())
        {
            if (_archiveKeyIndex.TryGetValue(archiveKey, out keys))
                _archiveKeyIndex.Remove(archiveKey);
        }

        // Iteration outside lock is safe: we own the HashSet after removing it from
        // the dictionary. Concurrent RegisterArchiveKey for the same archiveKey creates
        // a new HashSet, not this one.
        if (keys == null || keys.Count == 0)
            return 0;

        int removed = 0;
        foreach (string key in keys)
        {
            if (_memoryCache.TryRemove(key)) removed++;
            else if (_diskCache.TryRemove(key)) removed++;
        }

        _logger.LogInformation("RemoveArchive: {ArchiveKey} — removed {Count} cached file entries", archiveKey, removed);
        return removed;
    }

    private void RegisterArchiveKey(string cacheKey)
    {
        int colonIndex = cacheKey.IndexOf(':');
        if (colonIndex <= 0) return;
        string archiveKey = cacheKey[..colonIndex];

        using (_keyIndexLock.EnterScope())
        {
            if (!_archiveKeyIndex.TryGetValue(archiveKey, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _archiveKeyIndex[archiveKey] = set;
            }
            set.Add(cacheKey);
        }
    }

    /// <summary>
    /// Gets the memory tier cache (for testing/diagnostics).
    /// </summary>
    internal GenericCache<Stream> MemoryTier => _memoryCache;

    /// <summary>
    /// Gets the disk tier cache (for testing/diagnostics).
    /// </summary>
    internal GenericCache<Stream> DiskTier => _diskCache;
}
