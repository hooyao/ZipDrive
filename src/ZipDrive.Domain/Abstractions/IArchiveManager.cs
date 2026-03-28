using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Manages archive lifecycle (add/remove/list) separately from file system operations.
/// Implemented by ZipVirtualFileSystem alongside IVirtualFileSystem (ISP).
/// </summary>
public interface IArchiveManager
{
    /// <summary>
    /// Registers an archive, making it accessible via VFS operations.
    /// Idempotent — calling twice with the same VirtualPath overwrites the descriptor.
    /// </summary>
    Task AddArchiveAsync(ArchiveDescriptor archive, CancellationToken ct = default);

    /// <summary>
    /// Drains in-flight operations for the archive, removes it from the trie,
    /// and cleans up all cached data (structure + file content).
    /// No-op if the archive is not registered.
    /// </summary>
    Task RemoveArchiveAsync(string archiveKey, CancellationToken ct = default);

    /// <summary>
    /// Returns all currently registered archive descriptors.
    /// </summary>
    IEnumerable<ArchiveDescriptor> GetRegisteredArchives();
}
