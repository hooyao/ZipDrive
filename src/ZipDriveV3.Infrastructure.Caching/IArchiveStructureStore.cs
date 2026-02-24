using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Specialized cache for <see cref="ArchiveStructure"/> objects.
/// Wraps <see cref="GenericCache{T}"/> with <see cref="ObjectStorageStrategy{T}"/>
/// and exposes <see cref="Clear"/> without requiring a downcast.
/// </summary>
public interface IArchiveStructureStore : ICache<ArchiveStructure>
{
    /// <summary>
    /// Removes all cached entries immediately (entries with RefCount > 0 are skipped).
    /// </summary>
    void Clear();
}
