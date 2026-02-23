using KTrie;

namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Cached structure of a single ZIP archive.
/// Uses a trie for efficient path lookup and prefix-based directory listing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Memory estimation:</strong> ~114 bytes per entry
/// (ZipEntryInfo ~48 bytes + filename string ~50 bytes + trie node overhead ~16 bytes).
/// </para>
/// </remarks>
public sealed class ArchiveStructure
{
    /// <summary>
    /// Unique key identifying this archive (virtual path, e.g., "games/doom.zip").
    /// </summary>
    public required string ArchiveKey { get; init; }

    /// <summary>
    /// Absolute filesystem path to the ZIP file.
    /// </summary>
    public required string AbsolutePath { get; init; }

    /// <summary>
    /// Trie of all entries, keyed by internal path.
    /// Directories have trailing /, files do not.
    /// Always uses Ordinal (case-sensitive) comparison per ZIP spec.
    /// </summary>
    public required TrieDictionary<ZipEntryInfo> Entries { get; init; }

    /// <summary>
    /// Total number of entries in the archive.
    /// </summary>
    public int EntryCount => Entries.Count;

    /// <summary>
    /// Timestamp when this structure was built.
    /// </summary>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>
    /// True if this is a ZIP64 archive.
    /// </summary>
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
    public long EstimatedMemoryBytes { get; init; }

    /// <summary>
    /// Archive comment (usually empty).
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Looks up a file or directory entry by exact path.
    /// </summary>
    /// <param name="internalPath">
    /// Path within the archive. Files have no trailing slash.
    /// Directories must include trailing / for exact match.
    /// </param>
    /// <returns>The entry info if found, or null if not found.</returns>
    public ZipEntryInfo? GetEntry(string internalPath)
    {
        return Entries.TryGetValue(internalPath, out ZipEntryInfo entry) ? entry : null;
    }

    /// <summary>
    /// Checks if a directory exists in the archive.
    /// Accepts paths with or without trailing slash.
    /// </summary>
    public bool DirectoryExists(string dirPath)
    {
        string key = dirPath.EndsWith('/') ? dirPath : dirPath + "/";
        return Entries.TryGetValue(key, out ZipEntryInfo entry) && entry.IsDirectory;
    }

    /// <summary>
    /// Lists direct children of a directory using trie prefix enumeration.
    /// </summary>
    /// <param name="dirPath">Directory path (empty string for root). Trailing slash optional.</param>
    /// <returns>Direct children as (Name, ZipEntryInfo) tuples.</returns>
    public IEnumerable<(string Name, ZipEntryInfo Entry)> ListDirectory(string dirPath)
    {
        // Normalize: ensure trailing / for non-root
        string prefix = string.IsNullOrEmpty(dirPath)
            ? ""
            : (dirPath.EndsWith('/') ? dirPath : dirPath + "/");

        // KTrie doesn't support empty prefix in EnumerateByPrefix, so enumerate all for root
        IEnumerable<KeyValuePair<string, ZipEntryInfo>> source =
            string.IsNullOrEmpty(prefix) ? Entries : Entries.EnumerateByPrefix(prefix);

        foreach (KeyValuePair<string, ZipEntryInfo> kvp in source)
        {
            // Skip the directory entry itself
            if (kvp.Key == prefix)
                continue;

            // Get remaining path after prefix
            ReadOnlySpan<char> remaining = kvp.Key.AsSpan(prefix.Length);

            // Check if direct child:
            // - File: no '/' in remaining
            // - Directory: exactly one '/' at the end
            int slashIndex = remaining.IndexOf('/');

            if (slashIndex == -1)
            {
                // File (no slash)
                yield return (remaining.ToString(), kvp.Value);
            }
            else if (slashIndex == remaining.Length - 1)
            {
                // Directory (trailing slash only) - return name without slash
                yield return (remaining[..slashIndex].ToString(), kvp.Value);
            }
            // else: nested entry, skip
        }
    }
}
