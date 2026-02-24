namespace ZipDrive.Domain.Models;

/// <summary>
/// Platform-independent file/directory metadata for the virtual file system.
/// </summary>
public readonly record struct VfsFileInfo
{
    /// <summary>File or directory name (not full path).</summary>
    public required string Name { get; init; }

    /// <summary>Full virtual path from mount root.</summary>
    public required string FullPath { get; init; }

    /// <summary>True if this entry is a directory.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>File size in bytes. 0 for directories.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public required DateTime CreationTimeUtc { get; init; }

    /// <summary>Last write time (UTC).</summary>
    public required DateTime LastWriteTimeUtc { get; init; }

    /// <summary>Last access time (UTC).</summary>
    public required DateTime LastAccessTimeUtc { get; init; }

    /// <summary>File attributes.</summary>
    public required FileAttributes Attributes { get; init; }
}
