namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Cached structure of a single ZIP archive.
/// Built by parsing the Central Directory once, then cached.
/// </summary>
/// <remarks>
/// <para>
/// This class represents the fully parsed metadata of a ZIP archive,
/// including a flat dictionary for O(1) file lookups and a hierarchical
/// tree for directory listings.
/// </para>
/// <para>
/// <strong>Memory estimation:</strong> ~114 bytes per entry
/// (ZipEntryInfo ~40 bytes + filename string ~50 bytes + dictionary overhead ~24 bytes).
/// </para>
/// <para>
/// Example memory usage:
/// <list type="bullet">
/// <item>100 files: ~11 KB</item>
/// <item>1,000 files: ~114 KB</item>
/// <item>10,000 files: ~1.1 MB</item>
/// <item>100,000 files: ~11 MB</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ArchiveStructure
{
    /// <summary>
    /// Unique key identifying this archive.
    /// </summary>
    /// <remarks>
    /// Typically the filename without path (e.g., "archive.zip").
    /// Used as the virtual directory name under the mount point.
    /// </remarks>
    public required string ArchiveKey { get; init; }

    /// <summary>
    /// Absolute filesystem path to the ZIP file.
    /// </summary>
    /// <remarks>
    /// Used to open FileStream for extraction operations.
    /// </remarks>
    public required string AbsolutePath { get; init; }

    /// <summary>
    /// Flat dictionary of all entries, keyed by internal path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Path format:
    /// <list type="bullet">
    /// <item>Forward slashes as separators</item>
    /// <item>No leading slash</item>
    /// <item>No trailing slash for files</item>
    /// <item>Example: "folder/subfolder/file.txt"</item>
    /// </list>
    /// </para>
    /// <para>
    /// Uses <see cref="StringComparer.OrdinalIgnoreCase"/> for Windows-style
    /// case-insensitive path matching.
    /// </para>
    /// </remarks>
    public required IReadOnlyDictionary<string, ZipEntryInfo> Entries { get; init; }

    /// <summary>
    /// Hierarchical tree structure for directory listing.
    /// </summary>
    /// <remarks>
    /// Used by DokanNet's FindFiles operation to enumerate directory contents.
    /// </remarks>
    public required DirectoryNode RootDirectory { get; init; }

    /// <summary>
    /// Total number of entries in the archive.
    /// </summary>
    public int EntryCount => Entries.Count;

    /// <summary>
    /// Timestamp when this structure was built.
    /// </summary>
    /// <remarks>
    /// Used for cache invalidation if ZIP file modification time is newer.
    /// </remarks>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>
    /// True if this is a ZIP64 archive.
    /// </summary>
    /// <remarks>
    /// ZIP64 archives may contain files larger than 4GB or more than 65535 entries.
    /// </remarks>
    public bool IsZip64 { get; init; }

    /// <summary>
    /// Total uncompressed size of all entries in bytes.
    /// </summary>
    public long TotalUncompressedSize { get; init; }

    /// <summary>
    /// Total compressed size of all entries in bytes.
    /// </summary>
    public long TotalCompressedSize { get; init; }

    /// <summary>
    /// Estimated memory usage of this structure in bytes.
    /// </summary>
    /// <remarks>
    /// Used by the cache for capacity tracking and eviction decisions.
    /// </remarks>
    public long EstimatedMemoryBytes { get; init; }

    /// <summary>
    /// Archive comment (usually empty).
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Looks up a file or directory entry by path.
    /// </summary>
    /// <param name="internalPath">Path within the archive (forward slashes, no leading slash).</param>
    /// <returns>The entry info if found, or null if not found.</returns>
    public ZipEntryInfo? GetEntry(string internalPath)
    {
        return Entries.TryGetValue(internalPath, out ZipEntryInfo entry) ? entry : null;
    }

    /// <summary>
    /// Gets the directory node for a given path.
    /// </summary>
    /// <param name="directoryPath">Path to the directory (empty string for root).</param>
    /// <returns>The directory node if found, or null if not found.</returns>
    public DirectoryNode? GetDirectory(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
            return RootDirectory;

        string[] parts = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        DirectoryNode current = RootDirectory;

        foreach (string part in parts)
        {
            if (!current.Subdirectories.TryGetValue(part, out DirectoryNode? subdir))
                return null;
            current = subdir;
        }

        return current;
    }
}
