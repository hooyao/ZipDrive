namespace ZipDriveV3.Domain.Models;

/// <summary>
/// Entry in a virtual folder listing. Represents either a subfolder or a mounted archive.
/// </summary>
public readonly record struct VirtualFolderEntry
{
    /// <summary>
    /// Entry name (not full path). E.g., "doom.zip" or "games".
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// True if this entry is a ZIP archive mounted as a folder.
    /// False if this is an intermediate virtual folder.
    /// </summary>
    public required bool IsArchive { get; init; }

    /// <summary>
    /// Archive descriptor. Only set when <see cref="IsArchive"/> is true.
    /// </summary>
    public ArchiveDescriptor? Archive { get; init; }
}
