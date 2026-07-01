using Microsoft.Extensions.Logging;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// DI-friendly wrapper around <see cref="GenericCache{T}"/> for <see cref="ArchiveStructure"/> caching.
/// Uses <see cref="ObjectStorageStrategy{T}"/> with a fixed 256 MB capacity.
/// </summary>
public sealed class ArchiveStructureStore : IArchiveStructureStore
{
    private const long CapacityMb = 256;

    private readonly GenericCache<ArchiveStructure> _inner;

    public ArchiveStructureStore(
        IEvictionPolicy evictionPolicy,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _inner = new GenericCache<ArchiveStructure>(
            new ObjectStorageStrategy<ArchiveStructure>(),
            evictionPolicy,
            CapacityMb * 1024 * 1024,
            timeProvider,
            loggerFactory.CreateLogger<GenericCache<ArchiveStructure>>(),
            name: "structure");
    }

    /// <inheritdoc />
    public Task<ICacheHandle<ArchiveStructure>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<ArchiveStructure>>> factory,
        CancellationToken cancellationToken = default)
        => _inner.BorrowAsync(cacheKey, ttl, factory, cancellationToken);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public void EvictExpired() => _inner.EvictExpired();

    /// <inheritdoc />
    public long CurrentSizeBytes => _inner.CurrentSizeBytes;

    /// <inheritdoc />
    public long CapacityBytes => _inner.CapacityBytes;

    /// <inheritdoc />
    public double HitRate => _inner.HitRate;

    /// <inheritdoc />
    public int EntryCount => _inner.EntryCount;

    /// <inheritdoc />
    public int BorrowedEntryCount => _inner.BorrowedEntryCount;

    /// <inheritdoc />
    public bool TryRemove(string cacheKey) => _inner.TryRemove(cacheKey);

    /// <inheritdoc />
    public bool ContainsKey(string cacheKey) => _inner.ContainsKey(cacheKey);
}
