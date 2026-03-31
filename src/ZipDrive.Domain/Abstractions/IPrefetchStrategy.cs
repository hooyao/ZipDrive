using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Format-specific prefetch strategy. Different archive formats have fundamentally
/// different optimization strategies:
///
/// - ZIP: Contiguous byte-span read (entries are independently accessible at known offsets)
/// - RAR: No prefetch in V1 (SharpCompress doesn't expose entry offsets)
///
/// Implementations live in the format's infrastructure project.
/// IFormatRegistry.GetPrefetchStrategy returns null if the format has no prefetch optimization.
/// </summary>
public interface IPrefetchStrategy
{
    /// <summary>Format identifier.</summary>
    string FormatId { get; }

    /// <summary>
    /// Performs format-specific prefetch for sibling entries in a directory.
    /// Called fire-and-forget by VFS on cache miss or directory listing.
    /// </summary>
    /// <param name="archivePath">Physical path to the archive file.</param>
    /// <param name="structure">Parsed archive structure with entry metadata.</param>
    /// <param name="dirInternalPath">Directory internal path to prefetch siblings from.</param>
    /// <param name="triggerEntry">Entry that triggered the prefetch (null for directory listing triggers).</param>
    /// <param name="triggerInternalPath">Internal path of the trigger entry for exact matching (null for directory listing triggers).</param>
    /// <param name="contentCache">File content cache to warm entries into.</param>
    /// <param name="options">Prefetch configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PrefetchAsync(
        string archivePath,
        ArchiveStructure structure,
        string dirInternalPath,
        ArchiveEntryInfo? triggerEntry,
        string? triggerInternalPath,
        IFileContentCache contentCache,
        PrefetchOptions options,
        CancellationToken cancellationToken = default);
}
