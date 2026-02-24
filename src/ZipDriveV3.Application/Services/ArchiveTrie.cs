using KTrie;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Application.Services;

/// <summary>
/// KTrie-based prefix tree for mapping virtual paths to archives.
/// Supports platform-aware case sensitivity and virtual folder derivation.
/// </summary>
public sealed class ArchiveTrie : IArchiveTrie
{
    private readonly TrieDictionary<ArchiveDescriptor> _trie;
    private readonly HashSet<string> _virtualFolders;
    private readonly StringComparer _folderComparer;

    /// <summary>
    /// Creates a new ArchiveTrie.
    /// </summary>
    /// <param name="charComparer">
    /// Character comparer for trie key matching.
    /// Use <see cref="CaseInsensitiveCharComparer.Instance"/> on Windows, null for case-sensitive.
    /// </param>
    public ArchiveTrie(IEqualityComparer<char>? charComparer = null)
    {
        _trie = charComparer != null
            ? new TrieDictionary<ArchiveDescriptor>(charComparer)
            : new TrieDictionary<ArchiveDescriptor>();

        bool caseInsensitive = charComparer != null;
        _folderComparer = caseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        _virtualFolders = new HashSet<string>(_folderComparer);
    }

    /// <inheritdoc />
    public void AddArchive(ArchiveDescriptor archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Key with trailing /
        string key = archive.VirtualPath + "/";
        _trie[key] = archive;

        // Register all ancestor virtual folders
        string[] parts = archive.VirtualPath.Split('/');
        string current = "";
        for (int i = 0; i < parts.Length - 1; i++) // Exclude the ZIP name itself
        {
            current = i == 0 ? parts[i] : current + "/" + parts[i];
            _virtualFolders.Add(current);
        }
    }

    /// <inheritdoc />
    public ArchiveTrieResult Resolve(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return ArchiveTrieResult.VirtualRoot();
        }

        // Try to find longest matching archive prefix
        // Append "/" to ensure we only match at segment boundaries
        string queryPath = normalizedPath.EndsWith('/')
            ? normalizedPath
            : normalizedPath + "/";

        KeyValuePair<string, ArchiveDescriptor>? match = _trie.LongestPrefixMatch(queryPath);

        if (match.HasValue)
        {
            ArchiveDescriptor archive = match.Value.Value;
            string matchedKey = match.Value.Key; // e.g., "games/doom.zip/"

            // Determine internal path (everything after the matched key)
            string internalPath = normalizedPath.Length > matchedKey.Length
                ? normalizedPath[matchedKey.Length..]
                : "";

            return string.IsNullOrEmpty(internalPath)
                ? ArchiveTrieResult.AtArchiveRoot(archive)
                : ArchiveTrieResult.Inside(archive, internalPath);
        }

        // Check if it's a virtual folder
        // Strip trailing slash for folder comparison
        string folderPath = normalizedPath.TrimEnd('/');
        if (_virtualFolders.Contains(folderPath))
        {
            return ArchiveTrieResult.Folder(folderPath);
        }

        return ArchiveTrieResult.NotFound();
    }

    /// <inheritdoc />
    public IEnumerable<VirtualFolderEntry> ListFolder(string folderPath)
    {
        string prefix = string.IsNullOrEmpty(folderPath) ? "" : folderPath + "/";
        HashSet<string> seen = new(_folderComparer);

        // KTrie doesn't support empty prefix in EnumerateByPrefix, so enumerate all for root
        IEnumerable<KeyValuePair<string, ArchiveDescriptor>> entries =
            string.IsNullOrEmpty(prefix) ? _trie : _trie.EnumerateByPrefix(prefix);

        foreach (KeyValuePair<string, ArchiveDescriptor> kvp in entries)
        {
            string remaining = kvp.Key[prefix.Length..];

            // Direct child archive: remaining is "name.zip/"
            int slashIndex = remaining.IndexOf('/');

            if (slashIndex == remaining.Length - 1)
            {
                // Direct archive child (single segment + trailing /)
                string name = remaining[..slashIndex];
                if (seen.Add(name))
                {
                    yield return new VirtualFolderEntry
                    {
                        Name = name,
                        IsArchive = true,
                        Archive = kvp.Value
                    };
                }
            }
            else if (slashIndex >= 0)
            {
                // Nested: extract first segment as subfolder
                string folderName = remaining[..slashIndex];
                if (seen.Add(folderName))
                {
                    yield return new VirtualFolderEntry
                    {
                        Name = folderName,
                        IsArchive = false,
                        Archive = null
                    };
                }
            }
        }
    }

    /// <inheritdoc />
    public bool IsVirtualFolder(string path)
    {
        string trimmed = path.TrimEnd('/');
        return !string.IsNullOrEmpty(trimmed) && _virtualFolders.Contains(trimmed);
    }

    /// <inheritdoc />
    public bool RemoveArchive(string virtualPath)
    {
        string key = virtualPath + "/";
        return _trie.Remove(key);
        // Note: Virtual folder cleanup with reference counting is deferred
    }

    /// <inheritdoc />
    public IEnumerable<ArchiveDescriptor> Archives => _trie.Values;

    /// <inheritdoc />
    public int ArchiveCount => _trie.Count;
}
