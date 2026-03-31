using System.Collections.Concurrent;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Stores ZIP-specific entry metadata needed for extraction and prefetch
/// that is not part of the format-agnostic ArchiveEntryInfo.
///
/// Thread-safe: concurrent reads during extraction, writes during structure building.
/// Keyed by (archiveKey, internalPath). Lifetime tied to ArchiveStructureCache:
/// when an archive is invalidated, its entries are removed via IArchiveMetadataCleanup.
/// </summary>
public sealed class ZipFormatMetadataStore
{
    // archiveKey → (internalPath → ZipEntryInfo)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ZipEntryInfo>> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Populates all entries for an archive. Called by ZipStructureBuilder during BuildAsync.
    /// Replaces any existing entries for the same archiveKey (handles rebuild after invalidation).
    /// </summary>
    public void Populate(string archiveKey, IEnumerable<(string InternalPath, ZipEntryInfo Entry)> entries)
    {
        var dict = new ConcurrentDictionary<string, ZipEntryInfo>(StringComparer.Ordinal);
        foreach (var (path, entry) in entries)
            dict[path] = entry;
        _store[archiveKey] = dict;
    }

    /// <summary>
    /// Retrieves ZIP metadata for a single entry.
    /// </summary>
    /// <exception cref="FileNotFoundException">Entry not found in the metadata store.</exception>
    public ZipEntryInfo Get(string archiveKey, string internalPath)
    {
        if (_store.TryGetValue(archiveKey, out var dict) &&
            dict.TryGetValue(internalPath, out var entry))
        {
            return entry;
        }

        throw new FileNotFoundException(
            $"ZIP metadata not found: {archiveKey}:{internalPath}. " +
            "The structure may not have been built yet or the archive was invalidated.");
    }

    /// <summary>
    /// Retrieves all entries for an archive (for prefetch). Returns null if not populated.
    /// </summary>
    public IReadOnlyDictionary<string, ZipEntryInfo>? GetArchiveEntries(string archiveKey)
    {
        return _store.TryGetValue(archiveKey, out var dict) ? dict : null;
    }

    /// <summary>
    /// Removes all metadata for an archive (called on structure cache invalidation).
    /// </summary>
    public void Remove(string archiveKey)
    {
        _store.TryRemove(archiveKey, out _);
    }

    /// <summary>
    /// Returns the number of archives tracked. For diagnostics.
    /// </summary>
    public int ArchiveCount => _store.Count;
}
