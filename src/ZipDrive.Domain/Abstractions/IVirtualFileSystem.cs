using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Platform-independent virtual file system abstraction.
/// Maps ZIP archives as folders with fully async operations.
/// </summary>
public interface IVirtualFileSystem
{
    /// <summary>
    /// Whether the VFS is currently mounted.
    /// </summary>
    bool IsMounted { get; }

    /// <summary>
    /// Discovers ZIP archives and prepares the VFS for operations.
    /// </summary>
    Task MountAsync(VfsMountOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to mount a single archive file.
    /// </summary>
    /// <param name="filePath">Path to the archive file on the host file system.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the file was successfully mounted; <c>false</c> if the file was not mounted,
    /// for example because the format is not recognized or the file could not be accessed.
    /// </returns>
    Task<bool> MountSingleFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears caches, releases resources, and marks the VFS as unmounted.
    /// </summary>
    Task UnmountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when mount state changes.
    /// </summary>
    event EventHandler<bool>? MountStateChanged;

    /// <summary>
    /// Gets file or directory metadata for a virtual path.
    /// </summary>
    Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists direct children of a directory.
    /// </summary>
    Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads file content at a specified offset into the provided buffer.
    /// </summary>
    /// <param name="path">Virtual path to the file.</param>
    /// <param name="buffer">Buffer to write data into.</param>
    /// <param name="offset">Byte offset in the file to start reading from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes actually read.</returns>
    Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file (not directory) exists at the given path.
    /// </summary>
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a directory exists at the given path.
    /// Returns true for virtual folders, archive roots, and directories inside archives.
    /// </summary>
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets volume information. Synchronous - no I/O required.
    /// </summary>
    VfsVolumeInfo GetVolumeInfo();
}
