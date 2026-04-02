namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Optional interface for format providers that maintain internal metadata stores
/// (e.g., ZipFormatMetadataStore). Called by IFormatRegistry.OnArchiveRemoved
/// to clean up format-specific metadata when an archive is invalidated or removed.
/// </summary>
public interface IArchiveMetadataCleanup
{
    /// <summary>
    /// Removes all format-specific metadata for the given archive.
    /// Called during dynamic reload (archive removed) and structure cache invalidation.
    /// </summary>
    /// <param name="archiveKey">The archive virtual path (e.g., "games/doom.zip").</param>
    void CleanupArchive(string archiveKey);
}
