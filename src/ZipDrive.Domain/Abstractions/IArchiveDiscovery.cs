using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Discovers ZIP archives in a directory tree.
/// Used at mount time to populate the archive trie.
/// </summary>
public interface IArchiveDiscovery
{
    /// <summary>
    /// Discovers all ZIP files under the root directory.
    /// </summary>
    /// <param name="rootPath">Physical directory to scan.</param>
    /// <param name="maxDepth">Maximum directory depth to scan (clamped to 1-6).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of discovered archives with metadata.</returns>
    Task<IReadOnlyList<ArchiveDescriptor>> DiscoverAsync(
        string rootPath,
        int maxDepth = 6,
        CancellationToken cancellationToken = default);
}
