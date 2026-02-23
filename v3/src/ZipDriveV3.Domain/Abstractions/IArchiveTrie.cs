using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Abstractions;

/// <summary>
/// Trie for mapping virtual paths to archives.
/// Built at mount time, mutable for future file watcher support.
/// </summary>
public interface IArchiveTrie
{
    /// <summary>
    /// Resolves a virtual path to find the archive boundary.
    /// </summary>
    /// <param name="normalizedPath">Path with forward slashes, no leading/trailing slash.</param>
    /// <returns>Resolution result indicating path type and components.</returns>
    ArchiveTrieResult Resolve(string normalizedPath);

    /// <summary>
    /// Lists contents of a virtual folder (archives and subfolders).
    /// </summary>
    /// <param name="folderPath">Virtual folder path (empty string for root).</param>
    /// <returns>Direct children at this level.</returns>
    IEnumerable<VirtualFolderEntry> ListFolder(string folderPath);

    /// <summary>
    /// Checks if a path is a virtual folder (intermediate directory).
    /// </summary>
    bool IsVirtualFolder(string path);

    /// <summary>
    /// Adds an archive to the trie. Also registers ancestor virtual folders.
    /// </summary>
    void AddArchive(ArchiveDescriptor archive);

    /// <summary>
    /// Removes an archive from the trie.
    /// </summary>
    /// <returns>True if the archive was found and removed.</returns>
    bool RemoveArchive(string virtualPath);

    /// <summary>
    /// All registered archives.
    /// </summary>
    IEnumerable<ArchiveDescriptor> Archives { get; }

    /// <summary>
    /// Number of registered archives.
    /// </summary>
    int ArchiveCount { get; }
}
