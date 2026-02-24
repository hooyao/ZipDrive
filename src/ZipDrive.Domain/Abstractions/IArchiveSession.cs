using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Represents an open archive instance (lifecycle management)
/// </summary>
public interface IArchiveSession : IAsyncDisposable
{
    /// <summary>Archive metadata</summary>
    ArchiveInfo Info { get; }

    /// <summary>
    /// Builds the entry tree (called lazily on first access)
    /// </summary>
    Task<IFileSystemTree> BuildTreeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file stream for reading with optional range support
    /// </summary>
    /// <param name="entryPath">Internal archive path (forward slashes)</param>
    /// <param name="range">Optional byte range (for partial reads)</param>
    Task<Stream> OpenReadAsync(string entryPath, Range? range = null,
                                CancellationToken cancellationToken = default);

    /// <summary>Provider-specific capabilities</summary>
    ArchiveCapabilities Capabilities { get; }
}
