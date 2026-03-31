using KTrie;

namespace ZipDrive.Domain.Models;

/// <summary>
/// Shared helper that synthesizes parent directory entries for files whose parent
/// directories are not explicitly listed in the archive. Used by format-specific
/// structure builders (ZIP, RAR, etc.) after populating the entry trie.
/// </summary>
public static class DirectorySynthesizer
{
    /// <summary>
    /// Walks every key in <paramref name="trie"/> and ensures that all ancestor
    /// directories exist as entries. Missing directories are inserted as
    /// <see cref="ArchiveEntryInfo"/> with <c>IsDirectory = true</c>.
    /// </summary>
    public static void SynthesizeParentDirectories(TrieDictionary<ArchiveEntryInfo> trie)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> keys = [.. trie.Keys];

        foreach (string key in keys)
        {
            if (key.EndsWith('/')) { seen.Add(key); continue; }
            int lastSlash = key.LastIndexOf('/');
            while (lastSlash > 0)
            {
                string dirPath = key[..(lastSlash + 1)];
                if (!seen.Add(dirPath)) break;
                if (!trie.ContainsKey(dirPath))
                {
                    trie[dirPath] = new ArchiveEntryInfo
                    {
                        UncompressedSize = 0,
                        IsDirectory = true,
                        LastModified = DateTime.UtcNow,
                        Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                    };
                }
                lastSlash = key.LastIndexOf('/', lastSlash - 1);
            }
        }
    }
}
