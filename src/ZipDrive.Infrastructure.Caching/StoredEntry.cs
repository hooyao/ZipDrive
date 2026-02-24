namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Opaque wrapper for internally stored data.
/// Hides the actual storage type (byte[], MMF, etc.) from cache users.
/// </summary>
public sealed class StoredEntry
{
    /// <summary>
    /// Internal data (byte[], DiskCacheEntry, or T object).
    /// </summary>
    internal object Data { get; }

    /// <summary>
    /// Size in bytes for capacity tracking.
    /// </summary>
    internal long SizeBytes { get; }

    internal StoredEntry(object data, long sizeBytes)
    {
        Data = data;
        SizeBytes = sizeBytes;
    }
}
