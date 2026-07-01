namespace ZipDrive.Domain.Models;

/// <summary>
/// Describes a discovered ZIP archive in the virtual file system.
/// Stored in the Archive Trie as the value for each registered archive.
/// </summary>
public sealed class ArchiveDescriptor
{
    /// <summary>
    /// Virtual path relative to mount root (e.g., "games/doom.zip").
    /// Uses forward slashes, no leading/trailing slash.
    /// </summary>
    public required string VirtualPath { get; init; }

    /// <summary>
    /// Absolute physical path to the ZIP file on disk.
    /// </summary>
    public required string PhysicalPath { get; init; }

    /// <summary>
    /// ZIP file size in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Last modified time of the ZIP file (UTC).
    /// </summary>
    public required DateTime LastModifiedUtc { get; init; }

    /// <summary>
    /// Archive format identifier (e.g., "zip", "rar").
    /// Set during discovery by IFormatRegistry.DetectFormat.
    /// </summary>
    public string FormatId { get; init; } = "zip";

    /// <summary>
    /// Display name derived from virtual path (e.g., "doom.zip").
    /// </summary>
    public string Name => VirtualPath.Contains('/')
        ? VirtualPath[(VirtualPath.LastIndexOf('/') + 1)..]
        : VirtualPath;
}
