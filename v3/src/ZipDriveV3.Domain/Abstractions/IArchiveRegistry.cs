using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Abstractions;

/// <summary>
/// Multi-archive registry (tracks all mounted archives).
/// Deferred for future implementation - VFS uses IArchiveTrie directly.
/// </summary>
public interface IArchiveRegistry
{
    /// <summary>
    /// Registers an archive.
    /// </summary>
    Task<ArchiveDescriptor> RegisterAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters an archive.
    /// </summary>
    Task UnregisterAsync(string archiveKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all registered archives.
    /// </summary>
    IReadOnlyCollection<ArchiveDescriptor> List();

    /// <summary>
    /// Fired when archives are added/removed.
    /// </summary>
    event EventHandler<ArchivesChangedEventArgs>? Changed;
}
