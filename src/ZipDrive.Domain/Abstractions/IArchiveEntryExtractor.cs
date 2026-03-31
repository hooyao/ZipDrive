using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Format-specific extractor that produces a decompressed stream for a single archive entry.
/// Called by FileContentCache on cache miss.
///
/// Resource management: the returned ExtractionResult is a plain DTO (not IAsyncDisposable).
/// The caller (FileContentCache's storage strategy) chains resource cleanup via
/// CacheFactoryResult.OnDisposed using ExtractionResult.OnDisposed.
/// </summary>
public interface IArchiveEntryExtractor
{
    /// <summary>Format identifier (e.g., "zip", "rar").</summary>
    string FormatId { get; }

    /// <summary>
    /// Extracts and decompresses a single entry from the archive.
    /// </summary>
    /// <param name="archiveKey">Virtual path key for metadata lookup (e.g., "games/doom.zip").</param>
    /// <param name="archivePath">Absolute path to the archive file for I/O.</param>
    /// <param name="internalPath">Entry path within the archive (forward slashes, no leading /).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An ExtractionResult wrapping the decompressed stream and resource cleanup callback.
    /// </returns>
    Task<ExtractionResult> ExtractAsync(
        string archiveKey,
        string archivePath,
        string internalPath,
        CancellationToken cancellationToken = default);
}
