using KTrie;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// KTrie-based prefix tree for mapping virtual paths to archives.
/// Supports platform-aware case sensitivity and virtual folder derivation.
/// Thread-safe: reads use shared lock, writes use exclusive lock.
/// </summary>
public sealed class ArchiveTrie : IArchiveTrie
{
    private readonly TrieDictionary<ArchiveDescriptor> _trie;
    private readonly HashSet<string> _virtualFolders;
    private readonly Dictionary<string, int> _virtualFolderRefCounts;
    private readonly StringComparer _folderComparer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

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
        _virtualFolderRefCounts = new Dictionary<string, int>(_folderComparer);
    }

    /// <inheritdoc />
    public void AddArchive(ArchiveDescriptor archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        _lock.EnterWriteLock();
        try
        {
            string key = archive.VirtualPath + "/";
            if (_trie.TryGetValue(key, out ArchiveDescriptor? existing))
                RemoveFolderReferences(existing.VirtualPath);

            _trie[key] = archive;
            AddFolderReferences(archive.VirtualPath);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public ArchiveTrieResult Resolve(string normalizedPath)
    {
        _lock.EnterReadLock();
        try
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
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public IEnumerable<VirtualFolderEntry> ListFolder(string folderPath)
    {
        _lock.EnterReadLock();
        try
        {
            string prefix = string.IsNullOrEmpty(folderPath) ? "" : folderPath + "/";
            HashSet<string> seen = new(_folderComparer);
            List<VirtualFolderEntry> results = new();

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
                        results.Add(new VirtualFolderEntry
                        {
                            Name = name,
                            IsArchive = true,
                            Archive = kvp.Value
                        });
                    }
                }
                else if (slashIndex >= 0)
                {
                    // Nested: extract first segment as subfolder
                    string folderName = remaining[..slashIndex];
                    if (seen.Add(folderName))
                    {
                        results.Add(new VirtualFolderEntry
                        {
                            Name = folderName,
                            IsArchive = false,
                            Archive = null
                        });
                    }
                }
            }

            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public bool IsVirtualFolder(string path)
    {
        _lock.EnterReadLock();
        try
        {
            string trimmed = path.TrimEnd('/');
            return !string.IsNullOrEmpty(trimmed) && _virtualFolders.Contains(trimmed);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public bool RemoveArchive(string virtualPath)
    {
        _lock.EnterWriteLock();
        try
        {
            string key = virtualPath + "/";
            bool removed = _trie.Remove(key);
            if (removed)
                RemoveFolderReferences(virtualPath);
            return removed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public IEnumerable<ArchiveDescriptor> Archives
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _trie.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <inheritdoc />
    public int ArchiveCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _trie.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    private void AddFolderReferences(string virtualPath)
    {
        foreach (string folder in EnumerateAncestorFolders(virtualPath))
        {
            _virtualFolderRefCounts.TryGetValue(folder, out int count);
            _virtualFolderRefCounts[folder] = count + 1;
            _virtualFolders.Add(folder);
        }
    }

    private void RemoveFolderReferences(string virtualPath)
    {
        foreach (string folder in EnumerateAncestorFolders(virtualPath))
        {
            if (!_virtualFolderRefCounts.TryGetValue(folder, out int count))
                continue;

            if (count <= 1)
            {
                _virtualFolderRefCounts.Remove(folder);
                _virtualFolders.Remove(folder);
            }
            else
            {
                _virtualFolderRefCounts[folder] = count - 1;
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestorFolders(string virtualPath)
    {
        string[] parts = virtualPath.Split('/');
        string current = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = i == 0 ? parts[i] : current + "/" + parts[i];
            yield return current;
        }
    }
}
