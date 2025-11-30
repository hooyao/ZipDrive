using ZipDriveV3.Domain.Models;

namespace ZipDriveV3.Domain.Abstractions;

/// <summary>
/// Multi-archive registry (tracks all mounted archives)
/// </summary>
public interface IArchiveRegistry
{
    /// <summary>
    /// Registers an archive with a unique key
    /// </summary>
    Task<ArchiveDescriptor> RegisterAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters an archive
    /// </summary>
    Task UnregisterAsync(string archiveKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all registered archives
    /// </summary>
    IReadOnlyCollection<ArchiveDescriptor> List();

    /// <summary>
    /// Gets a specific archive session by key
    /// </summary>
    IArchiveSession? GetSession(string archiveKey);

    /// <summary>
    /// Fired when archives are added/removed
    /// </summary>
    event EventHandler<ArchivesChangedEventArgs>? Changed;
}
