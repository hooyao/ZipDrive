namespace ZipDrive.Domain.Models;

/// <summary>
/// Volume information for the virtual file system.
/// </summary>
public readonly record struct VfsVolumeInfo
{
    /// <summary>Volume label displayed in file manager.</summary>
    public required string VolumeLabel { get; init; }

    /// <summary>File system name (e.g., "ZipDriveFS").</summary>
    public required string FileSystemName { get; init; }

    /// <summary>Total capacity in bytes.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Free space in bytes (always 0 for read-only).</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Whether the volume is read-only.</summary>
    public required bool IsReadOnly { get; init; }
}
