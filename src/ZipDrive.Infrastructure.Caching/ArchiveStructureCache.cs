using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Caches parsed archive structures using GenericCache with ObjectStorageStrategy.
/// Delegates format-specific parsing to <see cref="IArchiveStructureBuilder"/> resolved
/// via <see cref="IFormatRegistry"/>. This class owns caching, eviction, and thundering
/// herd prevention; the builder just parses.
/// </summary>
public sealed class ArchiveStructureCache : IArchiveStructureCache
{
    private readonly IArchiveStructureStore _cache;
    private readonly IFormatRegistry _formatRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<ArchiveStructureCache> _logger;

    private long _missCount;

    public ArchiveStructureCache(
        IArchiveStructureStore cache,
        IFormatRegistry formatRegistry,
        TimeProvider timeProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<ArchiveStructureCache> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _formatRegistry = formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));
        _timeProvider = timeProvider;
        _defaultTtl = cacheOptions.Value.DefaultTtl;
        _logger = logger;

        _logger.LogInformation(
            "ArchiveStructureCache initialized with TTL={TtlMinutes} minutes",
            _defaultTtl.TotalMinutes);
    }

    /// <inheritdoc />
    public async Task<ArchiveStructure> GetOrBuildAsync(
        string archiveKey,
        string absolutePath,
        string formatId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);

        _logger.LogDebug("GetOrBuildAsync: {ArchiveKey} at {Path} format={FormatId}", archiveKey, absolutePath, formatId);

        ICacheHandle<ArchiveStructure> handle = await _cache.BorrowAsync(
            archiveKey,
            _defaultTtl,
            async ct =>
            {
                _logger.LogInformation("Building structure for {ArchiveKey} at {Path} format={FormatId}",
                    archiveKey, absolutePath, formatId);
                Interlocked.Increment(ref _missCount);

                IArchiveStructureBuilder builder = _formatRegistry.GetStructureBuilder(formatId);
                ArchiveStructure structure = await builder.BuildAsync(archiveKey, absolutePath, ct).ConfigureAwait(false);

                return new CacheFactoryResult<ArchiveStructure>
                {
                    Value = structure,
                    SizeBytes = structure.EstimatedMemoryBytes,
                    Metadata = new Dictionary<string, object>
                    {
                        ["EntryCount"] = structure.EntryCount,
                        ["FormatId"] = formatId,
                        ["BuildTimeMs"] = 0 // builder logs its own timing
                    }
                };
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            ArchiveStructure structure = handle.Value;

            _logger.LogDebug(
                "Returning structure for {ArchiveKey}: {EntryCount} entries",
                archiveKey,
                structure.EntryCount);

            return structure;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <inheritdoc />
    public bool Invalidate(string archiveKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

        bool removed = _cache.TryRemove(archiveKey);
        if (removed)
            _logger.LogInformation("Invalidated structure cache for {ArchiveKey}", archiveKey);
        else
            _logger.LogDebug("Invalidate: {ArchiveKey} not found in cache (already expired or never cached)", archiveKey);

        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        _logger.LogInformation("Cache cleared");
    }

    /// <inheritdoc />
    public void EvictExpired()
    {
        _cache.EvictExpired();
    }

    /// <inheritdoc />
    public int CachedArchiveCount => _cache.EntryCount;

    /// <inheritdoc />
    public long EstimatedMemoryBytes => _cache.CurrentSizeBytes;

    /// <inheritdoc />
    public double HitRate => _cache.HitRate;

    /// <inheritdoc />
    public long HitCount
    {
        get
        {
            double cacheHitRate = _cache.HitRate;
            long totalMisses = Interlocked.Read(ref _missCount);

            if (Math.Abs(cacheHitRate - 1.0) < 0.0001)
                return _cache.EntryCount > 0 ? long.MaxValue : 0;

            return (long)(cacheHitRate * totalMisses / (1 - cacheHitRate));
        }
    }

    /// <inheritdoc />
    public long MissCount => Interlocked.Read(ref _missCount);
}
