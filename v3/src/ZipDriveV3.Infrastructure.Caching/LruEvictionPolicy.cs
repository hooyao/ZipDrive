namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Least Recently Used (LRU) eviction policy.
/// </summary>
/// <remarks>
/// Evicts entries with the oldest LastAccessedAt timestamp first.
/// This is the default eviction policy and works well for most use cases.
/// </remarks>
public class LruEvictionPolicy : IEvictionPolicy
{
    /// <inheritdoc />
    public IEnumerable<ICacheEntry> SelectVictims(
        IReadOnlyCollection<ICacheEntry> entries,
        long requiredBytes,
        long currentBytes,
        long capacityBytes)
    {
        long spaceNeeded = requiredBytes - (capacityBytes - currentBytes);
        long freedSpace = 0L;

        return entries
            .OrderBy(e => e.LastAccessedAt) // Oldest first
            .TakeWhile(e =>
            {
                if (freedSpace >= spaceNeeded)
                    return false;

                freedSpace += e.SizeBytes;
                return true;
            });
    }
}
