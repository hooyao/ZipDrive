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
    Task PrefetchAsync(
        string archivePath,
        ArchiveStructure structure,
        string dirInternalPath,
        ArchiveEntryInfo? triggerEntry,
        IFileContentCache contentCache,
        PrefetchOptions options,
        CancellationToken cancellationToken = default);
}
